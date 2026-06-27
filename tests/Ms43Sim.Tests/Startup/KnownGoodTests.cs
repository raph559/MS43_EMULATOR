using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

/// <summary>
/// Every standard drive on the car and the release files (spec §8.7): warm-start first from a blank
/// EEPROM, then the others in parallel, the seasoned ones from warm-start's final EEPROM (C5).
/// </summary>
public sealed class KnownGood
{
    public KnownGood()
    {
        var car = Task.Run(() => RunAll(TestImages.Car));
        var release = Task.Run(() => RunAll(TestImages.AsFlashed(TestBins.ReleaseBytes)));
        Car = car.Result;
        Release = release.Result;
    }

    public IReadOnlyDictionary<string, DriveResult> Car { get; }

    public IReadOnlyDictionary<string, DriveResult> Release { get; }

    static IReadOnlyDictionary<string, DriveResult> RunAll(FlashedImage image)
    {
        var warm = DriveRun.RunWithChecks(image, StandardDrives.Load("warm-start"));
        var seasoned = warm.Eeprom.FinalImage;
        var rest = StandardDrives.All.Skip(1).AsParallel().AsOrdered().Select(name =>
        {
            var drive = StandardDrives.Load(name);
            return (name, DriveRun.RunWithChecks(image, drive, new DriveRunOptions(Eeprom: drive.Start == StartEeprom.Seasoned ? seasoned : null)));
        }).ToArray();
        return rest.Prepend(("warm-start", warm)).ToDictionary(p => p.Item1, p => p.Item2);
    }
}

[Trait("Category", "StandardDrives")]
public class KnownGoodTests(KnownGood f) : IClassFixture<KnownGood>
{
    static string Why(DriveResult r) => string.Join("; ", Verdict.Reasons(r.Checks, r.Coverage!));

    [Theory]
    [InlineData("warm-start")] [InlineData("cold-start")] [InlineData("idle")] [InlineData("part-load")]
    [InlineData("full-load")] [InlineData("overrun")] [InlineData("key-cycle")]
    public void Car_Passes(string drive) => Assert.True(f.Car[drive].Verdict == RunVerdict.Pass, $"car {drive}: {Why(f.Car[drive])}");

    [Theory]
    [InlineData("warm-start")] [InlineData("cold-start")] [InlineData("idle")] [InlineData("part-load")]
    [InlineData("full-load")] [InlineData("overrun")] [InlineData("key-cycle")]
    public void Release_Passes(string drive) => Assert.True(f.Release[drive].Verdict == RunVerdict.Pass, $"release {drive}: {Why(f.Release[drive])}");

    [Fact] public void FullLoad_TakesBothFilesPastTheirLimit_SoTheCutIsJudged()     // final review C2
    {
        foreach (var (file, r) in new[] { ("car", f.Car["full-load"]), ("release", f.Release["full-load"]) })
            Assert.True(((CheckBase)r.Check("rev-limit")).Exercised, $"{file}: the full-load drive never went past the limit + 50 rpm, so rev-limit judged nothing");
    }

    [Fact] public void TheWarmStart_SeedsASeasonedEeprom()                           // C5, R11
    {
        Assert.True(f.Car["warm-start"].PowerCycles[0].Eeprom.WordsWritten > 0);
        Assert.Equal(7, f.Car["idle"].PowerCycles[0].Firmware.ThrottleCheckState);  // the short throttle check of a seasoned key-on
        Assert.DoesNotContain(f.Car["idle"].PowerCycles[0].Faults.Detected, x => x.Path == 12);   // no t_es: the stop record exists
    }
}
