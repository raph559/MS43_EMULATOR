using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Peripherals;

public class SscTests
{
    const uint Sscc = 0xFFB2, Tb = 0xF0B0, Rb = 0xF0B2, Br = 0xF0B4, Tic = 0xFF72, Ric = 0xFF74, Eic = 0xFF76;

    sealed class Slave : ISscSlave
    {
        public string Name => "test";
        public bool IsSelected => true;
        public bool? Miso { get; set; } = false;
        public readonly List<(bool Rising, bool Mosi)> Edges = new();
        public Func<bool, bool?, bool?> Next = (_, m) => m;                 // (rising, old miso) → new miso
        public void ClockEdge(bool rising, bool mosi) { Edges.Add((rising, mosi)); Miso = Next(rising, Miso); }
    }

    static (TestMachine T, Slave S) Master(ushort sscconProgramming)
    {
        var t = TestMachine.Create("0DFF");
        var s = new Slave();
        t.M.Board.Spi.Attach(s);
        t.WriteSfr(Br, 0x0017).WriteSfr(Sscc, sscconProgramming);
        return (t, s);
    }

    [Fact] public void Word_Takes768Cycles_TirAtOnce_RirAtTheEnd()
    {
        var (t, _) = Master(0xC23F);
        t.WriteSfr(Tb, 0x1819);
        Assert.Equal((0x80, 0x1000), (t.Sfr(Tic) & 0x80, t.Sfr(Sscc) & 0x1000));
        t.M.Scheduler.Advance(767); Assert.Equal(0, t.Sfr(Ric) & 0x80);
        t.M.Scheduler.Advance(768); Assert.Equal((0x80, 0), (t.Sfr(Ric) & 0x80, t.Sfr(Sscc) & 0x1000));
    }

    [Fact] public void Sscon_ShowsTheProgrammingOrTheOperatingView()
    {
        var (t, _) = Master(0x423F);
        Assert.Equal(0x423F, t.Sfr(Sscc));
        t.WriteSfr(Sscc, 0xC23F); Assert.Equal(0xC000, t.Sfr(Sscc));
        t.WriteSfr(Sscc, 0x4000); Assert.Equal(0x423F, t.Sfr(Sscc));
    }

    [Theory]
    [InlineData(0xC037, 0x55)]          // SSCPH = 1: latch on the rising edge, before the slave's update
    [InlineData(0xC017, 0xAA)]          // SSCPH = 0: latch on the falling edge, after it
    public void LatchEdge_DecidesWhatTheMasterSamples(int ssccon, int received)
    {
        var (t, s) = Master((ushort)ssccon);
        s.Next = (rising, m) => rising ? !m : m;                             // MISO toggles at each rising edge
        t.WriteSfr(Tb, 0x00A5); t.M.Scheduler.Advance(384);
        Assert.Equal(received, t.Sfr(Rb) & 0xFF);
    }

    [Fact] public void Ph1_SlaveSamplesMsbFirstOnRisingEdges()
    {
        var (t, s) = Master(0xC037);
        t.WriteSfr(Tb, 0x00A5); t.M.Scheduler.Advance(384);
        Assert.Equal(new[] { true, false, true, false, false, true, false, true },
            s.Edges.Where(e => e.Rising).Select(e => e.Mosi).ToArray());
    }

    [Fact] public void SecondWord_FollowsWithoutAGap()
    {
        var (t, _) = Master(0xC23F);
        t.WriteSfr(Tb, 0x1111).WriteSfr(Tb, 0x2222);
        t.M.Scheduler.Advance(768); Assert.Equal(0x1000, t.Sfr(Sscc) & 0x1000);
        t.M.Scheduler.Advance(1536); Assert.Equal(0, t.Sfr(Sscc) & 0x1000);
    }

    [Fact] public void UnreadReceiveBuffer_GivesAReceiveError()
    {
        var (t, _) = Master(0xC23F);
        t.WriteSfr(Tb, 0x1111); t.M.Scheduler.Advance(768);
        t.WriteSfr(Tb, 0x2222); t.M.Scheduler.Advance(1536);
        Assert.Equal((0x0200, 0x80), (t.Sfr(Sscc) & 0x0200, t.Sfr(Eic) & 0x80));
    }

    [Fact] public void ClearingSscen_AbortsTheTransfer()
    {
        var (t, _) = Master(0xC23F);
        t.WriteSfr(Tb, 0x1111); t.M.Scheduler.Advance(100);
        t.WriteSfr(Sscc, 0x4000); t.M.Scheduler.Advance(2000);
        Assert.Equal(0, t.Sfr(Ric) & 0x80);
    }

    [Fact] public void Slave_Job3Pattern_MovesNineBytesEachWayByPec()
    {
        // PEC3: FC30h+ → SSCTB (PECC3 0509h); PEC2: SSCRB → FC27h+ (PECC2 0309h); RETI at SCTINT (B4h) and SCRINT (B8h)
        var t = TestMachine.Create(string.Concat(Enumerable.Repeat("CC00 ", 64)), (0xB4, "FB88"), (0xB8, "FB88"))
            .WriteSfr(0xFEC6, 0x0509).Poke16(0xFCEC, 0xFC30).Poke16(0xFCEE, 0xF0B0)
            .WriteSfr(0xFEC4, 0x0309).Poke16(0xFCE8, 0xF0B2).Poke16(0xFCEA, 0xFC27)
            .WriteSfr(Br, 0x002F).WriteSfr(Sscc, 0x1F47).WriteSfr(Sscc, 0x9F47);
        for (var i = 0; i < 9; i++) t.M.Bus.Write8((uint)(0xFC30 + i), (byte)(i + 1));   // the 9 bytes to send: 1…9
        t.WriteSfr(Ric, 0x007A).WriteSfr(Tic, 0x00FB);                                  // SSCTIC.IR set: PEC3 starts
        t.Psw = 0x0800;
        t.Step(4);
        var sent = new List<int>();
        for (var i = 0; i < 9; i++) { sent.Add(t.M.Ssc.ExternalTransfer((ushort)(0x10 + i)) & 0xFF); t.Step(4); }
        Assert.Equal(Enumerable.Range(1, 9), sent);
        Assert.Equal(Enumerable.Range(0x10, 9), Enumerable.Range(0, 9).Select(i => (int)t.M.Bus.Read8((uint)(0xFC27 + i))));
    }

    [Fact] public void Slave_TransferWithoutNewData_IsATransmitError()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(Sscc, 0x1F47).WriteSfr(Sscc, 0x9F47);
        Assert.Equal(0xFF, t.M.Ssc.ExternalTransfer(0x55) & 0xFF);
        Assert.Equal((0x0100, 0x80), (t.Sfr(Sscc) & 0x0100, t.Sfr(Eic) & 0x80));
    }

    [Fact] public void Slave_TransmitError_ShiftsOutTheLastReceivedCharacter()
    {
        // UM §12.6: without a reloaded SSCTB the slave shifts out the old shift register, normally
        // the data received in the last transfer.
        var t = TestMachine.Create("0DFF").WriteSfr(Sscc, 0x1F47).WriteSfr(Sscc, 0x9F47).WriteSfr(Tb, 0x0012);
        Assert.Equal(0x12, t.M.Ssc.ExternalTransfer(0x34) & 0xFF);
        Assert.Equal(0x34, t.M.Ssc.ExternalTransfer(0x56) & 0xFF);
    }

    [Fact] public void Disabled_ExternalTransfer_ShiftsOutOnes_AndChangesNothing()
    {
        var t = TestMachine.Create("0DFF");
        Assert.Equal((0xFFFF, 0L, 0), (t.M.Ssc.ExternalTransfer(0x55), t.M.Ssc.Transfers, t.Sfr(Ric) & 0x80));
    }

    [Fact] public void BaudRateWrite_WhileEnabled_IsReported()
    {
        var (t, _) = Master(0xC23F);
        t.WriteSfr(Br, 0x002F);
        Assert.Equal(0x002F, t.Sfr(Br));
        Assert.Contains(t.M.Events.All.OfType<UnsupportedEvent>(), e => e.What.Contains("SSCBR"));
    }

    [Fact] public void MrstLevel_IsTheAndOfTheSelectedDrivers_AndHighWhenReleased()
    {
        var bus = new SscBus();
        Assert.True(bus.MrstLevel);
        var s = new Slave { Miso = null };
        bus.Attach(s);
        Assert.True(bus.MrstLevel);
        s.Miso = false;
        Assert.False(bus.MrstLevel);
    }

    [Fact] public void P3Bit8_ReadsTheMrstLine()
    {
        var (t, s) = Master(0xC23F);
        Assert.False(t.M.Ports.Pin(3, 8));
        s.Miso = true;
        Assert.True(t.M.Ports.Pin(3, 8));
    }
}
