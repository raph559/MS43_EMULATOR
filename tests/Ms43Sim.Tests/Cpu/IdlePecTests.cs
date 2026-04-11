using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

public class IdlePecTests
{
    // 00:1000 IDLE, 00:1004 NOP, 00:1006 NOP. PEC channel 0 moves the word at F900h to F902h.
    // ADCIC = 78h: IE, ILVL 14, GLVL 0. Vector A0h (ADC): RETI.
    static TestMachine Idle(ushort pecc, ushort psw)
    {
        var t = TestMachine.Create("87788787 CC00 CC00", (0xA0, "FB88"))
            .WriteSfr(0xFEC0, pecc).Poke16(0xFCE0, 0xF900).Poke16(0xFCE2, 0xF902)
            .Poke16(0xF900, 0xBEEF).WriteSfr(0xFF98, 0x0078);
        t.Psw = psw;
        t.Step();
        Assert.Equal(CpuMode.Idle, t.Cpu.Mode);
        t.M.Interrupts.Request(InterruptSources.ByName("ADC"));
        return t;
    }

    [Fact] public void PecRequest_IsTransferred_AndTheCpuStaysIdle()
    {
        var t = Idle(0x00FF, 0x0800).Step();
        Assert.Equal((0xBEEF, CpuMode.Idle, 0x1004, 0), (t.Peek16(0xF902), t.Cpu.Mode, t.Cpu.IP, t.Sfr(0xFF98) & 0x80));
        Assert.Equal((1UL, 1UL), (t.M.Instructions, t.M.Entries));
    }

    [Fact] public void PecRequest_WithIenClear_EndsIdle_WithoutATransfer()
    {
        var t = Idle(0x00FF, 0x0000).Step();
        Assert.Equal((0x0000, CpuMode.Running, 0x1006, 0x80), (t.Peek16(0xF902), t.Cpu.Mode, t.Cpu.IP, t.Sfr(0xFF98) & 0x80));
    }

    [Fact] public void PecCountReachingZero_LeavesTheRequest_WhichThenEndsIdle()
    {
        var t = Idle(0x0001, 0x0800).Step();
        Assert.Equal((0xBEEF, CpuMode.Idle), (t.Peek16(0xF902), t.Cpu.Mode));
        t.Step();
        Assert.Equal((CpuMode.Running, 0x00A0), (t.Cpu.Mode, t.Cpu.IP));
    }

    [Fact] public void PecWordTransfer_FromAnOddAddress_EndsIdle_InTheIllopaTrap()
    {
        var t = Idle(0x00FF, 0x0800).Poke16(0xFCE0, 0xF901).Step();
        Assert.Equal((CpuMode.Running, 0x0028, 0x0004), (t.Cpu.Mode, t.Cpu.IP, t.Sfr(0xFFAC) & 0x0004));
    }

    [Fact] public void InterruptEntry_IsAnEntry_NotAnInstruction()
    {
        var t = TestMachine.Create("CC00 CC00", (0x84, "FB88")).WriteSfr(0xFF9E, 0x00CC);
        t.Psw = 0x0800;
        t.Step();
        Assert.Equal((0UL, 1UL), (t.M.Instructions, t.M.Entries));
    }

    [Fact] public void FaultingInstruction_WhoseTrapIsEntered_IsAnEntry()
    {
        var t = TestMachine.Create("8B00").Step();                       // undefined opcode: UNDOPC
        Assert.Equal((0x0028, 0UL, 1UL), (t.Cpu.IP, t.M.Instructions, t.M.Entries));
    }
}
