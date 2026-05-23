using Ms43Sim.Core;

namespace Ms43Sim.Tests.EngineModel;

public class VanosActuatorsTests
{
    static void For(VanosActuators v, double seconds, double rpm) { for (var i = 0; i < (int)Math.Round(seconds / 0.0005); i++) v.Step(0.0005, rpm); }

    [Fact] public void AtTheHoldDuty_TheCamsStayAtRest()
    {
        var v = new VanosActuators(() => 0.377, () => 0.365, 0.377, 0.365); For(v, 1.0, 900);
        Assert.Equal((0.0, 0.0), (v.IntakeAdvance, v.ExhaustRetard));
    }

    [Fact] public void FullDrive_MovesAtGainTimesTheExcess()
    {
        var v = new VanosActuators(() => 1.0, () => 0.365, 0.377, 0.365); For(v, 0.05, 900);
        Assert.Equal(400 * (1 - 0.377) * 0.05, v.IntakeAdvance, 6);
    }

    [Fact] public void WithoutOilPressure_TheCamsReturnToRest()
    {
        var drive = 1.0;
        var v = new VanosActuators(() => drive, () => drive, 0.377, 0.365); For(v, 0.05, 900);
        drive = 0.0; For(v, 0.2, 400);                                       // below 500 rpm: 200 °/s back to rest
        Assert.Equal((0.0, 0.0), (v.IntakeAdvance, v.ExhaustRetard));
    }

    [Fact] public void TheRangesStopTheTravel()
    {
        var v = new VanosActuators(() => 1.0, () => 1.0, 0.377, 0.365); For(v, 1.0, 900);
        Assert.Equal((40.1, 25.1), (v.IntakeAdvance, v.ExhaustRetard));
    }
}
