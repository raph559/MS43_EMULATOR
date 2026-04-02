using System.Text;

namespace Ms43Sim.Core;

/// <summary>
/// Writes boot-report.md and inventory.csv for a boot run. Nothing time- or host-dependent goes
/// in, so equal runs give equal files. Addresses: CPU addresses as SS:OOOO, physical as
/// 0xNNNNNN, file offsets as "file 0xNNNNN".
/// </summary>
public static class BootReportWriter
{
    public static void Write(BootResult r, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "boot-report.md"), Report(r));
        File.WriteAllText(Path.Combine(directory, "inventory.csv"), Inventory(r));
    }

    public static string Cpu(uint address) => $"{address >> 16:X2}:{address & 0xFFFF:X4}";

    /// <summary>The text of boot-report.md.</summary>
    public static string Report(BootResult r)
    {
        var s = new StringBuilder();
        s.AppendLine("# Boot report").AppendLine();

        s.AppendLine("## Image").AppendLine();
        s.AppendLine($"- Source SHA-256: `{r.Image.SourceSha256}`");
        s.AppendLine($"- Image run (SHA-256): `{r.Image.Image.Sha256}`");
        s.AppendLine($"- Mode: {r.Image.Mode} — {r.Image.Description}");
        s.AppendLine("- Checksums of the source file, before any correction:");
        foreach (var c in r.Image.BeforeCorrection)
        {
            var width = c.Kind is ChecksumKind.ProgramSum or ChecksumKind.CalibrationSum ? 8 : 4;
            var state = c.Problem ?? (c.Ok ? "OK" : "MISMATCH");
            s.AppendLine($"  - {c.Kind} at file 0x{c.StoredAt:X5}: stored {c.Stored.ToString("X" + width)}, computed {c.Computed.ToString("X" + width)} — {state}");
        }
        s.AppendLine();

        s.AppendLine("## Outcome").AppendLine();
        s.AppendLine($"**{r.Outcome}** after {r.Cycles:N0} cycles and {r.Instructions:N0} instructions.");
        s.AppendLine();
        s.AppendLine($"- Handover target: {Cpu(r.HandoverTarget)}");
        s.AppendLine($"- Boot checkpoints recognised: {(r.CheckpointsRecognised ? "yes" : "no")}");
        s.AppendLine($"- Watchdog resets: {r.WatchdogResets}; software resets: {r.SoftwareResets}; SRVWDT executed: {r.SrvwdtCount:N0}");
        s.AppendLine($"- Traps: {r.CountOf<TrapEvent>():N0}; events not kept because the log was full: {r.EventsDropped:N0}");
        s.AppendLine();

        s.AppendLine("## Boot passes").AppendLine();
        s.AppendLine("| # | Start cycle | Self-test passed | RAM-test word (R15 at 00:2FA2) | ID check (RL4 at 00:2CE0) | Handover at cycle |");
        s.AppendLine("|---|---|---|---|---|---|");
        for (var i = 0; i < r.Passes.Count; i++)
        {
            var p = r.Passes[i];
            s.AppendLine($"| {i + 1} | {p.StartCycle:N0} | {(p.SelfTestPassed ? "yes" : "no")} | {Hex4(p.RamTestWord)} | {(p.IdCheck is { } id ? id.ToString("X2") + "h" : "—")} | {(p.HandoverCycle is { } h ? h.ToString("N0") : "—")} |");
        }
        s.AppendLine();

        var trapsAndResets = r.Events.Where(e => e is TrapEvent or ResetEvent).ToList();
        s.AppendLine("## Resets and traps").AppendLine();
        if (trapsAndResets.Count == 0)
            s.AppendLine("None.");
        Truncated(s, trapsAndResets.Count, r.CountOf<TrapEvent>() + r.CountOf<ResetEvent>());
        foreach (var e in trapsAndResets)
        {
            s.AppendLine(e switch
            {
                TrapEvent t => $"- cycle {t.Cycle:N0}: **{t.Kind}** trap at {Cpu(t.Pc)}, pushed IP {t.PushedIp:X4}h, TFR {t.Tfr:X4}h",
                ResetEvent x => $"- cycle {x.Cycle:N0}: **{x.Kind} reset** at {Cpu(x.Pc)}",
                _ => "",
            });
        }
        s.AppendLine();

        s.AppendLine("## Erased-flash execution").AppendLine();
        var erased = r.Events.OfType<ErasedFlashExecutionEvent>().ToList();
        if (erased.Count == 0)
            s.AppendLine("None.");
        Truncated(s, erased.Count, r.CountOf<ErasedFlashExecutionEvent>());
        foreach (var e in erased)
            s.AppendLine($"- cycle {e.Cycle:N0}: the CPU fetched FFFFh (erased flash) at {Cpu(e.Pc)}");
        s.AppendLine();

        s.AppendLine("## Flash writes").AppendLine();
        var writeCount = r.CountOf<FlashWriteEvent>();
        var firstWrite = r.Events.OfType<FlashWriteEvent>().FirstOrDefault();
        s.AppendLine(writeCount == 0 ? "None."
            : $"{writeCount:N0} write(s)" + (firstWrite is { } w ? $"; first: {Cpu(w.Pc)} wrote {w.Value:X4}h to 0x{w.Address:X6}." : "."));
        s.AppendLine();

        var other = r.Events.Where(e => e is UnsupportedEvent or ConfigLockedWriteEvent).ToList();
        var otherCount = r.CountOf<UnsupportedEvent>() + r.CountOf<ConfigLockedWriteEvent>();
        if (otherCount > 0)
        {
            s.AppendLine("## Other events").AppendLine();
            Truncated(s, other.Count, otherCount);
            foreach (var e in other)
                s.AppendLine(e switch
                {
                    UnsupportedEvent u => $"- cycle {u.Cycle:N0} at {Cpu(u.Pc)}: unsupported: {u.What}",
                    ConfigLockedWriteEvent c => $"- cycle {c.Cycle:N0} at {Cpu(c.Pc)}: write of {c.Value:X4}h to SYSCON after EINIT ignored",
                    _ => "",
                });
            s.AppendLine();
        }

        s.AppendLine("## Last branches").AppendLine();
        s.AppendLine("Oldest first. A loop that waits on hardware shows up here as the same few transfers repeating.").AppendLine();
        foreach (var b in r.LastBranches)
            s.AppendLine($"- cycle {b.Cycle:N0}: {b.Kind} {Cpu(b.From)} → {Cpu(b.To)}");
        s.AppendLine();

        s.AppendLine("## Unmodelled hardware").AppendLine();
        s.AppendLine("Every access to hardware this run does not model (also in inventory.csv).").AppendLine();
        s.AppendLine("| Area | Address | Name | Kind | Count | First cycle | First PC |");
        s.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var a in r.Inventory)
            s.AppendLine($"| {a.Area} | 0x{a.Address:X6} | {a.Name} | {a.Kind} | {a.Count:N0} | {a.FirstCycle:N0} | {Cpu(a.FirstPc)} |");
        s.AppendLine();

        s.AppendLine("## Assumptions").AppendLine();
        foreach (var a in r.Assumptions)
            s.AppendLine($"- {a}");
        return s.ToString();
    }

    static string Inventory(BootResult r)
    {
        var s = new StringBuilder();
        s.AppendLine("area,address,name,kind,count,first_cycle,first_pc");
        foreach (var a in r.Inventory)
            s.AppendLine($"{a.Area},0x{a.Address:X6},{a.Name},{a.Kind},{a.Count},{a.FirstCycle},{Cpu(a.FirstPc)}");
        return s.ToString();
    }

    static string Hex4(ushort? value) => value is { } v ? v.ToString("X4") + "h" : "—";

    /// <summary>Says so when the event log kept only part of a list (EventLog caps).</summary>
    static void Truncated(StringBuilder s, int listed, long total)
    {
        if (listed < total)
            s.AppendLine($"The log kept the first {listed:N0} of {total:N0}; the rest are counted only.").AppendLine();
    }
}
