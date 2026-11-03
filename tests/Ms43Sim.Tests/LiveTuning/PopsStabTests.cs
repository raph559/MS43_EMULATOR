using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V10's pops stabs: after the load, the throttle addition alternates a stab (the opening plus the stab size) and a
/// gap (the opening), on the 10 ms tick. Direct CPU tests: one machine starts a window through the real overrun-cut
/// gate (0A:A7BC), then runs housekeeping tick by tick and the throttle store (09:B138) in between.
/// </summary>
public sealed class PopsStabTests
{
    private const uint Stay = 0xAB0F2, Cut = 0xAA7C8;
    private const ushort Overrun = 1 << 11, PartLoad = 1 << 10;

    /// <summary>V10's settings (<see cref="V10Fixture.Classic"/>) with pops on, or <paramref name="image"/>'s own.</summary>
    internal static byte[] Image(byte[]? image = null, Action<byte[]>? change = null)
    {
        var bytes = image ?? V10Fixture.ClassicBytes;
        bytes[V10Fixture.Defaults] = 1;                              // 138/0: pops always on
        change?.Invoke(bytes);
        return bytes;
    }

    /// <summary>A V10 (or V9) machine with the DS2 handler at 0C:1000, housekeeping at 0C:1010 and the gate at 0C:1030.</summary>
    internal static Machine Machine(byte[] image, Func<string, int> symbol)
    {
        new byte[] { 0xFA, 0x0A, 0xBC, 0xA7 }.CopyTo(image, 0x41030);
        var m = OverlayV9Tests.Boot(image, symbol);
        V8Fixture.PutByte(m, 0x4007F, 100);                          // 3,200 rpm
        V8Fixture.PutByte(m, 0x4047F, 80);                           // 80 km/h
        V8Fixture.PutByte(m, 0x40433, 200);                          // 102 deg C
        m.Bus.Poke16(0xFD2E, 0);
        m.Bus.Poke16(0xFD30, 1 << 2);                                // the stock cut is requested
        m.Bus.Poke16(0xFD18, 0);
        m.Bus.Poke16(0xFD2C, Overrun);
        m.Bus.Poke16(0xFDF8, 0x4000);
        return m;
    }

    internal static void Gate(Machine m)
    {
        m.Cpu.SetR(0, 0xF780); m.Cpu.Psw = 0x0805;
        m.Cpu.Jump(0x0C, 0x1030);
        m.Run(50_000, x => x.Cpu.Address is Stay or Cut);
        Assert.Equal(Stay, m.Cpu.Address);
        Assert.True(Window(m), "no window started");
    }

    internal static void Tick(Machine m)
    {
        m.Cpu.SetR(0, 0xF780);
        m.Cpu.Jump(0x0C, 0x1010);
        m.Run(200_000, x => x.Cpu.Address == 0xD80DE);
        Assert.Equal(0xD80DEu, m.Cpu.Address);
    }

    /// <summary>The throttle store from its real site with the pedal-map setpoint in R4; returns 86A4 and the PSW.</summary>
    internal static (ushort Setpoint, ushort Psw) Store(Machine m, ushort pedalMap, ushort psw = 0x0800)
    {
        m.Cpu.SetR(0, 0xF780); m.Cpu.SetR(4, pedalMap); m.Cpu.Psw = psw;
        m.Cpu.Jump(0x09, 0xB138);
        m.Run(200_000, x => x.Cpu.Address == 0x9B13C);
        Assert.Equal(0x9B13Cu, m.Cpu.Address);
        return (m.Bus.Peek16(0x406A4), m.Cpu.Psw);
    }

    internal static bool Window(Machine m) => (m.Bus.Peek16(0xFDF8) & 1 << 3) != 0;
    internal static bool Stab(Machine m) => V10Fixture.Flag(m, V10Fixture.PopsStab);

    private static Machine Started(Action<byte[]>? change = null)
    {
        var m = Machine(Image(change: change), V10Fixture.Symbol);
        Gate(m);
        return m;
    }

    /// <summary>The ticks (1-based, from the window's start) on which POPS_STAB was set, until the window ended.</summary>
    private static List<int> StabTicks(Machine m, int ticks)
    {
        var on = new List<int>();
        for (var tick = 1; tick <= ticks; tick++)
        {
            Tick(m);
            if (Stab(m)) on.Add(tick);
            if (!Window(m)) break;
        }
        return on;
    }

    [Theory] // RF5: with stab size 0 the store is V9's
    [InlineData(0)] [InlineData(100)] [InlineData(200)]
    public void WithStabSizeZeroTheThrottleIsV9s(byte opening)
    {
        void Settings(byte[] b) { b[V10Fixture.Defaults + 1] = 50; b[V10Fixture.Defaults + 6] = opening; b[V10Fixture.Defaults + 9] = 0; }
        var v10 = Machine(Image(change: Settings), V10Fixture.Symbol);
        var v9 = Machine(Image(V9Fixture.Bytes, Settings), V9Fixture.Symbol);
        Gate(v10); Gate(v9);
        for (var tick = 1; tick <= 500; tick++)
        {
            Tick(v10); Tick(v9);
            var pedal = (ushort)(tick * 97 % 4000);
            var psw = (ushort)(0x0800 | tick % 16);
            Assert.True(Store(v9, pedal, psw) == Store(v10, pedal, psw), $"tick {tick}");
            Assert.False(Stab(v10), $"tick {tick}");
        }
    }

    [Fact]
    public void TheLoadComesFirst()
    {
        var m = Started();
        for (var tick = 1; tick <= 200; tick++)
        {
            Tick(m);
            Assert.False(Stab(m), $"tick {tick}");
            var (setpoint, _) = Store(m, 1000);
            Assert.InRange(setpoint - 1000, 5460, 5464);
        }
    }

    [Fact]
    public void StabsAlternateWithTheSetLengths()
    {
        var m = Started();
        var on = StabTicks(m, 500);
        // A 4.0 s window: ticks 1..399 run, 400 ends it. After the 2.0 s load, 15 ticks on and 25 off.
        int[] expected = [.. Enumerable.Range(0, 5).SelectMany(n => Enumerable.Range(201 + 40 * n, 15))];
        Assert.Equal(expected, on);
    }

    [Fact]
    public void DuringAStabTheAddIsOpeningPlusStab()
    {
        var m = Started();
        for (var tick = 1; tick <= 201; tick++) Tick(m);
        Assert.True(Stab(m));
        var (setpoint, _) = Store(m, 1000);
        Assert.InRange(setpoint - 1000, 13652, 13660);
        for (var tick = 202; tick <= 216; tick++) Tick(m);
        Assert.False(Stab(m));
        (setpoint, _) = Store(m, 1000);
        Assert.InRange(setpoint - 1000, 5460, 5464);
    }

    public static TheoryData<string> Stops => new() { "time", "pedal", "speed", "clutch", "rise" };

    [Theory, MemberData(nameof(Stops))]
    public void AStabNeverOutlivesTheWindow(string stop)
    {
        var m = Started(b => b[V10Fixture.Defaults + 1] = 21);      // a 2.1 s window: its last ticks are a stab
        for (var tick = 1; tick <= 205; tick++) Tick(m);
        Assert.True(Stab(m));
        switch (stop)
        {
            case "time": for (var tick = 206; tick <= 210; tick++) Tick(m); break;
            case "pedal": m.Bus.Poke16(0xFD2C, PartLoad); Tick(m); break;
            case "speed": V8Fixture.PutByte(m, 0x4047F, 19); Tick(m); break;
            case "clutch": m.Bus.Poke16(0xFD2E, 1 << 6); Tick(m); break;
            case "rise": V8Fixture.PutByte(m, 0x4007F, 105); Tick(m); break;
        }
        Assert.False(Window(m), stop);
        Assert.False(Stab(m), stop);
        Assert.Equal(1000, Store(m, 1000).Setpoint);
    }

    [Fact]
    public void ALoadAsLongAsTheWindowGivesNoStab()
    {
        var m = Started(b => b[V10Fixture.Defaults + 8] = 40);      // a 4.0 s load in the 4.0 s window
        Assert.Empty(StabTicks(m, 500));
    }

    [Fact]
    public void ALiveGapChangeAppliesAtTheNextPhase()
    {
        var m = Started();
        Assert.Equal(0xA0, OverlayV9Tests.Command(m, V3Fixture.Open())[0]);
        Assert.Equal(0xA0, OverlayV9Tests.Mutate(m, V5Fixture.Load(139, 704))[0]);
        var live = V10Fixture.ClassicGroup(139); live[3] = 10;       // gaps of 100 ms
        Assert.Equal(0xA0, OverlayV9Tests.Mutate(m, [0x6D, 2, 139, 0, 0, .. live])[0]);
        Assert.Equal(0xA0, OverlayV9Tests.Mutate(m, LiveOverlay.On(139, LiveOverlay.Crc32(live)))[0]);
        var on = StabTicks(m, 500);
        // 15 ticks on, 10 off, from tick 201 to the window's end at 400.
        int[] expected = [.. Enumerable.Range(0, 8).SelectMany(n => Enumerable.Range(201 + 25 * n, 15)).Where(t => t < 400)];
        Assert.Equal(expected, on);
    }

    /// <summary>A window with V10.1's flashed settings and pops on, started at 4,160 rpm (V10.1's minimum is 4,000).</summary>
    private static Machine StartedV101(Action<byte[]>? change = null)
    {
        var m = Machine(Image(V10Fixture.Bytes, change), V10Fixture.Symbol);
        V8Fixture.PutByte(m, 0x4007F, 130);
        Gate(m);
        return m;
    }

    /// <summary>
    /// V10.1 (the car, 2026-11-05: a stab adds about 200 rpm, and V8's 160 rpm rule ended each window after one or two
    /// stabs). During the 2.0 s load a window still ends when the rpm rises 160 rpm above its lowest; once the stabs have
    /// begun, at 139/4 x 32 rpm, V10.1's default 15 (480 rpm).
    /// </summary>
    [Theory] // RF1
    [InlineData(150, 4, true)] [InlineData(150, 5, false)] [InlineData(199, 5, false)]
    [InlineData(260, 5, true)] [InlineData(260, 14, true)] [InlineData(260, 15, false)]
    public void OnceTheStabsBeginTheWindowAllowsTheSetRise(int ticks, byte rise, bool kept)
    {
        var m = StartedV101();
        for (var tick = 1; tick <= ticks; tick++) Tick(m);
        Assert.True(Window(m));
        V8Fixture.PutByte(m, 0x4007F, (byte)(130 + rise));
        Tick(m);
        Assert.Equal(kept, Window(m));
        Tick(m);
        Assert.Equal(kept, Window(m));
    }

    [Fact] // RF5: with stab size 0 there is no stab phase, so the 160 rpm rule holds for the whole window
    public void WithoutStabsTheWindowEndsAt160RpmThroughout()
    {
        var m = StartedV101(b => b[V10Fixture.Defaults + 9] = 0);
        for (var tick = 1; tick <= 260; tick++) Tick(m);
        Assert.True(Window(m));
        V8Fixture.PutByte(m, 0x4007F, 135);
        Tick(m);
        Assert.False(Window(m));
    }

    /// <summary>The overrun-cut gate (0A:A7BC) with the stock cut requested, for a window that runs: Stay or Cut.</summary>
    private static uint GateExit(Machine m)
    {
        m.Cpu.SetR(0, 0xF780); m.Cpu.Psw = 0x0805;
        m.Cpu.Jump(0x0C, 0x1030);
        m.Run(50_000, x => x.Cpu.Address is Stay or Cut);
        return m.Cpu.Address;
    }

    /// <summary>
    /// The V10.1 review's finding: the gate checks the rise too, on every request of the stock cut, so it must allow 139/4
    /// once stabbing as the tick does; with V8's +5 there, each stabbing window still ended 160 rpm above its lowest.
    /// </summary>
    [Theory] // RF1
    [InlineData(150, 4, true)] [InlineData(150, 5, false)] [InlineData(260, 7, true)] [InlineData(260, 14, true)]
    [InlineData(260, 15, false)]
    public void TheGateAllowsTheSetRiseOnceStabbing(int ticks, byte rise, bool kept)
    {
        var m = StartedV101();
        for (var tick = 1; tick <= ticks; tick++) Tick(m);
        V8Fixture.PutByte(m, 0x4007F, (byte)(130 + rise));
        Assert.Equal(kept ? Stay : Cut, GateExit(m));
        Assert.Equal(kept, Window(m));
    }

    [Fact] // RF5: with stab size 0 the gate's rule is V8's for the whole window
    public void WithoutStabsTheGateEndsTheWindowAt160Rpm()
    {
        var m = StartedV101(b => b[V10Fixture.Defaults + 9] = 0);
        for (var tick = 1; tick <= 260; tick++) Tick(m);
        V8Fixture.PutByte(m, 0x4007F, 135);
        Assert.Equal(Cut, GateExit(m));
        Assert.False(Window(m));
    }

    /// <summary>
    /// V10.1: the stabs begun in the window are counted in pops' spare byte (09FBF, after POPS_MIN) for the app's log, which
    /// reads it twice a second: FDF8.7 alone shows a 150 ms stab to about one read in five. A window's start clears it, and
    /// it keeps the window's count after the window ends.
    /// </summary>
    [Fact]
    public void TheStabsBegunAreCountedForTheLog()
    {
        var m = Machine(Image(V10Fixture.Bytes), V10Fixture.Symbol);
        V8Fixture.PutByte(m, 0x4007F, 130);
        V8Fixture.PutByte(m, V10Fixture.PopsStabs, 7);                  // left from an earlier window
        Gate(m);
        Assert.Equal(0, V10Fixture.Byte(m, V10Fixture.PopsStabs));
        var counts = new List<int>();
        while (Window(m) && counts.Count < 500) { Tick(m); counts.Add(V10Fixture.Byte(m, V10Fixture.PopsStabs)); }
        Assert.Equal(0, counts[199]);                                    // still loading at tick 200
        Assert.Equal(1, counts[200]);                                    // the first stab begins at tick 201
        Assert.Equal(1, counts[239]);
        Assert.Equal(2, counts[240]);                                    // the next 400 ms later
        Assert.Equal(5, counts[^1]);                                     // five in the 4.0 s window, kept after it
        Assert.False(Window(m));
    }

    [Fact]
    public void V101sStabsAreFiveDegreesOnTopOfThreeDegrees()
    {
        var m = StartedV101();
        for (var tick = 1; tick <= 200; tick++) Tick(m);
        Assert.False(Stab(m));
        Assert.InRange(Store(m, 1000).Setpoint - 1000, 1637, 1641);  // 3.0 deg TPS
        Tick(m);
        Assert.True(Stab(m));
        Assert.InRange(Store(m, 1000).Setpoint - 1000, 4367, 4373);  // 3.0 + 5.0 deg TPS
    }
}
