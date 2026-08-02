using System.Globalization;

namespace Ms43Sim.Core;

/// <summary>Where the reference run stood at a moment: its time, speed, air mass and coolant, the road speed and the drive's segment.</summary>
public sealed record OperatingPoint(double Seconds, double Rpm, double MafKgh, double CoolantC, double SpeedKmh, string Segment);

/// <summary>
/// One range over which two runs part on one signal (and cylinder): its first and last cycle in the drive's time, how many
/// events or samples differ in it, the largest difference, the two runs' values there and the reference's operating point
/// there. For the end-of-drive signals (faults, power_cycles, eeprom) the count is of differing entries and the values are
/// counts (faults, power_cycles) or the first differing byte (eeprom).
/// </summary>
public sealed record BehaviourDifference(string Signal, int? Cylinder, ulong FromCycle, ulong ToCycle, int Count,
    double MaxDifference, double Reference, double Candidate, OperatingPoint At);

/// <summary>
/// The compare's behaviour diff (plan 5 Task 7, C5). Sparks and injections pair per cylinder: each reference event with the
/// candidate's nearest of that cylinder within half an engine cycle at the reference's speed (taken from its trace, at least
/// 100 rpm); the rest are spark.missing or injection.missing (1 where a run has the event, 0 where it has not). Paired, they
/// compare by <see cref="CompareTolerances"/>. Trace columns compare at the same 100 ms samples and PWM shares at those
/// times (the last value set by then); either counts in runs of at least <see cref="CompareTolerances.Samples"/> samples.
/// The fault memory, how each power cycle ended and the end EEPROM compare whole, at the end of the reference's trace.
/// Differences of one signal and cylinder less than 0.5 s apart make one range.
/// </summary>
public static class BehaviourDiff
{
    const double CpuHz = 24e6;
    const ulong MergeGap = 12_000_000;                                  // 0.5 s
    const double SlowestRpm = 100;                                      // for the pairing window at a near standstill

    static readonly (string Source, string Signal)[] PwmSignals = { ("PWM0", "pwm.vanos_in"), ("PWM1", "pwm.vanos_ex"), ("PWM2", "pwm.throttle") };

    /// <summary>A single differing event or sample.</summary>
    sealed record Seen(string Signal, int? Cylinder, ulong Cycle, double Difference, double Reference, double Candidate);

    public static IReadOnlyList<BehaviourDifference> Of(RunTrack reference, RunTrack candidate, Drive drive, CompareTolerances tolerances)
    {
        var seen = new List<Seen>();
        var columns = VariableTrace.Columns.ToList();
        var rpmColumn = columns.IndexOf("rpm_fed") is >= 0 and var fed ? fed : columns.IndexOf("rpm_ecu");
        ulong Window(ulong cycle) => (ulong)(60 / Math.Max(SlowestRpm, Value(reference.Trace, rpmColumn, cycle)) * CpuHz);

        foreach (var (r, c) in Pair(reference.Sparks, candidate.Sparks, s => s.Cylinder, s => s.Fire, Window))
        {
            if (r is null || c is null)
            {
                seen.Add(Missing("spark.missing", r?.Cylinder ?? c!.Cylinder, r?.Fire ?? c!.Fire, r is not null));
                continue;
            }
            Compare(seen, "spark.decided_advance", r.Cylinder, r.Fire, r.DecidedAdvance, c.DecidedAdvance, 0);
            Compare(seen, "spark.decided_dwell", r.Cylinder, r.Fire, r.DecidedDwellTicks, c.DecidedDwellTicks, 0);
            Compare(seen, "spark.advance", r.Cylinder, r.Fire, r.Advance, c.Advance, tolerances.SparkAdvanceDeg);
            Compare(seen, "spark.dwell", r.Cylinder, r.Fire, r.DwellMs, c.DwellMs, tolerances.SparkDwellShare * r.DwellMs);
        }
        foreach (var (r, c) in Pair(reference.Injections, candidate.Injections, i => i.Cylinder, i => i.Open, Window))
        {
            if (r is null || c is null)
            {
                seen.Add(Missing("injection.missing", r?.Cylinder ?? c!.Cylinder, r?.Open ?? c!.Open, r is not null));
                continue;
            }
            Compare(seen, "injection.ticks", r.Cylinder, r.Open, r.ExpectedTicks, c.ExpectedTicks, 0);
            Compare(seen, "injection.duration", r.Cylinder, r.Open, r.Ticks, c.Ticks, tolerances.InjectionTicks);
            Compare(seen, "injection.angle", r.Cylinder, r.Open, r.OpenAngle, c.OpenAngle, tolerances.InjectionAngleDeg);
        }

        var samples = Math.Min(reference.Trace.Count, candidate.Trace.Count);
        for (var k = 0; k < columns.Count; k++)
        {
            if (!tolerances.TraceColumns.TryGetValue(columns[k], out var tolerance))
                continue;
            var column = k;
            Sampled(seen, columns[k], samples, tolerances.Samples, i => reference.Trace[i].Values[column], i => candidate.Trace[i].Values[column],
                i => Cycle(reference.Trace[i]), tolerance);
        }
        foreach (var (source, signal) in PwmSignals)
        {
            var r = Shares(reference.Outputs, source);
            var c = Shares(candidate.Outputs, source);
            Sampled(seen, signal, samples, tolerances.Samples, i => ShareAt(r, Cycle(reference.Trace[i])), i => ShareAt(c, Cycle(candidate.Trace[i])),
                i => Cycle(reference.Trace[i]), tolerances.PwmPerMille);
        }

        var end = reference.Trace.Count > 0 ? Cycle(reference.Trace[^1]) : 0;
        var referenceFaults = reference.Faults.Select(Key).ToList();
        var candidateFaults = candidate.Faults.Select(Key).ToList();
        if (!referenceFaults.SequenceEqual(candidateFaults))
        {
            var apart = referenceFaults.Except(candidateFaults).Count() + candidateFaults.Except(referenceFaults).Count();
            seen.Add(new Seen("faults", null, end, Math.Max(apart, 1), referenceFaults.Count, candidateFaults.Count));
        }
        if (!reference.Ends.SequenceEqual(candidate.Ends))
        {
            var apart = reference.Ends.Zip(candidate.Ends).Count(p => p.First != p.Second) + Math.Abs(reference.Ends.Count - candidate.Ends.Count);
            seen.Add(new Seen("power_cycles", null, end, apart, reference.Ends.Count, candidate.Ends.Count));
        }
        if (!reference.Eeprom.AsSpan().SequenceEqual(candidate.Eeprom))
        {
            var length = Math.Max(reference.Eeprom.Length, candidate.Eeprom.Length);
            int At(byte[] image, int i) => i < image.Length ? image[i] : -1;
            var apart = Enumerable.Range(0, length).Where(i => At(reference.Eeprom, i) != At(candidate.Eeprom, i)).ToList();
            seen.Add(new Seen("eeprom", null, end, apart.Count, At(reference.Eeprom, apart[0]), At(candidate.Eeprom, apart[0])));
        }

        return Ranges(seen, reference, drive);
    }

    /// <summary>
    /// Pairs each cylinder's events in time order: each reference event with the nearest unused candidate event within its
    /// window; the candidate events it passes over, and those left at the end, are unpaired.
    /// </summary>
    static IEnumerable<(T? Reference, T? Candidate)> Pair<T>(IReadOnlyList<T> reference, IReadOnlyList<T> candidate,
        Func<T, int> cylinder, Func<T, ulong> at, Func<ulong, ulong> window) where T : class
    {
        foreach (var number in reference.Select(cylinder).Concat(candidate.Select(cylinder)).Distinct().Order())
        {
            var references = reference.Where(e => cylinder(e) == number).OrderBy(at).ToList();
            var candidates = candidate.Where(e => cylinder(e) == number).OrderBy(at).ToList();
            var next = 0;
            foreach (var r in references)
            {
                var t = at(r);
                var w = window(t);
                while (next < candidates.Count && at(candidates[next]) + w < t)
                    yield return (null, candidates[next++]);
                var best = -1;
                var nearest = ulong.MaxValue;
                for (var k = next; k < candidates.Count && at(candidates[k]) <= t + w; k++)
                {
                    var distance = at(candidates[k]) > t ? at(candidates[k]) - t : t - at(candidates[k]);
                    if (distance < nearest)
                        (best, nearest) = (k, distance);
                }
                if (best < 0)
                {
                    yield return (r, null);
                    continue;
                }
                while (next < best)
                    yield return (null, candidates[next++]);
                yield return (r, candidates[best]);
                next = best + 1;
            }
            while (next < candidates.Count)
                yield return (null, candidates[next++]);
        }
    }

    static Seen Missing(string signal, int cylinder, ulong cycle, bool inReference) =>
        new(signal, cylinder, cycle, 1, inReference ? 1 : 0, inReference ? 0 : 1);

    static void Compare(List<Seen> seen, string signal, int cylinder, ulong cycle, double reference, double candidate, double tolerance)
    {
        var difference = Math.Abs(candidate - reference);
        if (difference > tolerance)
            seen.Add(new Seen(signal, cylinder, cycle, difference, reference, candidate));
    }

    /// <summary>A sampled signal: the samples that differ beyond the tolerance, kept only in runs of at least <paramref name="run"/>.</summary>
    static void Sampled(List<Seen> seen, string signal, int samples, int run, Func<int, double?> reference, Func<int, double?> candidate,
        Func<int, ulong> cycle, double tolerance)
    {
        var differing = new List<Seen>();
        void Flush()
        {
            if (differing.Count >= run)
                seen.AddRange(differing);
            differing.Clear();
        }
        for (var i = 0; i < samples; i++)
        {
            var (r, c) = (reference(i), candidate(i));
            var apart = r is { } a && c is { } b ? Math.Abs(b - a) > tolerance : r.HasValue != c.HasValue;
            if (!apart)
            {
                Flush();
                continue;
            }
            differing.Add(new Seen(signal, null, cycle(i), r is { } x && c is { } y ? Math.Abs(y - x) : double.NaN,
                r ?? double.NaN, c ?? double.NaN));
        }
        Flush();
    }

    static List<OutputEvent> Shares(IReadOnlyList<OutputEvent> outputs, string source) =>
        outputs.Where(e => e.Kind == OutputKind.Pwm && e.Source == source).OrderBy(e => e.Cycle).ToList();

    /// <summary>The share set last at or before <paramref name="cycle"/>; null before the first.</summary>
    static double? ShareAt(List<OutputEvent> shares, ulong cycle)
    {
        double? value = null;
        foreach (var e in shares)
        {
            if (e.Cycle > cycle)
                break;
            value = e.Value;
        }
        return value;
    }

    static string Key(FaultEntry f) => string.Create(CultureInfo.InvariantCulture,
        $"{f.Path}/{f.Ds2Code:X2}/{f.Status:X2}/{f.Type}/{f.Stored}/{f.Present}/{f.Sporadic}/{f.ObdConfirmed}/{f.MilRequest}/{f.EmlRequest}/{f.Frequency}/{f.Heal}/{Convert.ToHexString(f.Environment)}");

    static ulong Cycle(TraceRow row) => (ulong)Math.Round(row.Seconds * CpuHz);

    /// <summary>The trace's value in <paramref name="column"/> at the sample nearest <paramref name="cycle"/>; 0 without one.</summary>
    static double Value(IReadOnlyList<TraceRow> trace, int column, ulong cycle) =>
        column >= 0 && Nearest(trace, cycle) is { } row ? row.Values[column] ?? 0 : 0;

    static TraceRow? Nearest(IReadOnlyList<TraceRow> trace, ulong cycle)
    {
        if (trace.Count == 0)
            return null;
        var seconds = cycle / CpuHz;
        int low = 0, high = trace.Count - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (trace[middle].Seconds < seconds)
                low = middle + 1;
            else
                high = middle;
        }
        return low > 0 && seconds - trace[low - 1].Seconds <= trace[low].Seconds - seconds ? trace[low - 1] : trace[low];
    }

    /// <summary>Merges each signal's and cylinder's differences less than 0.5 s apart into ranges, in the order they begin.</summary>
    static IReadOnlyList<BehaviourDifference> Ranges(List<Seen> seen, RunTrack reference, Drive drive)
    {
        var columns = VariableTrace.Columns.ToList();
        var rpm = columns.IndexOf("rpm_fed") is >= 0 and var fed ? fed : columns.IndexOf("rpm_ecu");
        var (maf, coolant) = (columns.IndexOf("maf_kgh"), columns.IndexOf("coolant_c"));
        OperatingPoint Point(ulong cycle)
        {
            var seconds = cycle / CpuHz;
            var at = DriveTimeline.At(drive, seconds);
            var segment = $"line {at.Segment.Line}: {at.Mode.ToString().ToLowerInvariant()}{(at.Ended ? ", past the drive's end" : "")}";
            return new OperatingPoint(seconds, Value(reference.Trace, rpm, cycle), Value(reference.Trace, maf, cycle),
                Value(reference.Trace, coolant, cycle), at.Values.GetValueOrDefault("speed"), segment);
        }

        var ranges = new List<BehaviourDifference>();
        foreach (var group in seen.GroupBy(s => (s.Signal, s.Cylinder)))
        {
            var run = new List<Seen>();
            void Close()
            {
                if (run.Count == 0)
                    return;
                var largest = run.MaxBy(s => double.IsNaN(s.Difference) ? double.MaxValue : s.Difference)!;
                ranges.Add(new BehaviourDifference(group.Key.Signal, group.Key.Cylinder, run[0].Cycle, run[^1].Cycle, run.Count,
                    largest.Difference, largest.Reference, largest.Candidate, Point(largest.Cycle)));
                run.Clear();
            }
            foreach (var s in group.OrderBy(s => s.Cycle))
            {
                if (run.Count > 0 && s.Cycle - run[^1].Cycle >= MergeGap)
                    Close();
                run.Add(s);
            }
            Close();
        }
        return ranges.OrderBy(r => r.FromCycle).ThenBy(r => r.Signal, StringComparer.Ordinal).ThenBy(r => r.Cylinder).ToList();
    }
}
