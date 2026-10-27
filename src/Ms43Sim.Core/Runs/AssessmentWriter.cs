using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ms43Sim.Core;

public static class AssessmentWriter
{
    public const string Scope = "software execution and modeled output checks";
    public const string PhysicalSummary = "physical calibration safety: NotEvaluated; no engine-safe clearance";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Json(RunVerdict? softwareVerdict, IEnumerable<string> imageSha256,
        IReadOnlyList<string>? notJudged = null) =>
        JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            ImageSha256 = imageSha256.Distinct().ToArray(),
            SoftwareOutputChecks = new
            {
                Verdict = softwareVerdict is { } verdict ? DriveReportWriter.Word(verdict) : "not_run",
                ExitCode = softwareVerdict is { } code ? (int?)code : null,
                Scope,
                NotJudged = notJudged ?? Array.Empty<string>(),
            },
            PhysicalCalibrationSafety = CalibrationSafetyAssessment.Current,
        }, JsonOptions) + "\n";

    public static void AppendPhysicalSafety(StringBuilder report)
    {
        report.AppendLine("## Physical calibration safety").AppendLine();
        report.AppendLine("**NotEvaluated.** This simulator provides no engine-safe clearance, including when every software/output check passes.").AppendLine();
        foreach (var missing in CalibrationSafetyAssessment.Current.MissingModels)
            report.AppendLine($"- `{missing.Id}`: {missing.Description}");
        report.AppendLine();
    }
}
