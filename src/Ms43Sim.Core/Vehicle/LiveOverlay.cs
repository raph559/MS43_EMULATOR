namespace Ms43Sim.Core;

/// <summary>
/// One live table of the overlay (plan 6, R1): its index in the overlay's own list, its name, where the car file holds it
/// (file offset, and the page and page offset its sites load into R13/R12), its size in bytes (each cell one byte, read
/// by 0D:029A), and the file offsets of the CALLS at its sites.
/// </summary>
public sealed record OverlayTable(int Index, string Name, int FileOffset, int Page, int PageOffset, int Bytes, IReadOnlyList<int> Sites);

/// <summary>A table's state in the overlay's directory: Free, Loaded (copied, not confirmed), Ready (confirmed, off), On.</summary>
public enum OverlayState : byte { Free, Loaded, Ready, On }

/// <summary>Why the overlay refused a request: the byte after its B0 (parameter) or B1 (function) status.</summary>
public enum OverlayReason : byte { Length = 1, Sub, Table, Range, Overlap, NotRunning, TableOn, NotLoaded, Integrity, Leaner, EditLimit }

/// <summary>A directory entry: the state, the copy's offset in the copy area, and the full-copy CRC-32 used at activation.</summary>
public sealed record OverlayEntry(OverlayState State, int Offset, uint ContentCrc32);

/// <summary>
/// The status reply's fields (plan 6, "The K-line contract"): the version, whether the engine runs, whether the build holds
/// the richer-only rule, the five tables' entries and the copy area's free bytes.
/// </summary>
public sealed record OverlayStatus(int Version, bool Running, bool RicherOnly, IReadOnlyList<OverlayEntry> Tables, int FreeBytes);

/// <summary>The window 9D00–9FDF as RAM holds it: the control block and the copy area (read by an observer, never written).</summary>
public sealed record OverlayWindow(bool Initialised, ushort Seen, IReadOnlyList<OverlayEntry> Tables, byte[] Copies);

/// <summary>
/// The live-tuning overlay's K-line contract, DS2 command 0x6D (plan 6): the requests the app sends, as DS2 payloads from the
/// command byte on, and readers for its replies and for the window in RAM. Multi-byte arguments and reply fields are
/// big-endian; the window in RAM is the C167's little-endian.
/// </summary>
public static class LiveOverlay
{
    public const byte Command = 0x6D;
    public const byte Version = 2;
    public const int MaxRawDelta = 8;

    /// <summary>The copy area: 688 bytes from 9D30, after the 48-byte control block at 9D00.</summary>
    public const int CopyAddress = 0x9D30, CopyBytes = 688;

    /// <summary>The most data one write carries: its request is then 128 bytes, inside the firmware's 129.</summary>
    public const int MostWrite = 120;

    /// <summary>300 processed firmware ticks without a 0x6D frame; nominally 3 s, dependent on main-task progress.</summary>
    public const int SilenceTicks = 300;

    public const ushort Magic = 0xC0D2;

    const int WindowInRam = 0x1D00;                          // 9D00 through DPP2 (10h): external RAM 41D00h, offset 1D00h

    enum Sub : byte { Status, Load, Write, On, Off, AllOn, AllOff, Free, Revert }

    /// <summary>
    /// The five live tables (plan 6, R1), in the overlay's order. The owner took the pedal map and the fuel constant K out
    /// of the live set (2026-10-22).
    /// </summary>
    public static IReadOnlyList<OverlayTable> Tables { get; } = new[]
    {
        new OverlayTable(0, "ign98_pl", 0x77315, 0x1D, 0x3315, 320, new[] { 0x2C650 }),
        new OverlayTable(1, "ign98_is", 0x775D4, 0x1D, 0x35D4, 36, new[] { 0x2C3A4 }),
        new OverlayTable(2, "af98", 0x7D69C, 0x1F, 0x169C, 192, new[] { 0x3D40A }),
        new OverlayTable(3, "cam_in", 0x76390, 0x1D, 0x2390, 192, new[] { 0x33B48, 0x33BA2 }),
        new OverlayTable(4, "cam_ex", 0x7629C, 0x1D, 0x229C, 192, new[] { 0x33B74, 0x33BEA }),
    };

    public static byte[] Status() => Payload(Sub.Status);

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

    /// <summary>
    /// The writes that turn a copy of <paramref name="flash"/> into <paramref name="copy"/>: one per run of changed bytes, a
    /// run longer than <see cref="MostWrite"/> split in order.
    /// </summary>
    public static IReadOnlyList<byte[]> Edit(int table, ReadOnlySpan<byte> copy, ReadOnlySpan<byte> flash)
    {
        if (copy.Length != flash.Length)
            throw new ArgumentException("the copy and the flashed table differ in size", nameof(copy));
        var writes = new List<byte[]>();
        for (var i = 0; i < copy.Length;)
        {
            if (copy[i] == flash[i])
            {
                i++;
                continue;
            }
            var end = i;
            while (end < copy.Length && copy[end] != flash[end] && end - i < MostWrite)
                end++;
            writes.Add(Write(table, i, copy[i..end]));
            i = end;
        }
        return writes;
    }

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

    /// <summary>The fields of a whole status reply (<c>12 34 A0 …</c>).</summary>
    public static OverlayStatus ParseStatus(IReadOnlyList<byte> reply)
    {
        if (!Ds2.IsFrame(reply) || reply.Count != 52 || reply[2] != 0xA0 || reply[3] != 'L' || reply[4] != 'T' || reply[5] != 'O' || reply[6] != 'V')
            throw new InvalidDataException("not the overlay's status reply: " + Convert.ToHexString(reply.ToArray()));
        var p = reply.Skip(3).ToArray();
        int Word(int at) => p[at] << 8 | p[at + 1];
        if (p[4] != Version)
            throw new InvalidDataException($"overlay protocol {p[4]} is unsupported; expected {Version}");
        uint Dword(int at) => (uint)Word(at) << 16 | (uint)Word(at + 2);
        var tables = Enumerable.Range(0, Tables.Count).Select(i => new OverlayEntry((OverlayState)p[6 + 8 * i], Word(8 + 8 * i), Dword(10 + 8 * i)))
            .ToArray();
        return new OverlayStatus(p[4], (p[5] & 1) != 0, (p[5] & 2) != 0, tables, Word(46));
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

    /// <summary>The window as the external RAM holds it (<c>Bus.ExternalRam</c>, 9D00 at offset 1D00h).</summary>
    public static OverlayWindow ReadWindow(byte[] externalRam)
    {
        int Word(int at) => externalRam[WindowInRam + at] | externalRam[WindowInRam + at + 1] << 8;
        uint Dword(int at) => (uint)Word(at) | (uint)Word(at + 2) << 16;
        var tables = Enumerable.Range(0, Tables.Count)
            .Select(i => new OverlayEntry((OverlayState)externalRam[WindowInRam + 8 + 8 * i], Word(10 + 8 * i), Dword(12 + 8 * i)))
            .ToArray();
        return new OverlayWindow(Word(0) == Magic, (ushort)Word(2), tables, externalRam[(WindowInRam + 0x30)..(WindowInRam + 0x30 + CopyBytes)]);
    }

    /// <summary>A drive file's line that sends <paramref name="payload"/>: <c>kline send 6D …</c>.</summary>
    public static string KLineSend(byte[] payload) => "kline send " + string.Join(" ", payload.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    static byte[] Payload(Sub sub, params byte[] arguments) => new[] { Command, (byte)sub }.Concat(arguments).ToArray();
}
