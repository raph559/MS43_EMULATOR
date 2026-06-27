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

    [Fact] public void Run_EepromThatTheRunWouldOverwrite_IsAUsageError_AndTheFileSurvives()
    {
        using var dir = new TempDirectory();
        var input = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(dir.File("eeprom.bin"), input);
        var err = new StringWriter();
        Assert.Equal(2, Program.Run(new[] { "run", CarPath, "--seconds", "0.3", "--eeprom", dir.File("eeprom.bin"), "--out", dir.Path }, TextWriter.Null, err));
        Assert.Equal(input, File.ReadAllBytes(dir.File("eeprom.bin")));
        Assert.Contains("eeprom.bin", err.ToString());
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

    [Fact] public void Run_WithTheWarmIdleEngine_WritesFiveFiles()
    {
        using var dir = new TempDirectory();
        var output = new StringWriter();
        var code = Program.Run(new[] { "run", TestImages.CarPath, "--engine", "warm-idle", "--seconds", "8", "--out", dir.Path }, output, TextWriter.Null);
        Assert.Equal(0, code);
        Assert.Contains("engine: running at ", output.ToString());
        foreach (var f in new[] { "run-report.md", "inventory.csv", "eeprom.bin", "events.csv", "trace.csv" })
            Assert.True(File.Exists(Path.Combine(dir.Path, f)), f);
    }

    [Fact] public void Run_WithAnUnknownEngine_IsAUsageError()
    {
        var err = new StringWriter();
        Assert.Equal(2, Program.Run(new[] { "run", TestImages.CarPath, "--engine", "cold-start" }, TextWriter.Null, err));
        Assert.Contains("warm-idle", err.ToString());
    }

    [Fact] public void Boot_EcuBootWithPartialOnto_IsAUsageError()
    {
        var bin = Path.Combine(TestBins.Root, "bins", "CAR_current_43X001_SP95_STAGE1.bin");
        var err = new StringWriter();
        Assert.Equal(2, Ms43Sim.Cli.Program.Run(new[] { "boot", bin, "--partial-onto", bin, "--ecu-boot", bin }, TextWriter.Null, err));
        Assert.Contains("--ecu-boot", err.ToString());
    }


    [Fact] public void Run_DriveWithAMistake_ExitsWithCode2()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("bad.drive"), "drive x\n3 idle pedal=20");
        var err = new StringWriter();
        Assert.Equal(2, Program.Run(new[] { "run", CarPath, "--drive", dir.File("bad.drive") }, TextWriter.Null, err));
        Assert.Contains("line 2", err.ToString());
    }

    [Fact] public void Run_ASeasonedDriveWithoutAnEeprom_IsAUsageError()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("s.drive"), "drive s\nstart seasoned\n3 on");
        var err = new StringWriter();
        Assert.Equal(2, Program.Run(new[] { "run", CarPath, "--drive", dir.File("s.drive") }, TextWriter.Null, err));
        Assert.Contains("starts seasoned", err.ToString());
    }

    [Fact] public void Check_RunsEveryDriveInADir_WarmStartFirst_AndWritesAVerdictPerDrive()
    {
        // a --drives dir written here, so this test does not depend on sim/drives/ (Task 12); the full
        // seven-drive check of the car and release is Task 13
        using var drives = new TempDirectory(); using var dir = new TempDirectory();
        File.WriteAllText(drives.File("warm-start.drive"), "drive warm-start\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20\n3 on\n1.5 crank rpm=200\n10 idle");
        File.WriteAllText(drives.File("idle.drive"), "drive idle\nstart seasoned\n3 on\n1.5 crank rpm=200\n10 idle");
        var output = new StringWriter();
        var code = Program.Run(new[] { "check", CarPath, "--drives", drives.Path, "--out", dir.Path }, output, TextWriter.Null);
        Assert.True(code == 0, output.ToString());
        Assert.Equal("pass", File.ReadAllText(Path.Combine(dir.Path, "warm-start", "verdict.txt")).Trim());
        Assert.Equal("pass", File.ReadAllText(Path.Combine(dir.Path, "idle", "verdict.txt")).Trim());
        foreach (var file in new[] { "drive-report.md", "events.csv", "trace.csv", "eeprom.bin" })
            Assert.True(File.Exists(Path.Combine(dir.Path, "idle", file)), file);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("warm-start", lines.First(l => l.StartsWith("warm-start") || l.StartsWith("idle")));
    }

    [Fact] public void Check_TwoDrivesOfOneName_IsAUsageError_NamingBothFiles()      // final review C1
    {
        // one name is one output folder and one verdict: the second run would overwrite the first
        // and could hide its fail
        using var drives = new TempDirectory(); using var dir = new TempDirectory();
        File.WriteAllText(drives.File("a.drive"), "drive same\n0.2 on");
        File.WriteAllText(drives.File("b.drive"), "drive same\n0.2 on");
        var err = new StringWriter();
        Assert.Equal(2, Program.Run(new[] { "check", CarPath, "--drives", drives.Path, "--out", dir.Path }, TextWriter.Null, err));
        Assert.Contains("a.drive", err.ToString());
        Assert.Contains("b.drive", err.ToString());
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "same")));
    }

    [Fact] public void StockFile_KeyOnRun_IsIncompleteNotPass()                     // Review Focus 4
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("k.drive"), "drive k\n20 on");
        var stock = Path.Combine(TestBins.Root, "bins", "MS43_430069_stock.bin");
        var code = Program.Run(new[] { "run", stock, "--drive", dir.File("k.drive"), "--out", dir.Path }, new StringWriter(), TextWriter.Null);
        Assert.Equal(4, code);                                                      // the RAM checks are NotEvaluated off MS43X001
        Assert.Equal("incomplete", File.ReadAllText(dir.File("verdict.txt")).Trim());
        Assert.Contains("not evaluated", File.ReadAllText(dir.File("drive-report.md")));
    }
}
