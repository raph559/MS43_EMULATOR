using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V10.2's real pops AFR in whole drives: sim/drives/v10/pops-v101.drive on the V10.2 image with pops on and the AFR
/// byte (138/3, the real AFR × 10) at 14.7, 9.8, 7.0 and 5.0, as in-memory bins whose checksums are corrected as a full
/// write does. No RAM is touched; the logged target (91B9) is read at each housekeeping pass.
/// </summary>
public sealed class PopsRealAfrDriveFixture
{
    public static readonly byte[] Afrs = [147, 98, 70, 50];

    /// <summary>One housekeeping pass: the time, FDF8 and the logged AFR target, 91B9 (AFR × 10).</summary>
    public sealed record Tick(double Seconds, int Fdf8, int Logged);

    /// <summary>
    /// One pass of the stock fuel factor (0C:AFD0 to 0C:AFDA, 0D:194C): the time, FDF8, the fuel before it (R9), the
    /// factor word ([R0+2], 91B0 + the enrichment), the fuel after it (R4), and 91A0 and 91B0 as it ran.
    /// </summary>
    public sealed record FactorPass(double Seconds, int Fdf8, int Before, int Factor, int After, int Enrichment, int Base);

    public sealed record Run(DriveResult Result, IReadOnlyList<Tick> Ticks, IReadOnlyList<FactorPass> Factors);

    public static byte[] Settings(byte afr)
    {
        var settings = V10Fixture.Flashed.ToArray();
        settings[0] = 1;
        settings[3] = afr;
        return settings;
    }

    static Run Record(byte afr)
    {
        var ticks = new List<Tick>(); var factors = new List<FactorPass>();
        var hk = (uint)V10Fixture.Symbol("hk");
        (int Before, int Factor)? factor = null;
        void Attach(DriveContext context) => context.PowerCycleStarted += (machine, _) =>
        {
            machine.OnInstruction(hk, m => ticks.Add(new Tick(m.Cycles / 24e6, m.Bus.Peek16(0xFDF8), m.Bus.Peek16(0x411B8) >> 8)));
            machine.OnInstruction(0xCAFD0, m => factor = (m.Cpu.R(9), m.Bus.Peek16(m.Cpu.R(0) + 2u)));
            machine.OnInstruction(0xCAFDA, m =>
            {
                if (factor is not { } f) return;
                factors.Add(new FactorPass(m.Cycles / 24e6, m.Bus.Peek16(0xFDF8), f.Before, f.Factor, m.Cpu.R(4),
                    (short)m.Bus.Peek16(0x411A0), m.Bus.Peek16(0x411B0)));
                factor = null;
            });
        };
        var result = DriveRun.RunWithChecks(V10DriveFixture.Image(Settings(afr)), V10DriveFixture.Load("pops-v101"),
            new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        return new Run(result, ticks, factors);
    }

    public IReadOnlyDictionary<byte, Run> Runs { get; }

    // Two at a time: the owner games on this PC.
    public PopsRealAfrDriveFixture() =>
        Runs = Afrs.AsParallel().WithDegreeOfParallelism(2).Select(afr => (afr, Run: Record(afr))).ToDictionary(p => p.afr, p => p.Run);
}

public sealed class PopsRealAfrDriveTests(PopsRealAfrDriveFixture fixture) : IClassFixture<PopsRealAfrDriveFixture>
{
    private static int Column(string name) => VariableTrace.Columns.ToList().IndexOf(name);

    private static double Sample(DriveResult run, string column, double seconds) =>
        run.Trace.MinBy(r => Math.Abs(r.Seconds - seconds))!.Values[Column(column)]!.Value;

    private static int Fdf8(TraceRow row) => (int)row.Values[Column("fdf8")]!.Value;

    /// <summary>The window's trace rows (FDF8.3), from 0.3 s after it opens to 0.1 s before it ends.</summary>
    private static List<TraceRow> WindowRows(DriveResult run)
    {
        var rows = run.Trace.Where(r => (Fdf8(r) & 1 << 3) != 0).ToList();
        Assert.NotEmpty(rows);
        var (start, end) = (rows[0].Seconds, rows[^1].Seconds);
        return rows.Where(r => r.Seconds > start + 0.3 && r.Seconds < end - 0.1).ToList();
    }

    /// <summary>The window's rows in the load, before the first stab: the steady mixture.</summary>
    private static List<TraceRow> LoadRows(DriveResult run)
    {
        var rows = WindowRows(run);
        var stab = rows.First(r => (Fdf8(r) & 1 << 7) != 0).Seconds;
        return rows.Where(r => r.Seconds < stab).ToList();
    }

    private static double Ti(DriveResult run, double seconds) => Sample(run, "ti1_ms", seconds);

    [Theory] // 147 / A in the load: 1.50 at 9.8, 2.10 at 7.0, 2.94 at 5.0, against the 14.7 run at the same moment
    [InlineData((byte)98, 1.50)] [InlineData((byte)70, 2.10)] [InlineData((byte)50, 2.94)]
    public void TiFollowsTheRealAfr(byte afr, double factor)
    {
        var rows = LoadRows(fixture.Runs[afr].Result);
        Assert.True(rows.Count >= 10, $"{rows.Count} rows in the load");
        foreach (var row in rows)
        {
            var ratio = Ti(fixture.Runs[afr].Result, row.Seconds) / Ti(fixture.Runs[147].Result, row.Seconds);
            Assert.True(Math.Abs(ratio / factor - 1) <= 0.03, $"AFR {afr / 10.0} at {row.Seconds:F2} s: ti x {ratio:F3}, expected x {factor:F2}");
        }
    }

    [Theory]
    [InlineData((byte)98)] [InlineData((byte)70)] [InlineData((byte)50)]
    public void TheLoggedTargetIsTheRealAfr(byte afr)
    {
        var window = fixture.Runs[afr].Ticks.Where(t => (t.Fdf8 & 1 << 3) != 0).ToList();
        Assert.NotEmpty(window);
        var settled = window.Where(t => t.Seconds > window[0].Seconds + 0.05).ToList();
        Assert.All(settled, t => Assert.InRange(t.Logged, afr - 1, afr + 1));
    }

    /// <summary>
    /// The stabs let the most air in; at the richest setting the stock fuel factor (0C:AFD6, 0D:194C, the fuel ×
    /// factor / 4096, FFFFh on an overflow) must not saturate. At every factor pass of the stabs at 5.0 it applies the
    /// whole enrichment: the factor is 91B0 + 91A0 with 91A0 the real 7,946, and the fuel after it is the fuel before × the
    /// factor / 4096 exactly. (The injection time itself is not linear in 14.7 / A there: the stock tip-in fuel added after
    /// the factor, about 0.5 ms, depends on the fuel before it, 2026-11-08 probe: +110, +97 and +90 ticks at 14.7, 9.8 and
    /// 5.0.)
    /// </summary>
    [Fact]
    public void NoSaturationDuringTheStabs()
    {
        var passes = fixture.Runs[50].Factors.Where(p => (p.Fdf8 & 1 << 3) != 0 && (p.Fdf8 & 1 << 7) != 0).ToList();
        Assert.True(passes.Count >= 100, $"{passes.Count} factor passes in the stabs");
        foreach (var p in passes)
        {
            Assert.True(p.Enrichment == 7946 && p.Factor == p.Base + p.Enrichment,
                $"at {p.Seconds:F4} s: 91A0 {p.Enrichment}, factor {p.Factor}, 91B0 {p.Base}");
            Assert.True(p.After == p.Before * p.Factor / 4096 && p.After < 0xFFFF,
                $"at {p.Seconds:F4} s: {p.Before} x {p.Factor} / 4096 gave {p.After}");
        }
    }
}

/// <summary>
/// V10.2's every AFR real in a whole drive: sim/drives/full-load.drive (a pull in 3rd at pedal 95 to the limiter, the
/// upshift, the coast) on the V10.2 image, switches off. Each pass of the shared rich path's join is read on its way in
/// (0B:D4D4, the hook site: 91A0 as the stock code stored it, L) and on its way out (0B:D4E8: 91A0 and the logged 91B9).
/// </summary>
public sealed class RealAfrDriveFixture
{
    public sealed record Pass(double Seconds, bool Rich, int Stored, int Stoich, int Out, int Logged);

    public IReadOnlyList<Pass> Passes { get; }

    public RealAfrDriveFixture()
    {
        var passes = new List<Pass>();
        (double Seconds, bool Rich, int Stored, int Stoich)? entry = null;
        void Attach(DriveContext context) => context.PowerCycleStarted += (machine, _) =>
        {
            machine.OnInstruction(0xBD4D4, m => entry = (m.Cycles / 24e6, (m.Bus.Peek16(0xFD9A) & 1 << 10) != 0,
                (short)m.Bus.Peek16(0x411A0), m.Bus.Peek16(0x411B8) & 0xFF));
            machine.OnInstruction(0xBD4E8, m =>
            {
                if (entry is not { } e) return;
                passes.Add(new Pass(e.Seconds, e.Rich, e.Stored, e.Stoich, (short)m.Bus.Peek16(0x411A0), m.Bus.Peek16(0x411B8) >> 8));
                entry = null;
            });
        };
        var drive = DriveParser.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot, "sim/drives/full-load.drive")));
        DriveRun.RunWithChecks(V10DriveFixture.Image(null), drive, new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        Passes = passes;
    }
}

public sealed class RealAfrDriveTests(RealAfrDriveFixture fixture) : IClassFixture<RealAfrDriveFixture>
{
    /// <summary>L × 4096 / (4096 − L), truncated toward zero: the firmware's real enrichment.</summary>
    private static int Real(int stored) => stored >= 0 ? stored * 4096 / (4096 - stored) : -(-stored * 4096 / (4096 - stored));

    /// <summary>The stock display, 0D:194C: 91B8 × (4096 − L) / 4096, FFFFh on an overflow, its low byte stored.</summary>
    private static int StockLogged(int stoich, int stored)
    {
        var product = (uint)(stoich * (ushort)(4096 - stored)) / 4096;
        return (int)(product > 0xFFFF ? 0xFFFF : product) & 0xFF;
    }

    [Fact]
    public void EveryRichTargetOfThePullIsReal()
    {
        var rich = fixture.Passes.Where(p => p.Rich && p.Stored != 0).ToList();
        Assert.True(rich.Count >= 50, $"{rich.Count} rich passes");
        Assert.Contains(rich, p => p.Stored >= 32 * 16);                // rich cells at shown 12.9 or richer are reached
        foreach (var p in rich)
        {
            Assert.True(p.Out == Real(p.Stored), $"at {p.Seconds:F3} s: 91A0 {p.Stored} became {p.Out}, expected {Real(p.Stored)}");
            Assert.True(p.Logged == StockLogged(p.Stoich, p.Stored), $"at {p.Seconds:F3} s: 91B9 {p.Logged}, the stock display {StockLogged(p.Stoich, p.Stored)}");
        }
    }

    [Fact]
    public void WithoutARichTargetThePassIsStock()
    {
        var plain = fixture.Passes.Where(p => !p.Rich).ToList();
        Assert.NotEmpty(plain);
        Assert.All(plain, p => Assert.True(p.Out == p.Stored && p.Logged == StockLogged(p.Stoich, p.Stored),
            $"at {p.Seconds:F3} s: 91A0 {p.Stored} -> {p.Out}, 91B9 {p.Logged}"));
    }
}

/// <summary>
/// V10.2's start fuel: the warm-start drive (coolant 90 °C) on the V10.2 image and on the V10.1 fast-blip bin sent to the
/// owner. The car file's pre-injection is 22.95 ms there, stock X001's 14.35 ms: every cylinder's first pulse is 8.60 ms
/// shorter on V10.2, and nothing after it changes.
/// </summary>
public sealed class StartFuelDriveFixture
{
    public DriveResult V102 { get; }
    public DriveResult V101 { get; }

    public StartFuelDriveFixture()
    {
        var drive = DriveParser.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot, "sim/drives/warm-start.drive")));
        var runs = new[] { V10Fixture.Bytes, TestBins.LiveOverlayV101FastBlipBytes }.AsParallel().AsOrdered().WithDegreeOfParallelism(2)
            .Select(bytes => DriveRun.RunWithChecks(TestImages.AsFlashed(bytes), drive)).ToArray();
        (V102, V101) = (runs[0], runs[1]);
    }
}

public sealed class StartFuelDriveTests(StartFuelDriveFixture fixture) : IClassFixture<StartFuelDriveFixture>
{
    private static List<double> Widths(DriveResult run, int cylinder, int count) =>
        run.Injections.Where(i => i.Cylinder == cylinder).OrderBy(i => i.Open).Take(count).Select(i => (i.Close - i.Open) / 24e3).ToList();

    [Fact]
    public void TheWarmStartPrimesWithTheStockPreInjection()
    {
        for (var cylinder = 1; cylinder <= 6; cylinder++)
        {
            var (v102, v101) = (Widths(fixture.V102, cylinder, 3), Widths(fixture.V101, cylinder, 3));
            Assert.True(v102.Count == 3 && v101.Count == 3, $"cylinder {cylinder}: {v102.Count} and {v101.Count} pulses");
            Assert.True(Math.Abs(v101[0] - v102[0] - 8.60) <= 0.1, $"cylinder {cylinder}: first pulse {v102[0]:F2} ms against {v101[0]:F2} ms");
            for (var n = 1; n < 3; n++)
                Assert.True(Math.Abs(v101[n] - v102[n]) <= 0.05, $"cylinder {cylinder}, pulse {n + 1}: {v102[n]:F2} ms against {v101[n]:F2} ms");
        }
    }

    [Fact]
    public void TheEngineReachesIdleAsOnV101()
    {
        static double Idle(DriveResult run) => run.Trace.Where(r => r.Seconds is > 30 and < 40)
            .Average(r => r.Values[VariableTrace.Columns.ToList().IndexOf("rpm_ecu")]!.Value);
        Assert.InRange(Idle(fixture.V102), Idle(fixture.V101) - 30, Idle(fixture.V101) + 30);
    }

    /// <summary>
    /// V10.2 (spec §6c): the injector flow is the X001 release's 237 cm³/min, not the car file's 225, so at the warm idle
    /// (λ1, the same air and speed on both bins) the injection time without the dead time (ti1_ms, E46A) is 225 / 237 of
    /// V10.1's. Averaged over 10 s: the idle's own fuel correction steps from one firing to the next, and a row can catch
    /// the two bins at different steps (2026-11-08 probe: rows 0.922 to 0.978, most 0.948 to 0.951).
    /// </summary>
    [Fact]
    public void AtIdleTheFuelIsTheStockInjectorsFlow()
    {
        var ti = VariableTrace.Columns.ToList().IndexOf("ti1_ms");
        static List<TraceRow> Idle(DriveResult run) => run.Trace.Where(r => r.Seconds is > 30 and < 40).ToList();
        var (v102, v101) = (Idle(fixture.V102), Idle(fixture.V101));
        Assert.True(v102.Count >= 50 && v101.Count >= 50, $"{v102.Count} and {v101.Count} idle rows");
        var ratio = v102.Average(r => r.Values[ti]!.Value) / v101.Average(r => r.Values[ti]!.Value);
        Assert.True(Math.Abs(ratio / (225.0 / 237) - 1) <= 0.005, $"the idle's ti x {ratio:F4}, expected x {225.0 / 237:F4}");
    }
}
