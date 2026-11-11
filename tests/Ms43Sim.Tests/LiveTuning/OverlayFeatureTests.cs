using System.Text.Json;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using Xunit.Abstractions;

namespace Ms43Sim.Tests.LiveTuning;

public static class FeatureFixture
{
    public sealed record Reader(string Name, int Group, int Offset, string Operation, int Site, byte[] Original);

    public static Reader[] Readers
    {
        get
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot,
                "defs/live_overlay_v10_reads.json")));
            return json.RootElement.EnumerateArray().Select(e => new Reader(e.GetProperty("name").GetString()!,
                e.GetProperty("group").GetInt32(), e.GetProperty("offset").GetInt32(),
                e.GetProperty("operation").GetString()!, e.GetProperty("site").GetInt32(),
                Convert.FromHexString(e.GetProperty("original").GetString()!))).ToArray();
        }
    }

    public static int Address(int group) => 0x7132C + (group - 124) * 8;
}

public sealed class OverlayFeatureTests(ITestOutputHelper output)
{
    private const uint Trampoline = 0xC1000;
    private static readonly byte[] Values = [0, 1, 0x7F, 0x80, 0xFF];

    private static Machine Direct(byte[] image, byte[] code)
    {
        code.CopyTo(image, 0x41000);
        var m = Machine.Create(FlashImageBuilder.Raw(FirmwareImage.FromBytes(image)));
        m.PowerOn();
        m.Watchdog.Disable();
        m.Bus.XperipheralsEnabled = true;
        m.Sfrs.Write16(0xFE1A, 0x0401);
        m.Sfrs.Write16(0xFF16, 0x04AF);
        m.Sfrs.Write16(0xFE00, 0x1C);
        m.Sfrs.Write16(0xFE02, 0x3B);
        m.Sfrs.Write16(0xFE04, 0x10);
        m.Sfrs.Write16(0xFE06, 3);
        m.Cpu.SetR(0, 0xF780);
        return m;
    }

    internal static void Execute(Machine m, int length = 4)
    {
        var end = Trampoline + (uint)length;
        m.Cpu.Jump(0x0C, 0x1000);
        m.Run(20_000, x => x.Cpu.Address == end);
        Assert.Equal(end, m.Cpu.Address);
    }

    private static void SeedRegisters(Machine m, ushort psw, byte operand)
    {
        for (var r = 0; r < 16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + 0x31 * r));
        m.Cpu.SetR(0, 0xF780);
        m.Cpu.SetR(4, (ushort)(0xA500 | operand));
        m.Cpu.SetR(5, (ushort)(0x5A00 | operand));
        m.Cpu.Psw = psw;
        m.Bus.Poke16(0xFDF8, 0xA5F7);
    }

    private static uint Allocation(Machine m, int group, byte[] data, bool poolB = false,
        bool extra = false, string state = "live")
    {
        Array.Clear(m.Bus.Xram, 0x6B0, 0x118);
        m.Bus.Poke16(0xE6B0, state == "uninitialised" ? (ushort)0 : (ushort)0xC0DA);
        m.Bus.Poke16(0xF900, 2);
        m.Bus.Poke16(0xFD2C, 0x0400);
        m.Bus.Poke16(0xE2A6, state == "expired" ? (ushort)300 : (ushort)0);
        m.Bus.Xram[0x6DB] = 255;
        var logical = poolB ? 704 : 0;
        var record = extra ? 0xE7A8u : 0xE6D8u;
        m.Bus.Poke16(record, (ushort)(group << 8 | (state == "disabled" ? 2 : 3)));
        m.Bus.Poke16(record + 2, (ushort)(0xFF00 | logical / 4));
        if (extra)
        {
            m.Bus.Xram[0x6DB] = 50;
            m.Bus.Xram[0x7B0 + group / 8] = (byte)(1 << (group % 8));
        }
        if (poolB) data.CopyTo(m.Bus.Xram, 0x6E0);
        else data.CopyTo(m.Bus.ExternalRam, 0x1D00);
        return V10Fixture.Physical(logical);
    }

    private static void EqualCpu(Machine expected, Machine actual, string context)
    {
        for (var r = 0; r < 16; r++)
            Assert.True(expected.Cpu.R(r) == actual.Cpu.R(r),
                $"{context}: R{r} expected {expected.Cpu.R(r):X4}, actual {actual.Cpu.R(r):X4}");
        Assert.True(expected.Cpu.Psw == actual.Cpu.Psw,
            $"{context}: PSW expected {expected.Cpu.Psw:X4}, actual {actual.Cpu.Psw:X4}");
        for (var dpp = 0; dpp < 4; dpp++) Assert.Equal(expected.Cpu.Dpp(dpp), actual.Cpu.Dpp(dpp));
        Assert.Equal(expected.Cpu.Cp, actual.Cpu.Cp);
        Assert.Equal(expected.Cpu.Sp, actual.Cpu.Sp);
    }

    [Fact]
    public void AllCompiledFeatureHooksMatchOriginalInstructionsAcrossFallbacksAndAllocations()
    {
        var readers = FeatureFixture.Readers;
        Assert.Equal(20, readers.Length);
        Assert.Equal(6, readers.Count(r => r.Operation == "PAIR"));
        Assert.Equal(26, readers.Sum(r => r.Operation == "PAIR" ? 2 : 1));
        var cases = 0;
        foreach (var read in readers)
        for (var sample = 0; sample < Values.Length; sample++)
        {
            var flash = Enumerable.Range(0, 8).Select(i => Values[(sample + i) % Values.Length]).ToArray();
            var live = Enumerable.Range(0, 8).Select(i => Values[(sample + i + 1) % Values.Length]).ToArray();
            var overlay = V10Fixture.Bytes;
            var donor = TestBins.CarBytes;
            Assert.Equal(read.Original, donor.AsSpan(read.Site, read.Original.Length).ToArray());
            flash.CopyTo(overlay, FeatureFixture.Address(read.Group));
            flash.CopyTo(donor, FeatureFixture.Address(read.Group));
            var actual = Direct(overlay, overlay.AsSpan(read.Site, read.Original.Length).ToArray());
            var flashed = Direct(donor, read.Original);
            var changed = TestBins.CarBytes;
            live.CopyTo(changed, FeatureFixture.Address(read.Group));
            var edited = Direct(changed, read.Original);

            foreach (var state in new[] { "uninitialised", "disabled", "expired", "live" })
            foreach (var poolB in new[] { false, true })
            foreach (var extra in new[] { false, true })
            for (var cv = 0; cv < 4; cv++)
            {
                Allocation(actual, read.Group, live, poolB, extra, state);
                // From V9 an activation byte of 80h and up is a trigger, by design: TriggerTests covers it, not the raw read.
                if (read.Offset == 0 && (state == "live" ? live : flash)[0] >= 0x80) continue;
                var expected = state == "live" ? edited : flashed;
                var psw = (ushort)(0x0459 | cv << 1);
                var operand = new byte[] { 0, 0x7F, 0x80, 0xFF }[cv];
                SeedRegisters(actual, psw, operand);
                SeedRegisters(expected, psw, operand);
                Execute(expected, read.Original.Length);
                Execute(actual, read.Original.Length);
                var context = $"{read.Name}, {state}, pool {(poolB ? 'B' : 'A')}, extra {extra}, value {sample}, CV {cv}";
                EqualCpu(expected, actual, context);
                var flags = (ushort)0xA5F7;
                var effective = state == "live" ? live : flash;
                // Configuration zero additionally releases its latch; the donor instruction alone does not.
                if (read.Offset == 0 && effective[0] == 0) flags &= (ushort)~(1 << (read.Group - 124));
                Assert.True(flags == actual.Bus.Peek16(0xFDF8), context + ": unrelated feature flags changed");
                cases++;
            }
        }
        output.WriteLine($"{cases} compiled hook comparisons; 20 hook blocks, 26 scalar reads, both RAM pools and directory kinds.");
    }

    [Theory]
    [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(16, false)] [InlineData(16, true)]
    public void EveryCompiledPairKeepsItsTwoLoadsTogetherAcrossInterruptAndPecRequests(int page, bool pec)
    {
        foreach (var read in FeatureFixture.Readers.Where(r => r.Operation == "PAIR"))
        {
            var image = V10Fixture.Bytes;
            var start = V10Fixture.Symbol("feature_" + read.Name + "_read") & 0x7FFFF;
            var code = image.AsSpan(start, 8).ToArray();
            var offset = page == 3 ? 0x26E0 : 0x1D00;
            var physical = (uint)(page * 0x4000 + offset);
            var t = TestMachine.Create(Convert.ToHexString(code) + " CC00", (0x84, "FB88"))
                .SetR(2, 0xBEEF).SetR(3, (ushort)offset).SetR(4, (ushort)page)
                .WriteSfr(0xFE1A, 0x0401).WriteSfr(0xFF16, 0x04AF);
            t.M.Watchdog.Disable();
            t.M.Bus.XperipheralsEnabled = true;
            t.Poke16(physical, 0xA512);
            var destination = page == 3 ? (ushort)physical : (ushort)0xF900;
            if (pec)
                t.Poke16(0xE704, 0x5A34).WriteSfr(0xFEC0, 0x00FF).Poke16(0xFCE0, 0xE704)
                    .Poke16(0xFCE2, destination).WriteSfr(0xFF98, 0x0078);
            else t.WriteSfr(0xFF9E, 0x004C);
            t.Psw = 0x0800;
            t.Step(2); // EXTP and the first byte load.
            Assert.Equal(0x12, t.R(2) & 255);
            t.M.Interrupts.Request(InterruptSources.ByName(pec ? "ADC" : "T1"));
            t.Step();
            Assert.Equal(0UL, t.M.Entries);
            Assert.Equal(0x1008, t.Cpu.IP);
            Assert.Equal(0xA512, t.R(2));
            Assert.Equal(0xA512, t.Peek16(physical));
            t.Step();
            Assert.Equal(1UL, t.M.Entries);
            Assert.Equal(0xA512, t.R(2));
            if (pec) Assert.Equal(0x5A34, t.Peek16(destination));
            else Assert.Equal(0x84, t.Cpu.IP);
        }
    }

    internal static byte[] FeatureData(int group) => group switch
    {
        124 => [1, 30, 100, 140, 75, 128, 85, 128],
        125 => [1, 20, 100, 132, 75, 128, 85, 128],
        126 => [1, 100, 132, 75, 128, 85, 128, TestBins.CarBytes[0x71343]],
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    internal static Machine Feature(int group, byte[]? data = null, bool flashedDisabled = false,
        string state = "live", byte? flashed = null)
    {
        var image = V10Fixture.Bytes;
        if (flashedDisabled) image[FeatureFixture.Address(group)] = 0;
        if (flashed is { } config) image[FeatureFixture.Address(group)] = config;
        var function = group switch { 124 => 0xFCF2, 125 => 0xFD44, 126 => 0xFDA4, _ => throw new ArgumentOutOfRangeException(nameof(group)) };
        var m = Direct(image, [0xDA, 0x0B, (byte)function, (byte)(function >> 8)]);
        Allocation(m, group, data ?? FeatureData(group), state: state);
        Inputs(m);
        return m;
    }

    private static void PutByte(Machine m, uint address, byte value)
    {
        var word = m.Bus.Peek16(address & ~1u);
        var shift = (int)(address & 1) * 8;
        m.Bus.Poke16(address & ~1u, (ushort)((word & ~(255 << shift)) | value << shift));
    }

    internal static void Inputs(Machine m, byte speed = 30, byte pedal = 100, bool clutch = true,
        byte cruise = 6, bool sport = false, byte rpm = 100)
    {
        PutByte(m, 0x4047F, speed);
        PutByte(m, 0xE0FF, pedal);
        PutByte(m, 0x400B6, cruise);
        PutByte(m, 0x4007F, rpm);
        m.Bus.Poke16(0xFD2E, clutch ? (ushort)0x40 : (ushort)0);
        m.Bus.Poke16(0xFD18, sport ? (ushort)2 : (ushort)0);
    }

    internal static void Active(Machine m, int group, bool expected) =>
        Assert.Equal(expected, (m.Bus.Peek16(0xFDF8) & 1 << (group - 124)) != 0);

    [Fact]
    public void LaunchControlUsesLiveEntryAndExitThresholdsAndRetainsItsClutchReleaseBehavior()
    {
        var m = Feature(124);
        Inputs(m, speed: 0, pedal: 99);
        Execute(m); Active(m, 124, false);
        Inputs(m, speed: 1);
        Execute(m); Active(m, 124, false);
        Inputs(m, speed: 0, clutch: false);
        Execute(m); Active(m, 124, false);
        Inputs(m, speed: 0);
        Execute(m); Active(m, 124, true);
        Assert.Equal(100, m.Bus.Peek16(0xE3B0)); // donor E6 F6 64 00 loads decimal 100
        Assert.NotEqual(0, m.Bus.Peek16(0xFD2A) & 0x1000);
        Inputs(m, speed: 29, clutch: false);
        Execute(m); Active(m, 124, true);
        Inputs(m, speed: 30, clutch: false);
        Execute(m); Active(m, 124, false);
        Inputs(m, speed: 0);
        Execute(m); Active(m, 124, true);
        Inputs(m, speed: 0, pedal: 99);
        Execute(m); Active(m, 124, false);
    }

    [Fact]
    public void NoLiftShiftUsesLiveEntryGatesAndReleasesOnClutchOrPedal()
    {
        var m = Feature(125);
        Inputs(m, speed: 19);
        Execute(m); Active(m, 125, false);
        Inputs(m, speed: 20, pedal: 99);
        Execute(m); Active(m, 125, false);
        Inputs(m, speed: 20, clutch: false);
        Execute(m); Active(m, 125, false);
        Inputs(m, speed: 20);
        Execute(m); Active(m, 125, true);
        Assert.Equal(104, m.Bus.ExternalRam[0xBE]);
        Inputs(m, speed: 0);
        Execute(m); Active(m, 125, true);
        Inputs(m, clutch: false);
        Execute(m); Active(m, 125, false);
        Inputs(m);
        Execute(m); Active(m, 125, true);
        Inputs(m, pedal: 99);
        Execute(m); Active(m, 125, false);
    }

    [Fact]
    public void RollingAntiLagUsesLivePedalThresholdAndReleasesOnEveryOriginalGate()
    {
        var m = Feature(126);
        Inputs(m, sport: true);
        Execute(m); Active(m, 126, false);
        Inputs(m, cruise: 5);
        Execute(m); Active(m, 126, false);
        Inputs(m, pedal: 99);
        Execute(m); Active(m, 126, false);
        foreach (var gate in new[] { "sport", "cruise", "pedal" })
        {
            Inputs(m);
            Execute(m); Active(m, 126, true);
            Assert.Equal(104, m.Bus.ExternalRam[0xBE]);
            Inputs(m, sport: gate == "sport", cruise: gate == "cruise" ? (byte)5 : (byte)6,
                pedal: gate == "pedal" ? (byte)99 : (byte)100);
            Execute(m); Active(m, 126, false);
        }
    }

    [Theory]
    [InlineData(124)] [InlineData(125)]
    public void ConditionalConfigurationRequiresItsOriginalEntryGate(int group)
    {
        var data = FeatureData(group); data[0] = 2;
        var m = Feature(group, data);
        Inputs(m, speed: group == 124 ? (byte)0 : (byte)30);
        Execute(m); Active(m, group, false);
        Inputs(m, speed: group == 124 ? (byte)0 : (byte)30, sport: true);
        Execute(m); Active(m, group, true);
        Inputs(m, speed: group == 124 ? (byte)0 : (byte)30);
        Execute(m); Active(m, group, true);
    }

    [Theory]
    [InlineData(124)] [InlineData(125)] [InlineData(126)]
    public void LiveConfigurationZeroReleasesOnlyItsOwnLatchAndCanBeEnabledAgain(int group)
    {
        var m = Feature(group);
        Inputs(m, speed: group == 124 ? (byte)0 : (byte)30);
        Execute(m); Active(m, group, true);
        m.Bus.Poke16(0xFDF8, 0xA5F7);
        m.Bus.Poke16(0xE3B0, 0x1234);
        m.Bus.ExternalRam[0xBE] = 0xAA;
        m.Bus.ExternalRam[0x1D00] = 0;
        Execute(m);
        Assert.Equal((ushort)(0xA5F7 & ~(1 << (group - 124))), m.Bus.Peek16(0xFDF8));
        Assert.Equal(0x1234, m.Bus.Peek16(0xE3B0));
        Assert.Equal(0xAA, m.Bus.ExternalRam[0xBE]);
        m.Bus.ExternalRam[0x1D00] = 1;
        Execute(m); Active(m, group, true);
    }

    [Theory]
    [InlineData(124)] [InlineData(125)] [InlineData(126)]
    public void FallingBackToFlashedConfigurationZeroAlsoReleasesTheActiveLatch(int group)
    {
        foreach (var state in new[] { "uninitialised", "disabled", "expired" })
        {
            var m = Feature(group, flashedDisabled: true, state: state);
            m.Bus.Poke16(0xFDF8, 0xA5F7);
            Execute(m);
            Assert.Equal((ushort)(0xA5F7 & ~(1 << (group - 124))), m.Bus.Peek16(0xFDF8));
        }
    }

    [Theory]
    [InlineData(125)] [InlineData(126)]
    public void RpmOffsetChangesAreCapturedAtTheNextEngagement(int group)
    {
        var m = Feature(group);
        Execute(m); Active(m, group, true);
        Assert.Equal(104, m.Bus.ExternalRam[0xBE]);
        m.Bus.ExternalRam[0x1D00 + (group == 125 ? 3 : 2)] = 136;
        Inputs(m, rpm: 120);
        Execute(m); Active(m, group, true);
        Assert.Equal(104, m.Bus.ExternalRam[0xBE]);
        Inputs(m, clutch: group != 125, cruise: group == 126 ? (byte)5 : (byte)6, rpm: 120);
        Execute(m); Active(m, group, false);
        Inputs(m, rpm: 120);
        Execute(m); Active(m, group, true);
        Assert.Equal(128, m.Bus.ExternalRam[0xBE]);
    }

    [Theory]
    [InlineData(125, 100, 0, 0)] [InlineData(126, 100, 0, 0)]
    [InlineData(125, 200, 255, 255)] [InlineData(126, 200, 255, 255)]
    public void CapturedRpmRetainsTheOriginalSaturation(int group, byte rpm, byte offset, byte expected)
    {
        var data = FeatureData(group); data[group == 125 ? 3 : 2] = offset;
        var m = Feature(group, data);
        Inputs(m, rpm: rpm);
        Execute(m); Active(m, group, true);
        Assert.Equal(expected, m.Bus.ExternalRam[0xBE]);
    }
}
