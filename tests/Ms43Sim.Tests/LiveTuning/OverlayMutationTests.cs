using System.Buffers.Binary;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

public sealed class OverlayMutationTests
{
    private static byte[] Flash(int id) => TestBins.CarBytes.AsSpan(0x7132C + (id - 124) * 8, 8).ToArray();

    private static Machine Start() => V10Fixture.Start();
    private static byte[] Command(Machine m, byte[] payload) => V10Fixture.Command(m, payload);
    private static uint Revision(Machine m) => V10Fixture.Revision(m);
    private static byte[] Mutate(Machine m, byte[] payload) => V10Fixture.Mutate(m, payload);
    private static int Free(Machine m) => V10Fixture.Free(m);
    private static byte[] Write(int id, int at, byte[] bytes) => [0x6D, 2, (byte)id, (byte)(at >> 8), (byte)at, .. bytes];

    [Theory]
    [InlineData(124, 0)] [InlineData(125, 704)] [InlineData(126, 0)]
    public void WholeGroupsCanBeWrittenEnabledPatchedRetriedAndReleased(int id, int offset)
    {
        var m = Start(); var baseline = Flash(id); var changed = baseline.ToArray();
        changed[0] = 1; changed[1] = 44; changed[2] = 129;
        Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(id, offset))[0]);
        Assert.Equal(888, Free(m));
        Assert.Equal(0xA0, Mutate(m, Write(id, 0, changed))[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(id, LiveOverlay.Crc32(changed)))[0]);
        changed[0] = 0; changed[3] ^= 1; changed[4] ^= 1;
        var before = Revision(m); var patch = V10Fixture.Patch(id, 0, changed, before);
        Assert.Equal(0xA0, Command(m, patch)[0]); Assert.Equal(before + 1, Revision(m));
        Assert.Equal(0xA0, Command(m, patch)[0]); Assert.Equal(before + 1, Revision(m));
        Assert.Equal(changed, V10Fixture.Read(m, V10Fixture.Physical(offset), 8));
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOff())[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOn())[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Off(id))[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Free(id))[0]); Assert.Equal(896, Free(m));
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Revert())[0]);
    }

    [Theory]
    [InlineData(124, 3)] [InlineData(125, 3)] [InlineData(126, 2)] [InlineData(126, 255)]
    public void InvalidModesAndRalNeighbourAreRejectedByCompiledHandlers(int id, byte invalid)
    {
        var m = Start(); var baseline = Flash(id); var bad = baseline.ToArray();
        if (invalid == 255) bad[7] ^= 1; else bad[0] = invalid;
        Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(id, 704))[0]);
        var revision = Revision(m);
        Assert.Equal(new byte[] { 0xB1, 4 }, Mutate(m, Write(id, 0, bad)));
        Assert.Equal(revision, Revision(m)); Assert.Equal(baseline, V10Fixture.Read(m, 0xE6E0, 8));
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(id, LiveOverlay.Crc32(baseline)))[0]);
        revision = Revision(m);
        Assert.Equal(new byte[] { 0xB1, 4 }, Command(m, V10Fixture.Patch(id, 0, bad, revision)));
        Assert.Equal(revision, Revision(m)); Assert.Equal(baseline, V10Fixture.Read(m, 0xE6E0, 8));

        // Even a matching CRC cannot activate invalid data after corruption outside the protocol.
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Off(id))[0]);
        bad.CopyTo(m.Bus.Xram, 0x6E0);
        BinaryPrimitives.WriteUInt32LittleEndian(m.Bus.Xram.AsSpan(0x6BC), LiveOverlay.Crc32(bad));
        revision = Revision(m);
        Assert.Equal(new byte[] { 0xB1, 4 }, Mutate(m, LiveOverlay.On(id, LiveOverlay.Crc32(bad))));
        Assert.Equal(new byte[] { 0xB1, 4 }, Mutate(m, LiveOverlay.AllOn()));
        Assert.Equal(2, m.Bus.Xram[0x6B8]); Assert.Equal(revision, Revision(m));
    }

    [Fact]
    public void PartialScalarWritesAreRefusedWithoutTouchingTheirCopy()
    {
        var m = Start(); var baseline = Flash(124);
        Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(124, 0))[0]);
        foreach (var request in new[] { Write(124, 0, [0]), Write(124, 1, baseline[..7]), Write(124, 0, baseline[..7]) })
        {
            var before = Revision(m);
            Assert.Equal(new byte[] { 0xB1, 4 }, Mutate(m, request));
            Assert.Equal(before, Revision(m)); Assert.Equal(baseline, V10Fixture.Read(m, 0x41D00, 8));
        }
    }

    [Fact]
    public void ThreeFeatureGroupsWorkBeyondTheBaseDirectoryAndRecoverTheTrackingSpace()
    {
        var m = Start();
        var ordinary = V10Fixture.Catalog.Where(e => e.GetProperty("id").GetInt32() >= 5 && e.GetProperty("bytes").GetInt32() == 64)
            .Take(5).Select(e => e.GetProperty("id").GetInt32()).ToArray();
        for (var i = 0; i < ordinary.Length; i++) Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(ordinary[i], 64 * i))[0]);
        for (var i = 0; i < 3; i++)
        {
            var id = 124 + i;
            Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(id, 320 + 8 * i, (byte)(50 - 2 * i)))[0]);
            Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(id, LiveOverlay.Crc32(Flash(id))))[0]);
            var changed = Flash(id); changed[1]++;
            Assert.Equal(0xA0, Command(m, V10Fixture.Patch(id, 0, changed, Revision(m)))[0]);
            Assert.Equal(changed, V10Fixture.Read(m, V10Fixture.Physical(320 + 8 * i), 8));
            Assert.NotEqual(0, m.Bus.Xram[0x7BF] & (1 << (id % 8)));
        }
        Assert.Equal(508, Free(m));
        m.Bus.Poke16(0xE2A6, 300); _ = Command(m, [0x6D, 0]);
        for (var i = 0; i < 3; i++) Assert.Equal(2, m.Bus.Xram[0x7A8 - i * 8]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOn())[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOff())[0]);
        for (var id = 124; id < 127; id++) Assert.Equal(0xA0, Mutate(m, LiveOverlay.Free(id))[0]);
        Assert.Equal(576, Free(m)); Assert.Equal(255, m.Bus.Xram[0x6DB]);
    }

    [Fact]
    public void WordMapsAcceptFullWordRangeAndTailPatchesWithoutScalarRestrictions()
    {
        foreach (var map in WordMapFixture.Maps)
        {
            var id = map.GetProperty("id").GetInt32(); var bytes = map.GetProperty("bytes").GetInt32();
            var address = map.GetProperty("address").GetInt32(); var offset = bytes <= 200 ? 704 : 0;
            var m = Start(); var changed = TestBins.CarBytes.AsSpan(address, bytes).ToArray();
            Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(id, offset))[0]);
            BinaryPrimitives.WriteUInt16LittleEndian(changed, 0xFEDC);
            Assert.Equal(0xA0, Mutate(m, Write(id, 0, changed.AsSpan(0, 2).ToArray()))[0]);
            Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(id, LiveOverlay.Crc32(changed)))[0]);
            BinaryPrimitives.WriteUInt16LittleEndian(changed.AsSpan(bytes-2), 0xABCD);
            var revision = Revision(m); var patch = V10Fixture.Patch(id, bytes-8, changed.AsSpan(bytes-8).ToArray(), revision);
            Assert.Equal(0xA0, Command(m, patch)[0]); Assert.Equal(revision+1, Revision(m));
            Assert.Equal(0xA0, Command(m, patch)[0]); Assert.Equal(revision+1, Revision(m));
            Assert.Equal(changed, V10Fixture.Read(m, V10Fixture.Physical(offset), bytes));
            Assert.Equal(896-bytes, Free(m));
            Assert.Equal(0xA0, Mutate(m, LiveOverlay.Off(id))[0]);
            Assert.Equal(0xA0, Mutate(m, LiveOverlay.Free(id))[0]); Assert.Equal(896, Free(m));
        }
    }
}
