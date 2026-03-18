namespace Ms43Sim.Core;

/// <summary>
/// The machine's clock (in CPU cycles) and its timed events. Events run in time order, then in
/// the order they were scheduled, so a run is deterministic.
/// </summary>
public sealed class Scheduler
{
    readonly PriorityQueue<Item, (ulong At, long Sequence)> queue = new();
    readonly HashSet<long> cancelled = new();
    long nextSequence;

    public ulong Now { get; private set; }

    /// <summary>The time of the next pending event, or ulong.MaxValue when none is pending.</summary>
    public ulong NextDue
    {
        get
        {
            while (queue.TryPeek(out var item, out var key))
            {
                if (!cancelled.Contains(item.Handle))
                    return key.At;
                queue.Dequeue();
                cancelled.Remove(item.Handle);
            }
            return ulong.MaxValue;
        }
    }

    public long Schedule(ulong at, string name, Action action)
    {
        var handle = nextSequence++;
        queue.Enqueue(new Item(handle, name, action), (at, handle));
        return handle;
    }

    public void Cancel(long handle) => cancelled.Add(handle);

    /// <summary>Runs every event due at or before <paramref name="to"/>, then sets Now to it.</summary>
    public void Advance(ulong to)
    {
        while (queue.TryPeek(out var item, out var key) && key.At <= to)
        {
            queue.Dequeue();
            if (cancelled.Remove(item.Handle))
                continue;
            if (key.At > Now)
                Now = key.At;
            item.Action();
        }
        if (to > Now)
            Now = to;
    }

    readonly record struct Item(long Handle, string Name, Action Action);
}
