using Ms43Sim.Core;

namespace Ms43Sim.Tests.Drives;

public class DriveTimelineTests
{
    [Fact] public void RampsInterpolate_AndKeptKeysCarryForward()
    {
        var d = DriveParser.Parse("drive x\nset coolant=20\n1 crank rpm=200\n10 idle\n10 drive gear=3 speed=0..100 pedal=50");
        Assert.Equal(20, DriveTimeline.At(d, 5).Values["coolant"]);          // kept from set
        Assert.Equal(50, DriveTimeline.At(d, 16).Values["speed"]);           // half-way up the ramp
        Assert.Equal(DriveMode.Idle, DriveTimeline.At(d, 5).Mode);
        Assert.Equal(50 * GearRatios.RpmPerKmh(3), DriveTimeline.At(d, 16).Values["rpm"], 9);   // speed alone, in gear: rpm = speed × ratio
    }

    [Fact] public void KeysNotGiven_AreTheCarStateDefaults_AndRampedKeptKeysKeepTheirEnd()
    {
        var d = DriveParser.Parse("drive x\n2 on coolant=0..40\n1 on");
        var start = DriveTimeline.At(d, 0);
        Assert.Equal((0.0, CarState.Default.OilC, CarState.Default.ClockMinutes, 0.0), (start.Values["coolant"], start.Values["oil"], start.Values["clock"], start.Values["gear"]));
        Assert.Equal(20, DriveTimeline.At(d, 1).Values["coolant"]);
        Assert.Equal(40, DriveTimeline.At(d, 2.5).Values["coolant"]);       // kept at the ramp's end
    }

    [Fact] public void TheRpmAndSpeed_FollowR17()
    {
        var d = DriveParser.Parse("""
            drive x
            1.5 crank rpm=200
            5 idle
            2 drive gear=3 rpm=2280
            2 drive gear=3 speed=60 clutch=1
            2 drive gear=4 clutch=0 speed=60..30
            2 drive gear=N rpm=3000
            2 drive gear=4 rpm=1500 speed=10
            """);
        Assert.Equal((2280 / GearRatios.RpmPerKmh(3), false), (DriveTimeline.At(d, 7.5).Values["speed"], DriveTimeline.At(d, 7.5).RpmFromEngine));   // rpm alone in gear
        var clutch = DriveTimeline.At(d, 9.5);                              // speed alone, clutch in: the rpm holds
        Assert.Equal((2280.0, 60.0), (clutch.Values["rpm"], clutch.Values["speed"]));
        Assert.Equal(45 * GearRatios.RpmPerKmh(4), DriveTimeline.At(d, 11.5).Values["rpm"], 9);   // clutch out, 4th
        var neutral = DriveTimeline.At(d, 13.5);                             // rpm alone in neutral: the speed holds
        Assert.Equal((3000.0, 30.0), (neutral.Values["rpm"], neutral.Values["speed"]));
        Assert.Equal((1500.0, 10.0), (DriveTimeline.At(d, 15.5).Values["rpm"], DriveTimeline.At(d, 15.5).Values["speed"]));   // both: independent
    }

    [Fact] public void AfterIdle_ADriveSegmentWithoutRpm_TakesTheEnginesSpeed()
    {
        var d = DriveParser.Parse("drive x\n1.5 crank rpm=200\n5 idle\n2 drive gear=N speed=0 clutch=1");
        var p = DriveTimeline.At(d, 7);
        Assert.True(p.RpmFromEngine);
        Assert.False(p.Values.ContainsKey("rpm"));
        Assert.False(DriveTimeline.At(d, 3).Values.ContainsKey("rpm"));     // idle: the plant's speed
        Assert.Equal(200, DriveTimeline.At(d, 1).Values["rpm"]);            // crank: the starter's
    }

    [Fact] public void ForcesSilencesAndTheBattery_BelongToTheirSegmentOnly()
    {
        var d = DriveParser.Parse("drive x\n2 on battery=11..13 force=AN0:0 silence=153\n2 on");
        var first = DriveTimeline.At(d, 1);
        Assert.Equal((12.0, 0.0, true), (first.Values["battery"], first.Forces["AN0"], first.Silenced.Contains(0x153u)));
        var second = DriveTimeline.At(d, 3);
        Assert.Equal((false, 0, 0), (second.Values.ContainsKey("battery"), second.Forces.Count, second.Silenced.Count));
    }

    [Fact] public void PastTheEnd_TheLastSegmentsEndHolds()
    {
        var d = DriveParser.Parse("drive x\n2 on pedal=0..40");
        var p = DriveTimeline.At(d, 99);
        Assert.Equal((40.0, true, 0), (p.Values["pedal"], p.Ended, p.SegmentIndex));
        Assert.Equal(2, DriveTimeline.Duration(d));
    }
}
