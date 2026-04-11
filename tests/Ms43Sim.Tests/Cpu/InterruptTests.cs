using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

public class InterruptTests
{
    [Fact] public void Accepts_PushesState_SetsIlvl_ClearsIr()
    {
        var t = TestMachine.Create("CC00 CC00", (0x84, "FB88")).WriteSfr(0xFF9E, 0x00CC);  // T1IC: IR|IE|ILVL3
        t.Psw = 0x0800;                                              // IEN
        t.Step();                                                    // first boundary: interrupt entry
        Assert.Equal((0x0084, 0x3800, 0x004C), (t.Cpu.IP, t.Psw, t.Sfr(0xFF9E)));
        Assert.Equal((0x1000, 0x0000, 0x0800), (t.Peek16(0xFBFA), t.Peek16(0xFBFC), t.Peek16(0xFBFE)));
        t.Step();                                                    // RETI
        Assert.Equal((0x1000, 0x0800), (t.Cpu.IP, t.Psw));
    }

    [Theory]
    [InlineData(0x0000, 0x00CC)]   // IEN=0
    [InlineData(0x0800, 0x008C)]   // IE=0
    [InlineData(0x3800, 0x00CC)]   // CPU already at level 3
    [InlineData(0x0800, 0x00C0)]   // level 0 never serviced
    public void NotAccepted(int psw, int t1ic)
    {
        var t = TestMachine.Create("CC00 CC00 CC00").WriteSfr(0xFF9E, (ushort)t1ic);
        t.Psw = (ushort)psw; t.Step(3);
        Assert.Equal(0x1006, t.Cpu.IP);
    }

    [Fact] public void HigherLevelWins()
    {
        var t = TestMachine.Create("CC00").WriteSfr(0xFF9E, 0x00CC).WriteSfr(0xFF98, 0x00D4);  // T1 level 3, ADC level 5
        t.Psw = 0x0800; t.Step();
        Assert.Equal(0x00A0, t.Cpu.IP);
    }

    [Fact] public void AtomicHoldsOffTheInterrupt()
    {
        // ATOMIC #3, then BSET T1IC.7 raises the request inside the protected range
        var t = TestMachine.Create("D120 7FCF CC00 CC00 CC00", (0x84, "FB88")).WriteSfr(0xFF9E, 0x004C);
        t.Psw = 0x0800; t.Step(4);
        Assert.Equal(0x1008, t.Cpu.IP);                              // range done; not yet entered
        t.Step();
        Assert.Equal(0x0084, t.Cpu.IP);
    }

    [Fact] public void SoftwareSetIr_Triggers()
    {
        var t = TestMachine.Create("7FCF CC00").WriteSfr(0xFF9E, 0x004C);  // BSET T1IC.7
        t.Psw = 0x0800; t.Step(2);
        Assert.Equal(0x0084, t.Cpu.IP);
    }

    [Fact] public void IcUpperByte_ReadsZero() =>
        Assert.Equal(0x00FF, TestMachine.Create("CC00").WriteSfr(0xFF9E, 0xFFFF).M.Sfrs.Read16(0xFF9E));

    [Fact] public void SourceTable_Anchors()
    {
        Assert.Equal(56, InterruptSources.All.Count);
        Assert.Equal((0xFF9Eu, (byte)0x21), (InterruptSources.ByName("T1").IcAddress, InterruptSources.ByName("T1").TrapNumber));
        Assert.Equal((0xF19Cu, (byte)0x47), (InterruptSources.ByName("S0TB").IcAddress, InterruptSources.ByName("S0TB").TrapNumber));
        Assert.Equal((0xF186u, (byte)0x40), (InterruptSources.ByName("XP0").IcAddress, InterruptSources.ByName("XP0").TrapNumber));
        Assert.Equal((0xFF78u, (byte)0x10), (InterruptSources.ByName("CC0").IcAddress, InterruptSources.ByName("CC0").TrapNumber));
    }
}

public class PecTests
{
    [Fact] public void ContinuousWordTransfer_NoStackNoIlvl()
    {
        var t = TestMachine.Create("CC00 CC00").WriteSfr(0xFEC0, 0x00FF)        // PECC0: word, no INC, COUNT=FF
            .Poke16(0xFCE0, 0xF900).Poke16(0xFCE2, 0xF902).Poke16(0xF900, 0xBEEF)
            .WriteSfr(0xFF98, 0x00F8);                                          // ADCIC: IR|IE|ILVL14|GLVL0
        t.Psw = 0x0800; t.Step(2);
        Assert.Equal((0xBEEF, 0x0078, 0x00FF, 0xFC00), (t.Peek16(0xF902), t.Sfr(0xFF98), t.Sfr(0xFEC0), t.Cpu.Sp));
    }

    [Fact] public void CountOne_LeavesIrSet_ThenInterrupts()
    {
        var t = TestMachine.Create("CC00 CC00 CC00").WriteSfr(0xFEC0, 0x0001)
            .Poke16(0xFCE0, 0xF900).Poke16(0xFCE2, 0xF902).WriteSfr(0xFF98, 0x00F8);
        t.Psw = 0x0800; t.Step(2);                                   // PEC transfer, then the level-14 interrupt
        Assert.Equal((0x0000, 0x00A0), (t.Sfr(0xFEC0), t.Cpu.IP));
    }

    [Fact] public void TransferThatRaisesTheSameRequest_KeepsItPending()
    {
        // The PEC acknowledges the request before it moves the data, so a request the transfer
        // itself raises (a write to SSCTB sets SSCTIR) is not lost. Here the transfer writes
        // F8h (IR|IE|ILVL14) into ADCIC.
        var t = TestMachine.Create("CC00 CC00").WriteSfr(0xFEC0, 0x0002)        // word, no INC, COUNT=2
            .Poke16(0xFCE0, 0xF900).Poke16(0xFCE2, 0xFF98).Poke16(0xF900, 0x00F8).WriteSfr(0xFF98, 0x00F8);
        t.Psw = 0x0800; t.Step();
        Assert.Equal((0x00F8, 0x0001), (t.Sfr(0xFF98), t.Sfr(0xFEC0)));
    }

    [Fact] public void ByteTransfer_IncrementsSource()
    {
        var t = TestMachine.Create("CC00 CC00").WriteSfr(0xFEC0, 0x0502)        // INC=10, BWT=1, COUNT=2
            .Poke16(0xFCE0, 0xF900).Poke16(0xFCE2, 0xF910).Poke16(0xF900, 0x3412).WriteSfr(0xFF98, 0x00F8);
        t.Psw = 0x0800; t.Step(2);
        Assert.Equal((0x12, 0xF901, 0x0501), (t.Peek16(0xF910) & 0xFF, t.Peek16(0xFCE0), t.Sfr(0xFEC0)));
    }
}
