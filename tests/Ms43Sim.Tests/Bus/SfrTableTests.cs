using Ms43Sim.Core;

namespace Ms43Sim.Tests.Bus;

public class SfrTableTests
{
    [Theory]
    [InlineData("PSW", 0xFF10, 0x0000)] [InlineData("SP", 0xFE12, 0xFC00)] [InlineData("STKOV", 0xFE14, 0xFA00)]
    [InlineData("CP", 0xFE10, 0xFC00)] [InlineData("DPP3", 0xFE06, 0x0003)] [InlineData("ONES", 0xFF1E, 0xFFFF)]
    [InlineData("T1IC", 0xFF9E, 0x0000)] [InlineData("WDTCON", 0xFFAE, 0x0000)] [InlineData("TFR", 0xFFAC, 0x0000)]
    [InlineData("S0TBIC", 0xF19C, 0x0000)] [InlineData("XP0IC", 0xF186, 0x0000)] [InlineData("PECC7", 0xFECE, 0x0000)]
    public void KnownRegisters(string name, int address, int reset)
    {
        var s = SfrTable.ByAddress((uint)address)!;
        Assert.Equal(name, s.Name);
        Assert.Equal((ushort)reset, s.ResetValue);
    }

    [Fact] public void NoXperconOnC167cr() => Assert.Null(SfrTable.ByAddress(0xF024));
}
