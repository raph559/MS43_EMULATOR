using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Vehicle;

/// <summary>
/// The K-line on a synthetic machine whose ASC0 is set up as the firmware sets it for DS2 (S0CON = 80F7h,
/// S0BG = 4Dh): 8E1 at 2,496 cycles a bit, so an ECU frame lasts 11 × 2,496 = 27,456 cycles. The devices
/// here send at 2,500 cycles a bit (9,600 baud), so a byte lasts 27,500.
/// </summary>
public class KLineBusTests
{
    const uint S0Tbuf = 0xFEB0, S0Rbuf = 0xFEB2, S0Ric = 0xFF6E;

    static TestMachine Dsl() => TestMachine.Create("0DFF").WriteSfr(0xFEB4, 0x004D).WriteSfr(0xFFB0, 0x80F7);

    sealed record Seen(byte Value, bool Own, ulong Cycle);

    /// <summary>A device on the line: it writes down what it hears, and may answer each byte.</summary>
    sealed class Device : IKLineDevice
    {
        public List<Seen> Heard { get; } = new();
        public Action<bool>? Answer { get; set; }

        public void OnLineByte(byte value, bool own, ulong cycle)
        {
            Heard.Add(new Seen(value, own, cycle));
            Answer?.Invoke(own);
        }
    }

    static (TestMachine T, Device Device, List<KLineNote> Notes) Rig()
    {
        var t = Dsl();
        var device = new Device();
        var notes = new List<KLineNote>();
        t.M.KLine.Attach(device);
        t.M.KLine.Noted += notes.Add;
        return (t, device, notes);
    }

    [Fact] public void DeviceByte_ReachesTheEcuAndEchoesBack_AfterElevenBitTimes()
    {
        var (t, device, notes) = Rig();
        t.M.KLine.Send(0x12, 2_500);
        t.M.Scheduler.Advance(27_499);                                       // still on the line
        Assert.Equal((0, 0), (t.Sfr(S0Rbuf), t.Sfr(S0Ric) & 0x80));
        Assert.Empty(device.Heard);
        t.M.Scheduler.Advance(27_500);
        Assert.Equal((0x012, 0x80), (t.Sfr(S0Rbuf), t.Sfr(S0Ric) & 0x80));   // 12h has two 1s: the even parity bit 8 is 0
        Assert.Equal(new[] { new Seen(0x12, true, 27_500) }, device.Heard);
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData(0x12, 0x012)]    // two 1s
    [InlineData(0x0B, 0x10B)]    // three 1s
    [InlineData(0x91, 0x191)]    // three 1s
    [InlineData(0xFF, 0x0FF)]    // eight 1s
    public void DeviceByte_ArrivesWithItsEvenParityBitInBit8(int value, int received)
    {
        var (t, _, _) = Rig();
        t.M.KLine.Send((byte)value, 2_500);
        t.M.Scheduler.Advance(27_500);
        Assert.Equal(received, t.Sfr(S0Rbuf));
    }

    [Fact] public void EcuByte_ReachesTheDevice_AndItsOwnReceiver()
    {
        var (t, device, notes) = Rig();
        t.WriteSfr(S0Tbuf, 0x0057);                                          // five 1s: ASC0 sends 157h
        t.M.Scheduler.Advance(27_455);
        Assert.Empty(device.Heard);
        t.M.Scheduler.Advance(27_456);
        Assert.Equal(new[] { new Seen(0x57, false, 27_456) }, device.Heard);
        Assert.Equal((0x157, 0x80), (t.Sfr(S0Rbuf), t.Sfr(S0Ric) & 0x80));   // its own echo is the frame it sent, parity bit included
        Assert.Empty(notes);
    }

    [Fact] public void EcuByte_WithNoDeviceAttached_StillEchoesIntoItsOwnReceiver()
    {
        var t = Dsl();
        t.WriteSfr(S0Tbuf, 0x0057);
        t.M.Scheduler.Advance(27_456);
        Assert.Equal((0x157, 0x80), (t.Sfr(S0Rbuf), t.Sfr(S0Ric) & 0x80));
    }

    [Fact] public void BaudMismatch_LosesTheByte_AndNotesIt()
    {
        var (t, device, notes) = Rig();
        t.M.KLine.Send(0x12, 192);                                           // 125,000 baud against ASC0's 9,615
        t.M.Scheduler.Advance(2_112);                                        // 11 × 192
        Assert.Equal((0, 0), (t.Sfr(S0Rbuf), t.Sfr(S0Ric) & 0x80));
        Assert.Equal(new[] { new KLineNote(2_112, "mismatch", 0x12) }, notes);
        Assert.Equal(new[] { new Seen(0x12, true, 2_112) }, device.Heard);   // the device still hears itself
    }

    [Theory]
    [InlineData(2_570.38, true)]     // ASC0's bit time is 2,496, so 3 % is 74.88 cycles
    [InlineData(2_571.38, false)]
    [InlineData(2_421.62, true)]
    [InlineData(2_420.62, false)]
    public void ABitTimeWithin3PercentOfAsc0s_IsHeard_AndOtherwiseLost(double bitCycles, bool heard)
    {
        var (t, _, notes) = Rig();
        t.M.KLine.Send(0x12, bitCycles);
        t.M.Scheduler.Advance(40_000);
        Assert.Equal(heard ? (0x012, 0) : (0, 1), (t.Sfr(S0Rbuf), notes.Count));
        Assert.All(notes, n => Assert.Equal("mismatch", n.Kind));
    }

    [Fact] public void OverlappingBytes_AreBothLost_AsACollision()
    {
        var (t, device, notes) = Rig();
        t.M.KLine.Send(0x12, 2_500);                                         // on the line from 0 to 27,500
        t.M.Scheduler.Advance(1_000);
        t.M.KLine.Send(0x34, 2_500);                                         // and from 1,000 to 28,500
        t.M.Scheduler.Advance(28_500);
        Assert.Equal(new[] { new KLineNote(27_500, "collision", 0x12), new KLineNote(28_500, "collision", 0x34) }, notes);
        Assert.Equal((0, 0), (t.Sfr(S0Rbuf), t.Sfr(S0Ric) & 0x80));
        Assert.Equal(new[] { new Seen(0x12, true, 27_500), new Seen(0x34, true, 28_500) }, device.Heard);
    }

    [Fact] public void AnEcuByteAndADeviceByteOnTheLineTogether_AreBothLost()
    {
        var (t, device, notes) = Rig();
        t.WriteSfr(S0Tbuf, 0x0055);                                          // the ECU's frame is on the line from 0 to 27,456
        t.M.Scheduler.Advance(1_000);
        t.M.KLine.Send(0x12, 2_500);                                         // the device's from 1,000 to 28,500
        t.M.Scheduler.Advance(28_500);
        Assert.Equal(new[] { new KLineNote(27_456, "collision", 0x55), new KLineNote(28_500, "collision", 0x12) }, notes);
        Assert.Equal((0, 0), (t.Sfr(S0Rbuf), t.Sfr(S0Ric) & 0x80));          // the ECU's frame did not echo, the device's did not arrive
        Assert.Equal(new[] { new Seen(0x12, true, 28_500) }, device.Heard);  // the device hears only itself
    }

    [Fact] public void ADeviceByteAtTheWrongBaudRate_ThatOverlapsAnEcuByte_IsACollision_NotAMismatch()
    {
        var (t, device, notes) = Rig();
        t.WriteSfr(S0Tbuf, 0x0055);                                          // the ECU's frame: 0 to 27,456
        t.M.Scheduler.Advance(26_000);
        t.M.KLine.Send(0x12, 192);                                           // 125,000 baud, from 26,000 to 28,112
        t.M.Scheduler.Advance(28_112);
        Assert.Equal(new[] { new KLineNote(27_456, "collision", 0x55), new KLineNote(28_112, "collision", 0x12) }, notes);
        Assert.Equal(new[] { new Seen(0x12, true, 28_112) }, device.Heard);
    }

    [Fact] public void AnEcuByteThatOverlapsADeviceByteAlreadyDelivered_IsStillLost()
    {
        // The bus learns of an ECU frame only when it ends. The device's byte ended at 27,500, 500 cycles into
        // the frame (27,000 to 54,456), so it had reached ASC0 by then: what the bus can still do is lose the ECU's.
        var (t, device, notes) = Rig();
        t.M.KLine.Send(0x12, 2_500);
        t.M.Scheduler.Advance(27_000);
        t.WriteSfr(S0Tbuf, 0x0055);
        t.M.Scheduler.Advance(54_456);
        Assert.Equal(new[] { new KLineNote(54_456, "collision", 0x55) }, notes);
        Assert.DoesNotContain(device.Heard, h => !h.Own);
    }

    [Fact] public void AnEcuByteStartingWhereTheDeviceByteEnds_IsNotACollision()
    {
        var (t, device, notes) = Rig();
        t.M.KLine.Send(0x12, 2_500);
        t.M.Scheduler.Advance(27_500);
        t.WriteSfr(S0Tbuf, 0x0034);                                          // the reply begins in the cycle the request ends
        t.M.Scheduler.Advance(54_956);
        Assert.Equal(new[] { new Seen(0x12, true, 27_500), new Seen(0x34, false, 54_956) }, device.Heard);
        Assert.Empty(notes);
    }

    [Fact] public void ABytePlacedWhereThePreviousOneEnds_IsNotACollision_WhicheverEventRunsFirst()
    {
        var (t, device, notes) = Rig();
        var busyThen = true;
        // scheduled before the first byte is sent, so it runs before the bus finishes that byte, in the same cycle
        t.M.Scheduler.Schedule(27_500, "test device", () =>
        {
            busyThen = t.M.KLine.Busy;
            t.M.KLine.Send(0x34, 2_500);
        });
        t.M.KLine.Send(0x12, 2_500);
        t.M.Scheduler.Advance(55_000);
        Assert.False(busyThen);
        Assert.Empty(notes);
        Assert.Equal(new[] { new Seen(0x12, true, 27_500), new Seen(0x34, true, 55_000) }, device.Heard);
        Assert.Equal(0x134, t.Sfr(S0Rbuf));                                  // the second byte arrived: 34h has three 1s, so its parity bit is 1
    }

    [Fact] public void ADeviceCanSendTheNextByteFromItsOwnEcho()
    {
        var (t, device, notes) = Rig();
        var rest = new Queue<byte>(new byte[] { 0x34, 0x56 });
        device.Answer = own =>
        {
            if (own && rest.Count > 0)
                t.M.KLine.Send(rest.Dequeue(), 2_500);
        };
        t.M.KLine.Send(0x12, 2_500);
        t.M.Scheduler.Advance(82_500);
        Assert.Equal(new[] { new Seen(0x12, true, 27_500), new Seen(0x34, true, 55_000), new Seen(0x56, true, 82_500) }, device.Heard);
        Assert.Empty(notes);
        Assert.Equal(0x056, t.Sfr(S0Rbuf));
    }

    [Fact] public void Busy_IsTrueWhileADeviceByteIsOnTheLine()
    {
        var t = Dsl();
        IKLinePort port = t.M.KLine;
        Assert.False(port.Busy);
        port.Send(0x12, 2_500);
        Assert.True(port.Busy);
        t.M.Scheduler.Advance(27_499);
        Assert.True(port.Busy);
        t.M.Scheduler.Advance(27_500);
        Assert.False(port.Busy);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2_500.0)]
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Send_RefusesABitTimeShorterThanACycle_OrNotANumber(double bitCycles)
    {
        var t = Dsl();
        Assert.Throws<ArgumentOutOfRangeException>(() => t.M.KLine.Send(0x12, bitCycles));
        Assert.False(t.M.KLine.Busy);
    }

    [Fact] public void NoDevice_LeavesTheEcuUntouched()
    {
        // Ruling P2: with no device attached and an ECU that sends nothing, the bus schedules no events
        // and changes no machine state. A bus on a scheduler of its own shows what it schedules.
        var t = Dsl();
        var hash = t.M.StateHash();
        var elsewhere = new Scheduler();
        var bus = new KLineBus(elsewhere, t.M.Asc0, t.M.Events);
        elsewhere.Advance(100_000_000);
        Assert.Equal((ulong.MaxValue, false, hash), (elsewhere.NextDue, bus.Busy, t.M.StateHash()));
        Assert.Equal(0L, t.M.Asc0.FramesTransmitted);
    }
}
