using System.Globalization;

namespace Ms43Sim.Core;

/// <summary>
/// How far two runs may part before the compare counts a difference (plan 5 R6). The ECU's decisions are exact: the spark
/// advance and dwell it decided, the injection ticks it decided, the fault memory, how each power cycle ended and the
/// EEPROM at the end. What it drives is measured on the pins, where two bins drift by a few cycles (R5): the advance as
/// fired within <see cref="SparkAdvanceDeg"/>, the dwell as fired within <see cref="SparkDwellShare"/> of the reference's,
/// the injection as driven within <see cref="InjectionTicks"/> CAPCOM ticks of 5.333 µs and its angle within
/// <see cref="InjectionAngleDeg"/>. A trace column (within its entry in <see cref="TraceColumns"/>, 0 for exact) or a PWM
/// share (within <see cref="PwmPerMille"/>) counts only when it differs in <see cref="Samples"/> samples in a row: a value
/// computed a few cycles later can miss one 100 ms sample.
/// </summary>
public sealed record CompareTolerances(double SparkAdvanceDeg, double SparkDwellShare, double InjectionTicks,
    double InjectionAngleDeg, double PwmPerMille, int Samples, IReadOnlyDictionary<string, double> TraceColumns)
{
    /// <summary>
    /// R6's values. The trace columns: the firmware's (setpoints, F900, flags, ti, iga, dwell) exact; the plant's as R6 has
    /// them (rpm 5, temperatures 0.5 °C, angles 0.5°, the throttle 0.2°, shares 1 point, the battery 0.05 V); and, where R6
    /// is silent [call], the air mass 0.5 kg/h (two of its steps) and the raw cam counts 1.
    /// </summary>
    public static CompareTolerances Default { get; } = new(0.1, 0.02, 1, 0.1, 1, 2, Columns());

    static IReadOnlyDictionary<string, double> Columns()
    {
        var columns = new Dictionary<string, double>();
        void Set(double tolerance, params string[] names)
        {
            foreach (var name in names)
                columns[name] = tolerance;
        }
        Set(0, "f900", "fd2c", "fd9c", "fd0c", "e17a", "lambda_cfg", "tps_sp_deg", "cam_in_sp_deg", "cam_ex_sp_deg",
            "idle_target_rpm", "ti1_ms", "ti2_ms", "deadtime_ms", "iga1_deg", "dwell_ms");
        Set(5, "rpm_fed", "rpm_ecu");
        Set(0.5, "coolant_c", "intake_c", "oil_c");
        Set(0.5, "cam_in_deg", "cam_ex_deg", "cam_in_fed", "cam_ex_fed");
        Set(0.2, "tps_deg");
        Set(1, "throttle_drive_pct", "pedal_pct", "vanos_in_pct", "vanos_ex_pct", "idle_valve_pct", "idle_i_pct");
        Set(0.05, "battery_v");
        Set(0.5, "maf_kgh");
        Set(1, "cam_in_raw", "cam_ex_raw");
        return columns;
    }

    /// <summary>The tolerances in words, one line each, for a report.</summary>
    public IEnumerable<string> Describe()
    {
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        yield return "spark.decided_advance, spark.decided_dwell, injection.ticks (the ECU's decisions): exact";
        yield return $"spark.advance (as fired): {N(SparkAdvanceDeg)}°; spark.dwell (as fired): {N(SparkDwellShare * 100)} % of the reference's";
        yield return $"injection.duration (as driven): {N(InjectionTicks)} CAPCOM tick(s); injection.angle: {N(InjectionAngleDeg)}°";
        yield return "sparks and injections pair per cylinder with the other run's nearest, within half an engine cycle at the reference's speed";
        yield return "two bins drift by cycles, so the same behaviour may come a moment earlier or later: a paired event differs only beyond the range of the reference's values at that event and the events either side; unpaired events are spark.missing or injection.missing only two or more in a row";
        foreach (var group in TraceColumns.GroupBy(c => c.Value).OrderBy(g => g.Key))
            yield return $"trace {string.Join(", ", group.Select(c => c.Key))}: {(group.Key == 0 ? "exact" : $"±{N(group.Key)}")}, beyond the reference's range at that sample and the samples either side, for {Samples} samples in a row";
        yield return $"pwm.vanos_in, pwm.vanos_ex, pwm.throttle (shares, averaged over each sample's 100 ms): ±{N(PwmPerMille)} ‰, as the trace columns are judged";
        yield return "faults (stored at the end of each power cycle), power_cycles (how each ended), eeprom (at the end, without its copy of the flash checksums, words 1EEh-1F0h): exact";
        yield return "differences of one signal and cylinder less than 0.5 s apart are one range";
    }
}
