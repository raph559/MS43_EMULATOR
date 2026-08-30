using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The overlay bin's invariants (plan 6 Task 4; overlay design §8 item 9): it changes only its write list, the boot block,
/// the tags, the signature and the calibration stay the car's, its checksums verify, every hook calls segment 0Ch at an even
/// address inside its code, the code decodes whole, the program CRC covers it, and the checks know its program.
/// </summary>
public class OverlayImageTests
{
    static readonly int[] Sites = { 0x2C650, 0x2C3A4, 0x3D40A, 0x33B48, 0x33BA2, 0x33B74, 0x33BEA, 0x1B12A };
    const int KSite = 0x4AB28, Slot = 0x1FC8A, TaskEntry = 0x6E3E2, CrcEnd = 0x6FE00, Block = 0x4B544;

    /// <summary>The header at 0C:B544: LTOV, the version, then the code's start and end and the data's end in segment 0Ch.</summary>
    static (int CodeStart, int CodeEnd, int DataEnd) Extent(byte[] b)
    {
        Assert.Equal("LTOV", System.Text.Encoding.ASCII.GetString(b, Block, 4));
        int Word(int at) => b[at] | b[at + 1] << 8;
        Assert.Equal(1, Word(Block + 4));
        Assert.Equal((6, 0), (Word(Block + 12), Word(Block + 14)));
        return (0x40000 + Word(Block + 6), 0x40000 + Word(Block + 8), 0x40000 + Word(Block + 10));
    }

    static byte[] Flashed(FlashedImage image) => image.Image.ToArray();

    [Fact] public void TheOverlay_ChangesOnlyItsWriteList()
    {
        var overlay = Flashed(LiveOverlayBin.Image);
        var car = Flashed(TestImages.Car);
        var (_, _, dataEnd) = Extent(overlay);
        var hooks = Sites.Concat(new[] { KSite, Slot, TaskEntry, CrcEnd }).ToArray();
        var allowed = hooks.SelectMany(s => Enumerable.Range(s, 4))
            .Concat(Enumerable.Range(Block, dataEnd - Block))
            .Concat(Checksums.StoredFields.SelectMany(f => Enumerable.Range(f.Offset, f.Bytes))).ToHashSet();
        var changed = Enumerable.Range(0, FirmwareImage.Size).Where(i => overlay[i] != car[i]).ToList();
        Assert.Empty(changed.Where(i => !allowed.Contains(i)).Select(i => i.ToString("X5")));
        Assert.All(hooks, s => Assert.False(overlay.AsSpan(s, 4).SequenceEqual(car.AsSpan(s, 4)), $"0x{s:X5} is not patched"));
    }

    [Fact] public void TheBootBlock_TheTags_TheSignature_AndTheCalibration_AreUntouched()
    {
        var overlay = Flashed(LiveOverlayBin.Image);
        var car = Flashed(TestImages.Car);
        foreach (var (start, end) in new[] { (0x00000, 0x08000), (0x6FFF0, 0x70000), (0x7FFF0, 0x80000), (0x4FFE6, 0x50000), (0x70000, 0x80000) })
            Assert.True(overlay.AsSpan(start..end).SequenceEqual(car.AsSpan(start..end)), $"0x{start:X5}–0x{end - 1:X5} differs");
        Assert.All(LiveOverlayBin.Records(), r => Assert.True(r.Offset + r.Bytes.Length <= 0x70000, $"a record at 0x{r.Offset:X5} reaches the calibration"));
    }

    [Fact] public void AllFiveChecksums_Verify() =>
        Assert.All(Checksums.Verify(LiveOverlayBin.Image.Image.Bytes), c => Assert.True(c.Ok, $"{c.Kind}: stored {c.Stored:X}, computed {c.Computed:X} {c.Problem}"));

    [Fact] public void EveryHook_CallsSegment0C_AtAnEvenAddress_InsideTheCode()
    {
        var b = LiveOverlayBin.Bytes;
        var (codeStart, codeEnd, _) = Extent(b);
        void Inside(int target, string what)
        {
            Assert.True(target % 2 == 0, $"{what} → 0C:{target & 0xFFFF:X4} is odd");
            Assert.InRange(target, codeStart, codeEnd - 2);
        }
        foreach (var site in Sites.Append(KSite).Append(Slot))
        {
            Assert.Equal((0xDA, 0x0C), (b[site], b[site + 1]));                                   // CALLS 0Ch, …
            Inside(0x40000 + (b[site + 2] | b[site + 3] << 8), $"0x{site:X5}");
        }
        Assert.Equal((0x0C, 0x00), (b[TaskEntry + 2], b[TaskEntry + 3]));                        // queue-1 task 09h: …, 000Ch
        Inside(0x40000 + (b[TaskEntry] | b[TaskEntry + 1] << 8), "queue-1 task 09h");
    }

    [Fact] public void TheCode_DecodesWhole()
    {
        var b = LiveOverlayBin.Bytes;
        var (codeStart, codeEnd, _) = Extent(b);
        var starts = new HashSet<int>();
        var branches = new List<(int At, string Kind, int Target)>();
        var at = codeStart;
        while (at < codeEnd)
        {
            var d = Decoder.Decode(b, at, (uint)(0x80000 + at));
            Assert.False(d.Undefined, $"0C:{at & 0xFFFF:X4} does not decode");
            Assert.True(at % 2 == 0, $"0C:{at & 0xFFFF:X4} is odd");
            starts.Add(at);
            var next = at + d.Size;
            var op = b[at];
            if ((op & 0x0F) == 0x0D)                                                             // JMPR cc, rel
                branches.Add((at, "near", next + 2 * (sbyte)b[at + 1]));
            else if (op == 0xBB)                                                                 // CALLR rel
                branches.Add((at, "near", next + 2 * (sbyte)b[at + 1]));
            else if (op is 0xEA or 0xCA)                                                         // JMPA, CALLA cc, caddr
                branches.Add((at, "near", 0x40000 + (b[at + 2] | b[at + 3] << 8)));
            else if (op is 0x8A or 0x9A or 0xAA or 0xBA)                                         // JB, JNB, JBC, JNBS bit, rel
                branches.Add((at, "near", next + 2 * (sbyte)b[at + 2]));
            else if (op is 0xFA or 0xDA)                                                         // JMPS, CALLS seg, caddr
                branches.Add((at, "far", b[at + 1] << 16 | b[at + 2] | b[at + 3] << 8));
            else
                Assert.False(op is 0x9C or 0xAB, $"0C:{at & 0xFFFF:X4}: an indirect branch, whose target cannot be checked");
            at = next;
        }
        Assert.Equal(codeEnd, at);
        var exits = new HashSet<int> { 0x0D029A, 0x0D0168, 0x0D80DE };
        foreach (var (from, kind, target) in branches)
        {
            if (kind == "far" && exits.Contains(target))
                continue;
            var file = kind == "far" ? (target & 0x7FFFF) : target;
            Assert.True(kind == "near" || target >> 16 == 0x0C, $"0C:{from & 0xFFFF:X4} jumps far to {target:X6}");
            Assert.True(starts.Contains(file), $"0C:{from & 0xFFFF:X4} branches to 0C:{file & 0xFFFF:X4}, not an instruction's start");
        }
    }

    [Fact] public void TheCrcRange_EndsAtTheOverlaysLastByte()
    {
        var b = Flashed(LiveOverlayBin.Image);
        var (_, _, dataEnd) = Extent(b);
        Assert.Equal((uint)(0x0C0000 + (dataEnd - 0x40000) - 1), BitConverter.ToUInt32(b, CrcEnd));
        Assert.Equal((uint)0x0C0000, BitConverter.ToUInt32(b, CrcEnd - 4));                     // range 4 still starts at 0C:0000
    }

    [Fact] public void TheIdentity_IsPinned_AndKnown()
    {
        var overlay = LiveOverlayBin.Image.Image;
        Assert.Equal(ProgramIdentity.Ms43X001LiveOverlay, ProgramIdentity.Of(overlay));
        Assert.True(ProgramIdentity.HasMs43X001Ram(overlay));
        Assert.Equal("MS43X001 with the live-tuning overlay 1", ProgramIdentity.Name(overlay));
        Assert.Equal("MS43X001", ProgramIdentity.Name(TestImages.Car.Image));
        var old = Overlay.Over(TestBins.CarBytes).Image;                                         // the frozen known-bad build
        Assert.False(ProgramIdentity.HasMs43X001Ram(old));
        Assert.Null(ProgramIdentity.Name(old));
    }

    [AslFact] public void TheFixture_IsTheSourcesAssembly()
    {
        var source = File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot, "patches", "live_overlay.asm"));
        var assembled = LiveOverlayBin.Parse(Asl.Assemble(source));
        Assert.Equal(LiveOverlayBin.Records().Select(r => (r.Offset, Convert.ToHexString(r.Bytes))),
            assembled.Select(r => (r.Offset, Convert.ToHexString(r.Bytes))));
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
        var reply = OneReply(LiveOverlayBin.Image, "06 00 0C B5 44 10");                          // 16 bytes at 0C:B544
        var header = LiveOverlayBin.Bytes.AsSpan(Block, 16).ToArray();
        Assert.Equal(Convert.ToHexString(Ds2.Frame(new byte[] { 0xA0 }.Concat(header).ToArray())), Convert.ToHexString(reply.ToArray()));
        var car = OneReply(TestImages.Car, "06 00 0C B5 44 10");
        Assert.Equal(Convert.ToHexString(Ds2.Frame(new byte[] { 0xA0 }.Concat(Enumerable.Repeat((byte)0xFF, 16)).ToArray())),
            Convert.ToHexString(car.ToArray()));
    }

    [Fact] public void Status_AnswersOnAKeyOnRun()
    {
        var s = LiveOverlay.ParseStatus(OneReply(LiveOverlayBin.Image, "6D 00"));
        Assert.Equal((1, false, true, true), (s.Version, s.Running, s.RicherOnly, s.PedalLive));
        Assert.Equal((2381, 0, 2381, 0), ((int)s.KNow, (int)s.KEdit, (int)s.KStartUp, (int)s.KState));
        Assert.All(s.Tables, t => Assert.Equal(new OverlayEntry(OverlayState.Free, 0, 0), t));
        Assert.Equal(692, s.FreeBytes);
    }

    [Fact] public void AnyOtherSub_IsRefused_ForNow() =>                                        // Tasks 5–8 replace it
        Assert.Equal(OverlayReason.Sub, LiveOverlay.Refusal(OneReply(LiveOverlayBin.Image, "6D 01 00 00 00")));

    [Fact, Trait("Category", "StandardDrives")] public void TheOverlayOff_PassesTheWarmStart()
    {
        var r = DriveRun.RunWithChecks(LiveOverlayBin.Image, StandardDrives.Load("warm-start"));
        Assert.True(r.Verdict == RunVerdict.Pass, string.Join("; ", r.Checks.SelectMany(c => c.Findings.Select(f => $"{c.Name}: {f.Message}")))
            + " | not evaluated: " + string.Join("; ", r.Coverage?.NotEvaluated ?? Array.Empty<string>()));
    }
}
