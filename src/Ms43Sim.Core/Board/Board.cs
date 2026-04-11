namespace Ms43Sim.Core;

/// <summary>
/// The ECU board around the C167CR: it puts <see cref="BoardInputs"/> on the pins and holds the
/// output latch on CS3. Later tasks add the devices on the SSC.
/// </summary>
public sealed class Board : IPinEnvironment
{
    const double DigitalThresholdVolts = 2.5;

    readonly EventLog log;

    public Board(BoardInputs inputs, EventLog log)
    {
        Inputs = inputs;
        this.log = log;
        Latch = new OutputLatch(log);
    }

    public BoardInputs Inputs { get; private set; }

    /// <summary>The write-only latch on CS3: chip selects, multiplexer select, main-relay hold.</summary>
    public OutputLatch Latch { get; }

    /// <summary>(port, bit) of each input whose level changed through <see cref="SetInputs"/>.</summary>
    public event Action<int, int>? DigitalInputChanged;

    public bool InputLevel(int port, int bit) => LevelIn(Inputs, port, bit);

    public void SetInputs(BoardInputs inputs)
    {
        var before = Inputs;
        Inputs = inputs;
        var pins = before.DigitalInputs.Keys.Concat(inputs.DigitalInputs.Keys)
            .Concat(Enumerable.Range(0, 16).Select(bit => (Port: 5, Bit: bit)))
            .Distinct().Order();
        foreach (var (port, bit) in pins)
        {
            if (LevelIn(before, port, bit) != LevelIn(inputs, port, bit))
                DigitalInputChanged?.Invoke(port, bit);
        }
    }

    /// <summary>
    /// A listed digital input gives its level; any other pin reads high [assumption: pull-ups].
    /// A P5 pin reads 1 above 2.5 V [assumption]; the car never reads P5.
    /// </summary>
    static bool LevelIn(BoardInputs inputs, int port, int bit)
    {
        if (port == 5)
            return inputs.AnalogVolts[bit] > DigitalThresholdVolts;
        return !inputs.DigitalInputs.TryGetValue((port, bit), out var level) || level;
    }
}
