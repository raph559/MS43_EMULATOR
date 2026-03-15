using Ms43Sim.Core;

namespace Ms43Sim.Tests.Bus;

public class PhysicalBusTests
{
    static (PhysicalBus bus, SfrFile sfr, AccessInventory inv, EventLog log) Make(byte[]? flash = null)
    {
        var inv = new AccessInventory(); var log = new EventLog(); var sfr = new SfrFile(inv); sfr.ResetAll();
        flash ??= Enumerable.Repeat((byte)0xFF, 0x80000).ToArray();
        return (new PhysicalBus(FirmwareImage.FromBytes(flash), 8192, sfr, inv, log, 0xFFFF), sfr, inv, log);
    }

    [Fact]
    public void Flash_IsMirroredEvery512KiB()
    {
        var img = new byte[0x80000]; img[0x6366A] = 0x11; img[0x6366B] = 0x22;
        var (bus, _, _, _) = Make(img);
        Assert.Equal(0x2211, bus.Read16(0x06366A));
        Assert.Equal(0x2211, bus.Read16(0x0E366A));
        Assert.Equal(0x2211, bus.Read16(0x86366A));
    }

    [Fact]
    public void Iram_Sfr_Esfr_Reserved_Decode()
    {
        var (bus, _, _, _) = Make();
        Assert.Equal(Region.Iram, bus.Decode(0x00F600)); Assert.Equal(Region.Iram, bus.Decode(0x00FDFE));
        Assert.Equal(Region.Sfr, bus.Decode(0x00FE00)); Assert.Equal(Region.Esfr, bus.Decode(0x00F186));
        Assert.Equal(Region.Reserved, bus.Decode(0x00F400)); Assert.Equal(Region.Flash, bus.Decode(0x01F600));
    }

    [Fact]
    public void Xram_And_Can_OnlyWithXpen()
    {
        var (bus, _, _, _) = Make();
        Assert.Equal(Region.Flash, bus.Decode(0x00E000));
        bus.XperipheralsEnabled = true;
        Assert.Equal(Region.Xram, bus.Decode(0x00E7FE)); Assert.Equal(Region.Can, bus.Decode(0x00EF10));
    }

    [Fact]
    public void ExternalRam_8KiB_MirrorsInA16KiBWindow()
    {
        var (bus, sfr, _, _) = Make();
        sfr.Write16(0xFE1A, 0x0402);   // ADDRSEL2: 16 KiB at 0x40000
        sfr.Write16(0xFF16, 0x052E);   // BUSCON2 as the boot sets it (BUSACT=1)
        bus.Write16(0x040010, 0xBEEF);
        Assert.Equal(0xBEEF, bus.Read16(0x042010));
    }

    [Fact]
    public void UnmodelledWindow_ReadsFfffAndIsInventoried()
    {
        var (bus, sfr, inv, _) = Make();
        sfr.Write16(0xFE1C, 0x0800); sfr.Write16(0xFF18, 0x85AE);   // ADDRSEL3/BUSCON3 as the boot sets them
        bus.Write16(0x080000, 0x0078);
        Assert.Equal(0xFFFF, bus.Read16(0x080000));
        Assert.Contains(inv.Records, r => r.Area == AccessArea.ChipSelect3 && r.Address == 0x080000 && r.Kind == AccessKind.Write);
    }

    [Fact]
    public void Window4_LosesToXram_ButServesTheGap()
    {
        var (bus, sfr, _, _) = Make();
        bus.XperipheralsEnabled = true;
        sfr.Write16(0xFE1E, 0x00E0); sfr.Write16(0xFF1A, 0x0648);   // ADDRSEL4 4 KiB at 0xE000, BUSCON4 active
        Assert.Equal(Region.Xram, bus.Decode(0x00E000));
        Assert.Equal(Region.ChipSelect4, bus.Decode(0x00E800));
    }

    [Fact]
    public void FlashWrite_IsRecordedAndIgnored()
    {
        var (bus, _, _, log) = Make();
        bus.Write16(0x010000, 0x1234);
        Assert.Equal(0xFFFF, bus.Read16(0x010000));
        Assert.IsType<FlashWriteEvent>(Assert.Single(log.All));
    }

    [Fact]
    public void FlashAct_FollowsBuscon0()
    {
        var (bus, sfr, _, _) = Make();
        Assert.Equal(18, bus.AccessStates(0x010000));   // reset BUSCON0 0680h [assumption]: 2+15+1
        sfr.Write16(0xFF0C, 0x45BF);
        Assert.Equal(2, bus.AccessStates(0x010000));
        Assert.Equal(0, bus.AccessStates(0x00F800));
    }
}
