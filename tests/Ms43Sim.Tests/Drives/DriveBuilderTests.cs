using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Drives;

public class DriveBuilderTests
{
    [Theory]
    [InlineData("warm-start", StartEeprom.Blank)]
    [InlineData("part-load", StartEeprom.Seasoned)]
    [InlineData("full-load", StartEeprom.Seasoned)]
    [InlineData("overrun", StartEeprom.Seasoned)]
    [InlineData("key-cycle", StartEeprom.Blank)]
    public void TheNamedCodeDrives_AreShortEquivalentsOfTheStandardOnes(string name, StartEeprom start)
    {
        var d = DriveBuilder.Named(name);
        Assert.Equal((name, start), (d.Name, d.Start));
        Assert.InRange(DriveTimeline.Duration(d), 20, 60);
        Assert.Equal((DriveMode.On, DriveMode.Crank), (d.Segments[0].Mode, d.Segments[1].Mode));
    }

    [Fact] public void FullLoad_TouchesTheLimiterForOneSecond_AndShiftsWithoutLifting()
    {
        var d = DriveBuilder.Named("full-load");
        Assert.Single(d.Segments, s => s.Keys.TryGetValue("rpm", out var r) && r.To > 7008);
        Assert.Contains(d.Segments, s => s.Keys.GetValueOrDefault("clutch")?.From == 1 && s.Keys["pedal"].From == 95);
    }

    [Fact] public void AnUnknownName_IsRefused() => Assert.Throws<ArgumentException>(() => DriveBuilder.Named("tour-de-france"));
}
