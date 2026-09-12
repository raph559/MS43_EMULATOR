using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// Edits, their sums and the richer-only lambda rule (plan 6 Task 6), on the overlay as flashed from a blank EEPROM, warm,
/// the requests at 9,600 baud with a status every second. Each drive runs once.
/// </summary>
public sealed class OverlayEditRuns
{
    const string Head = "drive edits\n" + OverlayRuns.Warm + "3 on\nkline every 1 6D 00\n1.5 crank rpm=200\n3 idle\n";

    static string Send(byte[] payload) => OverlayHookRuns.Send(payload);

    static string Sends(IEnumerable<byte[]> payloads) => string.Concat(payloads.Select(Send));

    public static readonly byte[] Car = TestBins.CarBytes;

    public static byte[] Flash(int table) => Car.AsSpan(LiveOverlay.Tables[table].FileOffset, LiveOverlay.Tables[table].Bytes).ToArray();

    /// <summary>ign98_pl with every cell one step up, written in its three chunks.</summary>
    public static byte[] PlusOne => Flash(0).Select(b => (byte)(b + 1)).ToArray();

    /// <summary>af98 with its first cell one step leaner (128 → 127).</summary>
    public static byte[] Leaner => Flash(2).Select((b, i) => i == 0 ? (byte)(b - 1) : b).ToArray();

    /// <summary>
    /// ign98_pl at 0: two of its three chunks, an on that fails its sum, the third chunk, the on; a write while on; off, a
    /// write that leaves it Loaded. af98 at 320, one cell leaner, refused.
    /// </summary>
    public static string Edits()
    {
        var writes = LiveOverlay.Edit(0, PlusOne, Flash(0));
        var sum = LiveOverlay.EditSum(PlusOne, Flash(0));
        return Head + Send(LiveOverlay.Load(0, 0)) + Send(writes[0]) + Send(writes[1]) + Send(LiveOverlay.On(0, sum))
            + Send(writes[2]) + Send(LiveOverlay.On(0, sum))
            + Send(LiveOverlay.Write(0, 0, PlusOne[..1]))
            + Send(LiveOverlay.Off(0)) + Send(LiveOverlay.Write(0, 0, PlusOne[..1]))
            + Send(LiveOverlay.Load(2, 320)) + Sends(LiveOverlay.Edit(2, Leaner, Flash(2))) + Send(LiveOverlay.On(2, LiveOverlay.EditSum(Leaner, Flash(2))))
            + "2 idle\n";
    }

    /// <summary>ign98_is at 0 written whole and switched on: every cell two steps up, or (identical) as flashed.</summary>
    public static string IdleIgnition(bool edited)
    {
        var copy = Flash(1).Select(b => edited ? (byte)(b + 2) : b).ToArray();
        return Head + Send(LiveOverlay.Load(1, 0)) + Send(LiveOverlay.Write(1, 0, copy)) + Send(LiveOverlay.On(1, LiveOverlay.EditSum(copy, Flash(1))))
            + "3 idle\n";
    }

    public OverlayEditRuns()
    {
        var runs = new[] { Edits(), IdleIgnition(true), IdleIgnition(false) }.AsParallel().AsOrdered()
            .Select(text => OverlayRuns.Run(LiveOverlayBin.Image, text)).ToArray();
        (EditRun, IdleEdited, IdleIdentical) = (runs[0], runs[1], runs[2]);
    }

    public OverlayRun EditRun { get; }

    public OverlayRun IdleEdited { get; }

    public OverlayRun IdleIdentical { get; }
}

public class OverlayEditTests(OverlayEditRuns f) : IClassFixture<OverlayEditRuns>
{
    static IReadOnlyList<(KLineExchange Exchange, OverlayWindow Before, OverlayWindow After)> Steps(OverlayRun run)
    {
        var exchanges = run.Overlay;
        Assert.Equal(exchanges.Count, run.Windows.Count);
        return exchanges.Select((e, i) => (e, i == 0 ? run.Windows[0].Window : run.Windows[i - 1].Window, run.Windows[i].Window)).ToList();
    }

    static IReadOnlyList<(KLineExchange Exchange, OverlayWindow Before, OverlayWindow After)> Steps(OverlayRun run, byte sub) =>
        Steps(run).Where(s => s.Exchange.Request[2] == LiveOverlay.Command && s.Exchange.Request[3] == sub).ToList();

    static OverlayReason? Reason(KLineExchange e)
    {
        Assert.Equal(KLineOutcome.Replied, e.Outcome);
        return LiveOverlay.Refusal(e.Reply!);
    }

    static void Unchanged(OverlayWindow before, OverlayWindow after)
    {
        Assert.Equal(before.Tables, after.Tables);
        Assert.Equal(Convert.ToHexString(before.Copies), Convert.ToHexString(after.Copies));
    }

    [Fact] public void AnIdleIgnitionEdit_RaisesTheDecidedAdvanceByItsSteps()
    {
        // The edited run and the identical one execute the same requests, so their timing is the same until the edit's own
        // sparks move it; the comparison is held to the 0.3 s after the switch-on (plan 6 record, Task 5).
        var on = f.IdleEdited.Windows.First(w => w.Window.Tables[1].State == OverlayState.On).Cycle;
        Assert.Equal(on, f.IdleIdentical.Windows.First(w => w.Window.Tables[1].State == OverlayState.On).Cycle);
        var edited = f.IdleEdited.Result.Sparks.Where(s => s.Main && s.Fire > on && s.Fire < on + 7_200_000).ToList();
        var identical = f.IdleIdentical.Result.Sparks.Where(s => s.Main && s.Fire > on && s.Fire < on + 7_200_000).ToList();
        Assert.True(edited.Count >= 10, $"{edited.Count} sparks");
        Assert.Equal(identical.Select(s => s.Cylinder), edited.Select(s => s.Cylinder));
        Assert.All(edited.Zip(identical), p => Assert.Equal(p.Second.DecidedAdvance + 0.75, p.First.DecidedAdvance));
        var before = f.IdleEdited.Result.Sparks.Where(s => s.Main && s.Fire < on).Select(s => s.DecidedAdvance);
        Assert.Equal(f.IdleIdentical.Result.Sparks.Where(s => s.Main && s.Fire < on).Select(s => s.DecidedAdvance), before);
    }

    [Fact] public void AWriteWithAMissingChunk_FailsItsSum()                           // Review Focus 3
    {
        var ons = Steps(f.EditRun, 0x03).Where(s => s.Exchange.Request[4] == 0).ToList();
        Assert.Equal(OverlayReason.Sum, Reason(ons[0].Exchange));
        Unchanged(ons[0].Before, ons[0].After);
        Assert.Null(Reason(ons[1].Exchange));
        Assert.Equal(OverlayState.On, ons[1].After.Tables[0].State);
        Assert.Equal(Convert.ToHexString(OverlayEditRuns.PlusOne), Convert.ToHexString(ons[1].After.Copies, 0, 320));
    }

    [Fact] public void ALeanerLambdaCell_IsRefused()
    {
        var on = Steps(f.EditRun, 0x03).Single(s => s.Exchange.Request[4] == 2);
        Assert.Equal(OverlayReason.Leaner, Reason(on.Exchange));
        Unchanged(on.Before, on.After);
    }

    [Fact] public void AWriteToAnOnTable_IsRefused()
    {
        var write = Steps(f.EditRun, 0x02).Single(s => s.Before.Tables[0].State == OverlayState.On);
        Assert.Equal(OverlayReason.TableOn, Reason(write.Exchange));
        Unchanged(write.Before, write.After);
    }

    [Fact] public void AWriteToAReadyTable_MakesItLoaded()
    {
        var write = Steps(f.EditRun, 0x02).Single(s => s.Before.Tables[0].State == OverlayState.Ready);
        Assert.Null(Reason(write.Exchange));
        Assert.Equal(OverlayState.Loaded, write.After.Tables[0].State);
    }
}
