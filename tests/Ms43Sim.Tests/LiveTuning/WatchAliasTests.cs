using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The watch follows the watched map's live copy (W_APAGE:W_AOFF), recomputed by housekeeping, and drops it before any
/// sub-command or switch-off changes the RAM's allocation (RF1).
/// </summary>
public sealed class WatchAliasTests
{
    private const int Idle = 1;                  // ip_iga_ron98_is__n__maf, 36 bytes, catalogue row 1
    private const uint IdleAddress = 0x775D4;

    private static Machine Watched(int logical)
    {
        var m = V10Fixture.Start();
        m.Bus.Poke16(0xE2A6, 100);
        V10Fixture.Watch(m, IdleAddress);
        Assert.Equal(0xA0, V10Fixture.Mutate(m, V10Fixture.Load(Idle, logical))[0]);
        V10Fixture.Housekeeping(m, 101);
        return m;
    }

    /// <summary>Calls reader 0D:029A on R13:R12 and returns the record's count afterwards.</summary>
    private static byte ReadAt(Machine m, ushort page, ushort offset)
    {
        m.Cpu.SetR(0, 0xF780); m.Cpu.SetR(12, offset); m.Cpu.SetR(13, page);
        V10Fixture.PutByte(m, 0xFB6B, 1); m.Bus.Poke16(0xE390, 0); V10Fixture.PutByte(m, 0xFB6C, 1);
        m.Bus.Poke16(0xE392, 0); V10Fixture.PutByte(m, 0xFB6D, 6);
        m.Cpu.Jump(0x0C, 0x1020); m.Run(200_000, x => x.Cpu.Address == 0xC1024);
        Assert.Equal(0xC1024u, m.Cpu.Address);
        return V10Fixture.Byte(m, V10Fixture.Count);
    }

    [Fact]
    public void AliasFollowsALoadedWatchedMap()
    {
        var a = Watched(40);
        Assert.Equal(0x10, a.Bus.Peek16(V10Fixture.APage));
        Assert.Equal(0x1D28, a.Bus.Peek16(V10Fixture.AOff));
        Assert.True(V10Fixture.HasCopy(a));

        var b = Watched(712);
        Assert.Equal(3, b.Bus.Peek16(V10Fixture.APage));
        Assert.Equal(0x26E8, b.Bus.Peek16(V10Fixture.AOff));
    }

    public static TheoryData<string> Changes => new() { "free", "revert", "all off" };

    [Theory, MemberData(nameof(Changes))] // RF1
    public void AliasIsClearedTheMomentTheRamChangesHands(string change)
    {
        var m = Watched(40);
        var reply = change switch
        {
            "free" => V10Fixture.Mutate(m, LiveOverlay.Free(Idle)),
            "revert" => V10Fixture.Mutate(m, LiveOverlay.Revert()),
            _ => V10Fixture.Mutate(m, LiveOverlay.AllOff()),
        };
        Assert.Equal(0xA0, reply[0]);
        Assert.Equal(0xFFFF, m.Bus.Peek16(V10Fixture.APage));
        Assert.False(V10Fixture.HasCopy(m));
        if (change == "all off") return;
        Assert.Equal(0xA0, V10Fixture.Mutate(m, V10Fixture.Load(2, 40))[0]);  // another table takes the same RAM
        Assert.Equal(0, ReadAt(m, 0x10, 0x1D28));
    }

    [Fact] // RF1
    public void TheQuietSwitchOffClearsTheAlias()
    {
        var m = Watched(40);
        Assert.Equal(0xA0, V10Fixture.Mutate(m, LiveOverlay.On(Idle, LiveOverlay.Crc32(V10Fixture.Flash(Idle))))[0]);
        V10Fixture.Housekeeping(m, 102);
        Assert.Equal(0x10, m.Bus.Peek16(V10Fixture.APage));
        m.Bus.Poke16(0xF900, 0);                                    // the engine stops: every On table goes to Ready
        V10Fixture.Housekeeping(m, 103);
        Assert.Equal(0xFFFF, m.Bus.Peek16(V10Fixture.APage));
    }

    [Fact]
    public void UncataloguedWatchHasNoAlias()
    {
        var m = V10Fixture.Start();
        V10Fixture.Watch(m, 0x70002);
        V10Fixture.Housekeeping(m, 1);
        Assert.Equal(0xFFFF, m.Bus.Peek16(V10Fixture.APage));
        Assert.False(V10Fixture.HasCopy(m));
    }
}
