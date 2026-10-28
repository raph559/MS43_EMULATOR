using System.Buffers.Binary;
using System.Text.Json;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using Xunit.Abstractions;

namespace Ms43Sim.Tests.LiveTuning;

public static class V7Fixture
{
    public static byte[] Bytes
    {
        get
        {
            var image = TestBins.CarBytes;
            foreach (var (at, data) in LiveOverlayBin.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot,
                         "sim/tests/Ms43Sim.Tests/fixtures/live_overlay_v7.hex")))) data.CopyTo(image, at);
            return image;
        }
    }

    public static JsonElement[] NewMaps
    {
        get
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot,
                "defs/live_overlay_v7_new_maps.json")));
            return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        }
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

    public static void Execute(Machine m)
    {
        m.Cpu.Jump(0x0C, 0x1000); m.Run(100_000, x => x.Cpu.Address == 0xC1004);
        Assert.Equal(0xC1004u, m.Cpu.Address);
    }
}

public sealed class OverlayV7ReaderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(7)] [InlineData(8)]
    public void AllWordReadersMatchStockInterpolationAndRegisterEffectsAcrossBothPoolsAndFallbacks(int version)
    {
        var cases = 0;
        var overhead = new Dictionary<string, ulong>();
        foreach (var map in V7Fixture.NewMaps)
        {
            var id = map.GetProperty("id").GetInt32(); var address = map.GetProperty("address").GetInt32();
            var bytes = map.GetProperty("bytes").GetInt32(); var site = map.GetProperty("sites")[0].GetInt32();
            var descriptor = map.GetProperty("descriptor").ValueKind == JsonValueKind.Null ? (int?)null : map.GetProperty("descriptor").GetInt32();
            var changed = new byte[bytes]; ushort[] values = [0, 65535, 1, 32768, 65534, 16384, 42, 49152];
            for (var at = 0; at < bytes; at += 2) BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(at), values[at / 2 % values.Length]);
            foreach (var poolB in new[] { false, true }.Where(b => !b || bytes <= 200))
            foreach (var extra in new[] { false, true })
            foreach (var state in new[] { "uninitialised", "absent", "absent-indexed", "disabled", "expired", "stopped", "live" })
            {
                var logical = poolB ? 704 : 0; var physical = V5Fixture.Physical(logical);
                var live = state == "live";
                var reference = TestBins.CarBytes;
                if (live && descriptor is { } d)
                {
                    var at = 0x6C000 + d - 0x4000;
                    BinaryPrimitives.WriteUInt16LittleEndian(reference.AsSpan(at), (ushort)(physical % 0x4000));
                    BinaryPrimitives.WriteUInt16LittleEndian(reference.AsSpan(at+2), (ushort)(physical / 0x4000));
                }
                var image = version == 8 ? V8Fixture.Bytes : V7Fixture.Bytes;
                var actual = V7Fixture.Direct(image, image.AsSpan(site, 4).ToArray());
                var expected = V7Fixture.Direct(reference, TestBins.CarBytes.AsSpan(site, 4).ToArray());
                foreach (var m in new[] { actual, expected })
                {
                    m.Bus.Poke16(0xE6B0, state == "uninitialised" ? (ushort)0 : (ushort)(0xC0D0 + version));
                    m.Bus.Xram[0x6DB] = 255;
                    m.Bus.Poke16(0xF900, state == "stopped" ? (ushort)0 : (ushort)2);
                    m.Bus.Poke16(0xFD2C, 0x0400); m.Bus.Poke16(0xE2A6, state == "expired" ? (ushort)300 : (ushort)0);
                    if (state != "absent")
                    {
                        var record = extra ? 0xE7A8u : 0xE6D8u;
                        m.Bus.Poke16(record, (ushort)(id << 8 | (state == "disabled" ? 2 : 3)));
                        m.Bus.Poke16(record+2, (ushort)(0xFF00 | logical/4));
                        if (extra)
                        {
                            m.Bus.Xram[0x6DB] = 50;
                            m.Bus.Xram[0x7B0 + id/8] = (byte)(1 << (id%8));
                        }
                    }
                    if (state == "absent-indexed")
                    {
                        m.Bus.Poke16(0xE6D8, 0);
                        m.Bus.Xram[0x6DB] = 50;
                        m.Bus.Poke16(0xE7A8, 3);
                        m.Bus.Xram.AsSpan(0x7B0, 20).Clear();
                        m.Bus.Xram[0x7B0] = 1;
                    }
                    if (poolB) changed.CopyTo(m.Bus.Xram, 0x6E0); else changed.CopyTo(m.Bus.ExternalRam, 0x1D00);
                }
                for (var sample = 0; sample < 8; sample++)
                {
                    foreach (var m in new[] { actual, expected })
                    {
                        for (var r=1; r<16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + r*19));
                        m.Cpu.SetR(0, 0xF780);
                        m.Cpu.Psw = (ushort)(0x0040 | sample);
                        if (descriptor is { } descriptorPointer)
                        {
                            m.Cpu.SetR(12, (ushort)descriptorPointer);
                            m.Cpu.SetR(13, new ushort[] { 0, 1000, 2500, 4000, 65535, 32768, 512, 6000 }[sample]);
                            m.Cpu.SetR(14, new ushort[] { 0, 128, 2500, 14000, 65535, 32768, 192, 4000 }[sample]);
                        }
                        else
                        {
                            var pointer = ReferenceEquals(m, expected) && live ? (int)physical : address;
                            m.Cpu.SetR(12, (ushort)(pointer % 0x4000)); m.Cpu.SetR(13, (ushort)(pointer / 0x4000));
                            var rows = map.GetProperty("rows").GetInt32(); var cols = map.GetProperty("columns").GetInt32();
                            var last = sample == 7;
                            m.Bus.Poke16(0xFB6A, (ushort)((last ? rows-1 : sample % (rows-1)) << 8));
                            m.Bus.Poke16(0xFB6C, (ushort)(cols << 8 | (cols == 1 ? 0 : last ? cols-1 : sample % (cols-1))));
                            m.Bus.Poke16(0xE390, (ushort)(last || sample % 3 == 0 ? 0 : sample % 2 == 0 ? 0x7FFF : 0x4000));
                            m.Bus.Poke16(0xE392, (ushort)(last || sample % 2 == 0 ? 0 : 0x4000));
                        }
                    }
                    var expectedStart = expected.Cycles; var actualStart = actual.Cycles;
                    V7Fixture.Execute(expected); V7Fixture.Execute(actual);
                    var extraCycles = (actual.Cycles - actualStart) - (expected.Cycles - expectedStart);
                    overhead[state] = Math.Max(overhead.GetValueOrDefault(state), extraCycles);
                    var context = $"id={id} state={state} poolB={poolB} extra={extra} sample={sample}";
                    for (var r=0; r<16; r++) Assert.True(expected.Cpu.R(r) == actual.Cpu.R(r), context+$" R{r}: {expected.Cpu.R(r):X4} != {actual.Cpu.R(r):X4}");
                    Assert.True(expected.Cpu.Psw == actual.Cpu.Psw, context+" PSW");
                    Assert.Equal(expected.Cpu.Sp, actual.Cpu.Sp);
                    for (var dpp=0; dpp<4; dpp++) Assert.Equal(expected.Cpu.Dpp(dpp), actual.Cpu.Dpp(dpp));
                    Assert.Equal(expected.Sfrs.Read16(0xFE0C), actual.Sfrs.Read16(0xFE0C));
                    Assert.Equal(expected.Sfrs.Read16(0xFE0E), actual.Sfrs.Read16(0xFE0E));
                    cases++;
                }
            }
        }
        output.WriteLine($"{cases} full-register comparisons against original C167 readers, including u16 extremes and interpolation boundaries.");
        foreach (var (state, cycles) in overhead) output.WriteLine($"Maximum word-hook overhead, {state}: {cycles / 24.0:F3} us");
    }
}
