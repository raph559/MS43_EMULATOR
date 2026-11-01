using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V10's downshift blip, its state machine in housekeeping (spec 7.4 to 7.7). Direct CPU tests: the inputs are seeded
/// (the clutch and brake bits of FD2E, the pedal E0FF, the road speed 847F, the engine speed 807F, the coolant 8433
/// and the gear 81AE), then housekeeping runs tick by tick.
/// </summary>
public sealed class BlipTests
{
    private const int Blip = V10Fixture.Defaults + 16, Gears = V10Fixture.Defaults + 24, Ratios = V10Fixture.Defaults + 32;

    /// <summary>A V10 machine with the blip always on (140 byte 0 = 1) and the defaults otherwise.</summary>
    private static Machine Machine(Action<byte[]>? change = null, byte gear = 4, byte speed = 60, byte rpm = 40)
    {
        var image = V10Fixture.Bytes;
        image[Blip] = 1;
        change?.Invoke(image);
        var m = OverlayV9Tests.Boot(image, V10Fixture.Symbol);
        V8Fixture.PutByte(m, 0x401AE, gear);
        V8Fixture.PutByte(m, 0x4047F, speed);
        V8Fixture.PutByte(m, 0x4007F, rpm);
        V8Fixture.PutByte(m, 0x40433, 180);                         // 87 deg C
        V8Fixture.PutByte(m, 0xE0FF, 0);                            // the pedal released
        m.Bus.Poke16(0xFD2C, 1 << 11);
        m.Bus.Poke16(0xFD18, 0);
        m.Bus.Poke16(0xFDF8, 0x4000);
        m.Bus.Poke16(0xFD2E, 0);
        PopsStabTests.Tick(m);                                       // one tick with the clutch up
        return m;
    }

    private static void Clutch(Machine m, bool clutch, bool lightSwitch = false, bool testSwitch = false) =>
        m.Bus.Poke16(0xFD2E, (ushort)((clutch ? 1 << 6 : 0) | (lightSwitch ? 1 << 3 : 0) | (testSwitch ? 1 << 4 : 0)));

    private static void Braking(Machine m) => Clutch(m, true, true, true);
    private static void Pedal(Machine m, byte value) => V8Fixture.PutByte(m, 0xE0FF, value);
    private static void Ticks(Machine m, int n) { for (var i = 0; i < n; i++) PopsStabTests.Tick(m); }

    private static bool On(Machine m) => V10Fixture.Flag(m, V10Fixture.BlipOn);
    private static int Target(Machine m) => V10Fixture.Byte(m, V10Fixture.BlipTgt);
    private static int Add(Machine m) => m.Bus.Peek16(V10Fixture.BlipAdd);

    /// <summary>A tap after the clutch went in: the pedal up past the threshold for <paramref name="up"/> ticks, then down.</summary>
    private static void Tap(Machine m, int up = 20)
    {
        Pedal(m, 40); Ticks(m, up);
        Pedal(m, 0); PopsStabTests.Tick(m);
    }

    [Fact]
    public void BrakingFrom4thAt60StartsABlipTo3rdsTarget()
    {
        var m = Machine();
        Braking(m);
        PopsStabTests.Tick(m);
        Assert.True(On(m));
        Assert.Equal(83, Target(m));                                // 60 x 89 / 64: about 2,656 rpm
        Assert.InRange(Add(m), 8191, 8195);                         // 15.0 deg TPS
    }

    [Theory] // RF1: one stuck switch never starts a blip
    [InlineData(true, false)] [InlineData(false, true)]
    public void OneBrakeSwitchAloneStartsNoBlip(bool lightSwitch, bool testSwitch)
    {
        var m = Machine();
        Clutch(m, true, lightSwitch, testSwitch);
        Ticks(m, 50);
        Assert.False(On(m));
    }

    [Fact]
    public void ATapStartsABlipWhenThePedalComesBack()
    {
        var m = Machine();
        Clutch(m, true);
        PopsStabTests.Tick(m);
        Pedal(m, 40); Ticks(m, 20);
        Assert.False(On(m));
        Pedal(m, 0); PopsStabTests.Tick(m);
        Assert.True(On(m));
    }

    [Fact] // RF1
    public void PedalAlreadyUpIsNotATap()
    {
        var m = Machine();
        Pedal(m, 40);
        Clutch(m, true);
        Ticks(m, 5);
        Pedal(m, 0);
        Ticks(m, 30);
        Assert.False(On(m));
    }

    [Fact] // RF1: the foot back on at the end of an upshift, with the clutch switch still closed
    public void APedalHeldLongerThanTheTapLengthIsNotATap()
    {
        var m = Machine();
        Clutch(m, true);
        PopsStabTests.Tick(m);
        Tap(m, up: 41);
        Assert.False(On(m));
        Assert.True(V10Fixture.Flag(m, V10Fixture.BlipUsed));
        Ticks(m, 20);
        Assert.False(On(m));
    }

    [Fact]
    public void APedalUpForTheWholeTapLengthIsStillATap()
    {
        var m = Machine();
        Clutch(m, true);
        PopsStabTests.Tick(m);
        Tap(m, up: 40);
        Assert.True(On(m));
    }

    [Fact] // RF1: an upshift, the clutch in with neither sign
    public void NeitherSignNoBlip()
    {
        var m = Machine();
        Clutch(m, true);
        Ticks(m, 300);
        Assert.False(On(m));
        Assert.Equal(255, V10Fixture.Byte(m, V10Fixture.BlipCl));
    }

    [Fact]
    public void From2ndOnlyATap()
    {
        void TwoToOne(byte[] b) => b[Gears + 3] = 1;
        var m = Machine(TwoToOne, gear: 2, speed: 30);
        Braking(m);
        Ticks(m, 30);
        Assert.False(On(m));

        m = Machine(TwoToOne, gear: 2, speed: 30);
        Clutch(m, true);
        PopsStabTests.Tick(m);
        Tap(m);
        Assert.True(On(m));
        Assert.Equal(30 * 226 / 64, Target(m));
    }

    [Theory]
    [InlineData(5, 0)] [InlineData(4, 1)] [InlineData(3, 2)]
    public void ADownshiftSwitchedOffGetsNoBlip(byte gear, int index)
    {
        var on = Machine(gear: gear);
        Braking(on); PopsStabTests.Tick(on);
        Assert.True(On(on));

        var off = Machine(b => b[Gears + index] = 0, gear: gear);
        Braking(off); Ticks(off, 30);
        Assert.False(On(off));
    }

    [Fact]
    public void TwoToOneIsOffByDefault()
    {
        var m = Machine(gear: 2, speed: 30);
        Clutch(m, true);
        PopsStabTests.Tick(m);
        Tap(m);
        Assert.False(On(m));
    }

    [Fact]
    public void ASignNotChosenStartsNoBlip()
    {
        var m = Machine(b => b[Blip + 1] = 2);                      // on a tap only
        Braking(m); Ticks(m, 30);
        Assert.False(On(m));

        m = Machine(b => b[Blip + 1] = 1);                          // while braking only
        Clutch(m, true);
        PopsStabTests.Tick(m);
        Tap(m);
        Assert.False(On(m));
    }

    public static TheoryData<string> NoBlipCases => new() { "speed", "cold", "late", "lc", "nls", "ral", "1st", "near" };

    [Theory, MemberData(nameof(NoBlipCases))]
    public void NoBlipWhen(string name)
    {
        var m = Machine(gear: name == "1st" ? (byte)1 : (byte)4, speed: name == "speed" ? (byte)29 : (byte)60,
            rpm: name == "near" ? (byte)78 : (byte)40);
        if (name == "cold") V8Fixture.PutByte(m, 0x40433, 143);
        if (name is "lc" or "nls" or "ral")
            m.Bus.Poke16(0xFDF8, (ushort)(0x4000 | 1 << (name == "lc" ? 0 : name == "nls" ? 1 : 2)));
        if (name == "late") { Clutch(m, true); Ticks(m, 201); }
        Braking(m);
        Ticks(m, 5);
        Assert.False(On(m), name);
    }

    [Fact]
    public void OneBlipPerClutchPress()
    {
        var m = Machine(b => b[Blip + 4] = 3);                      // 0.3 s at most
        Braking(m); PopsStabTests.Tick(m);
        Assert.True(On(m));
        Ticks(m, 40);
        Assert.False(On(m));
        Ticks(m, 100);
        Assert.False(On(m));
        Clutch(m, false); PopsStabTests.Tick(m);
        Braking(m); PopsStabTests.Tick(m);
        Assert.True(On(m));
    }

    public static TheoryData<string, bool> Stops => new()
    {
        { "clutch", false }, { "pedal", false }, { "time", false }, { "speed", false }, { "rpm", false },
        { "switch", false }, { "lc", false },
        { "clutch", true }, { "pedal", true }, { "time", true }, { "speed", true }, { "rpm", true },
        { "switch", true }, { "lc", true },
    };

    [Theory, MemberData(nameof(Stops))] // RF2
    public void TheBlipStops(string stop, bool fromTheHold)
    {
        var m = Machine(b => { b[Blip] = 2; b[Blip + 4] = 30; });  // cruise ready as the switch; 3.0 s
        m.Bus.Poke16(0xFD18, 2);                                    // the cruise main switch on, no speed held
        Braking(m); PopsStabTests.Tick(m);
        Assert.True(On(m));
        if (fromTheHold) { V8Fixture.PutByte(m, 0x4007F, 80); PopsStabTests.Tick(m); Assert.True(V10Fixture.Flag(m, V10Fixture.BlipHold)); }
        switch (stop)
        {
            case "clutch": Clutch(m, false); break;
            case "pedal": Pedal(m, 30); break;
            case "time": m.Bus.Poke16(V10Fixture.BlipLeft, 0); break;
            case "speed": V8Fixture.PutByte(m, 0x4047F, 29); break;
            case "rpm": V8Fixture.PutByte(m, 0x4007F, 187); break;
            case "switch": m.Bus.Poke16(0xFD18, 0); break;
            case "lc": m.Bus.Poke16(0xFDF8, (ushort)(m.Bus.Peek16(0xFDF8) | 1)); break;
        }
        PopsStabTests.Tick(m);
        Assert.False(On(m), stop);
        Assert.Equal(0, Add(m));
    }

    [Fact]
    public void RiseThenHold()
    {
        var m = Machine();                                          // the target is 83 (60 km/h, 3rd)
        Braking(m); PopsStabTests.Tick(m);
        int rise = Add(m), hold = 2731;                             // 15.0 and 5.0 deg TPS
        Assert.InRange(rise, 8191, 8195);
        foreach (byte rpm in new byte[] { 60, 76 })
        {
            V8Fixture.PutByte(m, 0x4007F, rpm); PopsStabTests.Tick(m);
            Assert.False(V10Fixture.Flag(m, V10Fixture.BlipHold), $"{rpm}");
            Assert.Equal(rise, Add(m));
        }
        V8Fixture.PutByte(m, 0x4007F, 77); PopsStabTests.Tick(m);   // 77 + 6 reaches 83: the hold begins
        Assert.True(V10Fixture.Flag(m, V10Fixture.BlipHold));
        Assert.InRange(Add(m), hold - 2, hold + 2);
        V8Fixture.PutByte(m, 0x4007F, 88); PopsStabTests.Tick(m);   // in the band (83..88): unchanged
        Assert.InRange(Add(m), hold - 2, hold + 2);
        V8Fixture.PutByte(m, 0x4007F, 89); PopsStabTests.Tick(m);   // above 83 + 5: nothing
        Assert.Equal(0, Add(m));
        V8Fixture.PutByte(m, 0x4007F, 85); PopsStabTests.Tick(m);   // back in the band: still nothing
        Assert.Equal(0, Add(m));
        V8Fixture.PutByte(m, 0x4007F, 82); PopsStabTests.Tick(m);   // below the target: the hold opening
        Assert.InRange(Add(m), hold - 2, hold + 2);
    }

    [Fact]
    public void TheTargetFallsAsTheCarSlows()
    {
        var m = Machine();
        Braking(m); PopsStabTests.Tick(m);
        Assert.Equal(83, Target(m));
        V8Fixture.PutByte(m, 0x4047F, 50); PopsStabTests.Tick(m);
        Assert.Equal(50 * 89 / 64, Target(m));
    }

    [Fact]
    public void ReleasingTheBrakeDoesNotEndABrakingBlip()
    {
        var m = Machine();
        Braking(m); PopsStabTests.Tick(m);
        Clutch(m, true);
        Ticks(m, 20);
        Assert.True(On(m));
    }

    [Fact]
    public void ABlipUsesUpTheLiftsPops()
    {
        var m = Machine();
        Braking(m); PopsStabTests.Tick(m);
        Assert.True(On(m));
        Assert.NotEqual(0, m.Bus.Peek16(0xFDF8) & 1 << 4);
    }

    [Fact]
    public void HeldClutchSinceBootStartsNoBlipAtAStandstill()
    {
        var image = V10Fixture.Bytes; image[Blip] = 1;
        var m = OverlayV9Tests.Boot(image, V10Fixture.Symbol);
        V8Fixture.PutByte(m, V10Fixture.BlipCl, 5);                 // left in RAM
        V8Fixture.PutByte(m, 0x401AE, 4);
        V8Fixture.PutByte(m, 0x4047F, 0);
        V8Fixture.PutByte(m, 0x40433, 180);
        m.Bus.Poke16(0xFDF8, 0x4000);
        Braking(m);
        Ticks(m, 30);
        Assert.False(On(m));
    }

    [Fact]
    public void TheBlipKeepsMisfireDetectionSuppressed()
    {
        var m = Machine();
        Braking(m); PopsStabTests.Tick(m);
        m.Bus.Poke16(0xE3B0, 7); m.Bus.Poke16(0xFD2A, 0);
        PopsStabTests.Tick(m);
        Assert.Equal(100, m.Bus.Peek16(0xE3B0));
        Assert.NotEqual(0, m.Bus.Peek16(0xFD2A) & 1 << 12);
    }
}
