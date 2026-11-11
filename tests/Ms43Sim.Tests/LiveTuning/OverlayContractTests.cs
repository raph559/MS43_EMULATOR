using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>The overlay's request builders: frames, the CRC-32 and the refusal reader.</summary>
public class OverlayContractTests
{
    static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    [Fact] public void EachRequest_HasItsLength()
    {
        Assert.Equal(8, Ds2.Frame(LiveOverlay.Load(4, 0x120)).Length);
        Assert.Equal(8 + 3, Ds2.Frame(LiveOverlay.Write(0, 0x013F, new byte[] { 0xAA, 0xBB, 0xCC })).Length);
        Assert.Equal(10, Ds2.Frame(LiveOverlay.On(2, 0x12345678)).Length);
        Assert.Equal(6, Ds2.Frame(LiveOverlay.Off(4)).Length);
        Assert.Equal(5, Ds2.Frame(LiveOverlay.AllOn()).Length);
        Assert.Equal(5, Ds2.Frame(LiveOverlay.AllOff()).Length);
        Assert.Equal(6, Ds2.Frame(LiveOverlay.Free(3)).Length);
        Assert.Equal(5, Ds2.Frame(LiveOverlay.Revert()).Length);
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

    [Fact] public void Refusal_ReadsTheReason()
    {
        Assert.Equal(OverlayReason.Overlap, LiveOverlay.Refusal(Ds2.Frame(new byte[] { 0xB0, 0x05 })));
        Assert.Equal(OverlayReason.Leaner, LiveOverlay.Refusal(Ds2.Frame(new byte[] { 0xB1, 0x0A })));
        Assert.Null(LiveOverlay.Refusal(Ds2.Frame(new byte[] { 0xA0 })));
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
