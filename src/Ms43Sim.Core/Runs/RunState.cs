namespace Ms43Sim.Core;

/// <summary>
/// What every run of the car's program shares (plans 2–4): the program checkpoints the car, release,
/// E85CAMPORT and stock files share, the stubbed devices, and the RAM read-outs at the end of a power
/// cycle. RAM is read straight from its arrays, so the values are real even while XPEN is clear.
/// </summary>
internal static class RunState
{
    internal const uint BootServiced = 0x002EFC, Handover = 0x0E366A, InterruptsEnabled = 0x0E36BC;
    internal const uint EepromLoaded = 0x0E3700, MainLoop = 0x0E0094, MainLoopService = 0x0E0144;

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

    /// <summary>The checkpoint bytes match: the program addresses a run watches are valid.</summary>
    internal static bool Recognises(FirmwareImage image) =>
        Checkpoints.All(c => image.Bytes.Slice((int)(c.Cpu & 0x7FFFF), c.Bytes.Length).SequenceEqual(c.Bytes));

    internal static StubInfo[] Stubs(bool canPartners, bool engine, bool drive = false)
    {
        if (drive)
            return DriveStubs();
        var stubs = new List<StubInfo>
        {
            new("EEPROM", "blank or from a file", false),
            new("output-driver chips A/B", "A always answers \"no fault\"; B answers the input set's status word (the car's lambda heaters read open); A's commands are captured, B's are not", true),
            new("monitoring module", "shut-off path not modelled", true),
            drive ? new("CAN partners", "ASC1 153h and the cluster's 613h and 615h with the drive's speed, A/C, ambient and clock; no gearbox, no DSC [owner: ASC]", false)
            : canPartners
                ? new("CAN partners", "ASC1 153h and the cluster's 613h and 615h with fixed healthy contents; no gearbox, no DSC [owner: ASC]", false)
                : new("CAN partners", "silent, every frame acknowledged", false),
        };
        if (engine)
        {
            stubs.Add(new("knock sensor", "the noise law", false));
            stubs.Add(new("coil feedback", "the 1.2 ms burn pulse and 2.7 V coil current", false));
            stubs.Add(drive
                ? new("engine", "the starter and the idle plant at start and idle, the drive's scripted speed off idle, the breathing law's air; no torque model", false)
                : new("engine", "crude idle-air plant, no torque model, throttle air not modelled", false));
        }
        else
            stubs.Add(new("knock chip and coil feedback", "0 V", false));
        if (drive)
            stubs.Add(new("steering wheel", "the \"no button\" frame train while the key is on", false));
        stubs.Add(new("K-line tester", "none", false));
        return stubs.ToArray();
    }

    /// <summary>
    /// A drive's stubs, each on the "not between" side of design §7's line with its reason (plan 4 C1):
    /// the checks read the CPU's own pins upstream of the driver chips, and the rest are inputs or laws
    /// derived from the CPU's outputs.
    /// </summary>
    static StubInfo[] DriveStubs() => new StubInfo[]
    {
        new("EEPROM", "blank or from the previous power cycle's after-run", false,
            "an input the program reads; no checked output passes through it"),
        new("output-driver chips A/B", "A always answers \"no fault\"; B answers the input set's status word (the car's lambda heaters read open)", false,
            "the checks read the CPU's own injector and coil pins (P2.0–P2.11), upstream of the chip; a healthy pass-through chip cannot hide a bin fault the pin checks would miss (design §7 'which stubs a pass may have'); the chips never report an injector or coil driver fault, so the firmware's injector and coil driver-fault reactions are never exercised"),
        new("monitoring module", "the protocol is modelled; its shut-off path is not", false,
            "its missing shut-off path can only make real outputs safer, and the monitoring check fails any answer it would object to"),
        new("CAN partners", "ASC1 153h and the cluster's 613h and 615h with the drive's speed, A/C, ambient and clock; no gearbox, no DSC [owner: ASC]", false,
            "inputs from the car; no checked output passes through them"),
        new("knock sensor", "the noise law", false, "derived from the CPU's own outputs (the shaft's speed)"),
        new("coil feedback", "the 1.2 ms burn pulse and 2.7 V coil current", false, "derived from the CPU's own outputs (its sparks)"),
        new("engine", "the starter and the idle plant at start and idle, the drive's scripted speed off idle, the breathing law's air; no torque model", false,
            "the plant around the ECU: it feeds inputs; the checks judge the CPU's outputs against it"),
        new("steering wheel", "the \"no button\" frame train while the key is on", false, "an input from the car"),
        new("K-line tester", "none", false, "nothing is connected"),
    };

    /// <summary>RAM read-outs (plan 2, Reference facts): IRAM through the bus; XRAM and the external RAM from their arrays.</summary>
    internal static FirmwareState ReadFirmwareState(Machine m)
    {
        ushort Iram(uint a) => m.Bus.Peek16(a);
        return new FirmwareState(
            Iram(0xF8F4), Iram(0xFD26), Iram(0xFD7E), Iram(0xFD0C), Iram(0xFD46),
            Xram(m, 0xE027), Xram(m, 0xE7E9), Xram(m, 0xE7EA), XramWord(m, 0xE7FC), (byte)Iram(0xF900),
            ExternalWord(m, 0x883C), Xram(m, 0xE17A), Iram(0xFD14), Iram(0xFD9C), (sbyte)m.Bus.ExternalRam[0x8101 - 0x8000]);
    }

    internal static byte Xram(Machine m, uint a) => m.Bus.Xram[a - 0xE000];

    internal static ushort XramWord(Machine m, uint a) => (ushort)(Xram(m, a) | (Xram(m, a + 1) << 8));

    /// <summary>A word of the external RAM, 8000h-9FFFh through DPP2.</summary>
    internal static ushort ExternalWord(Machine m, uint a) =>
        (ushort)(m.Bus.ExternalRam[a - 0x8000] | (m.Bus.ExternalRam[a - 0x8000 + 1] << 8));
}

/// <summary>
/// One power cycle's checkpoints (first cycle each was reached), the main loop's SRVWDTs at 0E:0144 and
/// the main relay's drop (latch bit 5 cleared after the handover) — plan 2's start-up watch.
/// </summary>
internal sealed class CycleWatch
{
    readonly Machine machine;

    public CycleWatch(Machine machine)
    {
        this.machine = machine;
        Recognised = RunState.Recognises(machine.Image.Image);
        if (Recognised)
        {
            machine.OnInstruction(RunState.BootServiced, m => BootServiced ??= m.Cycles);
            machine.OnInstruction(RunState.Handover, m => Handover ??= m.Cycles);
            machine.OnInstruction(RunState.InterruptsEnabled, m => InterruptsEnabled ??= m.Cycles);
            machine.OnInstruction(RunState.EepromLoaded, m => EepromLoaded ??= m.Cycles);
            machine.OnInstruction(RunState.MainLoop, m => MainLoop ??= m.Cycles);
        }
        machine.WatchdogServiced += address =>
        {
            if (!Recognised || address != RunState.MainLoopService)
                return;
            var now = machine.Cycles;
            if (LastService is { } previous)
                LongestGap = Math.Max(LongestGap, now - previous);
            LastService = now;
            Services++;
        };
        machine.Board.Latch.Changed += (before, after) =>
        {
            if (Handover != null && PowerOff == null && (before & 0x20) != 0 && (after & 0x20) == 0)
                PowerOff = machine.Cycles;
        };
    }

    public bool Recognised { get; }

    public ulong? BootServiced { get; private set; }

    public ulong? Handover { get; private set; }

    public ulong? InterruptsEnabled { get; private set; }

    public ulong? EepromLoaded { get; private set; }

    public ulong? MainLoop { get; private set; }

    public ulong? KeyOff { get; set; }

    public ulong? PowerOff { get; private set; }

    public int Services { get; private set; }

    public ulong? LastService { get; private set; }

    public ulong LongestGap { get; private set; }

    public StartupMilestones Milestones => new(BootServiced, Handover, InterruptsEnabled, EepromLoaded, MainLoop, KeyOff, PowerOff);

    public SchedulerHealth Health => new(Services, LongestGap, RunState.XramWord(machine, 0xE2A6));
}
