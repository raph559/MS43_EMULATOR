namespace Ms43Sim.Core;

/// <summary>The run's events, in order. Capped so a runaway loop cannot exhaust memory.</summary>
public sealed class EventLog
{
    const int Capacity = 10_000;

    readonly List<SimEvent> events = new();

    public Func<(ulong Cycle, uint Pc)> Context { get; set; } = () => (0, 0);

    public IReadOnlyList<SimEvent> All => events;

    /// <summary>Events not kept because the log was full.</summary>
    public long Dropped { get; private set; }

    public void Add(Func<ulong, uint, SimEvent> make)
    {
        if (events.Count >= Capacity)
        {
            Dropped++;
            return;
        }
        var (cycle, pc) = Context();
        events.Add(make(cycle, pc));
    }
}
