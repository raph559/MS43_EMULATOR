using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

public class ExtendTests
{
    [Fact] public void Extp_OverridesDpp_ForTheRangeOnly()
    {
        var t = TestMachine.Create("D7500300 F2F10038 F2F20038 F2F30038").Poke16(0xF800, 0x1234);
        t.Step(4);                                                   // EXTP #3,#2 then three loads of 3800h
        Assert.Equal((0x1234, 0x1234, 0xFFFF), (t.R(1), t.R(2), t.R(3)));
    }

    [Fact] public void ExtsRegisterForm_ReachesSegment0_LikeTheBootIdCheck()
    {
        var img = (0x3FE8, "A5");
        var t = TestMachine.Create("E6001C00 DC07 A986", img).SetR(7, 0).SetR(6, 0x3FE8);
        t.Step(3);                                                   // DPP0:=1Ch; EXTS R7,#1; MOVB RL4,[R6]
        Assert.Equal(0xA5, t.R(4) & 0xFF);
    }

    [Fact] public void ExtpRegisterForm_UsesRegisterAsPage()
    {
        var t = TestMachine.Create("DC4D A812").SetR(13, 0x0003).SetR(2, 0x3800).Poke16(0xF800, 0x7777);
        t.Step(2);                                                   // EXTP R13,#1 ; MOV R1,[R2]
        Assert.Equal(0x7777, t.R(1));
    }

    [Fact] public void Extr_RedirectsShortRegToEsfr()
    {
        var t = TestMachine.Create("D180 E6120304");                 // EXTR #1 ; MOV (reg 12h → F024h),#0403h
        t.Step(2);
        Assert.Equal(0x0403, t.Sfr(0xF024));
    }

    [Fact] public void StackOverflowInsideAnExtpRange_WaitsForTheRangeToEnd()
    {
        // ISM: ATOMIC/EXT* lock class A traps for the sequence. STKOV := FC00h ;
        // EXTP #3,#2 ; PUSH R1 (overflows) ; MOV R2,3800h (still page 3) ; then the trap.
        var t = TestMachine.Create("D7500300 ECF1 F2F20038").WriteSfr(0xFE14, 0xFC00).Poke16(0xF800, 0x1234);
        t.Step(3);
        Assert.Equal(0x1234, t.R(2));
        Assert.Empty(t.M.Events.All.OfType<Ms43Sim.Core.TrapEvent>());
        t.Step();
        var e = Assert.Single(t.M.Events.All.OfType<Ms43Sim.Core.TrapEvent>());
        Assert.Equal((Ms43Sim.Core.TrapKind.StackOverflow, 0x0010), (e.Kind, t.Cpu.IP));
    }

    [Fact] public void Extr_RedirectsBitoffToEsfr()
    {
        var t = TestMachine.Create("D180 0F80");                     // EXTR #1 ; BSET (bitoff 80h → F100h).0
        t.Step(2);
        Assert.Equal(0x0001, t.Sfr(0xF100));
    }
}
