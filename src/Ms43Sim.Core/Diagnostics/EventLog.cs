namespace Ms43Sim.Core;

/// <summary>
/// The run's events, in order. Capped so a runaway loop cannot exhaust memory: it keeps the first
/// <see cref="TrapAndResetCapacity"/> traps and resets and, separately, the first
/// <see cref="OtherCapacity"/> other events, so a flood of flash writes cannot push out a trap.
/// Every event is counted, kept or not.
/// </summary>
public sealed class EventLog
{
    public const int TrapAndResetCapacity = 1_000;
    public const int OtherCapacity = 10_000;

    readonly List<SimEvent> events = new();
    readonly Dictionary<Type, long> counts = new();
    int trapsAndResetsKept, otherKept;

    public Func<(ulong Cycle, uint Pc)> Context { get; set; } = () => (0, 0);

    /// <summary>The events kept, in order.</summary>
    public IReadOnlyList<SimEvent> All => events;

    /// <summary>How many events of each type were added, kept or not.</summary>
    public IReadOnlyDictionary<Type, long> Counts => counts;

    /// <summary>Events not kept because their part of the log was full.</summary>
    public long Dropped { get; private set; }

    public long CountOf<T>() where T : SimEvent => counts.GetValueOrDefault(typeof(T));

    public void Add(Func<ulong, uint, SimEvent> make)
    {
        var (cycle, pc) = Context();
        var e = make(cycle, pc);
        counts[e.GetType()] = counts.GetValueOrDefault(e.GetType()) + 1;
        if (e is TrapEvent or ResetEvent)
        {
            if (trapsAndResetsKept == TrapAndResetCapacity)
            {
                Dropped++;
                return;
            }
            trapsAndResetsKept++;
        }
        else
        {
            if (otherKept == OtherCapacity)
            {
                Dropped++;
                return;
            }
            otherKept++;
        }
        events.Add(e);
    }
}
