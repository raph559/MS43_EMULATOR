using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

public class StartupProgressTests
{
    static Machine Car(byte[]? eeprom = null)
    {
        var m = Machine.Create(FlashImageBuilder.AsFlashedFull(FirmwareImage.FromBytes(TestBins.CarBytes)),
            new MachineOptions { EepromImage = eeprom });
        m.PowerOn();
        return m;
    }

    [Fact] public void Car_BlankEeprom_GetsPastTheEepromWait_AndLoadsDefaults()
    {
        var m = Car();
        ulong? loaded = null;
        m.OnInstruction(0x0E3700, x => loaded ??= x.Cycles);
        m.Run(10_000_000, _ => loaded != null);
        Assert.NotNull(loaded);
        Assert.Equal(0, m.WatchdogResets + m.SoftwareResets);
        Assert.Equal(0x20, m.Bus.Peek16(0xFD46) & 0x20);                 // ID block invalid → defaults
        Assert.InRange(m.Board.Eeprom.WordsRead, 6, 19);                   // about 10 [obs]; a valid image reads 521
        Assert.Contains(0x19, m.Board.Eeprom.AddressesRead);
        Assert.True(m.Board.ChipA.Transfers >= 1);                         // job 0 completed, so the dispatcher ran
    }

    [Fact] public void Car_ReachesTheMainLoop_InsideTheBootsWatchdogWindow()
    {
        var m = Car();
        ulong? serviced = null, mainLoop = null;
        m.OnInstruction(0x002EFC, x => serviced ??= x.Cycles);
        m.OnInstruction(0x0E0094, x => mainLoop ??= x.Cycles);
        m.Run(10_000_000, _ => mainLoop != null);
        Assert.NotNull(mainLoop);
        Assert.Equal(0, m.WatchdogResets + m.SoftwareResets);
        Assert.True(mainLoop - serviced < 3_604_480, $"{mainLoop - serviced} cycles");
    }

    [Fact] public void Car_RunsOneSecondPastTheMainLoop_WithoutTheAdcFreshnessReset()
    {
        var m = Car();
        ulong? mainLoop = null;
        m.OnInstruction(0x0E0094, x => mainLoop ??= x.Cycles);
        m.Run(10_000_000, _ => mainLoop != null);
        m.Run(24_000_000);
        Assert.NotEqual(0x0010, m.Bus.Peek16(0xE7FC));                      // no forced reset for a stale AN1
        Assert.True(m.Adc.Conversions > 10_000, $"{m.Adc.Conversions} conversions");   // about 11 per ms
    }

    [Fact] public void Car_StartupToTheEepromLoads_IsDeterministic()
    {
        string Hash() { var m = Car(); var done = false; m.OnInstruction(0x0E3700, _ => done = true); m.Run(10_000_000, _ => done); return m.StateHash(); }
        Assert.Equal(Hash(), Hash());
    }
}
