using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

/// <summary>
/// The car file as flashed, blank EEPROM: a warm restart, idle, the key off at 75 s (the after-start
/// enrichment ends about 56 s into the run), the after-run to power-off; then the next key-on from
/// the EEPROM it wrote, warm, for 30 s.
/// </summary>
public sealed class CarWarmIdle
{
    public DecisionLog Log { get; private set; } = null!;
    public StartupResult R { get; }
    public StartupResult Next { get; }
    public CarWarmIdle()
    {
        R = StartupRun.Run(TestImages.Car, new(Seconds: 100, KeyOffAtSeconds: 75, Engine: EngineScenario.WarmIdle,
            Instrument: m => Log = new DecisionLog(m)));
        Next = StartupRun.Run(TestImages.Car,
            new(Seconds: 30, Engine: EngineScenario.WarmIdle, Machine: new MachineOptions { EepromImage = R.Eeprom.FinalImage }));
    }
}

public class CarWarmIdleTests : IClassFixture<CarWarmIdle>
{
    readonly StartupResult r, next;
    readonly DecisionLog log;
    public CarWarmIdleTests(CarWarmIdle f) => (r, next, log) = (f.R, f.Next, f.Log);

    static double V(TraceRow row, string column) => row.Values[VariableTrace.Columns.ToList().IndexOf(column)]!.Value;
    static IEnumerable<TraceRow> Between(StartupResult x, double from, double to) => x.Trace.Where(t => t.Seconds >= from && t.Seconds <= to);
    IEnumerable<TraceRow> Steady => Between(r, 65, 74.9);                // warm idle, enrichment over, before the key-off
    const ulong SteadyStart = 65UL * 24_000_000, KeyOff = 75UL * 24_000_000;

    /// <summary>
    /// Plan 2's open item 2: which of the words the after-run wrote the next key-on reads with the
    /// engine running. Pinned from the run: none, in 30 s.
    /// </summary>
    static readonly int[] ObservedSet = Array.Empty<int>();

    /// <summary>
    /// A forced pulse on a VANOS solenoid (a P7.0 or P7.1 latch change with its PWM off, which the
    /// firmware makes every 4.8 s on the intake and 2.9 s on the exhaust) moves that cam for about
    /// 25 ms; the firmware reads the cam once per engine cycle (133 ms at 900 rpm), so its reading can
    /// hold the excursion after the cam is back. Samples from 0.05 s before to 0.25 s after one are not compared.
    /// </summary>
    bool NearPulse(TraceRow t, string pin) => r.OutputEvents.Any(e => e.Kind == OutputKind.Pin && e.Source == pin
        && e.Cycle >= (t.Seconds - 0.25) * 24e6 && e.Cycle <= (t.Seconds + 0.05) * 24e6);

    [Fact] public void Engine_StartsAt4s_AndHoldsTheFirmwaresIdleTarget()
    {
        Assert.Equal(EnginePhase.Running, r.Engine!.Phases.First(p => p.Cycle == 96_000_000).Phase);
        Assert.DoesNotContain(r.Engine.Phases, p => p.Cycle > 96_000_000 && p.Cycle < KeyOff);   // it runs on until the key-off
        Assert.All(Steady, t => Assert.InRange(V(t, "rpm_fed") - V(t, "idle_target_rpm"), -25, 25));
        Assert.All(Steady, t => Assert.Equal(900, V(t, "idle_target_rpm")));
    }

    // §8.4: the ECU sees what it is fed
    [Fact] public void TheEcu_SeesTheSpeed() =>
        Assert.All(Steady, t => Assert.InRange(Math.Abs(V(t, "rpm_ecu") - V(t, "rpm_fed")), 0, V(t, "rpm_fed") * 0.01 + 2));

    [Fact] public void TheEcu_SeesTheTemperaturesThePedalAndTheBattery() => Assert.All(Steady, t =>
    {
        Assert.InRange(V(t, "coolant_c"), 89.25, 90.75);
        Assert.InRange(V(t, "intake_c"), 32.25, 33.75);
        Assert.InRange(V(t, "oil_c"), 88.9, 90.5);
        Assert.Equal(0, V(t, "pedal_pct"));                                 // released: below both track offsets
        Assert.InRange(V(t, "battery_v"), 13.86, 14.08);
    });

    [Fact] public void TheEcu_SeesTheCamPhases()
    {
        Assert.All(Steady.Where(t => !NearPulse(t, "P7.0")), t => Assert.InRange(V(t, "cam_in_raw") - (160 - V(t, "cam_in_fed") / 0.375), -1.5, 1.5));
        Assert.All(Steady.Where(t => !NearPulse(t, "P7.1")), t => Assert.InRange(V(t, "cam_ex_raw") - (120 - V(t, "cam_ex_fed") / 0.375), -1.5, 1.5));
        Assert.True(Steady.Count(t => !NearPulse(t, "P7.0") && !NearPulse(t, "P7.1")) >= 70);
    }

    // the plants
    [Fact] public void Vanos_TracksTheFirmwaresSetpoints() => Assert.All(Steady, t =>
    {
        Assert.InRange(V(t, "cam_in_deg") - V(t, "cam_in_sp_deg"), -2.25, 2.25);
        Assert.InRange(V(t, "cam_ex_deg") - V(t, "cam_ex_sp_deg"), -2.25, 2.25);
    });

    [Fact] public void Throttle_PassedItsAdaptation_AndRestsOnItsStopAtIdle() => Assert.All(Steady, t =>
    {
        Assert.Equal(7, V(t, "e17a"));
        Assert.Equal(0, (int)V(t, "fd0c") & 0x0400);
        Assert.InRange(V(t, "tps_deg"), 0, 0.5);
    });

    [Fact] public void Idle_ValveAndIntegrator_StayInsideTheirLimits() => Assert.All(Steady, t =>
    {
        Assert.InRange(V(t, "idle_valve_pct"), 4.0, 96.0);
        Assert.InRange(V(t, "idle_i_pct"), -9.9, 23.9);
    });

    // the car's absences
    [Fact] public void Lambda_ConcludesNoSensors() => Assert.All(Steady, t =>
    {
        Assert.Equal(0, (int)V(t, "fd9c") & 0x0008);                    // FD9C.3: no lambda set-up
        Assert.Equal(1, V(t, "lambda_cfg"));                            // 80F7 = 1: "no sensors"
    });

    [Fact] public void CanPartners_AreLearned_AsClusterAndAsc() => Assert.All(Steady, t => Assert.Equal(0x6, (int)V(t, "fd9c") & 0x7));

    // §8.3: every fault explained
    [Fact] public void NoFaultIsStored_AndEveryDetectedOneIsExplained()
    {
        Assert.True(r.Faults.Recognised);
        Assert.Empty(r.Faults.Stored);
        Assert.Empty(r.SavedFaults);
        Assert.All(r.Faults.Detected, e => Assert.True(FaultMemory.Explanation(e, r.FaultContext) != null, $"unexplained: {e.Label} {e.PCode} status {e.Status:X2}"));
    }

    // plan 2's open item 1
    [Fact] public void MonitoringModule_GetsEveryAnswerItExpects()
    {
        Assert.True(r.Monitor.Exchanges > 5_000);                       // > 4 periods of 1,240
        Assert.Equal(0, r.Monitor.UnexpectedAnswers);
    }

    // §8.5 (a): the outputs carry out the ECU's own decisions
    [Fact] public void Outputs_CarryOutTheEcusDecisions()
    {
        var sparks = r.Sparks.Where(s => s.Main && s.Fire >= SteadyStart && s.Fire < KeyOff).ToList();
        var injections = r.Injections.Where(i => i.Open >= SteadyStart && i.Open < KeyOff).ToList();
        Assert.True(sparks.Count >= 6 * 70 && injections.Count >= 6 * 70);     // 900 rpm: 75 cycles in 10 s
        Assert.All(sparks, s => Assert.InRange(s.Advance - s.DecidedAdvance, -0.75, 0.75));
        // the firmware predicts each dwell start from tooth periods counted in whole ticks: even at a
        // constant 900 rpm its dwells scatter -3...+3 ticks round its own 442, and the engine's small
        // speed changes add a tick
        Assert.All(sparks, s => Assert.InRange(s.DwellTicks - s.DecidedDwellTicks, -5, 5));
        // the pulse ends on the decision in force as it opened, or on the injection task's
        // recomputation during the pulse when that comes in time to move the close
        Assert.All(injections, i => Assert.True(Math.Abs(i.Ticks - i.ExpectedTicks) <= 2 || Math.Abs(i.Ticks - i.ExpectedTicksAtClose) <= 2,
            $"cylinder {i.Cylinder} at {i.Open / 24e6:0.0000} s: {i.Ticks:0.0} ticks, decided {i.ExpectedTicks} then {i.ExpectedTicksAtClose}"));
    }

    // §8.5 (b): the injection time agrees with a hand calculation from the calibration, once the
    // after-start enrichment has ended (the spark angle, dead time and dwell: Task 14)
    [Fact] public void BankInjectionTime_IsTheMs43x001Formula_AtEveryCallOnceTheEnrichmentEnds()
    {
        var calls = log.Injection.Where(c => c.Cycle >= SteadyStart && c.Cycle < KeyOff).ToList();
        Assert.True(calls.Count > 400, $"{calls.Count} calls");
        var cal = new Calibration(TestBins.CarBytes);
        foreach (var c in calls)
        {
            Assert.Equal((0, 0, 0, 0), (c.Entry[0xFD4E] & 0x0002, c.Entry[0x919A], c.Entry[0xFD48] & 1, c.Entry[0xFD2C] & 0x0100));   // the recipe's guards
            var t = Recipes.BankTime(cal, a => c.Entry[a]);
            Assert.Equal((t, t), (c.Result[0x8DAA], c.Result[0x8F18]));
        }
    }

    // Review Focus 2: a key-off while the engine runs
    [Fact] public void KeyOff_StopsTheEngine_ThenTheAfterRunDropsTheRelay()
    {
        Assert.Equal(StartupOutcome.PoweredOff, r.Outcome);
        var after = r.Engine!.Phases.Where(p => p.Cycle >= KeyOff).Select(p => p.Phase).ToArray();
        Assert.Equal(new[] { EnginePhase.Stopping, EnginePhase.Stopped }, after);
        var states = Between(r, 75, 100).Select(t => (int)V(t, "f900")).Distinct().ToArray();
        Assert.Contains(3, states); Assert.Contains(4, states);
        Assert.InRange(r.Milestones.PowerOff!.Value / 24e6, 76, 100);
    }

    // the next key-on
    [Fact] public void NextKeyOn_TakesTheShortCheck_AndStoresNothing()
    {
        Assert.Equal(7, next.Firmware.ThrottleCheckState);
        Assert.Empty(next.Faults.Stored);
        Assert.All(next.Faults.Detected, e => Assert.NotNull(FaultMemory.Explanation(e, next.FaultContext)));
        Assert.DoesNotContain(next.Faults.Detected, e => e.Path == 12);        // t_es: the after-run left an engine-stop record
        Assert.All(Between(next, 25, 30), t => Assert.InRange(V(t, "rpm_fed") - V(t, "idle_target_rpm"), -25, 25));
    }

    // plan 2's open item 2
    [Fact] public void NextKeyOn_WithTheEngineRunning_ReadsTheseOfTheWordsTheAfterRunWrote()
    {
        Assert.True(next.Eeprom.AddressesRead.Count >= 400, $"{next.Eeprom.AddressesRead.Count} distinct words read");
        Assert.Equal(ObservedSet, Enumerable.Range(0x1C2, 22).Append(0x1EB).Intersect(next.Eeprom.AddressesRead).Order());
    }
}
