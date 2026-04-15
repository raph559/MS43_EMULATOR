using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Peripherals;

public class CapcomTests
{
    [Fact] public void T8_RunsAtFcpuOver128_AndItsOverflowSetsT8ir()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xF052, 0xFFF0).WriteSfr(0xFF20, 0x4400);
        t.M.Scheduler.Advance(128 * 16);
        Assert.Equal((0x80, 0x0000), (t.Sfr(0xF17C) & 0x80, t.M.Capcom.T8));
    }

    // T1 at fCPU/8 from FFE0h with T1REL = FFE0h; CC12 = FFF0h; P2.12 is an output.
    static TestMachine Cc12(int ccm3) => TestMachine.Create("0DFF")
        .WriteSfr(0xFFC2, 0x1000).WriteSfr(0xFE56, 0xFFE0).WriteSfr(0xFE52, 0xFFE0)
        .WriteSfr(0xFE98, 0xFFF0).WriteSfr(0xFF58, (ushort)ccm3).WriteSfr(0xFF50, 0x4000);

    [Fact] public void CompareMode1_TogglesTheLatch_AtEveryMatch()
    {
        var t = Cc12(0x000D);                                               // ACC12 = T1, mode 1
        t.M.Scheduler.Advance(8 * 16); Assert.Equal((0x1000, 0x80), (t.M.Ports.Latch(2) & 0x1000, t.Sfr(0xFF90) & 0x80));
        t.M.Scheduler.Advance(8 * 48); Assert.Equal(0, t.M.Ports.Latch(2) & 0x1000);
    }

    [Fact] public void CompareMode2_InterruptsOncePerTimerPeriod()
    {
        var t = Cc12(0x000E);
        t.M.Scheduler.Advance(8 * 16); t.WriteSfr(0xFF90, 0x0000).WriteSfr(0xFE98, 0xFFF8);   // later value, same period
        t.M.Scheduler.Advance(8 * 31); Assert.Equal(0, t.Sfr(0xFF90) & 0x80);
        t.M.Scheduler.Advance(8 * 56); Assert.Equal(0x80, t.Sfr(0xFF90) & 0x80);             // FFF8h in the next period
    }

    [Fact] public void CompareMode3_SetsOnTheMatch_AndResetsAtTheOverflow()
    {
        var t = Cc12(0x000F);
        t.M.Scheduler.Advance(8 * 16); Assert.Equal(0x1000, t.M.Ports.Latch(2) & 0x1000);
        t.M.Scheduler.Advance(8 * 32); Assert.Equal(0, t.M.Ports.Latch(2) & 0x1000);
    }

    [Fact] public void CompareValueBehindTheTimer_MatchesOnlyAfterTheReload()
    {
        var t = Cc12(0x000C).WriteSfr(0xFE52, 0xFFF4);                     // mode 0; T1 already past FFF0h
        t.M.Scheduler.Advance(8 * 12); Assert.Equal(0, t.Sfr(0xFF90) & 0x80);
        t.M.Scheduler.Advance(8 * 28); Assert.Equal(0x80, t.Sfr(0xFF90) & 0x80);
    }

    [Fact] public void Capture_FallingEdgeOnP8_6_CopiesT7_AndSetsCc22ir()
    {
        var t = TestMachine.Create("0DFF");
        t.M.Board.SetInputs(t.M.Board.Inputs.WithInput(8, 6, true));
        t.WriteSfr(0xFF24, 0x0200).WriteSfr(0xFF20, 0x0044);              // CC22 capture falling on T7; T7 run fCPU/128
        t.M.Scheduler.Advance(128 * 20);
        t.M.Board.SetInputs(t.M.Board.Inputs.WithInput(8, 6, false));
        Assert.Equal((20, 0x80), (t.Sfr(0xFE6C), t.Sfr(0xF16C) & 0x80));
    }

    [Fact] public void CounterMode_T0CountsT6Underflows()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFF50, 0x0048)        // T0: counter mode, TxI = 000, run
            .WriteSfr(0xFE4A, 0x0001).WriteSfr(0xFE48, 0x0001).WriteSfr(0xFF48, 0x80C5);
        t.M.Scheduler.Advance(128 * 2 * 5);
        Assert.Equal(5, t.M.Capcom.T0);
    }

    [Fact] public void DoubleRegisterMode_IsReported()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFF52, 0x0005).WriteSfr(0xFF56, 0x0004);   // CC0 mode 1, CC8 mode 0
        Assert.Contains(t.M.Events.All.OfType<UnsupportedEvent>(), e => e.What.Contains("double-register"));
    }

    [Fact] public void CounterMode_T7CountsRisingEdgesOnT7in_AndT8StopsOnThatSelection()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFF20, 0x4949);       // T7 and T8: counter, rising edges, run
        foreach (var level in new[] { false, true, false, true })
            t.M.Board.SetInputs(t.M.Board.Inputs.WithInput(2, 15, level));
        Assert.Equal((2, 0), (t.M.Capcom.T7, t.M.Capcom.T8));
    }

    [Fact] public void CounterModeStep_ChecksTheCompareValues()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xF050, 0x0001).WriteSfr(0xFE60, 0x0002)
            .WriteSfr(0xFF22, 0x0004).WriteSfr(0xFF20, 0x0049);            // CC16 mode 0 on T7; T7 counts rising T7IN edges
        t.M.Board.SetInputs(t.M.Board.Inputs.WithInput(2, 15, false));
        t.M.Board.SetInputs(t.M.Board.Inputs.WithInput(2, 15, true));
        Assert.Equal((2, 0x80), (t.M.Capcom.T7, t.Sfr(0xF160) & 0x80));   // CC16IC
    }

    [Fact] public void Capture_FiresOnALatchWrite_WhenThePinIsAnOutput()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFC2, 0x0001)       // P2.0 output
            .WriteSfr(0xFF52, 0x0001).WriteSfr(0xFF50, 0x0044);            // CC0 capture rising on T0; T0 fCPU/128
        t.M.Scheduler.Advance(128 * 7);
        t.WriteSfr(0xFFC0, 0x0001);                                         // the latch rises
        Assert.Equal((7, 0x80), (t.Sfr(0xFE80), t.Sfr(0xFF78) & 0x80));
    }
}
