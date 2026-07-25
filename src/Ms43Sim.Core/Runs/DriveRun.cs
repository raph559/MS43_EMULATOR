using System.Security.Cryptography;
using System.Text;

namespace Ms43Sim.Core;

/// <summary>
/// How a drive runs. Eeprom: the image of the first key-on (null: blank). Each observer is called once
/// with the run's <see cref="DriveContext"/> before the run and attaches itself to each power cycle
/// through <see cref="DriveContext.PowerCycleStarted"/>. CaptureCapacity: the output capture's size.
/// </summary>
public sealed record DriveRunOptions(MachineOptions? Machine = null, byte[]? Eeprom = null,
    Action<DriveContext>[]? Observers = null, int CaptureCapacity = OutputCapture.Capacity);

/// <summary>
/// Why a power cycle's run stopped (final review I3): the drive's time ran out; the main relay dropped
/// after the drive turned the key off (the after-run's end); or it dropped while the drive had the key
/// on, with the firmware reading the key on (the ECU switched itself off) or off (AN1 forced); or it
/// dropped with the key on and the firmware reset itself (SRST) and booted again, which starts the next
/// power cycle (plan 5 Task 6).
/// </summary>
public enum CycleEnd { DriveEnded, AfterRun, RelayDroppedKeyOn, RelayDroppedKeyReadOff, Restarted }

/// <summary>The drive turned the key off or on, in the drive's time.</summary>
public sealed record KeySwitch(ulong Cycle, bool On);

/// <summary>
/// One power cycle of a drive: StartCycle and EndCycle in the drive's time; the milestones, the
/// engine's phases and the scheduler figures in the cycle's own time (from its power-on); the fault
/// memory, the RAM read-outs and the EEPROM at its end; the conditions its faults' explanations rest on;
/// why it ended.
/// </summary>
public sealed record PowerCycle(int Index, ulong StartCycle, StartupMilestones Milestones, EngineSummary? Engine,
    FaultMemoryState Faults, IReadOnlyList<FaultEntry> SavedFaults, FirmwareState Firmware, EepromSummary Eeprom,
    SchedulerHealth Scheduler, ulong EndCycle, FaultContext FaultContext, CycleEnd End)
{
    /// <summary>Every time the drive turned the key off or on in this power cycle, in the drive's time (plan 5 Task 6).</summary>
    public IReadOnlyList<KeySwitch> KeySwitches { get; init; } = Array.Empty<KeySwitch>();
}

/// <summary>
/// A whole drive: its power cycles, and every power cycle's outputs, cylinder events, trace and machine
/// events together, in the drive's time. Eeprom is the last cycle's chip, its source the drive's first
/// image. Checks is empty, Coverage null and Verdict incomplete from <see cref="DriveRun.Run"/>;
/// <see cref="DriveRun.RunWithChecks"/> fills them.
/// </summary>
public sealed record DriveResult(FlashedImage Image, Drive Drive, IReadOnlyList<PowerCycle> PowerCycles,
    IReadOnlyList<OutputEvent> OutputEvents, long OutputEventsDropped, IReadOnlyList<Injection> Injections,
    IReadOnlyList<Spark> Sparks, IReadOnlyList<TraceRow> Trace, EepromSummary Eeprom, int WatchdogResets,
    int SoftwareResets, IReadOnlyList<SimEvent> Events, IReadOnlyDictionary<Type, long> EventCounts,
    IReadOnlyList<AccessRecord> Inventory, IReadOnlyList<StubInfo> Stubs, IReadOnlyList<ICheck> Checks,
    string StateHash, IReadOnlyList<string> Assumptions, ulong Cycles, ulong Instructions,
    Coverage? Coverage = null, RunVerdict Verdict = RunVerdict.Incomplete)
{
    /// <summary>The check called <paramref name="name"/>.</summary>
    public ICheck Check(string name) =>
        Checks.FirstOrDefault(c => c.Name == name) ?? throw new KeyNotFoundException($"no check '{name}' in this run");

    /// <summary>The K-line tester's requests and what came of each, in the drive's time; empty when the drive has no kline line.</summary>
    public IReadOnlyList<KLineExchange> KLine { get; init; } = Array.Empty<KLineExchange>();

    /// <summary>The bytes the K-line lost (a baud mismatch or a collision), in the drive's time.</summary>
    public IReadOnlyList<KLineNote> KLineNotes { get; init; } = Array.Empty<KLineNote>();
}

/// <summary>
/// Runs a drive (plan 4, design milestone 4): the car file on a machine, the engine around it, and the
/// car's inputs following the drive — at each segment's start exactly and every 10 ms between.
/// <list type="bullet">
/// <item>on: the key on, the engine stopped; crank: the starter at the segment's rpm; idle: the
/// starter released, plan 3's idle plant; drive: the shaft at the drive's speed (a dyno, the
/// engine's own speed when the drive holds it); off: the key off (AN1 to 0 V) and the engine left to
/// coast.</item>
/// <item>When the main relay drops after an off and a later segment turns the key on again, that
/// key-on powers a new machine with the EEPROM image the after-run wrote — never the file.</item>
/// <item>When it drops while the drive has the key on, the drive's time up to its next key-on after an
/// off (or its end) does not run: the coverage lists it, and the run is incomplete (final review I3).</item>
/// </list>
/// </summary>
public static class DriveRun
{
    const ulong StepCycles = 240_000;                      // 10 ms
    const ulong TraceInterval = 2_400_000;                 // 100 ms

    /// <summary>The engine a drive runs: plan 3's warm-idle engine with the breathing law and no starter of its own.</summary>
    public static EngineScenario Scenario { get; } =
        EngineScenario.WarmIdle with { Name = "drive", CrankAtSeconds = null, Law = BreathingLaw.Car };

    /// <summary>
    /// <see cref="Run"/> with every check observing it, and the coverage and the verdict (C2) filled in:
    /// what <c>run --drive</c> and <c>check</c> call.
    /// </summary>
    public static DriveResult RunWithChecks(FlashedImage image, Drive drive, DriveRunOptions? options = null)
    {
        options ??= new DriveRunOptions();
        CheckSet? set = null;
        var watch = new DriveCoverage.Watch();
        var observers = (options.Observers ?? Array.Empty<Action<DriveContext>>())
            .Prepend(context => watch.Attach(context))
            .Prepend(context => set = CheckSet.Attach(context)).ToArray();
        var r = Run(image, drive, options with { Observers = observers });
        r = r with { Checks = set!.Checks };
        var coverage = DriveCoverage.Of(r, watch);
        return r with { Coverage = coverage, Verdict = Verdict.Of(r.Checks, coverage) };
    }

    public static DriveResult Run(FlashedImage image, Drive drive, DriveRunOptions? options = null)
    {
        options ??= new DriveRunOptions();
        var machineOptions = (options.Machine ?? new MachineOptions()) with { Inputs = Scenario.Inputs };
        var total = (ulong)Math.Round(DriveTimeline.Duration(drive) * machineOptions.CpuHz);
        var context = new DriveContext(image, drive);
        foreach (var observer in options.Observers ?? Array.Empty<Action<DriveContext>>())
            observer(context);

        // The laptop on the K-line (plan 5 Task 4): one tester for the whole drive, plugged into every power cycle's line.
        var usesKLine = drive.Segments.Any(s => s.KLine.Count > 0);
        KLineTester? tester = null;
        KLineTester? Plug(Machine machine, ulong at)
        {
            if (!usesKLine)
                return null;
            tester ??= new KLineTester(machine.Scheduler);
            tester.Plug(machine.KLine, machine.Scheduler, at);
            return tester;
        }

        var runs = new List<CycleRun>();
        var eeprom = options.Eeprom;
        ulong start = 0;
        while (true)
        {
            var run = new CycleRun(image, drive, machineOptions with { EepromImage = eeprom }, options.CaptureCapacity,
                context, runs.Count, start, Plug);
            run.RunTo(total);
            runs.Add(run);
            if (run.NextPowerOn() is not { } next)
                break;
            eeprom = run.Machine.Board.Eeprom.ToImage();
            start = next;
        }
        return Result(image, drive, options, machineOptions, runs, tester);
    }

    static DriveResult Result(FlashedImage image, Drive drive, DriveRunOptions options, MachineOptions machineOptions, List<CycleRun> runs,
        KLineTester? tester)
    {
        var counts = new Dictionary<Type, long>();
        foreach (var run in runs)
        {
            foreach (var (type, count) in run.Machine.Events.Counts)
                counts[type] = counts.GetValueOrDefault(type) + count;
        }
        var last = runs[^1];
        var finalEeprom = last.Eeprom() with { Source = Source(options.Eeprom) };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join("|", runs.Select(r => r.Machine.StateHash())))))
            .ToLowerInvariant();
        var assumptions = MachineAssumptions.For(machineOptions).ToList();
        assumptions.AddRange(MachineAssumptions.ForEngine(Scenario));
        assumptions.AddRange(MachineAssumptions.ForDrive());
        assumptions.Add("drive: the car's inputs follow the drive at each segment's start and every 10 ms between; an off segment drops KL15 (AN1) to 0 V; when the main relay drops after it, the next key-on powers a new machine with the EEPROM image the after-run wrote [call]");
        return new DriveResult(image, drive, runs.Select(r => r.Result()).ToArray(),
            runs.SelectMany(r => r.Shift(r.Capture.Events, (e, d) => e with { Cycle = e.Cycle + d })).ToArray(),
            runs.Sum(r => r.Capture.Dropped),
            runs.SelectMany(r => r.Shift(r.Cylinders.Injections, (e, d) => e with { Open = e.Open + d, Close = e.Close + d })).ToArray(),
            runs.SelectMany(r => r.Shift(r.Cylinders.Sparks, (e, d) => e with { DwellStart = e.DwellStart + d, Fire = e.Fire + d })).ToArray(),
            runs.SelectMany(r => r.Trace).ToArray(),
            finalEeprom,
            runs.Sum(r => r.Machine.WatchdogResets), runs.Sum(r => r.Machine.SoftwareResets),
            runs.SelectMany(r => r.Shift(r.Machine.Events.All, (e, d) => e with { Cycle = e.Cycle + d })).ToArray(),
            counts,
            runs.SelectMany(r => r.Machine.Inventory.Records).ToArray(),
            RunState.Stubs(canPartners: true, engine: true, drive: true, klineTester: tester != null),
            Array.Empty<ICheck>(), hash, assumptions,
            last.Start + last.Machine.Cycles, (ulong)runs.Sum(r => (decimal)r.Machine.Instructions))
        {
            KLine = tester?.Exchanges.ToArray() ?? Array.Empty<KLineExchange>(),
            KLineNotes = tester?.Notes.ToArray() ?? Array.Empty<KLineNote>(),
        };
    }

    /// <summary>
    /// Why a power cycle's run ended: whether the main relay dropped (after the handover), whether the
    /// drive had the key on then, and whether the firmware read it on (AN1).
    /// </summary>
    public static CycleEnd EndOf(bool relayDropped, bool driveKeyOn, bool keyReadOn, bool restarted = false) =>
        !relayDropped ? CycleEnd.DriveEnded
        : !driveKeyOn ? CycleEnd.AfterRun
        : restarted ? CycleEnd.Restarted
        : keyReadOn ? CycleEnd.RelayDroppedKeyOn
        : CycleEnd.RelayDroppedKeyReadOff;

    /// <summary>How long a run goes on after the relay dropped with the key on, waiting for the firmware's reset: 1 s.</summary>
    const ulong RestartWait = 24_000_000;

    static string Source(byte[]? eeprom) =>
        eeprom is null ? "blank" : Convert.ToHexString(SHA256.HashData(eeprom)).ToLowerInvariant();

    /// <summary>One machine's life inside a drive, from its power-on to the relay's drop or the drive's end.</summary>
    sealed class CycleRun
    {
        readonly FlashedImage image;
        readonly Drive drive;
        readonly DriveContext context;
        readonly int index;
        readonly double cpuHz;
        readonly double[] startOf;
        readonly Engine engine;
        readonly CarControls car;
        readonly CycleWatch watch;
        readonly byte[]? eepromImage;
        readonly KLineTester? tester;
        readonly List<TraceRow> trace = new();
        CarState state;
        int entered;
        bool keyOff;
        readonly List<KeySwitch> keySwitches = new();
        ulong? restartedAt;                                      // the firmware's reset after the relay dropped with the key on
        double? held;
        double lastChange = double.NegativeInfinity;

        /// <param name="plug">Plugs the drive's K-line tester, if it has one, into this cycle's machine; null when the drive has no kline line.</param>
        public CycleRun(FlashedImage image, Drive drive, MachineOptions options, int capacity, DriveContext context, int index, ulong start,
            Func<Machine, ulong, KLineTester?> plug)
        {
            this.image = image;
            this.drive = drive;
            this.context = context;
            this.index = index;
            Start = start;
            cpuHz = options.CpuHz;
            eepromImage = options.EepromImage;
            startOf = new double[drive.Segments.Count];
            for (var (i, at) = (0, 0.0); i < drive.Segments.Count; at += drive.Segments[i].Seconds, i++)
                startOf[i] = at;

            Machine = Machine.Create(image, options);
            (context.PowerCycleIndex, context.PowerCycleStart, context.Machine, context.Engine, context.Demand, context.Ended) = (index, start, Machine, null, null, null);
            context.RaiseStarted(Machine, index);
            Machine.PowerOn();
            Machine.Events.Added += e =>
            {
                if (e is ResetEvent { Kind: ResetKind.Software } && watch?.PowerOff != null)
                    restartedAt ??= Machine.Cycles;
            };
            tester = plug(Machine, start);
            engine = Engine.Attach(Machine, Scenario);
            context.Engine = engine;
            var first = DriveTimeline.At(drive, Seconds);
            entered = first.SegmentIndex - 1;
            state = StateAt(first);
            car = CarControls.Attach(Machine, () => state);
            Capture = new OutputCapture(Machine, () => engine.Shaft.Angle, capacity);
            Cylinders = new CylinderEvents(Machine, () => engine.Shaft.Angle);
            watch = new CycleWatch(Machine);
            for (var i = first.SegmentIndex + 1; i < drive.Segments.Count; i++)
            {
                var segment = i;
                var at = (ulong)Math.Round(startOf[i] * cpuHz);
                if (at >= start)
                    Machine.Scheduler.Schedule(at - start, "drive segment", () => Step(segment));
            }
            Every(0);
            Sample(0);
        }

        public Machine Machine { get; }

        /// <summary>The drive's cycle at this machine's power-on.</summary>
        public ulong Start { get; }

        public OutputCapture Capture { get; }

        public CylinderEvents Cylinders { get; }

        public IReadOnlyList<TraceRow> Trace => trace;

        /// <summary>Why the run stopped; DriveEnded until it has.</summary>
        public CycleEnd End { get; private set; }

        double Seconds => (Start + Machine.Cycles) / cpuHz;

        public void RunTo(ulong total)
        {
            Machine.Run(total - Start, _ => PoweredOff());
            End = EndOf(watch.PowerOff != null, !keyOff, CarControls.KeyIsOn(Machine.Board), restartedAt != null);
            context.Ended = End;
            context.RaiseEnded(Machine, index);
        }

        /// <summary>
        /// The ECU loses its power when the main relay drops with the key off, or read off. With the key on, KL15 keeps the
        /// CPU powered [obs: the firmware's loop after its relay drop, 0D:840E-0D:8434, waits about 10 ms, then for the key,
        /// and resets itself by SRST once it reads on]: the run goes on until that reset, which starts the next power cycle,
        /// or for <see cref="RestartWait"/> at most (plan 5 Task 6).
        /// </summary>
        bool PoweredOff() => watch.PowerOff is { } off
            && (keyOff || !CarControls.KeyIsOn(Machine.Board) || restartedAt != null || Machine.Cycles - off >= RestartWait);

        /// <summary>
        /// The drive's cycle of the next power-on: the firmware's own reset after a relay drop with the key on, else the key-on
        /// after this cycle's relay drop; null when neither comes.
        /// </summary>
        public ulong? NextPowerOn()
        {
            if (watch.PowerOff is null)
                return null;
            if (restartedAt is not null)
                return Start + Machine.Cycles;                    // where this run stopped: at the firmware's reset
            for (var i = entered + 1; i < drive.Segments.Count; i++)
            {
                if (drive.Segments[i - 1].Mode == DriveMode.Off && drive.Segments[i].Mode != DriveMode.Off)
                    return (ulong)Math.Round(startOf[i] * cpuHz);
            }
            return null;
        }

        public IEnumerable<T> Shift<T>(IEnumerable<T> items, Func<T, ulong, T> shift) =>
            Start == 0 ? items : items.Select(item => shift(item, Start));

        public EepromSummary Eeprom()
        {
            var chip = Machine.Board.Eeprom;
            return new EepromSummary(Source(eepromImage), chip.WordsRead, chip.WordsWritten, chip.ToImage(), chip.AddressesRead.Order().ToArray());
        }

        public PowerCycle Result() => new(index, Start, watch.Milestones,
            new EngineSummary(engine.Scenario.Name, engine.Phases.ToArray(), engine.Rpm, engine.AirflowKgPerHour,
                engine.Vanos.IntakeAdvance, engine.Vanos.ExhaustRetard, EngineSummary.OutcomeOf(engine.Phases, KeyOffAt)),
            FaultMemory.Read(Machine), FaultMemory.SavedInEeprom(image.Image, Machine.Board.Eeprom.ToImage()),
            RunState.ReadFirmwareState(Machine), Eeprom(), watch.Health, Start + Machine.Cycles,
            new FaultContext(eepromImage is not { } start || start.All(b => b == 0xFF), CanPartners: true), End)
        {
            KeySwitches = keySwitches.ToArray(),
        };

        void Every(ulong at) => Machine.Scheduler.Schedule(at, "drive step", () =>
        {
            Step(-1);
            Every(at + StepCycles);
        });

        void Sample(ulong at) => Machine.Scheduler.Schedule(at, "trace", () =>
        {
            trace.Add(new TraceRow(Seconds, VariableTrace.Sample(Machine, engine)));
            Sample(at + TraceInterval);
        });

        /// <summary>A segment's start (<paramref name="segment"/>), or the 10 ms step (-1): enter what began, then follow the drive.</summary>
        void Step(int segment)
        {
            var reached = Math.Max(segment, DriveTimeline.At(drive, Seconds).SegmentIndex);
            while (entered < reached)
                Enter(++entered);
            var point = DriveTimeline.At(drive, Math.Max(Seconds, startOf[entered]));
            Follow(point);
        }

        void Enter(int i)
        {
            var segment = drive.Segments[i];
            var previous = i > 0 ? drive.Segments[i - 1].Mode : (DriveMode?)null;
            var point = DriveTimeline.At(drive, startOf[i]);
            if (i > 0 && Controls(DriveTimeline.At(drive, startOf[i] - 1e-9)) != Controls(point))
                lastChange = startOf[i];
            switch (segment.Mode)
            {
                case DriveMode.Off:
                    watch.KeyOff ??= Machine.Cycles;
                    Key(false);
                    break;
                case DriveMode.On:
                    Key(true);
                    break;
                case DriveMode.Crank:
                    Key(true);
                    engine.Starve(false);
                    engine.StartStarter(point.Values["rpm"]);
                    break;
                case DriveMode.Stall:
                    Key(true);
                    engine.Starve(true);
                    break;
                case DriveMode.Idle or DriveMode.Drive:
                    if (previous == DriveMode.Crank)
                        engine.StopStarter();
                    held = segment.Mode == DriveMode.Drive && point.RpmFromEngine ? engine.Rpm : null;
                    break;
            }
            foreach (var action in segment.KLine)
                tester?.Apply(action);
            context.RaiseSegmentEntered(point);
        }

        void Follow(DrivePoint point)
        {
            state = StateAt(point);
            car.Apply();
            engine.SetTemperatures(point.Values["intake"], point.Values["coolant"]);
            engine.SetCommandedSpeed(point.Mode == DriveMode.Drive ? held ?? point.Values["rpm"] : null);
            context.Demand = new DriveDemand(point, car.KeyOn(), point.Seconds - lastChange);
        }

        void Key(bool on)
        {
            if (keyOff == !on)
                return;
            keyOff = !on;
            keySwitches.Add(new KeySwitch(Start + Machine.Cycles, on));
            Machine.Board.SetInputs(Machine.Board.Inputs.WithIgnition(on));
        }

        /// <summary>The key was off in <paramref name="cycle"/> of this machine: the last switch at or before it turned it off.</summary>
        bool KeyOffAt(ulong cycle) => keySwitches.LastOrDefault(k => k.Cycle - Start <= cycle) is { On: false };

        static (double Pedal, double Clutch, double Gear) Controls(DrivePoint point) =>
            (point.Values["pedal"], point.Values["clutch"], point.Values["gear"]);

        static CarState StateAt(DrivePoint point)
        {
            var v = point.Values;
            return new CarState
            {
                CoolantC = v["coolant"], OilC = v["oil"], IntakeC = v["intake"], AmbientC = v["ambient"],
                ClockMinutes = v["clock"], Ac = v["ac"] >= 0.5, PedalFraction = v["pedal"] / 100, Gear = (int)v["gear"],
                SpeedKmh = v["speed"], Clutch = v["clutch"] >= 0.5, Brake = v["brake"] >= 0.5,
                Battery = v.TryGetValue("battery", out var battery) ? battery : null,
                Forces = point.Forces, Silenced = point.Silenced,
            };
        }
    }
}
