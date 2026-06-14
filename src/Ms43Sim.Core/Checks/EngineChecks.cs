namespace Ms43Sim.Core;

/// <summary>
/// R16's rev-limit rule over a stream of shaft speeds and cylinder events: once the shaft has been
/// above L + 50 rpm for more than 50 ms, a cylinder with both a spark and an injector opening within
/// one engine cycle (both while over) is a finding. Times are CPU cycles at 24 MHz.
/// </summary>
public sealed class RevLimitRule
{
    const double Margin = 50;
    const ulong Hold = 1_200_000;                          // 50 ms

    readonly ulong?[] spark = new ulong?[7], fuel = new ulong?[7];
    ulong? aboveSince;
    double rpm, limit;

    /// <summary>The shaft's speed and the limit L at <paramref name="now"/>.</summary>
    public void Speed(ulong now, double rpm, double limit)
    {
        (this.rpm, this.limit) = (rpm, limit);
        if (rpm > limit + Margin)
            aboveSince ??= now;
        else
        {
            aboveSince = null;
            Array.Clear(spark);
            Array.Clear(fuel);
        }
    }

    /// <summary>Cylinder <paramref name="cylinder"/> sparked; the finding's figures when it was fuelled within the cycle.</summary>
    public (int Cylinder, double Rpm, double Limit)? Spark(ulong now, int cylinder) => Event(now, cylinder, spark, fuel);

    /// <summary>Cylinder <paramref name="cylinder"/>'s injector opened; the finding's figures when it sparked within the cycle.</summary>
    public (int Cylinder, double Rpm, double Limit)? Opening(ulong now, int cylinder) => Event(now, cylinder, fuel, spark);

    (int, double, double)? Event(ulong now, int cylinder, ulong?[] mine, ulong?[] other)
    {
        if (aboveSince is not { } since || now - since <= Hold)
            return null;
        mine[cylinder] = now;
        return other[cylinder] is { } then && now - then <= OutputChecks.EngineCycleCycles(rpm) ? (cylinder, rpm, limit) : null;
    }
}

/// <summary>
/// The owner's power rule (plan 4 R16) over a stream of cylinder events: while power is asked, each
/// cylinder needs a spark and an injector opening within the last three engine cycles, once the demand
/// has lasted three engine cycles. Times are CPU cycles at 24 MHz.
/// </summary>
public sealed class PowerRule
{
    readonly ulong?[] spark = new ulong?[7], fuel = new ulong?[7];
    ulong? askedSince;

    public void Spark(ulong now, int cylinder) => spark[cylinder] = now;

    public void Opening(ulong now, int cylinder) => fuel[cylinder] = now;

    /// <summary>The cylinders silent for three engine cycles at <paramref name="rpm"/> while power is asked.</summary>
    public IEnumerable<int> Tick(ulong now, bool demanded, double rpm)
    {
        if (!demanded || rpm <= 0)
        {
            askedSince = null;
            return Array.Empty<int>();
        }
        askedSince ??= now;
        var window = 3 * OutputChecks.EngineCycleCycles(rpm);
        if (now - askedSince.Value < window)
            return Array.Empty<int>();
        return Enumerable.Range(1, 6).Where(c => Silent(spark[c]) || Silent(fuel[c])).ToArray();

        bool Silent(ulong? last) => last is not { } t || now - t > window;
    }
}

/// <summary>
/// The engine-behaviour checks (plan 4 R16). A spark is a coil's fall after at least 0.5 ms of dwell; an
/// opening an injector's fall; cylinder c has its injector on P2.(c − 1) and its coil on P2.(c + 5).
/// <list type="bullet">
/// <item>spark-window: a spark outside 30° ATDC…75° BTDC of its cylinder's firing TDC while the shaft
/// turns. R16's second rule (an injector opening twice within 0.9 engine cycles while E7E8 = 0) is
/// left out: the car's own firmware does that in its start, its full-load pull, at its limiter and
/// at the coast's hand-off to idle (6–19 times per cylinder in a drive) [obs plan 4 Task 9];</item>
/// <item>rev-limit: <see cref="RevLimitRule"/> with L = 83EA × 32 when c_conf_n_max (0x7131E) ≠ 0, else 83F0 × 32;</item>
/// <item>engine: a crank segment that ends without the engine running on its own (50 ms after it ends);
/// an engine that stops during an idle segment with the key on, after running in it;</item>
/// <item>power: <see cref="PowerRule"/> while the drive asks for power (<see cref="DriveDemand.PowerAsked"/>)
/// and the shaft turns from 900 rpm to L − 300 rpm.</item>
/// </list>
/// The rules that read the firmware's RAM are not evaluated off the MS43X001 program.
/// </summary>
public static class EngineChecks
{
    const ulong Ms = 24_000;

    public static IEnumerable<CheckBase> All() =>
        new CheckBase[] { new SparkWindow(), new RevLimit(), new EngineRuns(), new Power() };

    /// <summary>A spark of <paramref name="cylinder"/> at <paramref name="crankAngle"/> lies within 30° ATDC…75° BTDC of its firing TDC.</summary>
    public static bool InSparkWindow(int cylinder, double crankAngle) =>
        CylinderEvents.BeforeTdc(EngineShaft.FiringTdc(cylinder) - crankAngle) is >= -30 and <= 75;

    /// <summary>The rev limit L (R16), rpm.</summary>
    internal static double Limit(Machine m) =>
        32.0 * (m.Image.Image.Bytes[0x7131E] != 0 ? m.Bus.ExternalRam[0x83EA - 0x8000] : m.Bus.ExternalRam[0x83F0 - 0x8000]);

    static void Every(Machine machine, ulong period, Action tick)
    {
        void At(ulong at) => machine.Scheduler.Schedule(at, "engine check", () =>
        {
            tick();
            At(at + period);
        });
        At(period);
    }

    /// <summary>A machine's sparks and injector openings, by cylinder 1–6.</summary>
    sealed class Firing
    {
        public Firing(Machine machine)
        {
            var rise = new ulong?[6];
            machine.Ports.OutputEdge += (port, bit, level) =>
            {
                if (port != 2 || bit > 11)
                    return;
                if (bit < 6)
                {
                    if (!level)
                        Opening?.Invoke(bit + 1);
                    return;
                }
                var coil = bit - 6;
                if (level)
                    rise[coil] = machine.Cycles;
                else
                {
                    if (rise[coil] is { } start && machine.Cycles - start >= Engine.MinSparkDwellCycles)
                        Spark?.Invoke(coil + 1);
                    rise[coil] = null;
                }
            };
        }

        public event Action<int>? Spark;

        public event Action<int>? Opening;
    }

    sealed class SparkWindow() : CheckBase("spark-window")
    {
        protected override void Start(Machine machine, int index)
        {
            var firing = new Firing(machine);
            firing.Spark += c =>
            {
                if (Context.Shaft is not { } shaft || shaft.Rpm < EngineSensors.TurningRpm)
                    return;
                var angle = shaft.Angle;
                Exercise();
                var btdc = CylinderEvents.BeforeTdc(EngineShaft.FiringTdc(c) - angle);
                if (!InSparkWindow(c, angle))
                    Find($"spark {c}", $"cylinder {c} sparked at {(btdc >= 0 ? $"{btdc:0.0}° BTDC" : $"{-btdc:0.0}° ATDC")}, outside 30° ATDC…75° BTDC of its firing TDC");
            };
        }
    }

    sealed class RevLimit() : CheckBase("rev-limit")
    {
        protected override void Start(Machine machine, int index)
        {
            if (!ProgramIdentity.IsMs43X001(machine.Image.Image))
            {
                NotEvaluated("the program is not MS43X001 (R15): the limit is in its RAM");
                return;
            }
            var rule = new RevLimitRule();
            var firing = new Firing(machine);
            Every(machine, Ms, () =>
            {
                var limit = Limit(machine);
                var rpm = Context.Shaft?.Rpm ?? 0;
                rule.Speed(machine.Cycles, rpm, limit);
                if (rpm > limit + 50)
                    Exercise();
            });
            firing.Spark += c => Report(rule.Spark(machine.Cycles, c));
            firing.Opening += c => Report(rule.Opening(machine.Cycles, c));

            void Report((int Cylinder, double Rpm, double Limit)? found)
            {
                if (found is { } f)
                    Find($"over {f.Cylinder}", $"cylinder {f.Cylinder} was fuelled and sparked in one engine cycle at {f.Rpm:0} rpm, above the limit {f.Limit:0} rpm + 50");
            }
        }
    }

    sealed class EngineRuns() : CheckBase("engine")
    {
        DriveMode? mode;
        int line;
        bool ranInIdle, stallSeen;

        protected override void Attached() => Context.SegmentEntered += point =>
        {
            var machine = Context.Machine!;
            if (mode == DriveMode.Crank)
                CrankEnded(machine, line);
            if (point.Mode is DriveMode.Crank or DriveMode.Idle)
                Exercise();
            (mode, line, ranInIdle, stallSeen) = (point.Mode, point.Segment.Line, false, false);
        };

        protected override void Start(Machine machine, int index)
        {
            mode = null;
            Every(machine, 10 * Ms, () =>
            {
                if (mode != DriveMode.Idle || Context.Engine is not { } engine)
                    return;
                if (engine.Phase == EnginePhase.Running)
                    ranInIdle = true;
                else if (ranInIdle && !stallSeen && CarControls.KeyIsOn(machine.Board))
                {
                    stallSeen = true;
                    Find($"stall {line}", $"the engine stopped during the idle at line {line} with the key on");
                }
            });
        }

        protected override void End(Machine machine, int index)
        {
            if (mode == DriveMode.Crank && Context.Engine?.Phase != EnginePhase.Running)
                Find($"crank {line}", $"the crank at line {line} ended with the drive, the engine not running on its own");
        }

        void CrankEnded(Machine machine, int crankLine) =>
            machine.Scheduler.Schedule(machine.Cycles + 50 * Ms, "engine check", () =>
            {
                if (Context.Engine?.Phase != EnginePhase.Running)
                    Find($"crank {crankLine}", $"the crank at line {crankLine} ended without the engine running on its own");
            });
    }

    sealed class Power() : CheckBase("power")
    {
        protected override void Start(Machine machine, int index)
        {
            if (!ProgramIdentity.IsMs43X001(machine.Image.Image))
            {
                NotEvaluated("the program is not MS43X001 (R15): the speed window reads its rev limit");
                return;
            }
            var rule = new PowerRule();
            var firing = new Firing(machine);
            var silent = new HashSet<int>();
            firing.Spark += c => rule.Spark(machine.Cycles, c);
            firing.Opening += c => rule.Opening(machine.Cycles, c);
            Every(machine, Ms, () =>
            {
                var rpm = Context.Shaft?.Rpm ?? 0;
                var asked = Context.Demand is { PowerAsked: true } && rpm >= 900 && rpm <= Limit(machine) - 300;
                if (asked)
                    Exercise();
                var now = rule.Tick(machine.Cycles, asked, rpm).ToHashSet();
                foreach (var c in now)
                {
                    if (silent.Add(c))
                        Find($"silent {c}", $"cylinder {c} went three engine cycles without a spark and an injector opening while the drive asked for power (at {rpm:0} rpm)");
                }
                silent.IntersectWith(now);
            });
        }
    }
}
