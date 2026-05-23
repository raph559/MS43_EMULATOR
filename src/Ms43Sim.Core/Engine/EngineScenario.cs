namespace Ms43Sim.Core;

/// <summary>
/// What an engine run does around the ECU (plan 3, Reference facts): the board's inputs, when and
/// for how long the starter turns the engine and how fast, the shaft's start angle, the battery in
/// each phase (key on 12.6 V, starter 10.5 V, alternator 14.0 V [assumption]) and the VANOS hold
/// duties (the car's <c>ip_ivvthpwm__tmag__vb</c>, 0x77858, at 90 °C and 14.0 V, the exhaust with
/// its −1.17 % offset [obs; frozen as the actuators' property]). Times are seconds from power-on.
/// </summary>
public sealed record EngineScenario(string Name, BoardInputs Inputs, double CrankAtSeconds, double StarterSeconds,
    double StarterRpm, double StartAngle, double BatteryOff, double BatteryStarter, double BatteryRunning,
    double HoldIntake, double HoldExhaust)
{
    /// <summary>A warm engine started at 3.0 s: 1 s of starter at 200 rpm, then the firmware holds its idle.</summary>
    public static EngineScenario WarmIdle { get; } =
        new("warm-idle", BoardInputs.WarmIdle, 3.0, 1.0, 200, 100, 12.6, 10.5, 14.0, 0.377, 0.365);

    /// <summary>The scenario called <paramref name="name"/> ("warm-idle"), else null.</summary>
    public static EngineScenario? Named(string name) => name == WarmIdle.Name ? WarmIdle : null;
}
