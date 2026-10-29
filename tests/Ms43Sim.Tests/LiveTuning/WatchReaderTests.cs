using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The eight lookup-library readers (0D:0168 … 0D:0454) with V9's watch at their entry. Direct CPU tests from the
/// reader's entry to its RETS, V9 against the donor's original reader.
/// </summary>
public sealed class WatchReaderTests
{
    public sealed record Reader(int Index, uint Entry, bool Map);

    public static readonly Reader[] Readers =
    [
        new(0, 0xD022A, false), new(1, 0xD0262, false), new(2, 0xD0454, false), new(3, 0xD042E, false),
        new(4, 0xD0168, true), new(5, 0xD029A, true), new(6, 0xD03D2, true), new(7, 0xD043A, true),
    ];

    // The data read: inside the RON98 part-load ignition map's page, at an even offset so the u16 readers' word loads are aligned.
    private const ushort Page = 0x1D, Offset = 0x3316;

    private static readonly (byte Row, ushort RowF, byte Col, ushort ColF, byte Cols)[] Indices =
        [(3, 0x8000, 5, 0x4000, 12), (0, 0, 0, 0, 12), (7, 0xFFFF, 11, 0x0001, 12), (1, 0x1234, 2, 0xABCD, 16), (5, 0x4000, 0, 0x8000, 4)];

    public enum Watch { None, OtherMap, OtherOffset, This, LiveCopy }

    private static Machine Seed(byte[] image, Reader reader, int sample, Watch watch, ushort r13 = Page, ushort r12 = Offset)
    {
        var call = new byte[] { 0xDA, (byte)(reader.Entry >> 16), (byte)reader.Entry, (byte)(reader.Entry >> 8) };
        var m = V9Fixture.Direct(image, call);
        for (var r = 1; r < 16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + r * 19 + sample * 0x0101));
        m.Cpu.SetR(0, 0xF780); m.Cpu.SetR(12, r12); m.Cpu.SetR(13, r13);
        m.Cpu.Psw = 0xF800;
        var i = Indices[sample];
        V9Fixture.PutByte(m, 0xFB6B, i.Row); m.Bus.Poke16(0xE390, i.RowF);
        V9Fixture.PutByte(m, 0xFB6C, i.Col); m.Bus.Poke16(0xE392, i.ColF); V9Fixture.PutByte(m, 0xFB6D, i.Cols);
        var (page, offset) = watch switch
        {
            Watch.OtherMap => ((ushort)0x1F, (ushort)0x169C),
            Watch.OtherOffset => (Page, (ushort)(Offset + 2)),
            Watch.This => (r13, r12),
            _ => ((ushort)0xFFFF, (ushort)0),
        };
        m.Bus.Poke16(V9Fixture.WPage, page); m.Bus.Poke16(V9Fixture.WOff, offset);
        m.Bus.Poke16(V9Fixture.APage, watch == Watch.LiveCopy ? r13 : (ushort)0xFFFF);
        m.Bus.Poke16(V9Fixture.AOff, watch == Watch.LiveCopy ? r12 : (ushort)0);
        return m;
    }

    private static void Run(Machine m) => V9Fixture.Execute(m, cycles: 200_000);

    private static void EqualCpu(Machine expected, Machine actual, string context)
    {
        for (var r = 0; r < 16; r++)
            Assert.True(expected.Cpu.R(r) == actual.Cpu.R(r), $"{context}: R{r} expected {expected.Cpu.R(r):X4}, actual {actual.Cpu.R(r):X4}");
        Assert.True(expected.Cpu.Psw == actual.Cpu.Psw, $"{context}: PSW expected {expected.Cpu.Psw:X4}, actual {actual.Cpu.Psw:X4}");
        for (var dpp = 0; dpp < 4; dpp++) Assert.Equal(expected.Cpu.Dpp(dpp), actual.Cpu.Dpp(dpp));
        Assert.Equal(expected.Cpu.Sp, actual.Cpu.Sp);
    }

    private static void EqualMemory(Machine expected, Machine actual, string context)
    {
        Assert.True(expected.Bus.ExternalRam.AsSpan().SequenceEqual(actual.Bus.ExternalRam), $"{context}: external RAM differs");
        Assert.True(expected.Bus.Xram.AsSpan().SequenceEqual(actual.Bus.Xram), $"{context}: XRAM differs");
        for (var a = 0xF600u; a < 0xFE00u; a += 2)
            Assert.True(expected.Bus.Peek16(a) == actual.Bus.Peek16(a), $"{context}: {a:X4} differs");
    }

    public static IEnumerable<object[]> NotWatched() =>
        from reader in Enumerable.Range(0, 8)
        from watch in new[] { Watch.None, Watch.OtherMap, Watch.OtherOffset }
        select new object[] { reader, watch };

    [Theory, MemberData(nameof(NotWatched))]
    public void EachReaderIsStockWhenItIsNotTheWatchedMap(int index, Watch watch)
    {
        var reader = Readers[index];
        for (var sample = 0; sample < Indices.Length; sample++)
        {
            var expected = Seed(TestBins.CarBytes, reader, sample, watch); Run(expected);
            var actual = Seed(V9Fixture.Bytes, reader, sample, watch); Run(actual);
            EqualCpu(expected, actual, $"reader {index}, {watch}, sample {sample}");
            EqualMemory(expected, actual, $"reader {index}, {watch}, sample {sample}");
        }
    }

    [Theory, MemberData(nameof(AllReaders))]
    public void EachReaderRecordsTheWatchedMapsIndices(int index)
    {
        var reader = Readers[index];
        var expected = Seed(TestBins.CarBytes, reader, 0, Watch.This); Run(expected);
        var actual = Seed(V9Fixture.Bytes, reader, 0, Watch.This); Run(actual);
        Assert.Equal(reader.Map ? 1 : 2, V9Fixture.Byte(actual, V9Fixture.Kind));
        Assert.Equal(3, actual.Bus.Peek16(V9Fixture.Row));
        Assert.Equal(0x8000, actual.Bus.Peek16(V9Fixture.RowF));
        Assert.Equal(reader.Map ? 5 : 0, actual.Bus.Peek16(V9Fixture.Col));
        Assert.Equal(reader.Map ? 0x4000 : 0, actual.Bus.Peek16(V9Fixture.ColF));
        Assert.Equal(reader.Map ? 12 : 0, V9Fixture.Byte(actual, V9Fixture.Cols));
        Assert.Equal(1, V9Fixture.Byte(actual, V9Fixture.Count));
        EqualCpu(expected, actual, $"reader {index}");

        var wrap = Seed(V9Fixture.Bytes, reader, 0, Watch.This);
        V9Fixture.PutByte(wrap, V9Fixture.Count, 255); Run(wrap);
        Assert.Equal(0, V9Fixture.Byte(wrap, V9Fixture.Count));
    }

    public static IEnumerable<object[]> AllReaders() => Enumerable.Range(0, 8).Select(i => new object[] { i });

    [Theory, MemberData(nameof(AllReaders))]
    public void TheLiveCopyAddressAlsoMatches(int index)
    {
        var reader = Readers[index];
        var expected = Seed(TestBins.CarBytes, reader, 0, Watch.LiveCopy, 0x10, 0x1D40); Run(expected);
        var actual = Seed(V9Fixture.Bytes, reader, 0, Watch.LiveCopy, 0x10, 0x1D40); Run(actual);
        Assert.Equal(reader.Map ? 1 : 2, V9Fixture.Byte(actual, V9Fixture.Kind));
        Assert.Equal(1, V9Fixture.Byte(actual, V9Fixture.Count));
        EqualCpu(expected, actual, $"reader {index}");
    }

    [Theory, MemberData(nameof(AllReaders))] // RF2
    public void RecordWritesRunWithInterruptsHeldOff(int index)
    {
        var reader = Readers[index];
        var m = Seed(V9Fixture.Bytes, reader, 0, Watch.This);
        var start = (uint)V9Fixture.Symbol($"wr{index}_rec"); var end = (uint)V9Fixture.Symbol($"wr{index}_end");
        var seen = 0;
        for (var at = start; at < end; at += 2)
            m.OnInstruction(at, x => { seen++; Assert.True((x.Cpu.Psw & 0x0800) == 0, $"IEN set at {x.Cpu.Address:X5}"); });
        Run(m);
        Assert.True(seen > 5, $"only {seen} instructions seen");
        Assert.Equal(0x0800, m.Cpu.Psw & 0x0800);
        Assert.Equal(1, V9Fixture.Byte(m, V9Fixture.Count));
    }
}
