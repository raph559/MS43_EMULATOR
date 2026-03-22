using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Peripherals;

public class CapcomTimerTests
{
    [Fact] public void T1_CountsAtFcpuOver64_375TicksPerMs()
    {
        var t = TestMachine.Create("E6A80043 0DFF");                 // T01CON := 4300h
        t.Step();
        t.M.Run(24_000);
        Assert.InRange(t.M.Capcom.T1, (ushort)374, (ushort)376);
    }

    [Fact] public void T1_Overflow_ReloadsAndSetsIr()
    {
        var t = TestMachine.Create("E62B3412 E629F6FF E6A80043 0DFF");   // T1REL(reg 2Bh):=1234h ; T1(reg 29h):=FFF6h ; start
        t.Step(3); t.M.Run(64 * 12);
        Assert.InRange(t.M.Capcom.T1, (ushort)0x1234, (ushort)0x1236);
        Assert.Equal(0x0080, t.Sfr(0xFF9E) & 0x0080);
    }

    [Fact] public void Idle_WakesOnTimerInterruptRequest()
    {
        var t = TestMachine.Create("E6CF4400 E629F0FF E6A80043 87788787 CC00");  // T1IC IE|lvl1 ; T1:=FFF0h ; start ; IDLE
        t.Step(4);
        Assert.Equal(CpuMode.Idle, t.Cpu.Mode);
        t.M.Run(64 * 20);
        Assert.Equal(CpuMode.Running, t.Cpu.Mode);
    }
}
