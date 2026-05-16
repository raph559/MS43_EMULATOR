using System.Security.Cryptography;

namespace Ms43Sim.Core;

public sealed record StartupRunOptions(double Seconds = 5.0, double? KeyOffAtSeconds = null, MachineOptions? Machine = null);

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
    ushort EcuFaults, byte ThrottleCheckState, ushort Fd14)
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

/// <summary>Source is "blank" or the SHA-256 of the loaded image; FinalImage is the chip at the end.</summary>
public sealed record EepromSummary(string Source, int WordsRead, int WordsWritten, byte[] FinalImage);

/// <summary>A stubbed device and its side of design §7's line.</summary>
public sealed record StubInfo(string Device, string Behaviour, bool BetweenCpuAndCheckedOutput);

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
    IReadOnlyList<string> Assumptions)
{
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
    const uint BootServiced = 0x002EFC, Handover = 0x0E366A, InterruptsEnabled = 0x0E36BC;
    const uint EepromLoaded = 0x0E3700, MainLoop = 0x0E0094, MainLoopService = 0x0E0144;
    const ulong SchedulerWindow = 720_000;                 // 30 ms

    static readonly (uint Cpu, byte[] Bytes)[] Checkpoints =
    {
        (BootServiced, new byte[] { 0xA7, 0x58, 0xA7, 0xA7 }),
        (Handover, new byte[] { 0xDA, 0x0E, 0xBA, 0x2E }),
        (InterruptsEnabled, new byte[] { 0xBF, 0x88 }),
        (EepromLoaded, new byte[] { 0xDA, 0x0D, 0x08, 0xEC }),
        (0x0E3812, new byte[] { 0x9A, 0x42, 0xFE, 0x90 }),
        (MainLoop, new byte[] { 0x8A, 0x42, 0x02, 0x90 }),
        (MainLoopService, new byte[] { 0xA7, 0x58, 0xA7, 0xA7 }),
    };

    static readonly StubInfo[] Stubs =
    {
        new("EEPROM", "blank or from a file", false),
        new("output-driver chips A/B", "always \"no fault\"; outputs not captured", true),
        new("monitoring module", "shut-off path not modelled", true),
        new("CAN partners", "silent, every frame acknowledged", false),
        new("knock chip and coil feedback", "0 V", false),
        new("K-line tester", "none", false),
    };

    /// <summary>The checkpoint bytes match: the program addresses this runner watches are valid.</summary>
    public static bool Recognises(FirmwareImage image) =>
        Checkpoints.All(c => image.Bytes.Slice((int)(c.Cpu & 0x7FFFF), c.Bytes.Length).SequenceEqual(c.Bytes));

    public static StartupResult Run(FlashedImage image, StartupRunOptions? options = null)
    {
        options ??= new StartupRunOptions();
        var machineOptions = options.Machine ?? new MachineOptions();
        var machine = Machine.Create(image, machineOptions);
        machine.PowerOn();

        var recognised = Recognises(image.Image);
        ulong? bootServiced = null, handover = null, interrupts = null, eepromLoaded = null, mainLoop = null, powerOff = null;
        if (recognised)
        {
            machine.OnInstruction(BootServiced, m => bootServiced ??= m.Cycles);
            machine.OnInstruction(Handover, m => handover ??= m.Cycles);
            machine.OnInstruction(InterruptsEnabled, m => interrupts ??= m.Cycles);
            machine.OnInstruction(EepromLoaded, m => eepromLoaded ??= m.Cycles);
            machine.OnInstruction(MainLoop, m => mainLoop ??= m.Cycles);
        }

        var services = 0;
        ulong? lastService = null;
        ulong longestGap = 0;
        machine.WatchdogServiced += address =>
        {
            if (!recognised || address != MainLoopService)
                return;
            var now = machine.Cycles;
            if (lastService is { } previous)
                longestGap = Math.Max(longestGap, now - previous);
            lastService = now;
            services++;
        };
        machine.Board.Latch.Changed += (before, after) =>
        {
            if (handover != null && powerOff == null && (before & 0x20) != 0 && (after & 0x20) == 0)
                powerOff = machine.Cycles;
        };
        ulong? keyOff = null;
        if (options.KeyOffAtSeconds is { } keyOffSeconds)
        {
            machine.Scheduler.Schedule((ulong)Math.Round(keyOffSeconds * machineOptions.CpuHz), "key off", () =>
            {
                keyOff = machine.Cycles;
                machine.Board.SetInputs(machine.Board.Inputs.WithIgnition(false));
            });
        }

        var limit = (ulong)Math.Round(options.Seconds * machineOptions.CpuHz);
        machine.Run(limit, _ => powerOff != null);

        var end = machine.Cycles;
        var outcome = machine.Events.CountOf<TrapEvent>() > 0 ? StartupOutcome.Trapped
            : machine.WatchdogResets + machine.SoftwareResets > 0 ? StartupOutcome.ResetLoop
            : powerOff != null ? StartupOutcome.PoweredOff
            : lastService is { } last && end - last <= SchedulerWindow ? StartupOutcome.SchedulerRunning
            : handover != null ? StartupOutcome.Stalled
            : machine.Watchdog.ServiceCount > 0 ? StartupOutcome.StayedInBoot
            : StartupOutcome.NoProgress;

        var assumptions = MachineAssumptions.For(machineOptions).ToList();
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
            new StartupMilestones(bootServiced, handover, interrupts, eepromLoaded, mainLoop, keyOff, powerOff),
            new SchedulerHealth(services, longestGap, XramWord(machine, 0xE2A6)),
            ReadFirmwareState(machine),
            new MonitorSummary(monitor.Exchanges, monitor.WrongAnswers, monitor.UnexpectedAnswers, monitor.ErrorCounter),
            machine.Can.Transmitted,
            new EepromSummary(source, eeprom.WordsRead, eeprom.WordsWritten, eeprom.ToImage()),
            machine.WatchdogResets, machine.SoftwareResets,
            machine.Events.All.ToList(), new Dictionary<Type, long>(machine.Events.Counts), machine.Events.Dropped,
            machine.Inventory.Records, machine.Branches.Last(), Stubs,
            end, machine.Instructions, machine.Entries, machine.StateHash(), assumptions);
    }

    /// <summary>IRAM through the bus; XRAM straight from its array, so the values are real even while XPEN is clear.</summary>
    static FirmwareState ReadFirmwareState(Machine m)
    {
        ushort Iram(uint a) => m.Bus.Peek16(a);
        return new FirmwareState(
            Iram(0xF8F4), Iram(0xFD26), Iram(0xFD7E), Iram(0xFD0C), Iram(0xFD46),
            Xram(m, 0xE027), Xram(m, 0xE7E9), Xram(m, 0xE7EA), XramWord(m, 0xE7FC), (byte)Iram(0xF900),
            ExternalWord(m, 0x883C), Xram(m, 0xE17A), Iram(0xFD14));
    }

    static byte Xram(Machine m, uint a) => m.Bus.Xram[a - 0xE000];

    static ushort XramWord(Machine m, uint a) => (ushort)(Xram(m, a) | (Xram(m, a + 1) << 8));

    /// <summary>A word of the external RAM, 8000h-9FFFh through DPP2.</summary>
    static ushort ExternalWord(Machine m, uint a) =>
        (ushort)(m.Bus.ExternalRam[a - 0x8000] | (m.Bus.ExternalRam[a - 0x8000 + 1] << 8));
}
