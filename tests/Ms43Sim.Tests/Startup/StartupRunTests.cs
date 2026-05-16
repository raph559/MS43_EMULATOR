using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

public class StartupRunTests
{
    static FlashedImage Car => TestImages.Car;

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
