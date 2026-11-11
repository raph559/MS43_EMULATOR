using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V10: either brake switch (FD2E.3 the brake light switch, FD2E.4 the brake test switch) ends the pops, and the lift
/// stays used up (FDF8.4) until the pedal is pressed with the clutch up, so the speed-holding pops never come back as
/// the car turns in after braking (owner, 2026-11-05). V9's gate and tick facts with both switches clear are
/// PopsGateTests.
/// </summary>
public sealed class PopsBrakeTests
{
    private const uint Stay = 0xAB0F2, Cut = 0xAA7C8;
    private const ushort Overrun = 1 << 11, PartLoad = 1 << 10;

    private static Machine Overrun_() => PopsWindowTests.Machine(PopsWindowTests.Image());

    private static uint Gate(Machine m)
    {
        m.Cpu.SetR(0, 0xF780); m.Cpu.Psw = 0x0805;
        m.Cpu.Jump(0x0C, 0x1030);
        m.Run(50_000, x => x.Cpu.Address is Stay or Cut);
        Assert.True(m.Cpu.Address is Stay or Cut, $"stopped at {m.Cpu.Address:X5}");
        return m.Cpu.Address;
    }

    private static bool Used(Machine m) => (m.Bus.Peek16(0xFDF8) & 1 << 4) != 0;

    [Theory]
    [InlineData(3)] [InlineData(4)]
    public void EitherBrakeSwitchEndsAWindowWithinATick(int bit)
    {
        var m = Overrun_();
        PopsWindowTests.Gate(m);
        PopsWindowTests.Tick(m);
        Assert.True(PopsWindowTests.Window(m));
        m.Bus.Poke16(0xFD2E, (ushort)(1 << bit));
        PopsWindowTests.Tick(m);
        Assert.False(PopsWindowTests.Window(m));
        Assert.True(Used(m));
        Assert.Equal(1000, PopsWindowTests.Store(m, 1000).Setpoint);
    }

    [Theory] // RF3
    [InlineData(3)] [InlineData(4)]
    public void AfterTheBrakeNoWindowUntilThePedalIsPressedAgain(int bit)
    {
        var m = Overrun_();
        m.Bus.Poke16(0xFD2E, (ushort)(1 << bit));                    // 1. braking in overrun, before the cut decision
        PopsWindowTests.Tick(m);
        Assert.True(Used(m));
        m.Bus.Poke16(0xFD2E, 0);                                     //    then off the brake
        PopsWindowTests.Tick(m);
        Assert.Equal(Cut, Gate(m));                                  // 2. the cut decision: no window
        Assert.False(PopsWindowTests.Window(m));
        m.Bus.Poke16(0xFD2C, PartLoad);                              // 3. the pedal, with the clutch up, re-arms
        PopsWindowTests.Tick(m);
        Assert.False(Used(m));
        m.Bus.Poke16(0xFD2C, Overrun);                               // 4. the next lift has its window
        Assert.Equal(Stay, Gate(m));
        Assert.True(PopsWindowTests.Window(m));
    }

    [Fact] // RF3
    public void APedalPressWithTheClutchInDoesNotReArm()
    {
        var m = Overrun_();
        PopsWindowTests.Gate(m);                                       // the lift's window, so FDF8.4 is set
        m.Bus.Poke16(0xFD2E, 1 << 6);                                // clutch in: the window ends
        PopsWindowTests.Tick(m);
        Assert.True(Used(m));
        m.Bus.Poke16(0xFD2C, PartLoad);                              // a tap with the clutch in
        PopsWindowTests.Tick(m);
        Assert.True(Used(m));
        m.Bus.Poke16(0xFD2C, Overrun);
        Assert.Equal(Cut, Gate(m));
        m.Bus.Poke16(0xFD2E, 0);                                     // the clutch up and the pedal pressed re-arm it
        m.Bus.Poke16(0xFD2C, PartLoad);
        PopsWindowTests.Tick(m);
        Assert.False(Used(m));
    }
}
