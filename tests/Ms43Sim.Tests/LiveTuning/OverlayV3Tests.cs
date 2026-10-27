using System.Buffers.Binary;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using Xunit.Abstractions;

namespace Ms43Sim.Tests.LiveTuning;

public static class V3Fixture
{
    public const uint Token = 0x7351AB29;
    public static int CommitFileOffset => Bytes.AsSpan().IndexOf(Convert.FromHexString("DC7EB84DC45D0200C46D0400C47D0600"));
    public static uint CommittedAddress => (uint)(0x80000 + CommitFileOffset + 16);
    public static readonly int[] Addresses = { 0x41D00, 0xE6E0, 0x41E40, 0xE704, 0x41F00 };
    public static byte[] Bytes
    {
        get
        {
            var image = TestBins.CarBytes;
            var path = Path.Combine(LiveOverlayBin.RepositoryRoot, "sim/tests/Ms43Sim.Tests/fixtures/live_overlay_v3.hex");
            foreach (var (offset, data) in LiveOverlayBin.Parse(File.ReadAllText(path))) data.CopyTo(image, offset);
            return image;
        }
    }
    public static FlashedImage Image => TestImages.AsFlashed(Bytes);
    public static byte[] Flash(int table) => TestBins.CarBytes.AsSpan(LiveOverlay.Tables[table].FileOffset, LiveOverlay.Tables[table].Bytes).ToArray();
    public static void Put(byte[] p, int at, uint n) => BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(at), n);
    public static uint Get(byte[] p, int at) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(at));
    public static string Send(byte[] p) => LiveOverlay.KLineSend(p) + "\n";
    public static byte[] Open(uint token = Token) { var p = new byte[6] { 0x6D, 10, 0, 0, 0, 0 }; Put(p, 2, token); return p; }
    public static byte[] Mutate(byte[] p, uint revision, uint token = Token)
    {
        var b = new byte[p.Length + 8]; p.AsSpan(0, 2).CopyTo(b); Put(b, 2, token); Put(b, 6, revision); p.AsSpan(2).CopyTo(b.AsSpan(10)); return b;
    }
    public static byte[] Patch(int table, int at, byte[] before, byte[] after, uint revision, uint token = Token)
    {
        var p = new byte[34]; p[0] = 0x6D; p[1] = 9; Put(p, 2, token); Put(p, 6, revision); p[10] = (byte)table;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(12), (ushort)at);
        Put(p, 14, LiveOverlay.Crc32(before)); Put(p, 18, LiveOverlay.Crc32(after)); after.AsSpan(Math.Clamp(at, 0, after.Length - 8), 8).CopyTo(p.AsSpan(22));
        Put(p, 30, LiveOverlay.Crc32(p.AsSpan(2, 28))); return p;
    }
    public static string Head => "drive live-v3\n" + OverlayRuns.Warm + "3 on\nkline every 1 6D 00\n1.5 crank rpm=200\n3 idle\n" + Send(Open());
    public static string AllLoaded()
    {
        var text = Head; uint rev = 0;
        for (var i = 0; i < 5; i++)
        {
            text += Send(Mutate(LiveOverlay.Load(i, Addresses[i] & 0x3FFF), rev++));
            text += Send(Mutate(LiveOverlay.On(i, LiveOverlay.Crc32(Flash(i))), rev++));
        }
        return text + "1 idle\n";
    }
    public static byte[] Copy(Machine m, int table) => table is 1 or 3
        ? m.Bus.Xram.AsSpan(Addresses[table] - 0xE000, LiveOverlay.Tables[table].Bytes).ToArray()
        : m.Bus.ExternalRam.AsSpan(Addresses[table] & 0x1FFF, LiveOverlay.Tables[table].Bytes).ToArray();
}

public sealed class OverlayV3Tests(ITestOutputHelper output)
{
    [Fact]
    public void AllFiveMapsStayLive_AndEightCellsCommitWithoutMixedReads()
    {
        var original = V3Fixture.Flash(1); var edited = original.ToArray();
        for (var i = 0; i < 8; i++) edited[i] = (byte)(edited[i] + 1);
        var p = V3Fixture.Patch(1, 0, original, edited, 10);
        var text = V3Fixture.AllLoaded() + V3Fixture.Send(p) + V3Fixture.Send(p) + "2 idle\n6 drive gear=3 speed=30..90 pedal=35\n";
        var reads = new List<(int Table, ushort Page, byte[] Bytes)>();
        var windows = new List<byte[]>();
        var commits = new List<ulong>(); ulong? started = null;
        void Attach(DriveContext context) => context.PowerCycleStarted += (m, _) =>
        {
            m.OnInstruction(0x0D029A, machine =>
            {
                var back = (uint)machine.Bus.Peek16((uint)(machine.Cpu.Sp+2)) << 16 | machine.Bus.Peek16(machine.Cpu.Sp);
                var table = LiveOverlay.Tables.FirstOrDefault(t => t.Sites.Any(s => back == 0x80000 + s + 4));
                if (table is null || machine.Bus.Peek16(0xE7C4) < 10) return;
                reads.Add((table.Index, machine.Cpu.R(13), V3Fixture.Copy(machine, table.Index)));
            });
            m.Bus.DataWatch = (address, write) =>
            {
                if (!write || m.Cpu.LastStepWasEntry || m.Cpu.InstructionAddress is < 0xCB544 or >= 0xCC000) return;
                var allocated = address is >= 0xE6B0 and <= 0xE7C7 or >= 0x41D00 and <= 0x41FDF;
                var reply = address is >= 0xF74E and < 0xF78A;
                var stack = address < 0x10000 && (Math.Abs((int)address - m.Cpu.R(0)) <= 16 || Math.Abs((int)address - m.Cpu.Sp) <= 8);
                Assert.True(allocated || reply || stack, $"Overlay write outside its allocation/reply/stacks: {address:X6}");
                if (address is >= 0xE6E0 and < 0xE6E8 && m.Bus.Peek16(0xE7C4) == 10)
                {
                    Assert.True(m.Cpu.InterruptsLocked);
                    started ??= m.Cycles;
                    if (address == 0xE6E6) { commits.Add(m.Cycles-started.Value); started = null; }
                }
            };
            context.PowerCycleEnded += (ended, _) => { if (ReferenceEquals(m, ended)) windows.Add(m.Bus.Xram.ToArray()); };
        };
        var r = DriveRun.RunWithChecks(V3Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        foreach (var exchange in r.KLine.Where(e => e.Request.Count > 3 && e.Request[3] != 0))
            Assert.True(exchange.Reply is { Count: 8 } && exchange.Reply[2] == 0xA0,
                Convert.ToHexString(exchange.Request.ToArray()) + " => " + (exchange.Reply is null ? "none" : Convert.ToHexString(exchange.Reply.ToArray())));
        Assert.Single(commits);
        Assert.InRange(commits[0], 1UL, 240UL);
        output.WriteLine($"Four-word commit first-to-last store: {commits[0]} cycles ({commits[0]/24.0:F3} us).");
        Assert.NotEmpty(reads);
        foreach (var read in reads)
        {
            Assert.Equal(read.Table is 1 or 3 ? 3 : 16, read.Page);
            var expected = V3Fixture.Flash(read.Table);
            Assert.True(read.Bytes.SequenceEqual(expected) || (read.Table == 1 && read.Bytes.SequenceEqual(edited)));
        }
        Assert.Equal(edited, windows.Single().AsSpan(0x6E0, 36).ToArray());
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(windows.Single().AsSpan(0x7C4)));
        Assert.Equal((0,0), (r.WatchdogResets, r.SoftwareResets));
        Assert.Equal(RunVerdict.Pass, r.Verdict);
    }

    [Fact]
    public void BadPacketsStaleRevisionsAndWrongSessionsDoNotWriteActiveMaps()
    {
        var before = V3Fixture.Flash(0); var next = before.ToArray(); next[0]++;
        var good = V3Fixture.Patch(0, 0, before, next, 10);
        var bad = good.ToArray(); bad[22] ^= 1;
        var wrongCrc = good.ToArray(); wrongCrc[18] ^= 1; V3Fixture.Put(wrongCrc, 30, LiveOverlay.Crc32(wrongCrc.AsSpan(2,28)));
        var text = V3Fixture.AllLoaded() + V3Fixture.Send(bad) + V3Fixture.Send(wrongCrc)
            + V3Fixture.Send(V3Fixture.Patch(0, 1, before, next, 10))
            + V3Fixture.Send(V3Fixture.Patch(0, 0, before, next, 9))
            + V3Fixture.Send(V3Fixture.Patch(0, 0, before, next, 10, 1234)) + "2 idle\n";
        byte[]? final = null; uint revision = 0;
        void Attach(DriveContext c) => c.PowerCycleEnded += (m, _) => { final = V3Fixture.Copy(m,0); revision = BinaryPrimitives.ReadUInt32LittleEndian(m.Bus.Xram.AsSpan(0x7C4)); };
        var r = DriveRun.RunWithChecks(V3Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        var patches = r.KLine.Where(e => e.Request[3] == 9).ToArray();
        Assert.Equal(new byte[] { 9,9,4,9,12 }, patches.Select(e => e.Reply![3]).ToArray());
        Assert.Equal(new byte[] { 0xB1,0xB1,0xB0,0xB1,0xB1 }, patches.Select(e => e.Reply![2]).ToArray());
        Assert.Equal(before, final); Assert.Equal(10u, revision);
    }
}
