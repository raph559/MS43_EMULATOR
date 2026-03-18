using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>
/// A machine on an all-FFh flash image with test code at file offset 0x1000. After power-on
/// the CPU is placed at 00:1000 directly (the reset vector is not used).
/// </summary>
public sealed class TestMachine
{
    public const int CodeOffset = 0x1000;

    TestMachine(Core.Machine machine) => M = machine;

    public static TestMachine Create(string codeHex, params (int Offset, string Hex)[] extra)
    {
        var image = Enumerable.Repeat((byte)0xFF, FirmwareImage.Size).ToArray();
        Hex.Bytes(codeHex).CopyTo(image, CodeOffset);
        foreach (var (offset, hex) in extra)
            Hex.Bytes(hex).CopyTo(image, offset);
        var machine = Core.Machine.Create(FlashImageBuilder.Raw(FirmwareImage.FromBytes(image)));
        machine.PowerOn();
        machine.Cpu.Jump(0x00, CodeOffset);
        return new TestMachine(machine);
    }

    public Core.Machine M { get; }

    public C167Cpu Cpu => M.Cpu;

    /// <summary>Runs <paramref name="count"/> CPU steps (see the plan's "What one Step() does").</summary>
    public TestMachine Step(int count = 1)
    {
        for (var i = 0; i < count; i++)
            M.StepOnce();
        return this;
    }

    public ushort R(int n) => Cpu.R(n);

    public TestMachine SetR(int n, ushort value)
    {
        Cpu.SetR(n, value);
        return this;
    }

    public ushort Psw
    {
        get => Cpu.Psw;
        set => Cpu.Psw = value;
    }

    public ushort Peek16(uint address) => M.Bus.Peek16(address);

    public TestMachine Poke16(uint address, ushort value)
    {
        M.Bus.Poke16(address, value);
        return this;
    }

    /// <summary>Writes an SFR through the normal path, hooks included.</summary>
    public TestMachine WriteSfr(uint address, ushort value)
    {
        M.Sfrs.Write16(address, value);
        return this;
    }

    /// <summary>The live value of an SFR, without side effects.</summary>
    public ushort Sfr(uint address) => M.Sfrs.Peek(address);
}
