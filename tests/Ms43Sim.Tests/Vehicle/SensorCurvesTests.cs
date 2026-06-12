using Ms43Sim.Core;

namespace Ms43Sim.Tests.Vehicle;

public class SensorCurvesTests
{
    [Fact] public void ATablePoint_GivesItsVolts_AndTheVoltsGiveItBack()
    {
        Assert.Equal((1.001, 3.086, 0.996), (SensorCurves.CoolantVolts(90.0), SensorCurves.IntakeVolts(30.0), SensorCurves.OilVolts(89.7)));
        Assert.Equal((90.0, 30.0, 89.7), (SensorCurves.CoolantC(1.001), SensorCurves.IntakeC(3.086), SensorCurves.OilC(0.996)));
    }

    [Fact] public void BetweenPoints_ItIsLinear_AndOutsideTheTableFlat()
    {
        Assert.Equal((1.001 + 1.279) / 2, SensorCurves.CoolantVolts((90.0 + 80.2) / 2), 9);
        Assert.Equal((0.283, 4.971), (SensorCurves.CoolantVolts(200), SensorCurves.CoolantVolts(-60)));
    }

    [Fact] public void TheDefaultCarState_ReadsAsPlanThreesWarmIdle()
    {
        var warm = BoardInputs.WarmIdle.MuxAVolts;
        var s = CarState.Default;
        var codes = new[] { SensorCurves.CoolantVolts(s.RadiatorOutletC), SensorCurves.IntakeVolts(s.IntakeC),
                            SensorCurves.OilVolts(s.OilC), SensorCurves.CoolantVolts(s.CoolantC) }.Select(BoardInputs.AdcCode);
        Assert.Equal(warm.Take(4).Select(BoardInputs.AdcCode), codes);
    }
}
