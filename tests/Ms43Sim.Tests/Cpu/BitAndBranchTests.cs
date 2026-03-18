using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

public class BitAndBranchTests
{
    [Theory] //  code        R1in    R2in    R1out   PSWout
    [InlineData("3FF1",      0x0000, 0,      0x0008, 0x0008)]   // BSET R1.3: old 0 → Z
    [InlineData("3EF1",      0x0008, 0,      0x0000, 0x0001)]   // BCLR R1.3: old 1 → N
    [InlineData("4AF2F1F0",  0x0000, 0x8000, 0x0001, 0x0001)]   // BMOV R1.0,R2.15
    [InlineData("3AF2F1F0",  0x0001, 0x8000, 0x0000, 0x0001)]   // BMOVN R1.0,R2.15
    [InlineData("6AF2F1F0",  0x0001, 0x8000, 0x0001, 0x0006)]   // BAND: V,C
    [InlineData("2AF2F1F0",  0x0000, 0x8000, 0x0000, 0x0005)]   // BCMP: V,N (XOR) ; no write
    [InlineData("0AF10F05",  0xABFF, 0,      0xABF5, 0x0001)]   // BFLDL mask 0F data 05
    [InlineData("1AF130F0",  0xABF5, 0,      0x3BF5, 0x0000)]   // BFLDH data 30 mask F0
    public void BitOps(string code, int r1, int r2, int r1Out, int psw)
    {
        var t = TestMachine.Create(code).SetR(1, (ushort)r1).SetR(2, (ushort)r2);
        t.Step();
        Assert.Equal(((ushort)r1Out, (ushort)psw), (t.R(1), t.Psw));
    }

    [Theory]
    [InlineData(0x1, 0x0000, true)] [InlineData(0x1, 0x0010, false)] [InlineData(0xA, 0x0001, false)]
    [InlineData(0xA, 0x0000, true)] [InlineData(0xC, 0x0001, true)] [InlineData(0xE, 0x0002, false)]
    [InlineData(0xF, 0x0008, true)] [InlineData(0x0, 0xFFFF, true)]
    public void ConditionTable(int cc, int psw, bool holds) => Assert.Equal(holds, Conditions.Holds(cc, (ushort)psw));

    [Fact] public void Jmpr_TakenAndNot()
    {
        var t = TestMachine.Create("2D01 CC00 CC00");
        t.Psw = 0x0008; t.Step(); Assert.Equal(0x1004, t.Cpu.IP);    // taken: 1002 + 2*1
        t = TestMachine.Create("2D01"); t.Psw = 0; t.Step(); Assert.Equal(0x1002, t.Cpu.IP);
    }

    [Fact] public void Jbc_TakesAndClears_Jnbs_TakesAndSets()
    {
        var t = TestMachine.Create("AAF10100").SetR(1, 1); t.Step();
        Assert.Equal((0x1006, 0, 0x0001), (t.Cpu.IP, t.R(1), t.Psw));
        t = TestMachine.Create("BAF10100").SetR(1, 0); t.Step();
        Assert.Equal((0x1006, 1, 0x0008), (t.Cpu.IP, t.R(1), t.Psw));
    }

    [Fact] public void Jmps_Jmpi_Jmpa()
    {
        var t = TestMachine.Create("FA010020"); t.Step();
        Assert.Equal((0x01, 0x2000), (t.Cpu.CSP, t.Cpu.IP));
        t = TestMachine.Create("9C02").SetR(2, 0x3000); t.Step();
        Assert.Equal(0x3000, t.Cpu.IP);                               // no DPP on code targets
        t = TestMachine.Create("EA000210"); t.Step();
        Assert.Equal(0x1002, t.Cpu.IP);
    }
}

public class CpuHarnessTests
{
    [Fact]
    public void ErasedFlash_ExecutesBsetAndIsRecorded()
    {
        var t = TestMachine.Create("FFFF FFFF");
        t.SetR(15, 0); t.Step(2);
        Assert.Equal(0x8000, t.R(15));                // FFFF = BSET R15.15, as on hardware
        Assert.IsType<ErasedFlashExecutionEvent>(Assert.Single(t.M.Events.All));
    }
}
