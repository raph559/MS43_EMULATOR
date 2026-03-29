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

    static string Report(BootResult r)
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

        var traps = r.Events.OfType<TrapEvent>().ToList();
        s.AppendLine("## Resets and traps").AppendLine();
        if (traps.Count == 0 && r.WatchdogResets + r.SoftwareResets == 0)
            s.AppendLine("None.");
        foreach (var e in r.Events.Where(e => e is TrapEvent or ResetEvent))
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
        s.AppendLine(erased.Count == 0 ? "None." : string.Join(Environment.NewLine,
            erased.Select(e => $"- cycle {e.Cycle:N0}: the CPU fetched FFFFh (erased flash) at {Cpu(e.Pc)}")));
        s.AppendLine();

        s.AppendLine("## Flash writes").AppendLine();
        var writes = r.Events.OfType<FlashWriteEvent>().ToList();
        s.AppendLine(writes.Count == 0 ? "None." : $"{writes.Count} write(s); first: {Cpu(writes[0].Pc)} wrote {writes[0].Value:X4}h to 0x{writes[0].Address:X6}.");
        s.AppendLine();

        var other = r.Events.Where(e => e is UnsupportedEvent or ConfigLockedWriteEvent).ToList();
        if (other.Count > 0)
        {
            s.AppendLine("## Other events").AppendLine();
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
}
