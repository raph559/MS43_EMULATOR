using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Peripherals;

public class WatchdogTests
{
    [Fact] public void PowerOn_OverflowsAfter131072Cycles()
    {
        var t = TestMachine.Create("0DFF");                          // JMPR cc_UC,$ (spin)
        t.M.Run(140_000);
        var r = Assert.IsType<ResetEvent>(t.M.Events.All.Single(e => e is ResetEvent re && re.Kind == ResetKind.Watchdog));
        Assert.InRange(r.Cycle, 131_072UL, 131_072UL + 64);
        Assert.Equal(0x0002, t.Sfr(0xFFAE) & 0x0002);               // WDTR
    }

    [Fact] public void Srvwdt_Reloads_And_ClearsWdtr()
    {
        var t = TestMachine.Create("E6D70192 A758A7A7 0DFF");        // WDTCON:=9201h ; SRVWDT ; spin
        t.M.Run(3_604_480 - 200);
        Assert.Equal(0, t.M.WatchdogResets);
        t.M.Run(400);
        Assert.Equal(1, t.M.WatchdogResets);
    }

    [Fact] public void Diswdt_OnlyBeforeEinitOrSrvwdt()
    {
        var t = TestMachine.Create("A55AA5A5 0DFF"); t.M.Run(300_000);
        Assert.Equal(0, t.M.WatchdogResets);
        t = TestMachine.Create("B54AB5B5 A55AA5A5 0DFF"); t.M.Run(300_000);
        Assert.True(t.M.WatchdogResets >= 1);
    }

    [Fact] public void Reset_KeepsRam_RestoresSfrs_RestartsAtZero()
    {
        var t = TestMachine.Create("E60A00F8 0DFF", (0x0000, "0DFF")).Poke16(0xF900, 0x5A5A);  // STKOV:=F800h ; spin
        t.M.Run(140_000);                                            // watchdog reset at 131,072
        Assert.Equal((0x5A5A, 0xFA00, 0x0000), (t.Peek16(0xF900), t.Sfr(0xFE14), t.Cpu.IP));
    }

    [Fact] public void Srst_IsASoftwareReset()
    {
        var t = TestMachine.Create("B748B7B7", (0x0000, "0DFF"));
        t.Step(2);
        Assert.Equal((1, 0x0004), (t.M.SoftwareResets, t.Sfr(0xFFAE) & 0x0004));
    }
}
