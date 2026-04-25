using Ms43Sim.Cli;
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

    static string CarPath => TestImages.CarPath;

    [Fact] public void Run_WritesReportInventoryAndEeprom()
    {
        using var dir = new TempDirectory();
        Assert.Equal(0, Program.Run(new[] { "run", CarPath, "--seconds", "0.3", "--out", dir.Path }, TextWriter.Null, TextWriter.Null));
        Assert.True(File.Exists(dir.File("run-report.md")) && File.Exists(dir.File("inventory.csv")));
        Assert.Equal(2048, new FileInfo(dir.File("eeprom.bin")).Length);
    }

    [Fact] public void Run_EepromOfTheWrongSize_ExitsWithCode3()
    {
        using var dir = new TempDirectory();
        File.WriteAllBytes(dir.File("small.bin"), new byte[1024]);
        var err = new StringWriter();
        Assert.Equal(3, Program.Run(new[] { "run", CarPath, "--eeprom", dir.File("small.bin"), "--out", dir.Path }, TextWriter.Null, err));
        Assert.Contains("2,048", err.ToString());
    }

    [Fact] public void Run_NeverWritesTheInputEeprom()
    {
        using var dir = new TempDirectory();
        var input = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(dir.File("in.bin"), input);
        Program.Run(new[] { "run", CarPath, "--seconds", "0.3", "--eeprom", dir.File("in.bin"), "--out", dir.File("out") }, TextWriter.Null, TextWriter.Null);
        Assert.Equal(input, File.ReadAllBytes(dir.File("in.bin")));
    }

    [Theory]
    [InlineData("--seconds 0")]
    [InlineData("--seconds 601")]
    [InlineData("--raw --partial-onto CAR")]
    public void Run_UsageErrors_ExitWithCode2(string options) =>
        Assert.Equal(2, Program.Run(new[] { "run", CarPath }.Concat(options.Split(' ').Select(o => o == "CAR" ? CarPath : o)).ToArray(),
            TextWriter.Null, TextWriter.Null));

    [Fact] public void Run_KeyOffAtOrAfterTheEnd_IsAUsageError()
    {
        var err = new StringWriter();
        Assert.Equal(2, Program.Run(new[] { "run", CarPath, "--seconds", "5", "--key-off-at", "5" }, TextWriter.Null, err));
        Assert.Contains("--key-off-at needs", err.ToString());
    }

    [Fact] public void Run_KeyOffBeforeTheEnd_IsAccepted()
    {
        using var dir = new TempDirectory();
        Assert.Equal(0, Program.Run(new[] { "run", CarPath, "--seconds", "0.2", "--key-off-at", "0.1", "--out", dir.Path }, TextWriter.Null, TextWriter.Null));
        Assert.Contains("| key off | — | 2,400,000 |", File.ReadAllText(dir.File("run-report.md")));
    }

    [Fact] public void Boot_EcuBootWithPartialOnto_IsAUsageError()
    {
        var bin = Path.Combine(TestBins.Root, "bins", "CAR_current_43X001_SP95_STAGE1.bin");
        var err = new StringWriter();
        Assert.Equal(2, Ms43Sim.Cli.Program.Run(new[] { "boot", bin, "--partial-onto", bin, "--ecu-boot", bin }, TextWriter.Null, err));
        Assert.Contains("--ecu-boot", err.ToString());
    }
}
