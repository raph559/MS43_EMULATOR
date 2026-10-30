using System.Buffers.Binary;
using System.Text.Json;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>The V9 image: V8 plus the watched map. Its machines and helpers are V8's; only the image and symbols differ.</summary>
public static class V9Fixture
{
    public const int Defaults = V8Fixture.Defaults;

    // The watch record at the end of pool A (logical 680..699), below pops' four bytes.
    public const uint WOff = 0x41FA8, WPage = 0x41FAA, AOff = 0x41FAC, APage = 0x41FAE, Kind = 0x41FB0, Count = 0x41FB1,
        Row = 0x41FB2, RowF = 0x41FB4, Col = 0x41FB6, ColF = 0x41FB8, Cols = 0x41FBA, RowIx = 0x41FBB;

    public static byte[] Bytes
    {
        get
        {
            var image = TestBins.CarBytes;
            foreach (var (at, data) in LiveOverlayBin.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot,
                         "sim/tests/Ms43Sim.Tests/fixtures/live_overlay_v9.hex")))) data.CopyTo(image, at);
            return image;
        }
    }

    public static int Symbol(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot,
            "defs/live_overlay_v9_symbols.json")));
        return json.RootElement.GetProperty(name).GetInt32();
    }

    public static JsonElement[] Catalog => JsonDocument.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot,
        "defs/live_overlay_v9_catalog.json"))).RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();

    public static Machine Direct(byte[] image, byte[] call) => V8Fixture.Direct(image, call);

    public static byte[] CallA(string symbol)
    {
        var target = Symbol(symbol) & 0xFFFF;
        return [0xCA, 0x00, (byte)target, (byte)(target >> 8)];
    }

    public static void Execute(Machine m, uint end = 0xC1004, ulong cycles = 100_000) => V8Fixture.Execute(m, end, cycles);

    public static void PutByte(Machine m, uint address, byte value) => V8Fixture.PutByte(m, address, value);

    public static byte Byte(Machine m, uint address) => (byte)(m.Bus.Peek16(address & ~1u) >> (int)(address & 1) * 8);
}

/// <summary>An overlay image and its symbols, so one test class can run against V8 and V9.</summary>
public sealed record OverlayFixture(string Name, Func<byte[]> Bytes, Func<string, int> Symbol)
{
    public static readonly OverlayFixture V8 = new("V8", () => V8Fixture.Bytes, V8Fixture.Symbol);
    public static readonly OverlayFixture V9 = new("V9", () => V9Fixture.Bytes, V9Fixture.Symbol);
}

public sealed class OverlayV9Tests
{
    internal static Machine Boot(byte[]? image = null)
    {
        var bytes = image ?? V9Fixture.Bytes; bytes.AsSpan(0x1FC8A, 4).CopyTo(bytes.AsSpan(0x41000));
        var hk = V9Fixture.Symbol("hk") & 0xFFFF;                    // 0C:1010 enters housekeeping, which ends at 0D:80DE
        new byte[] { 0xFA, 0x0C, (byte)hk, (byte)(hk >> 8) }.CopyTo(bytes, 0x41010);
        new byte[] { 0xDA, 0x0D, 0x9A, 0x02 }.CopyTo(bytes, 0x41020);  // 0C:1020 calls reader 0D:029A, back at 0C:1024
        var m = Machine.Create(FlashImageBuilder.Raw(FirmwareImage.FromBytes(bytes)));
        m.PowerOn(); m.Watchdog.Disable(); m.Bus.XperipheralsEnabled = true;
        m.Sfrs.Write16(0xFE1A, 0x0401); m.Sfrs.Write16(0xFF16, 0x04AF);
        m.Sfrs.Write16(0xFE00, 0x1C); m.Sfrs.Write16(0xFE02, 0x3B); m.Sfrs.Write16(0xFE04, 0x10); m.Sfrs.Write16(0xFE06, 3);
        m.Cpu.SetR(0, 0xF780); m.Bus.Poke16(0xF900, 2); m.Bus.Poke16(0xFD2C, 0x0400);
        return m;
    }

    internal static Machine Start(byte[]? image = null)
    {
        var m = Boot(image);
        Assert.Equal(0xA0, Command(m, V3Fixture.Open())[0]);
        return m;
    }

    internal static byte[] Command(Machine m, byte[] payload)
    {
        var frame = new byte[payload.Length + 3]; frame[0] = 0x12; frame[1] = (byte)frame.Length;
        payload.CopyTo(frame, 2); frame[^1] = frame.Aggregate((byte)0, (a, b) => (byte)(a ^ b));
        for (var i = 0; i < frame.Length; i++) V9Fixture.PutByte(m, 0xF6CDu + (uint)i, frame[i]);
        m.Cpu.Jump(0x0C, 0x1000); m.Run(2_400_000, x => x.Cpu.Address == 0xC1004);
        Assert.Equal(0xC1004u, m.Cpu.Address);
        return V5Fixture.Read(m, 0xF74F, (m.Bus.Peek16(0xF74E) & 255) - 3);
    }

    internal static byte[] Watch(Machine m, uint address) =>
        Command(m, [0x6D, 0x0C, (byte)(address >> 16), (byte)(address >> 8), (byte)address]);

    internal static uint Revision(Machine m) => BinaryPrimitives.ReadUInt32LittleEndian(m.Bus.Xram.AsSpan(0x7C4));
    internal static byte[] Mutate(Machine m, byte[] payload) => Command(m, V3Fixture.Mutate(payload, Revision(m)));
    private static int Free(Machine m) => BinaryPrimitives.ReadUInt16BigEndian(Command(m, [0x6D, 0]).AsSpan(47, 2));

    /// <summary>Runs the housekeeping entry once, with the 10 ms counter at <paramref name="ticks"/>.</summary>
    internal static void Housekeeping(Machine m, ushort ticks)
    {
        m.Bus.Poke16(0xE2A6, ticks);
        m.Cpu.SetR(0, 0xF780);
        m.Cpu.Jump(0x0C, 0x1010);
        m.Run(200_000, x => x.Cpu.Address == 0xD80DE);
        Assert.Equal(0xD80DEu, m.Cpu.Address);
    }

    [Fact]
    public void HeaderIsVersion9With139EntriesAndMagicC0D9()
    {
        var image = V9Fixture.Bytes;
        Assert.Equal("LTOV"u8.ToArray(), image.AsSpan(0x4B544, 4).ToArray());
        Assert.Equal(9, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B548)));
        Assert.Equal(0xFF88, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B54E)));
        Assert.Equal(139, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B550)));
        Assert.Equal(0xC0D9, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B552)));
    }

    [Fact]
    public void PoolAAllocationsMayNotReachTheWatchRecord()
    {
        var m = Start();
        Assert.Equal(908, Free(m));
        Assert.NotEqual(0xA0, Mutate(m, V5Fixture.Load(138, 676))[0]);
        Assert.Equal(908, Free(m));
        Assert.Equal(0xA0, Mutate(m, V5Fixture.Load(138, 672))[0]);
        Assert.Equal(900, Free(m));
    }

    public static TheoryData<int[], int[]> FittingSets => new()
    {
        { [2, 3, 4, 1], [0, 192, 384, 576] },          // the four without the main ignition map, in pool A alone
        { [0, 3, 1, 4], [0, 320, 512, 704] },          // each four without one 192-byte table, that one in pool B
        { [0, 2, 1, 4], [0, 320, 512, 704] },
        { [0, 2, 1, 3], [0, 320, 512, 704] },
    };

    [Theory, MemberData(nameof(FittingSets))] // RF5
    public void V8sFittingCombinationsStillFit(int[] ids, int[] offsets)
    {
        var m = Start();
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.Equal(0xA0, Mutate(m, V5Fixture.Load(ids[i], offsets[i]))[0]);
            Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(ids[i], LiveOverlay.Crc32(V5Fixture.Flash(ids[i]))))[0]);
        }
    }

    [Fact]
    public void WatchSetStoresTheAddressAndClearsTheRecord()
    {
        var m = Start();
        V9Fixture.PutByte(m, V9Fixture.Kind, 1); V9Fixture.PutByte(m, V9Fixture.Count, 9);
        Assert.Equal(new byte[] { 0xA0, 0x07, 0x75, 0xD4 }, Watch(m, 0x775D4));
        Assert.Equal(0x1D, m.Bus.Peek16(V9Fixture.WPage));
        Assert.Equal(0x35D4, m.Bus.Peek16(V9Fixture.WOff));
        Assert.Equal(0, V9Fixture.Byte(m, V9Fixture.Kind));
        Assert.Equal(0, V9Fixture.Byte(m, V9Fixture.Count));
    }

    [Fact]
    public void WatchSetFindsTheCatalogueRow()
    {
        var m = Start();
        var row0 = (uint)V9Fixture.Catalog[0].GetProperty("address").GetInt32();
        Watch(m, row0);
        Assert.Equal(0, V9Fixture.Byte(m, V9Fixture.RowIx));
        Watch(m, 0x70002);
        Assert.Equal(0xFF, V9Fixture.Byte(m, V9Fixture.RowIx));
    }

    [Fact]
    public void WatchAddressZeroClears()
    {
        var m = Start();
        Watch(m, 0x775D4);
        Assert.Equal(new byte[] { 0xA0, 0, 0, 0 }, Watch(m, 0));
        Assert.Equal(0xFFFF, m.Bus.Peek16(V9Fixture.WPage));
    }

    [Fact]
    public void WatchAddressOutOfRangeIsRefusedWithReason4()
    {
        var m = Start();
        Watch(m, 0x775D4);
        Assert.Equal(new byte[] { 0xB0, 4 }, Watch(m, 0x80000));
        Assert.Equal(0x1D, m.Bus.Peek16(V9Fixture.WPage));
        Assert.Equal(0x35D4, m.Bus.Peek16(V9Fixture.WOff));
    }

    [Fact]
    public void WatchNeedsNoSession()
    {
        var m = Boot();
        Assert.Equal(new byte[] { 0xA0, 0x07, 0x75, 0xD4 }, Watch(m, 0x775D4));
        Assert.Equal(0x1D, m.Bus.Peek16(V9Fixture.WPage));
    }

    [Fact] // RF3
    public void WatchEndsAfterThreeSecondsOfSilenceButNotAtEngineOff()
    {
        var m = Start();
        m.Bus.Poke16(0xE2A6, 1000);
        Watch(m, 0x775D4);
        Housekeeping(m, 1299);
        Assert.Equal(0x1D, m.Bus.Peek16(V9Fixture.WPage));
        Housekeeping(m, 1301);
        Assert.Equal(0xFFFF, m.Bus.Peek16(V9Fixture.WPage));

        m.Bus.Poke16(0xF900, 0);                                   // the engine is not running
        m.Bus.Poke16(0xE2A6, 2000);
        Watch(m, 0x775D4);
        for (ushort ticks = 2100; ticks <= 3000; ticks += 100)
        {
            m.Bus.Poke16(0xE2A6, ticks);
            Command(m, [0x6D, 0]);
            Housekeeping(m, ticks);
        }
        Assert.Equal(0x1D, m.Bus.Peek16(V9Fixture.WPage));
    }

    [Fact]
    public void StockLogRequestIsUnchanged()
    {
        byte[] Reply(byte[] image)
        {
            var m = V9Fixture.Direct(image, [0xDA, 0x0C, 0x7E, 0x46]);
            for (var a = 0x8000u; a < 0x8500u; a += 2) m.Bus.Poke16(a, (ushort)(a * 37 + 11));
            V9Fixture.Execute(m, cycles: 400_000);
            return V5Fixture.Read(m, 0xF74E, 77);
        }
        Assert.Equal(Reply(V8Fixture.Bytes), Reply(V9Fixture.Bytes));
    }
}
