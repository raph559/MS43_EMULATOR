using System.Globalization;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Drives;

/// <summary>
/// The drives the K-line tests share, each run once: the app's session at a warm idle, and the same tester across a
/// power cycle. The car file as flashed, from a blank EEPROM.
/// </summary>
public sealed class KLineRuns
{
    const string Head = "drive kline\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n";

    /// <summary>Warm idle; connect and poll 10 s; quiet and disconnect; 1 s later poll at 9,600 for 2 s.</summary>
    public const string Session = "3 on\n1.5 crank rpm=200\n5 idle\nkline connect\nkline poll\n10 idle\n"
        + "kline quiet\nkline disconnect\n1 idle\nkline poll\n2 idle\n";

    /// <summary>
    /// A connect before the boot has set the ECU's port up; a connect at idle and polling; the key off until the relay
    /// drops; the key on again with the tester still polling at 125,000; a connect 3 s into the second power cycle.
    /// </summary>
    public const string Cycle = "kline connect\n3 on\n1.5 crank rpm=200\n5 idle\nkline connect\nkline poll\n5 idle\n"
        + "15 off\n3 on\nkline connect\n5 on\n";

    readonly Lazy<DriveResult> session = new(() => Run(Session));
    readonly Lazy<DriveResult> cycle = new(() => Run(Cycle));

    public static DriveResult Run(string body) => DriveRun.Run(TestImages.Car, DriveParser.Parse(Head + body));

    public DriveResult SessionRun => session.Value;

    public DriveResult CycleRun => cycle.Value;
}

public class KLineDriveTests : IClassFixture<KLineRuns>
{
    static readonly byte[] Connect = { 0x12, 0x08, 0x91, 0x01, 0xE8, 0x48, 0x00, 0x2A };
    static readonly byte[] Disconnect = { 0x12, 0x08, 0x91, 0x00, 0x25, 0x80, 0x00, 0x2E };
    static readonly byte[] Poll = { 0x12, 0x05, 0x0B, 0xB0, 0xAC };
    static readonly byte[] Acknowledged = { 0x12, 0x04, 0xA0, 0xB6 };

    readonly KLineRuns runs;

    public KLineDriveTests(KLineRuns runs) => this.runs = runs;

    static bool Is(KLineExchange e, byte[] request) => e.Request.SequenceEqual(request);

    static double Seconds(ulong cycle) => cycle / 24e6;

    /// <summary>The polls between the first answered connect and the disconnect.</summary>
    static IReadOnlyList<KLineExchange> FastPolls(DriveResult r)
    {
        var exchanges = r.KLine.ToList();
        var connect = exchanges.FindIndex(e => Is(e, Connect) && e.Outcome == KLineOutcome.Replied);
        var disconnect = exchanges.FindIndex(e => Is(e, Disconnect));
        return exchanges.Skip(connect + 1).Take((disconnect < 0 ? exchanges.Count : disconnect) - connect - 1).ToList();
    }

    [Fact] public void KLineCsv_HasThePartialAndStrayRows()             // plan 5 review M3
    {
        var r = KLineRuns.Run("3 on\n") with
        {
            KLine = new[]
            {
                new KLineExchange(24_000, Hex.Bytes("12 05 0B B0 AC"), 125_000, null, null, KLineOutcome.TimedOut, 9_800_000)
                    { Partial = Hex.Bytes("12 4D A0") },
            },
            KLineNotes = new[] { new KLineNote(48_000, "stray", 0x55, "ecu") },
        };
        Assert.Equal(new[]
        {
            "cycle,time_ms,direction,outcome,bytes",
            "24000,1.000,tester,request,12 05 0B B0 AC",
            "48000,2.000,ecu,stray,55",
            "9800000,408.333,ecu,timeout,12 4D A0",
        }, DriveReportWriter.KLineCsv(r).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact] public void Car_AnswersTheAppsConnect_AndItsPolls_AsTheAppsRetryNeeds()
    {
        // The app gives each sample two attempts (Ds2Session.PollRetryCount): a poll the ECU misses must be followed by one
        // it answers. The misses are the ECU's (its S0R, at interrupt level 3, overrun under load): the line loses nothing.
        var r = runs.SessionRun;
        var connect = r.KLine.First(e => Is(e, Connect));
        Assert.Equal((KLineOutcome.Replied, 9_600), (connect.Outcome, connect.Baud));
        Assert.Equal(Acknowledged, connect.Reply);
        var polls = FastPolls(r);
        Assert.True(polls.Count >= 100, $"{polls.Count} polls in 10 s");
        Assert.Equal(KLineOutcome.Replied, polls[0].Outcome);            // 20 ms after the connect's reply, the ECU is at 125,000
        Assert.All(polls, p =>
        {
            Assert.Equal(Poll, p.Request);
            Assert.Equal(125_000, p.Baud);
        });
        Assert.All(polls.Where(p => p.Outcome != KLineOutcome.Replied), p => Assert.Equal(KLineOutcome.TimedOut, p.Outcome));
        Assert.All(polls.Where(p => p.Outcome == KLineOutcome.Replied), p =>
        {
            Assert.Equal(77, p.Reply!.Count);
            Assert.Equal(new byte[] { 0x12, 0x4D, 0xA0 }, p.Reply.Take(3));
            Assert.True(Ds2.IsFrame(p.Reply));
        });
        var missed = polls.Count(p => p.Outcome == KLineOutcome.TimedOut);
        Assert.True(missed * 50 <= polls.Count, $"{missed} of {polls.Count} polls missed");
        for (var i = 1; i < polls.Count; i++)
            Assert.False(polls[i - 1].Outcome == KLineOutcome.TimedOut && polls[i].Outcome == KLineOutcome.TimedOut, $"polls {i - 1} and {i} both missed");
        Assert.Empty(r.KLineNotes);
    }

    [Fact] public void Car_PollFrame_CarriesTheRpmTheFirmwareHolds()
    {
        // The ADX's "Engine Speed": payload offset 4 (frame bytes 7-8), 16 bits, least significant byte first (flags 0x02, the
        // app's LsbFirst), X & 8191.
        var r = runs.SessionRun;
        var column = VariableTrace.Columns.ToList().IndexOf("rpm_ecu");
        foreach (var poll in FastPolls(r).Where(p => p.Outcome == KLineOutcome.Replied))
        {
            var rpm = (poll.Reply![7] | (poll.Reply[8] << 8)) & 0x1FFF;
            var at = Seconds(poll.RepliedAt!.Value);
            var sample = r.Trace.MinBy(row => Math.Abs(row.Seconds - at))!;
            var held = sample.Values[column]!.Value;
            Assert.True(Math.Abs(rpm - held) <= held * 0.01, $"{rpm} rpm in the frame at {at:0.000} s, the firmware held {held}");
        }
    }

    [Fact] public void Car_Disconnect_ReturnsTo9600()
    {
        var r = runs.SessionRun;
        var exchanges = r.KLine.ToList();
        var disconnect = exchanges.FindIndex(e => Is(e, Disconnect));
        Assert.True(disconnect > 0);
        Assert.Equal((KLineOutcome.Replied, 125_000), (exchanges[disconnect].Outcome, exchanges[disconnect].Baud));
        Assert.Equal(Acknowledged, exchanges[disconnect].Reply);
        var slow = exchanges.Skip(disconnect + 1).ToList();
        Assert.True(slow.Count >= 5, $"{slow.Count} polls at 9,600 in 2 s");
        Assert.All(slow, p => Assert.Equal((KLineOutcome.Replied, 9_600, 77), (p.Outcome, p.Baud, p.Reply!.Count)));
    }

    [Fact] public void ConnectBeforeTheEcuIsUp_TimesOut_AndNothingThrows()
    {
        var r = runs.CycleRun;
        var first = r.KLine[0];
        Assert.True(Is(first, Connect));
        Assert.Equal(KLineOutcome.TimedOut, first.Outcome);
        Assert.Contains(r.KLineNotes, n => n.Kind == "mismatch" && n.Cycle < first.SentAt + 24_000_000);
        Assert.Contains(r.KLine, e => Is(e, Connect) && e.Outcome == KLineOutcome.Replied);
    }

    [Fact] public void AfterAPowerCycle_TheTesterIsMismatched_UntilItConnectsAgain()
    {
        var r = runs.CycleRun;
        Assert.Equal(2, r.PowerCycles.Count);
        var second = r.PowerCycles[1].StartCycle;
        var later = r.KLine.Where(e => e.SentAt >= second).ToList();
        var reconnect = later.FindIndex(e => Is(e, Connect) && e.Outcome == KLineOutcome.Replied);
        Assert.True(reconnect > 0, "the tester connects again in the second power cycle");
        Assert.All(later.Take(reconnect), e => Assert.NotEqual(KLineOutcome.Replied, e.Outcome));
        Assert.Contains(r.KLineNotes, n => n.Kind == "mismatch" && n.Cycle >= second && n.Cycle < later[reconnect].SentAt);
        var after = later.Skip(reconnect + 1).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, p => Assert.Equal((KLineOutcome.Replied, 125_000), (p.Outcome, p.Baud)));
    }

    [Fact] public void KLineFiles_AndReportSection_AreWritten()
    {
        var r = runs.SessionRun;
        using var dir = new TempDirectory();
        DriveReportWriter.Write(r, dir.Path);
        var lines = File.ReadAllLines(dir.File("kline.csv"));
        Assert.Equal("cycle,time_ms,direction,outcome,bytes", lines[0]);
        Assert.Contains(lines, l => l.EndsWith(",tester,request,12 08 91 01 E8 48 00 2A"));
        Assert.Contains(lines, l => l.EndsWith(",ecu,reply,12 04 A0 B6"));
        var report = File.ReadAllText(dir.File("drive-report.md"));
        Assert.Contains("## K-line", report);
        Assert.Contains("K-line tester: the app's DS2 session (scripted)", report);
    }

    [Fact] public void KLineCsv_PutsATimeoutWhenTheTesterGaveUp_AndEachLostByteWithTheEndThatSentIt()
    {
        // The first connect leaves before the boot has set ASC0 up: its eight bytes (11 bits of 2,500 cycles) are lost at
        // the ECU as the tester's, and the tester gives up 500 ms after the last one came back.
        var r = runs.CycleRun;
        var first = r.KLine[0];
        var gaveUp = first.SentAt + 8 * 27_500 + 12_000_000;
        Assert.Equal(gaveUp, first.EndedAt);
        var lines = DriveReportWriter.KLineCsv(r).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains($"{gaveUp},{(gaveUp / 24e3).ToString("0.000", CultureInfo.InvariantCulture)},ecu,timeout,", lines);
        Assert.Contains($"{first.SentAt + 27_500},{((first.SentAt + 27_500) / 24e3).ToString("0.000", CultureInfo.InvariantCulture)},tester,mismatch,12", lines);
        Assert.All(lines.Skip(1), l => Assert.Matches("^[0-9]+,[0-9]+\\.[0-9]{3},(tester|ecu),(request|reply|timeout|malformed|mismatch|collision),", l));
        var cycles = lines.Skip(1).Select(l => ulong.Parse(l[..l.IndexOf(',')], CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(cycles.Order(), cycles);
    }

    [Fact] public void KLineReport_CountsTheExchanges_AndListsTheFirstTenProblems()
    {
        var r = runs.CycleRun;
        var report = DriveReportWriter.Report(r);
        var section = report[report.IndexOf("## K-line", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n## ", StringComparison.Ordinal)];
        int Count(KLineOutcome o) => r.KLine.Count(e => e.Outcome == o);
        Assert.Contains($"- requests: {r.KLine.Count}; replies: {Count(KLineOutcome.Replied)}; timeouts: {Count(KLineOutcome.TimedOut)}; malformed: {Count(KLineOutcome.Malformed)}", section);
        Assert.Contains($"- bytes lost: {r.KLineNotes.Count(n => n.Kind == "mismatch")} to a baud rate more than 3 % from the other end's, {r.KLineNotes.Count(n => n.Kind == "collision")} in collisions", section);
        Assert.Contains($"- stray ECU bytes, no part of any reply: {r.KLineNotes.Count(n => n.Kind == "stray")}; timeouts with part of a frame: {r.KLine.Count(e => e.Partial.Count > 0)}", section);
        var problems = section.Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains(" s: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(10, problems.Count);
        string T(ulong cycle) => (cycle / 24e6).ToString("0.000", CultureInfo.InvariantCulture);
        var first = r.KLine[0];
        Assert.Equal($"- {T(first.SentAt + 27_500)} s: the tester's byte 12 was lost: its baud rate is more than 3 % from the other end's", problems[0]);
        Assert.Contains($"- {T(first.EndedAt)} s: no reply to 12 08 91 01 E8 48 00 2A, sent at {T(first.SentAt)} s at 9,600 baud", problems);
        Assert.Contains("more in kline.csv", section);
    }

    [Fact] public void ADriveWithoutKLineLines_IsUnchanged()
    {
        // The state hash of this drive on the code before plan 5 (main at c0248d9, ms43sim run, as flashed, blank EEPROM).
        const string BeforePlan5 = "fd6812640400fca9619d35c4746b71a66a4e3f7a8073721f6514fcc7bdc12d3e";
        var r = DriveRun.Run(TestImages.Car, DriveParser.Parse(
            "drive unchanged\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n3 on\n1.5 crank rpm=200\n10 idle\n"));
        Assert.Equal(BeforePlan5, r.StateHash);
        Assert.Empty(r.KLine);
        Assert.Contains(r.Stubs, s => s.Device == "K-line tester" && s.Behaviour == "none");
    }

    [Fact] public void EqualDriveRunsWithTheTester_GiveIdenticalFiles()
    {
        var again = KLineRuns.Run(KLineRuns.Session);
        Assert.Equal(runs.SessionRun.StateHash, again.StateHash);
        using var a = new TempDirectory();
        using var b = new TempDirectory();
        DriveReportWriter.Write(runs.SessionRun, a.Path);
        DriveReportWriter.Write(again, b.Path);
        Assert.Equal(File.ReadAllText(a.File("kline.csv")), File.ReadAllText(b.File("kline.csv")));
        Assert.Equal(File.ReadAllText(a.File("drive-report.md")), File.ReadAllText(b.File("drive-report.md")));
    }
}
