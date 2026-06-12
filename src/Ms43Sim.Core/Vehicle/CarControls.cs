using System.Text.RegularExpressions;

namespace Ms43Sim.Core;

/// <summary>
/// Puts a live <see cref="CarState"/> in front of the ECU (plan 4 R9, R10): the temperatures as volts
/// on mux A through the car's sensor curves (radiator outlet 0, intake air 1, oil 2, coolant 3), the
/// pedal on AN10 = 0.75 + 3.65 p V and AN6 = AN10 / 2, the clutch on mux B 2 (4.5 V pressed, 0 V
/// released), the brake switches on P3.1 and P3.5 (both high when pressed), a battery override on
/// AN5 and, while the key is on, AN1; the road speed as pulses on P7.6 (<see cref="SpeedPulses"/>),
/// the steering wheel's "no button" line on P7.4 (<see cref="SteeringWheelLine"/>) and the CAN
/// partners' frames (<see cref="CanPartners"/>, which read the state as each frame goes).
/// Faults go in through the state: a forced channel reads its volts instead of its sensor until the
/// force is lifted, and a silenced partner sends nothing. <see cref="Apply"/> reads the state and
/// changes only what moved; attach it after the engine, whose sensors it wraps.
/// </summary>
public sealed partial class CarControls
{
    /// <summary>AN1's ADC code from which the firmware sees the key on (KL15).</summary>
    public const int KeyOnCode = 235;

    const double PedalVolts = 0.75, PedalSpan = 3.65, ClutchPressed = 4.5;

    readonly Machine machine;
    readonly Func<CarState> state;
    readonly IReadOnlyList<double> muxA, muxB;
    readonly HashSet<int> wrapped = new();

    CarControls(Machine machine, Func<CarState> state)
    {
        this.machine = machine;
        this.state = state;
        muxA = machine.Board.Inputs.MuxAVolts;
        muxB = machine.Board.Inputs.MuxBVolts;
        Applied = state();
        Partners = new CanPartners(machine.Scheduler, machine.Can.Deliver, KeyOn, state);
        Speed = new SpeedPulses(machine.Scheduler, machine.Board, machine.Options.CpuHz);
        Wheel = new SteeringWheelLine(machine.Scheduler, machine.Board, KeyOn);
    }

    /// <summary>Puts <paramref name="state"/>'s current value on <paramref name="machine"/>'s board and starts the partners and the steering-wheel line.</summary>
    public static CarControls Attach(Machine machine, Func<CarState> state)
    {
        Check(state());
        var car = new CarControls(machine, state);
        car.Wrap(1);
        car.Wrap(5);
        car.Apply();
        car.Partners.Start();
        car.Wheel.Start();
        return car;
    }

    public CanPartners Partners { get; }

    public SpeedPulses Speed { get; }

    public SteeringWheelLine Wheel { get; }

    /// <summary>The state the last <see cref="Apply"/> put on the board.</summary>
    public CarState Applied { get; private set; }

    /// <summary>The key is on as the firmware sees it: AN1 at code 235 or more.</summary>
    public bool KeyOn() => BoardInputs.AdcCode(machine.Board.AnalogVolts(1)) >= KeyOnCode;

    /// <summary>Reads the state and puts what moved on the board.</summary>
    public void Apply()
    {
        var car = state();
        Check(car);
        Applied = car;
        foreach (var name in car.Forces.Keys)
        {
            if (Channel(name) is ('A', var channel))
                Wrap(channel);
        }
        var board = machine.Board;
        var next = Compose(board.Inputs, car);
        if (!next.SameAs(board.Inputs))
            board.SetInputs(next);
        Speed.SetSpeed(car.SpeedKmh);
    }

    BoardInputs Compose(BoardInputs inputs, CarState car)
    {
        var pedal = PedalVolts + PedalSpan * car.PedalFraction;
        var next = inputs with { MuxAVolts = muxA, MuxBVolts = muxB };
        next = next
            .WithMuxA(0, SensorCurves.CoolantVolts(car.RadiatorOutletC))
            .WithMuxA(1, SensorCurves.IntakeVolts(car.IntakeC))
            .WithMuxA(2, SensorCurves.OilVolts(car.OilC))
            .WithMuxA(3, SensorCurves.CoolantVolts(car.CoolantC))
            .WithMuxB(2, car.Clutch ? ClutchPressed : 0)
            .WithAnalog(10, pedal)
            .WithAnalog(6, pedal / 2)
            .WithInput(3, 1, car.Brake)
            .WithInput(3, 5, car.Brake);
        foreach (var (name, volts) in car.Forces)
        {
            next = Channel(name) switch
            {
                ('a', var position) => next.WithMuxA(position, volts),
                ('b', var position) => next.WithMuxB(position, volts),
                _ => next,
            };
        }
        return next;
    }

    /// <summary>Channel <paramref name="channel"/> now reads a force, the battery override, or else what it read before.</summary>
    void Wrap(int channel)
    {
        if (!wrapped.Add(channel))
            return;
        var board = machine.Board;
        var sensor = board.AnalogSource(channel);
        var name = $"AN{channel}";
        board.AttachAnalog(channel, () =>
        {
            if (Applied.Forces.TryGetValue(name, out var forced))
                return forced;
            var inputs = board.Inputs;
            if (Applied.Battery is { } battery && (channel == 5 || channel == 1 && inputs.AnalogVolts[1] >= BoardInputs.KeyOnVolts))
                return battery / BoardInputs.BatteryDivider;
            return sensor?.Invoke() ?? inputs.AnalogVolts[channel];
        });
    }

    /// <summary>('A', 0–15) for an ADC channel, ('a', 0–7) for mux A, ('b', 0–7) for mux B; else ('?', -1).</summary>
    static (char Kind, int Index) Channel(string name)
    {
        var match = ChannelName().Match(name);
        if (!match.Success)
            return ('?', -1);
        var index = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return match.Groups[1].Success ? ('A', index) : (match.Groups[2].Value == "A" ? 'a' : 'b', index);
    }

    /// <summary>A channel a force can name: AN0–AN15 but AN4 and AN13 (multiplexed), muxA0–muxA7, muxB0–muxB7.</summary>
    public static bool CanForce(string channel) => Channel(channel) switch
    {
        ('A', var index) => index is >= 0 and <= 15 and not 4 and not 13,
        ('a' or 'b', var position) => position is >= 0 and <= 7,
        _ => false,
    };

    static void Check(CarState car)
    {
        foreach (var name in car.Forces.Keys)
        {
            if (!CanForce(name))
                throw new ArgumentException($"cannot force \"{name}\": a channel is AN0–AN15 (AN4 and AN13 are multiplexed: force muxB0–7 or muxA0–7)", nameof(car));
        }
    }

    [GeneratedRegex("^(?:(AN)|mux([AB]))([0-9]{1,2})$")]
    private static partial Regex ChannelName();
}
