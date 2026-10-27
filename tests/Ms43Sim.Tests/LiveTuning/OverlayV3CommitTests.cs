using System.Buffers.Binary;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using Xunit.Abstractions;

namespace Ms43Sim.Tests.LiveTuning;

public sealed class OverlayV3CommitTests(ITestOutputHelper output)
{
    // The pinned V3 fixture's commit is EXTP R14,#4 followed by four word stores.

    [Theory]
    [InlineData(3, false)]
    [InlineData(16, false)]
    [InlineData(3, true)]
    [InlineData(16, true)]
    public void CompiledCommitDefersPendingInterruptOrPecUntilAllFourStores(int page, bool pec)
    {
        var code = V3Fixture.Bytes.AsSpan(V3Fixture.CommitFileOffset, 16).ToArray();
        Assert.Equal(new byte[] { 0xDC, 0x7E }, code[..2]);
        var offset = page == 3 ? 0x2704 : 0x1D00;
        var t = TestMachine.Create(Convert.ToHexString(code) + " CC00", (0x84, "FB88"))
            .SetR(14, (ushort)page).SetR(13, (ushort)offset).SetR(4, 0x1020).SetR(5, 0x3040).SetR(6, 0x5060).SetR(7, 0x7080)
            .WriteSfr(0xFE1A, 0x0401).WriteSfr(0xFF16, 0x04AF);
        t.M.Bus.XperipheralsEnabled = true;
        if (pec)
            t.WriteSfr(0xFEC0, 0x00FF).Poke16(0xFCE0, 0xE704).Poke16(0xFCE2, 0xF900).WriteSfr(0xFF98, 0x0078);
        else
            t.WriteSfr(0xFF9E, 0x004C);
        t.Psw = 0x0800;
        t.Step();
        t.M.Interrupts.Request(InterruptSources.ByName(pec ? "ADC" : "T1"));
        t.Step(4);
        Assert.Equal(0UL, t.M.Entries);
        Assert.Equal(0x1010, t.Cpu.IP);
        for (var i = 0; i < 4; i++) Assert.Equal(0x1020 + 0x2020 * i, t.Peek16((uint)(page * 0x4000 + offset + 2 * i)));
        Assert.Equal(0x0800, t.Psw & 0x0800);
        t.Step();
        Assert.Equal(1UL, t.M.Entries);
        if (!pec) Assert.Equal(0x84, t.Cpu.IP);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllFiveMapsAcceptLiveGroupsUnderLoad_AndSilenceDisablesThem(bool highRpm)
    {
        var phase = highRpm ? "drive gear=3 rpm=6500 pedal=85" : "idle";
        var text = V3Fixture.AllLoaded() + (highRpm ? "8 drive gear=3 speed=50..150 pedal=95\n" : "") + $"1 {phase}\n";
        var expected = Enumerable.Range(0, 5).Select(V3Fixture.Flash).ToArray();
        for (var i = 0; i < 5; i++)
        {
            var next = expected[i].ToArray();
            for (var k = 0; k < 8; k++) next[k]++;
            text += V3Fixture.Send(V3Fixture.Patch(i, 0, expected[i], next, (uint)(10 + i)));
            expected[i] = next;
        }
        text += $"2 {phase}\nkline quiet\n4 {phase}\n";
        var intervals = new List<ulong>(); var commits = new List<ulong>(); ulong? entered = null; var commitSeen = false;
        var readers = new int[5]; var final = new byte[5][]; var states = new byte[5];
        var bytes = V3Fixture.Bytes;
        var handler = (uint)(0xC0000 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0x1FC8C)));
        void Attach(DriveContext c) => c.PowerCycleStarted += (m, _) =>
        {
            m.OnInstruction(handler, x =>
            {
                if (entered is null && (x.Bus.Peek16(0xF6D0) & 255) == 9) { entered = x.Cycles; commitSeen = false; }
            });
            m.OnInstruction(V3Fixture.CommittedAddress, x =>
            {
                if (entered is not null && !commitSeen) { commits.Add(x.Cycles); commitSeen = true; }
            });
            foreach (var back in new uint[] { 0x9FAD6, 0x9FB04 }) m.OnInstruction(back, x =>
            {
                if (entered is { } start) { intervals.Add(x.Cycles - start); entered = null; }
            });
            m.OnInstruction(0xD029A, x =>
            {
                var back = (uint)x.Bus.Peek16((uint)(x.Cpu.Sp + 2)) << 16 | x.Bus.Peek16(x.Cpu.Sp);
                var table = LiveOverlay.Tables.FirstOrDefault(t => t.Sites.Any(s => back == 0x80000 + s + 4));
                if (table is null || x.Bus.Peek16(0xE7C4) < 10 || x.Bus.Xram[0x6B8 + table.Index * 8] != 3) return;
                var eligible = (ushort)(x.Bus.Peek16(0xE2A6) - x.Bus.Peek16(0xE6B2)) < 300
                    && (x.Bus.Peek16(0xF900) & 255) == 2 && (x.Bus.Peek16(0xFD2C) & 0x1E00) != 0;
                Assert.Equal(eligible ? (table.Index is 1 or 3 ? 3 : 16) : table.Page, x.Cpu.R(13));
                if (eligible) readers[table.Index]++;
            });
            c.PowerCycleEnded += (x, _) =>
            {
                for (var i = 0; i < 5; i++) { final[i] = V3Fixture.Copy(x, i); states[i] = x.Bus.Xram[0x6B8 + i * 8]; }
            };
        };
        var r = DriveRun.RunWithChecks(V3Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        var patches = r.KLine.Where(e => e.Request[2] == 0x6D && e.Request[3] == 9).ToArray();
        Assert.Equal(5, patches.Length); Assert.Equal(5, commits.Count); Assert.Equal(5, intervals.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(0xA0, patches[i].Reply![2]); Assert.Equal(expected[i], final[i]);
            Assert.Equal(2, states[i]);
            Assert.InRange(intervals[i] / 24000.0, 0.001, 12);
            output.WriteLine($"highRpm={highRpm} table={i}: handler {intervals[i]/24000.0:F3} ms including interrupts; first request byte to commit {(commits[i]-patches[i].SentAt)/24000.0:F3} ms at {patches[i].Baud} baud; active reads={readers[i]}");
        }
        Assert.Equal((0, 0), (r.WatchdogResets, r.SoftwareResets));
        Assert.True(r.Verdict == RunVerdict.Pass, string.Join("; ", r.Checks.SelectMany(c => c.Findings.Select(f => c.Name + ": " + f.Message))));
    }

    [Fact]
    public void ValidatedPatchUsesPrivateSnapshotIfReceiveBufferChanges()
    {
        var before = V3Fixture.Flash(1); var after = before.ToArray(); after[0]++;
        var text = V3Fixture.AllLoaded() + V3Fixture.Send(V3Fixture.Patch(1, 0, before, after, 10)) + "1 idle\n";
        var changed = false; byte[]? final = null;
        void Attach(DriveContext c) => c.PowerCycleStarted += (m, _) =>
        {
            m.OnInstruction(0xCBAAA, x =>
            {
                if (changed || x.Bus.Peek16(0xE7C4) != 10 || (x.Bus.Peek16(0xF6D0) & 255) != 9) return;
                changed = true;
                for (uint at = 0xF6D2; at < 0xF6F0; at += 2) x.Bus.Poke16(at, 0xFFFF);
            });
            c.PowerCycleEnded += (x, _) => final = V3Fixture.Copy(x, 1);
        };
        var r = DriveRun.Run(V3Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        Assert.True(changed); Assert.Equal(after, final);
        Assert.Equal(0xA0, r.KLine.Single(e => e.Request[3] == 9).Reply![2]);
    }

    [Theory]
    [InlineData(0xFFFFu, true)]
    [InlineData(0xFFFFFFFEu, true)]
    [InlineData(0xFFFFFFFFu, false)]
    public void RevisionCarriesAcrossWordsAndNeverWraps(uint revision, bool accepted)
    {
        var before = V3Fixture.Flash(1); var after = before.ToArray(); after[0]++;
        var text = V3Fixture.AllLoaded() + V3Fixture.Send(V3Fixture.Patch(1, 0, before, after, revision)) + "1 idle\n";
        var handler = (uint)(0xC0000 + BinaryPrimitives.ReadUInt16LittleEndian(V3Fixture.Bytes.AsSpan(0x1FC8C)));
        uint finalRevision = 0; byte[]? final = null;
        void Attach(DriveContext c) => c.PowerCycleStarted += (m, _) =>
        {
            m.OnInstruction(handler, x =>
            {
                if ((x.Bus.Peek16(0xF6D0) & 255) == 9) BinaryPrimitives.WriteUInt32LittleEndian(x.Bus.Xram.AsSpan(0x7C4), revision);
            });
            c.PowerCycleEnded += (x, _) => { final = V3Fixture.Copy(x, 1); finalRevision = BinaryPrimitives.ReadUInt32LittleEndian(x.Bus.Xram.AsSpan(0x7C4)); };
        };
        var r = DriveRun.Run(V3Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        var reply = r.KLine.Single(e => e.Request[3] == 9).Reply!;
        Assert.Equal(accepted ? 0xA0 : 0xB1, reply[2]);
        Assert.Equal(accepted ? revision + 1 : revision, finalRevision);
        Assert.Equal(accepted ? after : before, final);
    }

    [Fact]
    public void ProspectiveCrcMatchesCompleteTableForEveryEvenWindowAndArbitraryBytes()
    {
        var image = V3Fixture.Bytes;
        // CALLA UC,patch_result_crc in the same segment; stop at its return instruction.
        new byte[] { 0xCA, 0x00, 0x60, 0xBE, 0xCC, 0x00 }.CopyTo(image, 0x41000);
        var m = Machine.Create(FlashImageBuilder.Raw(FirmwareImage.FromBytes(image)));
        m.PowerOn(); m.Watchdog.Disable();
        m.Bus.XperipheralsEnabled = true;
        m.Sfrs.Write16(0xFE1A, 0x0401); m.Sfrs.Write16(0xFF16, 0x04AF);
        var random = new Random(12345); var cases = 0;
        for (var table = 0; table < 5; table++)
        {
            var before = new byte[LiveOverlay.Tables[table].Bytes]; random.NextBytes(before);
            if (table is 1 or 3) before.CopyTo(m.Bus.Xram, V3Fixture.Addresses[table] - 0xE000);
            else before.CopyTo(m.Bus.ExternalRam, V3Fixture.Addresses[table] & 0x1FFF);
            for (var at = 0; at <= before.Length - 8; at += 2)
            {
                var after = before.ToArray(); random.NextBytes(after.AsSpan(at, 8));
                V3Fixture.Patch(table, at, before, after, 0).AsSpan(2, 32).CopyTo(m.Bus.ExternalRam.AsSpan(0x1FC0));
                m.Cpu.SetR(0, 0xF780); m.Cpu.SetR(2, (ushort)table); m.Cpu.Jump(0x0C, 0x1000);
                m.Run(100_000, x => x.Cpu.Address == 0xC1004);
                Assert.Equal(0xC1004u, m.Cpu.Address);
                Assert.Equal(LiveOverlay.Crc32(after), (uint)m.Cpu.R(4) | ((uint)m.Cpu.R(5) << 16));
                cases++;
            }
        }
        Assert.Equal(451, cases);
    }

    [Fact]
    public void FailedWordReadbackDisablesTheMapAndAdvancesRevision()
    {
        var before = V3Fixture.Flash(1); var after = before.ToArray(); after[0]++;
        var text = V3Fixture.AllLoaded() + V3Fixture.Send(V3Fixture.Patch(1, 0, before, after, 10)) + "1 idle\n";
        var injected = false; byte state = 0; uint revision = 0;
        void Attach(DriveContext c) => c.PowerCycleStarted += (m, _) =>
        {
            m.OnInstruction(V3Fixture.CommittedAddress, x =>
            {
                if (injected) return;
                injected = true; x.Bus.Xram[0x6E0] ^= 1;
            });
            c.PowerCycleEnded += (x, _) => { state = x.Bus.Xram[0x6C0]; revision = BinaryPrimitives.ReadUInt32LittleEndian(x.Bus.Xram.AsSpan(0x7C4)); };
        };
        var r = DriveRun.Run(V3Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        var reply = r.KLine.Single(e => e.Request[3] == 9).Reply!;
        Assert.True(injected); Assert.Equal(0xB1, reply[2]); Assert.Equal(9, reply[3]);
        Assert.Equal(2, state); Assert.Equal(11u, revision);
    }

    [Fact]
    public void NewSessionKeepsMapsActiveAndRejectsPacketsFromThePreviousSession()
    {
        var before = V3Fixture.Flash(1); var after = before.ToArray(); after[0]++;
        var text = V3Fixture.AllLoaded() + V3Fixture.Send(V3Fixture.Open(1234))
            + V3Fixture.Send(V3Fixture.Patch(1, 0, before, after, 10))
            + V3Fixture.Send(V3Fixture.Patch(1, 0, before, after, 0, 1234)) + "1 idle\n";
        byte[]? final = null; byte[]? states = null;
        void Attach(DriveContext c) => c.PowerCycleEnded += (m, _) =>
        {
            final = V3Fixture.Copy(m, 1); states = Enumerable.Range(0, 5).Select(i => m.Bus.Xram[0x6B8 + i * 8]).ToArray();
        };
        var r = DriveRun.Run(V3Fixture.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        var replies = r.KLine.Where(e => e.Request[3] == 9).Select(e => e.Reply!).ToArray();
        Assert.Equal(0xB1, replies[0][2]); Assert.Equal(12, replies[0][3]); Assert.Equal(0xA0, replies[1][2]);
        Assert.Equal(after, final); Assert.All(states!, s => Assert.Equal(3, s));
    }
}
