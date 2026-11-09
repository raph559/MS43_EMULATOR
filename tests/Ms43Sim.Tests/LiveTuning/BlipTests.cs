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

    /// <summary>
    /// A machine with the blip always on (140 byte 0 = 1) and V10's settings otherwise (<see cref="V10Fixture.Classic"/>:
    /// a 15.0 °TPS blip opening, no lead), or V10.1's flashed settings when <paramref name="v101"/> is set.
    /// </summary>
    private static Machine Machine(Action<byte[]>? change = null, byte gear = 4, byte speed = 60, byte rpm = 40, bool v101 = false)
    {
        var image = v101 ? V10Fixture.Bytes : V10Fixture.ClassicBytes;
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
        PopsWindowTests.Tick(m);                                       // one tick with the clutch up
        return m;
    }

    private static void Clutch(Machine m, bool clutch, bool lightSwitch = false, bool testSwitch = false) =>
        m.Bus.Poke16(0xFD2E, (ushort)((clutch ? 1 << 6 : 0) | (lightSwitch ? 1 << 3 : 0) | (testSwitch ? 1 << 4 : 0)));

    private static void Braking(Machine m) => Clutch(m, true, true, true);
    private static void Pedal(Machine m, byte value) => V8Fixture.PutByte(m, 0xE0FF, value);
    private static void Ticks(Machine m, int n) { for (var i = 0; i < n; i++) PopsWindowTests.Tick(m); }

    private static bool On(Machine m) => V10Fixture.Flag(m, V10Fixture.BlipOn);
    private static int Target(Machine m) => V10Fixture.Byte(m, V10Fixture.BlipTgt);
    private static int Add(Machine m) => m.Bus.Peek16(V10Fixture.BlipAdd);

    /// <summary>A tap after the clutch went in: the pedal up past the threshold for <paramref name="up"/> ticks, then down.</summary>
    private static void Tap(Machine m, int up = 20)
    {
        Pedal(m, 40); Ticks(m, up);
        Pedal(m, 0); PopsWindowTests.Tick(m);
    }

    [Fact]
    public void BrakingFrom4thAt60StartsABlipTo3rdsTarget()
    {
        var m = Machine();
        Braking(m);
        PopsWindowTests.Tick(m);
        Assert.True(On(m));
        Assert.Equal(83, Target(m));                                // 60 x 89 / 64: about 2,656 rpm
        Assert.InRange(Add(m), 8191, 8195);                         // 15.0 deg TPS
    }

    [Fact] // V10.2: a blip starts with its whole angle (the owner: "very sharp"): no ramp until the cap has acted
    public void ABlipStartsWithItsWholeAngle()
    {
        var m = Machine();
        m.Bus.Poke16(0xE2B4, 0x1D00);                               // the car file's overrun angle, -12.75 deg
        V8Fixture.PutByte(m, V10Fixture.BlipSpk, 0x1D);
        Braking(m);
        PopsWindowTests.Tick(m);
        Assert.True(On(m));
        Assert.Equal(0xFF, V10Fixture.Byte(m, V10Fixture.BlipSpk));
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
        PopsWindowTests.Tick(m);
        Pedal(m, 40); Ticks(m, 20);
        Assert.False(On(m));
        Pedal(m, 0); PopsWindowTests.Tick(m);
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
        PopsWindowTests.Tick(m);
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
        PopsWindowTests.Tick(m);
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
        PopsWindowTests.Tick(m);
        Tap(m);
        Assert.True(On(m));
        Assert.Equal(30 * 226 / 64, Target(m));
    }

    [Theory]
    [InlineData(5, 0)] [InlineData(4, 1)] [InlineData(3, 2)]
    public void ADownshiftSwitchedOffGetsNoBlip(byte gear, int index)
    {
        var on = Machine(gear: gear);
        Braking(on); PopsWindowTests.Tick(on);
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
        PopsWindowTests.Tick(m);
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
        PopsWindowTests.Tick(m);
        Tap(m);
        Assert.False(On(m));
    }

    public static TheoryData<string> NoBlipCases => new() { "speed", "cold", "late", "lc", "nls", "ral", "1st", "near", "stalled", "under800" };

    [Theory, MemberData(nameof(NoBlipCases))]
    public void NoBlipWhen(string name)
    {
        var m = Machine(gear: name == "1st" ? (byte)1 : (byte)4, speed: name == "speed" ? (byte)29 : (byte)60,
            rpm: name switch { "near" => (byte)78, "stalled" => (byte)0, "under800" => (byte)24, _ => (byte)40 });  // V10.4: under 800 rpm
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
        Braking(m); PopsWindowTests.Tick(m);
        Assert.True(On(m));
        Ticks(m, 40);
        Assert.False(On(m));
        Ticks(m, 100);
        Assert.False(On(m));
        Clutch(m, false); PopsWindowTests.Tick(m);
        Braking(m); PopsWindowTests.Tick(m);
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
        Braking(m); PopsWindowTests.Tick(m);
        Assert.True(On(m));
        if (fromTheHold) { V8Fixture.PutByte(m, 0x4007F, 80); PopsWindowTests.Tick(m); Assert.True(V10Fixture.Flag(m, V10Fixture.BlipHold)); }
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
        PopsWindowTests.Tick(m);
        Assert.False(On(m), stop);
        Assert.Equal(0, Add(m));
    }

    [Fact]
    public void RiseThenHold()
    {
        var m = Machine();                                          // the target is 83 (60 km/h, 3rd)
        Braking(m); PopsWindowTests.Tick(m);
        int rise = Add(m), hold = 2731;                             // 15.0 and 5.0 deg TPS
        Assert.InRange(rise, 8191, 8195);
        foreach (byte rpm in new byte[] { 60, 76 })
        {
            V8Fixture.PutByte(m, 0x4007F, rpm); PopsWindowTests.Tick(m);
            Assert.False(V10Fixture.Flag(m, V10Fixture.BlipHold), $"{rpm}");
            Assert.Equal(rise, Add(m));
        }
        V8Fixture.PutByte(m, 0x4007F, 77); PopsWindowTests.Tick(m);   // 77 + 6 reaches 83: the hold begins
        Assert.True(V10Fixture.Flag(m, V10Fixture.BlipHold));
        Assert.InRange(Add(m), hold - 2, hold + 2);
        V8Fixture.PutByte(m, 0x4007F, 88); PopsWindowTests.Tick(m);   // in the band (83..88): unchanged
        Assert.InRange(Add(m), hold - 2, hold + 2);
        V8Fixture.PutByte(m, 0x4007F, 89); PopsWindowTests.Tick(m);   // above 83 + 5: nothing
        Assert.Equal(0, Add(m));
        V8Fixture.PutByte(m, 0x4007F, 85); PopsWindowTests.Tick(m);   // back in the band: still nothing
        Assert.Equal(0, Add(m));
        V8Fixture.PutByte(m, 0x4007F, 82); PopsWindowTests.Tick(m);   // below the target: the hold opening
        Assert.InRange(Add(m), hold - 2, hold + 2);
    }

    [Fact]
    public void TheTargetFallsAsTheCarSlows()
    {
        var m = Machine();
        Braking(m); PopsWindowTests.Tick(m);
        Assert.Equal(83, Target(m));
        V8Fixture.PutByte(m, 0x4047F, 50); PopsWindowTests.Tick(m);
        Assert.Equal(50 * 89 / 64, Target(m));
    }

    [Fact]
    public void ReleasingTheBrakeDoesNotEndABrakingBlip()
    {
        var m = Machine();
        Braking(m); PopsWindowTests.Tick(m);
        Clutch(m, true);
        Ticks(m, 20);
        Assert.True(On(m));
    }

    [Fact]
    public void ABlipUsesUpTheLiftsPops()
    {
        var m = Machine();
        Braking(m); PopsWindowTests.Tick(m);
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
        Braking(m); PopsWindowTests.Tick(m);
        m.Bus.Poke16(0xE3B0, 7); m.Bus.Poke16(0xFD2A, 0);
        PopsWindowTests.Tick(m);
        Assert.Equal(100, m.Bus.Peek16(0xE3B0));
        Assert.NotEqual(0, m.Bus.Peek16(0xFD2A) & 1 << 12);
    }

    [Fact] // V10.1's default blip opening, 30.0 deg TPS, the most the limits allow (owner, 2026-11-06: "Make it the default")
    public void V101BlipsWithThirtyDegrees()
    {
        var m = Machine(v101: true);
        Braking(m); PopsWindowTests.Tick(m);
        Assert.True(On(m));
        Assert.InRange(Add(m), 16385, 16389);
    }

    /// <summary>The rpm at which the hold began, the rpm climbing one 807F unit (32 rpm) a tick from 50 toward 83.</summary>
    private static int HoldBeganAt(byte lead)
    {
        var m = Machine(b => b[Gears + 6] = lead, rpm: 50, v101: true);
        Braking(m); PopsWindowTests.Tick(m);
        Assert.True(On(m));
        for (var rpm = 51; rpm < 83; rpm++)
        {
            V8Fixture.PutByte(m, 0x4007F, (byte)rpm); PopsWindowTests.Tick(m);
            Assert.True(On(m), $"{rpm}");
            if (V10Fixture.Flag(m, V10Fixture.BlipHold)) return rpm;
        }
        return 0;
    }

    /// <summary>
    /// V10.1's lead (141/6, 10 ms): the rise ends when the rpm, climbing at its filtered rate (16 x the rise a tick, ~1/4
    /// of the step each tick), would be within 192 rpm of the target this long from now. At one unit a tick the rate
    /// settles at 16 after 8 ticks, so a 150 ms lead ends the rise 15 units (480 rpm) earlier; 0 is V10's rule.
    /// </summary>
    [Fact] // RF2
    public void TheLeadEndsTheRiseEarlier()
    {
        Assert.Equal(77, HoldBeganAt(0));                           // 77 + 6 reaches 83, as V10
        Assert.Equal(62, HoldBeganAt(15));                          // 62 + 6 + 15 x 16 / 16
        Assert.Equal(72, HoldBeganAt(5));                           // 72 + 6 + 5 x 16 / 16
    }

    /// <summary>blip_lead itself: R5 the rpm now; BLIP_PREV, BLIP_RATE before; R4 the expected rise within the lead.</summary>
    private static (int Gain, int Rate, int Prev, ushort[] Registers) Rate(byte prev, byte rate, ushort rpm, byte lead = 15)
    {
        var image = V10Fixture.Bytes; image[Gears + 6] = lead;
        var m = V10Fixture.Direct(image, V10Fixture.CallA("blip_lead"));
        for (var r = 1; r < 16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + r * 19));
        m.Cpu.SetR(0, 0xF780); m.Cpu.SetR(5, rpm);
        V8Fixture.PutByte(m, V10Fixture.BlipPrev, prev); V8Fixture.PutByte(m, V10Fixture.BlipRate, rate);
        V10Fixture.Execute(m);
        return (m.Cpu.R(4), V10Fixture.Byte(m, V10Fixture.BlipRate), V10Fixture.Byte(m, V10Fixture.BlipPrev),
            [.. Enumerable.Range(0, 16).Select(r => m.Cpu.R(r))]);
    }

    [Theory]
    [InlineData(70, 0, 60, 15, 0, 0)]                               // falling: no rise, no lead
    [InlineData(70, 40, 60, 15, 30, 28)]                            // falling: the rate only decays
    [InlineData(60, 0, 61, 15, 4, 3)]                               // the first unit
    [InlineData(60, 16, 61, 15, 16, 15)]                            // settled at one unit a tick
    [InlineData(60, 250, 70, 15, 228, 213)]
    [InlineData(0, 255, 100, 15, 255, 239)]                         // the rate saturates at 255
    [InlineData(60, 255, 100, 30, 255, 478)]                        // the longest lead
    [InlineData(60, 200, 100, 0, 255, 0)]                           // lead 0: no lead, whatever the rate
    public void TheRateFollowsTheRiseAndScalesTheLead(byte prev, byte rate, ushort rpm, byte lead, int newRate, int gain)
    {
        var (g, r, p, registers) = Rate(prev, rate, rpm, lead);
        Assert.Equal(newRate, r);
        Assert.Equal(gain, g);
        Assert.Equal(rpm, p);
        foreach (var reg in new[] { 1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })
            Assert.True(registers[reg] == (reg == 5 ? rpm : (ushort)(0x9100 + reg * 19)), $"R{reg} changed");
        Assert.Equal(0xF780, registers[0]);
    }

    [Theory] // the target indexes 142 with the gear: a gear outside 2..5 during a blip stops it
    [InlineData(0)] [InlineData(1)] [InlineData(6)] [InlineData(7)]
    public void AGearOutsideTwoToFiveStopsTheBlip(byte gear)
    {
        var m = Machine();
        Braking(m); PopsWindowTests.Tick(m);
        Assert.True(On(m));
        V8Fixture.PutByte(m, 0x401AE, gear);
        PopsWindowTests.Tick(m);
        Assert.False(On(m), $"gear {gear}");
        Assert.Equal(0, Add(m));
    }

    [Theory] // bt_go: the throttle store must never see BLIP_ON with the last blip's values
    [InlineData(false)] [InlineData(true)]
    public void BlipOnIsSetLast(bool v101)
    {
        var m = Machine(v101: v101);
        m.Bus.Poke16(V10Fixture.BlipLeft, 7); m.Bus.Poke16(0xFDF8, 0x4000 | 1 << V10Fixture.BlipHold);
        Braking(m);
        (int Add, int Left, bool Hold)? seen = null;
        m.Cpu.SetR(0, 0xF780);
        m.Cpu.Jump(0x0C, 0x1010);
        m.Run(200_000, x =>
        {
            if (seen is null && (x.Bus.Peek16(0xFDF8) & 1 << V10Fixture.BlipOn) != 0)
                seen = (x.Bus.Peek16(V10Fixture.BlipAdd), x.Bus.Peek16(V10Fixture.BlipLeft),
                    (x.Bus.Peek16(0xFDF8) & 1 << V10Fixture.BlipHold) != 0);
            return x.Cpu.Address == 0xD80DE;
        });
        Assert.Equal(0xD80DEu, m.Cpu.Address);
        Assert.NotNull(seen);
        Assert.InRange(seen.Value.Add, v101 ? 16385 : 8191, v101 ? 16389 : 8195);
        Assert.Equal(150, seen.Value.Left);
        Assert.False(seen.Value.Hold);
    }
}
