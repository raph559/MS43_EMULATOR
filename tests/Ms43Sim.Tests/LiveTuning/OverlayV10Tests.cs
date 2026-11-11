using System.Buffers.Binary;
using System.Text.Json;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

public sealed class OverlayV10Tests
{
    private static Machine Start(byte[]? image = null) => V10Fixture.Start(image);
    private static byte[] Command(Machine m, byte[] payload) => V10Fixture.Command(m, payload);
    private static byte[] Mutate(Machine m, byte[] payload) => V10Fixture.Mutate(m, payload);
    private static uint Revision(Machine m) => V10Fixture.Revision(m);
    private static int Free(Machine m) => V10Fixture.Free(m);
    private static byte[] Write(int id, byte[] bytes) => [0x6D, 2, (byte)id, 0, 0, .. bytes];

    private static byte[] With(int id, params (int Index, byte Value)[] changes)
    {
        var bytes = V10Fixture.Group(id);
        foreach (var (index, value) in changes) bytes[index] = value;
        return bytes;
    }

    [AslFact] // the source is patches/live_overlay.asm with its section files put in place of their INCLUDE lines
    public void TheFixtureIsTheSourcesAssembly()
    {
        var patches = Path.Combine(OverlayHex.RepositoryRoot, "patches");
        var source = string.Join('\n', File.ReadAllLines(Path.Combine(patches, "live_overlay.asm")).Select(line =>
        {
            var words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length == 2 && words[0] == "INCLUDE" ? File.ReadAllText(Path.Combine(patches, words[1].Trim('"'))) : line;
        })) + '\n';
        var fixture = OverlayHex.Parse(File.ReadAllText(Path.Combine(OverlayHex.RepositoryRoot,
            "sim/tests/Ms43Sim.Tests/fixtures/live_overlay_v10.hex")));
        Assert.Equal(fixture.Select(r => (r.Offset, Convert.ToHexString(r.Bytes))),
            OverlayHex.Parse(Asl.Assemble(source)).Select(r => (r.Offset, Convert.ToHexString(r.Bytes))));
    }

    [Fact] // V10.4: the low byte stays 10, so every V10 rule holds; the high byte is the minor (V8.5's way)
    public void TheV104HeaderSaysMinor4()
    {
        var image = V10Fixture.Bytes;
        Assert.Equal("LTOV"u8.ToArray(), image.AsSpan(0x4B544, 4).ToArray());
        Assert.Equal(0x040A, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B548)));
        Assert.Equal(0xFFA8, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B54E)));
        Assert.Equal(143, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B550)));
        Assert.Equal(0xC0DA, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B552)));
    }

    [Fact] // V10.4 beside the sent V10.2 first build, V10.1 and V10: each program is known whatever its flashed settings
    public void ProgramIdentityNamesV104()
    {
        static string? Name(byte[] bytes) => ProgramIdentity.Name(TestImages.AsFlashed(bytes).Image);
        Assert.Equal("MS43X001 live overlay V10.4", Name(V10Fixture.Bytes));
        Assert.Equal("MS43X001 live overlay V10.4", Name(V10Fixture.ClassicBytes));
        var first = TestBins.LiveOverlayV102FirstBytes;                   // sent 2026-11-08, before the review's cap fix
        Assert.Equal("MS43X001 live overlay V10.2 (first build)", Name(first));
        V10Fixture.Classic.CopyTo(first, V10Fixture.Defaults);
        Assert.Equal("MS43X001 live overlay V10.2 (first build)", Name(first));
        var fastBlip = TestBins.LiveOverlayV101FastBlipBytes;
        Assert.Equal("MS43X001 live overlay V10.1", Name(fastBlip));
        V10Fixture.Flashed.CopyTo(fastBlip, V10Fixture.Defaults);
        Assert.Equal("MS43X001 live overlay V10.1", Name(fastBlip));
        var sent = TestBins.LiveOverlayV10Bytes;
        Assert.Equal("MS43X001 live overlay V10", Name(sent));
        V10Fixture.Flashed.CopyTo(sent, V10Fixture.Defaults);
        Assert.Equal("MS43X001 live overlay V10", Name(sent));
    }

    [Fact] // V10.2: the start's pre-injection (ip_tipr_cst__tco) is stock X001's again; the car file has it x 1.6
    public void TheStartPreInjectionIsStockX001()
    {
        byte[] stock = [0x62, 0x9D, 0x02, 0x79, 0x33, 0x53, 0x2A, 0x37, 0x0A, 0x27, 0x6B, 0x17, 0xDA, 0x0E, 0x82, 0x0A];
        Assert.Equal(stock, V10Fixture.Bytes.AsSpan(0x7DE64, 16).ToArray());
        Assert.Equal(stock, TestBins.ReleaseBytes.AsSpan(0x7DE64, 16).ToArray());
        Assert.NotEqual(stock, TestBins.CarBytes.AsSpan(0x7DE64, 16).ToArray());
    }

    [Fact] // V10.2 (spec §6c): c_inj_flow, 16 bits little-endian, the X001 release's 237 cm³/min; the car file has 225
    public void TheInjectorFlowIsTheX001Releases()
    {
        Assert.Equal(new byte[] { 0xED, 0x00 }, V10Fixture.Bytes.AsSpan(0x70E14, 2).ToArray());
        Assert.Equal(new byte[] { 0xED, 0x00 }, TestBins.ReleaseBytes.AsSpan(0x70E14, 2).ToArray());
        Assert.Equal(new byte[] { 0xE1, 0x00 }, TestBins.CarBytes.AsSpan(0x70E14, 2).ToArray());
    }

    /// <summary>
    /// The built V10.3 (V10.2's plan), checksums corrected as a full write does, against the V10.1 fast-blip bin sent to the owner: the
    /// only differences are the overlay block, the new hook at 0x3D4D4, the start table, the injector flow and the
    /// checksum words.
    /// </summary>
    [Fact]
    public void V102DiffersFromV101OnlyWhereThePlanSays()
    {
        var built = TestImages.AsFlashed(V10Fixture.Bytes).Image.Bytes.ToArray();
        var sent = TestBins.LiveOverlayV101FastBlipBytes;
        (int Start, int End)[] allowed =
        [
            (0x4B544, 0x4FFE6),      // the overlay block, its code and its flashed settings
            (0x3D4D4, 0x3D4D8),      // the AFR display join's JMPS
            (0x7DE64, 0x7DE74),      // ip_tipr_cst__tco, back to stock
            (0x70E14, 0x70E16),      // c_inj_flow, the X001 release's 237
            .. ChecksumWords,
        ];
        var outside = Enumerable.Range(0, built.Length)
            .Where(i => built[i] != sent[i] && !allowed.Any(a => i >= a.Start && i < a.End) && !IsHookTarget(built, sent, i))
            .Select(i => $"{i:X5}").ToList();
        Assert.True(outside.Count == 0, "differences outside the plan's ranges: " + string.Join(" ", outside.Take(20)));
        Assert.Contains(Enumerable.Range(0x7DE64, 16), i => built[i] != sent[i]);
        Assert.Contains(Enumerable.Range(0x3D4D4, 4), i => built[i] != sent[i]);
        Assert.Contains(Enumerable.Range(0x70E14, 2), i => built[i] != sent[i]);
    }

    /// <summary>The five stored checksums (tools/ms43_checksums.py): boot, program and calibration CRCs, program and cal sums.</summary>
    private static readonly (int Start, int End)[] ChecksumWords =
        [(0x3C24, 0x3C26), (0x6FDE0, 0x6FDE2), (0x73FE0, 0x73FE2), (0x6FDAE, 0x6FDB2), (0x72FFC, 0x73000)];

    /// <summary>
    /// A byte of an existing hook's target: a CALLS or JMPS into segment 0C at the same site in both images, whose offset
    /// moved because the overlay's code grew.
    /// </summary>
    private static bool IsHookTarget(byte[] built, byte[] sent, int i)
    {
        foreach (var site in new[] { i - 2, i - 3 })
            if (site >= 0 && built[site] is 0xDA or 0xFA && built[site + 1] == 0x0C && sent[site] == built[site] && sent[site + 1] == 0x0C)
                return true;
        return false;
    }

    [Fact]
    public void PoolAAllocationsMayNotReachTheV10State()
    {
        var m = Start();
        Assert.Equal(896, Free(m));
        Assert.NotEqual(0xA0, Mutate(m, V10Fixture.Load(138, 664))[0]);
        Assert.Equal(896, Free(m));
        Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(138, 660))[0]);
        Assert.Equal(888, Free(m));
    }

    public static TheoryData<int[], int[]> FittingSets => new()
    {
        { [2, 3, 4, 1], [0, 192, 384, 576] },          // the four without the main ignition map, in pool A alone
        { [0, 3, 1, 4], [0, 320, 512, 704] },          // each four without one 192-byte table, that one in pool B
        { [0, 2, 1, 4], [0, 320, 512, 704] },
        { [0, 2, 1, 3], [0, 320, 512, 704] },
    };

    [Theory] // owner, 2026-10-29: the pops throttle opening goes to 20.0 deg TPS
    [InlineData(200, true)] [InlineData(201, false)] [InlineData(255, false)]
    public void PopsThrottleOpeningGoesTo200(byte value, bool accepted)
    {
        var m = Start();
        var copy = V10Fixture.Group(138); copy[6] = value;
        Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(138, 704))[0]);
        var reply = Mutate(m, [0x6D, 2, 138, 0, 0, .. copy]);
        if (accepted) Assert.Equal(0xA0, reply[0]);
        else Assert.Equal(new byte[] { 0xB1, 4 }, reply);
    }

    [Theory, MemberData(nameof(FittingSets))] // RF5, at 668
    public void V8sFittingCombinationsStillFit(int[] ids, int[] offsets)
    {
        var m = Start();
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(ids[i], offsets[i]))[0]);
            Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(ids[i], LiveOverlay.Crc32(V10Fixture.Flash(ids[i]))))[0]);
        }
    }

    [Fact]
    public void TheFlashedDefaultsAreTheSpecs()
    {
        var image = V10Fixture.Bytes;
        Assert.Equal(V10Fixture.Flashed, image.AsSpan(V10Fixture.Defaults, 40).ToArray());
        var tables = 0x40000 + BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B54C));
        for (var id = 138; id <= 142; id++)
        {
            var entry = image.AsSpan(tables + id * 8, 8);
            Assert.Equal(0x33, BinaryPrimitives.ReadUInt16LittleEndian(entry));
            Assert.Equal(0x3F80 + 8 * (id - 138), BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]));
            Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]));
            Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]));
        }
        Assert.Equal(0x0CFFA7u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x6FE00)));
        for (var id = 139; id <= 142; id++)
        for (var index = 0; index < 8; index++)
            Assert.True(V10Fixture.Group(id)[index] == Setting(image, id, index), $"{id}/{index}");
    }

    private static int Setting(byte[] image, int id, int index)
    {
        var m = V10Fixture.Direct(image, V10Fixture.CallA("v10_setting"));
        m.Cpu.SetR(0, 0xF780); m.Cpu.SetR(2, (ushort)id); m.Cpu.SetR(3, (ushort)index);
        V10Fixture.Execute(m);
        return m.Cpu.R(4);
    }

    public static TheoryData<int, int, byte> OutOfRange => new()
    {
        { 138, 3, 49 }, { 138, 3, 148 },
        { 138, 2, 144 }, { 138, 2, 255 },                                               // V10.4: pops spark at most +30.0
        { 139, 0, 1 }, { 139, 1, 25 }, { 139, 2, 15 }, { 139, 3, 25 }, { 139, 4, 15 },  // V10.3: 139 is reserved
        { 140, 0, 3 }, { 140, 0, 0x7F }, { 140, 0, 0xA0 }, { 140, 0, 0xFF }, { 140, 1, 0 }, { 140, 1, 4 },
        { 140, 2, 9 }, { 140, 3, 62 }, { 140, 3, 188 }, { 140, 4, 2 }, { 140, 4, 31 }, { 140, 5, 151 },
        { 140, 6, 151 }, { 140, 7, 62 }, { 140, 7, 144 },
        { 141, 0, 2 }, { 141, 1, 2 }, { 141, 2, 2 }, { 141, 3, 2 }, { 141, 4, 10 }, { 141, 4, 86 }, { 141, 5, 9 },
        { 141, 5, 61 }, { 141, 6, 31 }, { 141, 7, 144 },
        { 142, 0, 179 }, { 142, 1, 99 }, { 142, 1, 161 }, { 142, 2, 69 }, { 142, 2, 111 }, { 142, 3, 49 }, { 142, 3, 86 },
    };

    [Theory, MemberData(nameof(OutOfRange))]
    public void EachSettingOutsideItsLimitsIsRefusedWithReason4(int id, int index, byte value) =>
        Refused(id, With(id, (index, value)));

    public static TheoryData<int, int, byte> AtTheLimits => new()
    {
        { 138, 3, 50 }, { 138, 3, 70 }, { 138, 3, 147 },
        { 138, 2, 0 }, { 138, 2, 143 },
        { 139, 0, 0 },
        { 140, 0, 0 }, { 140, 0, 2 }, { 140, 0, 0x80 }, { 140, 0, 0x97 }, { 140, 0, 0x9F }, { 140, 1, 1 }, { 140, 1, 3 },
        { 140, 2, 10 }, { 140, 2, 255 }, { 140, 3, 63 }, { 140, 3, 187 }, { 140, 4, 3 }, { 140, 4, 30 }, { 140, 5, 150 },
        { 140, 6, 0 }, { 140, 7, 63 }, { 140, 7, 143 },
        { 141, 0, 0 }, { 141, 3, 1 }, { 141, 4, 11 }, { 141, 4, 85 }, { 141, 5, 10 }, { 141, 5, 60 }, { 141, 6, 0 },
        { 141, 6, 30 }, { 141, 7, 0 }, { 141, 7, 143 },
        { 142, 0, 180 }, { 142, 0, 255 }, { 142, 1, 100 }, { 142, 1, 160 }, { 142, 2, 70 }, { 142, 2, 110 },
        { 142, 3, 50 }, { 142, 3, 85 },
    };

    [Theory, MemberData(nameof(AtTheLimits))]
    public void EachSettingAtItsLimitsIsAccepted(int id, int index, byte value) => Accepted(id, With(id, (index, value)));

    [Fact]
    public void AHoldOpeningAboveTheBlipOpeningIsRefused()
    {
        Refused(140, With(140, (5, 50), (6, 51)));
        Accepted(140, With(140, (5, 50), (6, 50)));
    }

    public static TheoryData<int, int> Reserved => new()
    {
        { 139, 0 }, { 139, 1 }, { 139, 2 }, { 139, 3 }, { 139, 4 }, { 139, 5 }, { 139, 6 }, { 139, 7 }, { 142, 4 }, { 142, 5 }, { 142, 6 }, { 142, 7 },
    };

    [Theory, MemberData(nameof(Reserved))]
    public void ReservedBytesMustBeZero(int id, int index) => Refused(id, With(id, (index, 1)));

    /// <summary>A bad copy is refused on Write and on Patch with reason 4, and the copy stays as it was.</summary>
    private static void Refused(int id, byte[] bad)
    {
        var m = Start(); var flashed = V10Fixture.Group(id);
        Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(id, 704))[0]);
        var revision = Revision(m);
        Assert.Equal(new byte[] { 0xB1, 4 }, Mutate(m, Write(id, bad)));
        Assert.Equal(revision, Revision(m)); Assert.Equal(flashed, V10Fixture.Read(m, 0xE6E0, 8));
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(id, LiveOverlay.Crc32(flashed)))[0]);
        revision = Revision(m);
        Assert.Equal(new byte[] { 0xB1, 4 }, Command(m, V10Fixture.Patch(id, 0, bad, revision)));
        Assert.Equal(revision, Revision(m)); Assert.Equal(flashed, V10Fixture.Read(m, 0xE6E0, 8));
    }

    private static void Accepted(int id, byte[] good)
    {
        var m = Start();
        Assert.Equal(0xA0, Mutate(m, V10Fixture.Load(id, 704))[0]);
        Assert.Equal(0xA0, Mutate(m, Write(id, good))[0]);
        Assert.Equal(0xA0, Mutate(m, LiveOverlay.On(id, LiveOverlay.Crc32(good)))[0]);
        Assert.Equal(good, V10Fixture.Read(m, 0xE6E0, 8));
    }

    [Fact]
    public void FlashedValuesBeyondALimitAreClamped()
    {
        var image = V10Fixture.Bytes;
        image[V10Fixture.Defaults + 2 * 8 + 3] = 250;     // 140/3, highest 187
        image[V10Fixture.Defaults + 1 * 8 + 1] = 200;     // 139/1, reserved: highest 0 (V10.3)
        image[V10Fixture.Defaults + 1 * 8 + 2] = 1;       // 139/2, reserved
        Assert.Equal(187, Setting(image, 140, 3));
        Assert.Equal(0, Setting(image, 139, 1));
        Assert.Equal(0, Setting(image, 139, 2));
    }

    [Theory] // V10.1's new bytes: 139/4 is reserved on V10.3 (0 whatever is flashed), 141/6 0..30
    [InlineData(139, 4, 0, 0)] [InlineData(139, 4, 15, 0)] [InlineData(139, 4, 200, 0)]
    [InlineData(141, 6, 0, 0)] [InlineData(141, 6, 30, 30)] [InlineData(141, 6, 200, 30)]
    [InlineData(141, 7, 0, 0)] [InlineData(141, 7, 143, 143)] [InlineData(141, 7, 200, 143)] // V10.2's cap angle, −23.625..+30°
    public void TheNewBytesAreClampedToTheirLimits(int id, int index, byte flashed, int expected)
    {
        var image = V10Fixture.Bytes;
        image[V10Fixture.Defaults + (id - 138) * 8 + index] = flashed;
        Assert.Equal(expected, Setting(image, id, index));
    }

    [Theory] // V10.2: the pops AFR target is the real AFR x 10, 5.0..14.7 (V10.1's firmware bytes 128..255 are gone)
    [InlineData(50, 50)] [InlineData(70, 70)] [InlineData(147, 147)] [InlineData(49, 50)] [InlineData(210, 147)]
    public void ThePopsAfrIsARealAfrFrom5To14_7(byte flashed, int expected)
    {
        var image = V10Fixture.Bytes;
        image[V10Fixture.Defaults + 3] = flashed;
        var m = V10Fixture.Direct(image, V10Fixture.CallA("pops_setting"));
        m.Cpu.SetR(0, 0xF780); m.Cpu.SetR(3, 3);
        V10Fixture.Execute(m);
        Assert.Equal(expected, m.Cpu.R(4));
    }

    [Theory] // as pops_setting: the switch keeps a CAN-bit value (80h..9Fh) and reads anything else above 2 as off
    [InlineData(0x85, 0x85)] [InlineData(0x9F, 0x9F)] [InlineData(0xA0, 0)] [InlineData(0x7F, 0)] [InlineData(2, 2)]
    public void TheBlipSwitchKeepsACanBit(byte flashed, int expected)
    {
        var image = V10Fixture.Bytes; image[V10Fixture.Defaults + 2 * 8] = flashed;
        Assert.Equal(expected, Setting(image, 140, 0));
    }

    [Fact]
    public void V10SettingPreservesEveryOtherRegister()
    {
        var m = V10Fixture.Direct(V10Fixture.Bytes, V10Fixture.CallA("v10_setting"));
        for (var r = 1; r < 16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + r * 19));
        m.Cpu.SetR(0, 0xF780); m.Cpu.SetR(2, 141); m.Cpu.SetR(3, 4);
        var before = Enumerable.Range(0, 16).Select(r => m.Cpu.R(r)).ToArray();
        V10Fixture.Execute(m);
        Assert.Equal(0x15, m.Cpu.R(4));
        for (var r = 0; r < 16; r++) if (r != 4) Assert.True(before[r] == m.Cpu.R(r), $"R{r} changed");
    }
}
