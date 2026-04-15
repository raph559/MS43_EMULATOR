using System.Security.Cryptography;

namespace Ms43Sim.Core;

public enum StopReason { CycleBudget, StopCondition, PowerDown }

/// <summary>
/// One emulated MS43: the bus, the SFR file, the CPU with its interrupt controller, the
/// watchdog, ports, SSC, GPT and CAPCOM, the board, the scheduler and the diagnostics.
/// </summary>
public sealed partial class Machine : ICpuHost
{
    readonly Dictionary<uint, Action<Machine>> instructionHooks = new();

    Machine(FlashedImage image, MachineOptions options)
    {
        Image = image;
        Options = options;
        Scheduler = new Scheduler();
        Inventory = new AccessInventory();
        Events = new EventLog();
        Branches = new BranchTrace(options.BranchTraceDepth);
        Sfrs = new SfrFile(Inventory);
        Sfrs.ResetAll();
        Bus = new PhysicalBus(image.Image, options.ExternalRamBytes, Sfrs, Inventory, Events, options.UnmodelledReadValue);
        Cpu = new C167Cpu(Bus, Sfrs, Events, Branches, this, () => Cycles);
        Interrupts = new InterruptController(Sfrs, Bus);
        Cpu.Interrupts = Interrupts;
        Watchdog = new Watchdog(Scheduler, Sfrs, Reset, () => Cpu.InitDone);
        Board = new Board(options.Inputs, Events);
        Ports = new Ports(Sfrs, Board);
        Board.DigitalInputChanged += Ports.InputChanged;
        Ssc = new Ssc(Scheduler, Sfrs, Interrupts, Events, Board.Spi);
        Board.AttachDevices(Scheduler, Ssc, Events, options.EepromImage);
        Gpt = new Gpt(Scheduler, Sfrs, Interrupts, Ports, Events);
        Capcom = new Capcom(Scheduler, Sfrs, Interrupts, Ports, Gpt, Events);
        Bus.AttachDevice(3, Board.Latch);
        Inventory.Context = () => (Cycles, Cpu.InstructionAddress);
        Events.Context = () => (Cycles, Cpu.InstructionAddress);
    }

    public static Machine Create(FlashedImage image, MachineOptions? options = null) =>
        new(image, options ?? new MachineOptions());

    public MachineOptions Options { get; }
    public FlashedImage Image { get; }
    public PhysicalBus Bus { get; }
    public SfrFile Sfrs { get; }
    public Scheduler Scheduler { get; }
    public EventLog Events { get; }
    public AccessInventory Inventory { get; }
    public BranchTrace Branches { get; }
    public C167Cpu Cpu { get; }
    public InterruptController Interrupts { get; }
    public Watchdog Watchdog { get; }
    public Capcom Capcom { get; }
    public Board Board { get; }
    public Ports Ports { get; }
    public Ssc Ssc { get; }
    public Gpt Gpt { get; }

    public int WatchdogResets { get; private set; }
    public int SoftwareResets { get; private set; }

    public ulong Cycles => Scheduler.Now;

    /// <summary>Instructions executed (a faulting one whose trap is entered counts as an entry).</summary>
    public ulong Instructions { get; private set; }

    /// <summary>Trap and interrupt entries and PEC transfers: CPU steps that executed no instruction.</summary>
    public ulong Entries { get; private set; }

    /// <summary>Power-on: RAM powers up as 00h (for determinism), every SFR takes its reset value.</summary>
    public void PowerOn()
    {
        Array.Clear(Bus.Iram);
        Array.Clear(Bus.Xram);
        Array.Clear(Bus.ExternalRam);
        Array.Clear(Bus.CanStub);
        Sfrs.ResetAll();
        Bus.ChipSelects.Recompute();
        Cpu.Reset();
        Interrupts.Reset();
        Watchdog.OnReset(ResetKind.PowerOn);
        Capcom.OnReset();
        Ports.OnReset();
        Ssc.OnReset();
        Gpt.OnReset();
        Board.PowerOn();
    }

    /// <summary>
    /// A watchdog or software reset: every SFR and the CPU return to their reset values, RAM is
    /// kept, and the CPU restarts at 00:0000 (UM §5, "Reset Functions").
    /// </summary>
    public void Reset(ResetKind kind)
    {
        Events.Add((c, pc) => new ResetEvent(c, pc, kind));
        Branches.Add(new BranchRecord(Cycles, Cpu.InstructionAddress, 0, BranchKind.Reset));
        if (kind == ResetKind.Watchdog)
            WatchdogResets++;
        else if (kind == ResetKind.Software)
            SoftwareResets++;
        Sfrs.ResetAll();
        Bus.ChipSelects.Recompute();
        Cpu.Reset();
        Interrupts.Reset();
        Watchdog.OnReset(kind);
        Capcom.OnReset();
        Ports.OnReset();
        Ssc.OnReset();
        Gpt.OnReset();
        Board.OnCpuReset();
    }

    /// <summary>Calls <paramref name="hook"/> each time the CPU is about to execute at <paramref name="cpuAddress"/>.</summary>
    public void OnInstruction(uint cpuAddress, Action<Machine> hook) =>
        instructionHooks[cpuAddress] = instructionHooks.TryGetValue(cpuAddress, out var existing) ? existing + hook : hook;

    /// <summary>
    /// One CPU step (see the plan's "What one Step() does"), then the clock advances by its states.
    /// In IDLE an enabled request is first offered as a PEC transfer, which leaves the CPU idle
    /// (UM §20.1); any other request wakes it.
    /// </summary>
    public void StepOnce()
    {
        if (Cpu.Mode == CpuMode.Idle)
        {
            if (!Interrupts.AnyEnabledRequest)
            {
                // IDLE: nothing runs until the next timed event (or an enabled request) wakes the CPU.
                var next = Scheduler.NextDue;
                Scheduler.Advance(next == ulong.MaxValue ? Cycles + 2 : next);
                return;
            }
            if (Cpu.TryServePecWhileIdle(out var pecStates))
            {
                Entries++;
                Scheduler.Advance(Cycles + (ulong)pecStates);
                return;
            }
            Cpu.Wake();
        }
        if (instructionHooks.Count > 0 && instructionHooks.TryGetValue(Cpu.Address, out var hook))
            hook(this);
        var running = Cpu.Mode == CpuMode.Running;
        var states = Cpu.Step();
        if (Cpu.LastStepWasEntry)
            Entries++;
        else if (running)
            Instructions++;
        Scheduler.Advance(Cycles + (ulong)states);
    }

    /// <summary>Runs until <paramref name="cycleBudget"/> more cycles have passed, the stop condition holds, or PWRDN.</summary>
    public StopReason Run(ulong cycleBudget, Func<Machine, bool>? stopWhen = null)
    {
        var end = Cycles + cycleBudget;
        while (Cycles < end)
        {
            if (Cpu.Mode == CpuMode.PowerDown)
                return StopReason.PowerDown;
            if (stopWhen != null && stopWhen(this))
                return StopReason.StopCondition;
            if (Cpu.Mode == CpuMode.Idle && !Interrupts.AnyEnabledRequest)
            {
                Scheduler.Advance(Math.Min(Scheduler.NextDue, end));
                continue;
            }
            StepOnce();
        }
        return StopReason.CycleBudget;
    }

    void ICpuHost.Srvwdt() => Watchdog.Service();

    bool ICpuHost.Diswdt() => Watchdog.Disable();

    void ICpuHost.Einit() => Watchdog.OnEinit();

    void ICpuHost.SoftwareReset() => Reset(ResetKind.Software);

    void ICpuHost.PowerDown() => Events.Add((c, pc) => new UnsupportedEvent(c, pc, "PWRDN: the run stops here"));

    /// <summary>A hash of everything a run can change; equal hashes mean equal machine states.</summary>
    public string StateHash()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Bus.Iram);
        sha.AppendData(Bus.Xram);
        sha.AppendData(Bus.ExternalRam);
        sha.AppendData(Bus.CanStub);
        Span<byte> word = stackalloc byte[2];
        for (uint a = 0xF000; a < 0xF200; a += 2)
            AppendWord(sha, word, Sfrs.Peek(a));
        for (uint a = 0xFE00; a < 0x10000; a += 2)
            AppendWord(sha, word, Sfrs.Peek(a));
        sha.AppendData(BitConverter.GetBytes(Cpu.Address));
        sha.AppendData(BitConverter.GetBytes(Cycles));
        sha.AppendData(BitConverter.GetBytes(Instructions));
        sha.AppendData(BitConverter.GetBytes(Entries));
        sha.AppendData(Board.Eeprom.ToImage());
        sha.AppendData([Board.Latch.Value]);
        var monitor = Board.Monitor;
        foreach (var counter in new[] { monitor.Exchanges, monitor.ExchangesSinceReset, monitor.ErrorCounter, monitor.WrongAnswers, monitor.UnexpectedAnswers })
            sha.AppendData(BitConverter.GetBytes(counter));
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    static void AppendWord(IncrementalHash sha, Span<byte> buffer, ushort value)
    {
        buffer[0] = (byte)value;
        buffer[1] = (byte)(value >> 8);
        sha.AppendData(buffer);
    }
}
