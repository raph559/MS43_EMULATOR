using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.EngineModel;

/// <summary>A machine whose CPU only spins (TestMachine "0DFF"); the test plays the ECU through SFR writes.</summary>
public class EngineTests
{
    static EngineScenario Quick => EngineScenario.WarmIdle with { CrankAtSeconds = 0.1, StarterSeconds = 0.4 };

    [Fact] public void IdleSpeedFor_ThirtyFourPercent_Is899Rpm() => Assert.Equal(899.48, Engine.IdleSpeedFor(0.34), 2);

    [Fact] public void NoCombustion_TheEngineStopsWhenTheStarterReleases()
    {
        var t = TestMachine.Create("0DFF");
        var e = Engine.Attach(t.M, Quick);
        t.M.Scheduler.Advance(48_000_000);                                   // 2 s, nothing fires
        Assert.Equal(new[] { EnginePhase.Stopped, EnginePhase.Starter, EnginePhase.Stopping, EnginePhase.Stopped },
            e.Phases.Select(p => p.Phase).ToArray());
        Assert.Equal((0.0, true), (e.Rpm, e.Shaft.CrankTeeth > 0));
    }

    [Fact] public void Combustion_KeepsItRunning_WhereTheValveFlowMeetsTheIdleLoad()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFC2, 0x1041);         // P2.0, P2.6, P2.12 outputs
        t.M.Watchdog.Disable();                                             // it would reset the machine (and P2) at 131,072 cycles
        var e = Engine.Attach(t.M, Quick);
        FakeEcu.Fire(t, sparkPin: 6, injectorPin: 0, every: 480_000, valveLowShare: 0.34);   // every 20 ms; P2.12 low 3.4 ms of 10 ms
        t.M.Scheduler.Advance(96_000_000);                                   // 4 s
        Assert.Equal(EnginePhase.Running, e.Phase);
        Assert.InRange(e.Rpm, 890, 909);
    }

    [Fact] public void BurnPulse_FollowsEverySpark_For1Point2ms_AndRetriggers()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFC2, 0x0040).WriteSfr(0xFFC0, 0x0040);   // P2.6 output, high (dwelling)
        Engine.Attach(t.M, EngineScenario.WarmIdle);
        t.M.Scheduler.Schedule(10_000, "spark", () => t.WriteSfr(0xFFC0, 0));
        t.M.Scheduler.Schedule(20_000, "dwell", () => t.WriteSfr(0xFFC0, 0x0040));
        t.M.Scheduler.Schedule(30_000, "spark", () => t.WriteSfr(0xFFC0, 0));
        t.M.Scheduler.Advance(30_000 + 28_799); Assert.True(t.M.Board.InputLevel(3, 6));
        t.M.Scheduler.Advance(30_000 + 28_800); Assert.False(t.M.Board.InputLevel(3, 6));
    }

    [Fact] public void Battery_FollowsThePhases()
    {
        var t = TestMachine.Create("0DFF");
        Engine.Attach(t.M, Quick);
        double An5() => t.M.Board.AnalogVolts(5) * 5.2;
        var before = An5(); t.M.Scheduler.Advance(4_800_000);                // 0.2 s: starter
        var starter = An5(); t.M.Scheduler.Advance(24_000_000);               // 1 s: stopped again (no combustion)
        Assert.Equal((12.6, 10.5, 12.6), (Math.Round(before, 2), Math.Round(starter, 2), Math.Round(An5(), 2)));
    }
}
