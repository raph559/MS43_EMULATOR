using System.Text;

namespace Ms43Sim.Core;

/// <summary>
/// Writes run-report.md, inventory.csv (the boot runner's format) and eeprom.bin (the chip at the
/// end of the run) for a start-up run. Nothing time- or host-dependent goes in, so equal runs give
/// equal files.
/// </summary>
public static class RunReportWriter
{
    const double CyclesPerMs = 24_000;

    public static void Write(StartupResult r, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "run-report.md"), Report(r));
        File.WriteAllText(Path.Combine(directory, "inventory.csv"), Inventory(r));
        File.WriteAllBytes(Path.Combine(directory, "eeprom.bin"), r.Eeprom.FinalImage);
    }

    /// <summary>The text of run-report.md.</summary>
    public static string Report(StartupResult r)
    {
        var s = new StringBuilder();
        s.AppendLine("# Start-up run report").AppendLine();
        Image(s, r);
        Eeprom(s, r);
        Outcome(s, r);
        Milestones(s, r);
        Scheduler(s, r);
        ChecksumTasks(s, r);
        Monitoring(s, r);
        FirmwareStateSection(s, r);
        Can(s, r);
        ResetsAndTraps(s, r);
        Events(s, r);
        StubsSection(s, r);
        Unmodelled(s, r);
        LastBranches(s, r);
        s.AppendLine("## Assumptions").AppendLine();
        foreach (var a in r.Assumptions)
            s.AppendLine($"- {a}");
        return s.ToString();
    }

    static void Image(StringBuilder s, StartupResult r)
    {
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
    }

    static void Eeprom(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## EEPROM").AppendLine();
        s.AppendLine($"- Source: {(r.Eeprom.Source == "blank" ? "blank (every word FFFFh)" : $"image with SHA-256 `{r.Eeprom.Source}`")}");
        s.AppendLine($"- Words read: {r.Eeprom.WordsRead:N0}; words written: {r.Eeprom.WordsWritten:N0}");
        s.AppendLine("- The chip at the end of the run is in eeprom.bin (2,048 bytes, word i little-endian at byte 2i).");
        s.AppendLine();
    }

    static void Outcome(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## Outcome").AppendLine();
        s.AppendLine($"**{r.Outcome}** after {r.Cycles:N0} cycles ({Ms(r.Cycles)} ms), {r.Instructions:N0} instructions and {r.Entries:N0} interrupt, PEC and trap entries.");
        s.AppendLine();
    }

    static void Milestones(StringBuilder s, StartupResult r)
    {
        var m = r.Milestones;
        s.AppendLine("## Milestones").AppendLine();
        s.AppendLine("| Milestone | Where | Cycle | ms |");
        s.AppendLine("|---|---|---|---|");
        Row("the boot's last SRVWDT", "00:2EFC", m.BootServiced);
        Row("handover", "0E:366A", m.Handover);
        Row("interrupts enabled (BSET PSW.11)", "0E:36BC", m.InterruptsEnabled);
        Row("EEPROM block loads returned", "0E:3700", m.EepromLoaded);
        Row("main-loop head", "0E:0094", m.MainLoop);
        Row("key off", "—", m.KeyOff);
        Row("power off (main relay released)", "—", m.PowerOff);
        s.AppendLine();

        void Row(string name, string where, ulong? cycle) =>
            s.AppendLine($"| {name} | {where} | {(cycle is { } c ? c.ToString("N0") : "—")} | {(cycle is { } d ? Ms(d) : "—")} |");
    }

    static void Scheduler(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## Scheduler").AppendLine();
        s.AppendLine($"- Main-loop SRVWDT (0E:0144) executed: {r.Scheduler.MainLoopServices:N0}");
        s.AppendLine($"- Longest gap between two: {r.Scheduler.LongestGapCycles:N0} cycles ({Ms(r.Scheduler.LongestGapCycles)} ms)");
        s.AppendLine($"- Uptime counter E2A6: {r.Scheduler.Uptime:N0} (10 ms steps)");
        s.AppendLine();
    }

    static void ChecksumTasks(StringBuilder s, StartupResult r)
    {
        var f = r.Firmware;
        s.AppendLine("## Checksum tasks").AppendLine();
        s.AppendLine($"- Boot CRC: {Crc(f.BootCrcFinished, f.BootCrcFailed)}");
        s.AppendLine($"- Calibration CRC: {Crc(f.CalibrationCrcFinished, f.CalibrationCrcFailed)}");
        s.AppendLine($"- Program CRC: {Crc(f.ProgramCrcFinished, f.ProgramCrcFailed)}");
        s.AppendLine($"- CRC task (F8F4 = {f.F8F4:X4}h): {(f.CrcChecksPassed ? "done and passed" : "not done, or failed")}");
        s.AppendLine($"- Monitor sums: first pass {(f.MonitorSumFirstPassDone ? "done" : "not done")}; mismatch {(f.MonitorSumMismatch ? "**yes**" : "no")}");
        s.AppendLine();

        static string Crc(bool finished, bool failed) => failed ? "**failed**" : finished ? "passed" : "not finished";
    }

    static void Monitoring(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## Monitoring module").AppendLine();
        s.AppendLine($"- Exchanges: {r.Monitor.Exchanges:N0}; CPU state E027 = {r.Firmware.MonitorState:X2}h");
        s.AppendLine($"- Wrong answers: {r.Monitor.WrongAnswers:N0} (the CPU's deliberate ones included); unexpected answers and frames: {r.Monitor.UnexpectedAnswers:N0}");
        s.AppendLine($"- The module's error counter C: {r.Monitor.ErrorCounter}");
        s.AppendLine();
    }

    static void FirmwareStateSection(StringBuilder s, StartupResult r)
    {
        var f = r.Firmware;
        s.AppendLine("## Firmware state").AppendLine();
        s.AppendLine("Read at the end of the run.").AppendLine();
        s.AppendLine("| RAM | Value | Meaning |");
        s.AppendLine("|---|---|---|");
        s.AppendLine($"| F900 | {f.EngineState:X2}h | engine state: {EngineState(f.EngineState)} |");
        s.AppendLine($"| F8F4 | {f.F8F4:X4}h | CRC task state |");
        s.AppendLine($"| FD26 | {f.Fd26:X4}h | CRCs finished (bits 2-4) and failed (bits 5-7) |");
        s.AppendLine($"| FD7E | {f.Fd7e:X4}h | monitor sums and monitoring flags{(f.MonitorSumMismatch ? "; monitor-sum mismatch" : "")} |");
        s.AppendLine($"| FD0C | {f.Fd0c:X4}h | throttle limp (bit 10): {(f.ThrottleLimp ? "yes" : "no")} |");
        s.AppendLine($"| FD46 | {f.Fd46:X4}h | EEPROM defaults loaded (bit 5): {(f.EepromDefaultsLoaded ? "yes" : "no")} |");
        s.AppendLine($"| E027 | {f.MonitorState:X2}h | monitoring state (00, 12h, CFh, ADh; 3Bh degraded) |");
        s.AppendLine($"| E7E9 | {f.LastError:X2}h | last error code |");
        s.AppendLine($"| E7EA | {f.ErrorCount:X2}h | error count |");
        s.AppendLine($"| E7FC | {f.ResetReason:X4}h | reset reason (0020h SRST after an error, 0010h forced watchdog reset) |");
        s.AppendLine();
    }

    static string EngineState(byte value) => value switch
    {
        0 => "key on, engine stopped",
        1 or 2 or 3 => "running",
        4 => "after-run",
        5 => "init",
        _ => "unknown",
    };

    static void Can(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## CAN transmissions").AppendLine();
        if (r.CanTransmitted.Count == 0)
        {
            s.AppendLine("None.").AppendLine();
            return;
        }
        s.AppendLine("| Identifier | Frames | Mean period (ms) | First cycle | Last data |");
        s.AppendLine("|---|---|---|---|---|");
        foreach (var c in r.CanTransmitted)
        {
            var period = c.Count > 1 ? Ms((c.LastCycle - c.FirstCycle) / (ulong)(c.Count - 1)) : "—";
            s.AppendLine($"| {c.Id:X3}h | {c.Count:N0} | {period} | {c.FirstCycle:N0} | {Convert.ToHexString(c.LastData)} |");
        }
        s.AppendLine();
    }

    static void ResetsAndTraps(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## Resets and traps").AppendLine();
        s.AppendLine($"- Watchdog resets: {r.WatchdogResets}; software resets: {r.SoftwareResets}; traps: {r.CountOf<TrapEvent>():N0}");
        s.AppendLine($"- E7E9 (last error) = {r.Firmware.LastError:X2}h; E7FC (reset reason) = {r.Firmware.ResetReason:X4}h");
        var list = r.Events.Where(e => e is TrapEvent or ResetEvent).ToList();
        Truncated(s, list.Count, r.CountOf<TrapEvent>() + r.CountOf<ResetEvent>());
        foreach (var e in list)
        {
            s.AppendLine(e switch
            {
                TrapEvent t => $"- cycle {t.Cycle:N0}: **{t.Kind}** trap at {Cpu(t.Pc)}, pushed IP {t.PushedIp:X4}h, TFR {t.Tfr:X4}h",
                ResetEvent x => $"- cycle {x.Cycle:N0}: **{x.Kind} reset** at {Cpu(x.Pc)}",
                _ => "",
            });
        }
        s.AppendLine();
    }

    static void Events(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## Unsupported and monitoring events").AppendLine();
        var list = r.Events.Where(e => e is UnsupportedEvent or MonitoringEvent or ConfigLockedWriteEvent).ToList();
        var total = r.CountOf<UnsupportedEvent>() + r.CountOf<MonitoringEvent>() + r.CountOf<ConfigLockedWriteEvent>();
        if (total == 0)
            s.AppendLine("None.");
        Truncated(s, list.Count, total);
        foreach (var e in list)
        {
            s.AppendLine(e switch
            {
                UnsupportedEvent u => $"- cycle {u.Cycle:N0} at {Cpu(u.Pc)}: unsupported: {u.What}",
                MonitoringEvent m => $"- cycle {m.Cycle:N0} at {Cpu(m.Pc)}: monitoring module: {m.What}",
                ConfigLockedWriteEvent c => $"- cycle {c.Cycle:N0} at {Cpu(c.Pc)}: write of {c.Value:X4}h to SYSCON after EINIT ignored",
                _ => "",
            });
        }
        var writes = r.CountOf<FlashWriteEvent>();
        var first = r.Events.OfType<FlashWriteEvent>().FirstOrDefault();
        s.AppendLine();
        s.AppendLine("Flash writes: " + (writes == 0 ? "none."
            : $"{writes:N0}" + (first is { } w ? $"; first: {Cpu(w.Pc)} wrote {w.Value:X4}h to 0x{w.Address:X6}." : ".")));
        var erased = r.CountOf<ErasedFlashExecutionEvent>();
        if (erased > 0)
            s.AppendLine($"Erased-flash executions (FFFFh fetched): {erased:N0}.");
        s.AppendLine();
    }

    static void StubsSection(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## Stubs").AppendLine();
        s.AppendLine("| Device | Behaviour | Between the CPU and a checked output |");
        s.AppendLine("|---|---|---|");
        foreach (var stub in r.Stubs)
            s.AppendLine($"| {stub.Device} | {stub.Behaviour} | {(stub.BetweenCpuAndCheckedOutput ? "yes" : "no")} |");
        s.AppendLine();
    }

    static void Unmodelled(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## Unmodelled hardware").AppendLine();
        s.AppendLine("Every access to hardware this run does not model (also in inventory.csv).").AppendLine();
        s.AppendLine("| Area | Address | Name | Kind | Count | First cycle | First PC |");
        s.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var a in r.Inventory)
            s.AppendLine($"| {a.Area} | 0x{a.Address:X6} | {a.Name} | {a.Kind} | {a.Count:N0} | {a.FirstCycle:N0} | {Cpu(a.FirstPc)} |");
        s.AppendLine();
    }

    static void LastBranches(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## Last branches").AppendLine();
        s.AppendLine("Oldest first. A loop that waits on hardware shows up here as the same few transfers repeating.").AppendLine();
        foreach (var b in r.LastBranches)
            s.AppendLine($"- cycle {b.Cycle:N0}: {b.Kind} {Cpu(b.From)} → {Cpu(b.To)}");
        s.AppendLine();
    }

    static string Inventory(StartupResult r)
    {
        var s = new StringBuilder();
        s.AppendLine("area,address,name,kind,count,first_cycle,first_pc");
        foreach (var a in r.Inventory)
            s.AppendLine($"{a.Area},0x{a.Address:X6},{a.Name},{a.Kind},{a.Count},{a.FirstCycle},{Cpu(a.FirstPc)}");
        return s.ToString();
    }

    static string Cpu(uint address) => BootReportWriter.Cpu(address);

    static string Ms(ulong cycles) => (cycles / CyclesPerMs).ToString("N1");

    static void Truncated(StringBuilder s, int listed, long total)
    {
        if (listed < total)
            s.AppendLine($"The log kept the first {listed:N0} of {total:N0}; the rest are counted only.").AppendLine();
    }
}
