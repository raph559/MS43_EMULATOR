using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Vehicle;

public class CarControlsTests
{
    static double? V(TraceRow row, string column) => row.Values[VariableTrace.Columns.ToList().IndexOf(column)];

    /// <summary>
    /// The car stands still at key-on: a wired speed then makes the firmware abort its key-on throttle
    /// adaptation (tps_ad stored, limp-home; see the next test), so it starts rolling once the engine idles.
    /// </summary>
    [Fact] public void Temperatures_AndTheRoadSpeed_ReachTheEcu()
    {
        var state = new CarState { CoolantC = 90, OilC = 90, IntakeC = 33 };
        Machine? machine = null;
        var r = StartupRun.Run(TestImages.Car, new(Seconds: 20, CanPartners: false, Engine: EngineScenario.WarmIdle, Instrument: m =>
        {
            machine = m;
            var car = CarControls.Attach(m, () => state);
            m.Scheduler.Schedule(192_000_000, "rolling", () =>                // 8 s: idling since 4 s
            {
                state = state with { Gear = 4, SpeedKmh = 26.5 };              // 900 rpm / 26.5 km/h = 34: the 4th-gear band (R9)
                car.Apply();
            });
        }));
        var row = r.Trace.Last();
        Assert.InRange(V(row, "coolant_c")!.Value, 89.25, 90.75);
        Assert.InRange(V(row, "oil_c")!.Value, 89, 91);
        Assert.InRange(V(row, "intake_c")!.Value, 32.25, 33.75);
        Assert.InRange((int)machine!.Bus.ExternalRam[0x847F - 0x8000], 25, 28);  // the wired speed, km/h (R9)
        Assert.Equal(4, machine.Bus.ExternalRam[0x81AC - 0x8000]);             // the firmware's gear, from its rpm and the CAN speed
        Assert.Empty(r.Faults.Stored);
    }

    [Fact] public void ACarRollingAtKeyOn_AbortsTheThrottleAdaptation()
    {
        var state = new CarState { SpeedKmh = 26.5 };
        var r = StartupRun.Run(TestImages.Car, new(Seconds: 12, CanPartners: false, Engine: EngineScenario.WarmIdle,
            Instrument: m => CarControls.Attach(m, () => state)));
        Assert.Contains(r.Faults.Stored, f => f.Name == "tps_ad");
        Assert.True(r.Firmware.ThrottleLimp);
    }

    [Fact] public void ThePedal_ReachesTheEcu()
    {
        var state = new CarState { PedalFraction = 0.30 };
        var r = StartupRun.Run(TestImages.Car, new(Seconds: 4, CanPartners: false, Instrument: m => CarControls.Attach(m, () => state)));
        Assert.InRange(V(r.Trace.Last(), "pedal_pct")!.Value, 27, 30);         // E0FE = 98.7 p - 1.8 % (R9)
        Assert.Empty(r.Faults.Stored);
    }

    /// <summary>
    /// The firmware does not read the MAF with the engine stopped (0 kg/h at any AN0), so the forces are
    /// on inputs it reads at key-on: the battery (AN5, a direct channel) and the intake sensor (mux A 1).
    /// </summary>
    [Fact] public void ASilencedPartner_IsNotDelivered_AndAForcedChannelOverridesTheSensor()
    {
        var state = new CarState { Silenced = new HashSet<uint> { 0x153 },
                                   Forces = new Dictionary<string, double> { ["AN5"] = 2.0, ["muxA1"] = 1.660 } };   // 10.4 V; 60 °C (R10)
        CarControls? car = null;
        var r = StartupRun.Run(TestImages.Car, new(Seconds: 4, CanPartners: false, Instrument: m => car = CarControls.Attach(m, () => state)));
        Assert.Equal(0, car!.Partners.Sent.GetValueOrDefault(0x153u));        // 153h never sent
        Assert.True(car.Partners.Sent.GetValueOrDefault(0x613u) > 0);         // the cluster still is
        var row = r.Trace.Last();
        Assert.InRange(V(row, "battery_v")!.Value, 10.1, 10.7);
        Assert.InRange(V(row, "intake_c")!.Value, 59, 61);
    }

    [Fact] public void Apply_PutsTheStateOnTheBoard()
    {
        var t = TestMachine.Create("0DFF");
        var state = new CarState { CoolantC = 0, IntakeC = -9.8, OilC = 30, AmbientC = 0, PedalFraction = 0.5,
                                   Clutch = true, Brake = true, SpeedKmh = 40 };
        var car = CarControls.Attach(t.M, () => state);
        var b = t.M.Board;
        Assert.Equal(new[] { 4.497, 4.590, 3.418, 4.497 }, b.Inputs.MuxAVolts.Take(4).Select(v => Math.Round(v, 3)));   // R10
        Assert.Equal((2.575, 1.2875), (Math.Round(b.AnalogVolts(10), 4), Math.Round(b.AnalogVolts(6), 4)));            // AN10 = 0.75 + 3.65 p, AN6 = AN10 / 2
        Assert.Equal(4.5, b.Inputs.MuxBVolts[2]);                              // the clutch pressed
        Assert.True(b.InputLevel(3, 1) && b.InputLevel(3, 5));                 // both brake switches
        Assert.Equal(40, car.Speed.Kmh);

        var inputs = b.Inputs;
        car.Apply();
        Assert.Same(inputs, b.Inputs);                                          // nothing moved, nothing set
    }

    [Fact] public void AForceAndABattery_OverrideUntilLifted_ThenTheSensorsReadAgain()
    {
        var t = TestMachine.Create("0DFF");
        var state = new CarState { Clutch = true };
        var car = CarControls.Attach(t.M, () => state);
        var b = t.M.Board;
        state = state with { Battery = 9.1, Forces = new Dictionary<string, double> { ["AN0"] = 4.0, ["AN12"] = 0.2, ["muxB2"] = 0.0 } };
        car.Apply();
        Assert.Equal((4.0, 0.2, 0.0), (b.AnalogVolts(0), b.AnalogVolts(12), b.Inputs.MuxBVolts[2]));
        Assert.Equal((1.75, 1.75), (Math.Round(b.AnalogVolts(5), 2), Math.Round(b.AnalogVolts(1), 2)));   // 9.1 V ÷ 5.2, KL15 too while the key is on

        state = state with { Battery = null, Forces = new Dictionary<string, double>() };
        car.Apply();
        Assert.Equal(1.00, b.AnalogVolts(0));                                   // the input set's MAF volts again
        Assert.Equal(ThrottleBody.Track1Volts(t.M.Throttle.Angle), b.AnalogVolts(12));   // the throttle body's track again
        Assert.Equal(4.5, b.Inputs.MuxBVolts[2]);                               // the clutch again
        Assert.Equal((b.Inputs.AnalogVolts[5], b.Inputs.AnalogVolts[1]), (b.AnalogVolts(5), b.AnalogVolts(1)));   // the input set's battery and KL15
    }

    [Theory, InlineData("AN4"), InlineData("AN13"), InlineData("AN16"), InlineData("muxA8"), InlineData("knock")]
    public void AForceOnAChannelThatIsNotThere_IsRefused(string channel)
    {
        var t = TestMachine.Create("0DFF");
        var state = new CarState { Forces = new Dictionary<string, double> { [channel] = 1.0 } };
        Assert.Throws<ArgumentException>(() => CarControls.Attach(t.M, () => state));
    }

    [Fact] public void TheDriveAssumptions_NameTheCarsInputs() =>
        Assert.Contains(MachineAssumptions.ForDrive(), line => line.Contains("6.971") && line.Contains("P7.6"));
}
