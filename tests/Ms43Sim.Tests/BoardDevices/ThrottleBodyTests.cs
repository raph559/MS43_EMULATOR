using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.BoardDevices;

public class ThrottleBodyTests
{
    static ThrottleBody Body(Func<double> drive) { var tb = new ThrottleBody(new Scheduler(), drive); tb.PowerOn(); return tb; }
    static void For(ThrottleBody tb, double seconds) { for (var i = 0; i < (int)Math.Round(seconds / 0.0005); i++) tb.Step(0.0005); }

    [Fact] public void Undriven_ItRestsAtLimpHome_WhichReadsAsPlan2sKeyOnTracks()
    {
        var tb = Body(() => 0);
        Assert.Equal((4.90, 0.742, 4.277),
            (tb.Angle, Math.Round(ThrottleBody.Track1Volts(tb.Angle), 3), Math.Round(ThrottleBody.Track2Volts(tb.Angle), 3)));
    }

    [Fact] public void FullOpeningDrive_Passes17Degrees_Within50ms()
    {
        var tb = Body(() => 1); For(tb, 0.05);
        Assert.True(tb.Angle >= 17.0, $"{tb.Angle:0.00}°");
    }

    [Fact] public void SpringReturnsFrom17Degrees_ToLimpHome_InsideTheChecks0Point6s()
    {
        var drive = 1.0;
        var tb = Body(() => drive);
        while (tb.Angle < 17.0) tb.Step(0.0005);
        drive = 0; For(tb, 0.6);
        Assert.Equal(ThrottleBody.LimpHome, tb.Angle);
    }

    [Fact] public void ClosingDrive_PressesItOntoTheLowerStop()
    {
        var tb = Body(() => -0.47); For(tb, 0.5);                          // the firmware's closing mode at idle
        Assert.Equal(0.0, tb.Angle);
    }

    [Fact] public void SmallDrive_IsHeldByTheLimpHomeDetent()
    {
        var tb = Body(() => 0.12); For(tb, 1.0);
        Assert.Equal(ThrottleBody.LimpHome, tb.Angle);
    }

    [Fact] public void Drive_IsP7_2sHighShareMinusP6_5_OnlyWhileP6_7Enables()
    {
        var t = TestMachine.Create("0DFF")
            .WriteSfr(0xF03C, 0xEA5F).WriteSfr(0xFE34, 0x3A98).WriteSfr(0xFF32, 0x0004).WriteSfr(0xFF30, 0x0004)   // PWM2 at PW 15,000
            .WriteSfr(0xFFD2, 0x0004).WriteSfr(0xFFD0, 0x0004)              // P7.2 output, latch 1: high while PT2 < PW2
            .WriteSfr(0xFFCE, 0x00A0).WriteSfr(0xFFCC, 0x0080);             // P6.5 and P6.7 outputs; P6.7 = 1, P6.5 = 0
        Assert.Equal(0.25, ThrottleBody.Drive(t.M.Ports, t.M.Pwm), 6);
        t.WriteSfr(0xFFCC, 0x00A0); Assert.Equal(-0.75, ThrottleBody.Drive(t.M.Ports, t.M.Pwm), 6);   // closing
        t.WriteSfr(0xFFCC, 0x0020); Assert.Equal(0.0, ThrottleBody.Drive(t.M.Ports, t.M.Pwm), 6);     // disabled
    }
}
