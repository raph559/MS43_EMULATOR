using System.Security.Cryptography;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Compare;

/// <summary>Two bins through the same drive (plan 5 Task 9): the car file against itself and against edits of it, as flashed.</summary>
public class CompareTests
{
    const string Warm = "start blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n";

    static readonly Drive Short = DriveParser.Parse("drive short\n" + Warm + "3 on\n1.5 crank rpm=200\n3 idle\n");

    static readonly Lazy<CalibrationCatalogue> Catalogue = new(() =>
        CalibrationCatalogue.Load(CalibrationCatalogue.FindUpwards("calmaps_ms43x.csv", AppContext.BaseDirectory)!));

    static CompareOptions Options => new(Catalogue: Catalogue.Value);

    /// <summary>The car file with its program sector 0x60000–0x6FFFF erased: it never runs the engine and keeps the relay after a key-off.</summary>
    static FlashedImage Erased()
    {
        var bytes = TestBins.CarBytes;
        Array.Fill(bytes, (byte)0xFF, 0x60000, 0x10000);
        return TestImages.AsFlashed(bytes);
    }

    /// <summary>The car file with every cell of the RON98 idle ignition map (0x775D4, 36 cells) 4 steps (1.5°) higher.</summary>
    static FlashedImage MoreIdleAdvance()
    {
        var bytes = TestBins.CarBytes;
        for (var i = 0x775D4; i < 0x775D4 + 36; i++)
            bytes[i] += 4;
        return TestImages.AsFlashed(bytes);
    }

    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact] public void Compare_SameBin_HasNoDifference_AndItsOwnVerdict()
    {
        var r = Core.Compare.Run(TestImages.Car, TestImages.Car, Short, Options);
        Assert.Equal("short", r.Drive);
        Assert.Empty(r.Calibration);
        Assert.Empty(r.Code);
        Assert.Empty(r.Behaviour);
        Assert.Equal((r.Reference.Verdict, r.Reference.Verdict), (r.Candidate.Verdict, r.Verdict));
    }

    [Fact] public void DifferentPowerCycles_AreReported()
    {
        var drive = DriveParser.Parse("drive keyed\n" + Warm + "3 on\n1.5 crank rpm=200\n3 idle\n15 off\n3 on\n");
        var r = Core.Compare.Run(TestImages.Car, Erased(), drive, Options);
        var cycles = Assert.Single(r.Behaviour, d => d.Signal == "power_cycles");
        Assert.Equal((2.0, 1.0), (cycles.Reference, cycles.Candidate));
        Assert.Contains(r.Behaviour, d => d.Signal == "spark.missing");
        Assert.NotEmpty(r.Code);
        Assert.Equal(RunVerdict.Fail, r.Candidate.Verdict);
        Assert.Equal(RunVerdict.Fail, r.Verdict);                      // the worse of the two
    }

    [Fact] public void Compare_WritesTheReportAndTheDiff()
    {
        var r = Core.Compare.Run(TestImages.Car, MoreIdleAdvance(), Short, Options);
        Assert.Contains(r.Calibration, d => d.Name == "ip_iga_ron98_is__n__maf" && d.Changed == 36);
        Assert.Contains(r.Behaviour, d => d.Signal == "spark.decided_advance");
        using var dir = new TempDirectory();
        CompareReportWriter.Write(r, dir.Path);
        var report = File.ReadAllText(dir.File("compare-report.md"));
        foreach (var expected in new[] { "# Compare short", r.Reference.Image.SourceSha256, r.Candidate.Image.SourceSha256,
                     "ip_iga_ron98_is__n__maf", "spark.decided_advance", "## Tolerances", "spark.advance (as fired): 0.1°" })
            Assert.Contains(expected, report);
        var diff = File.ReadAllLines(dir.File("diff.csv"));
        Assert.Equal("signal,cylinder,from_s,to_s,count,max_difference,reference,candidate,rpm,maf_kgh,coolant_c,speed_kmh,segment", diff[0]);
        Assert.Equal(r.Behaviour.Count + 1, diff.Length);
        Assert.Contains(diff, l => l.StartsWith("spark.decided_advance,", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(dir.Path, "reference", "drive-report.md")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "candidate", "drive-report.md")));
    }

    [Fact] public void CompareSet_StartsBothBinsFromTheReferencesEeprom()
    {
        // C6: the warm-start drive on both bins from a blank EEPROM, every seasoned drive of both from the reference's
        // warm-start EEPROM. The erased candidate never writes its own, so starting from it would show.
        var warm = DriveParser.Parse("drive warm-start\n" + Warm + "3 on\n1.5 crank rpm=200\n5 idle\n15 off\n");
        var seasoned = DriveParser.Parse("drive seasoned\nstart seasoned\nset coolant=90 oil=90 intake=33 ambient=20\n3 on\n1.5 crank rpm=200\n3 idle\n");
        var results = Core.Compare.RunSet(TestImages.Car, Erased(), new[] { seasoned, warm }, Options, jobs: 1);
        Assert.Equal(new[] { "warm-start", "seasoned" }, results.Select(r => r.Drive));
        var (w, s) = (results[0], results[1]);
        Assert.Equal(("blank", "blank"), (w.Reference.Eeprom.Source, w.Candidate.Eeprom.Source));
        var referenceWarm = Sha(w.Reference.Eeprom.FinalImage);
        Assert.NotEqual(referenceWarm, Sha(w.Candidate.Eeprom.FinalImage));
        Assert.Equal((referenceWarm, referenceWarm), (s.Reference.Eeprom.Source, s.Candidate.Eeprom.Source));
    }

    [Fact] public void CompareSet_WritesAFolderPerDrive_AndASummary()
    {
        var results = new[] { Core.Compare.Run(TestImages.Car, TestImages.Car, Short, Options) };
        using var dir = new TempDirectory();
        CompareReportWriter.WriteSet(results, dir.Path);
        Assert.True(File.Exists(Path.Combine(dir.Path, "short", "compare-report.md")));
        var summary = File.ReadAllText(dir.File("summary.md"));
        Assert.Contains("| short |", summary);
        Assert.Contains(DriveReportWriter.Word(results[0].Verdict), summary);
    }
}
