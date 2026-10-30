using System.Buffers.Binary;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The sixteen descriptor lookups (0D:049A … 0D:1044) with V9's watch at their MOV DPP0 site. Direct CPU tests from the
/// helper's entry to its RETS, with a real descriptor of the donor in R12, V9 against the donor.
/// </summary>
public sealed class WatchDescriptorTests
{
    public sealed record Lookup(int Index, uint Entry, uint Site, bool Map, ushort Descriptor);

    // Each helper with the first descriptor one of the donor's own calls passes it.
    public static readonly Lookup[] Lookups =
    [
        new(0, 0xD049A, 0xD04B0, true, 0x42D2), new(1, 0xD05B2, 0xD05C8, true, 0x409E), new(2, 0xD0760, 0xD0776, true, 0x4032),
        new(3, 0xD0888, 0xD089E, true, 0x401E), new(4, 0xD09A6, 0xD09BC, true, 0x4450), new(5, 0xD0B4A, 0xD0B60, true, 0x405A),
        new(6, 0xD0C72, 0xD0C88, true, 0x4824), new(7, 0xD0EAC, 0xD0EC2, true, 0x45D2), new(8, 0xD107C, 0xD1092, true, 0x472C),
        new(9, 0xD06D6, 0xD06E4, false, 0x4052), new(10, 0xD0AC8, 0xD0AD6, false, 0x4006), new(11, 0xD0D96, 0xD0DA4, false, 0x4016),
        new(12, 0xD0E22, 0xD0E30, false, 0x4066), new(13, 0xD0FD4, 0xD0FE2, false, 0x412E), new(14, 0xD100A, 0xD1018, false, 0x475C),
        new(15, 0xD1044, 0xD1052, false, 0x440E),
    ];

    private static readonly (ushort Row, ushort Col)[] Inputs = [(0x1234, 0x0056), (0, 0), (0xFFFF, 0xFFFF), (0x0800, 0x0123)];

    /// <summary>The descriptor's data address: its first two words, offset then page.</summary>
    private static (ushort Page, ushort Offset) Data(Lookup lookup)
    {
        var at = 0x6C000 + lookup.Descriptor - 0x4000;
        var car = TestBins.CarBytes;
        return (BinaryPrimitives.ReadUInt16LittleEndian(car.AsSpan(at + 2)), BinaryPrimitives.ReadUInt16LittleEndian(car.AsSpan(at)));
    }

    private static Machine Seed(byte[] image, Lookup lookup, int sample, bool watched)
    {
        var call = new byte[] { 0xDA, (byte)(lookup.Entry >> 16), (byte)lookup.Entry, (byte)(lookup.Entry >> 8) };
        var m = V9Fixture.Direct(image, call);
        for (var r = 1; r < 16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + r * 19 + sample * 0x0101));
        m.Cpu.SetR(0, 0xF780); m.Cpu.SetR(12, lookup.Descriptor);
        m.Cpu.SetR(13, Inputs[sample].Row); m.Cpu.SetR(14, Inputs[sample].Col);
        m.Cpu.Psw = 0xF800;
        var (page, offset) = watched ? Data(lookup) : ((ushort)0x1F, (ushort)0x169C);
        m.Bus.Poke16(V9Fixture.WPage, page); m.Bus.Poke16(V9Fixture.WOff, offset);
        m.Bus.Poke16(V9Fixture.APage, 0xFFFF); m.Bus.Poke16(V9Fixture.AOff, 0);
        return m;
    }

    private static void Run(Machine m) => V9Fixture.Execute(m, cycles: 400_000);

    private static void EqualCpu(Machine expected, Machine actual, string context)
    {
        for (var r = 0; r < 16; r++)
            Assert.True(expected.Cpu.R(r) == actual.Cpu.R(r), $"{context}: R{r} expected {expected.Cpu.R(r):X4}, actual {actual.Cpu.R(r):X4}");
        Assert.True(expected.Cpu.Psw == actual.Cpu.Psw, $"{context}: PSW expected {expected.Cpu.Psw:X4}, actual {actual.Cpu.Psw:X4}");
        for (var dpp = 0; dpp < 4; dpp++) Assert.Equal(expected.Cpu.Dpp(dpp), actual.Cpu.Dpp(dpp));
        Assert.Equal(expected.Cpu.Sp, actual.Cpu.Sp);
    }

    public static IEnumerable<object[]> AllLookups() => Enumerable.Range(0, 16).Select(i => new object[] { i });

    public static IEnumerable<object[]> NotWatched() =>
        from lookup in Enumerable.Range(0, 16) from watch in new[] { "none", "other map" } select new object[] { lookup, watch };

    [Theory, MemberData(nameof(NotWatched))]
    public void EachDescriptorLookupIsStockWhenNotWatched(int index, string watch)
    {
        var lookup = Lookups[index];
        for (var sample = 0; sample < Inputs.Length; sample++)
        {
            var expected = Seed(TestBins.CarBytes, lookup, sample, false);
            var actual = Seed(V9Fixture.Bytes, lookup, sample, false);
            if (watch == "none") foreach (var m in new[] { expected, actual }) m.Bus.Poke16(V9Fixture.WPage, 0xFFFF);
            ushort dppExpected = 0, dppActual = 0;
            var after = lookup.Site + 4;
            expected.OnInstruction(after, x => dppExpected = x.Cpu.Dpp(0));
            actual.OnInstruction(after, x => dppActual = x.Cpu.Dpp(0));
            Run(expected); Run(actual);
            EqualCpu(expected, actual, $"lookup {index}, {watch}, sample {sample}");
            Assert.Equal(dppExpected, dppActual);
            Assert.Equal(0, V9Fixture.Byte(actual, V9Fixture.Count));
        }
    }

    [Theory, MemberData(nameof(AllLookups))]
    public void EachDescriptorLookupRecordsItsInputs(int index)
    {
        var lookup = Lookups[index];
        var expected = Seed(TestBins.CarBytes, lookup, 0, true); Run(expected);
        var actual = Seed(V9Fixture.Bytes, lookup, 0, true); Run(actual);
        Assert.Equal(lookup.Map ? 3 : 4, V9Fixture.Byte(actual, V9Fixture.Kind));
        Assert.Equal(0x1234, actual.Bus.Peek16(V9Fixture.Row));
        Assert.Equal(lookup.Map ? 0x0056 : 0, actual.Bus.Peek16(V9Fixture.Col));
        Assert.Equal(0, actual.Bus.Peek16(V9Fixture.RowF));
        Assert.Equal(0, actual.Bus.Peek16(V9Fixture.ColF));
        Assert.Equal(0, V9Fixture.Byte(actual, V9Fixture.Cols));
        Assert.Equal(1, V9Fixture.Byte(actual, V9Fixture.Count));
        EqualCpu(expected, actual, $"lookup {index}");
    }

    [Theory, MemberData(nameof(AllLookups))]
    public void RecordWritesRunWithInterruptsHeldOff(int index)
    {
        var lookup = Lookups[index];
        var m = Seed(V9Fixture.Bytes, lookup, 0, true);
        var start = (uint)V9Fixture.Symbol($"wd{index}_rec"); var end = (uint)V9Fixture.Symbol($"wd{index}_end");
        var seen = 0;
        for (var at = start; at < end; at += 2)
            m.OnInstruction(at, x => { seen++; Assert.True((x.Cpu.Psw & 0x0800) == 0, $"IEN set at {x.Cpu.Address:X5}"); });
        Run(m);
        Assert.True(seen > 5, $"only {seen} instructions seen");
        Assert.Equal(0x0800, m.Cpu.Psw & 0x0800);
        Assert.Equal(1, V9Fixture.Byte(m, V9Fixture.Count));
    }

    [Fact]
    public void V7LiveWordMapsAreSeenThroughTheirRamCopy()
    {
        var map = V7Fixture.NewMaps.Single(m => m.GetProperty("id").GetInt32() == 131);
        var site = map.GetProperty("sites")[0].GetInt32();
        var address = map.GetProperty("address").GetInt32();
        var descriptor = map.GetProperty("descriptor").GetInt32();

        byte Counted(bool alias)
        {
            var image = V9Fixture.Bytes;
            var m = V9Fixture.Direct(image, image.AsSpan(site, 4).ToArray());
            m.Bus.Poke16(0xE6B0, 0xC0D9); m.Bus.Xram[0x6DB] = 255;
            m.Bus.Poke16(0xF900, 2); m.Bus.Poke16(0xFD2C, 0x0400); m.Bus.Poke16(0xE2A6, 0);
            m.Bus.Poke16(0xE6D8, (ushort)(131 << 8 | 3)); m.Bus.Poke16(0xE6DA, 0xFF00);      // On, at logical 0
            for (var r = 1; r < 16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + r * 19));
            m.Cpu.SetR(0, 0xF780); m.Cpu.SetR(12, (ushort)descriptor); m.Cpu.SetR(13, 1000); m.Cpu.SetR(14, 128);
            m.Bus.Poke16(V9Fixture.WPage, (ushort)(address / 0x4000)); m.Bus.Poke16(V9Fixture.WOff, (ushort)(address % 0x4000));
            m.Bus.Poke16(V9Fixture.APage, alias ? (ushort)0x10 : (ushort)0xFFFF); m.Bus.Poke16(V9Fixture.AOff, 0x1D00);
            V7Fixture.Execute(m);
            return V9Fixture.Byte(m, V9Fixture.Count);
        }

        Assert.Equal(1, Counted(alias: true));
        Assert.Equal(0, Counted(alias: false));
    }
}
