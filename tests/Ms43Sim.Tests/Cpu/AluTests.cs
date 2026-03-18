using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

public class AluTests
{
    [Theory] //  code    R1      R2      PSWin    R1out   PSWout
    [InlineData("0012",  0x7FFF, 0x0001, 0x0000,  0x8000, 0x0005)]  // ADD: N,V
    [InlineData("0012",  0xFFFF, 0x0001, 0x0000,  0x0000, 0x000A)]  // ADD: Z,C
    [InlineData("0012",  0x1234, 0x8000, 0x0000,  0x9234, 0x0011)]  // ADD: E (op2=8000h),N
    [InlineData("2012",  0x0000, 0x0001, 0x0000,  0xFFFF, 0x0003)]  // SUB: borrow C,N
    [InlineData("2012",  0x8000, 0x0001, 0x0000,  0x7FFF, 0x0004)]  // SUB: V
    [InlineData("4012",  0x0005, 0x0005, 0x0000,  0x0005, 0x0008)]  // CMP: Z, R1 unchanged
    [InlineData("1012",  0xFFFF, 0x0000, 0x0002,  0x0000, 0x0002)]  // ADDC: C in, result 0, previous Z=0 → Z=0
    [InlineData("1012",  0xFFFF, 0x0000, 0x000A,  0x0000, 0x000A)]  // ADDC: previous Z=1 → Z=1
    [InlineData("3012",  0x0000, 0x0000, 0x0002,  0xFFFF, 0x0003)]  // SUBC: borrow in
    [InlineData("6012",  0xFFFF, 0x8000, 0x0006,  0x8000, 0x0011)]  // AND: E,N; V,C cleared
    [InlineData("8110",  0x8000, 0x0000, 0x0000,  0x8000, 0x0017)]  // NEG 8000h: E,V,C,N
    [InlineData("8110",  0x0000, 0x0000, 0x0000,  0x0000, 0x0008)]  // NEG 0: Z only
    [InlineData("9110",  0x00FF, 0x0000, 0x0006,  0xFF00, 0x0001)]  // CPL: N; V,C cleared
    public void WordOps(string code, int r1, int r2, int pswIn, int r1Out, int pswOut)
    {
        var t = TestMachine.Create(code).SetR(1, (ushort)r1).SetR(2, (ushort)r2);
        t.Psw = (ushort)pswIn; t.Step();
        Assert.Equal(((ushort)r1Out, (ushort)pswOut), (t.R(1), t.Psw));
    }

    [Fact] public void AddB_ByteOverflowAndByteE()
    {
        var t = TestMachine.Create("0124").SetR(1, 0x127F).SetR(2, 0x0001);   // ADDB RL1,RL2
        t.Step();
        Assert.Equal((0x1280, 0x0005), (t.R(1), t.Psw));
    }

    [Fact] public void CmpiAndCmpd_CompareThenStep()
    {
        var t = TestMachine.Create("8031 B051").SetR(1, 3);
        t.Step(); Assert.Equal((4, 0x0008), (t.R(1), t.Psw));                   // CMPI1 R1,#3
        t.Step(); Assert.Equal((2, 0x0003), (t.R(1), t.Psw));                   // CMPD2 R1,#5: 4-5 borrows
    }

    [Fact] public void AddImmediate16_Form() =>
        Assert.Equal(0x0000, TestMachine.Create("06F1AAAA").SetR(1, 0x5556).Step().R(1));  // the boot's self-test add
}
