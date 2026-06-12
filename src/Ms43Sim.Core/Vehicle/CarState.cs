namespace Ms43Sim.Core;

/// <summary>
/// What the car puts in front of the ECU at one moment of a drive (plan 4 R9, R10, R17): the
/// temperatures, the cluster's ambient and clock, the A/C request, the pedal, the gear, the road
/// speed, the clutch and brake switches, a battery override, and the faults injected — channels
/// forced to a voltage and CAN partners silenced. The defaults are plan 3's warm-idle set: the
/// temperatures its volts read through the car's sensor curves, 20 °C ambient, the clock at 60
/// minutes, A/C off, pedal released, neutral, stopped.
/// </summary>
public sealed record CarState
{
    static readonly IReadOnlyDictionary<string, double> NoForces = new Dictionary<string, double>();
    static readonly IReadOnlySet<uint> NoneSilenced = new HashSet<uint>();

    public static CarState Default { get; } = new();

    public double CoolantC { get; init; } = 90;

    public double OilC { get; init; } = 89.6;

    public double IntakeC { get; init; } = 32.8;

    public double AmbientC { get; init; } = 20;

    /// <summary>The cluster's clock, minutes 0…1439 (613h).</summary>
    public double ClockMinutes { get; init; } = 60;

    /// <summary>The A/C requested and running (615h byte 0).</summary>
    public bool Ac { get; init; }

    /// <summary>Pedal travel, 0…0.95.</summary>
    public double PedalFraction { get; init; }

    /// <summary>0 for neutral, else 1–5.</summary>
    public int Gear { get; init; }

    public double SpeedKmh { get; init; }

    public bool Clutch { get; init; }

    public bool Brake { get; init; }

    /// <summary>The battery in volts over the engine's own value for its phase; null leaves it to the engine.</summary>
    public double? Battery { get; init; }

    /// <summary>Channels forced to a voltage: "AN0"…"AN15" (not AN4 or AN13, which are multiplexed), "muxA0"…"muxA7", "muxB0"…"muxB7".</summary>
    public IReadOnlyDictionary<string, double> Forces { get; init; } = NoForces;

    /// <summary>CAN identifiers the partners do not send (153h, 613h, 615h).</summary>
    public IReadOnlySet<uint> Silenced { get; init; } = NoneSilenced;

    /// <summary>
    /// The radiator outlet (mux A 0): the ambient plus 0.9 × (coolant − ambient) [assumption]. It gives
    /// plan 3's 1.20 V (83 °C) at 90 °C coolant and 20 °C ambient, and the coolant itself when the
    /// engine is at the ambient (plan 3's key-on set and the 0 °C start).
    /// </summary>
    public double RadiatorOutletC => AmbientC + 0.9 * (CoolantC - AmbientC);
}
