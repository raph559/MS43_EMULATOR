using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

/// <summary>ALU instructions on SFR operands: one read of op1, and CMP stores nothing (ISM: CMP is (op1) - (op2)).</summary>
public class SfrOperandTests
{
    [Fact] public void Cmp_OnMdl_DoesNotWriteBack_SoMdriuStaysCleared()
    {
        var t = TestMachine.Create("1B12 46070000").SetR(1, 2).SetR(2, 3);   // MULU R1,R2 ; CMP MDL,#0
        t.Step(2);
        Assert.Equal(0, t.Sfr(0xFF0E) & 0x0010);
    }

    [Fact] public void AluOnUnmodelledSfr_ReadsOnce_AndCmpNeverWrites()
    {
        var t = TestMachine.Create("46800000 06800100");                  // CMP P0L,#0 ; ADD P0L,#1
        t.Step(2);
        var p0l = t.M.Inventory.Records.Where(r => r.Address == 0xFF00).ToList();
        Assert.Equal(2, p0l.Single(r => r.Kind == AccessKind.Read).Count);  // one read per instruction
        Assert.Equal(1, p0l.Single(r => r.Kind == AccessKind.Write).Count); // only the ADD writes
    }

    [Fact] public void CmpPollOnTimer_DoesNotRestartTheTimer()
    {
        // T01CON := 0047h (T0 running at fCPU/1024) ; loop: CMP T0,#5 ; JMPR cc_C,loop ; NOP
        var t = TestMachine.Create("E6A84700 46280500 8DFD CC00");
        t.Step();
        t.M.Run(20_000);                                                  // T0 reaches 5 after 5,120 cycles
        Assert.True(t.M.Capcom.T0 >= 5, $"T0 = {t.M.Capcom.T0}");
    }
}
