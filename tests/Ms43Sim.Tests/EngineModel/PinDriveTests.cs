using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.EngineModel;

public class PinDriveTests
{
    [Fact] public void SoftwareToggledPin_LowShare_IsExact()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFC2, 0x1000).WriteSfr(0xFFC0, 0x1000);        // P2.12 output, high
        var d = new PinDrive(t.M.Scheduler, t.M.Ports, 2, 12);
        t.M.Scheduler.Schedule(3_000, "low", () => t.WriteSfr(0xFFC0, 0x0000));
        t.M.Scheduler.Schedule(7_000, "high", () => t.WriteSfr(0xFFC0, 0x1000));
        t.M.Scheduler.Advance(10_000);
        Assert.Equal(0.4, d.TakeLowShare(), 9);
    }

    [Fact] public void PwmPin_LowShare_IsOneMinusItsHighShare()
    {
        var t = TestMachine.Create("0DFF")                                   // PWM0 at 200 Hz, PW0 = 708 of 1,875
            .WriteSfr(0xF038, 0x0752).WriteSfr(0xFE30, 0x02C4).WriteSfr(0xFF32, 0x0001).WriteSfr(0xFF30, 0x0011)
            .WriteSfr(0xFFD2, 0x0001);                                       // P7.0 output, latch 0
        var d = new PinDrive(t.M.Scheduler, t.M.Ports, 7, 0, t.M.Pwm, 0);
        t.M.Scheduler.Advance(120_000);                                      // one period: the watchdog's first overflow (131,072) resets the PWM
        Assert.Equal(708 / 1875.0, d.TakeLowShare(), 9);
    }

    [Fact] public void LatchForcedWithTheOutputDisabled_IsFullDriveWhileLow()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFD2, 0x0001).WriteSfr(0xFFD0, 0x0001);        // P7.0 output, high, PEN0 = 0
        var d = new PinDrive(t.M.Scheduler, t.M.Ports, 7, 0, t.M.Pwm, 0);
        t.M.Scheduler.Schedule(5_000, "pulse", () => t.WriteSfr(0xFFD0, 0x0000));
        t.M.Scheduler.Advance(10_000);
        Assert.Equal(0.5, d.TakeLowShare(), 9);
    }

    [Fact] public void PwmWidthWrittenWhileRunning_CountsFromTheRestartThatLoadsIt()
    {
        var t = TestMachine.Create("0DFF")                                   // PWM0 at fCPU: a period of 1,875 cycles
            .WriteSfr(0xF038, 0x0752).WriteSfr(0xFE30, 0x02C4).WriteSfr(0xFF32, 0x0001).WriteSfr(0xFF30, 0x0001)
            .WriteSfr(0xFFD2, 0x0001);
        var d = new PinDrive(t.M.Scheduler, t.M.Ports, 7, 0, t.M.Pwm, 0);
        t.M.Scheduler.Schedule(937, "width", () => t.WriteSfr(0xFE30, 0x03E8));      // mid-period: the shadow loads at 1,875
        t.M.Scheduler.Advance(3_750);
        Assert.Equal((708 + 1000) / 2 / 1875.0, d.TakeLowShare(), 9);       // one period of each
        t.M.Scheduler.Advance(7_500);
        Assert.Equal(1000 / 1875.0, d.TakeLowShare(), 9);
    }
}
