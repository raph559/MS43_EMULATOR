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
    // plan 5: every kline verb, in a drive that goes on, cranks, idles and goes off
    [InlineData("drive k\nkline connect\n3 on\nkline poll\n1 crank rpm=200\nkline send 6D 00\nkline every 0.5 0B B0\nkline raw 12 05 0B B0 AC\nkline quiet\n5 idle\nkline disconnect\n2 off")]
    // plan 5: a stall after an idle, or after a drive that ends at standstill (a ramp down to 0, a dyno revving at 0); then a crank, an on or an off
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n3 drive gear=1 speed=10..0\n2 stall pedal=0 clutch=1\n1 crank rpm=200\n5 idle")]
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n3 drive gear=N rpm=2500 speed=0\n2 stall")]
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n2 stall\n3 on\n2 crank rpm=200\n5 idle")]
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n2 stall\n3 off\n3 on")]
    // plan 5: a stall takes every kept key, a ramp of one included, and the forces and silences every mode takes
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n2 stall coolant=80..70 oil=90 intake=30 ambient=20 clock=600 ac=1 pedal=0 gear=N clutch=1 brake=1 force=AN0:2.5 silence=153")]
    public void GoodDrives_Parse(string text) => Assert.True(DriveParser.TryParse(text, out _, out var error), error?.ToString());

    // ---- plan 5: the kline lines ----

    [Fact] public void KLineLines_ApplyAtTheStartOfTheNextSegment()
    {
        var d = DriveParser.Parse("drive k\n3 on\nkline connect\nkline poll\n2 crank rpm=200\n20 idle\nkline send 6D 00\n5 idle");
        Assert.Equal(new[] { KLineVerb.Connect, KLineVerb.Poll }, d.Segments[1].KLine.Select(a => a.Verb));
        Assert.Equal((DriveMode.Crank, 5), (d.Segments[1].Mode, d.Segments[1].Line));       // the segment keeps its own line
        var send = Assert.Single(d.Segments[3].KLine);
        Assert.Equal(KLineVerb.Send, send.Verb);
        Assert.Equal(new byte[] { 0x6D, 0x00 }, send.Bytes);
        Assert.Empty(d.Segments[0].KLine);
        Assert.Empty(d.Segments[2].KLine);
    }

    [Fact] public void KLineActions_CarryTheirVerbBytesPeriodAndLine()
    {
        // before the first segment, between comments, in lower-case hex and with a comment after the bytes
        var d = DriveParser.Parse("drive k\n# a note\nkline send 6d 00   # lower-case hex\nkline every 0.25 0B B0\nkline raw 12 05 0B B0 AC\nkline quiet\n3 on");
        var actions = d.Segments[0].KLine;
        Assert.Equal(new[] { KLineVerb.Send, KLineVerb.Every, KLineVerb.Raw, KLineVerb.Quiet }, actions.Select(a => a.Verb));
        Assert.Equal(new[] { 3, 4, 5, 6 }, actions.Select(a => a.Line));
        Assert.Equal(new byte[] { 0x6D, 0x00 }, actions[0].Bytes);
        Assert.Equal(new byte[] { 0x0B, 0xB0 }, actions[1].Bytes);
        Assert.Equal(new byte[] { 0x12, 0x05, 0x0B, 0xB0, 0xAC }, actions[2].Bytes);
        Assert.Empty(actions[3].Bytes);
        Assert.Equal(new[] { 0, 0.25, 0, 0 }, actions.Select(a => a.Period));       // a period is the every verb's alone
    }

    [Theory]
    [InlineData("drive k\n3 on\nkline poll", 3, "needs a segment after it")]
    [InlineData("drive k\nkline fetch\n3 on", 2, "unknown")]
    [InlineData("drive k\nkline send 6G\n3 on", 2, "hex")]
    [InlineData("drive k\nkline every 6D 00\n3 on", 2, "seconds")]
    [InlineData("drive k\nkline raw\n3 on", 2, "bytes")]
    // the first of several dangling lines; an earlier mistake in the file comes first; no segment at all
    [InlineData("drive k\n3 on\nkline poll\nkline quiet", 3, "needs a segment after it")]
    [InlineData("drive k\n1 off\nkline poll", 2, "the first segment cannot be off")]
    [InlineData("drive k\nkline poll", 1, "at least one segment")]
    // physical lines, past blanks and comments
    [InlineData("drive k\n\n# a note\nkline fetch   # not a verb\n3 on", 4, "unknown")]
    // a verb is needed, and the four plain verbs take nothing
    [InlineData("drive k\nkline\n3 on", 2, "verb")]
    [InlineData("drive k\nkline poll now\n3 on", 2, "no arguments")]
    [InlineData("drive k\nkline connect 12 08\n3 on", 2, "no arguments")]
    // a byte is exactly two hex digits
    [InlineData("drive k\nkline send 6D 0\n3 on", 2, "hex")]
    [InlineData("drive k\nkline send 0x6D\n3 on", 2, "hex")]
    [InlineData("drive k\nkline raw 12 FFF\n3 on", 2, "hex")]
    [InlineData("drive k\nkline every 1 6D 0G\n3 on", 2, "hex")]
    // send and every need a payload, raw its bytes
    [InlineData("drive k\nkline send\n3 on", 2, "bytes")]
    [InlineData("drive k\nkline every 1\n3 on", 2, "bytes")]
    // every needs a period: a number above 0 and at most 60. The plan's "seconds" row at the top also
    // matches the message of a line that is not a kline line at all ("a segment starts with its duration
    // in seconds"), so these fragments are stricter.
    [InlineData("drive k\nkline every 6D 00\n3 on", 2, "not a number")]
    [InlineData("drive k\nkline every NaN 6D\n3 on", 2, "not a number")]
    [InlineData("drive k\nkline every\n3 on", 2, "its period")]
    [InlineData("drive k\nkline every 0 6D\n3 on", 2, "above 0")]
    [InlineData("drive k\nkline every -1 6D\n3 on", 2, "above 0")]
    [InlineData("drive k\nkline every 60.5 6D\n3 on", 2, "at most 60")]
    public void KLine_Mistakes_AreReportedWithTheirLine(string text, int line, string fragment)
    {
        Assert.False(DriveParser.TryParse(text, out _, out var error));
        Assert.Equal(line, error.Line);
        Assert.Contains(fragment, error.Message);
    }

    [Fact] public void EveryPeriod_AtMostSixtySeconds_IsAccepted()
    {
        Assert.True(DriveParser.TryParse("drive k\nkline every 60 6D 00\nkline every 0.001 0B B0\n3 on", out var d, out var error), error?.ToString());
        Assert.Equal(new[] { 60.0, 0.001 }, d.Segments[0].KLine.Select(a => a.Period));
    }

    /// <summary>A drive with one kline line: <paramref name="verb"/> (for every, with its period) and then <paramref name="count"/> bytes.</summary>
    static string KLineWithBytes(string verb, int count) => $"drive k\nkline {verb} {string.Join(' ', Enumerable.Repeat("6D", count))}\n3 on";

    [Fact] public void SendPayloadOver252Bytes_IsAMistake()
    {
        Assert.True(DriveParser.TryParse(KLineWithBytes("send", 252), out var d, out var accepted), accepted?.ToString());
        Assert.Equal(252, d.Segments[0].KLine[0].Bytes.Count);
        Assert.False(DriveParser.TryParse(KLineWithBytes("send", 253), out _, out var error));
        Assert.Equal(2, error.Line);
        Assert.Contains("252", error.Message);
    }

    [Theory]
    [InlineData("every 1", 252)]
    [InlineData("raw", 255)]
    public void EveryAndRawBytes_AreLimitedToWhatAFrameCarries(string verb, int most)
    {
        Assert.True(DriveParser.TryParse(KLineWithBytes(verb, most), out var d, out var accepted), accepted?.ToString());
        Assert.Equal(most, d.Segments[0].KLine[0].Bytes.Count);
        Assert.False(DriveParser.TryParse(KLineWithBytes(verb, most + 1), out _, out var error));
        Assert.Equal(2, error.Line);
        Assert.Contains(most.ToString(), error.Message);
    }

    // ---- plan 5: the stall mode ----

    [Fact] public void Stall_ThenCrank_IsAccepted_AndIdleAfterAStallNeedsACrank()
    {
        Assert.True(DriveParser.TryParse("drive s\n1 crank rpm=200\n5 idle\n2 stall\n1 crank rpm=200\n5 idle", out var d, out var accepted), accepted?.ToString());
        Assert.Equal(DriveMode.Stall, d.Segments[2].Mode);
        Assert.False(DriveParser.TryParse("drive s\n1 crank rpm=200\n5 idle\n2 stall\n5 idle", out _, out var error));
        Assert.Equal(5, error.Line);
        Assert.Contains("idle needs a crank first", error.Message);
    }

    [Fact] public void Stall_NeedsARunningEngine()
    {
        Assert.False(DriveParser.TryParse("drive k\n3 on\n2 stall", out _, out var error));
        Assert.Equal(3, error.Line);
        Assert.Contains("stall needs a running engine", error.Message);
    }

    [Fact] public void Stall_AfterADriveEndingAtSpeed_IsAMistake_BecauseTheCarIsStillMoving()
    {
        Assert.False(DriveParser.TryParse("drive s\n1 crank rpm=200\n5 idle\n3 drive gear=3 speed=40\n2 stall", out _, out var error));
        Assert.Equal(5, error.Line);
        Assert.Contains("still moving", error.Message);
        Assert.Contains("ends at 40 km/h", error.Message);
    }

    [Theory]
    // a stall follows an idle or a drive only: not first, not a crank (the engine may not have caught), not an off, not a stall
    [InlineData("drive s\n2 stall", 2, "stall needs a running engine")]
    [InlineData("drive s\n1 crank rpm=200\n2 stall", 3, "stall needs a running engine")]
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n3 off\n2 stall", 5, "stall needs a running engine")]
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n2 stall\n2 stall", 5, "stall needs a running engine")]
    // after a stall a drive needs a crank first, as an idle does
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n2 stall\n2 drive gear=3 speed=40", 5, "drive needs a crank first")]
    // a stall takes the kept keys alone
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n2 stall rpm=0", 4, "stall takes only the kept keys, not 'rpm'")]
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n3 drive gear=1 speed=10..0\n2 stall speed=0", 5, "stall takes only the kept keys, not 'speed'")]
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n2 stall battery=12", 4, "stall takes only the kept keys, not 'battery'")]
    // a stall stands the car still, so it cannot follow a segment that ends moving, as an off cannot:
    // a ramp that ends above 0, a speed held from the segment before, a speed that an rpm in gear gives
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n3 drive gear=3 speed=60..20\n2 stall", 5, "ends at 20 km/h")]
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n3 drive gear=3 speed=40\n2 drive gear=3 pedal=10\n2 stall", 6, "ends at 40 km/h")]
    [InlineData("drive s\n1 crank rpm=200\n5 idle\n3 drive gear=3 rpm=3000\n2 stall", 5, "still moving")]
    public void Stall_Mistakes_AreReportedWithTheirLine(string text, int line, string fragment)
    {
        Assert.False(DriveParser.TryParse(text, out _, out var error));
        Assert.Equal(line, error.Line);
        Assert.Contains(fragment, error.Message);
    }
}
