using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V10's torque guard (RF4): while a pops window or a blip runs, GUARD is set when the monitoring's torque estimate
/// (8013) is more than 20 Nm above the permitted torque (86AD), both bytes in 2 Nm: 20 Nm short of the monitoring's
/// own fault threshold (ip_tqi_dif_max_mon, 40 Nm). The guard only lowers the blip's addition (Task 6); V10.3 has no
/// stabs, so in a pops window it leaves the opening as it is. Direct CPU tests on the window machine of PopsWindowTests.
/// </summary>
public sealed class TorqueGuardTests
{
    private static void Torque(Machine m, byte estimate, byte permitted)
    {
        V8Fixture.PutByte(m, 0x40013, estimate);
        V8Fixture.PutByte(m, 0x406AD, permitted);
    }

    private static bool Guard(Machine m) => V10Fixture.Flag(m, V10Fixture.Guard);

    private static Machine Window()
    {
        var m = PopsWindowTests.Machine(PopsWindowTests.Image(), V10Fixture.Symbol);
        PopsWindowTests.Gate(m);
        return m;
    }

    [Theory]
    [InlineData(60, 50, false)] [InlineData(61, 50, true)] [InlineData(255, 244, true)] [InlineData(255, 245, false)]
    [InlineData(10, 250, false)]
    public void TheGuardActsBeyondTwentyNmOfExcess(byte estimate, byte permitted, bool acting)
    {
        var m = Window();
        Torque(m, estimate, permitted);
        PopsWindowTests.Tick(m);
        Assert.True(PopsWindowTests.Window(m));
        Assert.Equal(acting, Guard(m));
    }

    [Fact]
    public void InAWindowTheGuardLeavesTheOpening()
    {
        var m = Window();
        for (var tick = 1; tick <= 201; tick++) PopsWindowTests.Tick(m);
        Torque(m, 100, 50);
        PopsWindowTests.Tick(m);
        Assert.True(Guard(m));
        Assert.InRange(PopsWindowTests.Store(m, 1000).Setpoint - 1000, 5460, 5464);   // the opening, guard or not
        Torque(m, 50, 50);
        PopsWindowTests.Tick(m);
        Assert.False(Guard(m));
        Assert.InRange(PopsWindowTests.Store(m, 1000).Setpoint - 1000, 5460, 5464);
    }

    [Fact]
    public void TheGuardRestsOutsideAWindowOrBlip()
    {
        var m = PopsWindowTests.Machine(PopsWindowTests.Image(), V10Fixture.Symbol);   // overrun, no window yet
        Torque(m, 255, 0);
        m.Bus.Poke16(0xFDF8, (ushort)(m.Bus.Peek16(0xFDF8) | 1 << V10Fixture.Guard));  // as RAM may power up
        PopsWindowTests.Tick(m);
        Assert.False(PopsWindowTests.Window(m));
        Assert.False(Guard(m));

        m = Window();
        Torque(m, 255, 0);
        PopsWindowTests.Tick(m);
        Assert.True(Guard(m));
        m.Bus.Poke16(0xFD2C, 1 << 10);                                              // the pedal: the window ends
        PopsWindowTests.Tick(m);
        Assert.False(PopsWindowTests.Window(m));
        Assert.False(Guard(m));
    }
}
