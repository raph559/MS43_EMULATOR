using System.Security.Cryptography;

namespace Ms43Sim.Core;

/// <summary>
/// How a start-up run goes. With an engine the machine's inputs are the scenario's. Instrument is
/// called once, after the engine is attached and before the run (a test adds OnInstruction hooks
/// there); it must not change the machine's state, and it is not part of the report.
/// </summary>
public sealed record StartupRunOptions(double Seconds = 5.0, double? KeyOffAtSeconds = null, MachineOptions? Machine = null,
    bool CanPartners = true, EngineScenario? Engine = null, Action<Machine>? Instrument = null);

/// <summary>How an engine run ended.</summary>
public enum EngineOutcome { NeverStarted, Running, Stalled, StoppedAfterKeyOff }

/// <summary>The engine at the end of the run: its phases, speed, airflow, cam offsets from rest (°CRK) and how it ended.</summary>
public sealed record EngineSummary(string Scenario, IReadOnlyList<EnginePhaseChange> Phases,
    double FinalRpm, double AirflowKgPerHour, double IntakeAdvance, double ExhaustRetard, EngineOutcome Outcome)
{
    /// <summary>
    /// Never started: it never ran on its own. Running: it runs at the end. Otherwise it stopped
    /// after the key-off when its last run-down began at or after the key-off, and stalled when it
    /// began with the key still on.
    /// </summary>
    public static EngineOutcome OutcomeOf(IReadOnlyList<EnginePhaseChange> phases, ulong? keyOffCycle) =>
        OutcomeOf(phases, cycle => keyOffCycle is { } k && cycle >= k);

    /// <summary>The outcome, with <paramref name="keyOffAt"/> saying whether the key was off when the last run-down began.</summary>
    public static EngineOutcome OutcomeOf(IReadOnlyList<EnginePhaseChange> phases, Func<ulong, bool> keyOffAt)
    {
        if (!phases.Any(p => p.Phase == EnginePhase.Running))
            return EngineOutcome.NeverStarted;
        if (phases[^1].Phase == EnginePhase.Running)
            return EngineOutcome.Running;
        var runDown = phases.Last(p => p.Phase == EnginePhase.Stopping).Cycle;
        return keyOffAt(runDown) ? EngineOutcome.StoppedAfterKeyOff : EngineOutcome.Stalled;
    }

    /// <summary>The outcome in words, for the report and the console.</summary>
    public string Describe()
    {
        static string S(ulong cycle) => (cycle / 24e6).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var runDown = Phases.LastOrDefault(p => p.Phase == EnginePhase.Stopping);
        return Outcome switch
        {
            EngineOutcome.NeverStarted => "never started: it was not running on its own when the starter released",
            EngineOutcome.Running => $"running at {FinalRpm.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} rpm at the end",
            EngineOutcome.Stalled => $"stalled at {S(runDown!.Cycle)} s with the key on",
            _ => $"stopped after the key-off (coasting down from {S(runDown!.Cycle)} s)",
        };
    }
}

/// <summary>In order of precedence; see <see cref="StartupRun"/>.</summary>
public enum StartupOutcome { SchedulerRunning, PoweredOff, ResetLoop, Trapped, Stalled, StayedInBoot, NoProgress }

/// <summary>The first cycle each program checkpoint was reached; null when it never was.</summary>
public sealed record StartupMilestones(
    ulong? BootServiced, ulong? Handover, ulong? InterruptsEnabled, ulong? EepromLoaded, ulong? MainLoop,
    ulong? KeyOff, ulong? PowerOff);

/// <summary>The main loop's SRVWDT (0E:0144): how often, the longest gap between two, and E2A6.</summary>
public sealed record SchedulerHealth(int MainLoopServices, ulong LongestGapCycles, ushort Uptime);

/// <summary>RAM read-outs at the end of the run (plan 2, Reference facts).</summary>
public sealed record FirmwareState(
    ushort F8F4, ushort Fd26, ushort Fd7e, ushort Fd0c, ushort Fd46,
    byte MonitorState, byte LastError, byte ErrorCount, ushort ResetReason, byte EngineState,
    ushort EcuFaults, byte ThrottleCheckState, ushort Fd14, ushort Fd9c, sbyte Ambient)
{
    /// <summary>The CRC task finished and passed (F8F4) and FD26 shows all three CRCs finished, none failed.</summary>
    public bool CrcChecksPassed =>
        (F8F4 & 0x18C6) == 0 && (F8F4 & 0x0421) == 0x0421 && (Fd26 & 0x001C) == 0x001C && (Fd26 & 0x00E0) == 0;

    public bool BootCrcFinished => (Fd26 & 0x0004) != 0;
    public bool CalibrationCrcFinished => (Fd26 & 0x0008) != 0;
    public bool ProgramCrcFinished => (Fd26 & 0x0010) != 0;
    public bool BootCrcFailed => (Fd26 & 0x0020) != 0;
    public bool CalibrationCrcFailed => (Fd26 & 0x0040) != 0;
    public bool ProgramCrcFailed => (Fd26 & 0x0080) != 0;
    public bool MonitorSumFirstPassDone => (Fd7e & 0x0200) == 0;
    public bool MonitorSumMismatch => (Fd7e & 0x0100) != 0;
    public bool ThrottleLimp => (Fd0c & 0x0400) != 0;
    public bool EepromDefaultsLoaded => (Fd46 & 0x0020) != 0;
}

public sealed record MonitorSummary(int Exchanges, int WrongAnswers, int UnexpectedAnswers, int ErrorCounter);

/// <summary>Source is "blank" or the SHA-256 of the loaded image; FinalImage is the chip at the end; AddressesRead in order.</summary>
public sealed record EepromSummary(string Source, int WordsRead, int WordsWritten, byte[] FinalImage, IReadOnlyCollection<int> AddressesRead);

/// <summary>A stubbed device, its side of design §7's line, and why it stands on that side.</summary>
public sealed record StubInfo(string Device, string Behaviour, bool BetweenCpuAndCheckedOutput, string Reason = "");

public sealed record StartupResult(
    FlashedImage Image,
    StartupOutcome Outcome,
    StartupMilestones Milestones,
    SchedulerHealth Scheduler,
    FirmwareState Firmware,
    MonitorSummary Monitor,
    IReadOnlyList<CanTxSummary> CanTransmitted,
    EepromSummary Eeprom,
    int WatchdogResets,
    int SoftwareResets,
    IReadOnlyList<SimEvent> Events,
    IReadOnlyDictionary<Type, long> EventCounts,
    long EventsDropped,
    IReadOnlyList<AccessRecord> Inventory,
    IReadOnlyList<BranchRecord> LastBranches,
    IReadOnlyList<StubInfo> Stubs,
    ulong Cycles,
    ulong Instructions,
    ulong Entries,
    string StateHash,
    IReadOnlyList<string> Assumptions,
    FaultMemoryState Faults,
    IReadOnlyList<FaultEntry> SavedFaults,
    EngineSummary? Engine,
    IReadOnlyList<OutputEvent> OutputEvents,
    long OutputEventsDropped,
    IReadOnlyDictionary<string, long> OutputCounts,
    IReadOnlyList<Injection> Injections,
    IReadOnlyList<Spark> Sparks,
    IReadOnlyList<TraceRow> Trace,
    double ThrottleAngle,
    IReadOnlyDictionary<uint, long>? CanPartnersSent,
    FaultContext FaultContext)
{
    public CalibrationSafetyAssessment PhysicalCalibrationSafety => CalibrationSafetyAssessment.Current;

    /// <summary>Events of type T the run raised, including those the log did not keep.</summary>
    public long CountOf<T>() where T : SimEvent => EventCounts.GetValueOrDefault(typeof(T));
}

/// <summary>
/// Runs an image from power-on with the key on and the engine stopped (design §9 milestone 2), and
/// watches the program checkpoints the car, release, E85CAMPORT and stock files share:
/// 00:2EFC the boot's last SRVWDT, 0E:366A the handover, 0E:36BC BSET PSW.11, 0E:3700 the EEPROM
/// block loads returned, 0E:0094 the main-loop head, 0E:0144 the main loop's SRVWDT. The run ends
/// at the time limit or at power-off (the main relay released after the handover). A key-off, if
/// asked for, drops AN1 (KL15) to 0 V at that time.
/// Outcome, the first that holds: a trap → Trapped; a watchdog or software reset → ResetLoop; the
/// relay released after the handover → PoweredOff; the main loop's SRVWDT in the last 30 ms →
/// SchedulerRunning; a handover → Stalled; any SRVWDT → StayedInBoot; else NoProgress.
/// </summary>
public static class StartupRun
{
    const ulong SchedulerWindow = 720_000;                 // 30 ms
    const int KeyOnCode = 235;                             // AN1: the firmware's KL15 "on" threshold
    const ulong TraceInterval = 2_400_000;                 // 100 ms

    /// <summary>The checkpoint bytes match: the program addresses this runner watches are valid.</summary>
    public static bool Recognises(FirmwareImage image) => RunState.Recognises(image);

    public static StartupResult Run(FlashedImage image, StartupRunOptions? options = null)
    {
        options ??= new StartupRunOptions();
        var machineOptions = options.Machine ?? new MachineOptions();
        if (options.Engine is { } scenario)
            machineOptions = machineOptions with { Inputs = scenario.Inputs };
        var machine = Machine.Create(image, machineOptions);
        machine.PowerOn();
        CanPartners? partners = null;
        if (options.CanPartners)
        {
            partners = new CanPartners(machine.Scheduler, machine.Can.Deliver,
                () => BoardInputs.AdcCode(machine.Board.AnalogVolts(1)) >= KeyOnCode);
            partners.Start();
        }
        var engine = options.Engine is { } engineScenario ? Engine.Attach(machine, engineScenario) : null;
        Func<double>? crankAngle = engine is null ? null : () => engine.Shaft.Angle;
        var capture = new OutputCapture(machine, crankAngle);
        var cylinders = crankAngle is null ? null : new CylinderEvents(machine, crankAngle);
        var trace = new List<TraceRow>();
        void Sample(ulong at) => machine.Scheduler.Schedule(at, "trace", () =>
        {
            trace.Add(new TraceRow(machine.Cycles / machineOptions.CpuHz, VariableTrace.Sample(machine, engine)));
            Sample(at + TraceInterval);
        });
        Sample(machine.Cycles);

        var watch = new CycleWatch(machine);
        var recognised = watch.Recognised;
        if (options.KeyOffAtSeconds is { } keyOffSeconds)
        {
            machine.Scheduler.Schedule((ulong)Math.Round(keyOffSeconds * machineOptions.CpuHz), "key off", () =>
            {
                watch.KeyOff = machine.Cycles;
                machine.Board.SetInputs(machine.Board.Inputs.WithIgnition(false));
            });
        }

        options.Instrument?.Invoke(machine);
        var limit = (ulong)Math.Round(options.Seconds * machineOptions.CpuHz);
        machine.Run(limit, _ => watch.PowerOff != null);

        var end = machine.Cycles;
        var keyOff = watch.KeyOff;
        var outcome = machine.Events.CountOf<TrapEvent>() > 0 ? StartupOutcome.Trapped
            : machine.WatchdogResets + machine.SoftwareResets > 0 ? StartupOutcome.ResetLoop
            : watch.PowerOff != null ? StartupOutcome.PoweredOff
            : watch.LastService is { } last && end - last <= SchedulerWindow ? StartupOutcome.SchedulerRunning
            : watch.Handover != null ? StartupOutcome.Stalled
            : machine.Watchdog.ServiceCount > 0 ? StartupOutcome.StayedInBoot
            : StartupOutcome.NoProgress;

        var assumptions = MachineAssumptions.For(machineOptions).ToList();
        if (options.Engine is { } engineRun)
            assumptions.AddRange(MachineAssumptions.ForEngine(engineRun));
        if (options.CanPartners)
            assumptions.Add("CAN partners: 153h every 10 ms, 613h and 615h every 200 ms, the first 100, 105 and 110 ms after power-on; a frame is delivered only while the key is on (AN1 code ≥ 235) [assumption]");
        if (options.KeyOffAtSeconds is { } k)
            assumptions.Add($"key off at {k.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} s: only AN1 (KL15 sense) drops to 0 V, every other input stays; the run stops when the main relay drops (latch bit 5 cleared after the handover) [assumption]");
        if (!recognised)
            assumptions.Add("program checkpoints not recognised: milestones and the scheduler check are skipped (the checkpoint bytes are not at 00:2EFC, 0E:366A, 0E:36BC, 0E:3700, 0E:3812, 0E:0094 and 0E:0144)");

        var eeprom = machine.Board.Eeprom;
        var source = machineOptions.EepromImage is { } loaded
            ? Convert.ToHexString(SHA256.HashData(loaded)).ToLowerInvariant()
            : "blank";
        var monitor = machine.Board.Monitor;
        return new StartupResult(
            image, outcome,
            watch.Milestones,
            watch.Health,
            RunState.ReadFirmwareState(machine),
            new MonitorSummary(monitor.Exchanges, monitor.WrongAnswers, monitor.UnexpectedAnswers, monitor.ErrorCounter),
            machine.Can.Transmitted,
            new EepromSummary(source, eeprom.WordsRead, eeprom.WordsWritten, eeprom.ToImage(), eeprom.AddressesRead.Order().ToArray()),
            machine.WatchdogResets, machine.SoftwareResets,
            machine.Events.All.ToList(), new Dictionary<Type, long>(machine.Events.Counts), machine.Events.Dropped,
            machine.Inventory.Records, machine.Branches.Last(), RunState.Stubs(options.CanPartners, engine != null),
            end, machine.Instructions, machine.Entries, machine.StateHash(), assumptions,
            FaultMemory.Read(machine), FaultMemory.SavedInEeprom(image.Image, eeprom.ToImage()),
            engine is null ? null : new EngineSummary(engine.Scenario.Name, engine.Phases.ToArray(), engine.Rpm,
                engine.AirflowKgPerHour, engine.Vanos.IntakeAdvance, engine.Vanos.ExhaustRetard,
                EngineSummary.OutcomeOf(engine.Phases, keyOff)),
            capture.Events, capture.Dropped, capture.Counts,
            cylinders?.Injections ?? Array.Empty<Injection>(), cylinders?.Sparks ?? Array.Empty<Spark>(),
            trace, machine.Throttle.Angle, partners?.Sent,
            new FaultContext(machineOptions.EepromImage is not { } start || start.All(b => b == 0xFF), options.CanPartners));
    }
}
