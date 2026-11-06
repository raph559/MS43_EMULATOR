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
    /// <summary>V10's 40 flashed bytes (groups 138..142, <see cref="V10Fixture.Classic"/>), changed by <paramref name="change"/>.</summary>
    public static byte[] Settings(Action<byte[]>? change = null)
    {
        var settings = V10Fixture.Classic.ToArray();
        change?.Invoke(settings);
        return settings;
    }

    public static readonly byte[] Pops = Settings(s => s[0] = 1);                           // pops on, V10's defaults (V10.3 ignores 139)
    public static readonly byte[] Floor = Settings(s => { s[0] = 1; s[2] = 0; });            // pops' angle -23.625 deg
    public static readonly byte[] Blip = Settings(s => s[16] = 1);                           // the blip always on
    public static readonly byte[] BlipTwoToOne = Settings(s => { s[16] = 1; s[27] = 1; });

    /// <summary>V10.1's own flashed settings with pops on, and with the blip on.</summary>
    public static readonly byte[] V101Pops = V101(s => s[0] = 1);
    public static readonly byte[] V101Blip = V101(s => s[16] = 1);

    static byte[] V101(Action<byte[]> change)
    {
        var settings = V10Fixture.Flashed.ToArray();
        change(settings);
        return settings;
    }

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

    /// <summary>
    /// One blip spark decision (V10.2), taken right after psk_blip writes E2B5 (psk_written): the time, the group the angle
    /// came from (140 the blip angle, 141 the cap), 807F and BLIP_TGT as it compared them (32 rpm units), and the angle
    /// written, after the ramp.
    /// </summary>
    public sealed record SparkPick(double Seconds, int Group, int Rpm, int Target, int Angle);

    public sealed record Run(DriveResult Result, IReadOnlyList<Store> Stores, IReadOnlyList<Tick> Ticks, IReadOnlyList<SparkPick> Sparks);

    public static Run Record(byte[]? settings, string drive)
    {
        var stores = new List<Store>(); var ticks = new List<Tick>(); var sparks = new List<SparkPick>();
        var hk = (uint)V10Fixture.Symbol("hk");
        var picked = (uint)V10Fixture.Symbol("psk_written");             // right after MOVB 0E2B5h, RL4
        ushort pedalMap = 0;
        void Attach(DriveContext context) => context.PowerCycleStarted += (machine, _) =>
        {
            machine.OnInstruction(0x9B138, m => pedalMap = m.Cpu.R(4));
            machine.OnInstruction(0x9B13C, m => stores.Add(new Store(m.Cycles / 24e6, m.Bus.Peek16(0x406A4) - pedalMap, m.Bus.Peek16(0xFDF8))));
            machine.OnInstruction(hk, m => ticks.Add(new Tick(m.Cycles / 24e6, m.Bus.Peek16(0xFDF8))));
            machine.OnInstruction(picked, m => sparks.Add(new SparkPick(m.Cycles / 24e6, m.Cpu.R(2), V10Fixture.Byte(m, 0x4007F),
                V10Fixture.Byte(m, V10Fixture.BlipTgt), m.Bus.Peek16(0xE2B4) >> 8)));
        };
        var result = DriveRun.RunWithChecks(Image(settings), Load(drive), new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        return new Run(result, stores, ticks, sparks);
    }

    public IReadOnlyDictionary<string, Run> Runs { get; }

    public V10DriveFixture()
    {
        var jobs = new (string Key, string Drive, byte[]? Settings)[]
        {
            ("pops", "pops-window", Pops), ("floor", "pops-window", Floor),
            ("pops-brake", "pops-brake", Pops), ("braking", "blip-braking", Blip), ("braking-off", "blip-braking", null),
            ("tap", "blip-tap", Blip), ("upshift", "blip-upshift", Blip), ("stop", "blip-stop-in-2nd", Blip),
            ("stop-21", "blip-stop-in-2nd", BlipTwoToOne), ("from-cut", "blip-from-cut", Blip), ("from-cut-off", "blip-from-cut", null),
            ("pops-off", "pops-window", null), ("pops-brake-off", "pops-brake", null), ("tap-off", "blip-tap", null),
            ("upshift-off", "blip-upshift", null), ("stop-off", "blip-stop-in-2nd", null),
            ("v101-pops", "pops-v101", V101Pops), ("v101-pops-off", "pops-v101", null), ("v101-braking", "blip-braking", V101Blip),
            ("v101-downhill", "pops-downhill-v101", V101Pops), ("v101-downhill-off", "pops-downhill-v101", null),
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

    /// <summary>
    /// V10.2's cap on the scripted downshifts (the braking one passes 3rd's target, the tap one starts below 4th's): above
    /// the target the cap angle at once; at or below it the blip angle. A blip's first decision is its whole angle (the
    /// owner: "very sharp"); after the cap the angle comes back from the last one written a step of 807F / 4 at most per
    /// decision. Each run has one blip, so every decision after the first follows the one before.
    /// </summary>
    [Theory]
    [InlineData("braking", 0x5A)] [InlineData("tap", 0x5A)] [InlineData("v101-braking", 0x75)]
    public void TheBlipFiresTheCapAngleAboveItsTarget(string key, int blipAngle)
    {
        var sparks = fixture.Runs[key].Sparks;
        Assert.Contains(sparks, s => s.Rpm > s.Target);
        Assert.Contains(sparks, s => s.Rpm <= s.Target && s.Angle == blipAngle);
        for (var i = 0; i < sparks.Count; i++)
        {
            var s = sparks[i];
            var capped = s.Rpm > s.Target;
            var expected = capped ? 0 : i == 0 ? blipAngle : Math.Min(blipAngle, sparks[i - 1].Angle + s.Rpm / 4);
            Assert.True(s.Group == (capped ? 141 : 140) && s.Angle == expected,
                $"{key} at {s.Seconds:F3} s: rpm {s.Rpm} target {s.Target}, group {s.Group}, angle {s.Angle:X2}, expected {expected:X2}");
        }
    }

    private static readonly int[] FiringOrder = [1, 5, 3, 6, 2, 4];

    /// <summary>One coil charge cut short: when (the short spark, or the spark after a missing one) and what.</summary>
    internal sealed record CutCharge(double Seconds, string Text);

    /// <summary>
    /// The coil charges cut short from <paramref name="from"/> to <paramref name="to"/> seconds: a spark after under 1.8 ms
    /// of charge (these drives charge 2.3 to 2.4 ms, and a ramp step takes under 0.5 ms), or a cylinder missing from the
    /// firing order (the emulator keeps a spark only after 0.5 ms of charge, so a charge cut to almost nothing is none).
    /// </summary>
    internal static List<CutCharge> CutCharges(DriveResult run, double from, double to)
    {
        var sparks = run.Sparks.Where(s => s.Fire / 24e6 >= from && s.Fire / 24e6 <= to).OrderBy(s => s.Fire).ToList();
        var cuts = sparks.Where(s => s.DwellMs < 1.8)
            .Select(s => new CutCharge(s.Fire / 24e6, $"cylinder {s.Cylinder} at {s.Fire / 24e6:F3} s charged {s.DwellMs:F2} ms")).ToList();
        for (var i = 1; i < sparks.Count; i++)
        {
            var next = FiringOrder[(Array.IndexOf(FiringOrder, sparks[i - 1].Cylinder) + 1) % 6];
            if (sparks[i].Cylinder != next)
                cuts.Add(new CutCharge(sparks[i].Fire / 24e6,
                    $"cylinder {next} had no spark between {sparks[i - 1].Fire / 24e6:F3} and {sparks[i].Fire / 24e6:F3} s"));
        }
        return cuts;
    }

    /// <summary>
    /// V10.2: around a blip, only its first firing may lose its charge. The blip's start jumps to its whole angle, as on
    /// V10.1 (the owner: "very sharp"), and the coil already charging for the overrun angle fires early: at most one
    /// cylinder, within 40 ms of the first decision (2026-11-08: cylinder 3, 0.04 ms, from −12.75° to +20.25° at 1,440 rpm).
    /// The cap and its return never cut a charge (2026-11-08, before the ramp: cylinder 4, 0.21 ms, igcfb_5 detected).
    /// </summary>
    [Theory]
    [InlineData("braking")] [InlineData("tap")] [InlineData("v101-braking")]
    public void OnlyABlipsFirstFiringMayLoseItsCharge(string key)
    {
        var run = fixture.Runs[key];
        var blip = Assert.Single(Spans(run, V10Fixture.BlipOn));
        var first = run.Sparks[0].Seconds;
        var cuts = CutCharges(run.Result, blip.Start - 0.1, blip.End + 0.2);
        Assert.True(cuts.Count <= 1 && cuts.All(c => c.Seconds >= first && c.Seconds <= first + 0.04),
            $"{key} (first decision at {first:F3} s): " + string.Join("; ", cuts.Select(c => c.Text)));
    }

    [Fact]
    public void V103ImagesAreKnownWhateverTheirSettings()
    {
        Assert.Equal("MS43X001 live overlay V10.3", ProgramIdentity.Name(V10DriveFixture.Image(null).Image));
        Assert.Equal("MS43X001 live overlay V10.3", ProgramIdentity.Name(V10DriveFixture.Image(V10DriveFixture.Pops).Image));
        Assert.Equal("MS43X001 live overlay V10.3", ProgramIdentity.Name(V10DriveFixture.Image(V10DriveFixture.Blip).Image));
    }

    /// <summary>V10.3: a whole 4.0 s window at pops' own opening, +5462 (V10's flashed opening) in every store, and no stab.</summary>
    [Fact]
    public void ThePopsWindowHoldsItsOpening()
    {
        var run = fixture.Runs["pops"];
        var window = Assert.Single(Spans(run, 3));
        Assert.True(Math.Abs(window.End - window.Start - 4.0) <= 0.02, $"a window of {window.End - window.Start:F3} s");
        Assert.All(run.Stores.Where(s => Bit(s.Fdf8, 3)), s => Assert.True(Math.Abs(s.Added - 5462) <= 2, $"+{s.Added} at {s.Seconds:F3} s"));
        Assert.DoesNotContain(run.Ticks, t => Bit(t.Fdf8, V10Fixture.PopsStab));
    }

    /// <summary>
    /// V10.1's flashed pops on a lift in 3rd at 120 km/h: one 4.0 s window at 3.0 deg TPS throughout (V10.3: no stabs),
    /// the spark decided at -23.625 deg. In this run the fired angle lies up to about 0.6 deg from the decided one, at
    /// 4,200 to 5,300 rpm on a slowing engine.
    /// </summary>
    [Fact]
    public void V101sPopsDefaultsHoldThreeDegrees()
    {
        var run = fixture.Runs["v101-pops"];
        var window = Assert.Single(Spans(run, 3));
        Assert.True(Math.Abs(window.End - window.Start - 4.0) <= 0.02, $"a window of {window.End - window.Start:F3} s");
        Assert.DoesNotContain(run.Ticks, t => Bit(t.Fdf8, V10Fixture.PopsStab));
        Assert.All(run.Stores.Where(s => Bit(s.Fdf8, 3)), s => Assert.True(Math.Abs(s.Added - 1638) <= 4, $"+{s.Added} at {s.Seconds:F3} s"));
        var sparks = run.Result.Sparks.Where(s => s.Main && s.Fire / 24e6 > window.Start + 0.05 && s.Fire / 24e6 < window.End - 0.02).ToList();
        Assert.NotEmpty(sparks);
        Assert.All(sparks, s => Assert.True(Math.Abs(s.DecidedAdvance + 23.625) <= 0.01 && Math.Abs(s.Advance + 23.625) <= 1.0,
            $"spark at {s.Fire / 24e6:F3} s: decided {s.DecidedAdvance}, fired {s.Advance}"));
        Assert.Empty(Spans(fixture.Runs["v101-pops-off"], 3));
    }

    /// <summary>
    /// V10.3, end to end: on a slope that raises the rpm about 270 rpm, the window ends on the slope at V8's 160 rpm, as
    /// V10.2 did with 139/4 = 5; V10.1's 480 rpm in 139/4 no longer counts.
    /// </summary>
    [Fact]
    public void OnASlopeTheWindowEndsAt160Rpm()
    {
        var cut = Assert.Single(Spans(fixture.Runs["v101-downhill"], 3));
        Assert.True(cut.End - cut.Start > 2.3 && cut.End - cut.Start < 3.8, $"a window of {cut.End - cut.Start:F3} s");
        Assert.True(cut.End > 19.5, $"the window ended at {cut.End:F3} s");
    }

    /// <summary>
    /// The sparks of a blip: each decided between the cap's −23.625 deg and the blip angle (V10.2: the cap above the target,
    /// the ramp between), and those decided at the blip angle after a spark decided there too fired within
    /// <paramref name="fired"/> of it. (The ramp's last step rises while its coil charges, so that one spark fires
    /// between the step before and the blip angle.)
    /// </summary>
    private static void SparksOfABlip(IReadOnlyList<Spark> sparks, double angle, double fired)
    {
        Assert.All(sparks, s => Assert.True(s.DecidedAdvance >= -23.625 - 0.01 && s.DecidedAdvance <= angle + 0.01,
            $"spark at {s.Fire / 24e6:F3} s: decided {s.DecidedAdvance}"));
        static bool At(Spark s, double a) => Math.Abs(s.DecidedAdvance - a) <= 0.01;
        var ordered = sparks.OrderBy(s => s.Fire).ToList();
        var steady = ordered.Where((s, i) => i > 0 && At(s, angle) && At(ordered[i - 1], angle)).ToList();
        Assert.True(steady.Count >= 6, $"{steady.Count} steady sparks at {angle} deg");
        Assert.All(steady, s => Assert.True(Math.Abs(s.Advance - angle) <= fired, $"spark at {s.Fire / 24e6:F3} s: fired {s.Advance}"));
    }

    /// <summary>V10.1's default blip (owner, 2026-11-06: "Make it the default"): 30.0 deg TPS, fired at +20.25 deg.</summary>
    [Fact]
    public void V101BlipsWithThirtyDegreesAtTwentyDegreesOfSpark()
    {
        var run = fixture.Runs["v101-braking"];
        var blip = Assert.Single(Spans(run, V10Fixture.BlipOn));
        Assert.True(blip.Start >= 18.5 && blip.Start <= 18.6, $"the blip started at {blip.Start:F3} s");
        var rise = run.Stores.Where(s => s.Seconds > blip.Start + 0.01 && s.Seconds < blip.Start + 0.05).ToList();
        Assert.NotEmpty(rise);
        Assert.All(rise, s => Assert.True(Math.Abs(s.Added - 16387) <= 4, $"+{s.Added} at {s.Seconds:F3} s"));
        var sparks = run.Result.Sparks.Where(s => s.Main && s.Fire / 24e6 > blip.Start + 0.05 && s.Fire / 24e6 < blip.End - 0.02).ToList();
        SparksOfABlip(sparks, 20.25, 1.0);
        Assert.DoesNotContain(run.Ticks, t => (t.Fdf8 & 1 << V10Fixture.BlipOn) != 0 && (t.Fdf8 & 1 << V10Fixture.Guard) != 0);
    }

    [Fact] // RF3
    public void TheBrakeEndsThePopsAndTheyDoNotReturn()
    {
        var run = fixture.Runs["pops-brake"];
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
        SparksOfABlip(sparks, 10.125, 0.375);
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
        Assert.Single(Spans(run, 3));
        Assert.DoesNotContain(run.Ticks, t => Bit(t.Fdf8, V10Fixture.Guard));
    }

    /// <summary>Each run with V10's features on, and the same drive with them off (the image's flashed defaults).</summary>
    public static TheoryData<string, string> AllRuns => new()
    {
        { "pops", "pops-off" }, { "floor", "pops-off" }, { "pops-brake", "pops-brake-off" },
        { "braking", "braking-off" }, { "tap", "tap-off" }, { "upshift", "upshift-off" }, { "stop", "stop-off" },
        { "stop-21", "stop-off" }, { "from-cut", "from-cut-off" }, { "v101-pops", "v101-pops-off" },
        { "v101-braking", "braking-off" }, { "v101-downhill", "v101-downhill-off" },
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
