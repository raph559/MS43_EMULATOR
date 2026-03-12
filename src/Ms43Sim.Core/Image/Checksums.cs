using System.Buffers.Binary;

namespace Ms43Sim.Core;

public enum ChecksumKind { ProgramSum, CalibrationSum, BootCrc, ProgramCrc, CalibrationCrc }

public sealed record ChecksumResult(ChecksumKind Kind, int StoredAt, uint Stored, uint Computed)
{
    public bool Ok => Stored == Computed;
}

/// <summary>
/// The five MS43 checksums, ported line for line from tools/ms43_checksums.py. MS4X Flasher
/// v1.6.0 computes the same values (design §3); the only difference is that the Flasher reads
/// each CRC range count as a word, which this port does too.
/// </summary>
public static class Checksums
{
    const int Mask = 0x7FFFF;

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
        new ChecksumResult(ChecksumKind.BootCrc, BootCrcStore, U16(image, BootCrcStore), BootCrc(image)),
        new ChecksumResult(ChecksumKind.ProgramCrc, ProgramCrcStore, U16(image, ProgramCrcStore),
            BlockCrc(image, ProgramCrcStore, (int)(U32(image, ProgramCrcSeedPointer) & Mask))),
        new ChecksumResult(ChecksumKind.CalibrationCrc, CalCrcStore, U16(image, CalCrcStore),
            BlockCrc(image, CalCrcStore, (int)(U32(image, CalCrcSeedPointer) & Mask))),
    };

    /// <summary>Recomputes all five, sums first: the calibration sum sits inside the calibration CRC range.</summary>
    public static void Fix(Span<byte> image)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(image[ProgramSumStore..], MonitorSum(image, ProgramSumSeed, ProgramSumRanges));
        BinaryPrimitives.WriteUInt32LittleEndian(image[CalSumStore..], MonitorSum(image, CalSumSeed, CalSumRanges));
        BinaryPrimitives.WriteUInt16LittleEndian(image[BootCrcStore..], (ushort)BootCrc(image));
        BinaryPrimitives.WriteUInt16LittleEndian(image[ProgramCrcStore..],
            (ushort)BlockCrc(image, ProgramCrcStore, (int)(U32(image, ProgramCrcSeedPointer) & Mask)));
        BinaryPrimitives.WriteUInt16LittleEndian(image[CalCrcStore..],
            (ushort)BlockCrc(image, CalCrcStore, (int)(U32(image, CalCrcSeedPointer) & Mask)));
    }

    /// <summary>u32 seed plus every little-endian u16 over two [start, end) ranges.</summary>
    static uint MonitorSum(ReadOnlySpan<byte> image, int seedAt, int rangesAt)
    {
        var sum = U32(image, seedAt);
        for (var i = 0; i < 2; i++)
        {
            var start = (int)(U32(image, rangesAt + 8 * i) & Mask);
            var end = (int)(U32(image, rangesAt + 8 * i + 4) & Mask);
            var words = (end - start) / 2;
            for (var w = 0; w < words; w++)
                sum += U16(image, start + 2 * w);
        }
        return sum;
    }

    /// <summary>The boot block CRC: one inclusive range at 0x3C28, seed word at 0x3FE6.</summary>
    static uint BootCrc(ReadOnlySpan<byte> image)
    {
        var start = (int)(U32(image, BootCrcStore + 4) & Mask);
        var end = (int)(U32(image, BootCrcStore + 8) & Mask);
        return Crc16(image[start..(end + 1)], U16(image, BootCrcSeed));
    }

    /// <summary>Descriptor: +0 u16 crc, +2 u16 count, +4 count × (u32 start, u32 end), inclusive.</summary>
    static uint BlockCrc(ReadOnlySpan<byte> image, int descriptor, int seedAt)
    {
        uint crc = U16(image, seedAt);
        int count = U16(image, descriptor + 2);
        for (var i = 0; i < count; i++)
        {
            var start = (int)(U32(image, descriptor + 4 + 8 * i) & Mask);
            var end = (int)(U32(image, descriptor + 8 + 8 * i) & Mask);
            crc = Crc16(image[start..(end + 1)], crc);
        }
        return crc;
    }

    /// <summary>CRC-16, reflected polynomial 0xA001, byte-wise, no final XOR.</summary>
    static uint Crc16(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return crc & 0xFFFF;
    }

    static ushort U16(ReadOnlySpan<byte> image, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(image[offset..]);

    static uint U32(ReadOnlySpan<byte> image, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(image[offset..]);
}
