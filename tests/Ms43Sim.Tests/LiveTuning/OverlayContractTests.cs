using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>The overlay's K-line contract in C# (plan 6 Task 3): frames, sums and the readers of replies and RAM.</summary>
public class OverlayContractTests
{
    static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    [Fact] public void EachRequest_HasItsLength()
    {
        Assert.Equal(5, Ds2.Frame(LiveOverlay.Status()).Length);
        Assert.Equal(8, Ds2.Frame(LiveOverlay.Load(4, 0x120)).Length);
        Assert.Equal(8 + 3, Ds2.Frame(LiveOverlay.Write(0, 0x013F, new byte[] { 0xAA, 0xBB, 0xCC })).Length);
        Assert.Equal(10, Ds2.Frame(LiveOverlay.On(2, 0x12345678)).Length);
        Assert.Equal(6, Ds2.Frame(LiveOverlay.Off(4)).Length);
        Assert.Equal(5, Ds2.Frame(LiveOverlay.AllOn()).Length);
        Assert.Equal(5, Ds2.Frame(LiveOverlay.AllOff()).Length);
        Assert.Equal(6, Ds2.Frame(LiveOverlay.Free(3)).Length);
        Assert.Equal(5, Ds2.Frame(LiveOverlay.Revert()).Length);
        Assert.Equal("6D00", Hex(LiveOverlay.Status()));
        Assert.Equal("6D01040120", Hex(LiveOverlay.Load(4, 0x120)));                  // big-endian offset
        Assert.Equal("6D0200013FAABBCC", Hex(LiveOverlay.Write(0, 0x013F, new byte[] { 0xAA, 0xBB, 0xCC })));
        Assert.Equal("6D030212345678", Hex(LiveOverlay.On(2, 0x12345678)));
        Assert.Equal("6D0404", Hex(LiveOverlay.Off(4)));
        Assert.Equal("6D05", Hex(LiveOverlay.AllOn()));
        Assert.Equal("6D06", Hex(LiveOverlay.AllOff()));
        Assert.Equal("6D0703", Hex(LiveOverlay.Free(3)));
        Assert.Equal("6D08", Hex(LiveOverlay.Revert()));
    }

    [Fact] public void Write_RefusesNothingOrOver120Bytes()
    {
        Assert.Throws<ArgumentException>(() => LiveOverlay.Write(0, 0, Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => LiveOverlay.Write(0, 0, new byte[121]));
        Assert.Equal(128, Ds2.Frame(LiveOverlay.Write(0, 0, new byte[120])).Length);    // the largest request: 128 bytes
    }

    [Fact] public void Edit_WritesOnlyTheChangedRuns()
    {
        var flash = new byte[320];
        var copy = flash.ToArray();
        copy[10] = copy[11] = copy[12] = 5;                                            // a run of three …
        copy[16] = copy[17] = 7;                                                       // … and one of two, three bytes on
        Assert.Equal(new[] { "6D0200000A050505", "6D020000100707" }, LiveOverlay.Edit(0, copy, flash).Select(Hex));
        var big = flash.ToArray();
        for (var i = 10; i < 310; i++)
            big[i] = 1;                                                                // a 300-byte change
        var writes = LiveOverlay.Edit(0, big, flash);
        Assert.Equal(new[] { 120, 120, 60 }, writes.Select(w => w.Length - 5));        // 6D 02 i at(2), then the data
        Assert.Equal(new[] { "000A", "0082", "00FA" }, writes.Select(w => Hex(w[3..5])));
        Assert.Empty(LiveOverlay.Edit(0, flash, flash));
    }

    [Fact] public void Crc32_MatchesTheStandardVector_AndDetectsBalancedChanges()
    {
        Assert.Equal(0xCBF43926u, LiveOverlay.Crc32("123456789"u8));
        Assert.Equal(0u, LiveOverlay.Crc32(Array.Empty<byte>()));
        var flash = new byte[] { 80, 80, 137, 137 };
        var balanced = new byte[] { 81, 79, 138, 136 };
        Assert.Equal(flash.Sum(b => b), balanced.Sum(b => b));
        Assert.NotEqual(LiveOverlay.Crc32(flash), LiveOverlay.Crc32(balanced));
        Assert.NotEqual(LiveOverlay.Crc32(new byte[] { 80, 81 }), LiveOverlay.Crc32(new byte[] { 81, 80 }));
    }

    [Fact] public void ParseStatus_ReadsEveryField()
    {
        var payload = new List<byte> { (byte)'L', (byte)'T', (byte)'O', (byte)'V', 2, 0b01 };
        payload.AddRange(new byte[] { 3, 0, 0x00, 0x24, 0x12, 0x34, 0x56, 0x78 });                  // table 0: on at 24h, sum 48
        payload.AddRange(new byte[] { 2, 0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });                  // table 1: ready at 0
        for (var i = 2; i < 5; i++)
            payload.AddRange(new byte[8]);
        payload.AddRange(new byte[] { 0x01, 0x50 });                                    // free 336
        var frame = Ds2.Frame(new byte[] { 0xA0 }.Concat(payload).ToArray());
        Assert.Equal(52, frame.Length);
        var s = LiveOverlay.ParseStatus(frame);
        Assert.Equal((2, true, false), (s.Version, s.Running, s.RicherOnly));
        Assert.Equal(new OverlayEntry(OverlayState.On, 0x24, 0x12345678u), s.Tables[0]);
        Assert.Equal(new OverlayEntry(OverlayState.Ready, 0, 0), s.Tables[1]);
        Assert.Equal(5, s.Tables.Count);
        Assert.Equal(336, s.FreeBytes);
        Assert.Throws<InvalidDataException>(() => LiveOverlay.ParseStatus(Ds2.Frame(new byte[] { 0xB0, 0x01 })));
    }

    [Fact] public void Refusal_ReadsTheReason()
    {
        Assert.Equal(OverlayReason.Overlap, LiveOverlay.Refusal(Ds2.Frame(new byte[] { 0xB0, 0x05 })));
        Assert.Equal(OverlayReason.Leaner, LiveOverlay.Refusal(Ds2.Frame(new byte[] { 0xB1, 0x0A })));
        Assert.Null(LiveOverlay.Refusal(Ds2.Frame(new byte[] { 0xA0 })));
    }

    [Fact] public void ReadWindow_ReadsTheControlBlockAndTheCopies()
    {
        var ram = new byte[0x2000];
        new byte[] { 0xD2, 0xC0, 0x34, 0x12 }.CopyTo(ram, 0x1D00);                     // magic, seen 1234h
        new byte[] { 3, 0, 0x24, 0x00, 0x78, 0x56, 0x34, 0x12 }.CopyTo(ram, 0x1D08 + 8 * 2);       // table 2 on at 24h, sum 48
        ram[0x1D30] = 0x77;
        ram[0x1FDF] = 0x88;
        var w = LiveOverlay.ReadWindow(ram);
        Assert.Equal((true, 0x1234), (w.Initialised, w.Seen));
        Assert.Equal(5, w.Tables.Count);
        Assert.Equal(new OverlayEntry(OverlayState.On, 0x24, 0x12345678u), w.Tables[2]);
        Assert.Equal(new OverlayEntry(OverlayState.Free, 0, 0), w.Tables[0]);
        Assert.Equal((688, 0x77, 0x88), (w.Copies.Length, w.Copies[0], w.Copies[^1]));
        ram[0x1D01] = 0xC1;
        Assert.False(LiveOverlay.ReadWindow(ram).Initialised);
    }

    [Fact] public void KLineSend_IsADriveLine() => Assert.Equal("kline send 6D 01 04 01 20", LiveOverlay.KLineSend(LiveOverlay.Load(4, 0x120)));

    [Fact] public void TheTables_MatchTheCarFilesSites()
    {
        var car = TestBins.CarBytes;
        Assert.Equal(new[] { "ign98_pl", "ign98_is", "af98", "cam_in", "cam_ex" }, LiveOverlay.Tables.Select(t => t.Name));
        foreach (var t in LiveOverlay.Tables)
        {
            Assert.Equal(t.FileOffset, t.Page * 0x4000 + t.PageOffset);
            foreach (var site in t.Sites)
                Assert.Equal($"E6FC{t.PageOffset & 0xFF:X2}{t.PageOffset >> 8:X2}E6FD{t.Page:X2}00DA0D9A02", Convert.ToHexString(car, site - 8, 12));
        }
        Assert.Equal(new[] { 320, 36, 192, 192, 192 }, LiveOverlay.Tables.Select(t => t.Bytes));
    }
}
