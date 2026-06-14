using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

/// <summary>The seven standard drives in sim/drives parse and keep their shape (plan 4 Task 12).</summary>
public class StandardDriveTests
{
    [Theory]
    [InlineData("warm-start")] [InlineData("cold-start")] [InlineData("idle")] [InlineData("part-load")]
    [InlineData("full-load")] [InlineData("overrun")] [InlineData("key-cycle")]
    public void EachStandardDrive_ParsesAndMatchesItsName(string name)
    {
        var d = StandardDrives.Load(name);
        Assert.Equal(name, d.Name);
        // no segment demands power above the car's 7008 rpm limit with spark (R7): rpm ≤ 7050 only in full-load's brief touch
        Assert.All(d.Segments, s => Assert.True(!s.Keys.TryGetValue("rpm", out var r) || Math.Max(r.From, r.To) <= 7050));
        Assert.Equal(name is "warm-start" or "cold-start" or "key-cycle" ? StartEeprom.Blank : StartEeprom.Seasoned, d.Start);
    }

    [Fact] public void TheSetIsTheSevenDrives() =>
        Assert.Equal(new[] { "warm-start", "cold-start", "idle", "part-load", "full-load", "overrun", "key-cycle" }, StandardDrives.All);

    [Fact] public void FullLoad_NeverDemandsALaunchControlHold() =>              // no 0 km/h at full pedal (R7: P0500)
        Assert.DoesNotContain(StandardDrives.Load("full-load").Segments,
            s => s.Mode == DriveMode.Drive && (s.Keys.GetValueOrDefault("pedal")?.From ?? 0) >= 67
                 && s.Keys.GetValueOrDefault("speed", new Ramp(99, 99)).From == 0);

    [Fact] public void FullLoad_ReleasesTheClutchAfterItsNoLiftShift()
    {
        var d = StandardDrives.Load("full-load");
        var shift = d.Segments.ToList().FindIndex(s => s.Keys.GetValueOrDefault("clutch")?.From == 1);
        Assert.Equal(0, d.Segments[shift + 1].Keys["clutch"].From);
        var after = DriveTimeline.At(d, d.Segments.Take(shift + 1).Sum(s => s.Seconds) + 0.5);
        Assert.Equal(after.Values["speed"] * GearRatios.RpmPerKmh(4), after.Values["rpm"], 6);   // 4th gear holds the shaft to the road
    }

    [Fact] public void WarmStart_EndsWithAnAfterRun_SoItsEepromSeedsTheOthers()   // C5: the after-run writes the EEPROM (R11)
    {
        var last = StandardDrives.Load("warm-start").Segments[^1];
        Assert.Equal(DriveMode.Off, last.Mode);
        Assert.True(last.Seconds >= 15);
    }

    [Fact] public void TheDrives_RampTheRoadSpeedFromAStandstill_RatherThanStepping()
    {
        foreach (var name in StandardDrives.All)
        {
            var d = StandardDrives.Load(name);
            var points = Enumerable.Range(0, (int)(DriveTimeline.Duration(d) * 10)).Select(i => DriveTimeline.At(d, i / 10.0).Values["speed"]).ToArray();
            for (var i = 1; i < points.Length; i++)
                Assert.True(points[i] - points[i - 1] <= 60, $"{name}: the road speed jumps from {points[i - 1]} to {points[i]} km/h at {i / 10.0} s");
        }
    }
}
