namespace Ms43Sim.Core;

/// <summary>
/// What the drive asks of the engine at one moment (plan 4 R16): the drive's point, whether the key is
/// on, and the seconds since the pedal, the clutch or the gear last changed at a segment boundary (a
/// ramp is not a change). Power is asked in a drive segment with the pedal at 10 % or more, the clutch
/// released, the key on and no such change for more than 0.5 s; the shaft's speed window (900 rpm up
/// to the rev limit − 300) is the power check's own part, from the limit in RAM.
/// </summary>
public sealed record DriveDemand(DrivePoint Point, bool KeyOn, double SecondsSinceChange)
{
    public bool PowerAsked =>
        Point.Mode == DriveMode.Drive && Point.Values["pedal"] >= 10 && Point.Values["clutch"] < 0.5 && KeyOn
        && SecondsSinceChange > 0.5;
}

/// <summary>
/// A drive run as its observers see it: the image and the drive, the power cycle under way, its
/// machine and engine, and what the drive demands now. <see cref="PowerCycleStarted"/> fires for each
/// power cycle's new machine before its power-on, so an observer subscribes to that machine's events;
/// <see cref="PowerCycleEnded"/> fires when its run stops (the relay dropped, or the drive ended).
/// </summary>
public sealed class DriveContext
{
    internal DriveContext(FlashedImage image, Drive drive)
    {
        Image = image;
        Drive = drive;
    }

    public FlashedImage Image { get; }

    public Drive Drive { get; }

    public int PowerCycleIndex { get; internal set; }

    /// <summary>The drive's cycle at which the current power cycle's machine started.</summary>
    public ulong PowerCycleStart { get; internal set; }

    public Machine? Machine { get; internal set; }

    public Engine? Engine { get; internal set; }

    public EngineShaft? Shaft => Engine?.Shaft;

    /// <summary>What the drive asks now; null before the first step of a power cycle.</summary>
    public DriveDemand? Demand { get; internal set; }

    /// <summary>Why the power cycle under way stopped, set before <see cref="PowerCycleEnded"/>; null while it runs.</summary>
    public CycleEnd? Ended { get; internal set; }

    public event Action<Machine, int>? PowerCycleStarted;

    public event Action<Machine, int>? PowerCycleEnded;

    /// <summary>A segment of the drive begins (after the runner's own entry: the key, the starter, the held speed).</summary>
    public event Action<DrivePoint>? SegmentEntered;

    internal void RaiseStarted(Machine machine, int index) => PowerCycleStarted?.Invoke(machine, index);

    internal void RaiseEnded(Machine machine, int index) => PowerCycleEnded?.Invoke(machine, index);

    internal void RaiseSegmentEntered(DrivePoint point) => SegmentEntered?.Invoke(point);
}
