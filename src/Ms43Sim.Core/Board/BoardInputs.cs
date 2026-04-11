namespace Ms43Sim.Core;

/// <summary>
/// What the car puts on the ECU's inputs: volts on AN0-AN15 and on the two external 8-way
/// multiplexers (mux A feeds AN13, mux B feeds AN4), and the level of each listed digital input.
/// A pin that is not listed reads high [assumption: pull-ups].
/// </summary>
public sealed record BoardInputs
{
    /// <summary>[assumption] VAREF = 5.0 V and an ideal converter.</summary>
    public const double ReferenceVolts = 5.0;

    public IReadOnlyList<double> AnalogVolts { get; init; } = Array.AsReadOnly(new double[16]);

    /// <summary>Mux A positions 0-7, read on AN13.</summary>
    public IReadOnlyList<double> MuxAVolts { get; init; } = Array.AsReadOnly(new double[8]);

    /// <summary>Mux B positions 0-7, read on AN4.</summary>
    public IReadOnlyList<double> MuxBVolts { get; init; } = Array.AsReadOnly(new double[8]);

    public IReadOnlyDictionary<(int Port, int Bit), bool> DigitalInputs { get; init; } =
        new Dictionary<(int Port, int Bit), bool>();

    /// <summary>
    /// The healthy key-on set (plan 2, Reference facts): 12.6 V, 20 °C, key on, engine stopped,
    /// pedal released, throttle at limp-home, lambda sensors unplugged. AN4 and AN13 come from the
    /// multiplexers, so their own entries are 0 V.
    /// </summary>
    public static BoardInputs KeyOnEngineStopped { get; } = new()
    {
        AnalogVolts = Array.AsReadOnly(new[]
        {
            1.00, 2.42, 0.45, 0.45, 0.00, 2.42, 0.375, 0.45,
            0.45, 0.00, 0.75, 4.277, 0.742, 0.00, 0.00, 0.00,
        }),
        MuxAVolts = Array.AsReadOnly(new[] { 3.87, 3.54, 3.83, 3.87, 0.00, 0.00, 2.50, 0.00 }),
        MuxBVolts = Array.AsReadOnly(new[] { 2.50, 2.50, 0.00, 4.08, 0.00, 0.00, 0.00, 0.00 }),
        DigitalInputs = new Dictionary<(int Port, int Bit), bool>
        {
            [(3, 1)] = false,   // brake light switch
            [(3, 5)] = false,   // brake test switch
            [(8, 6)] = false,   // throttle shut-off feedback: low for the whole run
        },
    };

    /// <summary>KL15 sense on AN1: 2.42 V with the key on, 0 V with it off.</summary>
    public BoardInputs WithIgnition(bool on) => WithAnalog(1, on ? 2.42 : 0.00);

    public BoardInputs WithInput(int port, int bit, bool level) =>
        this with { DigitalInputs = new Dictionary<(int Port, int Bit), bool>(DigitalInputs) { [(port, bit)] = level } };

    public BoardInputs WithAnalog(int channel, double volts)
    {
        var volts16 = AnalogVolts.ToArray();
        volts16[channel] = volts;
        return this with { AnalogVolts = Array.AsReadOnly(volts16) };
    }

    /// <summary>The 10-bit result for <paramref name="volts"/>: floor(V / 5.0 × 1024 + 0.5), clamped to 0…1023.</summary>
    public static int AdcCode(double volts) =>
        Math.Clamp((int)Math.Floor(volts / ReferenceVolts * 1024 + 0.5), 0, 1023);
}
