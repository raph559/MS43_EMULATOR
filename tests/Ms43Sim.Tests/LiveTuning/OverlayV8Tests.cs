using System.Buffers.Binary;
using System.Text.Json;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>The V8 image: V7 plus the pops settings group 138, its flashed defaults, limits and pool-A reservation.</summary>
public static class V8Fixture
{
    public const int Defaults = 0x4FF80;
    public static readonly byte[] Flashed = [0x00, 0x14, 0x17, 0xB8, 0x32, 0x9E, 0x00, 0x14];

    public static byte[] Bytes
    {
        get
        {
            var image = TestBins.CarBytes;
            foreach (var (at, data) in LiveOverlayBin.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot,
                         "sim/tests/Ms43Sim.Tests/fixtures/live_overlay_v8.hex")))) data.CopyTo(image, at);
            return image;
        }
    }

    public static int Symbol(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot,
            "defs/live_overlay_v8_symbols.json")));
        return json.RootElement.GetProperty(name).GetInt32();
    }

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
}

public sealed class OverlayV8Tests
{
    private static Machine Start(byte[]? image = null)
    {
        var bytes = image ?? V8Fixture.Bytes; bytes.AsSpan(0x1FC8A, 4).CopyTo(bytes.AsSpan(0x41000));
        var m = Machine.Create(FlashImageBuilder.Raw(FirmwareImage.FromBytes(bytes)));
        m.PowerOn(); m.Watchdog.Disable(); m.Bus.XperipheralsEnabled = true;
        m.Sfrs.Write16(0xFE1A, 0x0401); m.Sfrs.Write16(0xFF16, 0x04AF);
        m.Sfrs.Write16(0xFE00, 0x1C); m.Sfrs.Write16(0xFE02, 0x3B); m.Sfrs.Write16(0xFE04, 0x10); m.Sfrs.Write16(0xFE06, 3);
        m.Cpu.SetR(0, 0xF780); m.Bus.Poke16(0xF900, 2); m.Bus.Poke16(0xFD2C, 0x0400);
        Assert.Equal(0xA0, Command(m, V3Fixture.Open())[0]);
        return m;
    }

    private static byte[] Command(Machine m, byte[] payload)
    {
        var frame = new byte[payload.Length + 3]; frame[0] = 0x12; frame[1] = (byte)frame.Length;
        payload.CopyTo(frame, 2); frame[^1] = frame.Aggregate((byte)0, (a, b) => (byte)(a ^ b));
        for (var i = 0; i < frame.Length; i++)
        {
            var at = 0xF6CDu + (uint)i; var shift = (int)(at & 1) * 8;
            m.Bus.Poke16(at & ~1u, (ushort)((m.Bus.Peek16(at & ~1u) & ~(255 << shift)) | frame[i] << shift));
        }
        m.Cpu.Jump(0x0C, 0x1000); m.Run(2_400_000, x => x.Cpu.Address == 0xC1004);
        Assert.Equal(0xC1004u, m.Cpu.Address);
        return V5Fixture.Read(m, 0xF74F, (m.Bus.Peek16(0xF74E) & 255) - 3);
    }

    private static uint Revision(Machine m) => BinaryPrimitives.ReadUInt32LittleEndian(m.Bus.Xram.AsSpan(0x7C4));
    private static byte[] Mutate(Machine m, byte[] payload) => Command(m, V3Fixture.Mutate(payload, Revision(m)));
    private static byte[] Write(int id, int at, byte[] bytes) => [0x6D, 2, (byte)id, (byte)(at >> 8), (byte)at, .. bytes];
    private static int Free(Machine m) => BinaryPrimitives.ReadUInt16BigEndian(Command(m, [0x6D, 0]).AsSpan(47, 2));

    private static byte[] With(int index, byte value)
    {
        var bytes = V8Fixture.Flashed.ToArray(); bytes[index] = value; return bytes;
    }

    [Fact]
    public void HeaderIsVersion8With139EntriesAndMagicC0D8()
    {
        var image = V8Fixture.Bytes;
        Assert.Equal("LTOV"u8.ToArray(), image.AsSpan(0x4B544, 4).ToArray());
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B548)));
        Assert.Equal(0xFF88, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B54E)));
        Assert.Equal(139, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B550)));
        Assert.Equal(0xC0D8, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B552)));
    }

    [Fact]
    public void Entry138PointsAtTheEightDefaultBytes()
    {
        var image = V8Fixture.Bytes;
        var tables = 0x40000 + BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B54C));
        var entry = image.AsSpan(tables + 138 * 8, 8);
        Assert.Equal(0x33, BinaryPrimitives.ReadUInt16LittleEndian(entry));
        Assert.Equal(0x3F80, BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]));
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]));
        Assert.Equal(V8Fixture.Flashed, image.AsSpan(V8Fixture.Defaults, 8).ToArray());
        Assert.Equal(0x0CFF87u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x6FE00)));
    }

    [Theory]
    [InlineData(0, 3)] [InlineData(1, 0)] [InlineData(1, 51)] [InlineData(3, 127)] [InlineData(3, 211)]
    [InlineData(4, 49)] [InlineData(4, 220)] [InlineData(5, 143)] [InlineData(6, 51)]
    public void Group138RejectsEachOutOfRangeByteWithReason4(int index, byte value)
    {
        var m = Start(); var bad = With(index, value);
        Assert.Equal(0xA0, Mutate(m, V5Fixture.Load(138, 704))[0]);
        var revision = Revision(m);
        Assert.Equal(new byte[] { 0xB1, 4 }, Mutate(m, Write(138, 0, bad)));
        Assert.Equal(revision, Revision(m)); Assert.Equal(V8Fixture.Flashed, V5Fixture.Read(m, 0xE6E0, 8));
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(138, LiveOverlay.Crc32(V8Fixture.Flashed)))[0]);
        revision = Revision(m);
        Assert.Equal(new byte[] { 0xB1, 4 }, Command(m, V5Fixture.Patch(138, 0, bad, revision)));
        Assert.Equal(revision, Revision(m)); Assert.Equal(V8Fixture.Flashed, V5Fixture.Read(m, 0xE6E0, 8));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 2)] [InlineData(1, 1)] [InlineData(1, 50)] [InlineData(3, 128)] [InlineData(3, 210)]
    [InlineData(4, 50)] [InlineData(4, 219)] [InlineData(5, 144)] [InlineData(5, 255)] [InlineData(6, 0)] [InlineData(6, 50)]
    [InlineData(7, 0)] [InlineData(7, 255)]
    public void Group138AcceptsBoundaryValues(int index, byte value)
    {
        var m = Start(); var good = With(index, value);
        Assert.Equal(0xA0, Mutate(m, V5Fixture.Load(138, 704))[0]);
        Assert.Equal(0xA0, Mutate(m, Write(138, 0, good))[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(138, LiveOverlay.Crc32(good)))[0]);
        Assert.Equal(good, V5Fixture.Read(m, 0xE6E0, 8));
    }

    [Fact]
    public void PoolAAllocationsMayNotReachItsLastFourBytes()
    {
        var m = Start();
        Assert.Equal(928, Free(m));
        Assert.NotEqual(0xA0, Mutate(m, V5Fixture.Load(138, 696))[0]);
        Assert.Equal(928, Free(m));
        Assert.Equal(0xA0, Mutate(m, V5Fixture.Load(138, 692))[0]);
        Assert.Equal(920, Free(m));
    }

    [Fact]
    public void PopsSettingClampsFlashedValues()
    {
        byte[] expected = [0, 1, 0, 210, 50, 144, 50, 0];
        for (var index = 0; index < 8; index++)
        {
            var image = V8Fixture.Bytes;
            new byte[] { 0x07, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00 }.CopyTo(image, V8Fixture.Defaults);
            var m = V8Fixture.Direct(image, V8Fixture.CallA("pops_setting"));
            for (var r = 1; r < 16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + r * 19));
            m.Cpu.SetR(0, 0xF780); m.Cpu.SetR(3, (ushort)index);
            var before = Enumerable.Range(0, 16).Select(r => m.Cpu.R(r)).ToArray();
            V8Fixture.Execute(m);
            Assert.True(expected[index] == m.Cpu.R(4), $"byte {index}: {m.Cpu.R(4)}");
            for (var r = 0; r < 16; r++) if (r != 4) Assert.True(before[r] == m.Cpu.R(r), $"byte {index}: R{r} changed");
        }
    }
}
