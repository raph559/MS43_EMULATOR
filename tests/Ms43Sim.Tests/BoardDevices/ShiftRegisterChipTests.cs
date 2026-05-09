using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.BoardDevices;

public class ShiftRegisterChipTests
{
    const uint Sscc = 0xFFB2, Tb = 0xF0B0, Rb = 0xF0B2, Br = 0xF0B4;

    /// <summary>A chip-select line the test drives; it tells the chip about each change.</summary>
    sealed class Flag
    {
        public bool Value;
        public Action Changed = () => { };

        public void Set(bool value)
        {
            Value = value;
            Changed();
        }
    }

    static (TestMachine T, Flag Cs, ShiftRegisterChip Chip) Harness(Func<Func<bool>, ShiftRegisterChip> factory)
    {
        var t = TestMachine.Create("0DFF");
        var cs = new Flag();
        var chip = factory(() => cs.Value);
        cs.Changed = chip.ChipSelectChanged;
        t.M.Board.Spi.Attach(chip);
        return (t, cs, chip);
    }

    /// <summary>8-bit master frames, SSCCON C617h (MSB first, SSCPH = 0, SSCPO = 0), SSCBR 0017h: 384 cycles each.</summary>
    static int[] Frames(TestMachine t, int[] bytes)
    {
        t.WriteSfr(Br, 0x0017).WriteSfr(Sscc, 0x4000).WriteSfr(Sscc, 0xC617);
        var received = new int[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            t.WriteSfr(Tb, (ushort)bytes[i]);
            t.M.Scheduler.Advance(t.M.Cycles + 384);
            received[i] = t.M.Sfrs.Read16(Rb) & 0xFF;
        }
        return received;
    }

    static int[] Job(TestMachine t, Flag cs, params int[] bytes)
    {
        cs.Set(true);
        var received = Frames(t, bytes);
        cs.Set(false);
        return received;
    }

    static int[] JobWithoutSelect(TestMachine t, params int[] bytes) => Frames(t, bytes);

    [Fact] public void ChipA_ReturnsTheComplementOfItsLastCommand_ThenTheA6Echo()
    {
        var (t, cs, chip) = Harness(c => ShiftRegisterChip.ChipA(c));
        Assert.Equal(new[] { 0xFF, 0xFF, 0xA6 }, Job(t, cs, 0xA6, 0x34, 0x12));
        Assert.Equal(0x3412UL, chip.Latched);
        Assert.Equal(new[] { 0xCB, 0xED, 0xA6 }, Job(t, cs, 0xA6, 0x56, 0x78));
    }

    [Fact] public void ChipB_HeldOffByTheModule_AnswersOnlyFf()
    {
        var (t, cs, _) = Harness(c => ShiftRegisterChip.ChipB(c, () => false, () => 0xFFFF_FFFFu));
        Assert.Equal(new[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, Job(t, cs, 0x96, 0xFF, 0xFF, 0xFF, 0xFF));
    }

    [Fact] public void ChipB_Released_AnswersNoFault_Then96()
    {
        var (t, cs, _) = Harness(c => ShiftRegisterChip.ChipB(c, () => true, () => 0xFFFF_FFFFu));
        Assert.Equal(new[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x96 }, Job(t, cs, 0x96, 0xFF, 0xFF, 0xFF, 0xFF));
    }

    [Fact] public void UnselectedChip_IgnoresTheClock()
    {
        var (t, _, chip) = Harness(c => ShiftRegisterChip.ChipA(c));
        JobWithoutSelect(t, 0xA6, 0x34, 0x12);
        Assert.Equal((0UL, 0), (chip.Latched, chip.Transfers));
    }

    [Fact] public void PowerOn_ForgetsTheLatchedCommand()
    {
        var (t, cs, chip) = Harness(c => ShiftRegisterChip.ChipA(c));
        Job(t, cs, 0xA6, 0x34, 0x12);
        chip.PowerOn();
        Assert.Equal(0UL, chip.Latched);
        Assert.Equal(new[] { 0xFF, 0xFF, 0xA6 }, Job(t, cs, 0xA6, 0x56, 0x78));
    }

    [Fact] public void ChipB_ShiftsOutItsStatusWord_D0First()
    {
        var (t, cs, _) = Harness(c => ShiftRegisterChip.ChipB(c, () => true, () => BoardInputs.HeatersOpen));
        Assert.Equal(new[] { 0xFF, 0xFF, 0xFA, 0xFA, 0x96 }, Job(t, cs, 0x96, 0xFF, 0xFF, 0xFF, 0xFF));
    }
}
