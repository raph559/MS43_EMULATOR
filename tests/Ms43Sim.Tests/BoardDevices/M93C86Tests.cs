using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.BoardDevices;

public class M93C86Tests
{
    const uint Sscc = 0xFFB2, Tb = 0xF0B0, Rb = 0xF0B2, Br = 0xF0B4;

    bool chipSelect;

    (TestMachine T, M93C86 E) Eeprom(byte[]? image = null)
    {
        var t = TestMachine.Create("0DFF");
        var e = new M93C86(t.M.Scheduler, () => chipSelect);
        if (image != null)
            e.Load(image);
        t.M.Board.Spi.Attach(e);
        return (t, e);
    }

    void SetCs(TestMachine t, M93C86 e, bool level)
    {
        chipSelect = level;
        e.ChipSelectChanged();
    }

    /// <summary>One 16-bit master transfer at SSCBR 0017h: 768 cycles.</summary>
    static void Transfer(TestMachine t, ushort frame)
    {
        t.WriteSfr(Tb, frame);
        t.M.Scheduler.Advance(t.M.Cycles + 16 * 48);
    }

    /// <summary>SSCCON C23Fh (16 bits, MSB first, SSCPH = 1): CS high, the frames, CS low.</summary>
    void Command(TestMachine t, M93C86 e, params ushort[] frames)
    {
        t.WriteSfr(Br, 0x0017).WriteSfr(Sscc, 0x4000).WriteSfr(Sscc, 0x423F).WriteSfr(Sscc, 0xC23F);
        SetCs(t, e, true);
        foreach (var frame in frames)
            Transfer(t, frame);
        SetCs(t, e, false);
    }

    /// <summary>The firmware's READ: 1800h + a with SSCPH = 1, then SSCPH = 0 and one 0000h frame per word.</summary>
    int[] ReadWords(TestMachine t, M93C86 e, int a, int n)
    {
        t.WriteSfr(Br, 0x0017).WriteSfr(Sscc, 0x4000).WriteSfr(Sscc, 0x423F).WriteSfr(Sscc, 0xC23F);
        SetCs(t, e, true);
        Transfer(t, (ushort)(0x1800 + a));
        t.WriteSfr(Sscc, 0x4000).WriteSfr(Sscc, 0x421F).WriteSfr(Sscc, 0xC21F);
        var words = new int[n];
        for (var i = 0; i < n; i++)
        {
            Transfer(t, 0x0000);
            words[i] = t.M.Sfrs.Read16(Rb);
        }
        SetCs(t, e, false);
        return words;
    }

    int ReadWord(TestMachine t, M93C86 e, int a) => ReadWords(t, e, a, 1)[0];

    [Fact] public void Read_FirmwareFramePair_ReturnsTheWord()
    {
        var image = Enumerable.Repeat((byte)0xFF, 2048).ToArray();
        image[0x32] = 0x34; image[0x33] = 0x33;                           // word 19h = 3334h ("43")
        var (t, e) = Eeprom(image);
        Assert.Equal(0x3334, ReadWord(t, e, 0x19));
        Assert.Equal((1, true), (e.WordsRead, e.AddressesRead.Contains(0x19)));
    }

    [Fact] public void BlankChip_ReadsFfff() { var (t, e) = Eeprom(); Assert.Equal(0xFFFF, ReadWord(t, e, 0x3FF)); }

    [Fact] public void Write_NeedsEwen_ThenIsBusyFor5ms()
    {
        var (t, e) = Eeprom();
        Command(t, e, 0x1300);                                            // EWEN
        Command(t, e, 0x1419, 0xBEEF);                                    // WRITE word 19h; the write starts as CS falls
        var fall = t.M.Cycles;
        SetCs(t, e, true);
        Assert.False(t.M.Board.Spi.MrstLevel);                            // busy
        t.M.Scheduler.Advance(fall + 119_999); Assert.False(t.M.Board.Spi.MrstLevel);
        t.M.Scheduler.Advance(fall + 120_000); Assert.True(t.M.Board.Spi.MrstLevel);
        Assert.Equal((0xBEEF, 1), (e.Word(0x19), e.WordsWritten));
    }

    [Fact] public void Write_WithoutEwen_IsIgnored()
    {
        var (t, e) = Eeprom();
        Command(t, e, 0x1419, 0xBEEF);
        Assert.Equal((0xFFFF, false), (e.Word(0x19), e.Busy));
    }

    [Fact] public void Ewds_DisablesWritesAgain()
    {
        var (t, e) = Eeprom();
        Command(t, e, 0x1300); Command(t, e, 0x1000); Command(t, e, 0x1419, 0xBEEF);
        Assert.Equal(0xFFFF, e.Word(0x19));
    }

    [Fact] public void Read_ContinuesIntoTheNextWord_WhileClockingContinues()
    {
        var image = new byte[2048]; image[0x40] = 0x11; image[0x41] = 0x22; image[0x42] = 0x33; image[0x43] = 0x44;
        var (t, e) = Eeprom(image);
        Assert.Equal(new[] { 0x2211, 0x4433 }, ReadWords(t, e, 0x20, 2));
    }

    [Fact] public void Deselected_ReleasesMrst() { var (t, e) = Eeprom(); Assert.Null(e.Miso); Assert.True(t.M.Board.Spi.MrstLevel); }

    [Theory] [InlineData(1024)] [InlineData(4096)] [InlineData(524288)]
    public void Load_RejectsAnImageThatIsNot2048Bytes(int size)
    {
        var e = new M93C86(new Scheduler(), () => false);
        Assert.Contains("2,048", Assert.Throws<InvalidDataException>(() => e.Load(new byte[size])).Message);
    }

    [Fact] public void Image_RoundTrips()
    {
        var image = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();
        var e = new M93C86(new Scheduler(), () => false); e.Load(image);
        Assert.Equal(image, e.ToImage());
        Assert.Equal(0x0302, e.Word(1));
    }

    [Fact] public void Erase_SetsTheWordToFfff()
    {
        var (t, e) = Eeprom(new byte[2048]);
        Command(t, e, 0x1300); Command(t, e, 0x1C19);                    // EWEN ; ERASE word 19h
        t.M.Scheduler.Advance(t.M.Cycles + M93C86.WriteCycles);
        Assert.Equal((0xFFFF, 0x0000, 1), (e.Word(0x19), e.Word(0x18), e.WordsWritten));
    }

    [Fact] public void Wral_WritesEveryWord_AndEralErasesThem()
    {
        var (t, e) = Eeprom(new byte[2048]);
        Command(t, e, 0x1300); Command(t, e, 0x1100, 0x1234);            // EWEN ; WRAL 1234h
        t.M.Scheduler.Advance(t.M.Cycles + M93C86.WriteCycles);
        Assert.Equal((0x1234, 0x1234), (e.Word(0), e.Word(0x3FF)));
        Command(t, e, 0x1200);                                            // ERAL
        t.M.Scheduler.Advance(t.M.Cycles + M93C86.WriteCycles);
        Assert.Equal((0xFFFF, 0xFFFF, 2048), (e.Word(0), e.Word(0x3FF), e.WordsWritten));
    }

    [Fact] public void Instruction_DuringAWriteCycle_IsIgnored()
    {
        var (t, e) = Eeprom();
        Command(t, e, 0x1300); Command(t, e, 0x1419, 0xBEEF);
        Command(t, e, 0x141A, 0x1234);                                    // clocked while busy
        t.M.Scheduler.Advance(t.M.Cycles + M93C86.WriteCycles);
        Assert.Equal((0xBEEF, 0xFFFF), (e.Word(0x19), e.Word(0x1A)));
    }

    [Fact] public void PowerOn_ClearsWriteEnable_AndKeepsTheWords()
    {
        var (t, e) = Eeprom();
        Command(t, e, 0x1300); Command(t, e, 0x1419, 0xBEEF);
        t.M.Scheduler.Advance(t.M.Cycles + M93C86.WriteCycles);
        e.PowerOn();
        Command(t, e, 0x141A, 0x1234);
        t.M.Scheduler.Advance(t.M.Cycles + M93C86.WriteCycles);
        Assert.Equal((false, 0xBEEF, 0xFFFF), (e.WriteEnabled, e.Word(0x19), e.Word(0x1A)));
    }
}
