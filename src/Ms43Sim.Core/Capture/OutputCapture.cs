namespace Ms43Sim.Core;

public enum OutputKind { Pin, Pwm, ChipA, Latch, Heaters, PinDirection }

/// <summary>One change of an ECU output. Angle: the crank angle at a pin edge, when a source was given.</summary>
public sealed record OutputEvent(ulong Cycle, OutputKind Kind, string Source, int Value, double? Angle);

/// <summary>
/// Every change of the ECU's outputs, in time order (plan 3, Reference facts): each edge of a pin
/// that is an output before and after it (<see cref="Ports.OutputEdge"/>; "P2.6", 0 or 1); each PWM
/// setting as its P7 pin's share of time high, in per mille ("PWM2"); each command chip A latches
/// ("chip A"); each CS3 latch byte ("CS3 latch"); and the lambda-heater command FDD2 the CPU sends the
/// monitoring module in byte 4 of its CFh and ADh frames, when it changes ("heaters FDD2"; 0 = on).
/// A pin's direction change is recorded as a set-up (<see cref="OutputKind.PinDirection"/>, 1 = now an
/// output), never as an edge, and is not counted as a change [obs plan 4 R1]. A value equal to the
/// previous one for the same kind and source (a PWMCON write repeats every channel's share; chip A
/// repeats its command every 10 ms) is counted but not kept again. Events beyond the capacity are
/// counted, not kept.
/// </summary>
public sealed class OutputCapture
{
    public const int Capacity = 1_000_000;

    readonly Machine machine;
    readonly Func<double>? crankAngle;
    readonly int capacity;
    readonly List<OutputEvent> events = new();
    readonly Dictionary<string, long> counts = new();
    readonly string[,] pinNames = new string[9, 16];
    readonly string[] pwmNames = { "PWM0", "PWM1", "PWM2", "PWM3" };
    readonly Dictionary<(OutputKind, string), int> lastSeen = new();
    int? heaters;

    public OutputCapture(Machine machine, Func<double>? crankAngle = null, int capacity = Capacity)
    {
        this.machine = machine;
        this.crankAngle = crankAngle;
        this.capacity = capacity;
        for (var port = 0; port < 9; port++)
            for (var bit = 0; bit < 16; bit++)
                pinNames[port, bit] = $"P{port}.{bit}";

        machine.Ports.OutputEdge += (port, bit, level) =>
            Add(OutputKind.Pin, pinNames[port, bit], level ? 1 : 0, crankAngle?.Invoke());
        machine.Ports.PinDirectionChanged += (port, bit, isOutput) =>
            Add(OutputKind.PinDirection, pinNames[port, bit], isOutput ? 1 : 0, null);
        machine.Pwm.SettingsChanged += x =>
        {
            var latch = (machine.Ports.Latch(7) & (1 << x)) != 0;
            Add(OutputKind.Pwm, pwmNames[x], (int)Math.Round(machine.Pwm.PinHighFraction(x, latch) * 1000), null);
        };
        machine.Board.ChipA.CommandLatched += word => Add(OutputKind.ChipA, "chip A", (int)word, null);
        machine.Board.Latch.Changed += (_, after) => Add(OutputKind.Latch, "CS3 latch", after, null);
        machine.Board.Monitor.CpuFrameReceived += frame =>
        {
            if (frame[0] is not (0xCF or 0xAD))
                return;
            var nibble = frame[4] & 0x0F;
            if (nibble == heaters)
                return;
            heaters = nibble;
            Add(OutputKind.Heaters, "heaters FDD2", nibble, null);
        };
    }

    public IReadOnlyList<OutputEvent> Events => events;

    /// <summary>Events not kept because the capture was full.</summary>
    public long Dropped { get; private set; }

    /// <summary>Events per source, kept or not.</summary>
    public IReadOnlyDictionary<string, long> Counts => counts;

    void Add(OutputKind kind, string source, int value, double? angle)
    {
        if (kind != OutputKind.PinDirection)
            counts[source] = counts.GetValueOrDefault(source) + 1;
        if (lastSeen.TryGetValue((kind, source), out var last) && last == value)
            return;                                  // a repeat of the previous value: counted above, not kept again
        lastSeen[(kind, source)] = value;
        if (events.Count < capacity)
            events.Add(new OutputEvent(machine.Cycles, kind, source, value, angle));
        else
            Dropped++;
    }
}
