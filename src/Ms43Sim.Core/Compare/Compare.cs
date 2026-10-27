namespace Ms43Sim.Core;

/// <summary>
/// How a compare runs: the EEPROM both runs start from (null: blank, or the drive's own rule in a set), the machine,
/// the tolerances (R6's by default) and the names for the calibration (none by default).
/// </summary>
public sealed record CompareOptions(byte[]? Eeprom = null, MachineOptions? Machine = null, CompareTolerances? Tolerances = null,
    CalibrationCatalogue? Catalogue = null);

/// <summary>
/// Two bins through one drive: both runs with every check, how their images differ (calibration, code) and how their
/// behaviour differs. <see cref="Verdict"/> is the worse of the two runs': fail before incomplete before pass.
/// </summary>
public sealed record CompareResult(string Drive, DriveResult Reference, DriveResult Candidate,
    IReadOnlyList<CalibrationDifference> Calibration, IReadOnlyList<CodeDifference> Code,
    IReadOnlyList<BehaviourDifference> Behaviour, RunVerdict Verdict)
{
    public RunVerdict SoftwareOutputVerdict => Verdict;

    public CalibrationSafetyAssessment PhysicalCalibrationSafety => CalibrationSafetyAssessment.Current;

    /// <summary>The tolerances the behaviour was judged with.</summary>
    public CompareTolerances Tolerances { get; init; } = CompareTolerances.Default;

    /// <summary>Where the calibration names came from.</summary>
    public string CatalogueSource { get; init; } = CalibrationCatalogue.Empty.Source;
}

/// <summary>Two bins through the same drives (plan 5 Task 9).</summary>
public static class Compare
{
    public const string WarmStart = "warm-start";

    /// <summary>
    /// Whether <paramref name="drive"/> loads the engine: a drive segment in gear, at the dyno's speed. There any change of
    /// the program's timing drifts two runs apart, so a code change's behaviour differences mix its own effect with that
    /// drift (plan 5 Task 10's open item; final review I4). A start, an idle, a blip in neutral or a key cycle holds the
    /// engine steady, and two runs agree there.
    /// </summary>
    public static bool LoadsTheEngine(Drive drive)
    {
        var start = 0.0;
        foreach (var segment in drive.Segments)
        {
            if (segment.Mode == DriveMode.Drive && DriveTimeline.At(drive, start).Values["gear"] != 0)
                return true;
            start += segment.Seconds;
        }
        return false;
    }

    /// <summary>Both bins through <paramref name="drive"/>, from the same EEPROM, then what differs.</summary>
    public static CompareResult Run(FlashedImage reference, FlashedImage candidate, Drive drive, CompareOptions? options = null)
    {
        options ??= new CompareOptions();
        var run = new DriveRunOptions(Machine: options.Machine, Eeprom: options.Eeprom);
        return Of(reference, candidate, drive, DriveRun.RunWithChecks(reference, drive, run), DriveRun.RunWithChecks(candidate, drive, run), options);
    }

    /// <summary>What differs between two runs already made of <paramref name="drive"/>, one on each bin, from the same EEPROM.</summary>
    public static CompareResult Of(FlashedImage reference, FlashedImage candidate, Drive drive, DriveResult r, DriveResult c,
        CompareOptions? options = null)
    {
        options ??= new CompareOptions();
        var tolerances = options.Tolerances ?? CompareTolerances.Default;
        var catalogue = options.Catalogue ?? CalibrationCatalogue.Empty;
        return new CompareResult(drive.Name, r, c, CalibrationDiff.Of(reference.Image, candidate.Image, catalogue),
            CodeDiff.Of(reference.Image, candidate.Image), BehaviourDiff.Of(RunTrack.From(r), RunTrack.From(c), drive, tolerances),
            Worse(r.Verdict, c.Verdict))
        {
            Tolerances = tolerances,
            CatalogueSource = catalogue.Source,
        };
    }

    /// <summary>
    /// A set of drives (C6): the warm-start drive first, on both bins from a blank EEPROM; then the others,
    /// <paramref name="jobs"/> at a time, each seasoned one on both bins from the reference's warm-start EEPROM, the rest
    /// from a blank one. The results come in that order. A seasoned drive needs the warm-start drive in the set.
    /// </summary>
    public static IReadOnlyList<CompareResult> RunSet(FlashedImage reference, FlashedImage candidate, IReadOnlyList<Drive> drives,
        CompareOptions? options, int jobs)
    {
        options ??= new CompareOptions();
        var warm = drives.FirstOrDefault(d => d.Name == WarmStart);
        if (warm is null && drives.Any(d => d.Start == StartEeprom.Seasoned))
            throw new ArgumentException("a seasoned drive needs the warm-start drive in the set: the reference's final EEPROM there is where it starts", nameof(drives));
        var results = new List<CompareResult>();
        byte[]? seasoned = null;
        if (warm is not null)
        {
            var first = Run(reference, candidate, warm, options with { Eeprom = null });
            seasoned = first.Reference.Eeprom.FinalImage;
            results.Add(first);
        }
        var rest = drives.Where(d => d != warm).ToArray();
        var outcomes = new CompareResult[rest.Length];
        Parallel.For(0, rest.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, i =>
            outcomes[i] = Run(reference, candidate, rest[i], options with { Eeprom = rest[i].Start == StartEeprom.Seasoned ? seasoned : null }));
        results.AddRange(outcomes);
        return results;
    }

    /// <summary>Fail before incomplete before pass.</summary>
    public static RunVerdict Worse(RunVerdict a, RunVerdict b) =>
        a == RunVerdict.Fail || b == RunVerdict.Fail ? RunVerdict.Fail
        : a == RunVerdict.Incomplete || b == RunVerdict.Incomplete ? RunVerdict.Incomplete
        : RunVerdict.Pass;
}
