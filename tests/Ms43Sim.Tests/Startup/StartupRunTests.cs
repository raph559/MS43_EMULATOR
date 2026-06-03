using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

public class StartupRunTests
{
    static FlashedImage Car => TestImages.Car;

    static StartupResult EngineRun(double seconds = 8) => StartupRun.Run(TestImages.Car, new(Seconds: seconds, Engine: EngineScenario.WarmIdle));

    [Fact] public void EngineRun_ReportsTheEngine_AndWritesTheCsvFiles()
    {
        using var dir = new TempDirectory();
        var r = EngineRun();
        RunReportWriter.Write(r, dir.Path);
        var report = File.ReadAllText(Path.Combine(dir.Path, "run-report.md"));
        foreach (var section in new[] { "## Engine", "## Inputs fed and seen", "## Outputs", "## Plants", "## Fault memory", "## CAN partners" })
            Assert.Contains(section, report);
        Assert.Equal(82, File.ReadAllLines(Path.Combine(dir.Path, "trace.csv")).Length);     // header + 0.0 … 8.0 s
        Assert.Contains(File.ReadLines(Path.Combine(dir.Path, "events.csv")), l => l.Contains(",Pin,P2.6,"));
        Assert.Equal(EnginePhase.Running, r.Engine!.Phases[^1].Phase);
        Assert.Contains("- Outcome: running at ", report);
    }

    [Fact] public void EngineRun_OfABinThatNeverFires_SaysItNeverStarted()
    {
        // the stock 430069 file never fires in this scenario: the starter turns it, then it coasts to a stop
        var r = StartupRun.Run(TestImages.AsFlashed(TestBins.StockBytes), new(Seconds: 6, Engine: EngineScenario.WarmIdle));
        Assert.Equal(EngineOutcome.NeverStarted, r.Engine!.Outcome);
        Assert.Contains("- Outcome: never started", RunReportWriter.Report(r));
    }

    [Fact] public void EqualEngineRuns_GiveIdenticalFiles()
    {
        using var a = new TempDirectory(); using var b = new TempDirectory();
        var ra = EngineRun(); var rb = EngineRun();
        RunReportWriter.Write(ra, a.Path); RunReportWriter.Write(rb, b.Path);
        Assert.Equal(ra.StateHash, rb.StateHash);
        foreach (var f in new[] { "run-report.md", "events.csv", "trace.csv", "inventory.csv", "eeprom.bin" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(a.Path, f)), File.ReadAllBytes(Path.Combine(b.Path, f)));
    }

    [Fact] public void Instrument_IsCalledOnce_BeforeTheRun()
    {
        var calls = new List<ulong>();
        StartupRun.Run(TestImages.Car, new(Seconds: 0.1, Instrument: m => calls.Add(m.Cycles)));
        Assert.Equal(new[] { 0UL }, calls);
    }

    [Fact] public void Checkpoints_AreRecognisedInTheCarReleaseAndStockFiles()
    {
        Assert.True(StartupRun.Recognises(FirmwareImage.FromBytes(TestBins.CarBytes)));
        Assert.True(StartupRun.Recognises(FirmwareImage.FromBytes(TestBins.ReleaseBytes)));
        Assert.True(StartupRun.Recognises(FirmwareImage.FromBytes(TestBins.StockBytes)));
        Assert.False(StartupRun.Recognises(FirmwareImage.FromBytes(new byte[0x80000])));
    }

    [Fact] public void UnknownImage_SkipsTheMilestones_AndSaysSo()
    {
        // 0.005 s = 120,000 cycles: shorter than the power-on watchdog period (131,072), so no reset
        var r = StartupRun.Run(FlashImageBuilder.Raw(FirmwareImage.FromBytes(new byte[0x80000])), new(Seconds: 0.005));
        Assert.Equal(StartupOutcome.NoProgress, r.Outcome);
        Assert.Contains(r.Assumptions, a => a.Contains("program checkpoints not recognised"));
    }

    [Fact] public void Report_HasItsSectionsInOrder()
    {
        var md = RunReportWriter.Report(StartupRun.Run(Car, new(Seconds: 0.3)));
        var sections = new[] { "## Image", "## EEPROM", "## Outcome", "## Milestones", "## Scheduler", "## Checksum tasks",
            "## Monitoring module", "## Firmware state", "## CAN transmissions", "## Resets and traps",
            "## Unsupported and monitoring events", "## Stubs", "## Unmodelled hardware", "## Last branches", "## Assumptions" };
        var at = sections.Select(s => md.IndexOf(s, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(-1, at);
        Assert.Equal(at.OrderBy(i => i), at);
    }

    [Fact] public void EqualRuns_GiveIdenticalReports() =>
        Assert.Equal(RunReportWriter.Report(StartupRun.Run(Car, new(Seconds: 0.3))), RunReportWriter.Report(StartupRun.Run(Car, new(Seconds: 0.3))));

    [Fact] public void ShortCarRun_ReachesTheMainLoop_AndRecordsItsMilestonesInOrder()
    {
        var r = StartupRun.Run(Car, new(Seconds: 0.3));
        var m = r.Milestones;
        Assert.Equal(StartupOutcome.SchedulerRunning, r.Outcome);
        Assert.True(m.BootServiced < m.Handover && m.Handover < m.InterruptsEnabled
                    && m.InterruptsEnabled < m.EepromLoaded && m.EepromLoaded < m.MainLoop, $"{m}");
        Assert.Equal(("blank", 2048), (r.Eeprom.Source, r.Eeprom.FinalImage.Length));
        Assert.Equal(6, r.Stubs.Count);                                   // the throttle body is modelled since plan 3
    }
}
