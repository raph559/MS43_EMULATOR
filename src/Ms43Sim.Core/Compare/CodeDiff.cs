namespace Ms43Sim.Core;

/// <summary>
/// A range of changed program bytes, from <see cref="Start"/> up to <see cref="End"/> (not included), with the candidate's
/// instructions over it: each line the CPU address (the flash as the program sees it, segment 08 at file 0x00000), the
/// bytes and the mnemonic.
/// </summary>
public sealed record CodeDifference(uint Start, uint End, IReadOnlyList<string> Listing);

/// <summary>
/// The program's differences (plan 5 Task 8, C7): 0x00000–0x6FFFF, without the boot and program checksum fields (R7), by
/// byte range; ranges less than 16 bytes apart are one. The listing decodes the candidate from an instruction boundary
/// found by a linear sweep from 32 bytes before the range (C166 instructions are 2 or 4 bytes on even addresses).
/// </summary>
public static class CodeDiff
{
    const int Start = 0x00000, End = 0x70000, Apart = 16, Lead = 32;
    const uint FlashBase = 0x080000;

    public static IReadOnlyList<CodeDifference> Of(FirmwareImage reference, FirmwareImage candidate)
    {
        var changed = CalibrationDiff.Changed(reference, candidate, Start, End);
        var image = candidate.Bytes.ToArray();
        return CalibrationDiff.Runs(changed, Apart)
            .Select(run => new CodeDifference((uint)run.First, (uint)run.Last + 1, Listing(image, run.First, run.Last + 1)))
            .ToList();
    }

    static IReadOnlyList<string> Listing(byte[] image, int start, int end)
    {
        var lines = new List<string>();
        foreach (var d in Decoder.Sweep(image, Math.Max(0, (start & ~1) - Lead), Math.Min(image.Length, end + 4)))
        {
            var (at, size) = ((int)d.Address, d.Size);
            if (at + size <= start)
                continue;                                      // before the range
            if (at >= end)
                break;
            var bytes = string.Join(" ", image.Skip(at).Take(size).Select(b => b.ToString("X2")));
            var cpu = FlashBase + (uint)at;
            lines.Add($"{cpu >> 16:X2}:{cpu & 0xFFFF:X4}  {bytes,-11}  {d.Mnemonic}");
        }
        return lines;
    }
}
