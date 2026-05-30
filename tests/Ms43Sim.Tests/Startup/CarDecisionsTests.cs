using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

/// <summary>The car file, warm restart, 15 s, with the decision log (about 45 ignition calls a second at 900 rpm).</summary>
public sealed class CarDecisions
{
    public DecisionLog Log { get; private set; } = null!;
    public CarDecisions() => StartupRun.Run(TestImages.Car, new(Seconds: 15, Engine: EngineScenario.WarmIdle, Instrument: m => Log = new DecisionLog(m)));
}

public class CarDecisionsTests : IClassFixture<CarDecisions>
{
    static readonly Calibration Cal = new(TestBins.CarBytes);
    readonly DecisionLog log;
    public CarDecisionsTests(CarDecisions f) => log = f.Log;
    // 9.5 s, 6.5 s after the crank starts. The idle path (FD36.9) begins once the speed, falling
    // after the start, reaches the idle target or turns back up (0B:1C9E-0B:1D38); this engine flares
    // to 971 rpm and its idle path begins at 8.97 s, so the calls before it are on the start path.
    const ulong FromCycle = 228_000_000;

    // The lookups first, on the car file's bytes, with the Reference facts' worked values.
    [Fact] public void Lookups_GiveTheWarmIdleWorkedValues()
    {
        Assert.Equal(89, Calibration.Map(Cal.Axis16(0x7506E), Cal.Axis16(0x7507C), Cal.Values8(0x775D4, 36), 900, 2495));
        Assert.Equal(112, Calibration.Curve(Cal.Axis16(0x74FAA), Cal.Values8(0x76FCD, 12), 0x8000));
        Assert.Equal(112, Calibration.Scale(112, 255));
        Assert.Equal(2381, Recipes.InjectorConstant(Cal));
        Assert.Equal((104, 136), (Recipes.DeadTime(Cal, 137), Recipes.DeadTime(Cal, 124)));
        Assert.Equal((442, 515), (Recipes.BaseDwell(Cal, 137, true, 0), Recipes.BaseDwell(Cal, 124, true, 0)));
    }

    [Fact] public void IgnitionAngles_AreTheIdleMapPlusTheIdleIntervention_AtEveryCall()
    {
        var calls = log.Ignition.Where(c => c.Cycle >= FromCycle).ToList();
        Assert.True(calls.Count > 200, $"{calls.Count} calls");
        foreach (var c in calls)
        {
            int E(uint a) => c.Entry[a];
            Assert.Equal((2, true, true, 0), (E(0xFD9A) & 7, (E(0xFD2C) & 0x0200) != 0, (E(0xFD36) & 0x0200) != 0, E(0xFD48) & 1));   // the idle path's guards
            var angle = Recipes.BaseAngle(Cal, E);
            Assert.Equal(angle, c.Result[0xE2B4] >> 8);
            for (var i = 0; i < 6; i++)
                Assert.Equal(Recipes.CylinderAngle(angle, c.Result[0x83A2u + (uint)i], E(0x80E1u + (uint)i)), c.Result[0xFBABu + (uint)i]);
        }
    }

    [Fact] public void DeadTimeAndDwell_FollowTheBatteryCurves_AtEveryCall()
    {
        var calls = log.Supply.Where(c => c.Cycle >= 24_000_000).ToList();
        Assert.True(calls.Count > 1_000);
        foreach (var c in calls)
        {
            Assert.Equal(Recipes.DeadTime(Cal, c.Entry[0xFBD5]), c.Result[0xE47C]);
            Assert.Equal(Recipes.BaseDwell(Cal, c.Entry[0xFBD5], (c.Entry[0xFD32] & 0x0008) != 0, (sbyte)c.Entry[0x80ED]), c.Result[0xE4B8]);
        }
    }
}
