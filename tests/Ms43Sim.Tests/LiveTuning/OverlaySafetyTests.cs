using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The safety rules (plan 6 Task 7): an engine stop and 3 s without a 0x6D frame switch every table off, a switch-off holds
/// until the app switches the table on again, and every start runs on the flashed maps. Each drive runs once, on the
/// overlay as flashed from a blank EEPROM, warm, with ign98_pl and ign98_is loaded as identical copies and switched on at
/// 7.5 s, at idle.
/// </summary>
public sealed class OverlaySafetyRuns
{
    static string Send(byte[] payload) => OverlayHookRuns.Send(payload);

    static readonly string BothOn = Send(LiveOverlay.On(0, 0)) + Send(LiveOverlay.On(1, 0));

    static readonly string Head = "drive safety\n" + OverlayRuns.Warm + "3 on\nkline every 1 6D 00\n1.5 crank rpm=200\n3 idle\n"
        + Send(LiveOverlay.Load(0, 0)) + Send(LiveOverlay.Load(1, 320)) + BothOn;

    static readonly string Start = Head + "5 idle\n";

    /// <summary>The app falls silent at 12.5 s while the tester keeps polling; at 20.5 s it speaks again and switches both on.</summary>
    public static readonly string Silence = Start + "kline quiet\nkline poll\n8 idle\nkline quiet\nkline every 1 6D 00\n" + BothOn + "3 idle\n";

    /// <summary>
    /// A stall at 9.5 s, plan 5's restart timing (a stall at 12.5 s fails checks on the car file alone); both on at the
    /// second crank (12.5 s), and again once the engine runs (17 s).
    /// </summary>
    public static readonly string Stall = Head + "2 idle\n3 stall\n" + BothOn + "1.5 crank rpm=200\n3 idle\n" + BothOn + "2 idle\n";

    /// <summary>The key off at 12.5 s through the after-run and the relay's drop; the key on at 42.5 s, a start, both on at 50 s.</summary>
    public static readonly string KeyCycle = Start + "30 off\n3 on\n1.5 crank rpm=200\n3 idle\n" + BothOn + "3 idle\n";

    /// <summary>The key off at 12.5 s and back on after <paramref name="off"/> s, cranking at once; both on 3 s later.</summary>
    public static string QuickKeyCycle(double off) =>
        Start + $"{off.ToString(System.Globalization.CultureInfo.InvariantCulture)} off\n3 crank rpm=200\n3 idle\n" + BothOn + "3 idle\n";

    /// <summary>The key on with the engine stopped: a load, a write, an on and an off at 3 s.</summary>
    public static readonly string Stopped = "drive stopped\n" + OverlayRuns.Warm + "3 on\nkline every 1 6D 00\n"
        + Send(LiveOverlay.Load(0, 0)) + Send(LiveOverlay.Write(0, 0, OverlayEditRuns.Flash(0)[..1])) + Send(LiveOverlay.On(0, 0))
        + Send(LiveOverlay.Off(0)) + "5 on\n";

    public OverlaySafetyRuns()
    {
        var texts = new[] { Silence, Stall, KeyCycle, QuickKeyCycle(3), QuickKeyCycle(11), Stopped };
        var runs = texts.AsParallel().AsOrdered().Select((text, i) => OverlayRuns.Run(LiveOverlayBin.Image, text, checks: i < 5)).ToArray();
        (SilenceRun, StallRun, KeyCycleRun, QuickRun, PowerDownRun, StoppedRun) = (runs[0], runs[1], runs[2], runs[3], runs[4], runs[5]);
    }

    public OverlayRun SilenceRun { get; }

    public OverlayRun StallRun { get; }

    public OverlayRun KeyCycleRun { get; }

    public OverlayRun QuickRun { get; }

    public OverlayRun PowerDownRun { get; }

    public OverlayRun StoppedRun { get; }
}

[Trait("Category", "StandardDrives")]
public class OverlaySafetyTests(OverlaySafetyRuns f, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<OverlaySafetyRuns>
{
    const double Second = 24e6;

    static OverlayReason? Reason(KLineExchange e)
    {
        Assert.Equal(KLineOutcome.Replied, e.Outcome);
        return LiveOverlay.Refusal(e.Reply!);
    }

    static bool BothOn(OverlayRunSample s) => s.States[0] == OverlayState.On && s.States[1] == OverlayState.On;

    static bool NoneOn(OverlayRunSample s) => s.States.All(x => x != OverlayState.On);

    /// <summary>The first sample after <paramref name="after"/> for which <paramref name="test"/> holds.</summary>
    static OverlayRunSample When(OverlayRun run, ulong after, Func<OverlayRunSample, bool> test) => run.Samples.First(s => s.Cycle > after && test(s));

    static IReadOnlyList<KLineExchange> Ons(OverlayRun run, ulong after, ulong before = ulong.MaxValue) =>
        run.Overlay.Where(e => e.Request[3] == 0x03 && e.SentAt > after && e.SentAt < before).ToList();

    static void Passes(OverlayRun run) => Assert.True(run.Result.Verdict == RunVerdict.Pass,
        $"{run.Result.Verdict}: " + string.Join("; ", Verdict.Reasons(run.Result.Checks, run.Result.Coverage!)));

    /// <summary>The engine stops: every table is off in the sample where the running signal falls, or in the next.</summary>
    static (ulong Stopped, ulong Off) StopsAndSwitchesOff(OverlayRun run)
    {
        var on = When(run, 0, BothOn).Cycle;
        var stopped = When(run, on, s => !s.Running).Cycle;
        var off = When(run, on, NoneOn);
        Assert.InRange((long)off.Cycle - (long)stopped, 0, 240_000);
        Assert.Equal(new[] { OverlayState.Ready, OverlayState.Ready }, off.States.Take(2));    // off, the copies kept
        return (stopped, off.Cycle);
    }

    /// <summary>After a reset: the first 0x6D frame finds the window wiped, nothing is loaded again, every read is the flash's.</summary>
    static void StartsWiped(OverlayRun run)
    {
        Assert.Equal(2, run.Result.PowerCycles.Count);
        var second = run.Result.PowerCycles[1].StartCycle;
        var first = run.Windows.First(w => w.Cycle > second);
        Assert.All(first.Window.Tables, t => Assert.Equal(new OverlayEntry(OverlayState.Free, 0, 0), t));
        Assert.All(run.Samples.Where(s => s.Cycle > first.Cycle), s => Assert.All(s.States, x => Assert.Equal(OverlayState.Free, x)));
        Assert.All(run.Reads.Where(r => r.Cycle > second), r => Assert.False(r.FromCopy));
        var ons = Ons(run, second);
        Assert.Equal(2, ons.Count);
        Assert.All(ons, e => Assert.Equal(OverlayReason.NotLoaded, Reason(e)));
    }

    [Fact] public void Silence_SwitchesEverythingOff_EvenWhilePolling()                    // Review Focus 5
    {
        var run = f.SilenceRun;
        var on = When(run, 0, BothOn).Cycle;
        var stamp = run.Windows.Where(w => w.Cycle < 12.5 * Second).Max(w => w.Cycle);        // the handler's last frame
        var off = When(run, on, NoneOn);
        output.WriteLine($"silence: everything off {(off.Cycle - stamp) / Second:0.0000} s after the handler's last frame");
        Assert.InRange((off.Cycle - stamp) / Second, 2.99, 3.02);
        Assert.Equal(new[] { OverlayState.Ready, OverlayState.Ready }, off.States.Take(2));
        Assert.Contains(run.Result.KLine, e => e.SentAt > stamp && e.SentAt < off.Cycle && e.Request[2] != LiveOverlay.Command);   // the tester polled on
        var back = When(run, off.Cycle, BothOn).Cycle;
        Assert.InRange(back / Second, 20.5, 21.0);
        Assert.All(run.Reads.Where(r => r.Cycle > off.Cycle && r.Cycle < back), r => Assert.False(r.FromCopy));
        Assert.Contains(run.Reads, r => r.Cycle > off.Cycle && r.Cycle < back);
        Assert.Contains(run.Reads, r => r.Cycle > back && r.FromCopy);
        var copies = run.Windows.First(w => w.Cycle > off.Cycle).Window.Copies;                // the copies stayed through the silence
        Assert.Equal(Convert.ToHexString(OverlayEditRuns.Flash(0)), Convert.ToHexString(copies, 0, 320));
        Assert.Equal(Convert.ToHexString(OverlayEditRuns.Flash(1)), Convert.ToHexString(copies, 320, 36));
        Passes(run);
    }

    [Fact] public void AStall_SwitchesEverythingOff_AndTheRestartRunsOnTheFlashedMaps()
    {
        var run = f.StallRun;
        var (stopped, off) = StopsAndSwitchesOff(run);
        output.WriteLine($"stall: running false at {stopped / Second:0.000} s, everything off at {off / Second:0.000} s");
        var running = When(run, stopped, s => s.Running).Cycle;
        var refused = Ons(run, stopped, running);
        Assert.Equal(2, refused.Count);
        Assert.All(refused, e => Assert.Equal(OverlayReason.NotRunning, Reason(e)));
        Assert.All(run.Reads.Where(r => r.Cycle > stopped && r.Cycle < running), r => Assert.False(r.FromCopy));
        Assert.All(Ons(run, running), e => Assert.Null(Reason(e)));
        Assert.Contains(run.Reads, r => r.Cycle > running && r.FromCopy);
        Passes(run);
    }

    [Fact] public void AKeyCycle_StartsOnTheFlashedMaps()                                    // Review Focus 2
    {
        var run = f.KeyCycleRun;
        StopsAndSwitchesOff(run);
        StartsWiped(run);
        Passes(run);
    }

    [Fact] public void AQuickKeyCycleInTheAfterRun_StartsOnTheFlashedMaps()
    {
        var run = f.QuickRun;
        Assert.Single(run.Result.PowerCycles);                                                   // no reset: the copies stay
        var (stopped, _) = StopsAndSwitchesOff(run);
        var running = When(run, stopped, s => s.Running).Cycle;
        Assert.All(run.Reads.Where(r => r.Cycle > stopped && r.Cycle < running), r => Assert.False(r.FromCopy));
        var ons = Ons(run, running);
        Assert.Equal(2, ons.Count);
        Assert.All(ons, e => Assert.Null(Reason(e)));
        Assert.Contains(run.Reads, r => r.Cycle > running && r.FromCopy);
        Passes(run);
    }

    [Fact] public void AQuickKeyCycleDuringThePowerDownWrite_StartsOnTheFlashedMaps()
    {
        var run = f.PowerDownRun;
        StopsAndSwitchesOff(run);
        StartsWiped(run);
        Passes(run);
    }

    [Fact] public void WithTheEngineStopped_NothingSwitchesOn()
    {
        var run = f.StoppedRun;
        var replies = run.Overlay.Where(e => e.Request[3] != 0x00).Select(e => (e.Request[3], Reason(e))).ToList();
        Assert.Equal(new (byte, OverlayReason?)[] { (0x01, null), (0x02, null), (0x03, OverlayReason.NotRunning), (0x04, null) }, replies);
        var statuses = run.Overlay.Where(e => e.Request[3] == 0x00).ToList();
        Assert.NotEmpty(statuses);
        Assert.All(statuses, e => Assert.False(LiveOverlay.ParseStatus(e.Reply!).Running));
        Assert.DoesNotContain(run.Reads, r => r.FromCopy);
    }
}
