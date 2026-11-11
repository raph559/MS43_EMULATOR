using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>The watch in whole drives (sim/drives/watch): the K-line tester logs with sub 11 at full speed.</summary>
public sealed class WatchDriveTests
{
    const uint PartLoad = 0x77315, Idle = 0x775D4, Overrun = 0x7717D, Nothing = 0x70002;   // Overrun: ip_iga_puc__n__tco, read through the overrun cut
    static readonly byte[] Log = Ds2.Frame(new byte[] { 0x6D, 0x0B });

    static FlashedImage Image => TestImages.AsFlashed(V10Fixture.Bytes);

    static string Text(string name) => File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot, "sim/drives/watch", name + ".drive"));

    static (DriveResult Result, WatchOracle Oracle) Run(string text, uint address, bool checks = false)
    {
        var oracle = new WatchOracle(address);
        var options = new DriveRunOptions(Observers: new Action<DriveContext>[] { oracle.Attach });
        var drive = DriveParser.Parse(text);
        return (checks ? DriveRun.RunWithChecks(Image, drive, options) : DriveRun.Run(Image, drive, options), oracle);
    }

    static List<KLineExchange> Logs(DriveResult r) =>
        r.KLine.Where(e => e.Request.SequenceEqual(Log) && e.Outcome == KLineOutcome.Replied).ToList();

    /// <summary>The record bytes of a sub-11 reply: after 12, the length, A0 and the 73 stock bytes.</summary>
    static byte[] RecordOf(KLineExchange e) => e.Reply!.Skip(76).Take(11).ToArray();

    [Fact]
    public void EveryLogReplyCarriesTheLastLibraryRead()
    {
        var (r, oracle) = Run(Text("part-load-watch"), PartLoad);
        var logs = Logs(r);
        Assert.True(logs.Count > 500, $"only {logs.Count} log replies");
        var checkedReplies = 0;
        foreach (var e in logs)
        {
            Assert.Equal(88, e.Reply!.Count);
            Assert.Equal(0x58, e.Reply[1]);
            Assert.Equal(0, e.Reply.Aggregate((byte)0, (a, b) => (byte)(a ^ b)));    // the XOR checksum over the frame
            var copy = oracle.Copies.LastOrDefault(c => c >= e.SentAt && c <= e.RepliedAt);
            Assert.True(copy != 0, $"no copy for the reply at {e.RepliedAt}");
            var expected = oracle.Expected(copy);
            var actual = RecordOf(e);
            if (expected[0] == 0) Assert.Equal(expected[..2], actual[..2]);
            else Assert.True(expected.SequenceEqual(actual),
                $"at {copy / 24e6:F3} s: expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}");
            checkedReplies++;
        }
        Assert.True(oracle.Reads.Count > 1000, $"only {oracle.Reads.Count} reads of the watched map");
        Console.WriteLine($"part-load-watch: {checkedReplies} replies against the oracle, {oracle.Reads.Count} reads");
    }

    [Fact]
    public void IdleMapIsReadAtIdleAndStopsBeingRead()
    {
        var (r, oracle) = Run(Text("idle-watch"), Idle);
        var logs = Logs(r);
        // The blip leaves idle at 4.5 + 20 + 5 = 29.5 s of the drive.
        var blip = 29.5 * 24e6;
        Assert.Contains(oracle.Reads, x => x.Cycle < blip - 24e6 && x.Cycle > blip - 5 * 24e6);
        Assert.DoesNotContain(oracle.Reads, x => x.Cycle > blip + 24e6 && x.Cycle < blip + 3 * 24e6);
        byte CountAt(double seconds) => RecordOf(logs.Last(e => e.RepliedAt <= seconds * 24e6))[1];
        Assert.NotEqual(CountAt(27), CountAt(28.5));                     // moving at idle
        Assert.Equal(CountAt(30.5), CountAt(32));                        // stopped within 1 s of leaving idle
    }

    [Fact]
    public void OverrunMapIsReadInOverrun()
    {
        var (_, oracle) = Run(Text("overrun-watch"), Overrun);
        // The lift runs from 4.5 + 6 + 6 = 16.5 s to 26.5 s.
        Assert.Contains(oracle.Reads, x => x.Cycle > 18 * 24e6 && x.Cycle < 25 * 24e6);
    }

    public static TheoryData<string, uint> Drives => new()
    {
        { "part-load-watch", PartLoad }, { "idle-watch", Idle }, { "overrun-watch", Overrun },
    };

    [Theory, MemberData(nameof(Drives))]
    public void WatchingAMapChangesNoEngineOutput(string name, uint address)
    {
        var text = Text(name);
        var watched = Run(text, address, checks: true).Result;
        var elsewhere = Run(text.Replace($"kline watch {address:X}", $"kline watch {Nothing:X}"), Nothing, checks: true).Result;
        // The record's writes cost a few cycles a read. That moves an injection by a few cycles, and the simulated engine's
        // idle control carries the shift on, so sparks and injections are not cycle-equal. What the watch must not change is
        // what V8 against V7 is held to: the faults, the power cycles, the verdict and every check.
        var differences = BehaviourDiff.Of(RunTrack.From(elsewhere), RunTrack.From(watched), watched.Drive, CompareTolerances.Default);
        var serious = differences.Where(d => d.Signal.StartsWith("faults") || d.Signal.StartsWith("power_cycles")).ToList();
        Assert.True(serious.Count == 0, $"{name}: " + string.Join("; ", serious));
        Assert.Equal(RunVerdict.Pass, watched.Verdict);
        Assert.Equal(elsewhere.Verdict, watched.Verdict);
        Assert.Equal(elsewhere.Checks.Select(c => (c.Name, c.Status)), watched.Checks.Select(c => (c.Name, c.Status)));
    }

    [Fact]
    public void LoggingWithTheWatchKeepsItsRateAndItsSchedule()
    {
        const string head = "drive kline-pause-v9\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n"
            + "3 on\n1.5 crank rpm=200\n5 idle\n";
        static string Drive(string poll, string watch) => head + $"kline connect\nkline pause 0\nkline poll {poll}\n1 idle\n"
            + watch + "4 idle\n8 drive gear=3 speed=50..150 pedal=95\n";
        static (double Rate, int Missed, int Polls) Rate(DriveResult r, byte[] frame)
        {
            var polls = r.KLine.Where(e => e.Request.SequenceEqual(frame)).ToList();
            var replied = polls.Where(p => p.Outcome == KLineOutcome.Replied).ToList();
            var seconds = (replied[^1].RepliedAt!.Value - replied[0].RepliedAt!.Value) / 24e6;
            return ((replied.Count - 1) / seconds, polls.Count - replied.Count, polls.Count);
        }
        var (r, _) = Run(Drive("6D 0B", $"kline watch {PartLoad:X}\n"), PartLoad, checks: true);
        var (rate, missed, polls) = Rate(r, Log);
        Assert.True(r.Check("scheduler").Status == CheckStatus.Pass, string.Join("; ", r.Check("scheduler").Findings.Select(f => f.Message)));
        Assert.True(missed * 50 <= polls, $"{missed} of {polls} polls missed");
        // 21.73 replies a second on this drive, as when the watch was added; the stock 0B B0 gave 20.99 on the overlay before it.
        Assert.True(rate >= 20.15, $"6D 0B with the watch {rate:F2}/s");
    }
}
