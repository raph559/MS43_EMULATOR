using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

public class ShiftMulDivTests
{
    [Theory] //  code    R1      R2     R1out   PSWout
    [InlineData("5C11",  0x8001, 0,     0x0002, 0x0002)]  // SHL #1: C = old MSB
    [InlineData("7C21",  0x0003, 0,     0x0000, 0x000E)]  // SHR #2: C=bit1, V=bit0, Z
    [InlineData("7C21",  0x0002, 0,     0x0000, 0x000A)]  // SHR #2: V=0
    [InlineData("6C12",  0x8001, 0x10,  0x8001, 0x0001)]  // SHR R1,R2 with count 16&15=0: C=V=0
    [InlineData("1C41",  0x1234, 0,     0x2341, 0x0002)]  // ROL #4: C = bit12
    [InlineData("3C41",  0x1234, 0,     0x4123, 0x0004)]  // ROR #4: C=bit3=0, V=bit2=1
    [InlineData("BC11",  0x8001, 0,     0xC000, 0x0003)]  // ASHR #1: N,C
    [InlineData("BC21",  0x8003, 0,     0xE000, 0x0007)]  // ASHR #2: N,V,C
    public void Shifts(string code, int r1, int r2, int r1Out, int pswOut)
    {
        var t = TestMachine.Create(code).SetR(1, (ushort)r1).SetR(2, (ushort)r2);
        t.Step();
        Assert.Equal(((ushort)r1Out, (ushort)pswOut), (t.R(1), t.Psw));
    }

    [Theory] //  code    R1      R2      MDH     MDL     PSWout
    [InlineData("0B12",  0xFFFF, 0x0002, 0xFFFF, 0xFFFE, 0x0001)]  // MUL -1*2
    [InlineData("0B12",  0x4000, 0x0004, 0x0001, 0x0000, 0x0004)]  // MUL: does not fit a word → V
    [InlineData("1B12",  0xFFFF, 0xFFFF, 0xFFFE, 0x0001, 0x0005)]  // MULU
    public void Multiply(string code, int r1, int r2, int mdh, int mdl, int psw)
    {
        var t = TestMachine.Create(code).SetR(1, (ushort)r1).SetR(2, (ushort)r2);
        t.Step();
        Assert.Equal(((ushort)mdh, (ushort)mdl, (ushort)psw), (t.Sfr(0xFE0C), t.Sfr(0xFE0E), t.Psw));
        Assert.Equal(0x0010, t.Sfr(0xFF0E) & 0x0010);   // MDRIU set
    }

    [Theory] //  code    MDH     MDL     R2      MDHout  MDLout  PSWout
    [InlineData("5B22",  0x0000, 0x0064, 7,      0x0002, 0x000E, 0x0000)]  // DIVU 100/7
    [InlineData("4B22",  0x0000, 0xFF9C, 7,      0xFFFE, 0xFFF2, 0x0001)]  // DIV -100/7 [assumption: toward zero]
    [InlineData("5B22",  0x5678, 0x1234, 0,      0x5678, 0x1234, 0x0004)]  // DIVU by 0: V, MD unchanged [assumption]
    [InlineData("7B22",  0x0001, 0x0000, 2,      0x0000, 0x8000, 0x0001)]  // DIVLU 0x10000/2
    public void Divide(string code, int mdh, int mdl, int r2, int mdhOut, int mdlOut, int psw)
    {
        var t = TestMachine.Create(code).SetR(2, (ushort)r2).WriteSfr(0xFE0C, (ushort)mdh).WriteSfr(0xFE0E, (ushort)mdl);
        t.Step();
        Assert.Equal(((ushort)mdhOut, (ushort)mdlOut, (ushort)psw), (t.Sfr(0xFE0C), t.Sfr(0xFE0E), t.Psw));
    }

    [Fact] public void ReadingMdl_ClearsMdriu()
    {
        var t = TestMachine.Create("0B12 F2F30EFE").SetR(1, 2).SetR(2, 3);
        t.Step(2);
        Assert.Equal(6, t.R(3));
        Assert.Equal(0, t.Sfr(0xFF0E) & 0x0010);
    }

    [Theory]
    [InlineData(0x0100, 7, 0x0000)] [InlineData(0x0000, 0, 0x0008)] [InlineData(0x8000, 0, 0x0000)]
    public void Prior(int r2, int r1Out, int psw)
    {
        var t = TestMachine.Create("2B12").SetR(2, (ushort)r2);
        t.Step();
        Assert.Equal(((ushort)r1Out, (ushort)psw), (t.R(1), t.Psw));
    }
}
