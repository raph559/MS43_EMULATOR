using Ms43Sim.Core;

namespace Ms43Sim.Tests.Vehicle;

public class CanPartnersTests
{
    [Fact] public void Partners_SendAsc1Every10ms_AndTheClusterEvery200ms_From100ms()
    {
        var s = new Scheduler(); var got = new List<(ulong Cycle, CanFrame Frame)>();
        var p = new CanPartners(s, f => got.Add((s.Now, f)), () => true);
        p.Start(); s.Advance(24_000_000);                                    // 1 s
        Assert.Equal((91L, 5L, 5L), (p.Sent[0x153], p.Sent[0x613], p.Sent[0x615]));
        Assert.Equal(2_400_000UL, got.First(g => g.Frame.Id == 0x153).Cycle);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00 }, got.First(g => g.Frame.Id == 0x615).Frame.Data);
    }

    [Fact] public void Partners_AreSilentWhileTheKeyIsOff()
    {
        var s = new Scheduler(); var got = 0;
        new CanPartners(s, _ => got++, () => false).Start();
        s.Advance(24_000_000);
        Assert.Equal(0, got);
    }

    [Fact] public void Contents_FollowTheCarState()
    {
        var s = new Scheduler(); var got = new List<CanFrame>();
        var state = new CarState { SpeedKmh = 50, Ac = true, AmbientC = -10, ClockMinutes = 754 };
        new CanPartners(s, got.Add, () => true, () => state).Start();
        s.Advance(12_000_000);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x19, 0xFF, 0x00, 0x00, 0xFF, 0x00 }, got.First(f => f.Id == 0x153).Data);
        var instr3 = got.First(f => f.Id == 0x615).Data;
        Assert.Equal((0x80, 0x8A), (instr3[0] & 0x80, (int)instr3[3]));                  // the A/C request; -10 °C, sign-magnitude
        Assert.Equal(new byte[] { 0x10, 0x27, 0x28, 0xF2, 0x02, 0x14, 0x05, 0x00 }, got.First(f => f.Id == 0x613).Data);   // 754 min = 02F2h
    }

    [Theory]
    [InlineData(0.0, 0x00, 0x00)]
    [InlineData(50.0, 0x00, 0x19)]
    [InlineData(73.5, 0xC0, 0x24)]
    [InlineData(100.0, 0x00, 0x32)]
    [InlineData(150.0, 0x00, 0x4B)]
    public void Speed_IsSixteenthsOfAKmh_InBytes1And2(double kmh, int b1, int b2) =>
        Assert.Equal(new byte[] { 0x00, (byte)b1, (byte)b2, 0xFF, 0x00, 0x00, 0xFF, 0x00 }, CanPartners.Asc1(new CarState { SpeedKmh = kmh }).Data);

    [Theory]
    [InlineData(20.0, 0x14)]
    [InlineData(0.0, 0x00)]
    [InlineData(-10.0, 0x8A)]
    [InlineData(-40.0, 0xA8)]
    [InlineData(50.0, 0x32)]
    public void Ambient_IsSignMagnitude_InByte3(double celsius, int code) =>
        Assert.Equal(code, CanPartners.Instr3(new CarState { AmbientC = celsius }).Data[3]);

    [Fact] public void TheContents_AreReadAsEachFrameGoes_AndASilencedIdentifierIsNotSent()
    {
        var s = new Scheduler(); var got = new List<CanFrame>();
        var state = new CarState { SpeedKmh = 73.5 };
        var p = new CanPartners(s, got.Add, () => true, () => state);
        p.Start(); s.Advance(2_400_000);                                     // the first 153h, at 100 ms
        state = state with { Silenced = new HashSet<uint> { 0x153 } };
        s.Advance(26_400_000);                                               // to 1.1 s
        Assert.Equal((1L, 5L), (p.Sent[0x153], p.Sent[0x613]));
        Assert.Equal(0xC0, got.Single(f => f.Id == 0x153).Data[1]);
    }
}
