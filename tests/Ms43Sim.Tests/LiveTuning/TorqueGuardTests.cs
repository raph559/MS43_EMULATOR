using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V10's torque guard (RF4): while a pops window or a blip runs, GUARD is set when the monitoring's torque estimate
/// (8013) is more than 20 Nm above the permitted torque (86AD), both bytes in 2 Nm: 20 Nm short of the monitoring's
/// own fault threshold (ip_tqi_dif_max_mon, 40 Nm). The guard only lowers the throttle addition: a stab's extra, and
/// the blip's addition (Task 6). Direct CPU tests on the window machine of PopsStabTests.
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
        var m = PopsStabTests.Machine(PopsStabTests.Image(), V10Fixture.Symbol);
        PopsStabTests.Gate(m);
        return m;
    }

    [Theory]
    [InlineData(60, 50, false)] [InlineData(61, 50, true)] [InlineData(255, 244, true)] [InlineData(255, 245, false)]
    [InlineData(10, 250, false)]
    public void TheGuardActsBeyondTwentyNmOfExcess(byte estimate, byte permitted, bool acting)
    {
        var m = Window();
        Torque(m, estimate, permitted);
        PopsStabTests.Tick(m);
        Assert.True(PopsStabTests.Window(m));
        Assert.Equal(acting, Guard(m));
    }

    [Fact]
    public void DuringAStabTheGuardLeavesTheOpening()
    {
        var m = Window();
        for (var tick = 1; tick <= 201; tick++) PopsStabTests.Tick(m);
        Assert.True(PopsStabTests.Stab(m));
        Torque(m, 100, 50);
        PopsStabTests.Tick(m);
        Assert.True(Guard(m));
        Assert.True(PopsStabTests.Stab(m));
        Assert.InRange(PopsStabTests.Store(m, 1000).Setpoint - 1000, 5460, 5464);   // the opening only
        Torque(m, 50, 50);
        PopsStabTests.Tick(m);
        Assert.False(Guard(m));
        Assert.True(PopsStabTests.Stab(m));
        Assert.InRange(PopsStabTests.Store(m, 1000).Setpoint - 1000, 13652, 13660); // the stab's extra is back
    }

    [Fact]
    public void TheGuardRestsOutsideAWindowOrBlip()
    {
        var m = PopsStabTests.Machine(PopsStabTests.Image(), V10Fixture.Symbol);   // overrun, no window yet
        Torque(m, 255, 0);
        m.Bus.Poke16(0xFDF8, (ushort)(m.Bus.Peek16(0xFDF8) | 1 << V10Fixture.Guard));  // as RAM may power up
        PopsStabTests.Tick(m);
        Assert.False(PopsStabTests.Window(m));
        Assert.False(Guard(m));

        m = Window();
        Torque(m, 255, 0);
        PopsStabTests.Tick(m);
        Assert.True(Guard(m));
        m.Bus.Poke16(0xFD2C, 1 << 10);                                              // the pedal: the window ends
        PopsStabTests.Tick(m);
        Assert.False(PopsStabTests.Window(m));
        Assert.False(Guard(m));
    }
}
