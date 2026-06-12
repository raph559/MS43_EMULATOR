using Ms43Sim.Core;

namespace Ms43Sim.Tests.Drives;

public class DriveParserTests
{
    [Fact] public void ParsesDirectivesSegmentsRampsAndGears()
    {
        var d = DriveParser.Parse("""
            drive part-load
            start seasoned
            set coolant=90 oil=90 intake=33 ambient=20
            3 on
            2 crank rpm=200
            10 idle
            20 drive gear=3 speed=40..80 pedal=15
            """);
        Assert.Equal(("part-load", StartEeprom.Seasoned, 4), (d.Name, d.Start, d.Segments.Count));
        Assert.Equal(90, d.Initial["coolant"]);
        var cruise = d.Segments[3];
        Assert.Equal((DriveMode.Drive, 20.0), (cruise.Mode, cruise.Seconds));
        Assert.Equal(new Ramp(40, 80), cruise.Keys["speed"]);
        Assert.Equal(new Ramp(3, 3), cruise.Keys["gear"]);
        Assert.Equal(7, cruise.Line);
    }

    [Fact] public void CommentsBlankLinesNeutralForcesAndSilences()
    {
        var d = DriveParser.Parse("""
            # a comment line
            drive faults   # the name

            3 on
            1.5 crank rpm=160 battery=10
            5 idle force=AN0:0 force=muxA3:4.5 silence=153 silence=615
            2 drive gear=N rpm=2500 speed=0
            """);
        Assert.Equal(StartEeprom.Blank, d.Start);                          // no start directive: blank
        var idle = d.Segments[2];
        Assert.Equal(new Dictionary<string, double> { ["AN0"] = 0, ["muxA3"] = 4.5 }, idle.Forces);
        Assert.Equal(new HashSet<uint> { 0x153, 0x615 }, idle.Silenced);
        Assert.Equal(new Ramp(0, 0), d.Segments[3].Keys["gear"]);         // N
        Assert.Equal(new Ramp(10, 10), d.Segments[1].Keys["battery"]);
    }

    [Fact] public void Parse_ThrowsWithTheLine_AndTryParseReportsIt()
    {
        var e = Assert.Throws<DriveFormatException>(() => DriveParser.Parse("drive x\n3 on\n2 fly"));
        Assert.Equal(3, e.Error.Line);
        Assert.Contains("line 3", e.Message);
        Assert.True(DriveParser.TryParse("drive x\n3 on", out var drive, out var error));
        Assert.Null(error);
        Assert.Single(drive!.Segments);
    }
}
