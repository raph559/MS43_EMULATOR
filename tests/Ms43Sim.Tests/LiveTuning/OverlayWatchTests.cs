using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>The watched map's commands: set, clear, range, session and silence, and the stock log request beside them.</summary>
public sealed class OverlayWatchTests
{
    [Fact]
    public void WatchSetStoresTheAddressAndClearsTheRecord()
    {
        var m = V10Fixture.Start();
        V10Fixture.PutByte(m, V10Fixture.Kind, 1); V10Fixture.PutByte(m, V10Fixture.Count, 9);
        Assert.Equal(new byte[] { 0xA0, 0x07, 0x75, 0xD4 }, V10Fixture.Watch(m, 0x775D4));
        Assert.Equal(0x1D, m.Bus.Peek16(V10Fixture.WPage));
        Assert.Equal(0x35D4, m.Bus.Peek16(V10Fixture.WOff));
        Assert.Equal(0, V10Fixture.Byte(m, V10Fixture.Kind));
        Assert.Equal(0, V10Fixture.Byte(m, V10Fixture.Count));
    }

    [Fact]
    public void WatchSetFindsTheCatalogueRow()
    {
        var m = V10Fixture.Start();
        var row0 = (uint)V10Fixture.Catalog[0].GetProperty("address").GetInt32();
        V10Fixture.Watch(m, row0);
        Assert.Equal(0, V10Fixture.Byte(m, V10Fixture.RowIx));
        V10Fixture.Watch(m, 0x70002);
        Assert.Equal(0xFF, V10Fixture.Byte(m, V10Fixture.RowIx));
    }

    [Fact]
    public void WatchAddressZeroClears()
    {
        var m = V10Fixture.Start();
        V10Fixture.Watch(m, 0x775D4);
        Assert.Equal(new byte[] { 0xA0, 0, 0, 0 }, V10Fixture.Watch(m, 0));
        Assert.Equal(0xFFFF, m.Bus.Peek16(V10Fixture.WPage));
    }

    [Fact]
    public void WatchArmsTheHooksFastCompareAndClearingDisarmsIt()
    {
        var m = V10Fixture.Start();
        Assert.False(V10Fixture.Armed(m));
        V10Fixture.Watch(m, 0x775D4);
        Assert.True(V10Fixture.Armed(m));
        V10Fixture.Watch(m, 0);
        Assert.False(V10Fixture.Armed(m));
    }

    [Fact]
    public void HousekeepingClearsAnArmBitLeftWithNoWatch()
    {
        var m = V10Fixture.Start();
        V10Fixture.Watch(m, 0);
        m.Bus.Poke16(V10Fixture.Flags, (ushort)(m.Bus.Peek16(V10Fixture.Flags) | 0x60));   // FDF8 as it may power up
        V10Fixture.Housekeeping(m, 1);
        Assert.False(V10Fixture.Armed(m));
        Assert.False(V10Fixture.HasCopy(m));
    }

    [Fact]
    public void WatchAddressOutOfRangeIsRefusedWithReason4()
    {
        var m = V10Fixture.Start();
        V10Fixture.Watch(m, 0x775D4);
        Assert.Equal(new byte[] { 0xB0, 4 }, V10Fixture.Watch(m, 0x80000));
        Assert.Equal(0x1D, m.Bus.Peek16(V10Fixture.WPage));
        Assert.Equal(0x35D4, m.Bus.Peek16(V10Fixture.WOff));
    }

    [Fact]
    public void WatchNeedsNoSession()
    {
        var m = V10Fixture.Boot();
        Assert.Equal(new byte[] { 0xA0, 0x07, 0x75, 0xD4 }, V10Fixture.Watch(m, 0x775D4));
        Assert.Equal(0x1D, m.Bus.Peek16(V10Fixture.WPage));
    }

    [Fact] // RF3
    public void WatchEndsAfterThreeSecondsOfSilenceButNotAtEngineOff()
    {
        var m = V10Fixture.Start();
        m.Bus.Poke16(0xE2A6, 1000);
        V10Fixture.Watch(m, 0x775D4);
        V10Fixture.Housekeeping(m, 1299);
        Assert.Equal(0x1D, m.Bus.Peek16(V10Fixture.WPage));
        V10Fixture.Housekeeping(m, 1301);
        Assert.Equal(0xFFFF, m.Bus.Peek16(V10Fixture.WPage));

        m.Bus.Poke16(0xF900, 0);                                   // the engine is not running
        m.Bus.Poke16(0xE2A6, 2000);
        V10Fixture.Watch(m, 0x775D4);
        for (ushort ticks = 2100; ticks <= 3000; ticks += 100)
        {
            m.Bus.Poke16(0xE2A6, ticks);
            V10Fixture.Command(m, [0x6D, 0]);
            V10Fixture.Housekeeping(m, ticks);
        }
        Assert.Equal(0x1D, m.Bus.Peek16(V10Fixture.WPage));
    }

    [Fact]
    public void StockLogRequestIsUnchanged()
    {
        byte[] Reply(byte[] image)
        {
            var m = V10Fixture.Direct(image, [0xDA, 0x0C, 0x7E, 0x46]);
            for (var a = 0x8000u; a < 0x8500u; a += 2) m.Bus.Poke16(a, (ushort)(a * 37 + 11));
            V10Fixture.Execute(m, cycles: 400_000);
            return V10Fixture.Read(m, 0xF74E, 77);
        }
        Assert.Equal(Reply(TestBins.CarBytes), Reply(V10Fixture.Bytes));
    }
}
