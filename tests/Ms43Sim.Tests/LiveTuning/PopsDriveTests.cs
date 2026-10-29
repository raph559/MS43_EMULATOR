using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// Pops in whole drives on the V8 image: no RAM is touched; a pops-on run is the V8 image with other flashed pops
/// settings (an in-memory bin, its checksums corrected as a full write does), beside the built image (switch 0).
/// </summary>
public sealed class PopsDriveFixture
{
    public const double Lift = 16.5;
    // Switch on, 2.0 s, -15 deg, 11.5 AFR, 1,600 rpm, 70.5 deg C, 3.0 deg TPS, 20 km/h.
    public static readonly byte[] On = [1, 20, 23, 184, 50, 158, 30, 20];

    public static byte[] Long(int index = -1, byte value = 0)
    {
        var settings = On.ToArray(); settings[1] = 50; settings[6] = 0;
        if (index >= 0) settings[index] = value;
        return settings;
    }

    /// <summary>The lift settings with the AFR at lambda 1 (128), the stock overrun target.</summary>
    public static byte[] Lambda1() { var settings = On.ToArray(); settings[3] = 128; return settings; }

    /// <summary>The lift settings with the switch on the A/C frame's bit 7 (615h byte 0), on while it is 1.</summary>
    public static byte[] AcSwitch() { var settings = On.ToArray(); settings[0] = 0x97; return settings; }

    public IReadOnlyDictionary<string, DriveResult> Runs { get; }

    public static FlashedImage Image(byte[]? settings)
    {
        var bytes = V8Fixture.Bytes;
        settings?.CopyTo(bytes, V8Fixture.Defaults);
        return TestImages.AsFlashed(bytes);
    }

    public static Drive Load(string name) =>
        DriveParser.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot, "sim/drives/pops", name + ".drive")));

    public PopsDriveFixture()
    {
        var jobs = new (string Key, string Drive, byte[]? Settings)[]
        {
            ("reference", "lift", null), ("lift", "lift", On), ("lift-lambda1", "lift", Lambda1()), ("ac-on", "lift-ac", AcSwitch()), ("ac-off", "lift", AcSwitch()),
            ("pedal-back", "pedal-back", Long()), ("clutch", "clutch", Long()),
            ("low-rpm", "low-rpm", Long(4, 100)), ("low-speed", "lift", Long(7, 90)),
            ("cold", "cold", On), ("downhill", "downhill", Long()), ("two-lifts", "two-lifts", On),
        };
        Runs = jobs.AsParallel().Select(j => (j.Key, Run: DriveRun.RunWithChecks(Image(j.Settings), Load(j.Drive))))
            .ToDictionary(p => p.Key, p => p.Run);
    }
}

public sealed class PopsDriveTests(PopsDriveFixture fixture) : IClassFixture<PopsDriveFixture>
{
    private static int Column(string name) => VariableTrace.Columns.ToList().IndexOf(name);

    /// <summary>The windows a run had, from the trace's fdf8 bit 3 (100 ms samples): start and end, in seconds.</summary>
    private static List<(double Start, double End)> Windows(DriveResult run)
    {
        var bit = Column("fdf8"); var windows = new List<(double, double)>(); double? start = null;
        foreach (var row in run.Trace)
        {
            var active = ((int)(row.Values[bit] ?? 0) & 8) != 0;
            if (active && start is null) start = row.Seconds;
            if (!active && start is { } s) { windows.Add((s, row.Seconds)); start = null; }
        }
        Assert.Null(start);
        return windows;
    }

    /// <summary>The last injection before the first gap of more than 0.2 s after <paramref name="after"/>: the fuel cut.</summary>
    private static double Cut(DriveResult run, double after)
    {
        var opens = run.Injections.Select(i => i.Open / 24e6).Where(t => t > after).Order().ToList();
        for (var i = 1; i < opens.Count; i++) if (opens[i] - opens[i - 1] > 0.2) return opens[i - 1];
        throw new InvalidOperationException("no fuel cut after " + after);
    }

    private static double Sample(DriveResult run, string column, double seconds) =>
        run.Trace.MinBy(t => Math.Abs(t.Seconds - seconds))!.Values[Column(column)] ?? double.NaN;

    [Fact]
    public void V8ImagesAreKnownWhateverTheirPopsSettings()
    {
        Assert.Equal("MS43X001 with the live-tuning overlay 8", ProgramIdentity.Name(PopsDriveFixture.Image(null).Image));
        Assert.Equal("MS43X001 with the live-tuning overlay 8", ProgramIdentity.Name(PopsDriveFixture.Image(PopsDriveFixture.On).Image));
    }

    [Fact]
    public void LiftHoldsTheCutForTheSetDuration()
    {
        var reference = fixture.Runs["reference"]; var run = fixture.Runs["lift"];
        Assert.Empty(Windows(reference));
        var window = Assert.Single(Windows(run));
        var stock = Cut(reference, PopsDriveFixture.Lift); var pops = Cut(run, PopsDriveFixture.Lift);
        Assert.True(Math.Abs(pops - stock - 2.0) <= 0.06, $"the cut came {pops - stock:F3} s after the stock one");
        var sparks = run.Sparks.Where(s => s.Main && s.Fire / 24e6 > stock + 0.1 && s.Fire / 24e6 < pops - 0.1).ToList();
        Assert.True(sparks.Count > 100);
        Assert.All(sparks, s => Assert.True(Math.Abs(s.Advance + 15.0) <= 0.375, $"spark at {s.Fire / 24e6:F3} s: {s.Advance}"));
        foreach (var row in run.Trace.Where(r => r.Seconds > window.Start + 0.05 && r.Seconds < window.End - 0.15))
        {
            var added = row.Values[Column("tps_sp_deg")]!.Value - Sample(reference, "tps_sp_deg", row.Seconds);
            Assert.True(Math.Abs(added - 3.0) <= 0.2, $"throttle +{added:F3} at {row.Seconds}");
        }
        // After the window the stock behaviour follows: fuel returns when it does without pops.
        static double Next(DriveResult r, double after) => r.Injections.Select(i => i.Open / 24e6).Where(t => t > after).Min();
        Assert.True(Math.Abs(Next(run, pops + 0.1) - Next(reference, pops + 0.1)) <= 0.1,
            $"fuel returned at {Next(run, pops + 0.1):F3} s, stock at {Next(reference, pops + 0.1):F3} s");
    }

    [Fact] // final review C1: the rich target reaches the injection during the window
    public void TheAfrSettingRichensTheWindow()
    {
        var rich = fixture.Runs["lift"]; var lambda1 = fixture.Runs["lift-lambda1"];
        var window = Assert.Single(Windows(rich));
        var samples = rich.Trace.Where(r => r.Seconds > window.Start + 0.3 && r.Seconds < window.End - 0.1).ToList();
        Assert.NotEmpty(samples);
        foreach (var row in samples)
        {
            var richMs = row.Values[Column("ti1_ms")]!.Value; var stockMs = Sample(lambda1, "ti1_ms", row.Seconds);
            Assert.True(richMs > stockMs * 1.15, $"at {row.Seconds}: {richMs:F3} ms at 11.5 AFR against {stockMs:F3} ms at lambda 1");
        }
    }

    [Theory]
    [InlineData("pedal-back", 18.5)] [InlineData("clutch", 18.5)] [InlineData("low-rpm", 18.5)] [InlineData("low-speed", 0)]
    public void EachStopEndsTheWindowWithin100ms(string key, double stop)
    {
        var run = fixture.Runs[key];
        // The firmware's road speed (847F) lags the drive's: the low-speed stop is when it falls under 90 km/h.
        if (key == "low-speed") stop = run.Trace.First(r => r.Seconds > PopsDriveFixture.Lift && r.Values[Column("vs_ecu_kmh")] < 90).Seconds;
        var window = Assert.Single(Windows(run));
        Assert.True(window.Start < stop, $"{key}: the window started at {window.Start}");
        Assert.True(window.End >= stop - 0.05 && window.End <= stop + 0.2, $"{key}: the window ended at {window.End}, stop at {stop}");
    }

    [Fact]
    public void DownhillEndsTheWindowWhenTheRpmRises()
    {
        var run = fixture.Runs["downhill"];
        var window = Assert.Single(Windows(run));
        Assert.True(window.Start < 19.0, $"the window started at {window.Start}");
        Assert.True(window.End > 19.0 && window.End < 20.6, $"the window ended at {window.End}");
        Assert.True(Sample(run, "rpm_ecu", window.End) > Sample(run, "rpm_ecu", 19.0) + 100);
    }

    [Fact] // the A/C button as the switch: the emulated cluster sends 615h byte 0 = C0h with the A/C on
    public void TheAcBitSwitchesPopsOnAndOff()
    {
        Assert.Single(Windows(fixture.Runs["ac-on"]));
        Assert.Empty(Windows(fixture.Runs["ac-off"]));
    }

    [Fact]
    public void ColdEngineNeverStartsAWindow() => Assert.Empty(Windows(fixture.Runs["cold"]));

    [Fact]
    public void OneWindowPerLift()
    {
        var windows = Windows(fixture.Runs["two-lifts"]);
        Assert.Equal(2, windows.Count);
        Assert.True(windows[0].End <= 19.6 && windows[1].Start >= 21.5, string.Join(", ", windows));
    }

    [Fact]
    public void PopsDrivesLeaveNoNewFaults()
    {
        static string[] Faults(DriveResult run) => run.PowerCycles
            .SelectMany(c => c.Faults.Detected.Concat(c.Faults.Stored)).Select(f => f.Label).Distinct().Order().ToArray();
        var reference = fixture.Runs["reference"];
        foreach (var key in new[] { "lift", "pedal-back", "clutch", "low-rpm", "low-speed", "downhill", "two-lifts" })
        {
            Assert.True(Faults(reference).SequenceEqual(Faults(fixture.Runs[key])),
                $"{key}: {string.Join(", ", Faults(fixture.Runs[key]))} against {string.Join(", ", Faults(reference))}");
            Assert.True(reference.Verdict == fixture.Runs[key].Verdict, $"{key}: {fixture.Runs[key].Verdict}: " + string.Join("; ",
                fixture.Runs[key].Checks.SelectMany(c => c.Findings).Take(5).Select(f => f.ToString())));
        }
    }
}
