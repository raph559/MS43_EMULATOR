using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// Load, on and off, and the stubs' substitution (plan 6 Task 5), on the overlay as flashed from a blank EEPROM, warm. The
/// requests go at 9,600 baud with a status every second (the keep-alive the stubs' silence rule needs). Each drive runs once.
/// </summary>
public sealed class OverlayHookRuns
{
    public const string Head = "drive hooks\n" + OverlayRuns.Warm + "3 on\nkline every 1 6D 00\n";

    /// <summary>
    /// ign98_is loaded at key-on; an on during the crank; at idle the refused loads (one of them of a table past the five)
    /// and a refused sum, then ign98_is on at 7.5 s, a load of it while on, and off at 11.5 s.
    /// </summary>
    public static readonly string Commands = Head + Send(LiveOverlay.Load(1, 0)) + "1.5 crank rpm=200\n"
        + Send(LiveOverlay.On(1, 0)) + "3 idle\n"
        + Send(LiveOverlay.Load(5, 0)) + Send(LiveOverlay.Load(0, 373)) + Send(LiveOverlay.Load(0, 372))
        + Send(LiveOverlay.Load(2, 356)) + Send(LiveOverlay.On(1, 1)) + Send(LiveOverlay.On(1, 0)) + "1 idle\n"
        + Send(LiveOverlay.Load(1, 0)) + "3 idle\n"
        + Send(LiveOverlay.Off(1)) + "3 idle\n";

    /// <summary>Two or three tables loaded at idle and on, then a part-load pull that reads them.</summary>
    public static string Sites(params (int Table, int Offset)[] tables) => Head + "1.5 crank rpm=200\n3 idle\n"
        + string.Concat(tables.Select(t => Send(LiveOverlay.Load(t.Table, t.Offset))))
        + string.Concat(tables.Select(t => Send(LiveOverlay.On(t.Table, 0)))) + "2 idle\n"
        + "6 drive gear=3 speed=30..70 pedal=25\n4 drive gear=3 speed=70..100 pedal=70\n";

    public static string Send(byte[] payload) => LiveOverlay.KLineSend(payload) + "\n";

    readonly Lazy<OverlayRun> commands = new(() => OverlayRuns.Run(LiveOverlayBin.Image, Commands));
    readonly Lazy<(OverlayRun A, OverlayRun B)> sites = new(() =>
    {
        var runs = new[] { Sites((0, 0), (1, 320), (2, 356)), Sites((3, 0), (4, 192)) }
            .AsParallel().AsOrdered().Select(text => OverlayRuns.Run(LiveOverlayBin.Image, text)).ToArray();
        return (runs[0], runs[1]);
    });

    public OverlayRun CommandRun => commands.Value;

    public (OverlayRun A, OverlayRun B) SiteRuns => sites.Value;
}

public class OverlayHookTests(OverlayHookRuns f) : IClassFixture<OverlayHookRuns>
{
    static readonly byte[] Car = TestBins.CarBytes;

    /// <summary>Each 0x6D exchange with the window as the handler left it; the window before it is the previous one's.</summary>
    static IReadOnlyList<(KLineExchange Exchange, OverlayWindow Before, OverlayWindow After)> Steps(OverlayRun run)
    {
        var exchanges = run.Overlay;
        Assert.Equal(exchanges.Count, run.Windows.Count);                                // every request was handled once
        return exchanges.Select((e, i) => (e, i == 0 ? run.Windows[0].Window : run.Windows[i - 1].Window, run.Windows[i].Window)).ToList();
    }

    static (KLineExchange Exchange, OverlayWindow Before, OverlayWindow After) Step(OverlayRun run, byte[] payload, int occurrence = 0) =>
        Steps(run).Where(s => s.Exchange.Request.Skip(2).Take(payload.Length).SequenceEqual(payload) && s.Exchange.Request.Count == payload.Length + 3)
            .ElementAt(occurrence);

    static void Unchanged(OverlayWindow before, OverlayWindow after)
    {
        Assert.Equal(before.Tables, after.Tables);
        Assert.Equal(Convert.ToHexString(before.Copies), Convert.ToHexString(after.Copies));
    }

    static OverlayReason? Reason(KLineExchange e)
    {
        Assert.Equal(KLineOutcome.Replied, e.Outcome);
        return LiveOverlay.Refusal(e.Reply!);
    }

    [Fact] public void ALoadedCopy_IsTheFlashedTable()
    {
        var load = Step(f.CommandRun, LiveOverlay.Load(1, 0));
        Assert.Null(Reason(load.Exchange));
        Assert.Equal(new OverlayEntry(OverlayState.Loaded, 0, 0), load.After.Tables[1]);
        Assert.Equal(Convert.ToHexString(Car, 0x775D4, 36), Convert.ToHexString(load.After.Copies, 0, 36));
    }

    [Fact] public void ATable_IsReadFromItsCopy_OnlyWhileOn()
    {
        var run = f.CommandRun;
        var on = run.Windows.First(w => w.Window.Tables[1].State == OverlayState.On).Cycle;
        var off = run.Windows.First(w => w.Cycle > on && w.Window.Tables[1].State != OverlayState.On).Cycle;
        var reads = run.Reads.Where(r => r.Table.Index == 1).ToList();
        Assert.Contains(reads, r => r.Cycle < on);
        Assert.Contains(reads, r => r.Cycle > on && r.Cycle < off);
        Assert.Contains(reads, r => r.Cycle > off);
        foreach (var r in reads)
        {
            var copy = r.Cycle > on && r.Cycle < off;
            var expected = copy ? (0x1D2C, 0x10) : (0x35D4, 0x1D);
            Assert.True((r.R12, r.R13) == expected, $"{r.Seconds:0.000} s: R12/R13 {r.R12:X4}/{r.R13:X2}, expected {expected.Item1:X4}/{expected.Item2:X2}");
        }
    }

    [Fact] public void EverySite_ReadsItsCopy()
    {
        var (a, b) = f.SiteRuns;
        foreach (var (run, tables) in new[] { (a, new[] { (0, 0), (1, 320), (2, 356) }), (b, new[] { (3, 0), (4, 192) }) })
        {
            foreach (var (table, offset) in tables)
            {
                var on = run.Windows.First(w => w.Window.Tables[table].State == OverlayState.On).Cycle;
                var reads = run.Reads.Where(r => r.Table.Index == table && r.Cycle > on).ToList();
                Assert.True(reads.Count > 0, $"table {table} is never read after its switch-on");
                Assert.All(reads, r => Assert.Equal((0x1D2C + offset, 0x10), ((int)r.R12, (int)r.R13)));
                Assert.All(run.Reads.Where(r => r.Table.Index == table && r.Cycle < on), r => Assert.False(r.FromCopy));
            }
        }
    }

    [Fact] public void OnWhileStarting_IsRefused()
    {
        var step = Step(f.CommandRun, LiveOverlay.On(1, 0));
        Assert.Equal(OverlayReason.NotRunning, Reason(step.Exchange));
        Unchanged(step.Before, step.After);
    }

    [Fact] public void ATablePastTheFive_IsRefused()
    {
        var step = Step(f.CommandRun, LiveOverlay.Load(5, 0));
        Assert.Equal(OverlayReason.Table, Reason(step.Exchange));
        Unchanged(step.Before, step.After);
    }

    [Fact] public void ACopyPastTheArea_OrOverlapping_IsRefused()
    {
        var past = Step(f.CommandRun, LiveOverlay.Load(0, 373));                         // 373 + 320 = 693 > 692
        Assert.Equal(OverlayReason.Range, Reason(past.Exchange));
        Unchanged(past.Before, past.After);
        var fits = Step(f.CommandRun, LiveOverlay.Load(0, 372));
        Assert.Null(Reason(fits.Exchange));
        Assert.Equal(new OverlayEntry(OverlayState.Loaded, 372, 0), fits.After.Tables[0]);
        var overlap = Step(f.CommandRun, LiveOverlay.Load(2, 356));                      // 356–547 meets ign98_pl at 372
        Assert.Equal(OverlayReason.Overlap, Reason(overlap.Exchange));
        Unchanged(overlap.Before, overlap.After);
    }

    [Fact] public void LoadWhileOn_IsRefused()
    {
        var step = Step(f.CommandRun, LiveOverlay.Load(1, 0), occurrence: 1);
        Assert.Equal(OverlayReason.TableOn, Reason(step.Exchange));
        Unchanged(step.Before, step.After);
    }

    [Fact] public void OnWithAWrongSum_IsRefused()
    {
        var step = Step(f.CommandRun, LiveOverlay.On(1, 1));
        Assert.Equal(OverlayReason.Sum, Reason(step.Exchange));
        Unchanged(step.Before, step.After);
        var off = Step(f.CommandRun, LiveOverlay.Off(1));
        Assert.Null(Reason(off.Exchange));
        Assert.Equal(new OverlayEntry(OverlayState.Ready, 0, 0), off.After.Tables[1]);
    }

    [Fact] public void OtherSubCommands_AreRefused()
    {
        var run = OverlayRuns.Run(LiveOverlayBin.Image, "drive subs\n3 on\n" + OverlayHookRuns.Send(new byte[] { 0x6D, 0x09 })
            + OverlayHookRuns.Send(new byte[] { 0x6D, 0xFF }) + "2 on\n");
        Assert.All(run.Overlay, e => Assert.Equal(OverlayReason.Sub, Reason(e)));
        Assert.Equal(2, run.Overlay.Count);
    }
}

/// <summary>
/// Identical copies on, on a warm start with a part-load pull (plan 6 Task 5), each set against its own baseline: the same
/// drive whose switch-ons carry a wrong sum, so nothing goes on. Equality of behaviour holds only between identical
/// instruction streams: the warm idle's after-start transitions move by up to 2 s when the CPU's timing at idle changes (a
/// refused request's work is enough), so a run with copies on is judged by what its readers read and by what it keeps
/// (plan 6 record, Task 5). The pull is there for the cam tables, which a warm idle never reads.
/// </summary>
public sealed class IdenticalCopyRuns
{
    static string Drive(bool on, params (int Table, int Offset)[] tables) =>
        "drive warm-start\n" + OverlayRuns.Warm + "3 on\nkline every 1 6D 00\n1.5 crank rpm=200\n3 idle\n"
        + string.Concat(tables.Select(t => OverlayHookRuns.Send(LiveOverlay.Load(t.Table, t.Offset))))
        + string.Concat(tables.Select(t => OverlayHookRuns.Send(LiveOverlay.On(t.Table, on ? (ushort)0 : (ushort)1))))   // a wrong sum keeps it off
        + "17 idle\n6 drive gear=3 speed=30..70 pedal=25\n4 drive gear=3 speed=70..100 pedal=70\n5 drive gear=3 speed=100..20 pedal=0\n"
        + "5 idle\nkline quiet\n15 off\n";

    public static readonly (int Table, int Offset)[][] Sets = { new[] { (0, 0), (1, 320), (2, 356) }, new[] { (3, 0), (4, 192) } };

    public IdenticalCopyRuns()
    {
        var runs = Sets.SelectMany(set => new[] { (On: true, Set: set), (On: false, Set: set) }).AsParallel().AsOrdered()
            .Select(v => (Text: Drive(v.On, v.Set), Run: OverlayRuns.Run(LiveOverlayBin.Image, Drive(v.On, v.Set), checks: true, watchWindow: v.On)))
            .ToList();
        Pairs = Enumerable.Range(0, Sets.Length)
            .Select(i => (Set: Sets[i], On: runs[2 * i].Run, Baseline: runs[2 * i + 1].Run, Drive: DriveParser.Parse(runs[2 * i].Text))).ToList();
    }

    public IReadOnlyList<((int Table, int Offset)[] Set, OverlayRun On, OverlayRun Baseline, Drive Drive)> Pairs { get; }
}

[Trait("Category", "StandardDrives")]
public class IdenticalCopyTests(IdenticalCopyRuns f) : IClassFixture<IdenticalCopyRuns>
{
    [Fact] public void IdenticalCopies_AreReadExactlyAsTheFlash()
    {
        var car = TestBins.CarBytes;
        foreach (var (set, on, _, _) in f.Pairs)
        {
            Assert.All(on.Overlay.Where(e => e.Request[3] == 0x03), e => Assert.Null(LiveOverlay.Refusal(e.Reply!)));     // really on
            var copies = on.Windows[^1].Window;
            foreach (var (table, offset) in set)
            {
                var t = LiveOverlay.Tables[table];
                Assert.Equal(new OverlayEntry(OverlayState.On, offset, 0), copies.Tables[table]);
                Assert.Equal(Convert.ToHexString(car, t.FileOffset, t.Bytes), Convert.ToHexString(copies.Copies, offset, t.Bytes));
            }
            Assert.NotEmpty(on.WindowReads);
            foreach (var (cycle, offset, table) in on.WindowReads)
            {
                var placed = set.Single(p => p.Table == table.Index).Offset;
                Assert.True(offset >= placed && offset < placed + table.Bytes,
                    $"{cycle / 24e6:0.000} s: a read of {table.Name} at copy offset {offset}, outside its copy {placed}–{placed + table.Bytes - 1}");
            }
        }
    }

    [Fact] public void IdenticalCopiesOn_KeepEveryCheck_AndChangeNoFaultOrPowerCycle()
    {
        // The learned values the after-run writes to the EEPROM follow the idle's timing too: on the car file alone, six more
        // status requests (answered FF) move seven of them (plan 6 record, Task 5). Faults and power cycles do not drift.
        foreach (var (set, on, baseline, drive) in f.Pairs)
        {
            foreach (var (table, _) in set)
                Assert.True(on.Reads.Any(r => r.Table.Index == table && r.FromCopy), $"{LiveOverlay.Tables[table].Name} is never read from its copy");
            Assert.DoesNotContain(baseline.Reads, r => r.FromCopy);
            Assert.Equal((RunVerdict.Pass, RunVerdict.Pass), (on.Result.Verdict, baseline.Result.Verdict));
            var compare = Core.Compare.Of(LiveOverlayBin.Image, LiveOverlayBin.Image, drive, baseline.Result, on.Result);
            var kept = compare.Behaviour.Where(d => d.Signal is "faults" or "power_cycles").ToList();
            Assert.True(kept.Count == 0, string.Join("; ", kept.Select(d => $"{d.Signal}: {d.Reference} → {d.Candidate}")));
        }
    }
}
