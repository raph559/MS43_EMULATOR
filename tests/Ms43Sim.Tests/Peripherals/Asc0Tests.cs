using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Peripherals;

public class Asc0Tests
{
    static TestMachine Dsl() => TestMachine.Create("0DFF").WriteSfr(0xFEB4, 0x004D).WriteSfr(0xFFB0, 0x80F7);

    [Fact] public void Transmit8E1_At9615Baud_TbirAtOnce_TirBeforeTheStopBit()
    {
        var t = Dsl().WriteSfr(0xFEB0, 0x0055);
        Assert.Equal(0x80, t.Sfr(0xF19C) & 0x80);
        t.M.Scheduler.Advance(10 * 2_496 - 1); Assert.Equal(0, t.Sfr(0xFF6C) & 0x80);
        t.M.Scheduler.Advance(10 * 2_496); Assert.Equal(0x80, t.Sfr(0xFF6C) & 0x80);
    }

    [Fact] public void Loopback_ReceivesTheFrame()
    {
        var t = Dsl().WriteSfr(0xFFB0, 0xC0F7).WriteSfr(0xFEB0, 0x00A5);
        t.M.Scheduler.Advance(11 * 2_496);
        Assert.Equal((0x00A5, 0x80), (t.Sfr(0xFEB2) & 0xFF, t.Sfr(0xFF6E) & 0x80));
    }

    [Fact] public void Receive_OverAnUnreadS0rbuf_SetsS0oeAndS0eir()
    {
        var t = Dsl();
        t.M.Asc0.Receive(0x0011); t.M.Asc0.Receive(0x0022);
        Assert.Equal((0x0400, 0x80), (t.Sfr(0xFFB0) & 0x0400, t.Sfr(0xFF70) & 0x80));
    }

    [Fact] public void SynchronousMode_IsReported()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFB0, 0x8010);
        Assert.Contains(t.M.Events.All.OfType<UnsupportedEvent>(), e => e.What.Contains("ASC0"));
    }

    [Fact] public void Transmission_AddsTheEvenParityBit_AndBuffersASecondFrame()
    {
        var t = Dsl();
        var sent = new List<ushort>();
        t.M.Asc0.FrameTransmitted += sent.Add;
        t.WriteSfr(0xF19C, 0x0000).WriteSfr(0xFEB0, 0x0055).WriteSfr(0xF19C, 0x0000).WriteSfr(0xFEB0, 0x0057);
        Assert.Equal(0, t.Sfr(0xF19C) & 0x80);                              // the second frame waits in S0TBUF
        t.M.Scheduler.Advance(11 * 2_496); Assert.Equal(0x80, t.Sfr(0xF19C) & 0x80);
        t.M.Scheduler.Advance(22 * 2_496);
        Assert.Equal(new ushort[] { 0x055, 0x157 }, sent);                  // 55h has four 1s, 57h five
        Assert.Equal(2L, t.M.Asc0.FramesTransmitted);
    }

    [Fact] public void TwoStopBits_TirComesBeforeTheSecond()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFEB4, 0x004D).WriteSfr(0xFFB0, 0x8009).WriteSfr(0xFEB0, 0x0041);   // 8N2
        t.M.Scheduler.Advance(10 * 2_496 - 1); Assert.Equal(0, t.Sfr(0xFF6C) & 0x80);
        t.M.Scheduler.Advance(10 * 2_496); Assert.Equal(0x80, t.Sfr(0xFF6C) & 0x80);
    }

    [Fact] public void Receive_WithTheReceiverOff_IsLost()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFB0, 0x8001);       // 8N1, S0REN = 0
        t.M.Asc0.Receive(0x0011);
        Assert.Equal((0, 0), (t.Sfr(0xFEB2), t.Sfr(0xFF6E) & 0x80));
    }

    [Theory]
    [InlineData(0x004D, 0x80F7, 2_496.0, 11)]    // 8E1 at the firmware's S0BG: 16 × 2 × 78 cycles, 1 + 9 + 1 bits
    [InlineData(0x0005, 0x80F7, 192.0, 11)]      // 125,000 baud: 16 × 2 × 6
    [InlineData(0x004D, 0xA0F7, 3_744.0, 11)]    // S0BRS: 16 × 3 × 78
    [InlineData(0x004D, 0x8001, 2_496.0, 10)]    // 8N1
    [InlineData(0x004D, 0x8009, 2_496.0, 11)]    // 8N2
    public void BitCycles_AndFrameBits_DescribeTheFrameThatStartsNow(int reload, int control, double bitCycles, int frameBits)
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFEB4, (ushort)reload).WriteSfr(0xFFB0, (ushort)control);
        Assert.Equal((bitCycles, frameBits), (t.M.Asc0.BitCycles, t.M.Asc0.FrameBits));
    }
}
