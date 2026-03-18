using System.Security.Cryptography;

namespace Ms43Sim.Core;

/// <summary>
/// One emulated MS43: the bus, the SFR file, the scheduler and the diagnostics. The CPU,
/// interrupt controller and peripherals are added by later parts of plan 1.
/// </summary>
public sealed partial class Machine
{
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
        Inventory.Context = () => (Cycles, CurrentAddress);
        Events.Context = () => (Cycles, CurrentAddress);
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

    public ulong Cycles => Scheduler.Now;
    public ulong Instructions { get; private set; }

    /// <summary>The CPU address (CSP:IP) of the instruction being executed; 0 before the CPU exists.</summary>
    uint CurrentAddress => 0;

    /// <summary>Power-on: RAM powers up as 00h (for determinism), every SFR takes its reset value.</summary>
    public void PowerOn()
    {
        Array.Clear(Bus.Iram);
        Array.Clear(Bus.Xram);
        Array.Clear(Bus.ExternalRam);
        Array.Clear(Bus.CanStub);
        Sfrs.ResetAll();
        Bus.ChipSelects.Recompute();
        Bus.XperipheralsEnabled = false;
    }

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
