using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.EngineModel;

/// <summary>A machine whose CPU only spins (TestMachine "0DFF"), except the warm-idle run on the car file.</summary>
public class EngineScriptedTests
{
    /// <summary>The 30 s warm-idle run's state hash before plan 4 Task 3 (commit 03ad801): the scripted speed and the breathing law must not move it.</summary>
    const string WarmIdleHash = "57225493b73172f4b2a8c3d94f22c5651e5b42dca96e2174827bade8d0a1e307";

    static EngineScenario Quick => EngineScenario.WarmIdle with { CrankAtSeconds = 0.1, StarterSeconds = 0.4 };

    [Fact] public void WarmIdle_IsUnchanged() =>
        Assert.Equal(WarmIdleHash, StartupRun.Run(TestImages.Car, new(Seconds: 30, Engine: EngineScenario.WarmIdle)).StateHash);

    [Fact] public void CommandedSpeed_HoldsTheShaft_WithoutCombustion()
    {
        var t = TestMachine.Create("0DFF"); t.M.Watchdog.Disable();
        var e = Engine.Attach(t.M, Quick);
        t.M.Scheduler.Advance(24_000_000);
        e.SetCommandedSpeed(2500);
        t.M.Scheduler.Advance(72_000_000);                                    // 2 s more, nothing fires
        Assert.InRange(e.Shaft.Rpm, 2490, 2510);                              // still turning: a cut must not stall a driven engine
        Assert.Equal(EnginePhase.Running, e.Phase);
        Assert.False(e.Combusting);
    }

    [Fact] public void ReleasingTheCommand_GivesTheEngineBackToTheIdlePlant()
    {
        var t = TestMachine.Create("0DFF"); t.M.Watchdog.Disable();
        var e = Engine.Attach(t.M, Quick);
        e.SetCommandedSpeed(2500);
        t.M.Scheduler.Advance(24_000_000);
        e.SetCommandedSpeed(null);
        t.M.Scheduler.Advance(96_000_000);                                    // 3 s more, nothing fires: it coasts down and stops
        Assert.Equal((EnginePhase.Stopped, 0.0), (e.Phase, e.Rpm));
        Assert.Contains(e.Phases, p => p.Phase == EnginePhase.Stopping && p.Cycle > 24_000_000);
    }

    [Fact] public void TheBreathingLaw_FeedsAN0_FromTheThrottleAngle()
    {
        var t = TestMachine.Create("0DFF"); t.M.Watchdog.Disable();
        var e = Engine.Attach(t.M, Quick with { Law = BreathingLaw.Car });
        e.SetCommandedSpeed(2500);
        t.M.Scheduler.Advance(24_000_000);                                    // the throttle rests at limp-home, 4.9°; P2.12 is an input, so d = 0
        var expected = BreathingLaw.Car.AirflowKgPerHour(2500, t.M.Throttle.Angle, 0, 101.3, 33, 90, 0, 1e9);
        Assert.InRange(expected, 30, 45);                                     // the throttle's air above closed, ~37.5 kg/h
        Assert.Equal(expected, e.AirflowKgPerHour, 3);
        Assert.Equal(EngineSensors.MafVolts(e.AirflowKgPerHour), t.M.Board.AnalogVolts(0));
    }

    [Fact] public void TheTemperaturesGiven_ChangeTheDensity()
    {
        var t = TestMachine.Create("0DFF"); t.M.Watchdog.Disable();
        var e = Engine.Attach(t.M, Quick with { Law = BreathingLaw.Car });
        e.SetCommandedSpeed(2500);
        e.SetTemperatures(intakeC: -9.75, coolantC: -20);
        t.M.Scheduler.Advance(24_000_000);
        var cold = BreathingLaw.Car.AirflowKgPerHour(2500, t.M.Throttle.Angle, 0, 101.3, -9.75, -20, 0, 1e9);
        Assert.Equal(cold, e.AirflowKgPerHour, 3);
    }

    [Fact] public void ACommandedStarter_TurnsTheShaft_UntilReleased()
    {
        var t = TestMachine.Create("0DFF"); t.M.Watchdog.Disable();
        var e = Engine.Attach(t.M, Quick with { CrankAtSeconds = null });       // no starter of its own
        t.M.Scheduler.Advance(24_000_000);
        Assert.Equal((EnginePhase.Stopped, 0.0), (e.Phase, e.Rpm));
        e.StartStarter(160);
        t.M.Scheduler.Advance(24_000_000 + 7_200_000);                        // 0.3 s: up to speed after 0.2 s
        Assert.Equal((EnginePhase.Starter, 160.0), (e.Phase, e.Rpm));
        e.StopStarter();
        t.M.Scheduler.Advance(24_000_000 + 7_200_000 + 48_000_000);            // nothing fires: it coasts down and stops
        Assert.Equal(new[] { EnginePhase.Stopped, EnginePhase.Starter, EnginePhase.Stopping, EnginePhase.Stopped },
            e.Phases.Select(p => p.Phase).ToArray());
    }
}
