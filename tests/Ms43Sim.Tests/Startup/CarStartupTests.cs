using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

/// <summary>One 5 s run of the car file as flashed, key on, engine stopped, blank EEPROM.</summary>
public sealed class CarKeyOn
{
    public StartupResult R { get; } = StartupRun.Run(TestImages.Car, new(Seconds: 5));
}

public class CarStartupTests : IClassFixture<CarKeyOn>
{
    readonly StartupResult r;

    public CarStartupTests(CarKeyOn f) => r = f.R;

    [Fact] public void SchedulerRuns_WithNoResetAndNoTrap()
    {
        Assert.Equal(StartupOutcome.SchedulerRunning, r.Outcome);
        Assert.Equal((0, 0, 0L), (r.WatchdogResets, r.SoftwareResets, r.EventCounts.GetValueOrDefault(typeof(TrapEvent))));
    }

    [Fact] public void MainLoop_IsReachedInsideTheBootsWatchdogWindow() =>
        Assert.True(r.Milestones.MainLoop - r.Milestones.BootServiced < 3_604_480);

    [Fact] public void MainLoop_ServicesTheWatchdogEvery10ms()
    {
        Assert.InRange(r.Scheduler.MainLoopServices, 480, 500);
        Assert.True(r.Scheduler.LongestGapCycles < 720_000);
        // E2A6 starts at the start-up time in 10 ms steps (0E:381E, from WDT), then counts with the main loop
        Assert.InRange(r.Scheduler.Uptime - r.Scheduler.MainLoopServices, 0, 15);
    }

    [Fact] public void CrcAndMonitorSumTasks_Pass()
    {
        Assert.True(r.Firmware.CrcChecksPassed, $"F8F4 {r.Firmware.F8F4:X4}, FD26 {r.Firmware.Fd26:X4}");
        Assert.True(r.Firmware.MonitorSumFirstPassDone);
        Assert.False(r.Firmware.MonitorSumMismatch);
    }

    [Fact] public void MonitoringHandshake_Completes_WithOnlyTheDeliberateWrongAnswers()
    {
        Assert.Equal((0xAD, 0), (r.Firmware.MonitorState, (int)r.Firmware.ErrorCount));
        Assert.InRange(r.Monitor.Exchanges, 480, 500);
        Assert.Equal(0, r.Monitor.UnexpectedAnswers);
        Assert.InRange(r.Monitor.WrongAnswers, 10, 20);                    // one per 31 ADh frames
    }

    [Fact] public void Engine_IsKeyOnAndStopped_AndTheThrottleStartCheckPasses()
    {
        Assert.Equal(0, r.Firmware.EngineState);
        Assert.False(r.Firmware.ThrottleLimp);
        Assert.Equal((7, 0x0040), ((int)r.Firmware.ThrottleCheckState, (int)r.Firmware.Fd14));   // the full adaptation passed
    }

    [Fact] public void ChipA_PassesItsChecks_SoTheEcuFaultMaskStaysClear() =>
        Assert.Equal(0, r.Firmware.EcuFaults);      // bit 10 (0400h) was plan 2's SSC artefact: P0604 at every key-on

    [Fact] public void BlankEeprom_LoadsDefaults_AndIsNotWrittenWhileTheKeyIsOn()
    {
        Assert.True(r.Firmware.EepromDefaultsLoaded);
        Assert.Equal(0, r.Eeprom.WordsWritten);
    }

    [Fact] public void CanPartners_AreLearned_AsClusterAndAsc_WithoutAGearbox()
    {
        Assert.Equal(0x0006, r.Firmware.Fd9c & 0x0007);                    // FD9C.1 cluster, .2 ASC, not .0 gearbox
        Assert.Equal(20, r.Firmware.Ambient);                               // 615h byte 3
    }

    [Fact] public void Can_Transmits316h_329h_545h() =>
        Assert.Equal(new uint[] { 0x316, 0x329, 0x545 }, r.CanTransmitted.Select(c => c.Id).OrderBy(i => i));

    [Fact] public void Inventory_HoldsNothingButTheBootsXperconRead() =>
        Assert.Equal(new[] { (AccessArea.Esfr, 0xF024u, AccessKind.Read) }, r.Inventory.Select(i => (i.Area, i.Address, i.Kind)));
}

public class CarVariantTests
{
    [Fact] public void SameInputs_SameResult()
    {
        var a = StartupRun.Run(TestImages.Car, new(Seconds: 1)); var b = StartupRun.Run(TestImages.Car, new(Seconds: 1));
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(a.Events, b.Events);
    }

    [Fact] public void Release_AsFlashed_RunsItsScheduler() =>
        Assert.Equal(StartupOutcome.SchedulerRunning, StartupRun.Run(TestImages.AsFlashed(TestBins.ReleaseBytes), new(Seconds: 2)).Outcome);

    [Fact] public void E85CamPort_AsFlashed_RunsItsScheduler() =>
        Assert.Equal(StartupOutcome.SchedulerRunning, StartupRun.Run(TestImages.AsFlashed(TestBins.E85CamPortBytes), new(Seconds: 2)).Outcome);

    // Design §8.2: the stale calibration CRC of the car and release files, and one flipped bit in
    // the CRC-only part of an otherwise corrected calibration (file 0x75000), must all fail.
    [Theory] [InlineData("car")] [InlineData("release")] [InlineData("flipped")]
    public void BrokenCalibrationCrc_FailsTheCheck_AndTheFirmwareResets(string which)
    {
        var image = which switch
        {
            "car" => FlashImageBuilder.Raw(FirmwareImage.FromBytes(TestBins.CarBytes)),
            "release" => FlashImageBuilder.Raw(FirmwareImage.FromBytes(TestBins.ReleaseBytes)),
            _ => FlippedCalibrationByte(),
        };
        var r = StartupRun.Run(image, new(Seconds: 3));
        Assert.Equal(StartupOutcome.ResetLoop, r.Outcome);
        Assert.True(r.SoftwareResets >= 1);
        Assert.Equal((0x0A, 0x0020), ((int)r.Firmware.LastError, (int)r.Firmware.ResetReason));
    }

    static FlashedImage FlippedCalibrationByte()
    {
        var b = TestImages.Car.Image.ToArray();
        b[0x75000] ^= 0x01;
        return FlashImageBuilder.Raw(FirmwareImage.FromBytes(b));
    }

    [Fact] public void CorruptedAnswerSeed_IsCaughtByTheModule()
    {
        var b = TestBins.CarBytes; b[0x6E4D2] ^= 0x01;                    // seed j = 0: 3E80h → 3E81h
        var r = StartupRun.Run(TestImages.AsFlashed(b), new(Seconds: 2));
        Assert.True(r.Monitor.UnexpectedAnswers > 0);
        Assert.Contains("unexpected answer", RunReportWriter.Report(r));
    }
}
