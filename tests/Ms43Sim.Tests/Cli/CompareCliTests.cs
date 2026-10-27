using Ms43Sim.Cli;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using System.Text.Json;

namespace Ms43Sim.Tests.Cli;

/// <summary><c>ms43sim compare</c> (plan 5 Task 9).</summary>
public class CompareCliTests
{
    static (int Code, string Output, string Error) Run(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var code = Program.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("one bin", "compare needs a REF and a CANDIDATE", "CAR")]
    [InlineData("three bins", "unexpected argument", "CAR", "CAR", "CAR")]
    [InlineData("both kinds of drive", "--drive cannot be combined with --drives", "CAR", "CAR", "--drive", "x.drive", "--drives", "dir")]
    [InlineData("an unknown option", "unexpected argument '--bogus'", "CAR", "CAR", "--bogus")]
    [InlineData("--eeprom with a set", "--eeprom goes with --drive", "CAR", "CAR", "--drives", "dir", "--eeprom", "e.bin")]
    public void Compare_UsageMistakes_ExitWith2(string what, string message, params string[] args)
    {
        var (code, _, error) = Run(args.Select(a => a == "CAR" ? TestImages.CarPath : a).Prepend("compare").ToArray());
        Assert.True(code == 2, $"{what}: exit {code}");
        Assert.Contains(message, error);
    }

    [Fact] public void Compare_OneDrive_WritesItsFolder_AndExitsWithTheVerdict()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("short.drive"),
            "drive short\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n3 on\n1.5 crank rpm=200\n3 idle\n");
        var outDir = Path.Combine(dir.Path, "out");
        var (code, output, _) = Run("compare", TestImages.CarPath, TestImages.CarPath, "--drive", dir.File("short.drive"), "--out", outDir);
        var verdict = File.ReadAllText(Path.Combine(outDir, "reference", "verdict.txt")).Trim();
        Assert.Equal(verdict switch { "pass" => 0, "fail" => 1, _ => 4 }, code);
        Assert.Contains("short:", output);
        Assert.Contains(AssessmentWriter.PhysicalSummary, output);
        using var assessment = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "assessment.json")));
        Assert.Equal("NotEvaluated", assessment.RootElement.GetProperty("physicalCalibrationSafety").GetProperty("status").GetString());
        Assert.False(assessment.RootElement.GetProperty("physicalCalibrationSafety").GetProperty("engineSafeClearance").GetBoolean());
        Assert.True(File.Exists(Path.Combine(outDir, "compare-report.md")));
        Assert.Single(File.ReadAllLines(Path.Combine(outDir, "diff.csv")));     // the header: the same bin twice
    }
}
