using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

/// <summary>One 40 s run of the car file with the key turned off at 3 s.</summary>
public sealed class CarKeyOff
{
    public StartupResult R { get; } = StartupRun.Run(TestImages.Car, new(Seconds: 40, KeyOffAtSeconds: 3));
}

public class KeyOffTests : IClassFixture<CarKeyOff>
{
    readonly StartupResult r;

    public KeyOffTests(CarKeyOff f) => r = f.R;

    [Fact] public void AfterRun_WritesTheEeprom_ThenDropsTheMainRelay()
    {
        Assert.Equal(StartupOutcome.PoweredOff, r.Outcome);
        Assert.InRange(r.Milestones.PowerOff!.Value / 24e6, 11.0, 30.0);   // about 8 s of after-run, then the writes
        Assert.True(r.Eeprom.WordsWritten > 0);
        Assert.Equal("43290010", System.Text.Encoding.ASCII.GetString(r.Eeprom.FinalImage, 0x32, 8));
        Assert.Equal(0x38C3, BitConverter.ToUInt16(r.Eeprom.FinalImage, 0x3A));
    }

    [Fact] public void AfterRun_ShutdownFrames_AreNotCountedAsUnexpected()
    {
        Assert.Equal((0, 0x3B), (r.Monitor.UnexpectedAnswers, (int)r.Firmware.MonitorState));
        Assert.Contains("3Bh after-run shutdown", RunReportWriter.Report(r));
    }

    [Fact] public void KeyOff_IsRecorded_AtTheRequestedTime() =>
        Assert.Equal(3UL * 24_000_000, r.Milestones.KeyOff);

    [Fact] public void NextKeyOn_ReadsTheWrittenEeprom_AndRuns()
    {
        var next = StartupRun.Run(TestImages.Car, new(Seconds: 3, Machine: new MachineOptions { EepromImage = r.Eeprom.FinalImage }));
        Assert.Equal(StartupOutcome.SchedulerRunning, next.Outcome);
        Assert.False(next.Firmware.EepromDefaultsLoaded);
        Assert.True(next.Eeprom.WordsRead >= 480, $"{next.Eeprom.WordsRead} words read");
        // The loaders under 0B:0418 request 37 blocks with the engine stopped; none asks for
        // bytes 0x384-0x3AF or 0x3D6, which the after-run writes (the plan's 521 counted all 40 blocks).
        var m = Machine.Create(TestImages.Car, new MachineOptions { EepromImage = r.Eeprom.FinalImage });
        m.PowerOn();
        m.Run(72_000_000);
        var written = Enumerable.Range(0, 1024).Where(i => r.Eeprom.FinalImage[2 * i] != 0xFF || r.Eeprom.FinalImage[2 * i + 1] != 0xFF);
        Assert.Equal(Enumerable.Range(0x1C2, 22).Append(0x1EB), written.Except(m.Board.Eeprom.AddressesRead));
    }
}
