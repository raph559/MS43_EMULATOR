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
/// The compare's behaviour diff (plan 5 Task 7, C5; the time-shift rules of Task 10). Sparks and injections pair per
/// cylinder: each reference event with the candidate's nearest of that cylinder within half an engine cycle at the
/// reference's speed (taken from its trace, at least 100 rpm). Two bins drift by cycles (R5), so the same behaviour can
/// come a moment earlier or later in one run, and the compare does not count that as a difference:
/// <list type="bullet">
/// <item>a paired event's value differs only when it leaves the range of the reference's values at that cylinder's events
/// within 100 ms of it, and the events either side, by more than <see cref="CompareTolerances"/> allows, and only at two or
/// more of that cylinder's events in a row: a decision on a breakpoint, sampled a few cycles later, can tip one step at one
/// event;</item>
/// <item>events of a cylinder one run has and the other has not are spark.missing or injection.missing (1 where a run has
/// the event, 0 where it has not) only two or more in a row: one alone is a cut that began or ended an event earlier;</item>
/// <item>trace columns compare at the same 100 ms samples and PWM shares as their average over each sample's 100 ms; a
/// sample differs when it leaves the range of the reference's at that sample and the samples either side, and only in
/// runs of at least <see cref="CompareTolerances.Samples"/> samples.</item>
/// </list>
/// The fault memory, how each power cycle ended and the end EEPROM compare whole, at the end of the reference's trace; the
/// EEPROM without its copy of the flash checksums (words 1EEh-1F0h: the program CRC, the calibration CRC and the record's
/// CRC-16/ARC from 000Fh), which any code change moves as it moves the image's own checksum fields (R7). Differences of
/// one signal and cylinder less than 0.5 s apart make one range.
/// </summary>
public static class BehaviourDiff
{
    const double CpuHz = 24e6;
    const ulong MergeGap = 12_000_000;                                  // 0.5 s
    const ulong SampleCycles = 2_400_000;                               // 100 ms
    const double SlowestRpm = 100;                                      // for the pairing window at a near standstill

    /// <summary>The EEPROM's copy of the flash checksums: bytes 0x3DC-0x3E1 (words 1EEh-1F0h).</summary>
    public const int ChecksumCopyStart = 0x3DC, ChecksumCopyEnd = 0x3E2;

    static readonly (string Source, string Signal)[] PwmSignals = { ("PWM0", "pwm.vanos_in"), ("PWM1", "pwm.vanos_ex"), ("PWM2", "pwm.throttle") };

    /// <summary>A single differing event or sample.</summary>
    sealed record Seen(string Signal, int? Cylinder, ulong Cycle, double Difference, double Reference, double Candidate);

    /// <summary>A value an event carries, and how far the candidate's may stray from the reference's.</summary>
    sealed record Signal<T>(string Name, Func<T, double> Value, Func<T, double> Tolerance);

    public static IReadOnlyList<BehaviourDifference> Of(RunTrack reference, RunTrack candidate, Drive drive, CompareTolerances tolerances)
    {
        var seen = new List<Seen>();
        var columns = VariableTrace.Columns.ToList();
        var rpmColumn = columns.IndexOf("rpm_fed") is >= 0 and var fed ? fed : columns.IndexOf("rpm_ecu");
        ulong Window(ulong cycle) => (ulong)(60 / Math.Max(SlowestRpm, Value(reference.Trace, rpmColumn, cycle)) * CpuHz);

        Events(seen, reference.Sparks, candidate.Sparks, s => s.Cylinder, s => s.Fire, Window, "spark.missing", new Signal<Spark>[]
        {
            new("spark.decided_advance", s => s.DecidedAdvance, _ => 0),
            new("spark.decided_dwell", s => s.DecidedDwellTicks, _ => 0),
            new("spark.advance", s => s.Advance, _ => tolerances.SparkAdvanceDeg),
            new("spark.dwell", s => s.DwellMs, s => tolerances.SparkDwellShare * s.DwellMs),
        });
        Events(seen, reference.Injections, candidate.Injections, i => i.Cylinder, i => i.Open, Window, "injection.missing", new Signal<Injection>[]
        {
            new("injection.ticks", i => i.ExpectedTicks, _ => 0),
            new("injection.duration", i => i.Ticks, _ => tolerances.InjectionTicks),
            new("injection.angle", i => i.OpenAngle, _ => tolerances.InjectionAngleDeg),
        });

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
            var r = Averages(Shares(reference.Outputs, source), reference.Trace, samples);
            var c = Averages(Shares(candidate.Outputs, source), reference.Trace, samples);
            Sampled(seen, signal, samples, tolerances.Samples, i => r[i], i => c[i], i => Cycle(reference.Trace[i]), tolerances.PwmPerMille);
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
        int At(byte[] image, int i) => i < image.Length ? image[i] : -1;
        var eeprom = Enumerable.Range(0, Math.Max(reference.Eeprom.Length, candidate.Eeprom.Length))
            .Where(i => i is < ChecksumCopyStart or >= ChecksumCopyEnd && At(reference.Eeprom, i) != At(candidate.Eeprom, i)).ToList();
        if (eeprom.Count > 0)
            seen.Add(new Seen("eeprom", null, end, eeprom.Count, At(reference.Eeprom, eeprom[0]), At(candidate.Eeprom, eeprom[0])));

        return Ranges(seen, reference, drive);
    }

    /// <summary>
    /// One kind of event: per cylinder, the unpaired ones in runs of two or more, and the paired ones whose values leave the
    /// reference's range at that event and the events either side of it.
    /// </summary>
    static void Events<T>(List<Seen> seen, IReadOnlyList<T> reference, IReadOnlyList<T> candidate, Func<T, int> cylinder, Func<T, ulong> at,
        Func<ulong, ulong> window, string missing, IReadOnlyList<Signal<T>> signals) where T : class
    {
        foreach (var number in reference.Select(cylinder).Concat(candidate.Select(cylinder)).Distinct().Order())
        {
            var references = reference.Where(e => cylinder(e) == number).OrderBy(at).ToList();
            var candidates = candidate.Where(e => cylinder(e) == number).OrderBy(at).ToList();
            var pairs = Pair(references, candidates, at, window).ToList();
            foreach (var run in InRuns(pairs.Where(p => p.Candidate is null).Select(p => p.Reference!.Value)))
                seen.AddRange(run.Select(i => new Seen(missing, number, at(references[i]), 1, 1, 0)));
            foreach (var run in InRuns(pairs.Where(p => p.Reference is null).Select(p => p.Candidate!.Value)))
                seen.AddRange(run.Select(i => new Seen(missing, number, at(candidates[i]), 1, 0, 1)));
            foreach (var signal in signals)
            {
                var differing = new Dictionary<int, Seen>();             // by the reference event's index
                foreach (var (r, c) in pairs)
                {
                    if (r is not { } ri || c is not { } ci)
                        continue;
                    var (value, mine) = (signal.Value(candidates[ci]), signal.Value(references[ri]));
                    var neighbours = Near(references, ri, at).Select(j => signal.Value(references[j])).ToList();
                    var tolerance = signal.Tolerance(references[ri]);
                    if (value < neighbours.Min() - tolerance || value > neighbours.Max() + tolerance)
                        differing[ri] = new Seen(signal.Name, number, at(references[ri]), Math.Abs(value - mine), mine, value);
                }
                foreach (var run in InRuns(differing.Keys.Order()))
                    seen.AddRange(run.Select(i => differing[i]));
            }
        }
    }

    /// <summary>
    /// Pairs one cylinder's events in time order: each reference event with the nearest unused candidate event within its
    /// window; the candidate events it passes over, and those left at the end, are unpaired. Indices into the two lists.
    /// </summary>
    static IEnumerable<(int? Reference, int? Candidate)> Pair<T>(List<T> references, List<T> candidates, Func<T, ulong> at, Func<ulong, ulong> window)
    {
        var next = 0;
        for (var ri = 0; ri < references.Count; ri++)
        {
            var t = at(references[ri]);
            var w = window(t);
            while (next < candidates.Count && at(candidates[next]) + w < t)
                yield return (null, next++);
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
                yield return (ri, null);
                continue;
            }
            while (next < best)
                yield return (null, next++);
            yield return (ri, best);
            next = best + 1;
        }
        while (next < candidates.Count)
            yield return (null, next++);
    }

    /// <summary>The indices of the events either side of <paramref name="i"/> and of every event within 100 ms of it, itself included.</summary>
    static IEnumerable<int> Near<T>(List<T> events, int i, Func<T, ulong> at)
    {
        var t = at(events[i]);
        var (low, high) = (Math.Max(0, i - 1), Math.Min(events.Count - 1, i + 1));
        while (low > 0 && t - at(events[low - 1]) <= SampleCycles)
            low--;
        while (high < events.Count - 1 && at(events[high + 1]) - t <= SampleCycles)
            high++;
        return Enumerable.Range(low, high - low + 1);
    }

    /// <summary>The runs of two or more consecutive indices among <paramref name="indices"/> (in order).</summary>
    static IEnumerable<List<int>> InRuns(IEnumerable<int> indices)
    {
        var run = new List<int>();
        foreach (var i in indices)
        {
            if (run.Count > 0 && i != run[^1] + 1)
            {
                if (run.Count >= 2)
                    yield return run;
                run = new List<int>();
            }
            run.Add(i);
        }
        if (run.Count >= 2)
            yield return run;
    }

    /// <summary>
    /// A sampled signal: the samples whose candidate value leaves the reference's range at that sample and the samples either
    /// side by more than the tolerance (or has a value where the reference has none, or none where it has one), kept only in
    /// runs of at least <paramref name="run"/>.
    /// </summary>
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
            bool apart;
            if (r is { } a && c is { } b)
            {
                var neighbours = Enumerable.Range(i - 1, 3).Where(j => j >= 0 && j < samples).Select(reference).OfType<double>().ToList();
                apart = b < neighbours.Min() - tolerance || b > neighbours.Max() + tolerance;
            }
            else
                apart = r.HasValue != c.HasValue;
            if (!apart)
            {
                Flush();
                continue;
            }
            differing.Add(new Seen(signal, null, cycle(i), r is { } x && c is { } y ? Math.Abs(y - x) : double.NaN, r ?? double.NaN, c ?? double.NaN));
        }
        Flush();
    }

    static List<OutputEvent> Shares(IReadOnlyList<OutputEvent> outputs, string source) =>
        outputs.Where(e => e.Kind == OutputKind.Pwm && e.Source == source).OrderBy(e => e.Cycle).ToList();

    /// <summary>
    /// For each of the reference's first <paramref name="samples"/> trace samples, the share's time average over the 100 ms
    /// from it, the last share set before it standing until the next; null while no share has been set.
    /// </summary>
    static double?[] Averages(List<OutputEvent> shares, IReadOnlyList<TraceRow> trace, int samples)
    {
        var averages = new double?[samples];
        var next = 0;                                                    // the first event after the window's start
        double? current = null;                                          // the share in force at the window's start
        for (var i = 0; i < samples; i++)
        {
            var (from, to) = (Cycle(trace[i]), Cycle(trace[i]) + SampleCycles);
            while (next < shares.Count && shares[next].Cycle <= from)
                current = shares[next++].Value;
            double sum = 0;
            ulong covered = 0;
            var (at, value) = (from, current);
            for (var k = next; k < shares.Count && shares[k].Cycle < to; k++)
            {
                if (value is { } v)
                {
                    sum += v * (shares[k].Cycle - at);
                    covered += shares[k].Cycle - at;
                }
                (at, value) = (shares[k].Cycle, shares[k].Value);
            }
            if (value is { } last)
            {
                sum += last * (to - at);
                covered += to - at;
            }
            averages[i] = covered > 0 ? sum / covered : null;
        }
        return averages;
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
