using System.Buffers.Binary;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

public sealed class OverlayV6MutationTests
{
    private static byte[] Image()
    {
        var bytes = TestBins.CarBytes;
        foreach (var (at, data) in LiveOverlayBin.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot,
                     "sim/tests/Ms43Sim.Tests/fixtures/live_overlay_v6.hex")))) data.CopyTo(bytes, at);
        return bytes;
    }

    private static byte[] Flash(int id) => TestBins.CarBytes.AsSpan(0x7132C + (id - 124) * 8, 8).ToArray();

    private static Machine Start()
    {
        var bytes = Image(); bytes.AsSpan(0x1FC8A, 4).CopyTo(bytes.AsSpan(0x41000));
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

    [Theory]
    [InlineData(124, 0)] [InlineData(125, 704)] [InlineData(126, 0)]
    public void WholeGroupsCanBeWrittenEnabledPatchedRetriedAndReleased(int id, int offset)
    {
        var m = Start(); var baseline = Flash(id); var changed = baseline.ToArray();
        changed[0] = 1; changed[1] = 44; changed[2] = 129;
        Assert.Equal(0xA0, Mutate(m, V5Fixture.Load(id, offset))[0]);
        Assert.Equal(924, Free(m));
        Assert.Equal(0xA0, Mutate(m, Write(id, 0, changed))[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(id, LiveOverlay.Crc32(changed)))[0]);
        changed[0] = 0; changed[3] ^= 1; changed[4] ^= 1;
        var before = Revision(m); var patch = V5Fixture.Patch(id, 0, changed, before);
        Assert.Equal(0xA0, Command(m, patch)[0]); Assert.Equal(before + 1, Revision(m));
        Assert.Equal(0xA0, Command(m, patch)[0]); Assert.Equal(before + 1, Revision(m));
        Assert.Equal(changed, V5Fixture.Read(m, V5Fixture.Physical(offset), 8));
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOff())[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOn())[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Off(id))[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Free(id))[0]); Assert.Equal(932, Free(m));
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.Revert())[0]);
    }

    [Theory]
    [InlineData(124, 3)] [InlineData(125, 3)] [InlineData(126, 2)] [InlineData(126, 255)]
    public void InvalidModesAndRalNeighbourAreRejectedByCompiledHandlers(int id, byte invalid)
    {
        var m = Start(); var baseline = Flash(id); var bad = baseline.ToArray();
        if (invalid == 255) bad[7] ^= 1; else bad[0] = invalid;
        Assert.Equal(0xA0, Mutate(m, V5Fixture.Load(id, 704))[0]);
        var revision = Revision(m);
        Assert.Equal(new byte[] { 0xB1, 4 }, Mutate(m, Write(id, 0, bad)));
        Assert.Equal(revision, Revision(m)); Assert.Equal(baseline, V5Fixture.Read(m, 0xE6E0, 8));
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(id, LiveOverlay.Crc32(baseline)))[0]);
        revision = Revision(m);
        Assert.Equal(new byte[] { 0xB1, 4 }, Command(m, V5Fixture.Patch(id, 0, bad, revision)));
        Assert.Equal(revision, Revision(m)); Assert.Equal(baseline, V5Fixture.Read(m, 0xE6E0, 8));

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
        Assert.Equal(0xA0, Mutate(m, V5Fixture.Load(124, 0))[0]);
        foreach (var request in new[] { Write(124, 0, [0]), Write(124, 1, baseline[..7]), Write(124, 0, baseline[..7]) })
        {
            var before = Revision(m);
            Assert.Equal(new byte[] { 0xB1, 4 }, Mutate(m, request));
            Assert.Equal(before, Revision(m)); Assert.Equal(baseline, V5Fixture.Read(m, 0x41D00, 8));
        }
    }

    [Fact]
    public void ThreeFeatureGroupsWorkBeyondTheBaseDirectoryAndRecoverTheTrackingSpace()
    {
        var m = Start();
        var ordinary = V5Fixture.Catalog.Where(e => e.GetProperty("id").GetInt32() >= 5 && e.GetProperty("bytes").GetInt32() == 64)
            .Take(5).Select(e => e.GetProperty("id").GetInt32()).ToArray();
        for (var i = 0; i < ordinary.Length; i++) Assert.Equal(0xA0, Mutate(m, V5Fixture.Load(ordinary[i], 64 * i))[0]);
        for (var i = 0; i < 3; i++)
        {
            var id = 124 + i;
            Assert.Equal(0xA0, Mutate(m, V5Fixture.Load(id, 320 + 8 * i, (byte)(51 - 2 * i)))[0]);
            Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(id, LiveOverlay.Crc32(Flash(id))))[0]);
            var changed = Flash(id); changed[1]++;
            Assert.Equal(0xA0, Command(m, V5Fixture.Patch(id, 0, changed, Revision(m)))[0]);
            Assert.Equal(changed, V5Fixture.Read(m, V5Fixture.Physical(320 + 8 * i), 8));
            Assert.NotEqual(0, m.Bus.Xram[0x7C3] & (1 << (id % 8)));
        }
        Assert.Equal(548, Free(m));
        m.Bus.Poke16(0xE2A6, 300); _ = Command(m, [0x6D, 0]);
        for (var i = 0; i < 3; i++) Assert.Equal(2, m.Bus.Xram[0x7AC - i * 8]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOn())[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.AllOff())[0]);
        for (var id = 124; id < 127; id++) Assert.Equal(0xA0, Mutate(m, LiveOverlay.Free(id))[0]);
        Assert.Equal(612, Free(m)); Assert.Equal(255, m.Bus.Xram[0x6DB]);
    }
}
