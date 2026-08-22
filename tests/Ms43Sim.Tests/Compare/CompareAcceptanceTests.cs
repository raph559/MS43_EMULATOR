using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Compare;

/// <summary>
/// The compare on the standard drives (plan 5 Task 10), each bin run once, in parallel as <see cref="Startup.KnownGood"/>
/// does: the car file twice, the R8 transparent stub, and, on the full-load drive, an ignition edit and the release. The
/// car is the reference throughout, and every seasoned drive starts from the car's warm-start EEPROM (C6).
/// </summary>
public sealed class CompareAcceptance
{
    /// <summary>R8: the RON98 ignition read at 0x2C650 calls a stub in free flash at 0x4B544 that makes the same call and returns.</summary>
    public static readonly (int Offset, byte[] New)[] Stub =
        { (0x2C650, new byte[] { 0xDA, 0x0C, 0x44, 0xB5 }), (0x4B544, new byte[] { 0xDA, 0x0D, 0x9A, 0x02, 0xDB, 0x00 }) };

    /// <summary>ip_iga_ron98_pl__n__maf: 20 rpm rows (800 to 6990) by 16 load columns of u8, 0.375° a step.</summary>
    public const int Map = 0x77315, Columns = 16;

    /// <summary>The edited block: the rows at 4000, 4250, 4500 and 4750 rpm, by the four top loads.</summary>
    public static readonly int[] Rows = { 10, 11, 12, 13 }, LoadColumns = { 12, 13, 14, 15 };

    public CompareAcceptance()
    {
        Drives = StandardDrives.All.ToDictionary(name => name, StandardDrives.Load);
        Catalogue = CalibrationCatalogue.Load(CalibrationCatalogue.FindUpwards("calmaps_ms43x.csv", AppContext.BaseDirectory)!);
        var ignition = TestBins.CarBytes;
        foreach (var row in Rows)
            foreach (var column in LoadColumns)
                ignition[Map + row * Columns + column] += 3;              // 1.125°
        Images = new Dictionary<string, FlashedImage>
        {
            ["car"] = TestImages.Car,
            ["car again"] = TestImages.Car,
            ["stub"] = InMemoryBin.With(TestBins.CarBytes, Stub),
            ["ignition"] = TestImages.AsFlashed(ignition),
            ["release"] = TestImages.AsFlashed(TestBins.ReleaseBytes),
        };

        var sets = new[] { "car", "car again", "stub" };
        var runs = sets.AsParallel().Select(bin => (Key: (bin, "warm-start"), Run: DriveRun.RunWithChecks(Images[bin], Drives["warm-start"])))
            .ToDictionary(p => p.Key, p => p.Run);
        var seasoned = runs[("car", "warm-start")].Eeprom.FinalImage;
        var rest = sets.SelectMany(bin => StandardDrives.All.Skip(1).Select(drive => (bin, drive)))
            .Append(("ignition", "full-load")).Append(("release", "full-load"));
        foreach (var (key, run) in rest.AsParallel().Select(key => (key, DriveRun.RunWithChecks(Images[key.Item1], Drives[key.Item2],
                     new DriveRunOptions(Eeprom: Drives[key.Item2].Start == StartEeprom.Seasoned ? seasoned : null)))).ToList())
            runs[key] = run;
        Runs = runs;
    }

    public IReadOnlyDictionary<string, Drive> Drives { get; }

    public CalibrationCatalogue Catalogue { get; }

    public IReadOnlyDictionary<string, FlashedImage> Images { get; }

    public IReadOnlyDictionary<(string Bin, string Drive), DriveResult> Runs { get; }

    /// <summary>The car against <paramref name="candidate"/> on <paramref name="drive"/>, from the runs already made.</summary>
    public CompareResult Against(string candidate, string drive) =>
        Core.Compare.Of(Images["car"], Images[candidate], Drives[drive], Runs[("car", drive)], Runs[(candidate, drive)], new CompareOptions(Catalogue: Catalogue));
}

[Trait("Category", "StandardDrives")]
public class CompareAcceptanceTests(CompareAcceptance f) : IClassFixture<CompareAcceptance>
{
    static string Describe(IEnumerable<BehaviourDifference> differences) => string.Join("; ", differences.Take(8).Select(d =>
        $"{d.Signal}{(d.Cylinder is { } c ? $" cylinder {c}" : "")} {d.FromCycle / 24e6:0.000}–{d.ToCycle / 24e6:0.000} s ×{d.Count}: {d.Reference} → {d.Candidate} at {d.At.Rpm:0} rpm, {d.At.Segment}"));

    [Fact] public void CarAgainstItself_HasNoDifferenceOnAnyStandardDrive()
    {
        var found = StandardDrives.All.Select(drive => (drive, r: f.Against("car again", drive)))
            .Where(p => p.r.Calibration.Count + p.r.Code.Count + p.r.Behaviour.Count > 0).Select(p => $"{p.drive}: {Describe(p.r.Behaviour)}").ToList();
        Assert.True(found.Count == 0, string.Join(" | ", found));
    }

    /// <summary>The drives that hold the engine steady: a start, an idle, a key cycle.</summary>
    static readonly string[] Steady = { "warm-start", "cold-start", "idle", "key-cycle" };

    [Fact] public void TransparentStub_ChangesNoDecision()
    {
        // On the steady drives the stub's runs agree with the car's within the tolerances. On part-load, full-load and overrun
        // any change of the program's timing drifts the runs apart (rotating cut patterns change phase, the VANOS and the
        // throttle settle differently): a stub on the main loop's background call, far from the ignition, drifts them as
        // far (plan 5 record). There the stub still changes nothing a run keeps: no fault, no power cycle, no EEPROM byte.
        var found = new List<string>();
        foreach (var drive in StandardDrives.All)
        {
            var r = f.Against("stub", drive);
            Assert.Equal(new[] { (0x2C651u, 0x2C654u), (0x4B544u, 0x4B54Au) }, r.Code.Select(d => (d.Start, d.End)));
            Assert.Empty(r.Calibration);
            Assert.Equal(RunVerdict.Pass, r.Reference.Verdict);
            Assert.Equal(RunVerdict.Incomplete, r.Candidate.Verdict);   // R7: a code patch is not MS43X001, so the RAM checks are not evaluated
            var kept = r.Behaviour.Where(d => d.Signal is "faults" or "power_cycles" or "eeprom").ToList();
            if ((Steady.Contains(drive) ? r.Behaviour : kept).Count > 0)
                found.Add($"{drive}: {Describe(Steady.Contains(drive) ? r.Behaviour : kept)}");
        }
        Assert.True(found.Count == 0, string.Join(" | ", found));
    }

    const double Step = 0.375;                                          // one step of the ignition maps

    [Fact] public void IgnitionEdit_ChangesTheSparkOnlyWhereItsCellsAreRead()
    {
        // The block's cells reach from the row below it to the row above (3750 to 5000 rpm) as the map interpolates; the
        // pull and 4th gear at 95 % pedal read the top loads. The octane blend scales the RON98 map by its learned factor,
        // so the edit moves a decision by 3 steps (1.125°) at most; measured against the reference at the same event, a
        // decision on a breakpoint can also sit one step (0.375°) apart. Elsewhere the drift any change brings (the stub's
        // on this drive) moves a decision by one step at most, as the idle controller settles after a pull with more advance.
        var r = f.Against("ignition", "full-load");
        Assert.Contains(r.Calibration, d => d.Name == "ip_iga_ron98_pl__n__maf" && d.Changed == 16);
        var drive = f.Drives["full-load"];
        var rpm = VariableTrace.Columns.ToList().IndexOf("rpm_fed");
        double RpmAt(ulong cycle) => r.Reference.Trace.MinBy(t => Math.Abs(t.Seconds - cycle / 24e6))!.Values[rpm] ?? 0;
        bool InTheBlock(BehaviourDifference d) => new[] { d.FromCycle, d.ToCycle }.All(cycle =>
            RpmAt(cycle) is >= 3750 - 100 and <= 5000 + 100 && DriveTimeline.At(drive, cycle / 24e6).Values["pedal"] >= 90);

        var decided = r.Behaviour.Where(d => d.Signal == "spark.decided_advance").ToList();
        var edit = decided.Where(d => d.MaxDifference > Step + 1e-6).ToList();
        Assert.All(edit, d =>
        {
            Assert.True(InTheBlock(d), $"{Describe(new[] { d })}: outside the block, or the pedal was not down");
            Assert.True(d.Candidate > d.Reference, Describe(new[] { d }));
            Assert.InRange(d.MaxDifference, 3 * Step - 1e-6, 4 * Step + 1e-6);
        });
        Assert.All(Enumerable.Range(1, 6), cylinder =>
            Assert.True(edit.Where(d => d.Cylinder == cylinder).Sum(d => d.Count) >= 50, $"cylinder {cylinder}: {Describe(edit)}"));
        Assert.All(decided.Except(edit), d => Assert.True(d.MaxDifference <= Step + 1e-6, Describe(new[] { d })));

        // As fired (plan 5 final review I6): in the block, the edit and the dwell's fall within the 0.1° tolerance; elsewhere
        // no more than the drift the stub's code change brings on this drive (the gear change's cut, the pull's top).
        var asFired = r.Behaviour.Where(d => d.Signal == "spark.advance").ToList();
        var inBlock = asFired.Where(InTheBlock).ToList();
        Assert.All(inBlock, d =>
        {
            Assert.True(d.Candidate > d.Reference, Describe(new[] { d }));
            Assert.InRange(d.MaxDifference, 3 * Step - 0.1, 4 * Step + 0.1);
        });
        Assert.All(Enumerable.Range(1, 6), cylinder =>
            Assert.True(inBlock.Where(d => d.Cylinder == cylinder).Sum(d => d.Count) >= 50, $"cylinder {cylinder}: {Describe(inBlock)}"));
        var drift = f.Against("stub", "full-load").Behaviour.Where(d => d.Signal == "spark.advance").Max(d => d.MaxDifference);
        Assert.All(asFired.Except(inBlock), d => Assert.True(d.MaxDifference <= drift, $"{Describe(new[] { d })}: beyond the stub's {drift}°"));
    }

    /// <summary>The RON98 ignition map's 16 cells (°) at <paramref name="rpm"/> along its rpm axis (a count, then u16 breakpoints), clamped at its ends.</summary>
    static double[] IgnitionRow(byte[] bin, double rpm)
    {
        int U16(int offset) => bin[offset] | bin[offset + 1] << 8;
        var axis = Enumerable.Range(1, U16(0x7501A)).Select(i => (double)U16(0x7501A + 2 * i)).ToArray();
        double Cell(int row, int column) => 0.375 * bin[CompareAcceptance.Map + row * CompareAcceptance.Columns + column] - 23.625;
        var below = Array.FindLastIndex(axis, x => x <= rpm);
        return Enumerable.Range(0, CompareAcceptance.Columns).Select(c =>
            below < 0 ? Cell(0, c)
            : below >= axis.Length - 1 ? Cell(axis.Length - 1, c)
            : Cell(below, c) + (rpm - axis[below]) / (axis[below + 1] - axis[below]) * (Cell(below + 1, c) - Cell(below, c))).ToArray();
    }

    /// <summary>
    /// Why <paramref name="r"/> does not show the release's tune in full-load's steady 6,900 rpm segment (line 10, 18.5 to
    /// 20.5 s), or null where it does: on every cylinder a decided-advance difference over the segment, each run's decisions
    /// there (from 19 s, settled) within its own RON98 row at 6,900 rpm and one step, and at least 16 steps apart. The release
    /// is past its own rev limit there (its rpm axis ends at 6,400): it closes the throttle and reads low-load cells.
    /// </summary>
    static string? TuneAt6900(CompareResult r)
    {
        const ulong From = 456_000_000, Settled = 456_000_000 + 12_000_000, To = 492_000_000;    // 19.0 s, 19.5 s, 20.5 s
        var rows = (Reference: IgnitionRow(TestBins.CarBytes, 6900), Candidate: IgnitionRow(TestBins.ReleaseBytes, 6900));
        foreach (var cylinder in Enumerable.Range(1, 6))
        {
            if (!r.Behaviour.Any(d => d.Signal == "spark.decided_advance" && d.Cylinder == cylinder && d.FromCycle <= To && d.ToCycle >= From))
                return $"cylinder {cylinder}: no decided-advance difference at 6,900 rpm";
            double Decided(DriveResult run) =>
                run.Sparks.Where(s => s.Cylinder == cylinder && s.Fire >= Settled && s.Fire < To).Select(s => s.DecidedAdvance).DefaultIfEmpty(double.NaN).Average();
            var (reference, candidate) = (Decided(r.Reference), Decided(r.Candidate));
            if (!(reference >= rows.Reference.Min() - Step - 1e-6 && reference <= rows.Reference.Max() + Step + 1e-6))
                return $"cylinder {cylinder}: the reference decided {reference}°, not from the car's row";
            if (!(candidate >= rows.Candidate.Min() - Step - 1e-6 && candidate <= rows.Candidate.Max() + Step + 1e-6))
                return $"cylinder {cylinder}: the candidate decided {candidate}°, not from the release's row";
            if (candidate - reference < 16 * Step)
                return $"cylinder {cylinder}: {reference}° against {candidate}°, under 16 steps apart";
        }
        return null;
    }

    [Fact] public void CarAgainstRelease_ShowsTheTune()
    {
        // The car is the reference and the release the candidate: the tune seen from the car's side.
        var r = f.Against("release", "full-load");
        var names = r.Calibration.Select(d => d.Name).ToList();
        Assert.Contains("ip_iga_ron98_pl__n__maf", names);
        Assert.Contains("ip_af_target_ron98__maf__n", names);
        Assert.Null(TuneAt6900(r));
        Assert.NotNull(TuneAt6900(f.Against("stub", "full-load")));          // plan 5 final review I6: drift alone does not pass it
    }
}
