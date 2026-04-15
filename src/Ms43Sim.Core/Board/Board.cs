namespace Ms43Sim.Core;

/// <summary>
/// The ECU board around the C167CR: it puts <see cref="BoardInputs"/> on the pins and holds the
/// output latch on CS3 and the devices on the SSC: the M93C86 EEPROM, output-driver chips A and B,
/// and the monitoring module. Every output-latch change reaches every device's chip select.
/// </summary>
public sealed class Board : IPinEnvironment
{
    const double DigitalThresholdVolts = 2.5;
    const int MuxBChannel = 4, MuxAChannel = 13;

    public Board(BoardInputs inputs, EventLog log)
    {
        Inputs = inputs;
        Latch = new OutputLatch(log);
    }

    public BoardInputs Inputs { get; private set; }

    /// <summary>The write-only latch on CS3: chip selects, multiplexer select, main-relay hold.</summary>
    public OutputLatch Latch { get; }

    /// <summary>The SSC lines and the slaves on them.</summary>
    public SscBus Spi { get; } = new();

    public M93C86 Eeprom { get; private set; } = null!;

    public ShiftRegisterChip ChipA { get; private set; } = null!;

    public ShiftRegisterChip ChipB { get; private set; } = null!;

    public MonitoringModule Monitor { get; private set; } = null!;

    /// <summary>
    /// (port, bit) of each input whose level changed through <see cref="SetInputs"/> or through the
    /// multiplexer select (P5.4 and P5.13).
    /// </summary>
    public event Action<int, int>? DigitalInputChanged;

    /// <summary>
    /// Creates the devices and wires their chip selects to the latch. A null
    /// <paramref name="eepromImage"/> means a blank EEPROM (every word FFFFh) [owner].
    /// </summary>
    public void AttachDevices(Scheduler scheduler, Ssc ssc, EventLog events, byte[]? eepromImage)
    {
        Eeprom = new M93C86(scheduler, () => Latch.EepromSelected);
        if (eepromImage != null)
            Eeprom.Load(eepromImage);
        Monitor = new MonitoringModule(scheduler, ssc, () => Latch.MonitorSelected,
            () => BoardInputs.AdcCode(AnalogVolts(6)), events);
        ChipA = ShiftRegisterChip.ChipA(() => Latch.ChipASelected);
        ChipB = ShiftRegisterChip.ChipB(() => Latch.ChipBSelected, () => Monitor.ShutOffReleased);
        Spi.Attach(Eeprom);
        Spi.Attach(ChipA);
        Spi.Attach(ChipB);
        Latch.Changed += OnLatchChanged;
    }

    /// <summary>AN13 shows mux A and AN4 shows mux B at the latch's select position; every other channel its own input.</summary>
    public double AnalogVolts(int channel) => channel switch
    {
        MuxAChannel => Inputs.MuxAVolts[Latch.MuxSelect],
        MuxBChannel => Inputs.MuxBVolts[Latch.MuxSelect],
        _ => Inputs.AnalogVolts[channel],
    };

    /// <summary>P3.8 is MRST: the firmware reads it as the EEPROM's ready line [obs].</summary>
    public bool InputLevel(int port, int bit) => port == 3 && bit == 8 ? Spi.MrstLevel : LevelIn(Inputs, port, bit);

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

    /// <summary>After a CPU reset the monitoring module restarts its protocol [inf].</summary>
    public void OnCpuReset() => Monitor.OnCpuReset();

    /// <summary>The latch, the EEPROM's decoder and the chips to their power-on state; the module restarts.</summary>
    public void PowerOn()
    {
        Latch.PowerOn();
        Eeprom.PowerOn();
        ChipA.PowerOn();
        ChipB.PowerOn();
        Monitor.PowerOn();
    }

    void OnLatchChanged(byte before, byte after)
    {
        Eeprom.ChipSelectChanged();
        ChipA.ChipSelectChanged();
        ChipB.ChipSelectChanged();
        Monitor.ChipSelectChanged();
        if (((before ^ after) & 0x07) != 0)
        {
            DigitalInputChanged?.Invoke(5, MuxBChannel);
            DigitalInputChanged?.Invoke(5, MuxAChannel);
        }
    }

    /// <summary>
    /// A listed digital input gives its level; any other pin reads high [assumption: pull-ups].
    /// A P5 pin reads 1 above 2.5 V [assumption]; the car never reads P5.
    /// </summary>
    bool LevelIn(BoardInputs inputs, int port, int bit)
    {
        if (port == 5)
        {
            var volts = bit switch
            {
                MuxAChannel => inputs.MuxAVolts[Latch.MuxSelect],
                MuxBChannel => inputs.MuxBVolts[Latch.MuxSelect],
                _ => inputs.AnalogVolts[bit],
            };
            return volts > DigitalThresholdVolts;
        }
        return !inputs.DigitalInputs.TryGetValue((port, bit), out var level) || level;
    }
}
