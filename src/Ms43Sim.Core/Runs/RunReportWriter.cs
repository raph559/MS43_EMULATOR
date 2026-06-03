using System.Globalization;
using System.Text;

namespace Ms43Sim.Core;

/// <summary>
/// Writes run-report.md, inventory.csv (the boot runner's format), eeprom.bin (the chip at the
/// end of the run), events.csv (every kept output event) and trace.csv (the variable trace) for a
/// start-up run. Nothing time- or host-dependent goes in, so equal runs give equal files.
/// </summary>
public static class RunReportWriter
{
    const double CyclesPerMs = 24_000;
    const double TicksToMs = 128 / CyclesPerMs;             // T1 ticks of 5.333 µs
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Write(StartupResult r, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "run-report.md"), Report(r));
        File.WriteAllText(Path.Combine(directory, "inventory.csv"), Inventory(r));
        File.WriteAllBytes(Path.Combine(directory, "eeprom.bin"), r.Eeprom.FinalImage);
        File.WriteAllText(Path.Combine(directory, "events.csv"), EventsCsv(r));
        File.WriteAllText(Path.Combine(directory, "trace.csv"), TraceCsv(r));
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
        EngineSection(s, r);
        InputsFedAndSeen(s, r);
        Outputs(s, r);
        Plants(s, r);
        FaultMemorySection(s, r);
        CanPartnersSection(s, r);
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
        s.AppendLine($"| E027 | {f.MonitorState:X2}h | monitoring state (00, 12h, CFh, ADh; 3Bh after-run shutdown or degraded) |");
        s.AppendLine($"| E7E9 | {f.LastError:X2}h | last error code |");
        s.AppendLine($"| E7EA | {f.ErrorCount:X2}h | error count |");
        s.AppendLine($"| E7FC | {f.ResetReason:X4}h | reset reason (0020h SRST after an error, 0010h forced watchdog reset) |");
        s.AppendLine($"| 883C | {f.EcuFaults:X4}h | ECU sub-fault mask (path 74, P0604): bit 10 chip A, bit 11 chip B, bits 7-9 a CRC failed |");
        s.AppendLine($"| E17A | {f.ThrottleCheckState:X2}h | the throttle's start check (07h = done) |");
        s.AppendLine($"| FD14 | {f.Fd14:X4}h | the throttle's adaptation (0040h = passed; bit 0: a full adaptation is due) |");
        s.AppendLine($"| FD9C | {f.Fd9c:X4}h | CAN partners learned: bit 0 gearbox, bit 1 cluster, bit 2 ASC/DSC, bit 4 A/C |");
        s.AppendLine($"| 8101 | {f.Ambient} °C | ambient temperature (615h byte 3) |");
        s.AppendLine();
    }

    /// <summary>F900 (0E:184E, plan 3 Reference facts): 1–3 are not all "running".</summary>
    static string EngineState(byte value) => value switch
    {
        0 => "key on, crank armed",
        1 => "12 or more plausible crank periods, not synced",
        2 => "synced: cranking or running",
        3 => "key off while still turning",
        4 => "after-run",
        5 => "init or inhibited",
        _ => "unknown",
    };

    static void EngineSection(StringBuilder s, StartupResult r)
    {
        if (r.Engine is not { } e)
            return;
        s.AppendLine("## Engine").AppendLine();
        s.AppendLine($"Scenario: {e.Scenario}.").AppendLine();
        s.AppendLine("| From (s) | Phase |");
        s.AppendLine("|---|---|");
        foreach (var p in e.Phases)
            s.AppendLine($"| {F(p.Cycle / CyclesPerMs / 1000, "0.000")} | {p.Phase} |");
        s.AppendLine();
        s.AppendLine($"- At the end: {F(e.FinalRpm, "0")} rpm, airflow {F(e.AirflowKgPerHour, "0.00")} kg/h");
        s.AppendLine($"- Cam offsets from rest: intake advanced {F(e.IntakeAdvance, "0.0")} °CRK, exhaust retarded {F(e.ExhaustRetard, "0.0")} °CRK");
        s.AppendLine();
    }

    static void InputsFedAndSeen(StringBuilder s, StartupResult r)
    {
        if (r.Engine is not { } e || EngineScenario.Named(e.Scenario) is not { } scenario)
            return;
        s.AppendLine("## Inputs fed and seen").AppendLine();
        if (LastKeyOnRow(r) is not { } row)
        {
            s.AppendLine("No trace row with the key on.").AppendLine();
            return;
        }
        double? V(string column) => Value(row, column);
        var inputs = scenario.Inputs;
        var battery = PhaseAt(e, row.Seconds) switch
        {
            EnginePhase.Starter => scenario.BatteryStarter,
            EnginePhase.Running => scenario.BatteryRunning,
            _ => scenario.BatteryOff,
        };
        s.AppendLine($"At {F(row.Seconds, "0.0")} s, the last trace row with the key on.").AppendLine();
        s.AppendLine("| Input | Fed | Seen by the ECU |");
        s.AppendLine("|---|---|---|");
        s.AppendLine($"| speed | {F(V("rpm_fed"), "0")} rpm | E0E4: {F(V("rpm_ecu"), "0")} rpm |");
        s.AppendLine($"| coolant | {F(inputs.MuxAVolts[3], "0.00")} V | 8433: {F(V("coolant_c"), "0.0")} °C |");
        s.AppendLine($"| intake air | {F(inputs.MuxAVolts[1], "0.00")} V | 8432: {F(V("intake_c"), "0.0")} °C |");
        s.AppendLine($"| oil | {F(inputs.MuxAVolts[2], "0.00")} V | 842F: {F(V("oil_c"), "0.0")} °C |");
        s.AppendLine($"| pedal | {F(inputs.AnalogVolts[10], "0.000")} V and {F(inputs.AnalogVolts[6], "0.000")} V | E0FE: {F(V("pedal_pct"), "0.0")} % |");
        s.AppendLine($"| battery | {F(battery, "0.0")} V | FBD5: {F(V("battery_v"), "0.00")} V |");
        s.AppendLine($"| intake cam | advanced {F(V("cam_in_fed"), "0.0")} °CRK | 8973 = {F(V("cam_in_raw"), "0")}: advanced {F(CamSeen(160, V("cam_in_raw")), "0.0")} °CRK |");
        s.AppendLine($"| exhaust cam | retarded {F(V("cam_ex_fed"), "0.0")} °CRK | 8AE1 = {F(V("cam_ex_raw"), "0")}: retarded {F(CamSeen(120, V("cam_ex_raw")), "0.0")} °CRK |");
        s.AppendLine();
    }

    /// <summary>A raw cam reading as °CRK from rest: 8973 = 160 − a / 0.375, 8AE1 = 120 − r / 0.375.</summary>
    static double? CamSeen(int rest, double? raw) => raw is { } x ? (rest - x) * 0.375 : null;

    static void Outputs(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## Outputs").AppendLine();
        if (r.OutputCounts.Count == 0)
            s.AppendLine("No output changed.").AppendLine();
        else
        {
            var last = new Dictionary<string, OutputEvent>();
            foreach (var e in r.OutputEvents)
                last[e.Source] = e;
            s.AppendLine("| Source | Changes | Last value kept |");
            s.AppendLine("|---|---|---|");
            foreach (var (source, count) in r.OutputCounts.OrderBy(c => SourceOrder(c.Key)).ThenBy(c => c.Key, StringComparer.Ordinal))
                s.AppendLine($"| {source} | {count:N0} | {(last.TryGetValue(source, out var e) ? Shown(e) : "—")} |");
            s.AppendLine();
        }
        s.AppendLine($"Dropped beyond the capture's {OutputCapture.Capacity:N0} events: {r.OutputEventsDropped:N0}. Every kept event is in events.csv.");
        s.AppendLine();
        if (r.Engine is null)
            return;
        s.AppendLine("Per cylinder, over the whole run (the start included); widths and dwells in ms, advances in °BTDC; the ECU's decisions read as each event began.").AppendLine();
        s.AppendLine("| Cylinder | Injections | Mean width | Mean ECU width | Main sparks | Mean advance | Mean ECU advance | Multi-sparks per main | Mean main dwell |");
        s.AppendLine("|---|---|---|---|---|---|---|---|---|");
        for (var c = 1; c <= 6; c++)
        {
            var injections = r.Injections.Where(i => i.Cylinder == c).ToList();
            var main = r.Sparks.Where(p => p.Cylinder == c && p.Main).ToList();
            var multi = r.Sparks.Count(p => p.Cylinder == c && !p.Main);
            s.AppendLine($"| {c} | {injections.Count:N0} | {Mean(injections.Select(i => i.Ticks * TicksToMs), "0.000")} | " +
                         $"{Mean(injections.Select(i => i.ExpectedTicks * TicksToMs), "0.000")} | {main.Count:N0} | " +
                         $"{Mean(main.Select(p => p.Advance), "0.0")} | {Mean(main.Select(p => p.DecidedAdvance), "0.0")} | " +
                         $"{(main.Count > 0 ? F((double)multi / main.Count, "0.0") : "—")} | {Mean(main.Select(p => p.DwellMs), "0.00")} |");
        }
        s.AppendLine();
    }

    /// <summary>Pins by port and bit (P2.2 before P2.10), then the other sources by name.</summary>
    static (int, int, int) SourceOrder(string source)
    {
        var dot = source.IndexOf('.');
        return source.StartsWith('P') && dot > 1
               && int.TryParse(source.AsSpan(1, dot - 1), NumberStyles.None, Inv, out var port)
               && int.TryParse(source.AsSpan(dot + 1), NumberStyles.None, Inv, out var bit)
            ? (0, port, bit)
            : (1, 0, 0);
    }

    static string Shown(OutputEvent e) => e.Kind switch
    {
        OutputKind.Pwm => $"{e.Value} ‰ high",
        OutputKind.ChipA => $"{e.Value:X4}h",
        OutputKind.Latch => $"{e.Value:X2}h",
        OutputKind.Heaters => $"{e.Value:X1}h",
        _ => e.Value.ToString(Inv),
    };

    static void Plants(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## Plants").AppendLine();
        s.AppendLine($"- Throttle: {F(r.ThrottleAngle, "0.00")}° above the lower stop at the end (limp-home {F(ThrottleBody.LimpHome, "0.00")}°); " +
                     $"start check E17A = {r.Firmware.ThrottleCheckState:X2}h (07h = done); limp FD0C.10 = {(r.Firmware.ThrottleLimp ? 1 : 0)}");
        if (r.Engine is not null && LastKeyOnRow(r) is { } row)
        {
            double? V(string column) => Value(row, column);
            s.AppendLine($"- VANOS at {F(row.Seconds, "0.0")} s: intake 8972 = {F(V("cam_in_deg"), "0.0")} °CRK for the setpoint 8971 = {F(V("cam_in_sp_deg"), "0.0")} °CRK " +
                         $"(the plant advanced {F(V("cam_in_fed"), "0.0")}); exhaust 8AE0 = {F(V("cam_ex_deg"), "0.0")} °CRK for 8ADF = {F(V("cam_ex_sp_deg"), "0.0")} °CRK " +
                         $"(the plant retarded {F(V("cam_ex_fed"), "0.0")})");
            s.AppendLine($"- Idle: the target 8878 = {F(V("idle_target_rpm"), "0")} rpm; the speed {F(V("rpm_fed"), "0")} rpm (E0E4 {F(V("rpm_ecu"), "0")}); the valve 8862 = {F(V("idle_valve_pct"), "0.0")} %");
        }
        s.AppendLine();
    }

    static void FaultMemorySection(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## Fault memory").AppendLine();
        if (!r.Faults.Recognised)
        {
            s.AppendLine("Not recognised: the 162 DS2 codes are not at file 0x6D08E, so nothing is decoded.").AppendLine();
            return;
        }
        Entries("Stored (list A)", r.Faults.Stored, explain: false);
        Entries("Detected (list B)", r.Faults.Detected, explain: true);
        Entries("The EEPROM's after-run record", r.SavedFaults, explain: false);

        void Entries(string title, IReadOnlyList<FaultEntry> list, bool explain)
        {
            s.AppendLine(list.Count == 0 ? $"{title}: none." : $"{title}:").AppendLine();
            if (list.Count == 0)
                return;
            s.AppendLine(explain ? "| Path | Name | DS2 | P-code | Type | Status | Why |" : "| Path | Name | DS2 | P-code | Type | Status |");
            s.AppendLine(explain ? "|---|---|---|---|---|---|---|" : "|---|---|---|---|---|---|");
            foreach (var e in list)
            {
                var row = $"| {e.Path} | {e.Label} | {e.Ds2Code:X2}h | {e.PCode} | {e.Type} | {e.Status:X2}h |";
                s.AppendLine(explain ? $"{row} {FaultMemory.Explanation(e, r.FaultContext) ?? "**UNEXPLAINED**"} |" : row);
            }
            s.AppendLine();
        }
    }

    static void CanPartnersSection(StringBuilder s, StartupResult r)
    {
        s.AppendLine("## CAN partners").AppendLine();
        if (r.CanPartnersSent is not { } sent)
            s.AppendLine("Off: no other node sends.");
        else if (sent.Count == 0)
            s.AppendLine("On, but no frame was delivered (the key was never on).");
        else
            s.AppendLine("Frames delivered: " + string.Join(", ", sent.OrderBy(k => k.Key).Select(k => $"{k.Key:X3}h {k.Value:N0}")) + ".");
        var f = r.Firmware.Fd9c;
        s.AppendLine($"Learned (FD9C = {f:X4}h): gearbox {Yes(f, 0)}, cluster {Yes(f, 1)}, ASC/DSC {Yes(f, 2)}, A/C {Yes(f, 4)}.");
        s.AppendLine();

        static string Yes(ushort word, int bit) => (word & (1 << bit)) != 0 ? "yes" : "no";
    }

    /// <summary>The last trace row before the key-off, or the last row when the key stayed on.</summary>
    static TraceRow? LastKeyOnRow(StartupResult r) =>
        r.Trace.LastOrDefault(t => r.Milestones.KeyOff is not { } off || t.Seconds * 1000 * CyclesPerMs < off);

    static double? Value(TraceRow row, string column)
    {
        var i = IndexOf(column);
        return i < 0 ? null : row.Values[i];
    }

    static int IndexOf(string column)
    {
        var columns = VariableTrace.Columns;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i] == column)
                return i;
        }
        return -1;
    }

    static EnginePhase PhaseAt(EngineSummary e, double seconds) =>
        e.Phases.LastOrDefault(p => p.Cycle <= seconds * 1000 * CyclesPerMs)?.Phase ?? EnginePhase.Stopped;

    static string Mean(IEnumerable<double> values, string format)
    {
        var list = values.ToList();
        return list.Count == 0 ? "—" : F(list.Average(), format);
    }

    static string F(double? value, string format) => value is { } v ? v.ToString(format, Inv) : "—";

    /// <summary>cycle,time_ms,kind,source,value,crank_deg: one row per kept output event; no angle, an empty cell.</summary>
    static string EventsCsv(StartupResult r)
    {
        var s = new StringBuilder();
        s.AppendLine("cycle,time_ms,kind,source,value,crank_deg");
        foreach (var e in r.OutputEvents)
        {
            s.Append(e.Cycle.ToString(Inv)).Append(',')
             .Append((e.Cycle / CyclesPerMs).ToString("0.000", Inv)).Append(',')
             .Append(e.Kind).Append(',')
             .Append(e.Source).Append(',')
             .Append(e.Value.ToString(Inv)).Append(',')
             .Append(e.Angle is { } a ? a.ToString("0.###", Inv) : "")
             .AppendLine();
        }
        return s.ToString();
    }

    /// <summary>time_s and the trace's columns: one row per sample; empty cells for null; bit fields in hex.</summary>
    static string TraceCsv(StartupResult r)
    {
        var s = new StringBuilder();
        s.Append("time_s,").AppendLine(string.Join(",", VariableTrace.Columns));
        foreach (var row in r.Trace)
        {
            s.Append(row.Seconds.ToString("0.###", Inv));
            for (var i = 0; i < row.Values.Length; i++)
            {
                s.Append(',');
                if (row.Values[i] is { } v)
                    s.Append(VariableTrace.HexColumns.Contains(VariableTrace.Columns[i]) ? $"0x{(int)v:X4}" : v.ToString("0.####", Inv));
            }
            s.AppendLine();
        }
        return s.ToString();
    }

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
