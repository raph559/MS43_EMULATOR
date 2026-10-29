using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The three effects of a pops window: the fixed spark angle (0A:D424), the rich target through the shared AFR
/// branch (0B:D434) and the throttle opening (09:B138). Direct CPU tests from the real sites against the donor.
/// "Active" is FDF8.3 with the engine in fuelled overrun (FD2C.11).
/// </summary>
public abstract class PopsHookTests
{
    /// <summary>The overlay image under test: V8, or V9, which must behave the same.</summary>
    protected abstract OverlayFixture Fixture { get; }

    private const ushort Overrun = 1 << 11, PartLoad = 1 << 10, Idle = 1 << 9;
    private static readonly byte[] Settings = [1, 20, 23, 184, 50, 158, 30, 20];

    private sealed record Hook(string Name, byte Segment, ushort Offset, uint[] Ends);

    private static readonly Hook Spark = new("spark", 0x0A, 0xD424, [0xAD428]);
    private static readonly Hook Afr = new("afr", 0x0B, 0xD434, [0xBD438, 0xBD44E, 0xBD456, 0xBD464]);
    private static readonly Hook Throttle = new("throttle", 0x09, 0xB138, [0x9B13C]);

    private byte[] Image(byte[] settings, bool donor)
    {
        var image = donor ? TestBins.CarBytes : Fixture.Bytes();
        if (!donor) settings.CopyTo(image, V8Fixture.Defaults);
        return image;
    }

    private static Machine Seed(byte[] image, ushort mode, ushort fdf8, ushort psw, int sample)
    {
        var m = V8Fixture.Direct(image, [0xCC, 0x00, 0xCC, 0x00]);
        for (var r = 1; r < 16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + r * 19 + sample * 0x0101));
        m.Cpu.SetR(0, 0xF780);
        m.Cpu.SetR(4, (ushort)(1000 + sample * 9000));
        m.Cpu.Psw = psw;
        m.Bus.Poke16(0xFD2C, mode);
        m.Bus.Poke16(0xFDF8, fdf8);
        m.Bus.Poke16(0xE2B4, (ushort)((10 + sample * 45) << 8));
        m.Bus.Poke16(0xE2B6, (ushort)((10 + sample * 45) << 8));
        return m;
    }

    /// <summary>Runs from the hook's real site to its first end; the donor's fall-through to 0B:D438 is its JMPR to D464.</summary>
    private static uint Run(Machine m, Hook hook)
    {
        m.Cpu.Jump(hook.Segment, hook.Offset);
        m.Run(200_000, x => hook.Ends.Contains(x.Cpu.Address));
        Assert.True(hook.Ends.Contains(m.Cpu.Address), $"{hook.Name} stopped at {m.Cpu.Address:X5}");
        return m.Cpu.Address == 0xBD438 ? 0xBD464 : m.Cpu.Address;
    }

    private static byte[] Memory(Machine m) =>
    [
        .. BitConverter.GetBytes(m.Bus.Peek16(0xE2B4)), .. BitConverter.GetBytes(m.Bus.Peek16(0x406A4)),
        .. Enumerable.Range(0, 6).Select(i => (byte)(m.Bus.Peek16((uint)(0xFBAB + i) & ~1u) >> (int)(((0xFBAB + i) & 1) * 8))),
    ];

    [Fact] // RF1, RF5
    public void EachHookIsStockWhenInactive()
    {
        var cases = 0;
        foreach (var hook in new[] { Spark, Afr, Throttle })
        foreach (var (mode, fdf8) in new (ushort, ushort)[] { (Overrun, 0x4000), (PartLoad, 0x4008), (Idle, 0x4008), (Overrun, 0x4004) })
        for (var cv = 0; cv < 4; cv++)
        for (var sample = 0; sample < 5; sample++)
        {
            if (hook != Afr && fdf8 == 0x4004) continue;
            var psw = (ushort)(0x0801 | cv << 1);
            var expected = Seed(Image(Settings, donor: true), mode, fdf8, psw, sample);
            var actual = Seed(Image(Settings, donor: false), mode, fdf8, psw, sample);
            var exit = Run(expected, hook);
            var context = $"{hook.Name} mode {mode:X4} FDF8 {fdf8:X4} CV {cv} sample {sample}";
            Assert.True(exit == Run(actual, hook), context + ": exit");
            for (var r = 0; r < 16; r++)
                Assert.True(expected.Cpu.R(r) == actual.Cpu.R(r), $"{context}: R{r} {expected.Cpu.R(r):X4} != {actual.Cpu.R(r):X4}");
            Assert.True(expected.Cpu.Psw == actual.Cpu.Psw, context + ": PSW");
            Assert.Equal(expected.Cpu.Sp, actual.Cpu.Sp);
            Assert.Equal(Memory(expected), Memory(actual));
            cases++;
        }
        Assert.True(cases > 150);
    }

    [Theory]
    [InlineData(10)] [InlineData(60)] [InlineData(200)]
    public void SparkImposesTheSetAngle(byte stock)
    {
        var m = Seed(Image(Settings, donor: false), Overrun, 0x4008, 0x0800, 0);
        m.Bus.Poke16(0xE2B4, (ushort)(stock << 8)); m.Bus.Poke16(0xE2B6, (ushort)(stock << 8));
        Assert.Equal(0xAD428u, Run(m, Spark));
        for (var i = 0; i < 6; i++)
        {
            var at = (uint)(0xFBAB + i);
            var value = (byte)(m.Bus.Peek16(at & ~1u) >> (int)((at & 1) * 8));
            Assert.True(0xE8 == value, $"cylinder {i}: {value:X2} with stock {stock}");
        }
        Assert.Equal(stock << 8, m.Bus.Peek16(0xE2B6));
    }

    [Fact]
    public void AfrFeedsTheRichTargetThroughTheSharedPath()
    {
        var m = Seed(Image(Settings, donor: false), Overrun, 0x4008, 0x0800, 0);
        Assert.Equal(0xBD456u, Run(m, Afr));
        Assert.Equal(184, m.Cpu.R(12)); Assert.Equal(184, m.Cpu.R(13));
        m = Seed(Image(Settings, donor: false), Overrun, 0x400C, 0x0800, 0);
        Assert.Equal(0xBD44Eu, Run(m, Afr));
    }

    [Theory]
    [InlineData(1000, 30, 2638)] [InlineData(0xFF00, 50, 0xFFFF)] [InlineData(1000, 255, 3731)]
    public void ThrottleAddsTheSetOpening(int r4, byte opening, int expected)
    {
        var settings = Settings.ToArray(); settings[6] = opening;
        var m = Seed(Image(settings, donor: false), Overrun, 0x4008, 0x0800, 0);
        m.Cpu.SetR(4, (ushort)r4);
        Assert.Equal(0x9B13Cu, Run(m, Throttle));
        Assert.Equal(expected, m.Cpu.R(4));
        Assert.Equal(expected, m.Bus.Peek16(0x406A4));
    }

    [Theory] // final review C1: with the pedal released the stock code skips the target path; a window runs it
    [InlineData(true, (ushort)(1 << 11), (ushort)0x4008, 0xBD3BAu)]
    [InlineData(true, (ushort)(1 << 11), (ushort)0x4000, 0xBD476u)]
    [InlineData(true, (ushort)(1 << 10), (ushort)0x4008, 0xBD476u)]
    [InlineData(false, (ushort)(1 << 10), (ushort)0x4000, 0xBD3BAu)]
    public void PedalReleasedRunsTheTargetPathOnlyDuringAWindow(bool released, ushort mode, ushort fdf8, uint expected)
    {
        foreach (var donor in new[] { false, true })
        {
            var m = Seed(Image(Settings, donor), mode, fdf8, 0x0803, 0);
            m.Bus.Poke16(0xFD44, (ushort)(released ? 8 : 0));
            m.Cpu.Jump(0x0B, 0xD3B2);
            m.Run(1_000, x => x.Cpu.Address is 0xBD3BA or 0xBD476);
            var stock = released ? 0xBD476u : 0xBD3BAu;
            Assert.Equal(donor ? stock : expected, m.Cpu.Address);
            Assert.Equal(0x0803, m.Cpu.Psw);
        }
    }

    [Theory]
    [InlineData(50, true)] [InlineData(49, false)]
    public void RichTargetSwitchesTheLambdaLoopOffFrom1600Rpm(byte rpm, bool open)
    {
        var m = Seed(Image(Settings, donor: false), Overrun, 0x4008, 0x0800, 0);
        m.Cpu.SetR(6, 0x80);   // the stock overrun target: lambda 1
        V8Fixture.PutByte(m, 0x4007F, rpm);
        m.Bus.Poke16(0xFD34, 0); m.Bus.Poke16(0xFD9A, 0); m.Bus.Poke16(0xFD42, 0); m.Bus.Poke16(0x40C5E, 0);
        m.Cpu.Jump(0x0B, 0xD42C);
        m.Run(200_000, x => x.Cpu.Address == 0xBD4D4);
        Assert.Equal(0xBD4D4u, m.Cpu.Address);
        Assert.Equal(open, (m.Bus.Peek16(0xFD9A) & 1 << 10) != 0);
    }
}

public sealed class PopsHookTestsOnV8 : PopsHookTests { protected override OverlayFixture Fixture => OverlayFixture.V8; }
public sealed class PopsHookTestsOnV9 : PopsHookTests { protected override OverlayFixture Fixture => OverlayFixture.V9; }
