using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

public sealed class OverlayV2Runs
{
    static string Send(byte[] p) => OverlayHookRuns.Send(p);
    static string Head => "drive protocol-v2\n" + OverlayRuns.Warm + "3 on\nkline every 1 6D 00\n1.5 crank rpm=200\n3 idle\n";
    public OverlayRun Integrity { get; } = OverlayRuns.Run(LiveOverlayBin.Image, IntegrityDrive());
    public OverlayRun Limits { get; } = OverlayRuns.Run(LiveOverlayBin.Image, LimitsDrive());

    static string IntegrityDrive()
    {
        var flash = OverlayEditRuns.Flash(0);
        var intended = flash.ToArray();
        intended[0]++; intended[1]--; intended[200]++; intended[201]--;
        var swapped = flash.ToArray();
        (swapped[0], swapped[4]) = (swapped[4], swapped[0]);
        return Head + Send(LiveOverlay.Load(0, 0))
            + Send(LiveOverlay.Write(0, 0, intended[..2])) + Send(LiveOverlay.On(0, LiveOverlay.Crc32(intended)))
            + Send(LiveOverlay.Write(0, 200, intended[200..202])) + Send(LiveOverlay.On(0, LiveOverlay.Crc32(intended)))
            + Send(LiveOverlay.Off(0)) + Send(LiveOverlay.Load(0, 0))
            + Send(LiveOverlay.Write(0, 0, swapped[..5])) + Send(LiveOverlay.On(0, LiveOverlay.Crc32(flash)))
            + Send(LiveOverlay.On(0, LiveOverlay.Crc32(swapped)))
            + Send(new byte[] { 0x6D, 3, 0, 0, 0 }) + "2 idle\n";
    }

    static string LimitsDrive()
    {
        var text = Head;
        for (var table = 0; table < 5; table++)
        {
            var flash = OverlayEditRuns.Flash(table);
            var at = Array.FindIndex(flash, b => b >= 9 && b <= 246);
            foreach (var delta in table == 2 ? new[] { 9, 8, -1 } : table == 0 ? new[] { 9, 8, -9, -8, -flash[at], 255 - flash[at] } : new[] { 9, 8, -9, -8 })
            {
                var copy = flash.ToArray(); copy[at] = (byte)(copy[at] + delta);
                text += Send(LiveOverlay.Revert()) + Send(LiveOverlay.Load(table, 0))
                    + Send(LiveOverlay.Write(table, at, copy.AsSpan(at, 1))) + Send(LiveOverlay.On(table, LiveOverlay.Crc32(copy))) + "0.8 idle\n";
            }
        }
        return text + "2 idle\n";
    }
}

public class OverlayV2Tests(OverlayV2Runs f) : IClassFixture<OverlayV2Runs>
{
    static IReadOnlyList<KLineExchange> Ons(OverlayRun run) => run.Overlay.Where(e => e.Request[3] == 3).ToList();

    [Fact] public void BalancedMissingChunk_AndPermutation_CannotActivateWithTheIntendedCrc()
    {
        var ons = Ons(f.Integrity);
        Assert.Equal(5, ons.Count);
        Assert.Equal(OverlayReason.Integrity, LiveOverlay.Refusal(ons[0].Reply!));
        Assert.Null(LiveOverlay.Refusal(ons[1].Reply!));
        Assert.Equal(OverlayReason.Integrity, LiveOverlay.Refusal(ons[2].Reply!));
        Assert.Null(LiveOverlay.Refusal(ons[3].Reply!));
        Assert.Equal(OverlayReason.Length, LiveOverlay.Refusal(ons[4].Reply!));
        foreach (var index in new[] { 0, 2 })
        {
            var after = f.Integrity.Windows[f.Integrity.Overlay.ToList().IndexOf(ons[index])].Window;
            Assert.NotEqual(OverlayState.On, after.Tables[0].State);
        }
    }

    [Fact] public void EveryTable_EnforcesBothRawDeltaEdges_AndAfrNeverLeans()
    {
        var ons = Ons(f.Limits);
        Assert.Equal(21, ons.Count);
        var n = 0;
        for (var table = 0; table < 5; table++)
        {
            var expected = table == 2
                ? new OverlayReason?[] { OverlayReason.EditLimit, null, OverlayReason.Leaner }
                : table == 0 ? new OverlayReason?[] { OverlayReason.EditLimit, null, OverlayReason.EditLimit, null, OverlayReason.EditLimit, OverlayReason.EditLimit }
                : new OverlayReason?[] { OverlayReason.EditLimit, null, OverlayReason.EditLimit, null };
            foreach (var reason in expected)
                Assert.Equal(reason, LiveOverlay.Refusal(ons[n++].Reply!));
        }
    }
}
