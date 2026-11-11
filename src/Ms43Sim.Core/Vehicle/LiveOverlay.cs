namespace Ms43Sim.Core;

/// <summary>
/// One of the five engine tables: its index in the overlay's own list, its name, where the car file holds it
/// (file offset, and the page and page offset its sites load into R13/R12), its size in bytes (each cell one byte, read
/// by 0D:029A), and the file offsets of the CALLS at its sites.
/// </summary>
public sealed record OverlayTable(int Index, string Name, int FileOffset, int Page, int PageOffset, int Bytes, IReadOnlyList<int> Sites);

/// <summary>A table's state in the overlay's directory: Free, Loaded (copied, not confirmed), Ready (confirmed, off), On.</summary>
public enum OverlayState : byte { Free, Loaded, Ready, On }

/// <summary>Why the overlay refused a request: the byte after its B0 (parameter) or B1 (function) status.</summary>
public enum OverlayReason : byte { Length = 1, Sub, Table, Range, Overlap, NotRunning, TableOn, NotLoaded, Integrity, Leaner, EditLimit }

/// <summary>
/// The live-tuning overlay's requests on the K-line, DS2 command 0x6D, as DS2 payloads from the command byte on: the
/// sub-commands' plain forms, which the tests wrap with the session and the revision. Multi-byte arguments are big-endian.
/// </summary>
public static class LiveOverlay
{
    public const byte Command = 0x6D;

    /// <summary>The most data one write carries: its request is then 128 bytes, inside the firmware's 129.</summary>
    public const int MostWrite = 120;

    /// <summary>The word the overlay writes at the head of its control block once it has initialised it.</summary>
    public const ushort Magic = 0xC0DA;

    enum Sub : byte { Load = 1, Write, On, Off, AllOn, AllOff, Free, Revert }

    /// <summary>The five engine tables, catalogue IDs 0 to 4, with the car file's sites that read them.</summary>
    public static IReadOnlyList<OverlayTable> Tables { get; } = new[]
    {
        new OverlayTable(0, "ign98_pl", 0x77315, 0x1D, 0x3315, 320, new[] { 0x2C650 }),
        new OverlayTable(1, "ign98_is", 0x775D4, 0x1D, 0x35D4, 36, new[] { 0x2C3A4 }),
        new OverlayTable(2, "af98", 0x7D69C, 0x1F, 0x169C, 192, new[] { 0x3D40A }),
        new OverlayTable(3, "cam_in", 0x76390, 0x1D, 0x2390, 192, new[] { 0x33B48, 0x33BA2 }),
        new OverlayTable(4, "cam_ex", 0x7629C, 0x1D, 0x229C, 192, new[] { 0x33B74, 0x33BEA }),
    };

    /// <summary>Places table <paramref name="table"/> at <paramref name="offset"/> in the copy area, as a copy of the flashed table.</summary>
    public static byte[] Load(int table, int offset) => Payload(Sub.Load, (byte)table, (byte)(offset >> 8), (byte)offset);

    /// <summary>Writes <paramref name="data"/> (1–120 bytes) into the copy of <paramref name="table"/> from its byte <paramref name="at"/>.</summary>
    public static byte[] Write(int table, int at, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > MostWrite)
            throw new ArgumentException($"a write carries 1 to {MostWrite} bytes, not {data.Length}", nameof(data));
        return Payload(Sub.Write, new[] { (byte)table, (byte)(at >> 8), (byte)at }.Concat(data.ToArray()).ToArray());
    }

    /// <summary>Finalizes the complete copy using CRC-32/ISO-HDLC before activation.</summary>
    public static byte[] On(int table, uint crc) => Payload(Sub.On, (byte)table, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc);

    public static byte[] Off(int table) => Payload(Sub.Off, (byte)table);

    public static byte[] AllOn() => Payload(Sub.AllOn);

    public static byte[] AllOff() => Payload(Sub.AllOff);

    public static byte[] Free(int table) => Payload(Sub.Free, (byte)table);

    public static byte[] Revert() => Payload(Sub.Revert);

    /// <summary>CRC-32/ISO-HDLC of the complete table, including its unchanged cells.</summary>
    public static uint Crc32(ReadOnlySpan<byte> copy)
    {
        var crc = uint.MaxValue;
        foreach (var value in copy)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return ~crc;
    }

    /// <summary>The reason of a B0/B1 refusal; null for an A0 reply.</summary>
    public static OverlayReason? Refusal(IReadOnlyList<byte> reply)
    {
        if (!Ds2.IsFrame(reply) || reply.Count < 4)
            throw new InvalidDataException("not a DS2 reply: " + Convert.ToHexString(reply.ToArray()));
        return reply[2] switch
        {
            0xA0 => null,
            0xB0 or 0xB1 when reply.Count == 5 => (OverlayReason)reply[3],
            _ => throw new InvalidDataException("not an overlay reply: " + Convert.ToHexString(reply.ToArray())),
        };
    }

    /// <summary>A drive file's line that sends <paramref name="payload"/>: <c>kline send 6D …</c>.</summary>
    public static string KLineSend(byte[] payload) => "kline send " + string.Join(" ", payload.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    static byte[] Payload(Sub sub, params byte[] arguments) => new[] { Command, (byte)sub }.Concat(arguments).ToArray();
}
