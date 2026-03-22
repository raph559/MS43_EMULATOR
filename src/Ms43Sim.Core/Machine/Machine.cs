using System.Security.Cryptography;

namespace Ms43Sim.Core;

public enum StopReason { CycleBudget, StopCondition, PowerDown }

/// <summary>
/// One emulated MS43: the bus, the SFR file, the CPU, the scheduler and the diagnostics.
/// The interrupt controller and peripherals are added by later parts of plan 1.
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

    public ulong Cycles => Scheduler.Now;
    public ulong Instructions { get; private set; }

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
    }

    /// <summary>Calls <paramref name="hook"/> each time the CPU is about to execute at <paramref name="cpuAddress"/>.</summary>
    public void OnInstruction(uint cpuAddress, Action<Machine> hook) =>
        instructionHooks[cpuAddress] = instructionHooks.TryGetValue(cpuAddress, out var existing) ? existing + hook : hook;

    /// <summary>One CPU step (see the plan's "What one Step() does"), then the clock advances by its states.</summary>
    public void StepOnce()
    {
        if (instructionHooks.Count > 0 && instructionHooks.TryGetValue(Cpu.Address, out var hook))
            hook(this);
        var states = Cpu.Step();
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
            StepOnce();
        }
        return StopReason.CycleBudget;
    }

    // ICpuHost: plan 1 Task 13 gives these their watchdog and reset behaviour.
    void ICpuHost.Srvwdt() { }

    bool ICpuHost.Diswdt() => true;

    void ICpuHost.Einit() { }

    void ICpuHost.SoftwareReset() { }

    void ICpuHost.PowerDown() { }

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
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    static void AppendWord(IncrementalHash sha, Span<byte> buffer, ushort value)
    {
        buffer[0] = (byte)value;
        buffer[1] = (byte)(value >> 8);
        sha.AppendData(buffer);
    }
}
