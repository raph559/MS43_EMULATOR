using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V10's blip at pops' three hook sites: the throttle store (09:B138) adds BLIP_ADD while GUARD is clear, the
/// overrun-cut gate (0A:A7BC) holds the cut off, and the spark call (0A:D424) fires at 140's angle in fuelled
/// overrun. With the blip off, every hook decides as V9's does (RF5). Direct CPU tests from the real sites.
/// </summary>
public sealed class BlipHookTests
{
    private const ushort Overrun = 1 << 11, PartLoad = 1 << 10, Idle = 1 << 9;
    private const uint Stay = 0xAB0F2, Cut = 0xAA7C8;
    private static readonly byte[] Pops = [1, 20, 23, 184, 50, 158, 30, 20];

    private sealed record Hook(string Name, byte Segment, ushort Offset, uint[] Ends);

    private static readonly Hook Spark = new("spark", 0x0A, 0xD424, [0xAD428]);
    private static readonly Hook Throttle = new("throttle", 0x09, 0xB138, [0x9B13C]);
    private static readonly Hook Gate = new("gate", 0x0A, 0xA7BC, [Stay, Cut]);

    private static byte[] Image(bool v10, Action<byte[]>? change = null)
    {
        var image = v10 ? V10Fixture.Bytes : V10Fixture.Bytes;
        Pops.CopyTo(image, V10Fixture.Defaults);
        change?.Invoke(image);
        return image;
    }

    private static Machine Seed(byte[] image, ushort mode, ushort fdf8, ushort psw, int sample, ushort fd2e = 0, ushort fd30 = 1 << 2)
    {
        var m = V10Fixture.Direct(image, [0xCC, 0x00, 0xCC, 0x00]);
        for (var r = 1; r < 16; r++) m.Cpu.SetR(r, (ushort)(0x9100 + r * 19 + sample * 0x0101));
        m.Cpu.SetR(0, 0xF780);
        m.Cpu.SetR(4, (ushort)(1000 + sample * 9000));
        m.Cpu.Psw = psw;
        V10Fixture.PutByte(m, 0x4007F, 100);
        V10Fixture.PutByte(m, 0x4047F, 80);
        V10Fixture.PutByte(m, 0x40433, 200);
        m.Bus.Poke16(0xFD2E, fd2e);
        m.Bus.Poke16(0xFD30, fd30);
        m.Bus.Poke16(0xFD2C, mode);
        m.Bus.Poke16(0xFDF8, fdf8);
        m.Bus.Poke16(0x41FBC, 50);
        V10Fixture.PutByte(m, 0x41FBE, 100);
        m.Bus.Poke16(0xE2B4, (ushort)((10 + sample * 45) << 8));
        m.Bus.Poke16(0xE2B6, (ushort)((10 + sample * 45) << 8));
        return m;
    }

    private static uint Run(Machine m, Hook hook)
    {
        m.Cpu.Jump(hook.Segment, hook.Offset);
        m.Run(200_000, x => hook.Ends.Contains(x.Cpu.Address));
        Assert.True(hook.Ends.Contains(m.Cpu.Address), $"{hook.Name} stopped at {m.Cpu.Address:X5}");
        return m.Cpu.Address;
    }

    private static byte[] Memory(Machine m) =>
    [
        .. BitConverter.GetBytes(m.Bus.Peek16(0xE2B4)), .. BitConverter.GetBytes(m.Bus.Peek16(0x406A4)),
        .. BitConverter.GetBytes(m.Bus.Peek16(0xFDF8)), .. BitConverter.GetBytes(m.Bus.Peek16(0x41FBC)),
    ];

    [Fact] // RF5
    public void WithTheBlipOffEveryHookIsV9s()
    {
        var cases = 0;
        foreach (var hook in new[] { Spark, Throttle, Gate })
        foreach (var (mode, fdf8) in new (ushort, ushort)[] { (Overrun, 0x4000), (Overrun, 0x4008), (PartLoad, 0x4008), (Idle, 0x4000), (Overrun, 0x4018) })
        foreach (var fd2e in new ushort[] { 0, 1 << 13, 1 << 6 })
        for (var cv = 0; cv < 4; cv++)
        for (var sample = 0; sample < 3; sample++)
        {
            var psw = (ushort)(0x0801 | cv << 1);
            var expected = Seed(Image(v10: false), mode, fdf8, psw, sample, fd2e);
            var actual = Seed(Image(v10: true), mode, fdf8, psw, sample, fd2e);
            var context = $"{hook.Name} mode {mode:X4} FDF8 {fdf8:X4} FD2E {fd2e:X4} CV {cv} sample {sample}";
            Assert.True(Run(expected, hook) == Run(actual, hook), context + ": exit");
            for (var r = 0; r < 16; r++)
                Assert.True(expected.Cpu.R(r) == actual.Cpu.R(r), $"{context}: R{r} {expected.Cpu.R(r):X4} != {actual.Cpu.R(r):X4}");
            Assert.True(expected.Cpu.Psw == actual.Cpu.Psw, context + ": PSW");
            Assert.Equal(expected.Cpu.Sp, actual.Cpu.Sp);
            Assert.True(Memory(expected).SequenceEqual(Memory(actual)), context + ": memory");
            cases++;
        }
        Assert.True(cases > 400);
    }

    private static Machine Blipping(ushort mode = Overrun, ushort add = 5000, bool guard = false, Action<byte[]>? change = null,
        ushort fd2e = 1 << 6, ushort fd30 = 1 << 2)
    {
        var m = Seed(Image(v10: true, change), mode, (ushort)(0x4000 | 1 << V10Fixture.BlipOn | (guard ? 1 << V10Fixture.Guard : 0)),
            0x0800, 0, fd2e, fd30);
        m.Bus.Poke16(V10Fixture.BlipAdd, add);
        V10Fixture.PutByte(m, V10Fixture.BlipTgt, 120);                 // a running blip has its target: 3,840 rpm, above the 3,200
        V10Fixture.PutByte(m, V10Fixture.BlipSpk, 0x75);                // and its last spark, here the flashed blip angle (V10.2)
        return m;
    }

    [Theory]
    [InlineData((ushort)1000, (ushort)5000, (ushort)6000)] [InlineData((ushort)0xF000, (ushort)0x2000, (ushort)0xFFFF)]
    public void DuringABlipTheThrottleAddsBlipAdd(ushort pedalMap, ushort add, ushort expected)
    {
        var m = Blipping(add: add);
        m.Cpu.SetR(4, pedalMap);
        Run(m, Throttle);
        Assert.Equal(expected, m.Bus.Peek16(0x406A4));
    }

    [Fact] // RF4
    public void TheGuardZeroesTheBlipsAdd()
    {
        var m = Blipping(guard: true);
        m.Cpu.SetR(4, 1000);
        Run(m, Throttle);
        Assert.Equal(1000, m.Bus.Peek16(0x406A4));
    }

    [Theory]
    [InlineData((ushort)(1 << 13), (ushort)0)] [InlineData((ushort)0, (ushort)(1 << 2))]
    public void DuringABlipTheGateStaysFuelled(ushort immediate, ushort request)
    {
        // pops is off and the clutch is in: the pops conditions fail, yet the blip keeps the fuel on
        var m = Blipping(change: b => b[V10Fixture.Defaults] = 0, fd2e: (ushort)(1 << 6 | immediate), fd30: request);
        var before = Enumerable.Range(0, 16).Select(r => m.Cpu.R(r)).ToArray();
        var psw = m.Cpu.Psw;
        Assert.Equal(Stay, Run(m, Gate));
        for (var r = 0; r < 16; r++) Assert.True(before[r] == m.Cpu.R(r), $"R{r} changed");
        Assert.Equal(psw, m.Cpu.Psw);
    }

    [Fact]
    public void DuringABlipTheSparkIsTheBlipAngle()
    {
        var m = Blipping();
        Run(m, Spark);
        Assert.Equal((int)V10Fixture.Group(140)[7], m.Bus.Peek16(0xE2B4) >> 8);         // the flashed blip angle, +20.25 deg on V10.1
        Assert.Equal(0, m.Bus.Peek16(0xE2B4) & 255);

        var notFuelled = Blipping(mode: 1 << 12);                    // in the overrun cut: the stock angle
        var stock = notFuelled.Bus.Peek16(0xE2B4);
        Run(notFuelled, Spark);
        Assert.Equal(stock, notFuelled.Bus.Peek16(0xE2B4));
    }

    /// <summary>
    /// V10.2's cap: while a blip runs, the spark is 141's cap angle (flashed 0, −23.625°) whenever the engine speed (807F) is
    /// above the blip's target (BLIP_TGT, both in 32 rpm units), and 140's blip angle (0x75, +20.25°) at or below it.
    /// </summary>
    private static Machine Capping(byte target, byte rpm, byte cap = 0)
    {
        var m = Blipping(change: b => b[V10Fixture.Defaults + 3 * 8 + 7] = cap);
        V10Fixture.PutByte(m, V10Fixture.BlipTgt, target);
        V10Fixture.PutByte(m, 0x4007F, rpm);
        return m;
    }

    [Theory]
    [InlineData((byte)75, (byte)76)] [InlineData((byte)75, (byte)90)] [InlineData((byte)70, (byte)72)]
    public void TheCapAngleFiresAboveTheTarget(byte target, byte rpm)
    {
        var m = Capping(target, rpm);
        Run(m, Spark);
        Assert.Equal(0x0000, m.Bus.Peek16(0xE2B4));                   // E2B5 = 0, −23.625°; E2B4 = 0 as for every fixed angle
    }

    [Theory]
    [InlineData((byte)75, (byte)75)] [InlineData((byte)75, (byte)74)] [InlineData((byte)75, (byte)60)]
    public void TheCapNeverFiresAtOrBelowTheTarget(byte target, byte rpm)
    {
        var m = Capping(target, rpm);
        Run(m, Spark);
        Assert.Equal(0x7500, m.Bus.Peek16(0xE2B4));                   // the blip angle, +20.25°
    }

    [Theory] // 0x75 is the blip angle itself: the cap is off and the spark is V10.1's
    [InlineData((byte)0x40)] [InlineData((byte)0x75)]
    public void TheCapAngleIsTheSetting(byte cap)
    {
        var m = Capping(75, 90, cap);
        Run(m, Spark);
        Assert.Equal(cap << 8, m.Bus.Peek16(0xE2B4));
    }

    /// <summary>
    /// V10.2's review (2026-11-08): a cap set above the blip angle would add advance above the target, the opposite of a
    /// cap. Above the target the spark is the lower of the two, so at or above the blip angle (0x75 here) the cap is off.
    /// </summary>
    [Theory]
    [InlineData((byte)0x76)] [InlineData((byte)0x8F)]                  // just above the blip angle, and the cap's top, +30.0°
    public void ACapAboveTheBlipAngleIsOff(byte cap)
    {
        var m = Capping(75, 90, cap);
        Run(m, Spark);
        Assert.Equal(0x7500, m.Bus.Peek16(0xE2B4));
        Assert.Equal(0x75, V10Fixture.Byte(m, V10Fixture.BlipSpk));
    }

    /// <summary>
    /// V10.2: the spark call arms each coil's charge for the angle before it, so an advance written while a coil charges
    /// cuts that charge by the advance's time (2026-11-08 drive: the cap's 44 deg release at 2,624 rpm left cylinder 4
    /// 0.21 ms of its 2.35, igcfb_5 detected). After the cap, an advance rises at most 807F / 4 steps of 0.375 deg a spark
    /// from the last angle the blip wrote (BLIP_SPK), under 0.5 ms of charge at any speed; a retard, the cap, is whole at
    /// once. A blip's start is whole (BLIP_SPK = FFh, <see cref="TheBlipsFirstSparkIsWhole"/>).
    /// </summary>
    [Theory]
    [InlineData((byte)0, (byte)100, (byte)25)]                         // from the cap at 3,200 rpm
    [InlineData((byte)0, (byte)82, (byte)20)]                          // the drive's release at 2,624 rpm
    [InlineData((byte)29, (byte)45, (byte)40)]                         // from a cap set at -12.75 deg, at 1,440 rpm
    [InlineData((byte)100, (byte)100, (byte)0x75)]                     // within a step: the whole angle
    public void AnAdvanceRisesAStepASpark(byte last, byte rpm, byte expected)
    {
        var m = Capping(target: 120, rpm: rpm);
        V10Fixture.PutByte(m, V10Fixture.BlipSpk, last);
        Run(m, Spark);
        Assert.Equal(expected << 8, m.Bus.Peek16(0xE2B4));
        Assert.Equal(expected, V10Fixture.Byte(m, V10Fixture.BlipSpk));
    }

    [Theory] // the owner, 2026-11-08: "i want them to feel very sharp": a blip's first spark is its whole angle, as V10.1
    [InlineData((byte)45)] [InlineData((byte)100)]
    public void TheBlipsFirstSparkIsWhole(byte rpm)
    {
        var m = Capping(target: 120, rpm: rpm);
        V10Fixture.PutByte(m, V10Fixture.BlipSpk, 0xFF);                // as bt_go leaves it
        Run(m, Spark);
        Assert.Equal(0x7500, m.Bus.Peek16(0xE2B4));
        Assert.Equal(0x75, V10Fixture.Byte(m, V10Fixture.BlipSpk));
    }

    [Fact]
    public void ARetardIsWholeAtOnce()
    {
        var m = Capping(target: 75, rpm: 90);                          // above the target, from the blip angle: the cap
        Run(m, Spark);
        Assert.Equal(0x0000, m.Bus.Peek16(0xE2B4));
        Assert.Equal(0, V10Fixture.Byte(m, V10Fixture.BlipSpk));
    }

    [Fact]
    public void NoBlipNoCap()
    {
        // no blip and no window, the speed above a stale target: the stock path leaves the angle as it came
        var m = Seed(Image(v10: true), Overrun, 0x4000, 0x0800, 0, 1 << 6);
        V10Fixture.PutByte(m, V10Fixture.BlipTgt, 75);
        V10Fixture.PutByte(m, 0x4007F, 90);
        var stock = m.Bus.Peek16(0xE2B4);
        Run(m, Spark);
        Assert.Equal(stock, m.Bus.Peek16(0xE2B4));
    }

    /// <summary>
    /// The in-cut handler (0A:A9EE) re-checks the engine speed at 0A:AAA0 and refuels only below the re-entry rpm (81AB).
    /// A blip that starts during a running cut takes the stock re-entry at 0A:AABA at once (Task 7's finding).
    /// </summary>
    private static Machine InCut(bool blip)
    {
        var m = Seed(Image(v10: true), 1 << 12, (ushort)(0x4000 | (blip ? 1 << V10Fixture.BlipOn : 0)), 0x0800, 0, 1 << 6);
        m.Bus.Poke16(0xFD9A, 0); m.Bus.Poke16(0xFD48, 0); m.Bus.Poke16(0xFD36, 0);
        V10Fixture.PutByte(m, 0x4007F, 100);                          // 3,200 rpm, above the re-entry rpm
        V10Fixture.PutByte(m, 0x401AB, 40);
        return m;
    }

    [Fact]
    public void DuringABlipARunningCutTakesTheStockReentry()
    {
        var m = InCut(blip: true);
        m.Cpu.Jump(0x0A, 0xAAA0);
        m.Run(2_000, x => x.Cpu.Address is 0xAAABA or 0xAAAA4 or 0xAB0F2);
        Assert.Equal(0xAAABAu, m.Cpu.Address);
    }

    /// <summary>
    /// V10.1: the blip path runs the stock BCMP FD9Ah.11, FD48h.2 and BMOV FD9Ah.11, PSW.2 before it jumps to the
    /// re-entry, so FD9A.11 and the flags at 0A:AABA are what a stock pass leaves at 0A:AAA8.
    /// </summary>
    [Fact]
    public void DuringABlipTheCutCheckStillUpdatesFd9aAsStock()
    {
        foreach (var (fd9a, fd48) in new (ushort, ushort)[] { (0, 0), (1 << 11, 0), (0, 1 << 2), (1 << 11, 1 << 2) })
        foreach (var psw in new ushort[] { 0x0800, 0x0805, 0x080A, 0x081F })
        {
            Machine CutCheck(byte[] image, ushort fdf8, uint end)
            {
                var m = Seed(image, 1 << 12, fdf8, psw, 0, 1 << 6);
                m.Bus.Poke16(0xFD9A, fd9a); m.Bus.Poke16(0xFD48, fd48);
                m.Cpu.Jump(0x0A, 0xAAA0);
                m.Run(2_000, x => x.Cpu.Address == end);
                Assert.Equal(end, m.Cpu.Address);
                return m;
            }
            var stock = CutCheck(TestBins.CarBytes, 0x4000, 0xAAAA8);
            var blip = CutCheck(Image(v10: true), 0x4000 | 1 << V10Fixture.BlipOn, 0xAAABA);
            var context = $"FD9A {fd9a:X4} FD48 {fd48:X4} PSW {psw:X4}";
            Assert.True(stock.Bus.Peek16(0xFD9A) == blip.Bus.Peek16(0xFD9A), context + ": FD9A");
            Assert.True(stock.Cpu.Psw == blip.Cpu.Psw, $"{context}: PSW {stock.Cpu.Psw:X4} != {blip.Cpu.Psw:X4}");
            for (var r = 0; r < 16; r++) Assert.True(stock.Cpu.R(r) == blip.Cpu.R(r), $"{context}: R{r}");
        }
    }

    [Fact] // RF5
    public void WithTheBlipOffTheCutCheckIsStock()
    {
        foreach (var (fd9a, fd48) in new (ushort, ushort)[] { (0, 0), (1 << 11, 0), (0, 1 << 2), (1 << 11, 1 << 2) })
        foreach (var psw in new ushort[] { 0x0800, 0x0805, 0x080A })
        {
            Machine Prepare(byte[] image)
            {
                var m = Seed(image, 1 << 12, 0x4000, psw, 0, 1 << 6);
                m.Bus.Poke16(0xFD9A, fd9a); m.Bus.Poke16(0xFD48, fd48);
                m.Cpu.Jump(0x0A, 0xAAA0);
                m.Run(2_000, x => x.Cpu.Address == 0xAAAA4);
                Assert.Equal(0xAAAA4u, m.Cpu.Address);
                return m;
            }
            var expected = Prepare(TestBins.CarBytes); var actual = Prepare(Image(v10: true));
            var context = $"FD9A {fd9a:X4} FD48 {fd48:X4} PSW {psw:X4}";
            Assert.True(expected.Cpu.Psw == actual.Cpu.Psw, context + ": PSW");
            for (var r = 0; r < 16; r++) Assert.True(expected.Cpu.R(r) == actual.Cpu.R(r), $"{context}: R{r}");
        }
    }

    [Fact] // RF2
    public void AfterTheBlipTheStockStoreReturns()
    {
        var m = Blipping();
        m.Bus.Poke16(0xFDF8, 0x4000);                               // BLIP_ON clear, BLIP_ADD left over
        m.Cpu.SetR(4, 1234);
        Run(m, Throttle);
        Assert.Equal(1234, m.Bus.Peek16(0x406A4));
    }
}
