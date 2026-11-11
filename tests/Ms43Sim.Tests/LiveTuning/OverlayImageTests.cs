using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The overlay bin's invariants: it changes only its write list; the boot block, the tags and the signature stay the
/// car's; its checksums verify; every hook jumps or calls into segment 0Ch at an even address inside the overlay; its
/// code decodes whole; the program CRC covers it; and its header is read with the stock command 06.
/// </summary>
public class OverlayImageTests
{
    const int Slot = 0x1FC8A, TaskEntry = 0x6E3E2, CrcEnd = 0x6FE00, Block = 0x4B544, BlockEnd = 0x4FFE6;

    /// <summary>The two calibration records the car's build carries: the start pre-injection and the injector flow.</summary>
    static readonly (int Offset, int Bytes)[] Calibration = [(0x7DE64, 16), (0x70E14, 2)];

    /// <summary>The header at 0C:B544: LTOV, the version, then the code's start and end and the data's end in segment 0Ch.</summary>
    static (int CodeStart, int CodeEnd, int DataEnd) Extent(byte[] b)
    {
        Assert.Equal("LTOV", System.Text.Encoding.ASCII.GetString(b, Block, 4));
        int Word(int at) => b[at] | b[at + 1] << 8;
        Assert.Equal(0x040A, Word(Block + 4));
        return (0x40000 + Word(Block + 6), 0x40000 + Word(Block + 8), 0x40000 + Word(Block + 10));
    }

    static byte[] Flashed(FlashedImage image) => image.Image.ToArray();

    /// <summary>The patched sites: every record outside the overlay's block and below the calibration.</summary>
    static IEnumerable<(int Offset, byte[] Bytes)> Hooks => V10Fixture.Records.Where(r => (r.Offset < Block || r.Offset >= BlockEnd) && r.Offset < 0x70000);

    [Fact] public void TheOverlay_ChangesOnlyItsWriteList()
    {
        var overlay = Flashed(V10Fixture.Image);
        var car = Flashed(TestImages.Car);
        var (_, _, dataEnd) = Extent(overlay);
        var allowed = Hooks.SelectMany(r => Enumerable.Range(r.Offset, r.Bytes.Length))
            .Concat(Enumerable.Range(Block, dataEnd - Block))
            .Concat(Calibration.SelectMany(c => Enumerable.Range(c.Offset, c.Bytes)))
            .Concat(Checksums.StoredFields.SelectMany(f => Enumerable.Range(f.Offset, f.Bytes))).ToHashSet();
        var changed = Enumerable.Range(0, FirmwareImage.Size).Where(i => overlay[i] != car[i]).ToList();
        Assert.Empty(changed.Where(i => !allowed.Contains(i)).Select(i => i.ToString("X5")));
        Assert.Equal(216, Hooks.Count());                       // the build's 216 sites, with the task's entry and the CRC range's end
        Assert.All(Hooks, r => Assert.False(overlay.AsSpan(r.Offset, r.Bytes.Length).SequenceEqual(car.AsSpan(r.Offset, r.Bytes.Length)), $"0x{r.Offset:X5} is not patched"));
    }

    [Fact] public void TheBootBlock_TheTags_AndTheSignature_AreUntouched_AndTheCalibrationButTwoRecords()
    {
        var overlay = Flashed(V10Fixture.Image);
        var car = Flashed(TestImages.Car);
        foreach (var (start, end) in new[] { (0x00000, 0x08000), (0x6FFF0, 0x70000), (0x7FFF0, 0x80000), (BlockEnd, 0x50000) })
            Assert.True(overlay.AsSpan(start..end).SequenceEqual(car.AsSpan(start..end)), $"0x{start:X5}–0x{end - 1:X5} differs");
        Assert.Equal(Calibration.OrderBy(c => c.Offset), V10Fixture.Records.Where(r => r.Offset >= 0x70000).Select(r => (r.Offset, r.Bytes.Length)).OrderBy(c => c.Offset));
    }

    [Fact] public void AllFiveChecksums_Verify() =>
        Assert.All(Checksums.Verify(V10Fixture.Image.Image.Bytes), c => Assert.True(c.Ok, $"{c.Kind}: stored {c.Stored:X}, computed {c.Computed:X} {c.Problem}"));

    [Fact] public void EveryHook_EntersSegment0C_AtAnEvenAddress_InsideTheOverlay()
    {
        var b = V10Fixture.Bytes;
        var (codeStart, _, dataEnd) = Extent(b);
        void Inside(int target, string what)
        {
            Assert.True(target % 2 == 0, $"{what} → 0C:{target & 0xFFFF:X4} is odd");
            Assert.InRange(target, codeStart, dataEnd - 2);
        }
        foreach (var (site, bytes) in Hooks.Where(r => r.Offset is not (TaskEntry or CrcEnd)))
        {
            Assert.True(bytes[0] is 0xDA or 0xFA && bytes[1] == 0x0C, $"0x{site:X5}: not CALLS or JMPS 0Ch");
            Inside(0x40000 + (bytes[2] | bytes[3] << 8), $"0x{site:X5}");
            Assert.True(bytes.Length % 2 == 0 && bytes.Length is 4 or 8, $"0x{site:X5}: {bytes.Length} bytes");
        }
        Assert.Contains(Hooks, r => r.Offset == Slot);
        Assert.Equal((0x0C, 0x00), (b[TaskEntry + 2], b[TaskEntry + 3]));                        // queue-1 task 09h: …, 000Ch
        Inside(0x40000 + (b[TaskEntry] | b[TaskEntry + 1] << 8), "queue-1 task 09h");
    }

    [Fact] public void TheFirstCodeSection_DecodesWhole()
    {
        var b = V10Fixture.Bytes;
        var (codeStart, codeEnd, dataEnd) = Extent(b);
        var starts = new HashSet<int>();
        var branches = new List<(int At, int Target)>();
        var at = codeStart;
        while (at < codeEnd)
        {
            var d = Decoder.Decode(b, at, (uint)(0x80000 + at));
            Assert.False(d.Undefined, $"0C:{at & 0xFFFF:X4} does not decode");
            Assert.True(at % 2 == 0, $"0C:{at & 0xFFFF:X4} is odd");
            starts.Add(at);
            var next = at + d.Size;
            var op = b[at];
            if ((op & 0x0F) == 0x0D || op == 0xBB)                                               // JMPR cc, rel; CALLR rel
                branches.Add((at, next + 2 * (sbyte)b[at + 1]));
            else if (op is 0xEA or 0xCA)                                                         // JMPA, CALLA cc, caddr
                branches.Add((at, 0x40000 + (b[at + 2] | b[at + 3] << 8)));
            else if (op is 0x8A or 0x9A or 0xAA or 0xBA)                                         // JB, JNB, JBC, JNBS bit, rel
                branches.Add((at, next + 2 * (sbyte)b[at + 2]));
            else if (op is 0xFA or 0xDA && b[at + 1] == 0x0C)                                    // JMPS, CALLS within segment 0Ch
                branches.Add((at, 0x40000 + (b[at + 2] | b[at + 3] << 8)));
            at = next;
        }
        Assert.Equal(codeEnd, at);
        foreach (var (from, target) in branches)
        {
            Assert.True(target % 2 == 0, $"0C:{from & 0xFFFF:X4} branches to an odd address");
            if (target >= codeStart && target < codeEnd)
                Assert.True(starts.Contains(target), $"0C:{from & 0xFFFF:X4} branches to 0C:{target & 0xFFFF:X4}, not an instruction's start");
            else if (target >= Block && target < BlockEnd)
                Assert.InRange(target, codeEnd, dataEnd - 2);                                    // the later sections, after the catalogue
        }
    }

    [Fact] public void TheCrcRange_EndsAtTheOverlaysLastByte()
    {
        var b = Flashed(V10Fixture.Image);
        var (_, _, dataEnd) = Extent(b);
        Assert.Equal((uint)(0x0C0000 + (dataEnd - 0x40000) - 1), BitConverter.ToUInt32(b, CrcEnd));
        Assert.Equal((uint)0x0C0000, BitConverter.ToUInt32(b, CrcEnd - 4));                     // range 4 still starts at 0C:0000
    }

    static IReadOnlyList<byte> OneReply(FlashedImage image, string payload)
    {
        var r = DriveRun.Run(image, DriveParser.Parse($"drive one\n3 on\nkline send {payload}\n2 on"));
        var exchange = Assert.Single(r.KLine);
        Assert.Equal(KLineOutcome.Replied, exchange.Outcome);
        return exchange.Reply!;
    }

    [Fact] public void TheHeader_IsReadWithCommand06()
    {
        var reply = OneReply(V10Fixture.Image, "06 00 0C B5 44 10");                              // 16 bytes at 0C:B544
        var header = V10Fixture.Bytes.AsSpan(Block, 16).ToArray();
        Assert.Equal(Convert.ToHexString(Ds2.Frame(new byte[] { 0xA0 }.Concat(header).ToArray())), Convert.ToHexString(reply.ToArray()));
        var car = OneReply(TestImages.Car, "06 00 0C B5 44 10");
        Assert.Equal(Convert.ToHexString(Ds2.Frame(new byte[] { 0xA0 }.Concat(Enumerable.Repeat((byte)0xFF, 16)).ToArray())),
            Convert.ToHexString(car.ToArray()));
    }

    [Fact] public void TheKnownBadBuild_IsNotKnown()
    {
        Assert.Equal("MS43X001", ProgramIdentity.Name(TestImages.Car.Image));
        var old = Overlay.Over(TestBins.CarBytes).Image;                                         // the frozen known-bad build
        Assert.False(ProgramIdentity.HasMs43X001Ram(old));
        Assert.Null(ProgramIdentity.Name(old));
    }

    [Fact, Trait("Category", "StandardDrives")] public void TheOverlayOff_PassesTheWarmStart()
    {
        var r = DriveRun.RunWithChecks(V10Fixture.Image, StandardDrives.Load("warm-start"));
        Assert.True(r.Verdict == RunVerdict.Pass, string.Join("; ", r.Checks.SelectMany(c => c.Findings.Select(f => $"{c.Name}: {f.Message}")))
            + " | not evaluated: " + string.Join("; ", r.Coverage?.NotEvaluated ?? Array.Empty<string>()));
    }
}
