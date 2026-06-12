using Ms43Sim.Core;

namespace Ms43Sim.Tests.EngineModel;

public class BreathingLawTests
{
    static readonly BreathingLaw Law = BreathingLaw.Car;

    [Fact] public void AtClosedThrottle_TheLawIsPlanThreesValveAir() =>          // θ = 0 → Q = 50·d
        Assert.Equal(50 * 0.34 * 1.0, Law.AirflowKgPerHour(900, 0, 0.34, 101.3, 33, 90, 17.0, 1e9), 1);

    [Fact] public void AtFullThrottle_TheLawReachesTheMapCeiling()                // 7100 rpm, θ 69.71°
    {
        var q = Law.AirflowKgPerHour(7100, 69.71, 0.0, 101.3, 33, 90, 600, 1e9);
        Assert.InRange(q, 600, 640);                                              // 618 kg/h (R12)
    }

    [Fact] public void TheDensity_AtTheWarmSet_Is1Point0186() =>                  // 101.3 kPa, intake 33 °C, coolant 90 °C (R12)
        Assert.Equal(1.0186, Law.Density(101.3, 33, 90), 3);

    [Fact] public void TheMap_IsBilinear_AndFlatOutsideItsAxes()
    {
        Assert.Equal((160 + 220) / 2.0, Law.MgPerStroke(1504, (7.47 + 9.96) / 2), 9);     // between two angles on the 1504 row
        Assert.Equal((10 + 10) / 2.0, Law.MgPerStroke((2016 + 2816) / 2.0, 0), 9);
        Assert.Equal((440 + 424) / 2.0, Law.MgPerStroke((2016 + 2816) / 2.0, 69.71), 9);
        Assert.Equal(80, Law.MgPerStroke(100, -5));                               // below both axes: the first point
        Assert.Equal(484, Law.MgPerStroke(9000, 88));                             // above both: the last
    }

    [Fact] public void ThePressureRatio_ThrottlesTheValve_NearTheCeiling()
    {
        // at 900 rpm the ceiling is about 58.8 kg/h; a lagged airflow at 90 % of it leaves 0.594 of the valve's air
        var ceiling = Law.Density(101.3, 33, 90) * Law.MgPerStroke(900, 69.71) * 900 / 5555.6;
        var q = Law.AirflowKgPerHour(900, 0, 1.0, 101.3, 33, 90, 0.9 * ceiling, 1e9);
        Assert.Equal(50 * 0.594, q, 6);
    }

    [Fact] public void AStoppedEngine_DrawsNoAir() =>                              // no pumping, and no 0/0
        Assert.Equal(0, Law.AirflowKgPerHour(0, 0, 0.95, 101.3, 33, 90, 0, 1e9));

    [Fact] public void TheLag_IsFiftyMilliseconds()
    {
        var q = Law.AirflowKgPerHour(900, 0, 0.34, 101.3, 33, 90, 0, 0.05);       // one time constant from 0
        Assert.Equal(17.0 * (1 - Math.Exp(-1)), q, 6);
    }
}
