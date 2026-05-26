using System.Buffers.Binary;

namespace Ms43Sim.Core;

public enum ChecksumKind { ProgramSum, CalibrationSum, BootCrc, ProgramCrc, CalibrationCrc }

/// <summary>One checksum. <paramref name="Problem"/> is set when the image's descriptor is not usable.</summary>
public sealed record ChecksumResult(ChecksumKind Kind, int StoredAt, uint Stored, uint Computed, string? Problem = null)
{
    public bool Ok => Problem == null && Stored == Computed;
}

/// <summary>
/// The five MS43 checksums, ported line for line from tools/ms43_checksums.py. MS4X Flasher
/// v1.6.0 computes the same values (design §3); the only difference is that the Flasher reads
/// each CRC range count as a word, which this port does too.
/// </summary>
public static class Checksums
{
    const int Mask = 0x7FFFF;

    // A real MS43 descriptor lists 2-6 ranges; more means the bytes are not a descriptor (an
    // erased or foreign image). Reporting that, instead of hashing 65535 ranges, keeps any bin
    // loadable.
    const int MaxRanges = 16;

    const int ProgramSumStore = 0x6FDAE, ProgramSumSeed = 0x6FDB2, ProgramSumRanges = 0x6FDBE;
    const int CalSumStore = 0x72FFC, CalSumSeed = 0x6FDB8, CalSumRanges = 0x6FDCE;
    const int BootCrcStore = 0x3C24, BootCrcSeed = 0x3FE6;
    const int ProgramCrcStore = 0x6FDE0, ProgramCrcSeedPointer = 0x6ED42;
    const int CalCrcStore = 0x73FE0, CalCrcSeedPointer = 0x6ED5A;

    public static IReadOnlyList<ChecksumResult> Verify(ReadOnlySpan<byte> image) => new[]
    {
        new ChecksumResult(ChecksumKind.ProgramSum, ProgramSumStore, U32(image, ProgramSumStore),
            MonitorSum(image, ProgramSumSeed, ProgramSumRanges)),
        new ChecksumResult(ChecksumKind.CalibrationSum, CalSumStore, U32(image, CalSumStore),
            MonitorSum(image, CalSumSeed, CalSumRanges)),
        BootResult(image),
        BlockResult(image, ChecksumKind.ProgramCrc, ProgramCrcStore, ProgramCrcSeedPointer),
        BlockResult(image, ChecksumKind.CalibrationCrc, CalCrcStore, CalCrcSeedPointer),
    };

    static ChecksumResult BlockResult(ReadOnlySpan<byte> image, ChecksumKind kind, int store, int seedPointer)
    {
        var (crc, problem) = BlockCrc(image, store, (int)(U32(image, seedPointer) & Mask));
        return new ChecksumResult(kind, store, U16(image, store), crc, problem);
    }

    /// <summary>Recomputes all five, sums first: the calibration sum sits inside the calibration CRC range.</summary>
    public static void Fix(Span<byte> image)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(image[ProgramSumStore..], MonitorSum(image, ProgramSumSeed, ProgramSumRanges));
        BinaryPrimitives.WriteUInt32LittleEndian(image[CalSumStore..], MonitorSum(image, CalSumSeed, CalSumRanges));
        var (bootCrc, bootProblem) = BootCrc(image);
        if (bootProblem == null)
            BinaryPrimitives.WriteUInt16LittleEndian(image[BootCrcStore..], (ushort)bootCrc);
        FixBlock(image, ProgramCrcStore, ProgramCrcSeedPointer);
        FixBlock(image, CalCrcStore, CalCrcSeedPointer);
    }

    /// <summary>Leaves a block with an unusable descriptor as it is.</summary>
    static void FixBlock(Span<byte> image, int store, int seedPointer)
    {
        var (crc, problem) = BlockCrc(image, store, (int)(U32(image, seedPointer) & Mask));
        if (problem == null)
            BinaryPrimitives.WriteUInt16LittleEndian(image[store..], (ushort)crc);
    }

    /// <summary>u32 seed plus every little-endian u16 over two [start, end) ranges.</summary>
    static uint MonitorSum(ReadOnlySpan<byte> image, int seedAt, int rangesAt)
    {
        var sum = U32(image, seedAt);
        for (var i = 0; i < 2; i++)
        {
            var start = (int)(U32(image, rangesAt + 8 * i) & Mask);
            var end = (int)(U32(image, rangesAt + 8 * i + 4) & Mask);
            var words = Math.Max(0, (end - start) / 2);
            for (var w = 0; w < words; w++)
                sum += U16(image, start + 2 * w);
        }
        return sum;
    }

    /// <summary>The boot block CRC: one inclusive range at 0x3C28, seed word at 0x3FE6.</summary>
    static (uint Crc, string? Problem) BootCrc(ReadOnlySpan<byte> image)
    {
        var start = (int)(U32(image, BootCrcStore + 4) & Mask);
        var end = (int)(U32(image, BootCrcStore + 8) & Mask);
        if (start > end)
            return (0, $"boot descriptor at 0x{BootCrcStore + 4:X5}: the range starts after it ends");
        return (Crc16(image[start..(end + 1)], U16(image, BootCrcSeed)), null);
    }

    static ChecksumResult BootResult(ReadOnlySpan<byte> image)
    {
        var (crc, problem) = BootCrc(image);
        return new ChecksumResult(ChecksumKind.BootCrc, BootCrcStore, U16(image, BootCrcStore), crc, problem);
    }

    /// <summary>Descriptor: +0 u16 crc, +2 u16 count, +4 count × (u32 start, u32 end), inclusive.</summary>
    static (uint Crc, string? Problem) BlockCrc(ReadOnlySpan<byte> image, int descriptor, int seedAt)
    {
        uint crc = U16(image, seedAt);
        int count = U16(image, descriptor + 2);
        if (count > MaxRanges)
            return (0, $"descriptor at 0x{descriptor:X5} lists {count} ranges (at most {MaxRanges} expected)");
        for (var i = 0; i < count; i++)
        {
            var start = (int)(U32(image, descriptor + 4 + 8 * i) & Mask);
            var end = (int)(U32(image, descriptor + 8 + 8 * i) & Mask);
            if (start > end)
                return (0, $"descriptor at 0x{descriptor:X5}: range {i} starts after it ends");
            crc = Crc16(image[start..(end + 1)], crc);
        }
        return (crc, null);
    }

    /// <summary>CRC-16, reflected polynomial 0xA001, byte-wise, no final XOR.</summary>
    internal static uint Crc16(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return crc & 0xFFFF;
    }

    // Reads wrap at 512 KiB, as the flash mirrors, so a pointer to the last byte cannot overrun.
    static ushort U16(ReadOnlySpan<byte> image, int offset) =>
        (ushort)(image[offset & Mask] | (image[(offset + 1) & Mask] << 8));

    static uint U32(ReadOnlySpan<byte> image, int offset) =>
        U16(image, offset) | ((uint)U16(image, offset + 2) << 16);
}
