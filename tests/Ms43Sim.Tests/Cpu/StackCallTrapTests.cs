using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

public class StackCallTrapTests
{
    [Fact] public void PushPop_SetFlags_AndMoveSp()
    {
        var t = TestMachine.Create("ECF1 FCF2").SetR(1, 0x8000);
        t.Step(); Assert.Equal((0xFBFE, 0x8000, 0x0011), (t.Cpu.Sp, t.Peek16(0xFBFE), t.Psw));
        t.Step(); Assert.Equal((0xFC00, 0x8000), (t.Cpu.Sp, t.R(2)));
    }

    [Fact] public void Calls_PushesCspThenIp_RetsRestores()
    {
        var t = TestMachine.Create("DA010020", (0x12000, "DB00"));
        t.Step();
        Assert.Equal((0x01, 0x2000, 0xFBFC), (t.Cpu.CSP, t.Cpu.IP, t.Cpu.Sp));
        Assert.Equal((0x1004, 0x0000), (t.Peek16(0xFBFC), t.Peek16(0xFBFE)));
        t.Step();
        Assert.Equal((0x00, 0x1004, 0xFC00), (t.Cpu.CSP, t.Cpu.IP, t.Cpu.Sp));
    }

    [Fact] public void Pcall_PushesRegThenIp_RetpRestores()
    {
        var t = TestMachine.Create("E2F10020", (0x2000, "EBF1")).SetR(1, 0x4242);
        t.Step(); t.SetR(1, 0); t.Step();
        Assert.Equal((0x1004, 0x4242), (t.Cpu.IP, t.R(1)));
    }

    [Fact] public void TrapInstruction_KeepsIlvl_PushesPswCspIp()
    {
        var t = TestMachine.Create("9B42", (0x84, "FB88"));
        t.Psw = 0x5003; t.Step();
        Assert.Equal((0x00, 0x0084, 0x5003), (t.Cpu.CSP, t.Cpu.IP, t.Psw));
        Assert.Equal((0x1002, 0x0000, 0x5003), (t.Peek16(0xFBFA), t.Peek16(0xFBFC), t.Peek16(0xFBFE)));
        t.Step();
        Assert.Equal((0x1002, 0xFC00), (t.Cpu.IP, t.Cpu.Sp));
    }

    [Fact] public void JumpToOddAddress_RaisesIllina()
    {
        var t = TestMachine.Create("EA000310");                       // JMPA cc_UC,1003h
        t.Step();
        var e = Assert.IsType<TrapEvent>(Assert.Single(t.M.Events.All));
        Assert.Equal((TrapKind.IllegalInstructionAccess, 0x1003u, 0x0002), (e.Kind, e.PushedIp, t.Sfr(0xFFAC)));
        Assert.Equal((0x0028, 0xF000), (t.Cpu.IP, t.Psw & 0xF000));
    }

    [Fact] public void OddWordWrite_TrapsWithoutWriting()
    {
        var t = TestMachine.Create("B812").SetR(1, 0xBEEF).SetR(2, 0xF801).Poke16(0xF800, 0x0000).Poke16(0xF802, 0x0000);
        t.Step();
        var e = Assert.IsType<TrapEvent>(Assert.Single(t.M.Events.All));
        Assert.Equal((TrapKind.IllegalOperandAccess, 0x1002u), (e.Kind, e.PushedIp));
        Assert.Equal((0, 0), (t.Peek16(0xF800), t.Peek16(0xF802)));
    }

    [Fact] public void UndefinedOpcode_PushesItsOwnAddress() =>
        Assert.Equal((TrapKind.UndefinedOpcode, 0x1000u),
            TestMachine.Create("8B00").Step().M.Events.All.OfType<TrapEvent>().Select(e => (e.Kind, e.PushedIp)).Single());

    [Fact] public void MalformedSrvwdt_IsProtectionFault() =>
        Assert.Equal((TrapKind.ProtectionFault, 0x1000u),
            TestMachine.Create("A758A7A6").Step().M.Events.All.OfType<TrapEvent>().Select(e => (e.Kind, e.PushedIp)).Single());

    [Fact] public void StackOverflow_AfterPushBelowStkov()
    {
        var t = TestMachine.Create("ECF1").WriteSfr(0xFE14, 0xFC00);  // STKOV := FC00h
        t.Step();
        var e = Assert.IsType<TrapEvent>(Assert.Single(t.M.Events.All));
        Assert.Equal((TrapKind.StackOverflow, 0x0010, 0x4000), (e.Kind, t.Cpu.IP, t.Sfr(0xFFAC)));
    }

    [Fact] public void Retrap_IfFlagStillSetAfterReti()
    {
        var t = TestMachine.Create("8B00", (0x28, "FB88"));           // class B handler: RETI without clearing TFR
        t.Step(); t.Step(); t.Step();
        Assert.Equal(2, t.M.Events.All.OfType<TrapEvent>().Count());
    }
}
