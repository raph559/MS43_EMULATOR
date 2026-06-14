using System.Globalization;
using System.Text;

namespace Ms43Sim.Core;

/// <summary>
/// Writes a drive's results (plan 4 Task 10): drive-report.md (the verdict and its reasons, the drive,
/// the power cycles, the checks and their findings, and design §7's coverage: the stubs with their side
/// and reason, what was not evaluated, what was not exercised, what is not modelled, the unexplained
/// faults and the benign notes), events.csv (every kept output event, in the drive's time), trace.csv
/// (the variables every 100 ms), eeprom.bin (the chip at the end) and verdict.txt (one word).
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
    }

    /// <summary>pass, fail or incomplete.</summary>
    public static string Word(RunVerdict verdict) => verdict.ToString().ToLowerInvariant();

    public static string Report(DriveResult r)
    {
        var s = new StringBuilder();
        var coverage = r.Coverage;
        s.AppendLine($"# Drive {r.Drive.Name}: {Word(r.Verdict)}").AppendLine();
        s.AppendLine($"Image: {r.Image.Mode}, source SHA-256 {r.Image.SourceSha256}; program identity {ProgramIdentity.Of(r.Image.Image)}" +
                     (ProgramIdentity.IsMs43X001(r.Image.Image) ? " (MS43X001)." : " (not MS43X001: the checks that read its RAM are not evaluated)."));
        s.AppendLine($"EEPROM at the first key-on: {r.Eeprom.Source} (the drive expects {r.Drive.Start.ToString().ToLowerInvariant()}).").AppendLine();

        s.AppendLine("## Verdict").AppendLine();
        s.AppendLine($"**{Word(r.Verdict)}** (exit code {(int)r.Verdict}). Fail beats incomplete beats pass (C2).").AppendLine();
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
            List(s, "Benign notes", coverage.BenignNotes);
        }

        s.AppendLine("## Outputs").AppendLine();
        s.AppendLine($"{r.OutputEvents.Count:N0} output events kept ({r.OutputEventsDropped:N0} beyond the capture's capacity; the checks saw them all); {r.Sparks.Count:N0} sparks and {r.Injections.Count:N0} injections kept. Every kept event is in events.csv.").AppendLine();
        s.AppendLine($"Watchdog resets: {r.WatchdogResets}; software resets: {r.SoftwareResets}; state hash {r.StateHash}.").AppendLine();

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

    static string N(double value) => value.ToString("0.###", Inv);

    static string Seconds(ulong cycle) => (cycle / 24e6).ToString("0.000", Inv);

    static string Since(ulong? cycle) => cycle is { } c ? $"{Seconds(c)} s" : "—";
}
