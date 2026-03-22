using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

public class TimingTests
{
    static int Cost(string code, Action<TestMachine>? setup = null)
    {
        var t = TestMachine.Create("E686BF45 " + code);              // MOV BUSCON0,#45BFh first
        setup?.Invoke(t);
        t.Step();
        var before = t.M.Cycles; t.Step();
        return (int)(t.M.Cycles - before);
    }

    [Theory]
    [InlineData("F012", 4)]        // MOV R1,R2: 2 + 1 ACT
    [InlineData("E6F13412", 6)]    // MOV R1,#1234h: 2 + 2 ACT
    [InlineData("0B12", 12)]       // MUL: 10 + 1 ACT
    [InlineData("5B22", 22)]       // DIVU: 20 + 1 ACT
    [InlineData("0D00", 6)]        // JMPR cc_UC (taken): 4 + 1 ACT
    [InlineData("2D00", 4)]        // JMPR cc_Z, not taken (Z=0): 2 + 1 ACT
    [InlineData("F2F10038", 8)]    // MOV R1,3800h: flash operand: 2 + 2 ACT + 1 ACT
    public void FromFlash(string code, int states) => Assert.Equal(states, Cost(code));

    [Fact] public void IramOperand_AddsNothing() =>
        Assert.Equal(4, Cost("A812", t => t.SetR(2, 0xF800)));       // MOV R1,[R2] → IRAM

    [Fact] public void ResetBuscon0_Is18StatesPerWord() =>
        Assert.Equal(20, (int)TestMachine.Create("F012").Step().M.Cycles);  // 2 + (2+15+1) [assumption]
}
