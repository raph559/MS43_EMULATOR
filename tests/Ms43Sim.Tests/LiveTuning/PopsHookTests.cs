using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The three effects of a pops window: the fixed spark angle (0A:D424), the rich target through the shared AFR
/// branch (0B:D434) and the throttle opening (09:B138). Direct CPU tests from the real sites against the donor.
/// "Active" is FDF8.3 with the engine in fuelled overrun (FD2C.11).
/// </summary>
public sealed class PopsHookTests
{
    /// <summary>The throttle opening's limit, 20.0 deg TPS in 0.1 deg, and the 86A4 units it adds (54.625 X by shifts).</summary>
    private static (byte Limit, int Add) ThrottleLimit => (200, 10925);

    /// <summary>
    /// The flashed AFR byte, the real AFR x 10, and what the shared path then receives in R12 and R13: 70 (7.0) forces
    /// the rich path with 255.
    /// </summary>
    private static (byte Setting, int Passed) Afr => (70, 255);

    private const ushort Overrun = 1 << 11, PartLoad = 1 << 10, Idle = 1 << 9;
    private byte[] Settings => [1, 20, 23, Afr.Setting, 50, 158, 30, 20];

    private sealed record Hook(string Name, byte Segment, ushort Offset, uint[] Ends);

    private static readonly Hook Spark = new("spark", 0x0A, 0xD424, [0xAD428]);
    private static readonly Hook AfrHook = new("afr", 0x0B, 0xD434, [0xBD438, 0xBD44E, 0xBD456, 0xBD464]);
    private static readonly Hook Throttle = new("throttle", 0x09, 0xB138, [0x9B13C]);

    private byte[] Image(byte[] settings, bool donor)
    {
        var image = donor ? TestBins.CarBytes : V10Fixture.Bytes;
        if (!donor) settings.CopyTo(image, V10Fixture.Defaults);
        return image;
    }

    private static Machine Seed(byte[] image, ushort mode, ushort fdf8, ushort psw, int sample)
    {
        var m = V10Fixture.Direct(image, [0xCC, 0x00, 0xCC, 0x00]);
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
        foreach (var hook in new[] { Spark, AfrHook, Throttle })
        foreach (var (mode, fdf8) in new (ushort, ushort)[] { (Overrun, 0x4000), (PartLoad, 0x4008), (Idle, 0x4008), (Overrun, 0x4004) })
        for (var cv = 0; cv < 4; cv++)
        for (var sample = 0; sample < 5; sample++)
        {
            if (hook != AfrHook && fdf8 == 0x4004) continue;
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
        Assert.Equal(0xBD456u, Run(m, AfrHook));
        Assert.Equal(Afr.Passed, m.Cpu.R(12)); Assert.Equal(Afr.Passed, m.Cpu.R(13));
        m = Seed(Image(Settings, donor: false), Overrun, 0x400C, 0x0800, 0);
        Assert.Equal(0xBD44Eu, Run(m, AfrHook));
    }

    [Theory]
    [InlineData(1000, 30, 2638)] [InlineData(0xFF00, 50, 0xFFFF)]
    public void ThrottleAddsTheSetOpening(int r4, byte opening, int expected)
    {
        var settings = Settings.ToArray(); settings[6] = opening;
        var m = Seed(Image(settings, donor: false), Overrun, 0x4008, 0x0800, 0);
        m.Cpu.SetR(4, (ushort)r4);
        Assert.Equal(0x9B13Cu, Run(m, Throttle));
        Assert.Equal(expected, m.Cpu.R(4));
        Assert.Equal(expected, m.Bus.Peek16(0x406A4));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ThrottleAtOrAboveItsLimitAddsTheLimit(bool above)
    {
        var settings = Settings.ToArray(); settings[6] = above ? (byte)255 : ThrottleLimit.Limit;
        var m = Seed(Image(settings, donor: false), Overrun, 0x4008, 0x0800, 0);
        m.Cpu.SetR(4, 1000);
        Assert.Equal(0x9B13Cu, Run(m, Throttle));
        Assert.Equal(1000 + ThrottleLimit.Add, m.Cpu.R(4));
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
        V10Fixture.PutByte(m, 0x4007F, rpm);
        m.Bus.Poke16(0xFD34, 0); m.Bus.Poke16(0xFD9A, 0); m.Bus.Poke16(0xFD42, 0); m.Bus.Poke16(0x40C5E, 0);
        m.Cpu.Jump(0x0B, 0xD42C);
        m.Run(200_000, x => x.Cpu.Address == 0xBD4D4);
        Assert.Equal(0xBD4D4u, m.Cpu.Address);
        Assert.Equal(open, (m.Bus.Peek16(0xFD9A) & 1 << 10) != 0);
    }
}

/// <summary>
/// V10.2's real pops AFR, on the V10 image. In a window with the pops AFR below 14.7, pops_afr (0B:D434) keeps the car
/// file's own target byte in POPS_XSTOCK (BLIP_RATE's byte, free while no blip runs) and forces the shared rich path with
/// 255, which stores 91A0 = 2032. At the path's join (0B:D4D4), pops_fuel then writes 91A0 = (147 − A) × 4096 / A, or
/// the car file's own target made real when that is richer, and the logged target 91B9 = 91B8 × 4096 / (4096 + 91A0).
/// On every other pass with a rich target it makes that target real (every AFR real), and without one it runs the
/// displaced MOVBZ R12, 91B8h and returns to 0B:D4D8. Direct CPU tests from the real sites.
/// </summary>
public sealed class PopsRealAfrHookTests
{
    private const ushort Overrun = 1 << 11, Window = 0x4008;
    private const uint Enrichment = 0x411A0, Stoich = 0x411B8, XStock = 0x41FA7;
    private static readonly uint[] AfrEnds = [0xBD438, 0xBD44E, 0xBD456, 0xBD464], FuelEnds = [0xBD4D8, 0xBD4E8];

    private static Machine Seed(byte afr, ushort fdf8 = Window, ushort fd9a = 1 << 10, ushort enrichment = 2032, byte xStock = 128)
    {
        var image = V10Fixture.Bytes;
        new byte[] { 1, 20, 23, afr, 50, 158, 30, 20 }.CopyTo(image, V10Fixture.Defaults);
        var m = V10Fixture.Direct(image, [0xCC, 0x00, 0xCC, 0x00]);
        for (var r = 1; r < 16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + r * 19));
        m.Cpu.SetR(0, 0xF780);
        m.Cpu.Psw = 0x0800;
        m.Bus.Poke16(0xFD2C, Overrun);
        m.Bus.Poke16(0xFDF8, fdf8);
        m.Bus.Poke16(0xFD42, 1 << 13);
        m.Bus.Poke16(0xFD9A, fd9a);
        m.Bus.Poke16(Enrichment, enrichment);
        m.Bus.Poke16(Stoich, 0x5500 | 147);                            // 91B8 = 147 (14.7); 91B9 = 55h, which only a write changes
        V10Fixture.PutByte(m, XStock, xStock);
        return m;
    }

    private static uint Run(Machine m, ushort offset, uint[] ends)
    {
        m.Cpu.Jump(0x0B, offset);
        m.Run(200_000, x => ends.Contains(x.Cpu.Address));
        Assert.True(ends.Contains(m.Cpu.Address), $"stopped at {m.Cpu.Address:X5}");
        return m.Cpu.Address;
    }

    private static int Logged(Machine m) => m.Bus.Peek16(Stoich) >> 8;

    [Fact]
    public void ThePopsAfrForcesTheRichPathAndKeepsTheStockTarget()
    {
        var m = Seed(afr: 70, xStock: 0x55);
        m.Cpu.SetR(6, 140);                                            // the car file's own target byte on entry
        Assert.Equal(0xBD456u, Run(m, 0xD434, AfrEnds));
        Assert.Equal(255, m.Cpu.R(12)); Assert.Equal(255, m.Cpu.R(13));
        Assert.Equal(140, V10Fixture.Byte(m, XStock));
    }

    [Fact]
    public void AtTheLeanestPopsAfrTheStockPathIsUntouched()
    {
        var m = Seed(afr: 147, xStock: 0x55);
        m.Cpu.SetR(6, 140);
        Assert.Equal(0xBD456u, Run(m, 0xD434, AfrEnds));
        Assert.Equal(140, m.Cpu.R(12)); Assert.Equal(140, m.Cpu.R(13));
        Assert.Equal(0x55, V10Fixture.Byte(m, XStock));
    }

    [Theory] // 91A0 = (147 − A) × 4096 / A, hand-computed and truncated; 91B9 = 147 × 4096 / (4096 + 91A0)
    [InlineData((byte)50, 7946, 50)] [InlineData((byte)70, 4505, 70)] [InlineData((byte)98, 2048, 98)]
    [InlineData((byte)120, 921, 120)] [InlineData((byte)146, 28, 146)]
    public void TheFuelHookWritesTheRealPopsEnrichment(byte afr, int enrichment, int logged)
    {
        var m = Seed(afr);
        var before = Enumerable.Range(0, 16).Select(r => m.Cpu.R(r)).ToArray();
        Assert.Equal(0xBD4E8u, Run(m, 0xD4D4, FuelEnds));
        Assert.InRange(m.Bus.Peek16(Enrichment), enrichment - 1, enrichment + 1);
        Assert.Equal(logged, Logged(m));
        foreach (var r in new[] { 0, 1, 2, 3, 6, 7, 8, 9, 10, 11, 14, 15 })
            Assert.True(before[r] == m.Cpu.R(r), $"R{r} changed");
    }

    [Theory] // the car file's own enrichment, made real (every AFR real), wins when it is richer than the pops AFR's
    [InlineData((byte)255, 4032, 74)] [InlineData((byte)184, 1146, 114)] [InlineData((byte)140, 921, 120)] [InlineData((byte)100, 921, 120)]
    public void TheCarFilesRicherTargetWins(byte xStock, int enrichment, int logged)
    {
        var m = Seed(afr: 120, xStock: xStock);
        Assert.Equal(0xBD4E8u, Run(m, 0xD4D4, FuelEnds));
        Assert.InRange(m.Bus.Peek16(Enrichment), enrichment - 1, enrichment + 1);
        Assert.Equal(logged, Logged(m));
    }

    [Theory] // no rich target in this pass (FD9A.10 clear, so 91A0 was cleared or never set), in a window or not
    [InlineData((ushort)0, (ushort)0x4000, (byte)70)] [InlineData((ushort)0, (ushort)Window, (byte)70)]
    [InlineData((ushort)2032, (ushort)Window, (byte)70)]
    public void WithoutARichTargetTheFuelHookIsStock(ushort enrichment, ushort fdf8, byte afr) =>
        Stock(Seed(afr, fdf8, fd9a: 0, enrichment));

    /// <summary>
    /// V10.2, every AFR real (spec §6b): with the rich path on in this pass (FD9A.10), the enrichment the shared path
    /// stored, L = (X − 128) × 16, becomes L × 4096 / (4096 − L), signed and truncated toward zero, so the byte the XDF
    /// shows as AFR 14.7 × (384 − X) / 256 gives that AFR. The logged target is the stock display from L,
    /// 91B8 × (4096 − L) / 4096, so its numbers stay as today. This holds outside a window and in one where pops does not
    /// act: RAL, a pops AFR of 14.7.
    /// </summary>
    [Theory]
    [InlineData((byte)255, 4032, 74)] [InlineData((byte)184, 1146, 114)] [InlineData((byte)172, 850, 121)]
    [InlineData((byte)112, -240, 156)]
    public void EveryRichTargetIsReal(byte x, int real, int logged)
    {
        foreach (var (fdf8, afr) in new (ushort, byte)[] { (0x4000, 70), (Window | 4, 70), (Window, 147) })
        {
            var m = Seed(afr, fdf8, enrichment: (ushort)((x - 128) * 16));
            var before = Enumerable.Range(0, 16).Select(r => m.Cpu.R(r)).ToArray();
            var context = $"X {x}, FDF8 {fdf8:X4}, pops AFR {afr}";
            Assert.True(Run(m, 0xD4D4, FuelEnds) == 0xBD4E8u, context);
            Assert.True((ushort)real == m.Bus.Peek16(Enrichment), $"{context}: 91A0 {(short)m.Bus.Peek16(Enrichment)}");
            Assert.True(logged == Logged(m), $"{context}: 91B9 {Logged(m)}");
            Assert.True(m.Cpu.R(5) == 0x1000 && m.Cpu.R(12) == 147, $"{context}: R5 {m.Cpu.R(5):X4}, R12 {m.Cpu.R(12)}");
            foreach (var r in new[] { 0, 1, 2, 3, 6, 7, 8, 9, 10, 11, 14, 15 })
                Assert.True(before[r] == m.Cpu.R(r), $"{context}: R{r} changed");
        }
    }

    [Fact] // a window's pass that pops_afr did not force (a window begun between pops_afr and the join): the car file's, real
    public void AWindowPassPopsDidNotForceIsRealToo()
    {
        var m = Seed(afr: 70, enrichment: 896);
        Assert.Equal(0xBD4E8u, Run(m, 0xD4D4, FuelEnds));
        Assert.Equal(1146, m.Bus.Peek16(Enrichment));
        Assert.Equal(114, Logged(m));
    }

    private static void Stock(Machine m)
    {
        var enrichment = m.Bus.Peek16(Enrichment);
        Assert.Equal(0xBD4D8u, Run(m, 0xD4D4, FuelEnds));
        Assert.Equal(147, m.Cpu.R(12));                                // the displaced MOVBZ R12, 91B8h
        Assert.Equal(enrichment, m.Bus.Peek16(Enrichment));
        Assert.Equal(0x55, Logged(m));
    }
}
