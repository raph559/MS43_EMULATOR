using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V10.3's pops window, without V10.2's stabs: the throttle store adds pops' own opening for the whole window, and the
/// window ends at V8's 160 rpm rise, in the tick and in the gate, whatever group 139 holds (it is reserved, every byte 0).
/// Direct CPU tests: one machine starts a window through the real overrun-cut gate (0A:A7BC), then runs housekeeping
/// tick by tick and the throttle store (09:B138) in between. The helpers serve the blip, brake and guard tests too.
/// </summary>
public sealed class PopsWindowTests
{
    private const uint Stay = 0xAB0F2, Cut = 0xAA7C8;
    private const ushort Overrun = 1 << 11;

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

    /// <summary>V10.2's stab bit, FDF8.7: free on V10.3, so it must never be set.</summary>
    private static bool OldStabBit(Machine m) => (m.Bus.Peek16(0xFDF8) & 1 << 7) != 0;

    /// <summary>A window with the image's flashed settings and pops on, started at 4,160 rpm (the flashed minimum is 4,000).</summary>
    private static Machine Started(Action<byte[]>? change = null)
    {
        var m = Machine(Image(V10Fixture.Bytes, change), V10Fixture.Symbol);
        V8Fixture.PutByte(m, 0x4007F, 130);
        Gate(m);
        return m;
    }

    /// <summary>V10.2's flashed stabs, written into the reserved group 139: a 2.0 s load, +5.0 deg TPS, 150/250 ms, 480 rpm.</summary>
    private static void V102Stabs(byte[] b) => new byte[] { 0x14, 0x19, 0x0F, 0x19, 0x0F, 0, 0, 0 }.CopyTo(b, V10Fixture.Defaults + 8);

    [Theory] // with V10.2's stabs flashed in 139, the store is still V9's: group 139 is never read
    [InlineData(0)] [InlineData(100)] [InlineData(200)]
    public void TheThrottleIsV9sWhatever139Holds(byte opening)
    {
        void Settings(byte[] b) { b[V10Fixture.Defaults + 1] = 50; b[V10Fixture.Defaults + 6] = opening; }
        var v10 = Machine(Image(change: b => { Settings(b); V102Stabs(b); }), V10Fixture.Symbol);
        var v9 = Machine(Image(V9Fixture.Bytes, Settings), V9Fixture.Symbol);
        Gate(v10); Gate(v9);
        for (var tick = 1; tick <= 500; tick++)
        {
            Tick(v10); Tick(v9);
            var pedal = (ushort)(tick * 97 % 4000);
            var psw = (ushort)(0x0800 | tick % 16);
            Assert.True(Store(v9, pedal, psw) == Store(v10, pedal, psw), $"tick {tick}");
            Assert.False(OldStabBit(v10), $"tick {tick}");
        }
    }

    [Fact] // the flashed 3.0 deg TPS, from the first tick to the window's end, with no stab on top
    public void TheOpeningHoldsTheWholeWindow()
    {
        var m = Started(V102Stabs);
        var ticks = 0;
        while (Window(m) && ticks < 500)
        {
            Tick(m); ticks++;
            if (!Window(m)) break;
            Assert.InRange(Store(m, 1000).Setpoint - 1000, 1637, 1641);
            Assert.False(OldStabBit(m), $"tick {ticks}");
        }
        Assert.InRange(ticks, 399, 401);                             // the flashed 4.0 s window ran out
        Assert.Equal(1000, Store(m, 1000).Setpoint);
    }

    [Theory] // V8's rule for the whole window: it ends 160 rpm above its lowest, even with V10.2's 480 rpm in 139/4
    [InlineData(150, 4, true)] [InlineData(150, 5, false)] [InlineData(260, 4, true)] [InlineData(260, 5, false)]
    [InlineData(260, 14, false)]
    public void TheWindowEndsAt160RpmThroughout(int ticks, byte rise, bool kept)
    {
        var m = Started(V102Stabs);
        for (var tick = 1; tick <= ticks; tick++) Tick(m);
        Assert.True(Window(m));
        V8Fixture.PutByte(m, 0x4007F, (byte)(130 + rise));
        Tick(m);
        Assert.Equal(kept, Window(m));
    }

    /// <summary>The overrun-cut gate (0A:A7BC) with the stock cut requested, for a window that runs: Stay or Cut.</summary>
    private static uint GateExit(Machine m)
    {
        m.Cpu.SetR(0, 0xF780); m.Cpu.Psw = 0x0805;
        m.Cpu.Jump(0x0C, 0x1030);
        m.Run(50_000, x => x.Cpu.Address is Stay or Cut);
        return m.Cpu.Address;
    }

    [Theory] // the gate checks the same 160 rpm rise on every request of the stock cut
    [InlineData(150, 4, true)] [InlineData(150, 5, false)] [InlineData(260, 4, true)] [InlineData(260, 7, false)]
    public void TheGateEndsTheWindowAt160Rpm(int ticks, byte rise, bool kept)
    {
        var m = Started(V102Stabs);
        for (var tick = 1; tick <= ticks; tick++) Tick(m);
        V8Fixture.PutByte(m, 0x4007F, (byte)(130 + rise));
        Assert.Equal(kept ? Stay : Cut, GateExit(m));
        Assert.Equal(kept, Window(m));
    }

    [Fact] // group 139 is reserved: a live copy of V10.2's stabs is refused, a copy of zeros is taken
    public void TheReservedGroupTakesOnlyZeros()
    {
        var m = Machine(Image(V10Fixture.Bytes), V10Fixture.Symbol);
        Assert.Equal(0xA0, OverlayV9Tests.Command(m, V3Fixture.Open())[0]);
        Assert.Equal(0xA0, OverlayV9Tests.Mutate(m, V5Fixture.Load(139, 704))[0]);
        byte[] v102 = [0x14, 0x19, 0x0F, 0x19, 0x0F, 0, 0, 0];
        Assert.NotEqual(0xA0, OverlayV9Tests.Mutate(m, [0x6D, 2, 139, 0, 0, .. v102])[0]);
        Assert.Equal(0xA0, OverlayV9Tests.Mutate(m, [0x6D, 2, 139, 0, 0, .. new byte[8]])[0]);
    }

    [Fact] // V10.2's stab count byte, 09FBF, is no longer written by a window
    public void AWindowLeavesTheOldStabCountAlone()
    {
        var m = Machine(Image(V10Fixture.Bytes), V10Fixture.Symbol);
        V8Fixture.PutByte(m, 0x4007F, 130);
        V8Fixture.PutByte(m, V10Fixture.PopsStabs, 7);
        Gate(m);
        while (Window(m)) Tick(m);
        Assert.Equal(7, V10Fixture.Byte(m, V10Fixture.PopsStabs));
    }
}
