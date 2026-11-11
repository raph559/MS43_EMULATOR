using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>One sample of a safety drive: the cycle, whether the engine runs, and the states of tables 0 and 1.</summary>
public sealed record SafetySample(ulong Cycle, bool Running, byte[] States);

/// <summary>A safety drive's result with its samples, taken every 10 ms and kept when something changed.</summary>
public sealed record SafetyRun(DriveResult Result, IReadOnlyList<SafetySample> Samples)
{
    /// <summary>The exchanges of sub-command <paramref name="sub"/> sent after <paramref name="after"/> and before <paramref name="before"/>.</summary>
    public IReadOnlyList<KLineExchange> Sent(byte sub, ulong after = 0, ulong before = ulong.MaxValue) =>
        Result.KLine.Where(e => e.Request[2] == LiveOverlay.Command && e.Request[3] == sub && e.SentAt > after && e.SentAt < before).ToList();
}

/// <summary>
/// The safety drives, each run once on the V10 image from a blank EEPROM, warm: the main ignition map and the idle
/// ignition map loaded as identical copies and switched on at idle, then a stall, a key cycle through the relay's drop, or
/// the key on with the engine stopped.
/// </summary>
public sealed class OverlaySafetyRuns
{
    const int On = 3;

    static string Mutation(byte[] request, uint revision) => V10Fixture.Send(V10Fixture.Mutate(request, revision));

    static string SwitchOn(int table, uint revision) => Mutation(LiveOverlay.On(table, LiveOverlay.Crc32(V10Fixture.Flash(table))), revision);

    /// <summary>Both tables loaded and switched on: revisions 0 to 3.</summary>
    static readonly string Head = V10Fixture.Head + Mutation(V10Fixture.Load(0, 0), 0) + Mutation(V10Fixture.Load(1, 704), 1)
        + SwitchOn(0, 2) + SwitchOn(1, 3);

    /// <summary>A stall at 9.5 s; both on asked while the engine is stopped, then again once it runs.</summary>
    public static readonly string Stall = Head + "2 idle\n3 stall\n" + SwitchOn(0, 4) + SwitchOn(1, 4)
        + "1.5 crank rpm=200\n3 idle\n" + SwitchOn(0, 4) + SwitchOn(1, 5) + "2 idle\n";

    /// <summary>The key off through the after-run and the relay's drop; the key on, a start, a session and both on asked again.</summary>
    public static readonly string KeyCycle = Head + "5 idle\n30 off\n3 on\n1.5 crank rpm=200\n3 idle\n" + V10Fixture.Send(V10Fixture.Open())
        + SwitchOn(0, 0) + SwitchOn(1, 0) + "3 idle\n";

    /// <summary>The key on with the engine stopped: a session, a load, then an on.</summary>
    public static readonly string Stopped = "drive stopped\n" + V10Fixture.Warm + "3 on\nkline every 1 6D 00\n" + V10Fixture.Send(V10Fixture.Open())
        + Mutation(V10Fixture.Load(0, 0), 0) + SwitchOn(0, 1) + "5 on\n";

    public OverlaySafetyRuns()
    {
        StallRun = Run(Stall, checks: true);
        KeyCycleRun = Run(KeyCycle, checks: true);
        StoppedRun = Run(Stopped, checks: false);
    }

    public SafetyRun StallRun { get; }

    public SafetyRun KeyCycleRun { get; }

    public SafetyRun StoppedRun { get; }

    public static bool BothOn(SafetySample s) => s.States.All(state => state == On);

    public static bool NoneOn(SafetySample s) => s.States.All(state => state != On);

    static SafetyRun Run(string text, bool checks)
    {
        var samples = new List<SafetySample>();
        void Attach(DriveContext context) => context.PowerCycleStarted += (machine, _) =>
        {
            var origin = context.PowerCycleStart;
            void Sample(ulong at) => machine.Scheduler.Schedule(at, "safety-sample", () =>
            {
                var running = (machine.Bus.Peek16(0xF900) & 0xFF) == 2 && (machine.Bus.Peek16(0xFD2C) & 0x1E00) != 0;
                byte[] states = [machine.Bus.Xram[0x6B8], machine.Bus.Xram[0x6C0]];
                if (samples.Count == 0 || samples[^1].Running != running || !samples[^1].States.SequenceEqual(states))
                    samples.Add(new SafetySample(origin + machine.Cycles, running, states));
                Sample(at + 240_000);
            });
            Sample(0);
        };
        var options = new DriveRunOptions(Observers: [Attach]);
        var drive = DriveParser.Parse(text);
        return new SafetyRun(checks ? DriveRun.RunWithChecks(V10Fixture.Image, drive, options) : DriveRun.Run(V10Fixture.Image, drive, options), samples);
    }
}

[Trait("Category", "StandardDrives")]
public class OverlaySafetyTests(OverlaySafetyRuns f, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<OverlaySafetyRuns>
{
    const double Second = 24e6;

    static byte? Reason(KLineExchange e)
    {
        Assert.Equal(KLineOutcome.Replied, e.Outcome);
        return e.Reply![2] == 0xA0 ? null : e.Reply[3];
    }

    static SafetySample When(SafetyRun run, ulong after, Func<SafetySample, bool> test) => run.Samples.First(s => s.Cycle > after && test(s));

    static void Passes(SafetyRun run) => Assert.True(run.Result.Verdict == RunVerdict.Pass,
        $"{run.Result.Verdict}: " + string.Join("; ", Verdict.Reasons(run.Result.Checks, run.Result.Coverage!)));

    /// <summary>The engine stops: both tables are off in the sample where the running signal falls, or in the next.</summary>
    (ulong Stopped, SafetySample Off) StopsAndSwitchesOff(SafetyRun run)
    {
        var on = When(run, 0, OverlaySafetyRuns.BothOn).Cycle;
        var stopped = When(run, on, s => !s.Running).Cycle;
        var off = When(run, on, OverlaySafetyRuns.NoneOn);
        output.WriteLine($"running false at {stopped / Second:0.000} s, everything off at {off.Cycle / Second:0.000} s, states {off.States[0]} {off.States[1]}");
        Assert.InRange((long)off.Cycle - (long)stopped, 0, 240_000);
        return (stopped, off);
    }

    [Fact] public void AStall_SwitchesEverythingOff_AndTheRestartNeedsTheAppAgain()
    {
        var run = f.StallRun;
        var (stopped, off) = StopsAndSwitchesOff(run);
        Assert.Equal(new byte[] { 2, 2 }, off.States);                                           // ready: off, the copies kept
        var running = When(run, stopped, s => s.Running).Cycle;
        var refused = run.Sent(3, stopped, running);
        Assert.Equal(2, refused.Count);
        Assert.All(refused, e => Assert.Equal((byte)OverlayReason.NotRunning, Reason(e)));
        Assert.All(run.Samples.Where(s => s.Cycle > stopped && s.Cycle <= running), s => Assert.True(OverlaySafetyRuns.NoneOn(s)));
        var again = run.Sent(3, running);
        Assert.Equal(2, again.Count);
        Assert.All(again, e => Assert.Null(Reason(e)));
        Assert.True(OverlaySafetyRuns.BothOn(run.Samples[^1]));
        Passes(run);
    }

    [Fact] public void AKeyCycle_StartsOnTheFlashedMaps()
    {
        var run = f.KeyCycleRun;
        StopsAndSwitchesOff(run);
        Assert.Equal(2, run.Result.PowerCycles.Count);
        var second = run.Result.PowerCycles[1].StartCycle;
        Assert.All(run.Samples.Where(s => s.Cycle > second), s => Assert.Equal(new byte[] { 0, 0 }, s.States));   // free: nothing loaded
        var ons = run.Sent(3, second);
        Assert.Equal(2, ons.Count);
        Assert.All(ons, e => Assert.Equal((byte)OverlayReason.NotLoaded, Reason(e)));
        Passes(run);
    }

    [Fact] public void WithTheEngineStopped_NothingSwitchesOn()
    {
        var run = f.StoppedRun;
        Assert.Null(Reason(Assert.Single(run.Sent(1))));
        Assert.Equal((byte)OverlayReason.NotRunning, Reason(Assert.Single(run.Sent(3))));
        Assert.All(run.Samples, s => Assert.True(OverlaySafetyRuns.NoneOn(s)));
    }
}
