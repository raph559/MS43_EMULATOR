using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Peripherals;

public class PwmTests
{
    [Fact] public void Pwm2_At400Hz_RaisesPir2AndPwmir_Every60000Cycles()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xF03C, 0xEA5F).WriteSfr(0xF17E, 0x0052).WriteSfr(0xFF30, 0x0404);
        t.M.Scheduler.Advance(59_999); Assert.Equal(0, t.Sfr(0xFF30) & 0x4000);
        t.M.Scheduler.Advance(60_000); Assert.Equal((0x4000, 0x80), (t.Sfr(0xFF30) & 0x4000, t.Sfr(0xF17E) & 0x80));
    }

    [Fact] public void Pwm0_AtFcpuOver64_HasA120000CyclePeriod()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xF038, 0x0752).WriteSfr(0xFF30, 0x0111);   // PTI0, PIE0, PTR0
        t.M.Scheduler.Advance(120_000); Assert.Equal((1L, 0x1000), (t.M.Pwm.Periods(0), t.Sfr(0xFF30) & 0x1000));
    }

    [Fact] public void PulseWidth_TakesEffectAtTheNextPeriod()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xF03E, 0x0063).WriteSfr(0xFE36, 0x0032).WriteSfr(0xFF30, 0x0808);  // PWM3, PP 99, PW 50
        t.M.Scheduler.Advance(60); Assert.True(t.M.Pwm.Output(3));
        t.WriteSfr(0xFE36, 0x0050);                                         // PW 80 written mid-period
        t.M.Scheduler.Advance(70); Assert.True(t.M.Pwm.Output(3));
        t.M.Scheduler.Advance(170); Assert.False(t.M.Pwm.Output(3));
    }

    [Fact] public void PirFlags_StaySetUntilSoftwareClearsThem()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xF03E, 0x0009).WriteSfr(0xFF30, 0x0808);
        t.M.Scheduler.Advance(30); Assert.Equal(0x8000, t.Sfr(0xFF30) & 0x8000);
        t.WriteSfr(0xFF30, 0x0808); Assert.Equal(0, t.Sfr(0xFF30) & 0x8000);
    }

    [Fact] public void CenterAlignedMode_IsReported()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFF32, 0x0010);
        Assert.Contains(t.M.Events.All.OfType<UnsupportedEvent>(), e => e.What.Contains("PWM"));
    }

    [Fact] public void ChannelWithoutPie_CountsPeriodsAndSetsPir_FromTheClock()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xF03A, 0x0009).WriteSfr(0xFF30, 0x0002);   // PWM1: PP 9, run, PIE1 = 0
        t.M.Scheduler.Advance(35);
        Assert.Equal((3L, 5, 0x2000), (t.M.Pwm.Periods(1), (int)t.M.Pwm.Counter(1), t.Sfr(0xFF30) & 0x2000));
        Assert.Equal(0, t.Sfr(0xF17E) & 0x80);                              // no PIE1: no PWMIR
    }

    [Fact] public void Pwmir_NeedsPwmie()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xF03E, 0x0009).WriteSfr(0xFF30, 0x0808);   // PIE3, PWMIC = 0
        t.M.Scheduler.Advance(30);
        Assert.Equal((0x8000, 0), (t.Sfr(0xFF30) & 0x8000, t.Sfr(0xF17E) & 0x80));
    }

    [Fact] public void StoppedTimer_HoldsItsCount_AndAWriteLoadsTheShadowAtOnce()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xF03E, 0x0063).WriteSfr(0xFE36, 0x0032).WriteSfr(0xFF30, 0x0008);
        t.M.Scheduler.Advance(60); t.WriteSfr(0xFF30, 0x0000);              // stop at 60
        t.WriteSfr(0xFE36, 0x0050);                                         // stopped: the shadow loads now
        t.M.Scheduler.Advance(500);
        Assert.Equal((60, false), ((int)t.M.Pwm.Counter(3), t.M.Pwm.Output(3)));
    }

    // PWM2 at fCPU: PP2 = EA5Fh (60,000 counts), PW2 = 3A98h (15,000), PEN2 and PTR2 set.
    static TestMachine Pwm2() => TestMachine.Create("0DFF")
        .WriteSfr(0xF03C, 0xEA5F).WriteSfr(0xFE34, 0x3A98).WriteSfr(0xFF32, 0x0004).WriteSfr(0xFF30, 0x0004);

    [Fact] public void PinHighFraction_IsTheComparatorShare_XoredWithTheLatch()
    {
        var t = Pwm2();
        Assert.Equal(0.75, t.M.Pwm.PinHighFraction(2, latch: false), 6);   // PT2 ≥ PW2 for 45,000 of 60,000 counts
        Assert.Equal(0.25, t.M.Pwm.PinHighFraction(2, latch: true), 6);    // the throttle's set-up: high while PT2 < PW2
    }

    [Fact] public void PinHighFraction_WithTheOutputDisabled_IsTheLatch()
    {
        var t = Pwm2().WriteSfr(0xFF32, 0x0000);
        Assert.Equal((0.0, 1.0), (t.M.Pwm.PinHighFraction(2, false), t.M.Pwm.PinHighFraction(2, true)));
    }

    [Fact] public void PinHighFraction_KeepsTheShadow_UntilTheNextRestart()
    {
        var t = Pwm2();
        t.WriteSfr(0xFE34, 0x7530);                                         // PW2 = 30,000 while running
        Assert.Equal(0.75, t.M.Pwm.PinHighFraction(2, false), 6);
        t.M.Scheduler.Advance(60_000);
        Assert.Equal(0.5, t.M.Pwm.PinHighFraction(2, false), 6);
    }

    [Fact] public void SettingsChanged_FiresAgainAtTheRestartThatLoadsAWidthWrittenWhileRunning()
    {
        var t = TestMachine.Create("0DFF")                                   // PWM0 at fCPU: a period of 1,875 cycles
            .WriteSfr(0xF038, 0x0752).WriteSfr(0xFE30, 0x02C4).WriteSfr(0xFF32, 0x0001).WriteSfr(0xFF30, 0x0001);
        var seen = new List<(ulong Cycle, double High)>();
        t.M.Pwm.SettingsChanged += x => { if (x == 0) seen.Add((t.M.Cycles, t.M.Pwm.PinHighFraction(0, false))); };
        t.M.Scheduler.Schedule(937, "width", () => t.WriteSfr(0xFE30, 0x03E8));      // mid-period: the shadow loads at 1,875
        t.M.Scheduler.Advance(5_000);
        Assert.Equal(new[] { (937UL, 1167 / 1875.0), (1875UL, 875 / 1875.0) }, seen);
    }

    [Fact] public void SettingsChanged_FiresForPeriodWidthAndControlWrites()
    {
        var t = TestMachine.Create("0DFF");
        var seen = new List<int>();
        t.M.Pwm.SettingsChanged += seen.Add;
        t.WriteSfr(0xF03A, 0x0752).WriteSfr(0xFE32, 0x0100).WriteSfr(0xFF32, 0x0002);
        Assert.Equal(new[] { 1, 1, 0, 1, 2, 3 }, seen);
    }
}
