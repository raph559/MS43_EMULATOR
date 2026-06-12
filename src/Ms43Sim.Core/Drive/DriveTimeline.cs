namespace Ms43Sim.Core;

/// <summary>
/// A drive at one moment: the active segment (the last one past the drive's end), every kept key's
/// value, "speed", "rpm" where the drive sets it (the starter's in a crank, the scripted speed in a
/// drive segment), "battery" while a segment gives it, and the segment's forces and silences.
/// RpmFromEngine: a drive segment whose speed holds the engine's own at its start (after an idle or a
/// crank, R17's "holds the previous segment's end value").
/// </summary>
public sealed record DrivePoint(double Seconds, int SegmentIndex, Segment Segment, double SegmentStart,
    IReadOnlyDictionary<string, double> Values, bool RpmFromEngine, bool Ended)
{
    public DriveMode Mode => Segment.Mode;

    public IReadOnlyDictionary<string, double> Forces => Segment.Forces;

    public IReadOnlySet<uint> Silenced => Segment.Silenced;
}

/// <summary>
/// Where a drive stands at a given time (plan 4 R17). Ramps are linear across their segment; the kept
/// keys (temperatures, ambient, clock, A/C, pedal, gear, clutch, brakes) carry forward from the latest
/// set line or segment that gave them, starting from <see cref="CarState.Default"/>; an idle segment
/// is in neutral at a standstill. In a drive segment the rpm and the road speed resolve as R17 says:
/// both given, they are independent; one given, in gear with the clutch out, the ratio gives the
/// other (<see cref="GearRatios"/>); otherwise the missing one holds the previous segment's end.
/// </summary>
public static class DriveTimeline
{
    /// <summary>The keys a later segment keeps.</summary>
    public static IReadOnlyList<string> KeptKeys { get; } =
        new[] { "coolant", "oil", "intake", "ambient", "clock", "ac", "pedal", "gear", "clutch", "brake" };

    public static double Duration(Drive drive) => drive.Segments.Sum(s => s.Seconds);

    public static DrivePoint At(Drive drive, double seconds)
    {
        if (drive.Segments.Count == 0)
            throw new ArgumentException("a drive has at least one segment", nameof(drive));
        foreach (var at in Starts(drive))
        {
            var end = at.Start + at.Segment.Seconds;
            if (seconds >= end && at.Index < drive.Segments.Count - 1)
                continue;
            var fraction = Math.Clamp((seconds - at.Start) / at.Segment.Seconds, 0, 1);
            var values = Values(at.Kept, at.Segment, fraction);
            var (rpm, speed) = Motion(at.Segment, values, at.PreviousRpm, at.PreviousSpeed, fraction);
            values["speed"] = speed;
            if (rpm is { } r && at.Segment.Mode is DriveMode.Crank or DriveMode.Drive)
                values["rpm"] = r;
            if (at.Segment.Keys.TryGetValue("battery", out var battery))
                values["battery"] = battery.At(fraction);
            return new DrivePoint(seconds, at.Index, at.Segment, at.Start, values,
                at.Segment.Mode == DriveMode.Drive && rpm is null, seconds >= end);
        }
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>A segment's start: the kept values then, and the previous segment's end rpm (null: the engine's own) and road speed.</summary>
    internal sealed record SegmentStart(int Index, Segment Segment, double Start, IReadOnlyDictionary<string, double> Kept,
        double? PreviousRpm, double PreviousSpeed);

    internal static IEnumerable<SegmentStart> Starts(Drive drive)
    {
        var kept = Defaults(drive);
        double? rpm = 0;
        var speed = 0.0;
        var start = 0.0;
        for (var i = 0; i < drive.Segments.Count; i++)
        {
            var segment = drive.Segments[i];
            yield return new SegmentStart(i, segment, start, new Dictionary<string, double>(kept), rpm, speed);
            (rpm, speed) = Motion(segment, Values(kept, segment, 1), rpm, speed, 1);
            foreach (var key in KeptKeys)
            {
                if (segment.Keys.TryGetValue(key, out var ramp))
                    kept[key] = ramp.To;
            }
            if (segment.Mode == DriveMode.Idle)
                kept["gear"] = 0;
            start += segment.Seconds;
        }
    }

    /// <summary>The kept keys at <paramref name="fraction"/> of <paramref name="segment"/>, from the values kept at its start.</summary>
    internal static Dictionary<string, double> Values(IReadOnlyDictionary<string, double> kept, Segment segment, double fraction)
    {
        var values = new Dictionary<string, double>(kept);
        foreach (var key in KeptKeys)
        {
            if (segment.Keys.TryGetValue(key, out var ramp))
                values[key] = ramp.At(fraction);
        }
        if (segment.Mode == DriveMode.Idle)
            values["gear"] = 0;
        return values;
    }

    static Dictionary<string, double> Defaults(Drive drive)
    {
        var car = CarState.Default;
        var kept = new Dictionary<string, double>
        {
            ["coolant"] = car.CoolantC, ["oil"] = car.OilC, ["intake"] = car.IntakeC, ["ambient"] = car.AmbientC,
            ["clock"] = car.ClockMinutes, ["ac"] = car.Ac ? 1 : 0, ["pedal"] = car.PedalFraction * 100,
            ["gear"] = car.Gear, ["clutch"] = car.Clutch ? 1 : 0, ["brake"] = car.Brake ? 1 : 0,
        };
        foreach (var (key, value) in drive.Initial)
            kept[key] = value;
        return kept;
    }

    /// <summary>The rpm (null: the engine's own) and the road speed at <paramref name="fraction"/> of the segment.</summary>
    static (double? Rpm, double Speed) Motion(Segment segment, IReadOnlyDictionary<string, double> values,
        double? previousRpm, double previousSpeed, double fraction)
    {
        var keys = segment.Keys;
        switch (segment.Mode)
        {
            case DriveMode.Crank:
                return (keys.TryGetValue("rpm", out var starter) ? starter.At(fraction) : null, 0);
            case DriveMode.Idle:
                return (null, 0);
            case DriveMode.Drive:
                break;
            default:
                return (0, 0);
        }
        var gear = (int)values["gear"];
        var inGear = gear is >= 1 and <= 5 && values["clutch"] < 0.5;
        var ratio = GearRatios.RpmPerKmh(Math.Clamp(gear, 0, 5));
        var rpm = keys.TryGetValue("rpm", out var r) ? r.At(fraction) : (double?)null;
        var speed = keys.TryGetValue("speed", out var s) ? s.At(fraction) : (double?)null;
        return (rpm, speed) switch
        {
            ({ } both, { } and) => (both, and),
            ({ } alone, null) => (alone, inGear ? alone / ratio : previousSpeed),
            (null, { } alone) => (inGear ? alone * ratio : previousRpm, alone),
            _ => (previousRpm, previousSpeed),
        };
    }
}
