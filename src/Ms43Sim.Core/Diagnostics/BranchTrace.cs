namespace Ms43Sim.Core;

public enum BranchKind { Jump, Call, Return, Interrupt, Trap, Reti, Reset }

public readonly record struct BranchRecord(ulong Cycle, uint From, uint To, BranchKind Kind);

/// <summary>The last N control transfers, for crash reports.</summary>
public sealed class BranchTrace
{
    readonly BranchRecord[] ring;
    int next, count;

    public BranchTrace(int depth) => ring = new BranchRecord[Math.Max(1, depth)];

    public void Add(BranchRecord record)
    {
        ring[next] = record;
        next = (next + 1) % ring.Length;
        if (count < ring.Length)
            count++;
    }

    /// <summary>The recorded transfers, oldest first.</summary>
    public IReadOnlyList<BranchRecord> Last()
    {
        var result = new BranchRecord[count];
        var start = (next - count + ring.Length) % ring.Length;
        for (var i = 0; i < count; i++)
            result[i] = ring[(start + i) % ring.Length];
        return result;
    }
}
