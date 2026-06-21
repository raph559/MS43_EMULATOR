namespace Ms43Sim.Core;

/// <summary>
/// R16's stuck rule for one output: while its predicate holds, the output must switch at least once in
/// its window — three engine cycles at the slowest shaft speed since the window began (a speed step
/// does not shorten a window that began slower), or a fixed time — counted from the later of the
/// predicate's start and the output's last switch. Times are CPU cycles at 24 MHz.
/// </summary>
public sealed class StuckRule
{
    ulong? holdingSince, lastSwitch;
    bool reported;
    double slowest = double.MaxValue;

    public void Switched(ulong now)
    {
        lastSwitch = now;
        reported = false;
        slowest = double.MaxValue;
    }

    /// <summary>The window's start when the output is newly stuck at <paramref name="now"/>; else null.</summary>
    public ulong? Tick(ulong now, bool predicate, double rpm, ulong? fixedWindow = null)
    {
        if (!predicate)
        {
            (holdingSince, reported, slowest) = (null, false, double.MaxValue);
            return null;
        }
        holdingSince ??= now;
        slowest = Math.Min(slowest, rpm);
        var from = Math.Max(holdingSince.Value, lastSwitch ?? 0);
        var window = fixedWindow ?? (slowest > 0 ? (ulong)(3 * OutputChecks.EngineCycleCycles(slowest)) : ulong.MaxValue);
        if (now - from <= window || reported)
            return null;
        reported = true;
        return from;
    }
}

/// <summary>
/// The dangerous-output checks (plan 4 R16, C3). They watch only edges of pins that are outputs before
/// and after (<see cref="Ports.OutputEdge"/>: a direction change is a set-up, never an edge); a pin
/// released to an input ends its pulse. The crank counts as stopped after 100 ms without a tooth.
/// <list type="bullet">
/// <item>dwell: a coil (P2.6–P2.11) high for more than 8.0 ms, or high more than 50 ms into a stop of the crank;</item>
/// <item>injector: a pulse (P2.0–P2.5 low) longer than 98 % of the engine cycle at the shaft's speed
/// (the slower of its two ends), an opening with no crank tooth in the 100 ms before, or an injector
/// open more than 50 ms into a stop of the crank;</item>
/// <item>throttle: the bridge drive |u| above 0.80 for more than 300 ms, or at 0.95 or more with the
/// plate within 0.05° of a stop (0° or 88°) for more than 20 ms, sampled every 0.5 ms;</item>
/// <item>before-rotation: an output edge of P2.0–P2.11 in a power cycle before its first crank tooth;</item>
/// <item>stuck: an output that does not switch while its predicate holds (R16), read every 1 ms —
/// needs the MS43X001 program's RAM.</item>
/// </list>
/// </summary>
public static class OutputChecks
{
    internal const ulong Ms = 24_000;
    internal const ulong StopAfter = 100 * Ms, IntoAStop = 50 * Ms;

    public static IEnumerable<CheckBase> All() =>
        new CheckBase[] { new Dwell(), new Injector(), new Throttle(), new BeforeRotation(), new Stuck() };

    internal static double EngineCycleCycles(double rpm) => 120 * 24e6 / rpm;

    static string Ms2(ulong cycles) => (cycles / (double)Ms).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One power cycle's crank: its first and its latest tooth, from the shaft once the engine is there.</summary>
    sealed class CrankClock
    {
        public CrankClock(Machine machine, DriveContext context) =>
            machine.Scheduler.Schedule(0, "crank clock", () =>
            {
                if (context.Shaft is { } shaft)
                    shaft.ToothPassed += () =>
                    {
                        First ??= machine.Cycles;
                        Last = machine.Cycles;
                    };
            });

        public ulong? First { get; private set; }

        public ulong? Last { get; private set; }

        public bool Stopped(ulong now) => Last is not { } last || now - last > StopAfter;

        /// <summary>When the current stop began: 100 ms after the latest tooth (after power-on with none yet).</summary>
        public ulong StopStart => (Last ?? 0) + StopAfter;
    }

    static void Every(Machine machine, ulong period, Action tick)
    {
        void At(ulong at) => machine.Scheduler.Schedule(at, "output check", () =>
        {
            tick();
            At(at + period);
        });
        At(period);
    }

    sealed class Dwell() : CheckBase("dwell")
    {
        const ulong Limit = 192_000;                      // 8.0 ms

        protected override void Start(Machine machine, int index)
        {
            var crank = new CrankClock(machine, Context);
            var rise = new ulong?[6];
            var longSeen = new bool[6];
            var stopSeen = new bool[6];
            machine.Ports.OutputEdge += (port, bit, level) =>
            {
                if (port != 2 || bit is < 6 or > 11)
                    return;
                var coil = bit - 6;
                var now = machine.Cycles;
                if (level)
                {
                    (rise[coil], longSeen[coil], stopSeen[coil]) = (now, false, false);
                    Exercise();
                    return;
                }
                if (rise[coil] is { } start && now - start > Limit && !longSeen[coil])
                    Find($"long P2.{bit}", $"coil P2.{bit} (cylinder {coil + 1}) dwelt {Ms2(now - start)} ms (the limit is 8.0 ms)",
                        Context.PowerCycleStart + start, Context.PowerCycleStart + now);
                rise[coil] = null;
            };
            machine.Ports.PinDirectionChanged += (port, bit, isOutput) =>
            {
                if (port == 2 && bit is >= 6 and <= 11 && !isOutput)
                    rise[bit - 6] = null;
            };
            Every(machine, Ms, () =>
            {
                var now = machine.Cycles;
                for (var coil = 0; coil < 6; coil++)
                {
                    if (rise[coil] is not { } start)
                        continue;
                    if (now - start > Limit && !longSeen[coil])
                    {
                        longSeen[coil] = true;
                        Find($"long P2.{coil + 6}", $"coil P2.{coil + 6} (cylinder {coil + 1}) has dwelt more than 8.0 ms",
                            Context.PowerCycleStart + start, Context.PowerCycleStart + now);
                    }
                    if (crank.Stopped(now) && now > Math.Max(crank.StopStart, start) + IntoAStop && !stopSeen[coil])
                    {
                        stopSeen[coil] = true;
                        Find($"stop P2.{coil + 6}", $"coil P2.{coil + 6} (cylinder {coil + 1}) was high more than 50 ms into a stop of the crank");
                    }
                }
            });
        }
    }

    sealed class Injector() : CheckBase("injector")
    {
        protected override void Start(Machine machine, int index)
        {
            var crank = new CrankClock(machine, Context);
            var open = new (ulong Cycle, double Rpm)?[6];
            var dutySeen = new bool[6];
            var stopSeen = new bool[6];
            double Rpm() => Context.Shaft?.Rpm ?? 0;
            machine.Ports.OutputEdge += (port, bit, level) =>
            {
                if (port != 2 || bit > 5)
                    return;
                var now = machine.Cycles;
                if (!level)
                {
                    if (crank.Last is not { } tooth || now - tooth > StopAfter)
                        Find($"no tooth P2.{bit}", $"injector P2.{bit} (cylinder {bit + 1}) opened with no crank tooth in the 100 ms before");
                    (open[bit], dutySeen[bit], stopSeen[bit]) = ((now, Rpm()), false, false);
                    Exercise();
                    return;
                }
                if (open[bit] is { } o && !dutySeen[bit])
                    Duty(bit, o.Cycle, now, Math.Min(o.Rpm, Rpm()));
                open[bit] = null;
            };
            machine.Ports.PinDirectionChanged += (port, bit, isOutput) =>
            {
                if (port == 2 && bit <= 5 && !isOutput)
                    open[bit] = null;
            };
            Every(machine, Ms, () =>
            {
                var now = machine.Cycles;
                for (var n = 0; n < 6; n++)
                {
                    if (open[n] is not { } o)
                        continue;
                    if (!dutySeen[n])
                        dutySeen[n] = Duty(n, o.Cycle, now, Math.Min(o.Rpm, Rpm()));
                    if (crank.Stopped(now) && now > Math.Max(crank.StopStart, o.Cycle) + IntoAStop && !stopSeen[n])
                    {
                        stopSeen[n] = true;
                        Find($"stop P2.{n}", $"injector P2.{n} (cylinder {n + 1}) was open more than 50 ms into a stop of the crank");
                    }
                }
            });
        }

        /// <summary>A finding when the pulse from <paramref name="opened"/> to <paramref name="now"/> passes 98 % of the engine cycle.</summary>
        bool Duty(int n, ulong opened, ulong now, double rpm)
        {
            if (rpm <= 0 || now - opened <= 0.98 * EngineCycleCycles(rpm))
                return false;
            Find($"duty P2.{n}", $"injector P2.{n} (cylinder {n + 1}) open {Ms2(now - opened)} ms, {100 * (now - opened) / EngineCycleCycles(rpm):0} % of the engine cycle at {rpm:0} rpm (the limit is 98 %)",
                Context.PowerCycleStart + opened, Context.PowerCycleStart + now);
            return true;
        }
    }

    sealed class Throttle() : CheckBase("throttle")
    {
        const ulong Step = 12_000;                        // 0.5 ms

        protected override void Start(Machine machine, int index)
        {
            ulong? saturated = null, pinned = null;
            bool saturatedSeen = false, pinnedSeen = false;
            Every(machine, Step, () =>
            {
                var now = machine.Cycles;
                var u = Math.Abs(ThrottleBody.Drive(machine.Ports, machine.Pwm));
                var angle = machine.Throttle.Angle;
                if (u > 0)
                    Exercise();
                if (u > 0.80)
                {
                    saturated ??= now;
                    if (now - saturated > 300 * Ms && !saturatedSeen)
                    {
                        saturatedSeen = true;
                        Find("saturated", "the throttle bridge was driven above 80 % for more than 300 ms",
                            Context.PowerCycleStart + saturated.Value, Context.PowerCycleStart + now);
                    }
                }
                else
                    (saturated, saturatedSeen) = (null, false);
                if (u >= 0.95 && (angle <= 0.05 || angle >= 87.95))
                {
                    pinned ??= now;
                    if (now - pinned > 20 * Ms && !pinnedSeen)
                    {
                        pinnedSeen = true;
                        Find("pinned", $"the throttle bridge drove the plate into its {(angle <= 0.05 ? "closed" : "open")} stop at 95 % or more for more than 20 ms",
                            Context.PowerCycleStart + pinned.Value, Context.PowerCycleStart + now);
                    }
                }
                else
                    (pinned, pinnedSeen) = (null, false);
            });
        }
    }

    sealed class BeforeRotation() : CheckBase("before-rotation")
    {
        protected override void Start(Machine machine, int index)
        {
            var crank = new CrankClock(machine, Context);
            machine.Scheduler.Schedule(0, "before-rotation", () =>
            {
                if (Context.Shaft is { } shaft)
                    shaft.ToothPassed += () => Exercise();
            });
            machine.Ports.OutputEdge += (port, bit, level) =>
            {
                if (port == 2 && bit <= 11 && crank.First is null)
                    Find($"P2.{bit}", $"an output edge on P2.{bit} ({(bit < 6 ? $"injector, cylinder {bit + 1}" : $"coil, cylinder {bit - 5}")}) in power cycle {index + 1} before its first crank tooth");
            };
        }
    }

    sealed class Stuck() : CheckBase("stuck")
    {
        /// <summary>Pin P2.n (cylinder n + 1) → its index in the firing order 1-5-3-6-2-4.</summary>
        static readonly int[] FiringIndex = { 0, 4, 2, 5, 1, 3 };

        const int Valve = 12, Purge = 13;

        protected override void Start(Machine machine, int index)
        {
            if (!ProgramIdentity.IsMs43X001(machine.Image.Image))
            {
                NotEvaluated("the program is not MS43X001 (R15): the predicates read its RAM");
                return;
            }
            var rules = Enumerable.Range(0, 14).Select(_ => new StuckRule()).ToArray();   // P2.0–P2.11 by bit, then the idle valve and the purge valve
            var coilRise = new ulong?[6];
            void Switched(int output) => rules[output].Switched(machine.Cycles);
            machine.Ports.OutputEdge += (port, bit, level) =>
            {
                if (port == 8 && bit == 7)
                    Switched(Purge);
                if (port != 2)
                    return;
                if (bit <= 5 || bit == Valve)
                    Switched(bit);
                else if (bit <= 11)
                {
                    if (level)
                        coilRise[bit - 6] = machine.Cycles;
                    else if (coilRise[bit - 6] is { } rise && machine.Cycles - rise >= Engine.MinSparkDwellCycles)
                        Switched(bit);
                }
            };
            Every(machine, Ms, () =>
            {
                var now = machine.Cycles;
                var bus = machine.Bus;
                var rpm = Context.Shaft?.Rpm ?? 0;
                var running = (bus.Peek16(0xF900) & 0xFF) == 2 && rpm >= 300;
                var fb9a = bus.Peek16(0xFB9A) & 0xFF;
                var fuel = running && (RunState.XramWord(machine, 0xE47E) & 0x1FFF) == 0 && RunState.Xram(machine, 0xE7E8) == 0;
                var spark = running && (bus.Peek16(0xFD72) & 0xC000) == 0 && (bus.Peek16(0xFD74) & 0x000F) == 0
                    && RunState.XramWord(machine, 0xE4B8) != 0 && (bus.Peek16(0xFD50) & 1) == 0;
                var key = CarControls.KeyIsOn(machine.Board);
                for (var n = 0; n < 6; n++)
                {
                    Hold(n, fuel && (fb9a >> FiringIndex[n] & 1) == 0, null, $"injector P2.{n} (cylinder {n + 1}) did not switch for three engine cycles while it should fire");
                    Hold(n + 6, spark, null, $"coil P2.{n + 6} (cylinder {n + 1}) did not spark for three engine cycles while it should");
                }
                Hold(Valve, key && now >= 100 * Ms, 25 * Ms, "the idle valve P2.12 did not switch for 25 ms with the key on");
                Hold(Purge, key && now >= 500 * Ms, 250 * Ms, "the purge valve P8.7 did not switch for 250 ms with the key on");

                void Hold(int output, bool predicate, ulong? window, string message)
                {
                    if (predicate)
                        Exercise();
                    if (rules[output].Tick(now, predicate, rpm, window) is { } from)
                        Find($"stuck {output}", message, Context.PowerCycleStart + from, Context.PowerCycleStart + now);
                }
            });
        }
    }
}
