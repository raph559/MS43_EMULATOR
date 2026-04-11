using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.BoardDevices;

public class OutputLatchTests
{
    static TestMachine Window(string code) => TestMachine.Create(code).WriteSfr(0xFE1C, 0x0800).WriteSfr(0xFF18, 0x85AE);

    [Fact] public void PowersUp_WithEveryChipSelectInactive()
    {
        var t = Window("CC00");
        Assert.Equal(0x78, t.M.Board.Latch.Value);
    }

    [Fact] public void ProgramWrite_ThroughExtp_LoadsTheLatch()
    {
        var t = Window("E6F05800 D7402000 F6F00000").Step(3);       // MOV R0,#0058h ; EXTP #20h,#1 ; MOV 0000h,R0
        Assert.Equal(0x58, t.M.Board.Latch.Value);
        Assert.DoesNotContain(t.M.Inventory.Records, r => r.Area == AccessArea.ChipSelect3);
    }

    [Fact] public void ByteWriteToTheOddAddress_DoesNotLoadIt()
    {
        var t = Window("E6F0FF00 D7402000 F7F00100").Step(3);       // MOV R0,#00FFh ; EXTP #20h,#1 ; MOVB 0001h,RL0
        Assert.Equal(0x78, t.M.Board.Latch.Value);
    }

    [Fact] public void Read_GivesFfff_AndIsReported()
    {
        var t = Window("D7402000 F2F10000").Step(2);                // EXTP #20h,#1 ; MOV R1,0000h
        Assert.Equal(0xFFFF, t.R(1));
        Assert.Contains(t.M.Events.All.OfType<UnsupportedEvent>(), e => e.What.Contains("write-only latch"));
    }

    [Fact] public void Changed_FiresOnlyWhenTheValueChanges()
    {
        var l = new OutputLatch(new EventLog());
        var seen = new List<(byte, byte)>();
        l.Changed += (before, after) => seen.Add((before, after));
        l.Write16(0x080000, 0xFF78);
        l.Write8(0x080000, 0xF8);
        Assert.Equal(new[] { ((byte)0x78, (byte)0xF8) }, seen);
    }

    [Theory]
    [InlineData(0x78, 0, false, false, true, false, false)]
    [InlineData(0x87, 7, true, true, false, true, true)]
    public void Bits_DecodeAsTheFirmwareUsesThem(int value, int mux, bool chipB, bool chipA, bool relay, bool monitor, bool eeprom)
    {
        var l = new OutputLatch(new EventLog());
        l.Write16(0x080000, (ushort)value);
        Assert.Equal((mux, chipB, chipA, relay, monitor, eeprom),
            (l.MuxSelect, l.ChipBSelected, l.ChipASelected, l.MainRelayHeld, l.MonitorSelected, l.EepromSelected));
    }
}
