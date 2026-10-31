using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V9's widest pops throttle opening, 20.0 °TPS (owner, 2026-10-29), in the lift drive: the window adds it, and the
/// firmware's own throttle monitoring stores nothing. The drive sets the shaft's speed, so the extra air cannot make the
/// car pull here; the +160 rpm stop is V8's and is tested there.
/// </summary>
public sealed class PopsV9DriveTests
{
    private static int Column(string name) => VariableTrace.Columns.ToList().IndexOf(name);

    private static DriveResult Run(byte[] image, byte[]? settings)
    {
        var bytes = image;
        settings?.CopyTo(bytes, V9Fixture.Defaults);
        return DriveRun.RunWithChecks(TestImages.AsFlashed(bytes), PopsDriveFixture.Load("lift"));
    }

    public static TheoryData<string> Overlays => new() { "V9", "V8.5" };

    [Theory, MemberData(nameof(Overlays))]
    public void TwentyDegreesTpsIsAddedAndNothingIsStored(string overlay)
    {
        byte[] Image() => overlay == "V9" ? V9Fixture.Bytes : V85Fixture.Bytes;
        var settings = PopsDriveFixture.On.ToArray(); settings[6] = 200;
        var results = new[] { (byte[]?)null, settings }.Select(s => Run(Image(), s)).ToArray();
        var (reference, run) = (results[0], results[1]);

        Assert.Equal(RunVerdict.Pass, run.Verdict);
        Assert.All(run.Checks, check => Assert.Equal(CheckStatus.Pass, check.Status));
        var bit = Column("fdf8");
        var window = run.Trace.Where(row => ((int)(row.Values[bit] ?? 0) & 8) != 0).ToList();
        Assert.NotEmpty(window);
        var inside = window.Where(row => row.Seconds > window[0].Seconds + 0.05 && row.Seconds < window[^1].Seconds - 0.15).ToList();
        Assert.NotEmpty(inside);
        foreach (var row in inside)
        {
            var stock = reference.Trace.MinBy(r => Math.Abs(r.Seconds - row.Seconds))!.Values[Column("tps_sp_deg")]!.Value;
            var added = row.Values[Column("tps_sp_deg")]!.Value - stock;
            Assert.True(Math.Abs(added - 20.0) <= 0.3, $"throttle +{added:F3} at {row.Seconds}");
        }
    }
}
