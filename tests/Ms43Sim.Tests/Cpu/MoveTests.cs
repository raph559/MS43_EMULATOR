using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

public class MoveTests
{
    [Theory] //  code         preset R1  PSW in   → R1 / Rx      PSW out
    [InlineData("E6F10080",   0x0000,    0x0006,   1, 0x8000,    0x0017)]   // MOV R1,#8000h: E,N set; C,V kept
    [InlineData("E002",       0x0000,    0x0006,   2, 0x0000,    0x000E)]   // MOV R2,#0: Z set; C,V kept
    [InlineData("E7F28000",   0x1234,    0x0000,   1, 0x1280,    0x0011)]   // MOVB RL1,#80h: high byte kept; E,N
    [InlineData("C033",       0x8012,    0x0000,   3, 0x0080,    0x0000)]   // MOVBZ R3,RH1
    [InlineData("D033",       0x8012,    0x0000,   3, 0xFF80,    0x0001)]   // MOVBS R3,RH1
    public void Moves(string code, int r1, int pswIn, int reg, int expected, int pswOut)
    {
        var t = TestMachine.Create(code).SetR(1, (ushort)r1);
        t.Psw = (ushort)pswIn;
        t.Step();
        Assert.Equal((ushort)expected, t.R(reg));
        Assert.Equal((ushort)pswOut, t.Psw);
    }

    [Fact]
    public void Indirect_PostIncrement_PreDecrement_Displacement()
    {
        var t = TestMachine.Create("9842 8852 D4621000").SetR(2, 0xF800).Poke16(0xF800, 0xBEEF).Poke16(0xF810, 0x0042);
        t.Step();                                     // MOV R4,[R2+]
        Assert.Equal((0xBEEF, 0xF802), (t.R(4), t.R(2)));
        t.SetR(5, 0x1111).Step();                     // MOV [-R2],R5
        Assert.Equal((0xF800, 0x1111), (t.R(2), t.Peek16(0xF800)));
        t.Step();                                     // MOV R6,[R2+#0010h]
        Assert.Equal(0x0042, t.R(6));
    }

    [Fact]
    public void ExplicitPswWrite_Wins()
    {
        var t = TestMachine.Create("E6880300 6F88");
        t.Step(); Assert.Equal(0x0003, t.Psw);        // MOV PSW,#3: no implicit N/Z/E
        t.Psw = 0x0008; t.Step();                     // BSET PSW.6
        Assert.Equal(0x0048, t.Psw);
    }

    [Fact]
    public void LongAddress_UsesDpp()
    {
        var t = TestMachine.Create("F2F10038 E6000300 F2F20038").Poke16(0xF800, 0x1234);
        t.Step();                                     // MOV R1,3800h with DPP0=0 → flash 0x3800 = FFFF
        Assert.Equal(0xFFFF, t.R(1));
        t.Step(); t.Step();                           // MOV DPP0,#3 ; MOV R2,3800h → 0x00F800
        Assert.Equal(0x1234, t.R(2));
    }

    [Fact]
    public void GprBank_FollowsCp()
    {
        var t = TestMachine.Create("CC00");
        t.Poke16(0xFC00, 0x1111).Poke16(0xF800, 0x2222);
        Assert.Equal(0x1111, t.R(0));
        t.WriteSfr(0xFE10, 0xF800);                   // CP := F800h
        Assert.Equal(0x2222, t.R(0));
    }
}
