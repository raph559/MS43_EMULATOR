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
        t.M.Asc0.Receive(0x0011, t.M.Scheduler.Now); t.M.Asc0.Receive(0x0022, t.M.Scheduler.Now);
        Assert.Equal((0x0400, 0x80), (t.Sfr(0xFFB0) & 0x0400, t.Sfr(0xFF70) & 0x80));
    }

    [Fact] public void AFrameWhoseStartBitCameWithTheReceiverOff_IsLost_ThoughTheReceiverIsOnAtItsEnd()
    {
        // UM V3.1 §11.1.2 (p. 257): reception is initiated by the start bit's falling edge, provided S0R and S0REN are set.
        var t = TestMachine.Create("0DFF").WriteSfr(0xFEB4, 0x004D).WriteSfr(0xFFB0, 0x80EF);   // 8E2, S0REN = 0
        t.M.Scheduler.Advance(1_000);
        t.WriteSfr(0xFFB0, 0x80F7);                                          // on again at 1,000
        t.M.Scheduler.Advance(2_000);
        t.M.Asc0.Receive(0x0011, startBit: 999);
        Assert.Equal((0, 0), (t.Sfr(0xFEB2), t.Sfr(0xFF6E) & 0x80));
        t.M.Asc0.Receive(0x0022, startBit: 1_000);                           // a start bit in the cycle it came on is seen
        Assert.Equal((0x0022, 0x80), (t.Sfr(0xFEB2), t.Sfr(0xFF6E) & 0x80));
    }

    [Fact] public void AFrameWhoseStartBitCameWithTheReceiverOn_IsCompleted_ThoughS0renIsClearedBeforeItsEnd()
    {
        // UM V3.1 §11.1.2: clearing S0REN stops reception, but a frame being received is completed with its interrupt.
        var t = Dsl();
        t.M.Scheduler.Advance(1_000);
        t.WriteSfr(0xFFB0, 0x80E7);                                          // S0REN = 0 at 1,000
        t.M.Scheduler.Advance(2_000);
        t.M.Asc0.Receive(0x0011, startBit: 500);
        Assert.Equal((0x0011, 0x80), (t.Sfr(0xFEB2), t.Sfr(0xFF6E) & 0x80));
    }

    [Fact] public void FrameTransmitted_GivesWhereEachFrameStarted()
    {
        var t = Dsl();
        var sent = new List<(ushort, ulong)>();
        t.M.Asc0.FrameTransmitted += (frame, start) => sent.Add((frame, start));
        t.M.Scheduler.Advance(1_000);
        t.WriteSfr(0xFEB0, 0x0055).WriteSfr(0xFEB0, 0x0057);                 // the second starts where the first ends
        t.M.Scheduler.Advance(1_000 + 2 * 27_456);
        Assert.Equal(new[] { ((ushort)0x055, 1_000UL), ((ushort)0x157, 28_456UL) }, sent);
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
        t.M.Asc0.FrameTransmitted += (frame, _) => sent.Add(frame);
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
        t.M.Asc0.Receive(0x0011, t.M.Scheduler.Now);
        Assert.Equal((0, 0), (t.Sfr(0xFEB2), t.Sfr(0xFF6E) & 0x80));
    }

    [Fact] public void FrameInProgress_GivesWhereTheFrameOnTheLineStartsAndEnds()
    {
        var t = Dsl();
        Assert.Null(t.M.Asc0.FrameInProgress);
        t.M.Scheduler.Advance(1_000);
        t.WriteSfr(0xFEB0, 0x0055);                                          // the frame starts at once and takes 11 bits of 2,496 cycles
        Assert.Equal((1_000UL, 28_456UL), t.M.Asc0.FrameInProgress);
        t.M.Scheduler.Advance(28_455);
        Assert.NotNull(t.M.Asc0.FrameInProgress);
        t.M.Scheduler.Advance(28_456);
        Assert.Null(t.M.Asc0.FrameInProgress);                               // idle again in the cycle the frame ends
    }

    [Fact] public void FrameInProgress_ShowsABufferedFrameFromWhereTheFirstEnds_AndNothingAfterAReset()
    {
        var t = Dsl();
        t.WriteSfr(0xFEB0, 0x0055).WriteSfr(0xFEB0, 0x0057);                 // the second waits in S0TBUF
        t.M.Scheduler.Advance(27_456);
        Assert.Equal((27_456UL, 54_912UL), t.M.Asc0.FrameInProgress);
        t.M.Asc0.OnReset();
        Assert.Null(t.M.Asc0.FrameInProgress);
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
