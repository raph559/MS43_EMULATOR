namespace Ms43Sim.Core;

public enum AccessKind { Read, Write, Fetch }

public enum AccessArea { Sfr, Esfr, ChipSelect1, ChipSelect2, ChipSelect3, ChipSelect4, Reserved }

public sealed record AccessRecord(
    AccessArea Area, uint Address, string Name, AccessKind Kind, long Count, ulong FirstCycle, uint FirstPc);

/// <summary>
/// Every access to hardware the emulator does not model, grouped by (area, address, kind),
/// with a count and the first cycle and PC. This is the input to the peripheral inventory.
/// </summary>
public sealed class AccessInventory
{
    readonly Dictionary<(AccessArea, uint, AccessKind), Entry> entries = new();

    public Func<(ulong Cycle, uint Pc)> Context { get; set; } = () => (0, 0);

    public void Note(AccessArea area, uint address, AccessKind kind)
    {
        var key = (area, address, kind);
        if (entries.TryGetValue(key, out var entry))
        {
            entry.Count++;
            return;
        }
        var (cycle, pc) = Context();
        entries[key] = new Entry { Count = 1, FirstCycle = cycle, FirstPc = pc };
    }

    public IReadOnlyList<AccessRecord> Records =>
        entries
            .Select(e => new AccessRecord(e.Key.Item1, e.Key.Item2, NameOf(e.Key.Item1, e.Key.Item2), e.Key.Item3,
                e.Value.Count, e.Value.FirstCycle, e.Value.FirstPc))
            .OrderBy(r => r.Area).ThenBy(r => r.Address).ThenBy(r => r.Kind)
            .ToList();

    static string NameOf(AccessArea area, uint address) => area switch
    {
        AccessArea.Sfr or AccessArea.Esfr => SfrTable.ByAddress(address)?.Name ?? "?",
        AccessArea.ChipSelect1 => "CS1 window",
        AccessArea.ChipSelect2 => "CS2 window",
        AccessArea.ChipSelect3 => "CS3 window",
        AccessArea.ChipSelect4 => "CS4 window",
        AccessArea.Reserved => "reserved",
        _ => "?",
    };

    sealed class Entry
    {
        public long Count;
        public ulong FirstCycle;
        public uint FirstPc;
    }
}
