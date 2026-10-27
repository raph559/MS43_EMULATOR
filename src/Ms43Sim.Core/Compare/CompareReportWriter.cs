using System.Globalization;
using System.Text;

namespace Ms43Sim.Core;

/// <summary>
/// Writes a compare (plan 5 Task 9): compare-report.md (both bins and the drive, both verdicts, the calibration and code
/// differences with their listings, the behaviour differences with their ranges and operating points, and the
/// tolerances), diff.csv (one row per behaviour difference) and each run's own drive files under reference/ and
/// candidate/. A set gets one such folder per drive and a summary.md.
/// </summary>
public static class CompareReportWriter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Design §6.3's advice beside a code diff (plan 5 final review I4).</summary>
    const string Scrutiny = "**Code changes need extra scrutiny** (design §6.3). The compare shows where the two runs part, not "
        + "whether the new code is right: code can act in states no drive reaches, and the checks that read the firmware's RAM "
        + "are not evaluated for a changed program (R7).";

    /// <summary>The drift a code change brings on a drive that loads the engine (plan 5 Task 10's open item; final review I4).</summary>
    const string Drift = "**This drive loads the engine, where any code change drifts two runs apart.** On the car file a "
        + "transparent stub, which changes no value, paired several hundred sparks differently at the rotating cuts (with the "
        + "same totals), the cams up to 2.6° apart for seconds and decided advance up to 2.25° apart on the part-load, "
        + "full-load and overrun drives. Read these differences against a baseline: the same compare with the candidate's "
        + "code made transparent (for an overlay, nothing loaded).";

    public const string DiffHeader = "signal,cylinder,from_s,to_s,count,max_difference,reference,candidate,rpm,maf_kgh,coolant_c,speed_kmh,segment";

    public static void Write(CompareResult r, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "compare-report.md"), Report(r));
        File.WriteAllText(Path.Combine(directory, "diff.csv"), DiffCsv(r.Behaviour));
        File.WriteAllText(Path.Combine(directory, "assessment.json"), AssessmentWriter.Json(r.SoftwareOutputVerdict,
            new[] { r.Reference.Image.Image.Sha256, r.Candidate.Image.Image.Sha256 },
            (r.Reference.Coverage?.NotJudged ?? Array.Empty<string>()).Concat(r.Candidate.Coverage?.NotJudged ?? Array.Empty<string>()).Distinct().ToArray()));
        DriveReportWriter.Write(r.Reference, Path.Combine(directory, "reference"));
        DriveReportWriter.Write(r.Candidate, Path.Combine(directory, "candidate"));
    }

    public static void WriteSet(IReadOnlyList<CompareResult> results, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var r in results)
            Write(r, Path.Combine(directory, r.Drive));
        var worst = results.Select(r => r.Verdict).Aggregate(RunVerdict.Pass, Compare.Worse);
        var s = new StringBuilder();
        s.AppendLine($"# Compare: software/output checks {DriveReportWriter.Word(worst)}").AppendLine();
        AssessmentWriter.AppendPhysicalSafety(s);
        File.WriteAllText(Path.Combine(directory, "assessment.json"), AssessmentWriter.Json(worst,
            results.SelectMany(r => new[] { r.Reference.Image.Image.Sha256, r.Candidate.Image.Image.Sha256 }),
            results.SelectMany(r => (r.Reference.Coverage?.NotJudged ?? Array.Empty<string>()).Concat(r.Candidate.Coverage?.NotJudged ?? Array.Empty<string>())).Distinct().ToArray()));
        if (results.Count > 0)
        {
            s.AppendLine($"Reference: source SHA-256 {results[0].Reference.Image.SourceSha256} ({results[0].Reference.Image.Mode}).");
            s.AppendLine($"Candidate: source SHA-256 {results[0].Candidate.Image.SourceSha256} ({results[0].Candidate.Image.Mode}).").AppendLine();
        }
        s.AppendLine("| Drive | Compare software checks | Reference software checks | Candidate software checks | Calibration | Code | Behaviour |");
        s.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in results)
            s.AppendLine($"| {r.Drive} | {DriveReportWriter.Word(r.Verdict)} | {DriveReportWriter.Word(r.Reference.Verdict)} | {DriveReportWriter.Word(r.Candidate.Verdict)} | {r.Calibration.Count} | {r.Code.Count} | {r.Behaviour.Count} |");
        if (results.Any(r => r.Code.Count > 0))
        {
            s.AppendLine().AppendLine(Scrutiny);
            var drifting = results.Where(Drifts).Select(r => r.Drive).ToList();
            if (drifting.Count > 0)
                s.AppendLine().AppendLine($"These drives load the engine, where any code change drifts two runs apart: {string.Join(", ", drifting)}. "
                    + "Read their behaviour differences against a baseline; each compare-report.md says how.");
        }
        s.AppendLine().AppendLine("Each drive's folder holds its compare-report.md, diff.csv and both runs' own files.");
        File.WriteAllText(Path.Combine(directory, "summary.md"), s.ToString());
    }

    public static string Report(CompareResult r)
    {
        var s = new StringBuilder();
        s.AppendLine($"# Compare {r.Drive}: software/output checks {DriveReportWriter.Word(r.Verdict)}").AppendLine();
        s.AppendLine($"Reference: {r.Reference.Image.Mode}, source SHA-256 {r.Reference.Image.SourceSha256}.");
        s.AppendLine($"Candidate: {r.Candidate.Image.Mode}, source SHA-256 {r.Candidate.Image.SourceSha256}.");
        s.AppendLine($"Drive: {r.Drive}; EEPROM at the first key-on: {r.Reference.Eeprom.Source} (reference), {r.Candidate.Eeprom.Source} (candidate).").AppendLine();

        s.AppendLine("## Software/output-check verdicts").AppendLine();
        s.AppendLine($"- reference: **{DriveReportWriter.Word(r.Reference.Verdict)}**; candidate: **{DriveReportWriter.Word(r.Candidate.Verdict)}**; the compare: **{DriveReportWriter.Word(r.Verdict)}** (the worse of the two: fail beats incomplete beats pass)");
        s.AppendLine("- each run's reasons are in its drive-report.md, under reference/ and candidate/").AppendLine();
        AssessmentWriter.AppendPhysicalSafety(s);

        s.AppendLine("## Calibration differences (0x70000–0x7FFFF, without the checksum fields)").AppendLine();
        s.AppendLine($"Names: {r.CatalogueSource}.").AppendLine();
        if (r.Calibration.Count == 0)
            s.AppendLine("None.").AppendLine();
        else
        {
            s.AppendLine("| Name | Address | Bytes | Changed |");
            s.AppendLine("|---|---|---|---|");
            foreach (var d in r.Calibration)
                s.AppendLine($"| {d.Name} | 0x{d.Address:X5} | {d.Bytes} | {d.Changed} |");
            s.AppendLine();
        }

        s.AppendLine("## Code differences (0x00000–0x6FFFF, without the checksum fields)").AppendLine();
        if (r.Code.Count == 0)
            s.AppendLine("None.").AppendLine();
        else
            s.AppendLine(Scrutiny + " Read each listing below.").AppendLine();
        foreach (var d in r.Code)
        {
            s.AppendLine($"### 0x{d.Start:X5}–0x{d.End - 1:X5} ({d.End - d.Start} bytes)").AppendLine();
            s.AppendLine("```");
            foreach (var line in d.Listing)
                s.AppendLine(line);
            s.AppendLine("```").AppendLine();
        }

        s.AppendLine("## Behaviour differences").AppendLine();
        if (Drifts(r))
            s.AppendLine(Drift).AppendLine();
        if (r.Behaviour.Count == 0)
            s.AppendLine("None: the two runs agree within the tolerances.").AppendLine();
        else
        {
            s.AppendLine("| Signal | Cylinder | From (s) | To (s) | Count | Largest difference | Reference | Candidate | Where (the reference's) |");
            s.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var d in r.Behaviour)
            {
                var at = d.At;
                s.AppendLine($"| {d.Signal} | {d.Cylinder?.ToString(Inv) ?? ""} | {Seconds(d.FromCycle)} | {Seconds(d.ToCycle)} | {d.Count} | {N(d.MaxDifference)} | {N(d.Reference)} | {N(d.Candidate)} | {at.Rpm.ToString("0", Inv)} rpm, {at.MafKgh.ToString("0.#", Inv)} kg/h, {at.CoolantC.ToString("0.#", Inv)} °C, {at.SpeedKmh.ToString("0.#", Inv)} km/h, {at.Segment} |");
            }
            s.AppendLine();
        }

        s.AppendLine("## Tolerances (R6)").AppendLine();
        foreach (var line in r.Tolerances.Describe())
            s.AppendLine($"- {line}");
        return s.ToString();
    }

    /// <summary>One line for the console: the drive, the compare's verdict, the counts, and what a code change asks of the reader.</summary>
    public static string Line(CompareResult r) =>
        $"{r.Drive}: software/output checks {DriveReportWriter.Word(r.Verdict)} ({r.Calibration.Count} calibration, {r.Code.Count} code, {r.Behaviour.Count} behaviour differences)"
        + (r.Code.Count == 0 ? ""
            : Drifts(r) ? "; the code changed: it needs extra scrutiny, and this drive loads the engine, where any code change drifts two runs apart: read its behaviour against a baseline"
            : "; the code changed: it needs extra scrutiny");

    /// <summary>A code change on a drive that loads the engine: its behaviour differences mix with the drift.</summary>
    static bool Drifts(CompareResult r) => r.Code.Count > 0 && Compare.LoadsTheEngine(r.Reference.Drive);

    public static string DiffCsv(IReadOnlyList<BehaviourDifference> differences)
    {
        var s = new StringBuilder(DiffHeader).Append('\n');
        foreach (var d in differences)
        {
            var at = d.At;
            s.Append(string.Join(",", d.Signal, d.Cylinder?.ToString(Inv) ?? "", Seconds(d.FromCycle), Seconds(d.ToCycle),
                d.Count.ToString(Inv), N(d.MaxDifference), N(d.Reference), N(d.Candidate), N(at.Rpm), N(at.MafKgh), N(at.CoolantC),
                N(at.SpeedKmh), Csv(at.Segment))).Append('\n');
        }
        return s.ToString();
    }

    static string Seconds(ulong cycle) => (cycle / 24e6).ToString("0.000", Inv);

    /// <summary>A value: empty where a run had none (NaN).</summary>
    static string N(double value) => double.IsNaN(value) ? "" : value.ToString("0.######", Inv);

    static string Csv(string field) => field.Contains(',') || field.Contains('"') ? $"\"{field.Replace("\"", "\"\"")}\"" : field;
}
