using System.Buffers.Binary;
using System.Text.Json;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using Xunit.Abstractions;

namespace Ms43Sim.Tests.LiveTuning;

public sealed partial class OverlayCoreTests(ITestOutputHelper output)
{
    private static Machine Direct(byte[] image, byte[] call)
    {
        call.CopyTo(image, 0x41000);
        var m = Machine.Create(FlashImageBuilder.Raw(FirmwareImage.FromBytes(image)));
        m.PowerOn(); m.Bus.Xram[0x6DB] = 255; m.Watchdog.Disable(); m.Bus.XperipheralsEnabled = true;
        m.Sfrs.Write16(0xFE1A, 0x0401); m.Sfrs.Write16(0xFF16, 0x04AF);
        m.Sfrs.Write16(0xFE00, 0x1C); m.Sfrs.Write16(0xFE02, 0x3B); m.Sfrs.Write16(0xFE04, 0x10); m.Sfrs.Write16(0xFE06, 3);
        m.Cpu.SetR(0, 0xF780); return m;
    }

    private static void Execute(Machine m)
    {
        m.Cpu.Jump(0x0C, 0x1000); m.Run(100_000, x => x.Cpu.Address == 0xC1004);
        Assert.Equal(0xC1004u, m.Cpu.Address);
    }

    [Theory]
    [InlineData(3, false)] [InlineData(16, false)] [InlineData(255, false)]
    [InlineData(3, true)] [InlineData(16, true)] [InlineData(255, true)]
    public void CompiledMapAndLimiterStoresDeferInterruptsAndPec(int destination, bool pec)
    {
        var hard = destination == 255;
        var bytes = V10Fixture.Bytes;
        var start = hard ? V10Fixture.Symbol("hard_commit") & 0x7FFFF
            : bytes.AsSpan().IndexOf(Convert.FromHexString("DC7EB84DC45D0200C46D0400C47D0600"));
        Assert.True(start >= 0);
        var code = bytes.AsSpan(start, hard ? 20 : 16).ToArray();
        var page = hard ? 16 : destination; var offset = hard ? 0x1FD8 : page == 3 ? 0x26E0 : 0x1D00;
        var t = TestMachine.Create(Convert.ToHexString(code) + " CC00", (0x84, "FB88"))
            .SetR(14, (ushort)page).SetR(13, (ushort)offset).SetR(4, 0x1020).SetR(5, 0x3040).SetR(6, 0x5060).SetR(7, 0x7080)
            .WriteSfr(0xFE1A, 0x0401).WriteSfr(0xFF16, 0x04AF);
        t.M.Bus.XperipheralsEnabled = true;
        if (pec) t.WriteSfr(0xFEC0, 0x00FF).Poke16(0xFCE0, 0xE704).Poke16(0xFCE2, 0xF900).WriteSfr(0xFF98, 0x0078);
        else t.WriteSfr(0xFF9E, 0x004C);
        t.Psw = 0x0800; t.Step();
        t.M.Interrupts.Request(InterruptSources.ByName(pec ? "ADC" : "T1")); t.Step(4);
        Assert.Equal(0UL, t.M.Entries); Assert.Equal(0x1000 + code.Length, t.Cpu.IP);
        for (var i = 0; i < 4; i++) Assert.Equal(0x1020 + 0x2020 * i, t.Peek16((uint)(page * 0x4000 + offset + 2 * i)));
        t.Step(); Assert.Equal(1UL, t.M.Entries);
        if (!pec) Assert.Equal(0x84, t.Cpu.IP);
    }

    [Fact]
    public void AllSixGearThresholdsAndHysteresisUseRamOnlyWhileEnabledAndFresh()
    {
        foreach (var state in new[] { "uninitialised", "disabled", "live", "silent" })
        for (var gear = 0; gear < 6; gear++)
        {
            var m = Direct(V10Fixture.Bytes, V10Fixture.Bytes.AsSpan(0x58C88, 4).ToArray());
            m.Bus.Poke16(0xE6B0, state == "uninitialised" ? (ushort)0 : (ushort)0xC0DA);
            m.Bus.Poke16(0xF900, 2); m.Bus.Poke16(0xFD2C, 0x0400); m.Bus.Poke16(0xFB6A, (ushort)(gear << 8));
            m.Bus.Poke16(0xE2A6, state == "silent" ? (ushort)300 : (ushort)0);
            for (var i = 0; i < 6; i++) m.Bus.ExternalRam[0x1FD8 + i] = (byte)(180 + i);
            m.Bus.ExternalRam[0x1FDE] = 3; m.Bus.ExternalRam[0x1FDF] = state == "disabled" ? (byte)0x61 : (byte)0xE1;
            m.Cpu.SetR(12, 0x1D47); m.Cpu.SetR(13, 0x1D); Execute(m);
            Assert.Equal(state == "live" ? 180 + gear : V10Fixture.Bytes[0x75D47 + gear], m.Cpu.R(4) & 255);
            var hook = V10Fixture.Bytes.AsSpan(0x58CFC, 4).ToArray();
            // The test trampoline is in flash; use a fresh machine with the same data for the second hook.
            var h = Direct(V10Fixture.Bytes, hook);
            m.Bus.Xram.CopyTo(h.Bus.Xram, 0); m.Bus.Iram.CopyTo(h.Bus.Iram, 0); m.Bus.ExternalRam.CopyTo(h.Bus.ExternalRam, 0);
            h.Cpu.SetR(0, 0xF780); h.Cpu.SetR(4, 0xABCD); Execute(h);
            Assert.Equal(state == "live" ? 3 : V10Fixture.Bytes[0x70BFF], h.Cpu.R(13));
            Assert.Equal(0xABCD, h.Cpu.R(4));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EveryCatalogueHookPreservesTheOriginalReader_AndOnlyRedirectsItsOwnMap(bool overflow)
    {
        var cases = 0;
        foreach (var item in V10Fixture.ByteMaps)
        {
            var id = item.GetProperty("id").GetInt32(); var address = item.GetProperty("address").GetInt32();
            var rows = item.GetProperty("rows").GetInt32(); var cols = item.GetProperty("columns").GetInt32();
            var changed = V10Fixture.Flash(id).Select(b => (byte)(b ^ 0xA5)).ToArray();
            foreach (var site in item.GetProperty("sites").EnumerateArray().Select(e => e.GetInt32()))
            foreach (var live in new[] { false, true })
            {
                var overlay = V10Fixture.Bytes; var reference = TestBins.CarBytes;
                if (live) changed.CopyTo(reference, address);
                var m = Direct(overlay, overlay.AsSpan(site, 4).ToArray());
                var expected = Direct(reference, Convert.FromHexString("DA0D9A02"));
                m.Bus.Poke16(0xE6B0, 0xC0DA); m.Bus.Poke16(0xF900, 2); m.Bus.Poke16(0xFD2C, 0x0400);
                // Last slot exercises the complete search; this map is intentionally moved between RAM pools.
                var offset = changed.Length <= 200 ? 704 : 0;
                var record = overflow ? 0xE7A8u : 0xE6D8u;
                if (overflow)
                {
                    m.Bus.Xram[0x6DB] = 50;
                    m.Bus.Xram[0x7B0 + id / 8] = (byte)(1 << (id % 8));
                }
                m.Bus.Poke16(record, (ushort)((id << 8) | (live ? 3 : 2)));
                m.Bus.Poke16(record + 2, (ushort)(0xFF00 | offset / 4));
                if (offset == 704) changed.CopyTo(m.Bus.Xram, 0x6E0); else changed.CopyTo(m.Bus.ExternalRam, 0x1D00);
                for (var interpolation = 0; interpolation < 4; interpolation++)
                {
                    foreach (var machine in new[] { m, expected })
                    {
                        machine.Cpu.SetR(12, (ushort)(address & 0x3FFF)); machine.Cpu.SetR(13, (ushort)(address / 0x4000));
                        machine.Cpu.SetR(14, 0xBEEF);
                        machine.Bus.Poke16(0xFB6A, (ushort)((rows - 2) << 8));
                        machine.Bus.Poke16(0xFB6C, (ushort)((cols << 8) | (cols - 2)));
                        machine.Bus.Poke16(0xFB6E, 0x80C0); machine.Bus.Poke16(0xE392, (ushort)interpolation);
                        Execute(machine);
                    }
                    Assert.True(m.Cpu.R(4) == expected.Cpu.R(4), $"map {id}, site {site:X}, live {live}, interpolation {interpolation}");
                    Assert.Equal(0xBEEF, m.Cpu.R(14)); cases++;
                }
            }
        }
        output.WriteLine($"{cases} original-reader comparisons across all catalogue hook sites.");
    }

    [Fact]
    public void ProspectiveCrcMatchesAnIndependentFullCrcForEveryCatalogueWindow()
    {
        var address = V10Fixture.Symbol("patch_result_crc");
        var m = Direct(V10Fixture.Bytes, [(byte)0xCA, 0, (byte)address, (byte)(address >> 8)]);
        var random = new Random(401); var cases = 0;
        foreach (var item in V10Fixture.ByteMaps)
        {
            var id = item.GetProperty("id").GetInt32(); var data = new byte[item.GetProperty("bytes").GetInt32()]; random.NextBytes(data);
            data.CopyTo(m.Bus.ExternalRam, 0x1D00);
            m.Bus.Poke16(0xE6B8, (ushort)(id << 8 | 3)); m.Bus.Poke16(0xE6BA, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(m.Bus.Xram.AsSpan(0x6BC), LiveOverlay.Crc32(data));
            for (var at = 0; at <= data.Length - 8; at += 2)
            {
                var next = data.ToArray(); random.NextBytes(next.AsSpan(at, 8));
                V10Fixture.Patch(id, at, next.AsSpan(at, 8).ToArray(), 0).AsSpan(2, 24).CopyTo(m.Bus.ExternalRam.AsSpan(0x1FC0));
                m.Cpu.SetR(2, (ushort)id); m.Cpu.SetR(3, 0xE6B8); Execute(m);
                Assert.Equal(LiveOverlay.Crc32(next), (uint)m.Cpu.R(4) | ((uint)m.Cpu.R(5) << 16)); cases++;
            }
        }
        output.WriteLine($"{cases} independent full CRC comparisons, with arbitrary cell bytes.");
    }

    [Fact]
    public void CutModeWaitsForReleasedFlags_AndFallbackRestoresTheFlashedMode()
    {
        var image = V10Fixture.Bytes;
        var refresh = Direct(image, image.AsSpan(0x58D18, 4).ToArray());
        refresh.Bus.Poke16(0xE6B0, 0xC0DA); refresh.Bus.Poke16(0xF900, 2); refresh.Bus.Poke16(0xFD2C, 0x0400);
        refresh.Bus.ExternalRam[0x1FDF] = 0xE0; // requested fuel cut, currently ignition cut
        refresh.Bus.Poke16(0xFD9A, 0x8000); Execute(refresh);
        Assert.Equal(0xE0, refresh.Bus.ExternalRam[0x1FDF]);
        refresh.Bus.Poke16(0xFD9A, 0); Execute(refresh);
        Assert.Equal(0xA0, refresh.Bus.ExternalRam[0x1FDF]);
        refresh.Bus.Poke16(0xE2A6, 300); Execute(refresh);
        Assert.Equal(0xE0, refresh.Bus.ExternalRam[0x1FDF]);

        var mode = Direct(V10Fixture.Bytes, V10Fixture.Bytes.AsSpan(0x2A3AC, 4).ToArray());
        mode.Bus.Poke16(0xE6B0, 0xC0DA); mode.Bus.ExternalRam[0x1FDF] = 0xA0;
        mode.Cpu.SetR(3, 0x1234); mode.Cpu.SetR(4, 0xABCD); Execute(mode);
        Assert.Equal(0xAB00, mode.Cpu.R(4)); Assert.Equal(0x1234, mode.Cpu.R(3));
        Assert.NotEqual(0, mode.Cpu.Psw & 8); // MOVB zero result must retain the original branch condition.
    }

    [Fact]
    public void FiveTablesAndLimiterFit_UpdatesAreVerifiedAndRetriesDoNotRepeat()
    {
        var before = V10Fixture.Flash(1); var after = before.ToArray(); after[0]++;
        var patch = V10Fixture.Patch(1, 0, after, 10);
        byte[] hard = [192, 192, 192, 192, 192, 192, 2, 0x81];
        var hardPatch = V10Fixture.Patch(255, 0, hard, 11);
        var text = V10Fixture.AllLoaded() + V10Fixture.Send(patch) + V10Fixture.Send(patch)
            + V10Fixture.Send(hardPatch) + V10Fixture.Send(hardPatch) + "2 idle\n6 drive gear=3 speed=30..90 pedal=35\n";
        byte[]? copy = null, hardCopy = null; uint revision = 0; var reads = new int[4];
        void Attach(DriveContext c) => c.PowerCycleStarted += (m, _) =>
        {
            m.OnInstruction(0xD029A, x =>
            {
                var back = (uint)x.Bus.Peek16((uint)x.Cpu.Sp + 2) << 16 | x.Bus.Peek16(x.Cpu.Sp);
                var table = LiveOverlay.Tables.FirstOrDefault(t => t.Sites.Any(s => back == 0x80000 + s + 4));
                if (table is null || table.Index > 3 || x.Bus.Peek16(0xE7C4) < 10) return;
                var expected = V10Fixture.Physical(V10Fixture.Offsets[table.Index]);
                Assert.Equal(expected, (uint)(x.Cpu.R(13) * 0x4000 + x.Cpu.R(12)));
                reads[table.Index]++;
            });
            c.PowerCycleEnded += (x, _) =>
            {
                copy = V10Fixture.Read(x, V10Fixture.Physical(704), 36);
                hardCopy = V10Fixture.Read(x, 0x41FD8, 8); hardCopy[7] &= 0x81;
                revision = BinaryPrimitives.ReadUInt32LittleEndian(x.Bus.Xram.AsSpan(0x7C4));
            };
        };
        var r = DriveRun.RunWithChecks(V10Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: [Attach]));
        foreach (var e in r.KLine.Where(e => e.Request[3] != 0))
            Assert.True(e.Reply is { Count: 8 } && e.Reply[2] == 0xA0, Convert.ToHexString(e.Request.ToArray()) + " => " + (e.Reply is null ? "timeout" : Convert.ToHexString(e.Reply.ToArray())));
        Assert.Equal(after, copy); Assert.Equal(hard, hardCopy); Assert.Equal(12u, revision);
        Assert.All(reads, n => Assert.True(n > 0));
        Assert.Equal((0, 0), (r.WatchdogResets, r.SoftwareResets));
        Assert.True(r.Verdict == RunVerdict.Pass, string.Join("; ", r.Checks.SelectMany(c => c.Findings.Select(f => c.Name + ": " + f.Message))));
        output.WriteLine("Four engine tables read from RAM, limiter group committed, duplicate requests acknowledged once.");
    }

    [Fact]
    public void AllocationFailuresAndCorruptOrStalePatchesLeaveTheActiveMapsUntouched()
    {
        var before = V10Fixture.Flash(1); var edited = before.ToArray(); edited[0]++;
        var corrupt = V10Fixture.Patch(1, 0, edited, 10); corrupt[14] ^= 1;
        var commands = new[]
        {
            V10Fixture.Mutate(V10Fixture.Load(6, 0), 10), // no free allocation slot
            V10Fixture.Mutate(V10Fixture.Load(6, 690), 10), // crosses pool boundary
            V10Fixture.Mutate(V10Fixture.Load(6, 932), 10),
            V10Fixture.Mutate(V10Fixture.Load(6, 1), 10),
            V10Fixture.Patch(1, 0, edited, 10, 1234),
            V10Fixture.Patch(1, 0, edited, 9), // previous revision, but contents do not match
            corrupt,
            V10Fixture.Patch(1, 1, edited, 10),
        };
        var text = V10Fixture.AllLoaded() + string.Concat(commands.Select(V10Fixture.Send)) + "1 idle\n";
        uint revision = 0; byte[]? final = null;
        void Attach(DriveContext c) => c.PowerCycleEnded += (m, _) =>
        {
            revision = BinaryPrimitives.ReadUInt32LittleEndian(m.Bus.Xram.AsSpan(0x7C4));
            final = V10Fixture.Read(m, 0xE6E0, 36);
        };
        var r = DriveRun.Run(V10Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: [Attach]));
        var failures = r.KLine.Where(e => e.Reply is { Count: 5 } && e.Reply[2] is 0xB0 or 0xB1).ToArray();
        Assert.Equal(new byte[] { 5, 4, 4, 4, 12, 9, 9, 4 }, failures.Select(e => e.Reply![3]));
        Assert.Equal(10u, revision); Assert.Equal(before, final);
    }

    [Fact]
    public void PrivateSnapshotSurvivesReceiveBufferReuse_AndSilenceDisablesMapsAndLimiter()
    {
        var after = V10Fixture.Flash(1); after[0]++;
        byte[] hard = [210, 210, 210, 210, 210, 210, 2, 0x80];
        var text = V10Fixture.AllLoaded() + V10Fixture.Send(V10Fixture.Patch(1, 0, after, 10))
            + V10Fixture.Send(V10Fixture.Patch(255, 0, hard, 11)) + "2 idle\nkline quiet\n4 idle\n";
        var changed = false; var observedFuelMode = false; byte[]? final = null; byte[]? states = null; byte flags = 0;
        void Attach(DriveContext c) => c.PowerCycleStarted += (m, _) =>
        {
            m.OnInstruction((uint)V10Fixture.Symbol("content_crc"), x =>
            {
                if (changed || x.Bus.Peek16(0xE7C4) != 10 || (x.Bus.Peek16(0xF6D0) & 255) != 9) return;
                changed = true;
                for (uint a = 0xF6D2; a < 0xF6F0; a += 2) x.Bus.Poke16(a, 0xFFFF);
            });
            m.OnInstruction(0xAA3B0, x =>
            {
                if (x.Bus.Peek16(0xE7C4) == 12 && (x.Bus.ExternalRam[0x1FDF] & 0x80) != 0 && (x.Cpu.R(4) & 255) == 0)
                    observedFuelMode = true;
            });
            c.PowerCycleEnded += (x, _) =>
            {
                final = V10Fixture.Read(x, 0xE6E0, 36);
                states = Enumerable.Range(0, 5).Select(i => x.Bus.Xram[0x6B8 + 8 * i]).ToArray();
                flags = x.Bus.ExternalRam[0x1FDF];
            };
        };
        var r = DriveRun.RunWithChecks(V10Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: [Attach]));
        Assert.True(changed); Assert.True(observedFuelMode); Assert.Equal(after, final);
        Assert.All(states!, state => Assert.Equal(2, state)); Assert.Equal(0, flags & 0x80); Assert.Equal(0x40, flags & 0x40);
        Assert.All(r.KLine.Where(e => e.Request[3] == 9), e => Assert.Equal(0xA0, e.Reply![2]));
        Assert.Equal((0, 0), (r.WatchdogResets, r.SoftwareResets));
    }

    [Fact]
    public void FailedPostCommitReadbackDisablesTheMapAndAdvancesRevision()
    {
        var after = V10Fixture.Flash(1); after[0]++;
        var text = V10Fixture.AllLoaded() + V10Fixture.Send(V10Fixture.Patch(1, 0, after, 10)) + "1 idle\n";
        var injected = false; byte state = 0; uint revision = 0;
        void Attach(DriveContext c) => c.PowerCycleStarted += (m, _) =>
        {
            m.OnInstruction((uint)V10Fixture.Symbol("patch_committed"), x =>
            {
                if (injected) return; injected = true; x.Bus.Xram[0x6E0] ^= 1;
            });
            c.PowerCycleEnded += (x, _) => { state = x.Bus.Xram[0x6C0]; revision = BinaryPrimitives.ReadUInt32LittleEndian(x.Bus.Xram.AsSpan(0x7C4)); };
        };
        var r = DriveRun.Run(V10Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: [Attach]));
        var response = r.KLine.Single(e => e.Request[3] == 9).Reply!;
        Assert.True(injected); Assert.Equal(0xB1, response[2]); Assert.Equal(9, response[3]);
        Assert.Equal(2, state); Assert.Equal(11u, revision);
    }

    [Theory]
    [InlineData(0xFFFFu, true)]
    [InlineData(0xFFFFFFFEu, true)]
    [InlineData(0xFFFFFFFFu, false)]
    public void RevisionCarriesAcrossWordsAndNeverWraps(uint revision, bool accepted)
    {
        var before = V10Fixture.Flash(1); var after = before.ToArray(); after[0]++;
        var text = V10Fixture.AllLoaded() + V10Fixture.Send(V10Fixture.Patch(1, 0, after, revision)) + "1 idle\n";
        var handler = (uint)(0xC0000 + BinaryPrimitives.ReadUInt16LittleEndian(V10Fixture.Bytes.AsSpan(0x1FC8C)));
        uint finalRevision = 0; byte[]? final = null;
        void Attach(DriveContext c) => c.PowerCycleStarted += (m, _) =>
        {
            m.OnInstruction(handler, x =>
            {
                if ((x.Bus.Peek16(0xF6D0) & 255) == 9) BinaryPrimitives.WriteUInt32LittleEndian(x.Bus.Xram.AsSpan(0x7C4), revision);
            });
            c.PowerCycleEnded += (x, _) => { final = V10Fixture.Read(x, 0xE6E0, 36); finalRevision = BinaryPrimitives.ReadUInt32LittleEndian(x.Bus.Xram.AsSpan(0x7C4)); };
        };
        var r = DriveRun.Run(V10Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: [Attach]));
        var reply = r.KLine.Single(e => e.Request[3] == 9).Reply!;
        Assert.Equal(accepted ? 0xA0 : 0xB1, reply[2]);
        Assert.Equal(accepted ? revision + 1 : revision, finalRevision);
        Assert.Equal(accepted ? after : before, final);
    }

    [Fact]
    public void NewSessionKeepsMapsActiveAndRejectsPacketsFromThePreviousSession()
    {
        var before = V10Fixture.Flash(1); var after = before.ToArray(); after[0]++;
        var text = V10Fixture.AllLoaded() + V10Fixture.Send(V10Fixture.Open(1234))
            + V10Fixture.Send(V10Fixture.Patch(1, 0, after, 10))
            + V10Fixture.Send(V10Fixture.Patch(1, 0, after, 0, 1234)) + "1 idle\n";
        byte[]? final = null; byte[]? states = null;
        void Attach(DriveContext c) => c.PowerCycleEnded += (m, _) =>
        {
            final = V10Fixture.Read(m, 0xE6E0, 36); states = Enumerable.Range(0, 5).Select(i => m.Bus.Xram[0x6B8 + i * 8]).ToArray();
        };
        var r = DriveRun.Run(V10Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: [Attach]));
        var replies = r.KLine.Where(e => e.Request[3] == 9).Select(e => e.Reply!).ToArray();
        Assert.Equal(0xB1, replies[0][2]); Assert.Equal(12, replies[0][3]); Assert.Equal(0xA0, replies[1][2]);
        Assert.Equal(after, final); Assert.All(states!, s => Assert.Equal(3, s));
    }
}
