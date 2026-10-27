using System.Globalization;
using System.Text;

namespace Ms43Sim.Core;

/// <summary>
/// Writes a drive's results (plan 4 Task 10): drive-report.md (the verdict and its reasons, the drive,
/// the power cycles, the checks and their findings, and design §7's coverage: the stubs with their side
/// and reason, what was not evaluated, what was not exercised, what is not modelled, the unexplained
/// faults and the benign notes; plan 5 adds the K-line), events.csv (every kept output event, in the drive's
/// time), trace.csv (the variables every 100 ms), eeprom.bin (the chip at the end), verdict.txt (one word) and
/// kline.csv (the tester's requests, what came of them and the bytes the line lost).
/// </summary>
public static class DriveReportWriter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Write(DriveResult r, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "drive-report.md"), Report(r));
        File.WriteAllText(Path.Combine(directory, "events.csv"), RunReportWriter.EventsCsv(r.OutputEvents));
        File.WriteAllText(Path.Combine(directory, "trace.csv"), RunReportWriter.TraceCsv(r.Trace));
        File.WriteAllBytes(Path.Combine(directory, "eeprom.bin"), r.Eeprom.FinalImage);
        File.WriteAllText(Path.Combine(directory, "verdict.txt"), Word(r.Verdict) + "\n");
        File.WriteAllText(Path.Combine(directory, "assessment.json"), AssessmentWriter.Json(r.SoftwareOutputVerdict,
            new[] { r.Image.Image.Sha256 }, r.Coverage?.NotJudged));
        File.WriteAllText(Path.Combine(directory, "kline.csv"), KLineCsv(r));
    }

    /// <summary>
    /// The K-line in the drive's time (plan 5 Task 4), sorted by cycle: each request (tester); its reply, malformed
    /// answer or timeout (ecu), the timeout when the tester gave up, with the bytes of an unfinished frame (plan 6, M3);
    /// and each byte the line lost (mismatch or collision) or the tester took for no reply (stray), with the end that
    /// sent it. The bytes are spaced hex.
    /// </summary>
    public static string KLineCsv(DriveResult r)
    {
        var rows = new List<(ulong Cycle, string Row)>();
        void Add(ulong cycle, string direction, string outcome, IEnumerable<byte> bytes) =>
            rows.Add((cycle, $"{cycle},{(cycle / 24e3).ToString("0.000", Inv)},{direction},{outcome},{Hex(bytes)}"));
        foreach (var e in r.KLine)
        {
            Add(e.SentAt, "tester", "request", e.Request);
            Add(e.EndedAt, "ecu", Outcome(e.Outcome), e.Reply ?? e.Partial);
        }
        foreach (var n in r.KLineNotes)
            Add(n.Cycle, n.From, n.Kind, new[] { n.Value });
        var s = new StringBuilder("cycle,time_ms,direction,outcome,bytes\n");
        foreach (var row in rows.OrderBy(x => x.Cycle))
            s.Append(row.Row).Append('\n');
        return s.ToString();
    }

    static string Outcome(KLineOutcome outcome) => outcome switch
    {
        KLineOutcome.Replied => "reply",
        KLineOutcome.Malformed => "malformed",
        _ => "timeout",
    };

    static string Hex(IEnumerable<byte> bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2", Inv)));

    /// <summary>
    /// The K-line section: the tester (its stub entry), what it asked and got, the bytes the line lost, and the first ten
    /// problems in time order.
    /// </summary>
    static void KLine(StringBuilder s, DriveResult r)
    {
        s.AppendLine("## K-line").AppendLine();
        var tester = r.Stubs.FirstOrDefault(stub => stub.Device == "K-line tester")?.Behaviour ?? "none";
        if (tester == "none")
        {
            s.AppendLine("K-line tester: none (the drive has no kline line).").AppendLine();
            return;
        }
        int Count(KLineOutcome o) => r.KLine.Count(e => e.Outcome == o);
        s.AppendLine($"K-line tester: {tester}. Its requests and the bytes the line lost, in the drive's time, are all in kline.csv.").AppendLine();
        s.AppendLine($"- requests: {r.KLine.Count}; replies: {Count(KLineOutcome.Replied)}; timeouts: {Count(KLineOutcome.TimedOut)}; malformed: {Count(KLineOutcome.Malformed)}");
        s.AppendLine($"- bytes lost: {r.KLineNotes.Count(n => n.Kind == "mismatch")} to a baud rate more than 3 % from the other end's, {r.KLineNotes.Count(n => n.Kind == "collision")} in collisions");
        s.AppendLine($"- stray ECU bytes, no part of any reply: {r.KLineNotes.Count(n => n.Kind == "stray")}; timeouts with part of a frame: {r.KLine.Count(e => e.Partial.Count > 0)}");
        var problems = r.KLine.Where(e => e.Outcome != KLineOutcome.Replied).Select(e => (Cycle: e.EndedAt, Text: Problem(e)))
            .Concat(r.KLineNotes.Select(n => (n.Cycle, Text: Problem(n))))
            .OrderBy(p => p.Cycle).ToList();
        s.AppendLine();
        if (problems.Count == 0)
        {
            s.AppendLine("No problems: every request was answered, the line lost no byte, and the ECU sent nothing unasked.").AppendLine();
            return;
        }
        s.AppendLine("First problems:").AppendLine();
        foreach (var p in problems.Take(10))
            s.AppendLine($"- {Seconds(p.Cycle)} s: {p.Text}");
        if (problems.Count > 10)
            s.AppendLine($"- and {(problems.Count - 10).ToString("N0", Inv)} more in kline.csv");
        s.AppendLine();
    }

    static string Problem(KLineExchange e) => e.Outcome == KLineOutcome.Malformed
        ? $"a malformed reply to {Hex(e.Request)}, sent at {Seconds(e.SentAt)} s at {e.Baud.ToString("N0", Inv)} baud: {Hex(e.Reply!)}"
        : $"no reply to {Hex(e.Request)}, sent at {Seconds(e.SentAt)} s at {e.Baud.ToString("N0", Inv)} baud"
          + (e.Partial.Count > 0 ? $"; part of a frame came: {Hex(e.Partial)}" : "");

    static string Problem(KLineNote n) => n.Kind == "stray"
        ? $"the ECU's byte {n.Value:X2} came while no reply was awaited"
        : (n.From == "ecu" ? "the ECU's" : "the tester's") + $" byte {n.Value:X2} was lost: " +
          (n.Kind == "collision" ? "it collided with another" : "its baud rate is more than 3 % from the other end's");

    /// <summary>pass, fail or incomplete.</summary>
    public static string Word(RunVerdict verdict) => verdict.ToString().ToLowerInvariant();

    public static string Report(DriveResult r)
    {
        var s = new StringBuilder();
        var coverage = r.Coverage;
        s.AppendLine($"# Drive {r.Drive.Name}: software/output checks {Word(r.Verdict)}").AppendLine();
        s.AppendLine($"Image: {r.Image.Mode}, source SHA-256 {r.Image.SourceSha256}; program identity {ProgramIdentity.Of(r.Image.Image)}" +
                     (ProgramIdentity.Name(r.Image.Image) is { } name ? $" ({name})." : " (not a known program: the checks that read its RAM are not evaluated)."));
        s.AppendLine($"EEPROM at the first key-on: {r.Eeprom.Source} (the drive expects {r.Drive.Start.ToString().ToLowerInvariant()}).").AppendLine();

        s.AppendLine("## Software/output-check verdict").AppendLine();
        s.AppendLine($"**{Word(r.Verdict)}** (exit code {(int)r.Verdict}); scope: {AssessmentWriter.Scope}. Fail beats incomplete beats pass.").AppendLine();
        s.AppendLine("`verdict.txt` and the process exit code carry this software verdict only. `assessment.json` reports both this verdict and the physical calibration safety status.").AppendLine();
        if (coverage is null)
            s.AppendLine("- no checks ran: a drive run without checks is incomplete");
        else
        {
            var reasons = Verdict.Reasons(r.Checks, coverage).ToList();
            if (reasons.Count == 0)
                s.AppendLine("- every check passed, nothing unmodelled was touched and every fault is explained");
            foreach (var reason in reasons)
                s.AppendLine($"- {reason}");
        }
        s.AppendLine();

        AssessmentWriter.AppendPhysicalSafety(s);

        s.AppendLine("## Drive").AppendLine();
        s.AppendLine("| Line | From (s) | Seconds | Mode | Keys | Forces | Silenced |");
        s.AppendLine("|---|---|---|---|---|---|---|");
        var at = 0.0;
        foreach (var segment in r.Drive.Segments)
        {
            var keys = string.Join(" ", segment.Keys.Select(k => k.Value.From == k.Value.To ? $"{k.Key}={N(k.Value.From)}" : $"{k.Key}={N(k.Value.From)}..{N(k.Value.To)}"));
            var forces = string.Join(" ", segment.Forces.Select(f => $"{f.Key}:{N(f.Value)}"));
            var silenced = string.Join(" ", segment.Silenced.Select(id => $"{id:X3}h"));
            s.AppendLine($"| {segment.Line} | {N(at)} | {N(segment.Seconds)} | {segment.Mode.ToString().ToLowerInvariant()} | {keys} | {forces} | {silenced} |");
            at += segment.Seconds;
        }
        s.AppendLine();

        s.AppendLine("## Power cycles").AppendLine();
        foreach (var cycle in r.PowerCycles)
        {
            s.AppendLine($"### Power cycle {cycle.Index + 1}: {Seconds(cycle.StartCycle)} s to {Seconds(cycle.EndCycle)} s").AppendLine();
            var m = cycle.Milestones;
            s.AppendLine($"- handover {Since(m.Handover)}, main loop {Since(m.MainLoop)}, key-off {Since(m.KeyOff)}, power-off {Since(m.PowerOff)} (from this cycle's power-on)");
            s.AppendLine($"- ended: {Ended(cycle.End)}");
            if (cycle.KeySwitches.Count > 0)
                s.AppendLine($"- the key: {string.Join(", ", cycle.KeySwitches.Select(k => $"{(k.On ? "on" : "off")} at {Seconds(k.Cycle)} s"))}");
            if (cycle.Engine is { } engine)
                s.AppendLine($"- engine: {engine.Describe()}");
            s.AppendLine($"- EEPROM: {cycle.Eeprom.WordsRead:N0} words read, {cycle.Eeprom.WordsWritten:N0} written; E17A (the throttle check) = {cycle.Firmware.ThrottleCheckState}");
            s.AppendLine($"- faults stored: {(cycle.Faults.Stored.Count == 0 ? "none" : string.Join(", ", cycle.Faults.Stored.Select(f => $"{f.Label} ({f.PCode}, type {f.Type})")))}");
            s.AppendLine($"- faults detected: {(cycle.Faults.Detected.Count == 0 ? "none" : string.Join(", ", cycle.Faults.Detected.Select(f => $"{f.Label} ({f.PCode}, type {f.Type})")))}");
            s.AppendLine();
        }

        s.AppendLine("## Checks").AppendLine();
        if (r.Checks.Count == 0)
            s.AppendLine("No checks ran.").AppendLine();
        foreach (var check in r.Checks)
        {
            var exercised = check is CheckBase { Exercised: false } && check.Status == CheckStatus.Pass ? " (not exercised: its condition never occurred)" : "";
            s.AppendLine($"- **{check.Name}**: {check.Status.ToString().ToLowerInvariant()}{exercised}");
            if (check is CheckBase { NotEvaluatedReason: { } why })
                s.AppendLine($"  - not evaluated: {why}");
            foreach (var f in check.Findings)
                s.AppendLine($"  - {Seconds(f.FirstCycle)}–{Seconds(f.LastCycle)} s{(f.Count > 1 ? $", ×{f.Count}" : "")}: {f.Message}");
        }
        s.AppendLine();

        if (coverage != null)
        {
            s.AppendLine("## Coverage (design §7)").AppendLine();
            s.AppendLine("Stubs, each with its side of the line and why:").AppendLine();
            foreach (var stub in coverage.Stubs)
                s.AppendLine($"- {stub.Device}: {stub.Behaviour} — {(stub.BetweenCpuAndCheckedOutput ? "**between the CPU and a checked output**" : "not between the CPU and a checked output")}: {stub.Reason}");
            List(s, "Not evaluated", coverage.NotEvaluated);
            List(s, "Not exercised (a pass whose condition never occurred)", coverage.NotExercised);
            List(s, "Not modelled", coverage.UnmodelledAccesses);
            List(s, "Unexplained faults", coverage.UnexplainedFaults);
            List(s, "Not run (part of the drive that never ran)", coverage.NotRun);
            List(s, "Not judged (gaps the checks declare; the verdict does not count them)", coverage.NotJudged);
            List(s, "Benign notes", coverage.BenignNotes);
        }

        s.AppendLine("## Outputs").AppendLine();
        s.AppendLine($"{r.OutputEvents.Count:N0} output events kept ({r.OutputEventsDropped:N0} beyond the capture's capacity; the checks saw them all); {r.Sparks.Count:N0} sparks and {r.Injections.Count:N0} injections kept. Every kept event is in events.csv.").AppendLine();
        s.AppendLine($"Watchdog resets: {r.WatchdogResets}; software resets: {r.SoftwareResets}; state hash {r.StateHash}.").AppendLine();

        KLine(s, r);

        s.AppendLine("## Assumptions").AppendLine();
        foreach (var a in r.Assumptions)
            s.AppendLine($"- {a}");
        return s.ToString();
    }

    static void List(StringBuilder s, string title, IReadOnlyList<string> lines)
    {
        s.AppendLine().AppendLine($"{title}:").AppendLine();
        if (lines.Count == 0)
            s.AppendLine("- none");
        foreach (var line in lines)
            s.AppendLine($"- {line}");
    }

    static string Ended(CycleEnd end) => end switch
    {
        CycleEnd.DriveEnded => "the drive's end",
        CycleEnd.AfterRun => "the after-run: the main relay dropped after the key-off",
        CycleEnd.RelayDroppedKeyOn => "**the main relay dropped with the key on** (the ECU switched itself off)",
        CycleEnd.Restarted => "the firmware's restart: the main relay dropped with the key on at the end of its power-down write, then the ECU reset itself (SRST) and booted again",
        _ => "**the main relay dropped while the drive had the key on** (the firmware read it off: AN1 forced)",
    };

    static string N(double value) => value.ToString("0.###", Inv);

    static string Seconds(ulong cycle) => (cycle / 24e6).ToString("0.000", Inv);

    static string Since(ulong? cycle) => cycle is { } c ? $"{Seconds(c)} s" : "—";
}
