using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The pops window at the overrun-cut gate (0A:A7BC) and its 10 ms tick in housekeeping. Direct CPU tests: the
/// state the gate reads is seeded, and the run stops at the stock "stay fuelled" (0A:B0F2) or "cut" (0A:A7C8) exit.
/// </summary>
public sealed class PopsGateTests
{
    private const uint Stay = 0xAB0F2, Cut = 0xAA7C8;
    private const uint Left = 0x41FBC, Lowest = 0x41FBE;
    private static readonly byte[] ToGate = [0xFA, 0x0A, 0xBC, 0xA7];
    // Switch on, 2.0 s, -15 deg, 11.5 AFR, 1,600 rpm (50), 70.5 deg C (158), no throttle, 20 km/h.
    private static readonly byte[] On = [1, 20, 23, 184, 50, 158, 0, 20];

    private static byte[] Image(byte[] settings, bool donor = false)
    {
        var image = donor ? TestBins.CarBytes : V8Fixture.Bytes;
        if (!donor) settings.CopyTo(image, V8Fixture.Defaults);
        return image;
    }

    public sealed record Inputs(byte Rpm = 100, byte Speed = 80, byte Coolant = 200, bool Clutch = false,
        int Features = 0, bool CruiseMain = false, bool CruiseHeld = false, bool Immediate = false, bool Request = true,
        bool Active = false, bool Used = false, ushort WindowLeft = 0, byte WindowLowest = 0);

    private static Machine Seed(byte[] image, Inputs i)
    {
        var m = V8Fixture.Direct(image, ToGate);
        for (var r = 1; r < 16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + r * 19));
        m.Cpu.SetR(0, 0xF780);
        m.Cpu.Psw = 0x0805;
        V8Fixture.PutByte(m, 0x4007F, i.Rpm);
        V8Fixture.PutByte(m, 0x4047F, i.Speed);
        V8Fixture.PutByte(m, 0x40433, i.Coolant);
        m.Bus.Poke16(0xFD2E, (ushort)((i.Clutch ? 1 << 6 : 0) | (i.Immediate ? 1 << 13 : 0)));
        m.Bus.Poke16(0xFD30, (ushort)(i.Request ? 1 << 2 : 0));
        m.Bus.Poke16(0xFD18, (ushort)((i.CruiseMain ? 2 : 0) | (i.CruiseHeld ? 4 : 0)));
        m.Bus.Poke16(0xFD2C, 1 << 11);
        m.Bus.Poke16(0xFDF8, (ushort)(i.Features | (i.Active ? 8 : 0) | (i.Used ? 16 : 0) | 0x4000));
        m.Bus.Poke16(Left, i.WindowLeft);
        V8Fixture.PutByte(m, Lowest, i.WindowLowest);
        return m;
    }

    private static uint Run(Machine m)
    {
        m.Cpu.Jump(0x0C, 0x1000);
        m.Run(50_000, x => x.Cpu.Address is Stay or Cut);
        Assert.True(m.Cpu.Address is Stay or Cut, $"stopped at {m.Cpu.Address:X5}");
        return m.Cpu.Address;
    }

    private static bool Flag(Machine m, int bit) => (m.Bus.Peek16(0xFDF8) & 1 << bit) != 0;

    private static void SameRegisters(Machine expected, Machine actual, string context)
    {
        for (var r = 0; r < 16; r++)
            Assert.True(expected.Cpu.R(r) == actual.Cpu.R(r), $"{context}: R{r} {expected.Cpu.R(r):X4} != {actual.Cpu.R(r):X4}");
        Assert.True(expected.Cpu.Psw == actual.Cpu.Psw, $"{context}: PSW {expected.Cpu.Psw:X4} != {actual.Cpu.Psw:X4}");
        Assert.Equal(expected.Cpu.Sp, actual.Cpu.Sp);
        for (var dpp = 0; dpp < 4; dpp++) Assert.Equal(expected.Cpu.Dpp(dpp), actual.Cpu.Dpp(dpp));
    }

    [Fact]
    public void WithTheSwitchOffTheGateExitsExactlyAsStock()
    {
        var off = On.ToArray(); off[0] = 0;
        for (var combination = 0; combination < 16; combination++)
        {
            var inputs = new Inputs(Immediate: (combination & 1) != 0, Request: (combination & 2) != 0,
                Active: (combination & 4) != 0, Used: (combination & 8) != 0, WindowLeft: 50, WindowLowest: 100);
            var expected = Seed(Image(off, donor: true), inputs);
            var actual = Seed(Image(off), inputs);
            var fdf8 = actual.Bus.Peek16(0xFDF8);
            var exit = Run(actual);
            Assert.Equal(Run(expected), exit);
            SameRegisters(expected, actual, $"combination {combination}");
            var stockStay = !inputs.Immediate && !inputs.Request;
            // Off ends a running window; otherwise nothing in FDF8 changes.
            Assert.Equal(stockStay || !inputs.Active ? fdf8 : (ushort)(fdf8 & ~8), actual.Bus.Peek16(0xFDF8));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AWindowStartsOnceAndStaysFuelled(bool immediate)
    {
        var inputs = new Inputs(Immediate: immediate, Request: !immediate);
        var expected = Seed(Image(On, donor: true), inputs with { Request = false, Immediate = false });
        var m = Seed(Image(On), inputs);
        Assert.Equal(Stay, Run(m));
        Run(expected);
        SameRegisters(expected, m, "start");
        Assert.True(Flag(m, 3)); Assert.True(Flag(m, 4));
        Assert.Equal(200, m.Bus.Peek16(Left));
        Assert.Equal(100, m.Bus.Peek16(Lowest) & 255);
    }

    public static TheoryData<string, Inputs, byte[]> Failing => new()
    {
        { "rpm below the minimum", new Inputs(Rpm: 49), On },
        { "speed below the minimum", new Inputs(Speed: 19), On },
        { "coolant below the minimum", new Inputs(Coolant: 157), On },
        { "clutch", new Inputs(Clutch: true), On },
        { "launch control", new Inputs(Features: 1), On },
        { "no-lift shift", new Inputs(Features: 2), On },
        { "rolling anti-lag", new Inputs(Features: 4), On },
        { "cruise switch off", new Inputs(), [2, 20, 23, 184, 50, 158, 0, 20] },
        { "cruise holding a speed", new Inputs(CruiseMain: true, CruiseHeld: true), [2, 20, 23, 184, 50, 158, 0, 20] },
    };

    [Theory]
    [MemberData(nameof(Failing))]
    public void EachFailingConditionCutsAtTheStart(string name, Inputs inputs, byte[] settings)
    {
        var m = Seed(Image(settings), inputs);
        Assert.True(Cut == Run(m), name);
        Assert.False(Flag(m, 3), name);
    }

    [Theory]
    [MemberData(nameof(Failing))]
    public void EachFailingConditionEndsARunningWindow(string name, Inputs inputs, byte[] settings)
    {
        var m = Seed(Image(settings), inputs with { Active = true, Used = true, WindowLeft = 50, WindowLowest = 40 });
        Assert.True(Cut == Run(m), name);
        Assert.False(Flag(m, 3), name);
        Assert.True(Flag(m, 4), name);
    }

    [Fact]
    public void CruiseReadyStartsAWindow()
    {
        var m = Seed(Image([2, 20, 23, 184, 50, 158, 0, 20]), new Inputs(CruiseMain: true));
        Assert.Equal(Stay, Run(m));
        Assert.True(Flag(m, 3));
    }

    [Fact]
    public void TheWindowEndsWhenItsTimeIsUp()
    {
        var m = Seed(Image(On), new Inputs(Active: true, Used: true, WindowLeft: 0, WindowLowest: 100));
        Assert.Equal(Cut, Run(m));
        Assert.False(Flag(m, 3));
    }

    [Theory] // RF4
    [InlineData(100, 105, false, 100)] [InlineData(100, 104, true, 100)] [InlineData(100, 90, true, 90)]
    [InlineData(252, 255, true, 252)]
    public void RpmRiseOf160EndsTheWindow_150DoesNot(byte lowest, byte rpm, bool stays, byte newLowest)
    {
        var m = Seed(Image(On), new Inputs(Rpm: rpm, Active: true, Used: true, WindowLeft: 50, WindowLowest: lowest));
        Assert.Equal(stays ? Stay : Cut, Run(m));
        Assert.Equal(stays, Flag(m, 3));
        Assert.Equal(newLowest, m.Bus.Peek16(Lowest) & 255);
    }

    [Fact] // RF2
    public void UsedLatchBlocksASecondWindowInTheSameLift()
    {
        var m = Seed(Image(On), new Inputs(Used: true));
        Assert.Equal(Cut, Run(m));
        Assert.False(Flag(m, 3));
    }

    [Fact] // RF3
    public void AnEditMidWindowIsHonouredAtTheNextPass()
    {
        var off = On.ToArray(); off[0] = 0;
        var m = Seed(Image(off), new Inputs(Active: true, Used: true, WindowLeft: 150, WindowLowest: 100));
        Assert.Equal(Cut, Run(m));
        Assert.False(Flag(m, 3));
    }

    private static Machine Tick(ushort mode, bool active, bool used, ushort left, Inputs? inputs = null, byte[]? settings = null)
    {
        var image = Image(settings ?? On);
        var hk = V8Fixture.Symbol("hk") & 0xFFFF;
        var i = inputs ?? new Inputs();
        var m = V8Fixture.Direct(image, [0xFA, 0x0C, (byte)hk, (byte)(hk >> 8)]);
        V8Fixture.PutByte(m, 0x4007F, i.Rpm);
        V8Fixture.PutByte(m, 0x4047F, i.Speed);
        V8Fixture.PutByte(m, 0x40433, i.Coolant);
        m.Bus.Poke16(0xFD2E, (ushort)(i.Clutch ? 1 << 6 : 0));
        m.Bus.Poke16(0xFD18, (ushort)((i.CruiseMain ? 2 : 0) | (i.CruiseHeld ? 4 : 0)));
        m.Bus.Poke16(0xFD2C, mode);
        m.Bus.Poke16(0xFDF8, (ushort)((active ? 8 : 0) | (used ? 16 : 0) | 0x4000 | i.Features));
        m.Bus.Poke16(Left, left);
        V8Fixture.PutByte(m, Lowest, i.WindowLowest == 0 ? i.Rpm : i.WindowLowest);
        m.Bus.Poke16(0xE3B0, 7);
        m.Bus.Poke16(0xFD2A, 0);
        m.Cpu.Jump(0x0C, 0x1000);
        m.Run(50_000, x => x.Cpu.Address == 0xD80DE);
        Assert.Equal(0xD80DEu, m.Cpu.Address);
        return m;
    }

    [Fact] // final review C2: the tick ends the window whichever stock path runs
    public void TickEndsTheWindowWhenItsTimeIsUp()
    {
        var m = Tick(1 << 11, active: true, used: true, left: 1);
        Assert.Equal(0, m.Bus.Peek16(Left));
        Assert.Equal(0x4010, m.Bus.Peek16(0xFDF8));
    }

    [Theory]
    [MemberData(nameof(Failing))]
    public void TickEndsTheWindowWhenAConditionFails(string name, Inputs inputs, byte[] settings)
    {
        var m = Tick(1 << 11, active: true, used: true, left: 50, inputs, settings);
        Assert.True((m.Bus.Peek16(0xFDF8) & 8) == 0, name);
        Assert.True((m.Bus.Peek16(0xFDF8) & 16) != 0, name);
    }

    [Theory]
    [InlineData(105, false)] [InlineData(104, true)]
    public void TickEndsTheWindowWhenTheRpmRises(byte rpm, bool stays)
    {
        var m = Tick(1 << 11, active: true, used: true, left: 50, new Inputs(Rpm: rpm, WindowLowest: 100));
        Assert.Equal(stays, (m.Bus.Peek16(0xFDF8) & 8) != 0);
    }

    [Fact]
    public void TickCountsDownAndSuppressesMisfireWhileActive()
    {
        var m = Tick(1 << 11, active: true, used: true, left: 7);
        Assert.Equal(6, m.Bus.Peek16(Left));
        Assert.Equal(100, m.Bus.Peek16(0xE3B0));
        Assert.NotEqual(0, m.Bus.Peek16(0xFD2A) & 1 << 12);
        m = Tick(1 << 11, active: true, used: true, left: 0);
        Assert.Equal(0, m.Bus.Peek16(Left));
    }

    [Fact]
    public void TickLeavesEverythingAloneWhenInactive()
    {
        var m = Tick(1 << 11, active: false, used: true, left: 7);
        Assert.Equal(7, m.Bus.Peek16(Left));
        Assert.Equal(7, m.Bus.Peek16(0xE3B0));
        Assert.Equal(0, m.Bus.Peek16(0xFD2A));
        Assert.Equal(0x4010, m.Bus.Peek16(0xFDF8));
    }

    [Fact]
    public void TickClearsBothFlagsWhenTheLiftIsOver()
    {
        var m = Tick(1 << 10, active: true, used: true, left: 7);
        Assert.Equal(0x4000, m.Bus.Peek16(0xFDF8));
    }

    [Fact]
    public void TickKeepsTheLatchWhileStillInOverrun()
    {
        var m = Tick(1 << 12, active: false, used: true, left: 0);
        Assert.Equal(0x4010, m.Bus.Peek16(0xFDF8));
    }
}
