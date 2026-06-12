using Ms43Sim.Core;

namespace Ms43Sim.Tests.Vehicle;

public class GearRatiosTests
{
    [Fact] public void RpmPerKmh_PutsTheRatiosInTheRightGearBands()
    {
        Assert.Equal(0.0, GearRatios.RpmPerKmh(0));
        Assert.Equal(new[] { 115.5, 68.3, 45.6, 34.0, 27.4 }, Enumerable.Range(1, 5).Select(GearRatios.RpmPerKmh));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Enumerable.Range(1, 5).Select(g => FirmwareGear(GearRatios.RpmPerKmh(g))));
    }

    [Fact] public void AGearOutsideNeutralToFifth_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GearRatios.RpmPerKmh(6));

    /// <summary>81AC's bands (R9): rpm per km/h under 29 is 5th, 29–39 4th, 40–55 3rd, 56–85 2nd, 86–179 1st.</summary>
    static int FirmwareGear(double rpmPerKmh) => rpmPerKmh switch { < 29 => 5, < 40 => 4, < 56 => 3, < 86 => 2, _ => 1 };
}
