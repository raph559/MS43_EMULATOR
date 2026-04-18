using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Peripherals;

public class AdcTests
{
    [Fact] public void SingleConversion_Takes194Cycles_AndWritesTheChannelAndCode()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFA0, 0x0081);
        Assert.Equal(0x0100, t.Sfr(0xFFA0) & 0x0100);
        t.M.Scheduler.Advance(193); Assert.Equal(0, t.Sfr(0xFF98) & 0x80);
        t.M.Scheduler.Advance(194);
        Assert.Equal((0x1000 | 496, 0x80, 0x0001), (t.Sfr(0xFEA0), t.Sfr(0xFF98) & 0x80, t.Sfr(0xFFA0)));
    }

    [Fact] public void WritingAdstAgainWhileItIsSet_DoesNotRestart()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFA0, 0x0081);
        t.M.Scheduler.Advance(100); t.WriteSfr(0xFFA0, 0x0085);             // ADST still 1: no 0 → 1
        t.M.Scheduler.Advance(194); Assert.Equal(0x1000, t.Sfr(0xFEA0) & 0xF000);
    }

    [Fact] public void ResultOverwritingAnUnreadAddat_SetsAdeir()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFA0, 0x0081);
        t.M.Scheduler.Advance(194); t.WriteSfr(0xFFA0, 0x0085);
        t.M.Scheduler.Advance(388); Assert.Equal(0x80, t.Sfr(0xFF9A) & 0x80);
    }

    [Fact] public void Channel13_FollowsTheMultiplexer()
    {
        var t = TestMachine.Create("0DFF");
        t.M.Board.Latch.Write16(0x080000, 0x7B);                           // mux position 3: coolant 3.87 V
        t.WriteSfr(0xFFA0, 0x008D); t.M.Scheduler.Advance(194);
        Assert.Equal(0xD000 | 793, t.Sfr(0xFEA0));
    }

    [Fact] public void AutoScanMode_IsReported()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFA0, 0x00A3);
        Assert.Contains(t.M.Events.All.OfType<UnsupportedEvent>(), e => e.What.Contains("ADC"));
    }

    [Fact] public void AdstZeroThenOne_DuringAConversion_AbortsAndRestarts()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFA0, 0x0081);
        t.M.Scheduler.Advance(100);
        t.WriteSfr(0xFFA0, 0x0005).WriteSfr(0xFFA0, 0x0085);                // ADST 1 → 0 → 1: channel 5 from now
        t.M.Scheduler.Advance(194); Assert.Equal(0, t.Sfr(0xFF98) & 0x80);
        t.M.Scheduler.Advance(294); Assert.Equal(0x5000 | 496, t.Sfr(0xFEA0));
    }

    [Fact] public void ClearingAdst_LetsTheRunningConversionFinish()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFA0, 0x0081);
        t.M.Scheduler.Advance(100); t.WriteSfr(0xFFA0, 0x0001);
        Assert.Equal(0x0100, t.Sfr(0xFFA0) & 0x0100);
        t.M.Scheduler.Advance(194); Assert.Equal((0x1000 | 496, 0L), (t.Sfr(0xFEA0), t.M.Adc.Conversions - 1));
    }

    [Fact] public void ConversionTime_FollowsAdctcAndAdstc()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFA0, 0xB081);        // ADCTC = 10 (tBC 16), ADSTC = 11 (tS = 64 tBC)
        t.M.Scheduler.Advance(16 * 64 + 40 * 16 + 1); Assert.Equal(0, t.Sfr(0xFF98) & 0x80);
        t.M.Scheduler.Advance(16 * 64 + 40 * 16 + 2); Assert.Equal(0x80, t.Sfr(0xFF98) & 0x80);
    }
}
