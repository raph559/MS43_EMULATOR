using System.Buffers.Binary;
using Ms43Sim.Core;

namespace Ms43Sim.Tests.LiveTuning;

public sealed partial class OverlayCoreTests
{
    private static byte[] Command(Machine m, byte[] payload)
    {
        var frame = new byte[payload.Length + 3]; frame[0] = 0x12; frame[1] = (byte)frame.Length;
        payload.CopyTo(frame, 2); frame[^1] = frame.Aggregate((byte)0, (a, b) => (byte)(a ^ b));
        for (var i = 0; i < frame.Length; i++)
        {
            var address = 0xF6CDu + (uint)i; var word = m.Bus.Peek16(address & ~1u); var shift = (int)(address & 1) * 8;
            m.Bus.Poke16(address & ~1u, (ushort)((word & ~(255 << shift)) | frame[i] << shift));
        }
        m.Cpu.Jump(0x0C, 0x1000);
        m.Run(2_400_000, x => x.Cpu.Address == 0xC1004);
        Assert.Equal(0xC1004u, m.Cpu.Address);
        var length = m.Bus.Peek16(0xF74E) & 255;
        return V10Fixture.Read(m, 0xF74F, length - 3);
    }

    private static (Machine M, int[] Ids, int[] Offsets) TwelveCompiledMaps()
    {
        var image = V10Fixture.Bytes; var m = Direct(image, image.AsSpan(0x1FC8A, 4).ToArray());
        m.Bus.Poke16(0xF900, 2); m.Bus.Poke16(0xFD2C, 0x0400);
        Assert.Equal(0xA0, Command(m, V10Fixture.Open())[0]);
        var ids = V10Fixture.Catalog.Where(e => e.GetProperty("id").GetInt32() >= 5 && e.GetProperty("bytes").GetInt32() == 64)
            .Take(12).Select(e => e.GetProperty("id").GetInt32()).ToArray();
        var offsets = Enumerable.Range(0, 12).Select(i => i < 10 ? i * 64 : 704 + (i - 10) * 64).ToArray();    // ten in pool A, two in pool B
        for (var i = 0; i < ids.Length; i++)
        {
            var descriptor = i < 5 ? (byte)255 : (byte)((208 - 8 * (i - 4)) / 4);
            var reply = Command(m, V10Fixture.Mutate(V10Fixture.Load(ids[i], offsets[i], descriptor), (uint)(2 * i)));
            Assert.True(reply[0] == 0xA0, $"Load {i}: {Convert.ToHexString(reply)}");
            Assert.Equal(0xA0, Command(m, V10Fixture.Mutate(LiveOverlay.On(ids[i], LiveOverlay.Crc32(V10Fixture.Flash(ids[i]))), (uint)(2 * i + 1)))[0]);
            Assert.Equal(V10Fixture.Flash(ids[i]), V10Fixture.Read(m, V10Fixture.Physical(offsets[i]), 64));
        }
        return (m, ids, offsets);
    }

    private static uint Revision(Machine m) => BinaryPrimitives.ReadUInt32LittleEndian(m.Bus.Xram.AsSpan(0x7C4));
    private static byte[] Mutate(Machine m, byte[] payload) => Command(m, V10Fixture.Mutate(payload, Revision(m)));
    private static int FreeBytes(Machine m) => BinaryPrimitives.ReadUInt16BigEndian(Command(m, [0x6D, 0]).AsSpan(47, 2));

    [Fact]
    public void TwelveCompiledAllocationsCanBePatchedFreedReusedAndReverted()
    {
        var (m, ids, offsets) = TwelveCompiledMaps();
        var expectedIndex = new byte[20];
        foreach (var id in ids) expectedIndex[id / 8] |= (byte)(1 << (id % 8));
        Assert.Equal(expectedIndex, m.Bus.Xram.AsSpan(0x7B0, 20).ToArray());
        Assert.Equal(52, FreeBytes(m));
        var extra = ids[^1]; var edited = V10Fixture.Flash(extra); edited[0] ^= 0x80;
        var patch = V10Fixture.Patch(extra, 0, edited, Revision(m));
        Assert.Equal(0xA0, Command(m, patch)[0]); Assert.Equal(0xA0, Command(m, patch)[0]);
        Assert.Equal(25u, Revision(m));
        Assert.Equal(edited, V10Fixture.Read(m, V10Fixture.Physical(offsets[^1]), 64));

        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Off(ids[7]))[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Free(ids[7]))[0]);
        Assert.Equal(0, m.Bus.Xram[0x7B0 + ids[7] / 8] & (1 << (ids[7] % 8)));
        Assert.Equal(124, FreeBytes(m));
        Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(ids[7], offsets[7], 46))[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(ids[7], LiveOverlay.Crc32(V10Fixture.Flash(ids[7]))))[0]);
        Assert.Equal(52, FreeBytes(m));
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Off(ids[4]))[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Free(ids[4]))[0]);
        Assert.Equal(116, FreeBytes(m));
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOff())[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOn())[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Revert())[0]);
        Assert.Equal(896, FreeBytes(m)); Assert.Equal(255, m.Bus.Xram[0x6DB]);
        Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(ids[0], 0))[0]);
        Assert.Equal(832, FreeBytes(m));
    }

    [Fact]
    public void CompiledIndexIsReleasedWithLastExtraRecordAndCannotOverwriteMapData()
    {
        var (m, ids, _) = TwelveCompiledMaps();
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOff())[0]);
        foreach (var id in ids.Skip(5)) Assert.Equal(0xA0, Mutate(m, LiveOverlay.Free(id))[0]);
        Assert.Equal(576, FreeBytes(m)); Assert.Equal(255, m.Bus.Xram[0x6DB]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Free(ids[4]))[0]);
        Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(48, 908))[0]);
        var tail = V10Fixture.Read(m, 0xE7AC, 24);
        Assert.Equal(V10Fixture.Flash(48), tail);
        var refused = Mutate(m, V10Fixture.Load(ids[5], 256, 50));
        Assert.Equal(new byte[] { 0xB0, 5 }, refused);
        Assert.Equal(tail, V10Fixture.Read(m, 0xE7AC, 24));
        Assert.Equal(255, m.Bus.Xram[0x6DB]);
    }

    [Fact]
    public void CompiledAllocatorRejectsTrackingDataAndDirectoryOverlapsBeforeWriting()
    {
        var (m, ids, _) = TwelveCompiledMaps();
        var newId = V10Fixture.Catalog.First(e => !ids.Contains(e.GetProperty("id").GetInt32()) && e.GetProperty("bytes").GetInt32() == 64).GetProperty("id").GetInt32();
        var cases = new[]
        {
            V10Fixture.Load(newId, 768, 56),
            V10Fixture.Load(newId, 770, 40),
            V10Fixture.Load(newId, 768, 20),
            V10Fixture.Load(newId, 768, 0),
            V10Fixture.Load(newId, 0, 30),
            V10Fixture.Load(newId, 768, 40),
            V10Fixture.Load(newId, 768, 39),
            V10Fixture.Load(newId, 680, 30),
        };
        foreach (var command in cases)
        {
            var before = m.Bus.Xram.AsSpan(0x6B0, 280).ToArray();
            var maps = m.Bus.ExternalRam.AsSpan(0x1D00, 704).ToArray();
            var reply = Mutate(m, command);
            Assert.True(reply[0] is 0xB0 or 0xB1, Convert.ToHexString(command));
            Assert.Equal(before, m.Bus.Xram.AsSpan(0x6B0, 280).ToArray());
            Assert.Equal(maps, m.Bus.ExternalRam.AsSpan(0x1D00, 704).ToArray());
        }
    }

    [Fact]
    public void CompiledSilenceDisablesEveryOverflowMapAndAdvancesSnapshotEpochOnce()
    {
        var (m, ids, _) = TwelveCompiledMaps();
        m.Bus.Poke16(0xE2A6, 300);
        var status = Command(m, [0x6D, 0]);
        Assert.Equal(1, status[10]);
        Assert.Equal(24u, Revision(m));
        for (var i = 0; i < ids.Length; i++)
        {
            var at = i < 5 ? 0x6B8 + i * 8 : 0x6E0 + 208 - 8 * (i - 4);
            Assert.Equal(2, m.Bus.Xram[at]);
        }
        Assert.Equal(1, Command(m, [0x6D, 0])[10]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOn())[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOff())[0]);
        Assert.Equal(2, Command(m, [0x6D, 0])[10]);
    }

    [Fact]
    public void CompiledDirectoryTraversalIsBoundedEvenWithInvalidLinks()
    {
        var address = V10Fixture.Symbol("entry_addr");
        var m = Direct(V10Fixture.Bytes, [0xCA, 0, (byte)address, (byte)(address >> 8)]);
        // Fill every possible record position to bound traversal, independent of map-data capacity.
        for (var i = 0; i < 5; i++) m.Bus.Poke16((uint)(0xE6B8 + 8 * i), (ushort)(3 | i << 8));
        m.Bus.Xram[0x6DB] = 0;
        for (var i = 0; i < 26; i++)
        {
            m.Bus.Poke16((uint)(0xE6E0 + 8 * i), (ushort)(3 | (i + 5) << 8));
            m.Bus.Xram[0x6E3 + 8 * i] = i == 25 ? (byte)255 : (byte)(2 * (i + 1));
        }
        m.Cpu.SetR(2, 30); Execute(m); Assert.Equal(0xE7A8, m.Cpu.R(3));
        var visits = 0;
        m.OnInstruction((uint)V10Fixture.Symbol("ea_loop"), _ => visits++);
        m.Cpu.SetR(2, 123); var start = m.Cycles; Execute(m);
        Assert.Equal(0, m.Cpu.R(3));
        Assert.Equal(31, visits);
        output.WriteLine($"Command lookup, synthetic 31-record miss: {m.Cycles - start} cycles ({(m.Cycles - start) / 24.0:F2} us), without interrupts.");
        m.Bus.Poke16(0xE6B0, 0xC0DA);
        Array.Fill(m.Bus.Xram, (byte)255, 0x7B0, 20);
        m.Cpu.SetR(2, 123);
        m.Cpu.Jump(0x0C, (ushort)V10Fixture.Symbol("subst"));
        start = m.Cycles;
        m.Run(100_000, x => x.Cpu.Address == (uint)V10Fixture.Symbol("to_reader"));
        Assert.Equal((uint)V10Fixture.Symbol("to_reader"), m.Cpu.Address);
        output.WriteLine($"Runtime lookup, synthetic 31-record miss: {m.Cycles - start} cycles ({(m.Cycles - start) / 24.0:F2} us), without interrupts.");
        foreach (var invalid in new byte[] { 0, 1, 56, 254 })
        {
            m.Bus.Xram[0x6E3] = invalid;
            m.Cpu.SetR(2, 30); Execute(m); Assert.Equal(0, m.Cpu.R(3));
        }
        m.Bus.Xram[0x6DB] = 56;
        m.Cpu.SetR(2, 30); Execute(m); Assert.Equal(0, m.Cpu.R(3));
    }
}
