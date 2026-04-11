using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cli;

public class CliTests
{
    [Fact] public void Boot_WrongSizeFile_ExitsWithCode3()
    {
        using var dir = new TempDirectory();
        var path = dir.File("short.bin"); File.WriteAllBytes(path, new byte[0x10000]);
        var err = new StringWriter();
        Assert.Equal(3, Ms43Sim.Cli.Program.Run(new[] { "boot", path }, TextWriter.Null, err));
        Assert.Contains("524288", err.ToString());
    }

    [Fact] public void Boot_Car_WritesReportAndInventory()
    {
        var bin = Path.Combine(TestBins.Root, "bins", "CAR_current_43X001_SP95_STAGE1.bin");
        using var dir = new TempDirectory();
        Assert.Equal(0, Ms43Sim.Cli.Program.Run(new[] { "boot", bin, "--stop-at-handover", "--max-cycles", "2000000", "--out", dir.Path }, TextWriter.Null, TextWriter.Null));
        var md = File.ReadAllText(dir.File("boot-report.md"));
        Assert.Contains("HandedOver", md);
        Assert.Contains("0E:366A", md);
        Assert.True(File.Exists(dir.File("inventory.csv")));
    }

    [Fact] public void Boot_EcuBootWithPartialOnto_IsAUsageError()
    {
        var bin = Path.Combine(TestBins.Root, "bins", "CAR_current_43X001_SP95_STAGE1.bin");
        var err = new StringWriter();
        Assert.Equal(2, Ms43Sim.Cli.Program.Run(new[] { "boot", bin, "--partial-onto", bin, "--ecu-boot", bin }, TextWriter.Null, err));
        Assert.Contains("--ecu-boot", err.ToString());
    }
}
