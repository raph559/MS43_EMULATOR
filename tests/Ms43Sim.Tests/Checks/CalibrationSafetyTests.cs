using System.Text.Json;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Checks;

public sealed class CalibrationSafetyRuns
{
    public const string DriveText = "drive calibration-scope\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n"
        + "3 on\n1.5 crank rpm=200\n3 idle\n4 drive gear=3 speed=105 rpm=4000 pedal=95\n";

    public static FlashedImage ExtremeIgnition()
    {
        var bytes = TestBins.CarBytes;
        Array.Fill(bytes, (byte)255, 0x77315, 320); // The documented ignition scaling makes this 72 degrees.
        return TestImages.AsFlashed(bytes);
    }

    public CalibrationSafetyRuns()
    {
        var drive = DriveParser.Parse(DriveText);
        var results = new[] { TestImages.Car, ExtremeIgnition() }.AsParallel().AsOrdered()
            .Select(image => DriveRun.RunWithChecks(image, drive)).ToArray();
        (Normal, Extreme) = (results[0], results[1]);
    }

    public DriveResult Normal { get; }
    public DriveResult Extreme { get; }
}

public sealed class CalibrationSafetyTests(CalibrationSafetyRuns runs) : IClassFixture<CalibrationSafetyRuns>
{
    static void NoPhysicalClearance(CalibrationSafetyAssessment assessment)
    {
        Assert.Equal(CalibrationSafetyStatus.NotEvaluated, assessment.Status);
        Assert.False(assessment.EngineSafeClearance);
        Assert.Contains(assessment.MissingModels, m => m.Id == "knock_and_cylinder_pressure");
        Assert.Contains(assessment.MissingModels, m => m.Id == "mixture_and_fuel_delivery");
        Assert.Contains(assessment.MissingModels, m => m.Id == "thermal_response");
    }

    static void NoPhysicalClearance(JsonElement assessment)
    {
        var physical = assessment.GetProperty("physicalCalibrationSafety");
        Assert.Equal("NotEvaluated", physical.GetProperty("status").GetString());
        Assert.False(physical.GetProperty("engineSafeClearance").GetBoolean());
        Assert.NotEmpty(physical.GetProperty("missingModels").EnumerateArray());
    }

    [Fact]
    public void SuccessfulSoftwareChecks_DoNotIssueEngineSafetyClearance()
    {
        Assert.Equal(RunVerdict.Pass, runs.Normal.SoftwareOutputVerdict);
        Assert.Equal(runs.Normal.Verdict, runs.Normal.SoftwareOutputVerdict);
        NoPhysicalClearance(runs.Normal.PhysicalCalibrationSafety);
        using var json = JsonDocument.Parse(AssessmentWriter.Json(runs.Normal.SoftwareOutputVerdict,
            new[] { runs.Normal.Image.Image.Sha256 }, runs.Normal.Coverage?.NotJudged));
        Assert.Equal("pass", json.RootElement.GetProperty("softwareOutputChecks").GetProperty("verdict").GetString());
        NoPhysicalClearance(json.RootElement);
    }

    [Fact]
    public void ExtremeStaticIgnition_CanPassOutputChecks_ButCannotBeReportedEngineSafe()
    {
        Assert.Equal(RunVerdict.Pass, runs.Extreme.SoftwareOutputVerdict);
        Assert.Contains(runs.Extreme.Sparks, spark => spark.Main && spark.DecidedAdvance > 70);
        NoPhysicalClearance(runs.Extreme.PhysicalCalibrationSafety);
        var report = DriveReportWriter.Report(runs.Extreme);
        Assert.Contains("software/output checks pass", report);
        Assert.Contains("**NotEvaluated.**", report);
        Assert.Contains("no engine-safe clearance", report);
        using var directory = new TempDirectory();
        DriveReportWriter.Write(runs.Extreme, directory.Path);
        using var json = JsonDocument.Parse(File.ReadAllText(directory.File("assessment.json")));
        NoPhysicalClearance(json.RootElement);
        Assert.Equal("pass", File.ReadAllText(directory.File("verdict.txt")).Trim());
    }

    [Fact]
    public void Cli_SeparatesTheKnownExtremeFalsePassFromPhysicalSafety()
    {
        using var directory = new TempDirectory();
        File.WriteAllBytes(directory.File("extreme.bin"), runs.Extreme.Image.Image.ToArray());
        File.WriteAllText(directory.File("test.drive"), CalibrationSafetyRuns.DriveText);
        var output = new StringWriter();
        var code = Ms43Sim.Cli.Program.Run(new[] { "run", directory.File("extreme.bin"), "--raw", "--drive", directory.File("test.drive"),
            "--out", directory.File("out") }, output, TextWriter.Null);
        Assert.Equal(0, code);
        Assert.Contains("software/output checks: pass", output.ToString());
        Assert.Contains(AssessmentWriter.PhysicalSummary, output.ToString());
        using var json = JsonDocument.Parse(File.ReadAllText(directory.File("out/assessment.json")));
        NoPhysicalClearance(json.RootElement);
    }

    [Fact]
    public void StartupOutcome_IsNotACompletedSoftwareOrPhysicalSafetyAssessment()
    {
        var result = StartupRun.Run(TestImages.Car, new StartupRunOptions(Seconds: 0.3));
        NoPhysicalClearance(result.PhysicalCalibrationSafety);
        using var directory = new TempDirectory();
        RunReportWriter.Write(result, directory.Path);
        using var json = JsonDocument.Parse(File.ReadAllText(directory.File("assessment.json")));
        Assert.Equal("not_run", json.RootElement.GetProperty("softwareOutputChecks").GetProperty("verdict").GetString());
        NoPhysicalClearance(json.RootElement);
        Assert.Contains("**NotEvaluated.**", File.ReadAllText(directory.File("run-report.md")));
    }

    [Fact]
    public void CompareAndSetSummary_KeepTheTwoAssessmentsSeparate()
    {
        var result = Core.Compare.Of(runs.Normal.Image, runs.Extreme.Image, runs.Normal.Drive, runs.Normal, runs.Extreme);
        Assert.Equal(RunVerdict.Pass, result.SoftwareOutputVerdict);
        NoPhysicalClearance(result.PhysicalCalibrationSafety);
        using var directory = new TempDirectory();
        CompareReportWriter.WriteSet(new[] { result }, directory.Path);
        Assert.Contains("**NotEvaluated.**", File.ReadAllText(directory.File("summary.md")));
        foreach (var path in new[] { "assessment.json", "calibration-scope/assessment.json", "calibration-scope/reference/assessment.json", "calibration-scope/candidate/assessment.json" })
        {
            using var json = JsonDocument.Parse(File.ReadAllText(directory.File(path)));
            NoPhysicalClearance(json.RootElement);
        }
    }

    [Fact]
    public void NewAssessmentOutput_CannotOverwriteAnInputImage()
    {
        using var directory = new TempDirectory();
        var bytes = TestBins.CarBytes;
        File.WriteAllBytes(directory.File("assessment.json"), bytes);
        File.WriteAllText(directory.File("test.drive"), CalibrationSafetyRuns.DriveText);
        var error = new StringWriter();
        var code = Ms43Sim.Cli.Program.Run(new[] { "run", directory.File("assessment.json"), "--drive", directory.File("test.drive"), "--out", directory.Path },
            TextWriter.Null, error);
        Assert.Equal(2, code);
        Assert.Contains("assessment.json", error.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(directory.File("assessment.json")));
    }
}
