using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V10.2's review (2026-11-08): the pedal pressed while the blip's cap holds (sim/drives/v10/blip-braking-pedal, the
/// pedal at 30 % from 19.2 s into a hold at 2,700 rpm above 3rd's target). With the pedal down the stock code fires its
/// own angle again; that hand-over must not cut a coil's charge (spec §5's ramp). V10.2 runs its flashed settings with
/// the blip on; V10.1 fast blip, with its own flashed settings and the blip on, is the reference. No RAM is touched.
/// </summary>
public sealed class BlipPedalDriveFixture
{
    public const double Press = 19.2;

    public V10DriveFixture.Run V102 { get; }
    public DriveResult V101 { get; }

    public BlipPedalDriveFixture()
    {
        var v101 = TestBins.LiveOverlayV101FastBlipBytes;
        v101[V10Fixture.Defaults + 16] = 1;                                 // 140 byte 0: the blip always on
        var runs = new Func<object>[]
        {
            () => V10DriveFixture.Record(V10DriveFixture.V101Blip, "blip-braking-pedal"),
            () => DriveRun.RunWithChecks(TestImages.AsFlashed(v101), V10DriveFixture.Load("blip-braking-pedal")),
        }.AsParallel().AsOrdered().WithDegreeOfParallelism(2).Select(run => run()).ToArray();
        V102 = (V10DriveFixture.Run)runs[0];
        V101 = (DriveResult)runs[1];
    }
}

public sealed class BlipPedalDriveTests(BlipPedalDriveFixture fixture) : IClassFixture<BlipPedalDriveFixture>
{
    private static string Fired(DriveResult run, double from, double to) => string.Join("; ", run.Sparks
        .Where(s => s.Fire / 24e6 >= from && s.Fire / 24e6 <= to).OrderBy(s => s.Fire)
        .Select(s => $"{s.Fire / 24e6:F3} cyl {s.Cylinder} {s.Advance:F1}° {s.DwellMs:F2} ms"));

    /// <summary>The drive reaches the review's case: the cap's angle is the blip's last decision before the press.</summary>
    [Fact]
    public void ThePedalGoesDownWhileTheCapHolds()
    {
        var before = fixture.V102.Sparks.Where(s => s.Seconds < BlipPedalDriveFixture.Press).ToList();
        Assert.NotEmpty(before);
        Assert.True(before[^1].Group == 141 && before[^1].Angle == 0,
            $"last decision before the press at {before[^1].Seconds:F3} s: group {before[^1].Group}, angle {before[^1].Angle:X2}");
    }

    /// <summary>
    /// From the blip's start to a second after the press, only the blip's first firing may lose its charge (the owner's
    /// sharp start, as on V10.1); the hand-over from the cap to the stock angle never does. On 2026-11-08 the stock code
    /// took over from its own overrun angle (−14.6° at 2,700 rpm, 9° from the cap) and rose about 0.75° a firing, every
    /// charge 2.28 to 2.38 ms.
    /// </summary>
    [Fact]
    public void APedalPressDuringTheCapCutsNoCharge()
    {
        var run = fixture.V102.Result;
        var first = fixture.V102.Sparks[0].Seconds;
        var (from, to) = (first - 0.1, BlipPedalDriveFixture.Press + 1.0);
        var cuts = V10DriveTests.CutCharges(run, from, to);
        Assert.True(cuts.All(c => c.Seconds <= first + 0.04),
            $"V10.2 (first decision at {first:F3} s): " + string.Join("; ", cuts.Select(c => c.Text))
            + $"\nV10.2 fired: {Fired(run, BlipPedalDriveFixture.Press - 0.1, BlipPedalDriveFixture.Press + 0.2)}"
            + $"\nV10.1 cuts: {string.Join("; ", V10DriveTests.CutCharges(fixture.V101, from, to).Select(c => c.Text))}"
            + $"\nV10.1 fired: {Fired(fixture.V101, BlipPedalDriveFixture.Press - 0.1, BlipPedalDriveFixture.Press + 0.2)}");
    }

    private static string[] Faults(DriveResult run) => run.PowerCycles
        .SelectMany(c => c.Faults.Detected.Concat(c.Faults.Stored)).Select(f => f.Label).Distinct().Order().ToArray();

    /// <summary>The faults are V10.1's on the same drive, and every software and output check passes.</summary>
    [Fact]
    public void NoNewFaultAndEveryCheckPasses()
    {
        var run = fixture.V102.Result;
        Assert.True(Faults(run).SequenceEqual(Faults(fixture.V101)),
            $"{string.Join(", ", Faults(run))} against V10.1's {string.Join(", ", Faults(fixture.V101))}");
        Assert.True(run.Verdict == RunVerdict.Pass, $"{run.Verdict}: " +
            string.Join("; ", run.Checks.SelectMany(c => c.Findings).Take(5).Select(f => f.ToString())));
    }
}
