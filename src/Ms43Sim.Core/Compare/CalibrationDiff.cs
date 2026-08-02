namespace Ms43Sim.Core;

/// <summary>A catalogue entry with changed bytes: its name, where it is, its size and how many of its bytes changed.</summary>
public sealed record CalibrationDifference(string Name, uint Address, int Bytes, int Changed);

/// <summary>
/// The calibration's differences (plan 5 Task 8): 0x70000–0x7FFFF, without the calibration checksum fields (R7). Every
/// catalogue entry with a changed byte is listed, with its changed bytes; changed bytes no entry covers make "unnamed"
/// ranges, those less than 16 bytes apart together. By address.
/// </summary>
public static class CalibrationDiff
{
    const int Start = 0x70000, End = 0x80000, Apart = 16;

    public static IReadOnlyList<CalibrationDifference> Of(FirmwareImage reference, FirmwareImage candidate, CalibrationCatalogue catalogue)
    {
        var changed = Changed(reference, candidate, Start, End);
        if (changed.Count == 0)
            return Array.Empty<CalibrationDifference>();
        var differences = new List<CalibrationDifference>();
        var named = new bool[changed.Count];
        foreach (var entry in catalogue.Entries)
        {
            var (from, to) = (LowerBound(changed, (int)entry.Address), LowerBound(changed, (int)entry.Address + entry.Bytes));
            if (to == from)
                continue;
            differences.Add(new CalibrationDifference(entry.Name, entry.Address, entry.Bytes, to - from));
            for (var i = from; i < to; i++)
                named[i] = true;
        }
        var loose = changed.Where((_, i) => !named[i]).ToList();
        foreach (var (first, last, count) in Runs(loose, Apart))
            differences.Add(new CalibrationDifference("unnamed", (uint)first, last - first + 1, count));
        return differences.OrderBy(d => d.Address).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The offsets in [start, end) where the two images differ, the checksum fields left out, in order.</summary>
    internal static List<int> Changed(FirmwareImage reference, FirmwareImage candidate, int start, int end)
    {
        var a = reference.Bytes;
        var b = candidate.Bytes;
        var skip = new HashSet<int>(Checksums.StoredFields.SelectMany(f => Enumerable.Range(f.Offset, f.Bytes)));
        var changed = new List<int>();
        for (var i = start; i < end && i < a.Length && i < b.Length; i++)
        {
            if (a[i] != b[i] && !skip.Contains(i))
                changed.Add(i);
        }
        return changed;
    }

    /// <summary>Runs of offsets, each next one less than <paramref name="apart"/> bytes after the run's last: (first, last, how many).</summary>
    internal static IEnumerable<(int First, int Last, int Count)> Runs(IReadOnlyList<int> offsets, int apart)
    {
        for (var i = 0; i < offsets.Count;)
        {
            var j = i;
            while (j + 1 < offsets.Count && offsets[j + 1] - (offsets[j] + 1) < apart)
                j++;
            yield return (offsets[i], offsets[j], j - i + 1);
            i = j + 1;
        }
    }

    static int LowerBound(List<int> sorted, int value)
    {
        int low = 0, high = sorted.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle] < value)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }
}
