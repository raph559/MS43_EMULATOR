using Ms43Sim.Core;

namespace Ms43Sim.Tests.Drives;

/// <summary>Every mistake is an error on its line, never a silent default (plan 4 R17, Review Focus 3).</summary>
public class DriveFileTests
{
    [Theory]
    [InlineData("drive x\n3 on wobble=1", 2, "unknown key 'wobble'")]
    [InlineData("drive x\n3 drive gear=1..3", 2, "gear cannot ramp")]
    [InlineData("drive x\n3 idle pedal=20", 2, "pedal must be 0 in idle")]
    [InlineData("drive x\n1 crank rpm=200\n3 drive speed=50 gear=3\n2 off", 4, "off while moving")]
    [InlineData("drive x\n0 on", 2, "duration must be above 0")]
    [InlineData("drive x\n3 crank rpm=9000", 2, "rpm out of range")]
    [InlineData("3 on", 1, "the first line must be 'drive NAME'")]
    [InlineData("drive x\n3 off", 2, "the first segment cannot be off")]
    // ranges, ramps and values (R17's key table)
    [InlineData("drive x\n3 on pedal=120", 2, "pedal out of range")]
    [InlineData("drive x\n3 on coolant=-31", 2, "coolant out of range")]
    [InlineData("drive x\n3 on ambient=51", 2, "ambient out of range")]
    [InlineData("drive x\n3 on clock=1440", 2, "clock out of range")]
    [InlineData("drive x\n3 on battery=5..12", 2, "battery out of range")]
    [InlineData("drive x\n1 crank rpm=200\n5 idle\n3 drive gear=3 speed=251", 4, "speed out of range")]
    [InlineData("drive x\n3 on gear=6", 2, "gear out of range")]
    [InlineData("drive x\n3 on ac=2", 2, "ac out of range")]
    [InlineData("drive x\n3 on clutch=0..1", 2, "clutch cannot ramp")]
    [InlineData("drive x\n3 on coolant=hot", 2, "not a number")]
    [InlineData("drive x\n3 on coolant=NaN", 2, "not a number")]
    [InlineData("drive x\n3 on pedal=10 pedal=20", 2, "given twice")]
    [InlineData("drive x\n3 on force=AN4:1", 2, "cannot force 'AN4'")]
    [InlineData("drive x\n3 on force=knock:1", 2, "cannot force 'knock'")]
    [InlineData("drive x\n3 on force=AN0:6", 2, "force volts out of range")]
    [InlineData("drive x\n3 on silence=1F3", 2, "silence takes 153, 613 or 615")]
    // directives
    [InlineData("drive X\n3 on", 1, "lower-case")]
    [InlineData("drive x\nset rpm=900\n3 on", 2, "set takes only the kept keys")]
    [InlineData("drive x\nset coolant=20..40\n3 on", 2, "set cannot ramp")]
    [InlineData("drive x\n3 on\nstart blank", 3, "'start' comes before the segments")]
    [InlineData("drive x\nstart warm\n3 on", 2, "'start' takes blank or seasoned")]
    [InlineData("drive x", 1, "at least one segment")]
    [InlineData("drive x\n3 fly", 2, "unknown mode 'fly'")]
    // what each mode takes, and the order of the modes
    [InlineData("drive x\n1 crank", 2, "crank needs rpm=")]
    [InlineData("drive x\n1 crank rpm=100..200", 2, "crank's rpm cannot ramp")]
    [InlineData("drive x\n1 crank rpm=200 pedal=10", 2, "pedal must be 0 in crank")]
    [InlineData("drive x\n1 crank rpm=200\n5 idle\n2 drive gear=3 speed=40 pedal=20\n3 idle", 5, "pedal must be 0 in idle")]
    [InlineData("drive x\n3 on rpm=800", 2, "rpm only in crank and drive")]
    [InlineData("drive x\n1 crank rpm=200\n3 idle speed=10", 3, "speed only in drive")]
    [InlineData("drive x\n1 crank rpm=200\n3 idle gear=3", 3, "idle is in neutral")]
    [InlineData("drive x\n3 idle", 2, "idle needs a crank first")]
    [InlineData("drive x\n3 on\n3 drive gear=3 speed=50", 3, "drive needs a crank first")]
    [InlineData("drive x\n1 crank rpm=200\n5 idle\n3 on", 4, "the engine runs")]
    [InlineData("drive x\n1 crank rpm=200\n5 idle\n2 drive gear=3 speed=40\n1 crank rpm=200", 5, "the engine runs")]
    [InlineData("drive x\n3 on\n3 off\n3 off", 4, "the key is already off")]
    [InlineData("drive x\n1 crank rpm=200\n5 idle\n3 off\n3 idle", 5, "idle needs a crank first")]
    public void Mistakes_AreReportedWithTheirLine(string text, int line, string needle)
    {
        Assert.False(DriveParser.TryParse(text, out _, out var error));
        Assert.Equal(line, error.Line);
        Assert.Contains(needle, error.Message);
    }

    [Theory]
    [InlineData("drive x\n3 on\n1.5 crank rpm=200\n1.5 crank rpm=180\n10 idle\n10 off\n3 on\n1 crank rpm=200\n3 drive gear=1 speed=0..10\n2 drive gear=1 speed=10..0\n2 off")]
    [InlineData("drive x\nset coolant=-30 oil=140 intake=0 ambient=-40 clock=1439 ac=1 pedal=0 gear=N clutch=1 brake=1\n1 crank rpm=200 battery=6..16")]
    public void GoodDrives_Parse(string text) => Assert.True(DriveParser.TryParse(text, out _, out var error), error?.ToString());
}
