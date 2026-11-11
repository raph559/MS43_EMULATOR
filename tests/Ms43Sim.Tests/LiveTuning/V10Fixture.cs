using System.Buffers.Binary;
using System.Text.Json;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The live overlay the tests run: the committed fixture (protocol 10) over the car file, its symbols and catalogue,
/// the requests of its DS2 command, and bare machines that run one routine, one command or one housekeeping pass.
/// </summary>
public static class V10Fixture
{
    public const int Defaults = 0x4FF80;

    /// <summary>
    /// Groups 138..142 as flashed: pops (4.0 s, -23.625°, a real AFR of 7.0, 4,000 rpm, 3.0 °TPS), 139 reserved (0),
    /// blip (30.0 °TPS at +20.25°), gears and tap (a 150 ms lead, the cap angle -23.625°), ratios.
    /// </summary>
    public static readonly byte[] Flashed =
    [
        0x00, 0x28, 0x00, 0x46, 0x7D, 0x9E, 0x1E, 0x14,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x03, 0x1E, 0xBB, 0x0F, 0x96, 0x19, 0x75,
        0x01, 0x01, 0x01, 0x00, 0x15, 0x28, 0x0F, 0x00,
        0xE2, 0x86, 0x59, 0x43, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>
    /// V10's first flashed settings (4.0 s, 10.0 °TPS, 15.0 °TPS stabs and blip), with V10.1's two new bytes at V10's
    /// rules: 139/4 = 5 (a window ends 160 rpm above its lowest, stabbing or not) and 141/6 = 0 (no lead). V10.2: 138/3
    /// is 121, a real 12.1, the fuel V10's 184 (shown 11.5) gave through the firmware's straight line (x 1.219, real 12.06).
    /// </summary>
    public static readonly byte[] Classic =
    [
        0x00, 0x28, 0x17, 0x79, 0x32, 0x9E, 0x64, 0x14,
        0x14, 0x4B, 0x0F, 0x19, 0x05, 0x00, 0x00, 0x00,
        0x00, 0x03, 0x1E, 0xBB, 0x0F, 0x4B, 0x19, 0x5A,
        0x01, 0x01, 0x01, 0x00, 0x15, 0x28, 0x00, 0x00,
        0xE2, 0x86, 0x59, 0x43, 0x00, 0x00, 0x00, 0x00,
    ];

    public static byte[] Group(int id) => Flashed.AsSpan((id - 138) * 8, 8).ToArray();

    public static byte[] ClassicGroup(int id) => Classic.AsSpan((id - 138) * 8, 8).ToArray();

    // The blip's state at the end of pool A (logical 668..679), below the watch record, and its FDF8 bits.
    public const uint PopsT = 0x41F9C, BlipAdd = 0x41F9E, BlipLeft = 0x41FA0, PopsPh = 0x41FA2, BlipCl = 0x41FA3,
        BlipTgt = 0x41FA4, BlipTap = 0x41FA5, BlipPrev = 0x41FA6, BlipRate = 0x41FA7, Flags = 0xFDF8;

    /// <summary>The spark angle the blip wrote last (E2B5's encoding).</summary>
    public const uint BlipSpk = PopsPh;

    /// <summary>V10.2's stab count, pops' spare byte after POPS_MIN, and its stab bit FDF8.7: both free since V10.3.</summary>
    public const uint PopsStabs = 0x41FBF;
    public const int PopsStab = 7, BlipOn = 8, BlipUsed = 9, BlipArmed = 10, BlipTapping = 11, BlipHold = 12, Guard = 13;

    // The watch record at the end of pool A (logical 680..699), below pops' four bytes. FDF8.5: a watch is set; FDF8.6:
    // it has a live copy.
    public const uint WOff = 0x41FA8, WPage = 0x41FAA, AOff = 0x41FAC, APage = 0x41FAE, Kind = 0x41FB0, Count = 0x41FB1,
        Row = 0x41FB2, RowF = 0x41FB4, Col = 0x41FB6, ColF = 0x41FB8, Cols = 0x41FBA, RowIx = 0x41FBB;

    /// <summary>The session token the tests open with.</summary>
    public const uint Token = 0x7351AB29;

    static readonly Lazy<IReadOnlyList<(int Offset, byte[] Bytes)>> records = new(() => OverlayHex.Parse(File.ReadAllText(
        Path.Combine(OverlayHex.RepositoryRoot, "sim/tests/Ms43Sim.Tests/fixtures/live_overlay_v10.hex"))));

    static readonly Lazy<Dictionary<string, int>> symbols = new(() =>
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(OverlayHex.RepositoryRoot,
            "defs/live_overlay_v10_symbols.json")));
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
    });

    static readonly Lazy<JsonElement[]> catalog = new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        OverlayHex.RepositoryRoot, "defs/live_overlay_v10_catalog.json"))).RootElement.EnumerateArray()
        .Select(e => e.Clone()).ToArray());

    /// <summary>The fixture's records as (file offset, bytes).</summary>
    public static IReadOnlyList<(int Offset, byte[] Bytes)> Records => records.Value;

    /// <summary>The car file with the overlay's records, before any checksum is fixed. A fresh copy each time.</summary>
    public static byte[] Bytes
    {
        get
        {
            var image = TestBins.CarBytes;
            foreach (var (at, data) in records.Value) data.CopyTo(image, at);
            return image;
        }
    }

    public static FlashedImage Image => TestImages.AsFlashed(Bytes);

    /// <summary>The image with <see cref="Classic"/> flashed: today's code deciding with V10's first settings.</summary>
    public static byte[] ClassicBytes
    {
        get
        {
            var image = Bytes;
            Classic.CopyTo(image, Defaults);
            return image;
        }
    }

    /// <summary>A label's CPU address, from the build's listing.</summary>
    public static int Symbol(string name) => symbols.Value[name];

    /// <summary>The catalogue rows by ID: the byte maps (0..123), the settings groups and the word maps.</summary>
    public static JsonElement[] Catalog => catalog.Value;

    /// <summary>The byte maps, read through 0D:029A: IDs 0..123.</summary>
    public static IEnumerable<JsonElement> ByteMaps => Catalog.Where(e => e.TryGetProperty("reader", out var reader) && reader.GetInt32() == 0xD029A);

    /// <summary>A catalogue entry's bytes in the car file.</summary>
    public static byte[] Flash(int id) => TestBins.CarBytes.AsSpan(Catalog[id].GetProperty("address").GetInt32() & 0x7FFFF,
        Catalog[id].GetProperty("bytes").GetInt32()).ToArray();

    /// <summary>The RAM address of a logical copy offset: pool A in the external RAM, pool B in the XRAM from 704.</summary>
    public static uint Physical(int logical) => (uint)(logical < 704 ? 0x41D00 + logical : 0xE6E0 + logical - 704);

    // ---- requests ----

    public static void Put(byte[] p, int at, uint n) => BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(at), n);

    public static uint Get(byte[] p, int at) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(at));

    /// <summary>A drive-script line that sends the request on the K-line.</summary>
    public static string Send(byte[] p) => LiveOverlay.KLineSend(p) + "\n";

    public static byte[] Open(uint token = Token) { var p = new byte[6] { 0x6D, 10, 0, 0, 0, 0 }; Put(p, 2, token); return p; }

    /// <summary>A changing request with the session token and the revision it expects.</summary>
    public static byte[] Mutate(byte[] p, uint revision, uint token = Token)
    {
        var b = new byte[p.Length + 8]; p.AsSpan(0, 2).CopyTo(b); Put(b, 2, token); Put(b, 6, revision); p.AsSpan(2).CopyTo(b.AsSpan(10)); return b;
    }

    public static byte[] Load(int id, int offset, byte descriptor = 255) => [.. LiveOverlay.Load(id, offset), descriptor];

    /// <summary>An eight-byte patch of a live table from its byte <paramref name="at"/>.</summary>
    public static byte[] Patch(int table, int at, byte[] data, uint revision, uint token = Token)
    {
        var p = new byte[26]; p[0] = 0x6D; p[1] = 9;
        Put(p, 2, token); Put(p, 6, revision); p[10] = (byte)table;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(12), (ushort)at);
        data.AsSpan(0, 8).CopyTo(p.AsSpan(14)); Put(p, 22, LiveOverlay.Crc32(p.AsSpan(2, 20))); return p;
    }

    /// <summary>A warm engine from a blank EEPROM: the head of the drives the overlay tests build.</summary>
    public const string Warm = "start blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n";

    /// <summary>A drive's start: warm, cranked, idling, the 0x6D status polled every second, and a session opened.</summary>
    public static string Head => "drive live-v10\n" + Warm + "3 on\nkline every 1 6D 00\n1.5 crank rpm=200\n3 idle\n" + Send(Open());

    /// <summary>
    /// The copy offsets of the four engine tables <see cref="AllLoaded"/> switches on: the main ignition map and the AFR
    /// target in pool A, the idle ignition map and the intake cam map in pool B. The fifth base slot holds
    /// <see cref="Fifth"/> at 512. The exhaust cam map (4) is left on the flash: pool A's last 36 bytes are the blip's,
    /// the watch's and pops' state, so the five engine tables no longer fit together.
    /// </summary>
    public static readonly int[] Offsets = [0, 704, 320, 740];

    /// <summary>A 32-byte map that takes the fifth base slot in <see cref="AllLoaded"/>.</summary>
    public const int Fifth = 5;

    /// <summary>A drive up to five live tables, every base slot used, at revision 10.</summary>
    public static string AllLoaded()
    {
        var text = Head; uint revision = 0;
        foreach (var (id, offset) in Offsets.Select((offset, id) => (id, offset)).Append((Fifth, 512)))
        {
            text += Send(Mutate(Load(id, offset), revision++));
            text += Send(Mutate(LiveOverlay.On(id, LiveOverlay.Crc32(Flash(id))), revision++));
        }
        return text + "1 idle\n";
    }

    // ---- bare machines ----

    /// <summary>A powered machine with the ECU's DPPs and stack, and <paramref name="call"/> at the trampoline 0C:1000.</summary>
    public static Machine Direct(byte[] image, byte[] call)
    {
        call.CopyTo(image, 0x41000);
        var m = Machine.Create(FlashImageBuilder.Raw(FirmwareImage.FromBytes(image)));
        m.PowerOn(); m.Watchdog.Disable(); m.Bus.XperipheralsEnabled = true;
        m.Sfrs.Write16(0xFE1A, 0x0401); m.Sfrs.Write16(0xFF16, 0x04AF);
        m.Sfrs.Write16(0xFE00, 0x1C); m.Sfrs.Write16(0xFE02, 0x3B); m.Sfrs.Write16(0xFE04, 0x10); m.Sfrs.Write16(0xFE06, 3);
        m.Cpu.SetR(0, 0xF780);
        return m;
    }

    /// <summary>A near call from the trampoline at 0C:1000 to an overlay routine in segment 0C.</summary>
    public static byte[] CallA(string symbol)
    {
        var target = Symbol(symbol) & 0xFFFF;
        return [0xCA, 0x00, (byte)target, (byte)(target >> 8)];
    }

    public static void Execute(Machine m, uint end = 0xC1004, ulong cycles = 100_000)
    {
        m.Cpu.Jump(0x0C, 0x1000); m.Run(cycles, x => x.Cpu.Address == end);
        Assert.Equal(end, m.Cpu.Address);
    }

    public static void PutByte(Machine m, uint address, byte value)
    {
        var word = m.Bus.Peek16(address & ~1u);
        var shift = (int)(address & 1) * 8;
        m.Bus.Poke16(address & ~1u, (ushort)((word & ~(255 << shift)) | value << shift));
    }

    public static byte Byte(Machine m, uint address) => (byte)(m.Bus.Peek16(address & ~1u) >> (int)(address & 1) * 8);

    public static byte[] Read(Machine m, uint address, int length) => Enumerable.Range(0, length)
        .Select(i => Byte(m, address + (uint)i)).ToArray();

    public static bool Flag(Machine m, int bit) => (m.Bus.Peek16(Flags) & 1 << bit) != 0;

    /// <summary>
    /// A machine that runs the overlay's own entries with the engine running: 0C:1000 calls the DS2 0x6D handler and
    /// stops at 0C:1004, 0C:1010 enters housekeeping, which ends at 0D:80DE, and 0C:1020 calls the reader 0D:029A.
    /// </summary>
    public static Machine Boot(byte[]? image = null)
    {
        var bytes = image ?? Bytes; bytes.AsSpan(0x1FC8A, 4).CopyTo(bytes.AsSpan(0x41000));
        var hk = Symbol("hk") & 0xFFFF;
        new byte[] { 0xFA, 0x0C, (byte)hk, (byte)(hk >> 8) }.CopyTo(bytes, 0x41010);
        new byte[] { 0xDA, 0x0D, 0x9A, 0x02 }.CopyTo(bytes, 0x41020);
        var m = Machine.Create(FlashImageBuilder.Raw(FirmwareImage.FromBytes(bytes)));
        m.PowerOn(); m.Watchdog.Disable(); m.Bus.XperipheralsEnabled = true;
        m.Sfrs.Write16(0xFE1A, 0x0401); m.Sfrs.Write16(0xFF16, 0x04AF);
        m.Sfrs.Write16(0xFE00, 0x1C); m.Sfrs.Write16(0xFE02, 0x3B); m.Sfrs.Write16(0xFE04, 0x10); m.Sfrs.Write16(0xFE06, 3);
        m.Cpu.SetR(0, 0xF780); m.Bus.Poke16(0xF900, 2); m.Bus.Poke16(0xFD2C, 0x0400);
        return m;
    }

    /// <summary><see cref="Boot"/> with a session opened.</summary>
    public static Machine Start(byte[]? image = null)
    {
        var m = Boot(image);
        Assert.Equal(0xA0, Command(m, Open())[0]);
        return m;
    }

    /// <summary>Sends one 0x6D request through the handler and returns the reply from its status byte.</summary>
    public static byte[] Command(Machine m, byte[] payload)
    {
        var frame = new byte[payload.Length + 3]; frame[0] = 0x12; frame[1] = (byte)frame.Length;
        payload.CopyTo(frame, 2); frame[^1] = frame.Aggregate((byte)0, (a, b) => (byte)(a ^ b));
        for (var i = 0; i < frame.Length; i++) PutByte(m, 0xF6CDu + (uint)i, frame[i]);
        m.Cpu.Jump(0x0C, 0x1000); m.Run(2_400_000, x => x.Cpu.Address == 0xC1004);
        Assert.Equal(0xC1004u, m.Cpu.Address);
        return Read(m, 0xF74F, (m.Bus.Peek16(0xF74E) & 255) - 3);
    }

    public static uint Revision(Machine m) => BinaryPrimitives.ReadUInt32LittleEndian(m.Bus.Xram.AsSpan(0x7C4));

    /// <summary>A changing request with the session's token and the machine's current revision.</summary>
    public static byte[] Mutate(Machine m, byte[] payload) => Command(m, Mutate(payload, Revision(m)));

    /// <summary>The free copy bytes the status reports.</summary>
    public static int Free(Machine m) => BinaryPrimitives.ReadUInt16BigEndian(Command(m, [0x6D, 0]).AsSpan(47, 2));

    public static byte[] Watch(Machine m, uint address) =>
        Command(m, [0x6D, 0x0C, (byte)(address >> 16), (byte)(address >> 8), (byte)address]);

    /// <summary>Runs the housekeeping entry once, with the 10 ms counter at <paramref name="ticks"/>.</summary>
    public static void Housekeeping(Machine m, ushort ticks)
    {
        m.Bus.Poke16(0xE2A6, ticks);
        m.Cpu.SetR(0, 0xF780);
        m.Cpu.Jump(0x0C, 0x1010);
        m.Run(200_000, x => x.Cpu.Address == 0xD80DE);
        Assert.Equal(0xD80DEu, m.Cpu.Address);
    }

    /// <summary>Sets the watch as watch_set and watch_alias leave it, for a CPU test that starts inside a reader.</summary>
    public static void Arm(Machine m, ushort page, ushort offset, ushort copyPage = 0xFFFF, ushort copyOffset = 0)
    {
        m.Bus.Poke16(WPage, page); m.Bus.Poke16(WOff, offset); m.Bus.Poke16(APage, copyPage); m.Bus.Poke16(AOff, copyOffset);
        var flags = (ushort)(m.Bus.Peek16(Flags) & ~0x60);
        if (page != 0xFFFF) flags |= 0x20;
        if (page != 0xFFFF && copyPage != 0xFFFF) flags |= 0x40;
        m.Bus.Poke16(Flags, flags);
    }

    public static bool Armed(Machine m) => (m.Bus.Peek16(Flags) & 0x20) != 0;

    public static bool HasCopy(Machine m) => (m.Bus.Peek16(Flags) & 0x40) != 0;
}
