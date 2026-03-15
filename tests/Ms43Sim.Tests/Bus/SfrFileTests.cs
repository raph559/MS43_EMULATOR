using Ms43Sim.Core;

namespace Ms43Sim.Tests.Bus;

public class SfrFileTests
{
    [Fact]
    public void ByteWrite_ClearsTheOtherByte()          // UM §4: "Any write operation to a single byte of an SFR clears the non-addressed complementary byte"
    {
        var f = new SfrFile(new AccessInventory());
        f.Poke(0xFEB0, 0xABCD);
        f.Write8(0xFEB1, 0x56);
        Assert.Equal(0x5600, f.Peek(0xFEB0));
        f.Write8(0xFEB0, 0x34);
        Assert.Equal(0x0034, f.Peek(0xFEB0));
    }

    [Fact]
    public void UnmodelledAccess_IsInventoriedOnce_WithCount()
    {
        var inv = new AccessInventory { Context = () => (100UL, 0x002D1Au) };
        var f = new SfrFile(inv);
        f.Read16(0xFEB0); f.Read16(0xFEB0);
        var r = Assert.Single(inv.Records);
        Assert.Equal((AccessArea.Sfr, 0xFEB0u, "S0TBUF", AccessKind.Read, 2L, 100UL, 0x002D1Au),
                     (r.Area, r.Address, r.Name, r.Kind, r.Count, r.FirstCycle, r.FirstPc));
    }

    [Fact]
    public void RegisteredAccess_GoesToHooks_AndIsNotInventoried()
    {
        var inv = new AccessInventory();
        var f = new SfrFile(inv);
        ushort stored = 0;
        f.Register(0xFF10, "cpu", () => 0x1234, v => stored = v);
        f.Write16(0xFF10, 0x0042);
        Assert.Equal(0x1234, f.Read16(0xFF10));
        Assert.Equal(0x0042, stored);
        Assert.Empty(inv.Records);
    }

    [Fact]
    public void ResetAll_LoadsTableResetValues_AndThePlansTwoOverrides()
    {
        var f = new SfrFile(new AccessInventory());
        f.ResetAll();
        Assert.Equal(0xFC00, f.Peek(0xFE12));
        Assert.Equal(0xFFFF, f.Peek(0xFF1E));
        Assert.Equal(0x0680, f.Peek(0xFF0C));   // BUSCON0 [assumption]
        Assert.Equal(0x0000, f.Peek(0xFF12));   // SYSCON [assumption]
    }
}
