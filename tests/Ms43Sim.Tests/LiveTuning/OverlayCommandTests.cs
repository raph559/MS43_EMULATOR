using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The whole command set (plan 6 Task 8): all on and all off (A/B), free and revert, on the overlay as flashed from a blank
/// EEPROM, warm, the requests at 9,600 baud with a status every second. The drive runs once.
/// </summary>
public sealed class OverlayCommandRuns
{
    static string Send(byte[] payload) => OverlayHookRuns.Send(payload);

    const string Head = "drive commands\n" + OverlayRuns.Warm + "3 on\nkline every 1 6D 00\n1.5 crank rpm=200\n3 idle\n";

    /// <summary>
    /// ign98_pl at 0 (its first 120 bytes written again, unchanged, in one write) and ign98_is at 320 on, af98 at 356 loaded
    /// and never confirmed; all off at 8.5 s, all on at 9.5 s; at 10.5 s a free of the on ign98_pl, its off and its free;
    /// revert at 11.5 s.
    /// </summary>
    public static readonly byte[] Longest = LiveOverlay.Write(0, 0, OverlayEditRuns.Flash(0)[..LiveOverlay.MostWrite]);

    public static readonly string AB = Head + Send(LiveOverlay.Load(0, 0)) + Send(LiveOverlay.Load(1, 320)) + Send(LiveOverlay.Load(2, 356))
        + Send(Longest)
        + Send(LiveOverlay.On(0, 0)) + Send(LiveOverlay.On(1, 0)) + "1 idle\n"
        + Send(LiveOverlay.AllOff()) + "1 idle\n"
        + Send(LiveOverlay.AllOn()) + "1 idle\n"
        + Send(LiveOverlay.Free(0)) + Send(LiveOverlay.Off(0)) + Send(LiveOverlay.Free(0)) + "1 idle\n"
        + Send(LiveOverlay.Revert()) + "2 idle\n";

    public OverlayRun ABRun { get; } = OverlayRuns.Run(LiveOverlayBin.Image, AB);
}

/// <summary>
/// Abuse of the command (plan 6 Task 8): frames no handler sees whole and requests the overlay refuses, at idle with tables
/// on, a status after each; the overlay's own writes watched.
/// </summary>
public sealed class OverlayAbuseRuns
{
    static string Send(byte[] payload) => OverlayHookRuns.Send(payload);

    static string Raw(params byte[] bytes) => "kline raw " + string.Join(" ", bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))) + "\n";

    const string Head = "drive abuse\n" + OverlayRuns.Warm + "3 on\nkline every 1 6D 00\n1.5 crank rpm=200\n3 idle\n";

    /// <summary>The frames no handler sees whole: a bad xor, a cut-off frame, 140 bytes, a length byte of 2, no 12h start.</summary>
    public static readonly byte[][] Garbage =
    {
        new byte[] { 0x12, 0x05, 0x6D, 0x00, 0x85 },
        new byte[] { 0x12, 0x08, 0x6D, 0x01, 0x00 },
        new byte[] { 0x12, 0x8C, 0x6D, 0x00 }.Concat(new byte[135]).Append((byte)0xF3).ToArray(),
        new byte[] { 0x12, 0x02, 0x10 },
        new byte[] { 0x13, 0x05, 0x6D, 0x00, 0x7B },
    };

    /// <summary>Requests the overlay refuses, each with its reason, while ign98_pl (at 0) and ign98_is (at 320) are on and af98 (at 356) holds a leaner copy.</summary>
    public static readonly (byte[] Payload, byte Status, OverlayReason Reason)[] Refused =
    {
        (new byte[] { 0x6D }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x00, 0x00 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x01, 0x00, 0x00 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x01, 0x00, 0x00, 0x00, 0x00 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x02, 0x00, 0x00 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x03, 0x00, 0x00 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x03, 0x00, 0x00, 0x00, 0x00 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x04 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x04, 0x00, 0x00 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x05, 0x00 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x06, 0x00 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x07 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x07, 0x00, 0x00 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x08, 0x00 }, 0xB0, OverlayReason.Length),
        (new byte[] { 0x6D, 0x09 }, 0xB0, OverlayReason.Sub),
        (new byte[] { 0x6D, 0xFF }, 0xB0, OverlayReason.Sub),
        (LiveOverlay.Load(5, 0), 0xB0, OverlayReason.Table),
        (LiveOverlay.Load(0xFF, 0), 0xB0, OverlayReason.Table),
        (new byte[] { 0x6D, 0x02, 0x07, 0x00, 0x00, 0xAA }, 0xB0, OverlayReason.Table),
        (LiveOverlay.On(7, 0), 0xB0, OverlayReason.Table),
        (LiveOverlay.Off(0xFF), 0xB0, OverlayReason.Table),
        (LiveOverlay.Free(5), 0xB0, OverlayReason.Table),
        (LiveOverlay.Write(1, 36, new byte[] { 0xAA }), 0xB0, OverlayReason.Range),             // at the table's end
        (new byte[] { 0x6D, 0x02, 0x00, 0x00, 0x00 }.Concat(new byte[121]).ToArray(), 0xB0, OverlayReason.Range),
        (new byte[] { 0x6D, 0x02, 0x00, 0x00, 0x00 }, 0xB0, OverlayReason.Range),               // nothing to write
        (LiveOverlay.Load(3, 501), 0xB0, OverlayReason.Range),                                  // 501 + 192 = 693
        (LiveOverlay.Load(0, 0xFF00), 0xB0, OverlayReason.Range),
        (LiveOverlay.Load(3, 300), 0xB0, OverlayReason.Overlap),                                // ign98_pl ends at 320
        (LiveOverlay.Load(0, 0), 0xB1, OverlayReason.TableOn),
        (LiveOverlay.Write(0, 0, new byte[] { 0xAA }), 0xB1, OverlayReason.TableOn),
        (LiveOverlay.Free(1), 0xB1, OverlayReason.TableOn),
        (LiveOverlay.Write(3, 0, new byte[] { 0xAA }), 0xB1, OverlayReason.NotLoaded),
        (LiveOverlay.On(3, 0), 0xB1, OverlayReason.NotLoaded),
        (LiveOverlay.On(0, 1), 0xB1, OverlayReason.Sum),
        (LiveOverlay.On(2, LiveOverlay.EditSum(OverlayEditRuns.Leaner, OverlayEditRuns.Flash(2))), 0xB1, OverlayReason.Leaner),
    };

    /// <summary>
    /// ign98_pl and ign98_is on, af98 loaded with one cell leaner, at 7.5 s; from 8.5 s each garbage frame and each refused
    /// request, a status after each; a status at the end.
    /// </summary>
    public static readonly string Drive = Head + Send(LiveOverlay.Load(0, 0)) + Send(LiveOverlay.Load(1, 320)) + Send(LiveOverlay.On(0, 0))
        + Send(LiveOverlay.On(1, 0)) + Send(LiveOverlay.Load(2, 356))
        + string.Concat(LiveOverlay.Edit(2, OverlayEditRuns.Leaner, OverlayEditRuns.Flash(2)).Select(Send)) + "1 idle\n"
        + string.Concat(Garbage.Select(g => Raw(g) + Send(LiveOverlay.Status())))
        + string.Concat(Refused.Select(r => Send(r.Payload) + Send(LiveOverlay.Status()))) + "40 idle\n";

    public OverlayRun Run { get; } = OverlayRuns.Run(LiveOverlayBin.Image, Drive, checks: true, watchWrites: true);
}

public class OverlayCommandTests(OverlayCommandRuns f) : IClassFixture<OverlayCommandRuns>
{
    /// <summary>Each handled 0x6D exchange with the windows before and after it, and the cycle of its own window.</summary>
    static IReadOnlyList<(KLineExchange Exchange, OverlayWindow Before, OverlayWindow After, ulong At)> Steps(OverlayRun run)
    {
        var exchanges = run.Overlay;
        Assert.Equal(exchanges.Count, run.Windows.Count);
        return exchanges.Select((e, i) => (e, i == 0 ? run.Windows[0].Window : run.Windows[i - 1].Window, run.Windows[i].Window, run.Windows[i].Cycle)).ToList();
    }

    static (KLineExchange Exchange, OverlayWindow Before, OverlayWindow After, ulong At) Step(OverlayRun run, byte[] payload, int occurrence = 0) =>
        Steps(run).Where(s => s.Exchange.Request.Skip(2).SequenceEqual(payload.Append(s.Exchange.Request[^1]))).ElementAt(occurrence);

    static OverlayReason? Reason(KLineExchange e)
    {
        Assert.Equal(KLineOutcome.Replied, e.Outcome);
        return LiveOverlay.Refusal(e.Reply!);
    }

    static OverlayState[] States(OverlayWindow w, int n = 3) => w.Tables.Take(n).Select(t => t.State).ToArray();

    [Fact] public void AllOff_ThenAllOn_FlipsEveryReadyTable()                       // A/B; a Loaded table stays off
    {
        var run = f.ABRun;
        var off = Step(run, LiveOverlay.AllOff());
        Assert.Null(Reason(off.Exchange));
        Assert.Equal(new[] { OverlayState.Ready, OverlayState.Ready, OverlayState.Loaded }, States(off.After));
        var on = Step(run, LiveOverlay.AllOn());
        Assert.Null(Reason(on.Exchange));
        Assert.Equal(new[] { OverlayState.On, OverlayState.On, OverlayState.Loaded }, States(on.After));
        Assert.Equal(Convert.ToHexString(off.After.Copies), Convert.ToHexString(on.After.Copies));
        Assert.All(run.Reads.Where(r => r.Table.Index < 2 && r.Cycle > off.At && r.Cycle < on.At), r => Assert.False(r.FromCopy));
        Assert.Contains(run.Reads, r => r.Table.Index < 2 && r.Cycle > off.At && r.Cycle < on.At);
        Assert.Contains(run.Reads, r => r.Table.Index == 1 && r.Cycle > on.At && r.FromCopy);      // a warm idle reads ign98_is, not ign98_pl
        Assert.DoesNotContain(run.Reads, r => r.Table.Index == 2 && r.FromCopy);
    }

    [Fact] public void FreeOfAnOnTable_IsRefused()                                   // B1 07
    {
        var run = f.ABRun;
        var refused = Step(run, LiveOverlay.Free(0));
        Assert.Equal(OverlayReason.TableOn, Reason(refused.Exchange));
        Assert.Equal(refused.Before.Tables, refused.After.Tables);
        var freed = Step(run, LiveOverlay.Free(0), occurrence: 1);
        Assert.Null(Reason(freed.Exchange));
        Assert.Equal(OverlayState.Ready, freed.Before.Tables[0].State);
        Assert.Equal(new OverlayEntry(OverlayState.Free, 0, 0), freed.After.Tables[0]);
        Assert.Equal(freed.Before.Tables.Skip(1), freed.After.Tables.Skip(1));
    }

    [Fact] public void Revert_FreesEverything()
    {
        var run = f.ABRun;
        var revert = Step(run, LiveOverlay.Revert());
        Assert.Null(Reason(revert.Exchange));
        Assert.Equal(OverlayState.On, revert.Before.Tables[1].State);                     // an On table too
        Assert.All(revert.After.Tables, t => Assert.Equal(new OverlayEntry(OverlayState.Free, 0, 0), t));
        Assert.All(run.Reads.Where(r => r.Cycle > revert.At), r => Assert.False(r.FromCopy));
        var status = LiveOverlay.ParseStatus(run.Overlay.Last(e => e.Request[3] == 0x00).Reply!);
        Assert.Equal(LiveOverlay.CopyBytes, status.FreeBytes);
    }

    [Fact] public void TheCommands_WorkAt9600()                                        // the app's session before any connect
    {
        Assert.All(f.ABRun.Overlay, e => Assert.Equal((9_600, KLineOutcome.Replied), (e.Baud, e.Outcome)));
        var longest = Step(f.ABRun, OverlayCommandRuns.Longest);
        Assert.Equal(128, longest.Exchange.Request.Count);                                 // inside the firmware's 129
        Assert.Null(Reason(longest.Exchange));
        Assert.Equal(Convert.ToHexString(OverlayEditRuns.Flash(0)), Convert.ToHexString(longest.After.Copies[..320]));
        Assert.Equal(OverlayState.Loaded, longest.After.Tables[0].State);
        var whole = f.ABRun.Overlay.Where(e => e.Request[3] is 0x05 or 0x06 or 0x08).ToList();
        Assert.Equal(3, whole.Count);
        Assert.All(whole, e => Assert.Null(Reason(e)));
    }
}

[Trait("Category", "StandardDrives")]
public class OverlayAbuseTests(OverlayAbuseRuns f) : IClassFixture<OverlayAbuseRuns>
{
    static string Hex(IEnumerable<byte>? bytes) => bytes is null ? "nothing" : Convert.ToHexString(bytes.ToArray());

    /// <summary>The window before the exchange was sent, and the first after it ended: the status that follows each abuse.</summary>
    static void Unchanged(OverlayRun run, KLineExchange e)
    {
        var before = run.Windows.Last(w => w.Cycle < e.SentAt).Window;
        var after = run.Windows.First(w => w.Cycle > e.EndedAt).Window;
        Assert.True(before.Tables.SequenceEqual(after.Tables), $"{Hex(e.Request)} changed the directory");
        Assert.True(before.Copies.AsSpan().SequenceEqual(after.Copies), $"{Hex(e.Request)} changed a copy");
    }

    [Fact] public void EveryAbuse_IsRefused_AndChangesNothing()
    {
        var run = f.Run;
        foreach (var garbage in OverlayAbuseRuns.Garbage)
        {
            var e = run.Result.KLine.Single(x => x.Request.SequenceEqual(garbage));
            Assert.False(e.Outcome == KLineOutcome.Replied && e.Reply![2] is 0xA0 or 0xB0 or 0xB1, $"{Hex(garbage)} was answered {Hex(e.Reply)}");
            Unchanged(run, e);
        }
        foreach (var (payload, status, reason) in OverlayAbuseRuns.Refused)
        {
            var e = run.Result.KLine.Last(x => x.Request.SequenceEqual(Ds2.Frame(payload)));      // load 0 at 0 was the set-up's too
            Assert.True(e.Outcome == KLineOutcome.Replied, $"{Hex(e.Request)}: {e.Outcome}");
            Assert.Equal(Hex(Ds2.Frame(new[] { status, (byte)reason })), Hex(e.Reply));
            Unchanged(run, e);
        }
        var last = run.Overlay[^1];
        Assert.Equal((KLineOutcome.Replied, (byte)0xA0), (last.Outcome, last.Reply![2]));          // a status after them all
        Assert.Single(run.Result.PowerCycles);
        Assert.Equal((0, 0), (run.Result.WatchdogResets, run.Result.SoftwareResets));
        Assert.True(run.Result.Verdict == RunVerdict.Pass, $"{run.Result.Verdict}: " + string.Join("; ", Verdict.Reasons(run.Result.Checks, run.Result.Coverage!)));
    }

    [Fact] public void TheOverlay_WritesOnlyItsWindowTheReplyBufferAndTheStacks()
    {
        var writes = f.Run.OverlayWrites;
        static bool Allowed(OverlayWrite w) =>
            w.Address is >= 0x41D00 and < 0x41FE0                                         // the window, through EXTP #10h
            || w.Address is >= 0xF74E and <= 0xF7CC                                        // the DS2 reply buffer
            || w.Address < 0x10000 && (int)w.Address - w.R0 is >= -16 and < 16             // the user stack
            || w.Address < 0x10000 && (int)w.Address - w.Sp is >= -8 and <= 8;             // the system stack
        var stray = writes.Where(w => !Allowed(w)).Select(w => $"{w.Address:X6} from {w.Pc:X6} (R0 {w.R0:X4}, SP {w.Sp:X4}, CP {w.Cp:X4})").Distinct().ToList();
        Assert.True(stray.Count == 0, "writes elsewhere: " + string.Join("; ", stray.Take(10)));
        Assert.Contains(writes, w => w.Address is >= 0x41D00 and < 0x41FE0);
        Assert.Contains(writes, w => w.Address is >= 0xF74E and <= 0xF7CC);
    }
}
