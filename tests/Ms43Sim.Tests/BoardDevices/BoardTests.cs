using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.BoardDevices;

public class BoardTests
{
    [Theory]
    [InlineData(0x78, false, false, false, false)]
    [InlineData(0xF8, true, false, false, false)]
    [InlineData(0x68, false, true, false, false)]
    [InlineData(0x70, false, false, true, false)]
    [InlineData(0x38, false, false, false, true)]
    public void ChipSelects_FollowTheLatch(int latch, bool eeprom, bool chipA, bool chipB, bool monitor)
    {
        var t = TestMachine.Create("0DFF");
        t.M.Board.Latch.Write16(0x080000, (ushort)latch);
        var b = t.M.Board;
        Assert.Equal((eeprom, chipA, chipB), (b.Eeprom.IsSelected, b.ChipA.IsSelected, b.ChipB.IsSelected));
        Assert.Equal(monitor, b.Latch.MonitorSelected);
    }

    [Fact] public void Mux_SelectsWhatAn13AndAn4Show()
    {
        var t = TestMachine.Create("0DFF");
        t.M.Board.Latch.Write16(0x080000, 0x7B);                           // mux position 3
        Assert.Equal((3.87, 4.08), (t.M.Board.AnalogVolts(13), t.M.Board.AnalogVolts(4)));
    }

    [Fact] public void P3_8_ReadsTheEepromReadyLine_AndP8_6ReadsLow()
    {
        var t = TestMachine.Create("0DFF");
        Assert.Equal((true, false), (t.M.Ports.Pin(3, 8), t.M.Ports.Pin(8, 6)));
    }

    [Fact] public void Assumptions_ListTheBoard()
    {
        var all = string.Join("\n", MachineAssumptions.For(new MachineOptions()));
        Assert.Empty(new[] { "VAREF = 5.0 V", "pull-up", "P8.6", "write-only latch", "tW = 5 ms", "chip A", "chip B", "monitoring module", "20 µs" }
            .Where(p => !all.Contains(p)));
    }

    [Fact] public void LatchChange_ReachesEveryDevice_AndTheEepromAnswersOnTheBus()
    {
        var t = TestMachine.Create("0DFF");
        t.M.Board.Latch.Write16(0x080000, 0xF8);                           // EEPROM selected: ready
        Assert.True(t.M.Board.Spi.MrstLevel);
        t.M.Board.Latch.Write16(0x080000, 0x68);                           // chip A selected: drives its status
        Assert.Equal(true, t.M.Board.ChipA.Miso);
        t.M.Board.Latch.Write16(0x080000, 0x38);                           // module selected: an exchange starts
        t.M.Scheduler.Advance(t.M.Cycles + 10_000);
        Assert.True(t.M.Ssc.Transfers == 0 && t.M.Board.Monitor.ExchangesSinceReset == 1);
    }

    [Fact] public void EepromImage_OfTheWrongSize_FailsMachineCreate()
    {
        var image = FlashImageBuilder.Raw(FirmwareImage.FromBytes(new byte[0x80000]));
        Assert.Contains("2,048", Assert.Throws<InvalidDataException>(
            () => Machine.Create(image, new MachineOptions { EepromImage = new byte[1024] })).Message);
    }

    [Fact] public void CpuReset_RestartsTheModule_PowerOnRestoresTheLatch()
    {
        var t = TestMachine.Create("0DFF");
        t.M.Board.Latch.Write16(0x080000, 0x38);
        t.M.Scheduler.Advance(t.M.Cycles + 10_000);
        t.M.Reset(ResetKind.Software);
        Assert.Equal((0, 0x38), (t.M.Board.Monitor.ExchangesSinceReset, (int)t.M.Board.Latch.Value));
        t.M.PowerOn();
        Assert.Equal(0x78, t.M.Board.Latch.Value);
    }
}
