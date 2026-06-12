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
/// One power cycle of a drive: StartCycle and EndCycle in the drive's time; the milestones, the
/// engine's phases and the scheduler figures in the cycle's own time (from its power-on); the fault
/// memory, the RAM read-outs and the EEPROM at its end.
/// </summary>
public sealed record PowerCycle(int Index, ulong StartCycle, StartupMilestones Milestones, EngineSummary? Engine,
    FaultMemoryState Faults, IReadOnlyList<FaultEntry> SavedFaults, FirmwareState Firmware, EepromSummary Eeprom,
    SchedulerHealth Scheduler, ulong EndCycle);

/// <summary>
/// A whole drive: its power cycles, and every power cycle's outputs, cylinder events, trace and machine
/// events together, in the drive's time. Eeprom is the last cycle's chip, its source the drive's first
/// image. Checks is empty from <see cref="DriveRun.Run"/>; a run with checks fills it.
/// </summary>
public sealed record DriveResult(FlashedImage Image, Drive Drive, IReadOnlyList<PowerCycle> PowerCycles,
    IReadOnlyList<OutputEvent> OutputEvents, long OutputEventsDropped, IReadOnlyList<Injection> Injections,
    IReadOnlyList<Spark> Sparks, IReadOnlyList<TraceRow> Trace, EepromSummary Eeprom, int WatchdogResets,
    int SoftwareResets, IReadOnlyList<SimEvent> Events, IReadOnlyDictionary<Type, long> EventCounts,
    IReadOnlyList<AccessRecord> Inventory, IReadOnlyList<StubInfo> Stubs, IReadOnlyList<ICheck> Checks,
    string StateHash, IReadOnlyList<string> Assumptions, ulong Cycles, ulong Instructions)
{
    /// <summary>The check called <paramref name="name"/>.</summary>
    public ICheck Check(string name) =>
        Checks.FirstOrDefault(c => c.Name == name) ?? throw new KeyNotFoundException($"no check '{name}' in this run");
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
/// </list>
/// </summary>
public static class DriveRun
{
    const ulong StepCycles = 240_000;                      // 10 ms
    const ulong TraceInterval = 2_400_000;                 // 100 ms

    /// <summary>The engine a drive runs: plan 3's warm-idle engine with the breathing law and no starter of its own.</summary>
    public static EngineScenario Scenario { get; } =
        EngineScenario.WarmIdle with { Name = "drive", CrankAtSeconds = null, Law = BreathingLaw.Car };

    public static DriveResult Run(FlashedImage image, Drive drive, DriveRunOptions? options = null)
    {
        options ??= new DriveRunOptions();
        var machineOptions = (options.Machine ?? new MachineOptions()) with { Inputs = Scenario.Inputs };
        var total = (ulong)Math.Round(DriveTimeline.Duration(drive) * machineOptions.CpuHz);
        var context = new DriveContext(image, drive);
        foreach (var observer in options.Observers ?? Array.Empty<Action<DriveContext>>())
            observer(context);

        var runs = new List<CycleRun>();
        var eeprom = options.Eeprom;
        ulong start = 0;
        while (true)
        {
            var run = new CycleRun(image, drive, machineOptions with { EepromImage = eeprom }, options.CaptureCapacity,
                context, runs.Count, start);
            run.RunTo(total);
            runs.Add(run);
            if (run.NextPowerOn() is not { } next)
                break;
            eeprom = run.Machine.Board.Eeprom.ToImage();
            start = next;
        }
        return Result(image, drive, options, machineOptions, runs);
    }

    static DriveResult Result(FlashedImage image, Drive drive, DriveRunOptions options, MachineOptions machineOptions, List<CycleRun> runs)
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
            RunState.Stubs(canPartners: true, engine: true, drive: true),
            Array.Empty<ICheck>(), hash, assumptions,
            last.Start + last.Machine.Cycles, (ulong)runs.Sum(r => (decimal)r.Machine.Instructions));
    }

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
        readonly List<TraceRow> trace = new();
        CarState state;
        int entered;
        bool keyOff;
        double? held;
        double lastChange = double.NegativeInfinity;

        public CycleRun(FlashedImage image, Drive drive, MachineOptions options, int capacity, DriveContext context, int index, ulong start)
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
            (context.PowerCycleIndex, context.PowerCycleStart, context.Machine, context.Engine, context.Demand) = (index, start, Machine, null, null);
            context.RaiseStarted(Machine, index);
            Machine.PowerOn();
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

        double Seconds => (Start + Machine.Cycles) / cpuHz;

        public void RunTo(ulong total)
        {
            Machine.Run(total - Start, _ => watch.PowerOff != null);
            context.RaiseEnded(Machine, index);
        }

        /// <summary>The drive's cycle of the key-on after this cycle's relay drop, or null when no segment turns the key on again.</summary>
        public ulong? NextPowerOn()
        {
            if (watch.PowerOff is null)
                return null;
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
                engine.Vanos.IntakeAdvance, engine.Vanos.ExhaustRetard, EngineSummary.OutcomeOf(engine.Phases, watch.KeyOff)),
            FaultMemory.Read(Machine), FaultMemory.SavedInEeprom(image.Image, Machine.Board.Eeprom.ToImage()),
            RunState.ReadFirmwareState(Machine), Eeprom(), watch.Health, Start + Machine.Cycles);

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
                    engine.StartStarter(point.Values["rpm"]);
                    break;
                case DriveMode.Idle or DriveMode.Drive:
                    if (previous == DriveMode.Crank)
                        engine.StopStarter();
                    held = segment.Mode == DriveMode.Drive && point.RpmFromEngine ? engine.Rpm : null;
                    break;
            }
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
            Machine.Board.SetInputs(Machine.Board.Inputs.WithIgnition(on));
        }

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
