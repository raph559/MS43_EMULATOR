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
}
