using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V10 in whole drives (sim/drives/v10): no RAM is touched; a run with pops or the blip on is the V10 image with other
/// flashed settings (an in-memory bin, its checksums corrected as a full write does). The trace samples every 100 ms, so
/// the throttle stores and the housekeeping passes are recorded by instruction observers, which only read.
/// </summary>
public sealed class V10DriveFixture
{
    /// <summary>V10's 40 flashed bytes (groups 138..142), changed by <paramref name="change"/>.</summary>
    public static byte[] Settings(Action<byte[]>? change = null)
    {
        var settings = V10Fixture.Flashed.ToArray();
        change?.Invoke(settings);
        return settings;
    }

    public static readonly byte[] Stabs = Settings(s => s[0] = 1);                          // pops on, V10's defaults
    public static readonly byte[] NoStabs = Settings(s => { s[0] = 1; s[9] = 0; });          // pops on, stab size 0
    public static readonly byte[] Floor = Settings(s => { s[0] = 1; s[2] = 0; });            // pops' angle -23.625 deg
    public static readonly byte[] Blip = Settings(s => s[16] = 1);                           // the blip always on
    public static readonly byte[] BlipTwoToOne = Settings(s => { s[16] = 1; s[27] = 1; });

    public static FlashedImage Image(byte[]? settings)
    {
        var bytes = V10Fixture.Bytes;
        settings?.CopyTo(bytes, V10Fixture.Defaults);
        return TestImages.AsFlashed(bytes);
    }

    public static Drive Load(string name) =>
        DriveParser.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot, "sim/drives/v10", name + ".drive")));

    /// <summary>One throttle store: the time, the addition to the pedal-map setpoint, FDF8.</summary>
    public sealed record Store(double Seconds, int Added, int Fdf8);

    /// <summary>One housekeeping pass: the time and FDF8.</summary>
    public sealed record Tick(double Seconds, int Fdf8);

    public sealed record Run(DriveResult Result, IReadOnlyList<Store> Stores, IReadOnlyList<Tick> Ticks);

    public static Run Record(byte[]? settings, string drive)
    {
        var stores = new List<Store>(); var ticks = new List<Tick>();
        var hk = (uint)V10Fixture.Symbol("hk");
        ushort pedalMap = 0;
        void Attach(DriveContext context) => context.PowerCycleStarted += (machine, _) =>
        {
            machine.OnInstruction(0x9B138, m => pedalMap = m.Cpu.R(4));
            machine.OnInstruction(0x9B13C, m => stores.Add(new Store(m.Cycles / 24e6, m.Bus.Peek16(0x406A4) - pedalMap, m.Bus.Peek16(0xFDF8))));
            machine.OnInstruction(hk, m => ticks.Add(new Tick(m.Cycles / 24e6, m.Bus.Peek16(0xFDF8))));
        };
        var result = DriveRun.RunWithChecks(Image(settings), Load(drive), new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        return new Run(result, stores, ticks);
    }

    public IReadOnlyDictionary<string, Run> Runs { get; }

    public V10DriveFixture()
    {
        var jobs = new (string Key, string Drive, byte[]? Settings)[]
        {
            ("stabs", "pops-stabs", Stabs), ("no-stabs", "pops-stabs", NoStabs), ("floor", "pops-stabs", Floor),
            ("stabs-brake", "pops-stabs-brake", Stabs), ("braking", "blip-braking", Blip), ("braking-off", "blip-braking", null),
            ("tap", "blip-tap", Blip), ("upshift", "blip-upshift", Blip), ("stop", "blip-stop-in-2nd", Blip),
            ("stop-21", "blip-stop-in-2nd", BlipTwoToOne), ("from-cut", "blip-from-cut", Blip), ("from-cut-off", "blip-from-cut", null),
            ("stabs-off", "pops-stabs", null), ("stabs-brake-off", "pops-stabs-brake", null), ("tap-off", "blip-tap", null),
            ("upshift-off", "blip-upshift", null), ("stop-off", "blip-stop-in-2nd", null),
        };
        // Two at a time: the owner games on this PC.
        Runs = jobs.AsParallel().WithDegreeOfParallelism(2).Select(j => (j.Key, Run: Record(j.Settings, j.Drive)))
            .ToDictionary(p => p.Key, p => p.Run);
    }
}

public sealed class V10DriveTests(V10DriveFixture fixture) : IClassFixture<V10DriveFixture>
{
    private static bool Bit(int fdf8, int bit) => (fdf8 & 1 << bit) != 0;

    /// <summary>The spans while FDF8 bit <paramref name="bit"/> was set, from the housekeeping passes.</summary>
    private static List<(double Start, double End)> Spans(V10DriveFixture.Run run, int bit)
    {
        var spans = new List<(double, double)>(); double? start = null;
        foreach (var tick in run.Ticks)
        {
            if (Bit(tick.Fdf8, bit) && start is null) start = tick.Seconds;
            if (!Bit(tick.Fdf8, bit) && start is { } s) { spans.Add((s, tick.Seconds)); start = null; }
        }
        if (start is { } open) spans.Add((open, run.Ticks[^1].Seconds));
        return spans;
    }

    [Fact]
    public void V10ImagesAreKnownWhateverTheirSettings()
    {
        Assert.Equal("MS43X001 live overlay V10", ProgramIdentity.Name(V10DriveFixture.Image(null).Image));
        Assert.Equal("MS43X001 live overlay V10", ProgramIdentity.Name(V10DriveFixture.Image(V10DriveFixture.Stabs).Image));
        Assert.Equal("MS43X001 live overlay V10", ProgramIdentity.Name(V10DriveFixture.Image(V10DriveFixture.Blip).Image));
    }

    [Fact]
    public void TheStabsShowInTheThrottleStores()
    {
        var run = fixture.Runs["stabs"]; var reference = fixture.Runs["no-stabs"];
        var window = Assert.Single(Spans(run, 3));
        var stabs = Spans(run, V10Fixture.PopsStab);
        Assert.Equal(5, stabs.Count);                                   // a 4.0 s window: a 2.0 s load, then five stabs
        Assert.True(Math.Abs(stabs[0].Start - window.Start - 2.0) <= 0.02, $"the first stab {stabs[0].Start - window.Start:F3} s in");
        foreach (var (start, end) in stabs) Assert.True(Math.Abs(end - start - 0.15) <= 0.01, $"a stab of {end - start:F3} s");
        for (var i = 1; i < stabs.Count; i++)
            Assert.True(Math.Abs(stabs[i].Start - stabs[i - 1].End - 0.25) <= 0.01, $"a gap of {stabs[i].Start - stabs[i - 1].End:F3} s");
        foreach (var store in run.Stores.Where(s => Bit(s.Fdf8, 3)))
        {
            var expected = Bit(store.Fdf8, V10Fixture.PopsStab) ? 13655 : 5462;
            Assert.True(Math.Abs(store.Added - expected) <= 4, $"+{store.Added} at {store.Seconds:F3} s");
        }
        Assert.All(reference.Stores.Where(s => Bit(s.Fdf8, 3)), s => Assert.True(Math.Abs(s.Added - 5462) <= 2, $"+{s.Added}"));
        Assert.DoesNotContain(reference.Ticks, t => Bit(t.Fdf8, V10Fixture.PopsStab));
    }

    [Fact] // RF3
    public void TheBrakeEndsThePopsAndTheyDoNotReturn()
    {
        var run = fixture.Runs["stabs-brake"];
        var window = Assert.Single(Spans(run, 3));
        Assert.True(window.End >= 19.5 && window.End <= 19.6, $"the window ended at {window.End:F3} s");
    }

    [Fact]
    public void ABrakingDownshiftBlips()
    {
        var run = fixture.Runs["braking"];
        var blip = Assert.Single(Spans(run, V10Fixture.BlipOn));
        // the clutch and both brake switches each pass a 4-pass debounce first
        Assert.True(blip.Start >= 18.5 && blip.Start <= 18.6, $"the blip started at {blip.Start:F3} s");
        Assert.True(blip.End <= 19.6, $"the blip ended at {blip.End:F3} s");              // the clutch comes up at 19.5 s, debounced
        var rise = run.Stores.Where(s => s.Seconds > blip.Start + 0.01 && s.Seconds < blip.Start + 0.1).ToList();
        Assert.NotEmpty(rise);
        Assert.All(rise, s => Assert.True(Math.Abs(s.Added - 8193) <= 4, $"+{s.Added} at {s.Seconds:F3} s"));
        Assert.All(run.Stores.Where(s => s.Seconds > blip.End + 0.01 && s.Seconds < blip.End + 0.5), s => Assert.Equal(0, s.Added));
        var sparks = run.Result.Sparks.Where(s => s.Main && s.Fire / 24e6 > blip.Start + 0.05 && s.Fire / 24e6 < blip.End - 0.02).ToList();
        Assert.NotEmpty(sparks);
        Assert.All(sparks, s => Assert.True(Math.Abs(s.Advance - 10.125) <= 0.375, $"spark at {s.Fire / 24e6:F3} s: {s.Advance}"));
        var opens = run.Result.Injections.Select(i => i.Open / 24e6).Where(t => t > blip.Start + 0.05 && t < blip.End).Order().ToList();
        Assert.NotEmpty(opens);
        for (var i = 1; i < opens.Count; i++) Assert.True(opens[i] - opens[i - 1] <= 0.2, $"no injection from {opens[i - 1]:F3} s");
        Assert.Empty(Spans(fixture.Runs["braking-off"], V10Fixture.BlipOn));
    }

    [Fact]
    public void ATapBlips()
    {
        var blip = Assert.Single(Spans(fixture.Runs["tap"], V10Fixture.BlipOn));
        Assert.True(blip.Start >= 20.8 && blip.Start <= 20.85, $"the blip started at {blip.Start:F3} s");
    }

    [Fact] // RF1
    public void AnUpshiftNeverBlips() => Assert.Empty(Spans(fixture.Runs["upshift"], V10Fixture.BlipOn));

    [Theory]
    [InlineData("stop")] [InlineData("stop-21")]
    public void StoppingIn2ndNeverBlips(string key) => Assert.Empty(Spans(fixture.Runs[key], V10Fixture.BlipOn));

    [Fact]
    public void FuelReturnsFromACutForABlip()
    {
        var run = fixture.Runs["from-cut"]; var stock = fixture.Runs["from-cut-off"];
        static List<double> Opens(V10DriveFixture.Run r) => r.Result.Injections.Select(i => i.Open / 24e6).Order().ToList();
        Assert.DoesNotContain(Opens(stock), t => t > 20.0 && t < 20.5);                    // the drive is in the stock cut
        var blip = Assert.Single(Spans(run, V10Fixture.BlipOn));
        var first = Opens(run).FirstOrDefault(t => t > blip.Start);
        Assert.True(first > 0 && first - blip.Start <= 0.05, $"the first injection came {first - blip.Start:F3} s after the blip");
    }

    [Fact]
    public void TheGuardStaysQuietAtPopsFloorAngle()
    {
        var run = fixture.Runs["floor"];
        Assert.NotEmpty(Spans(run, V10Fixture.PopsStab));
        Assert.DoesNotContain(run.Ticks, t => Bit(t.Fdf8, V10Fixture.Guard));
    }

    /// <summary>
    /// Spec 9: a stab gets the stock tip-in fuel. The routine at 09:7D16 that the firmware reading called the wall film runs
    /// about twice a second, so it is not the per-stab path; the injections show the response instead. 54 to 106 ms after
    /// each stab (the throttle body and the air model's lag) the stock code adds short intermediate pulses (under 2 ms,
    /// about 1.1 ms) between the main ones, and the main pulses grow (about 8 to 10.5 ms). The run without stabs has neither.
    /// </summary>
    [Fact]
    public void AStabGetsTheTipInFuel()
    {
        var run = fixture.Runs["stabs"]; var reference = fixture.Runs["no-stabs"];
        static List<(double Open, double Ms)> Pulses(V10DriveFixture.Run r, double from, double to) => r.Result.Injections
            .Select(i => (Open: i.Open / 24e6, Ms: (i.Close - i.Open) / 24e3)).Where(p => p.Open >= from && p.Open < to).ToList();
        var stabs = Spans(run, V10Fixture.PopsStab);
        Assert.Equal(5, stabs.Count);
        foreach (var (start, _) in stabs)
        {
            Assert.Contains(Pulses(run, start, start + 0.12), p => p.Ms < 2.0);
            Assert.DoesNotContain(Pulses(reference, start, start + 0.12), p => p.Ms < 2.0);
            var before = Pulses(run, start - 0.05, start).Where(p => p.Ms >= 2.0).Max(p => p.Ms);
            var during = Pulses(run, start + 0.04, start + 0.2).Where(p => p.Ms >= 2.0).Max(p => p.Ms);
            Assert.True(during > before * 1.1, $"main pulses {before:F2} -> {during:F2} ms at the stab at {start:F3} s");
        }
    }

    /// <summary>Each run with V10's features on, and the same drive with them off (V10's flashed defaults).</summary>
    public static TheoryData<string, string> AllRuns => new()
    {
        { "stabs", "stabs-off" }, { "no-stabs", "stabs-off" }, { "floor", "stabs-off" }, { "stabs-brake", "stabs-brake-off" },
        { "braking", "braking-off" }, { "tap", "tap-off" }, { "upshift", "upshift-off" }, { "stop", "stop-off" },
        { "stop-21", "stop-off" }, { "from-cut", "from-cut-off" },
    };

    // The fault labels of paths 111, 112, 113 (the throttle controller), 117, 118 (the brake) and 149 (the torque check).
    private static readonly string[] Watched = ["mtc_ctl_1", "mtc_ctl_2", "mtc_ctl_3", "bls_plaus", "pvs_bls_bts_plaus", "mon_tqi_av"];

    private static string[] Faults(DriveResult run) => run.PowerCycles
        .SelectMany(c => c.Faults.Detected.Concat(c.Faults.Stored)).Select(f => f.Label).Distinct().Order().ToArray();

    [Theory, MemberData(nameof(AllRuns))]
    public void NoNewFaultsAndEveryCheckPasses(string key, string off)
    {
        var run = fixture.Runs[key].Result; var reference = fixture.Runs[off].Result;
        var faults = Faults(run);
        Assert.True(faults.SequenceEqual(Faults(reference)), $"{key}: {string.Join(", ", faults)} against {string.Join(", ", Faults(reference))}");
        Assert.DoesNotContain(faults, f => Watched.Any(w => f.StartsWith(w)));
        Assert.True(run.Verdict == RunVerdict.Pass, $"{key}: {run.Verdict}: " +
            string.Join("; ", run.Checks.SelectMany(c => c.Findings).Take(5).Select(f => f.ToString())));
        Assert.All(run.Checks, check => Assert.Equal(CheckStatus.Pass, check.Status));
    }
}
