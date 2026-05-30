namespace Ms43Sim.Core;

/// <summary>
/// An injector pulse: low from Open to Close. ExpectedTicks is the ECU's decision as the injector
/// opened, ExpectedTicksAtClose as it closed: the injection task recomputes the bank time each
/// segment, about 2–3 ms into a pulse at 900 rpm, and the recomputation moves the pulse's end when it
/// comes in time.
/// </summary>
public sealed record Injection(int Cylinder, ulong Open, ulong Close, double OpenAngle, double ExpectedTicks, double ExpectedTicksAtClose)
{
    /// <summary>The pulse in T1 ticks of 5.333 µs.</summary>
    public double Ticks => (Close - Open) / 128.0;
}

/// <summary>
/// A spark: the coil dwells from DwellStart and fires at Fire. Advance is °BTDC of the cylinder's
/// firing TDC; Main is a dwell of at least 1.5 ms (a multi-spark's re-dwell is shorter). The
/// decisions are the ECU's: the advance read as it fired, the dwell read as it started.
/// </summary>
public sealed record Spark(int Cylinder, ulong DwellStart, ulong Fire, double Advance, bool Main, double DecidedAdvance,
    int DecidedDwellTicks)
{
    public double DwellMs => (Fire - DwellStart) / 24_000.0;
    public double DwellTicks => (Fire - DwellStart) / 128.0;
}

/// <summary>
/// Injections and sparks per cylinder, measured on the pins and set beside the ECU's own decisions
/// (plan 3, Reference facts) [obs]. Cylinder c = n + 1 has its injector on P2.n (low = open) and
/// its coil on P2.(6 + n) (high = dwell, the falling edge fires); i is c's index in the firing
/// order 1-5-3-6-2-4.
/// <list type="bullet">
/// <item>Pulse width: floor(E46x × E470[i] / 4096) + E47C ticks, E46A for cylinders 1–3 and E46C
/// for 4–6 (XRAM words), read as the injector opens and again as it closes: a recomputation during
/// the pulse moves its end if it comes in time [obs: plan 3's warm-idle run].</item>
/// <item>Dwell: ((E4B8 × E4CC[i]) >> 16) &lt;&lt; 1 ticks (XRAM words), read as the dwell starts.</item>
/// <item>Advance: 72 − 0.375 × FBAB[i] °BTDC (IRAM bytes FBAB…FBB0), read as the coil fires.</item>
/// </list>
/// Events beyond the capacity (each list) are counted, not kept.
/// </summary>
public sealed class CylinderEvents
{
    public const int Capacity = 200_000;
    const ulong MainDwellCycles = 36_000;                           // 1.5 ms

    readonly Machine machine;
    readonly Func<double> crankAngle;
    readonly int capacity;
    readonly List<Injection> injections = new();
    readonly List<Spark> sparks = new();
    readonly (ulong Cycle, double Angle, double Expected)?[] opened = new (ulong, double, double)?[6];
    readonly (ulong Cycle, int Decided)?[] dwelling = new (ulong, int)?[6];

    public CylinderEvents(Machine machine, Func<double> crankAngle, int capacity = Capacity)
    {
        this.machine = machine;
        this.crankAngle = crankAngle;
        this.capacity = capacity;
        machine.Ports.PinChanged += OnPinChanged;
    }

    public IReadOnlyList<Injection> Injections => injections;

    public IReadOnlyList<Spark> Sparks => sparks;

    /// <summary>Injections and sparks not kept because their list was full.</summary>
    public long Dropped { get; private set; }

    void OnPinChanged(int port, int bit, bool level)
    {
        if (port != 2 || bit > 11 || !machine.Ports.IsOutput(2, bit))
            return;
        var now = machine.Cycles;
        if (bit < 6)
            Injector(bit, level, now);
        else
            Coil(bit - 6, level, now);
    }

    void Injector(int n, bool level, ulong now)
    {
        var cylinder = n + 1;
        if (!level)
        {
            opened[n] = (now, crankAngle(), ExpectedTicks(cylinder));
            return;
        }
        if (opened[n] is not { } open)
            return;
        opened[n] = null;
        Add(injections, new Injection(cylinder, open.Cycle, now, open.Angle, open.Expected, ExpectedTicks(cylinder)));
    }

    void Coil(int n, bool level, ulong now)
    {
        var cylinder = n + 1;
        if (level)
        {
            dwelling[n] = (now, DecidedDwellTicks(cylinder));
            return;
        }
        if (dwelling[n] is not { } dwell)
            return;
        dwelling[n] = null;
        var advance = BeforeTdc(EngineShaft.FiringTdc(cylinder) - crankAngle());
        Add(sparks, new Spark(cylinder, dwell.Cycle, now, advance, now - dwell.Cycle >= MainDwellCycles,
            DecidedAdvance(cylinder), dwell.Decided));
    }

    void Add<T>(List<T> list, T item)
    {
        if (list.Count < capacity)
            list.Add(item);
        else
            Dropped++;
    }

    /// <summary>An angle difference normalised to (−360, 360].</summary>
    static double BeforeTdc(double degrees)
    {
        var a = degrees % 720.0;
        return a > 360.0 ? a - 720.0 : a <= -360.0 ? a + 720.0 : a;
    }

    static int FiringIndex(int cylinder) => Array.IndexOf(EngineShaft.FiringOrder, cylinder);

    double ExpectedTicks(int cylinder)
    {
        var i = FiringIndex(cylinder);
        long bank = Xram16(cylinder <= 3 ? 0xE46Au : 0xE46Cu);
        return bank * Xram16(0xE470u + 2u * (uint)i) / 4096 + Xram16(0xE47C);
    }

    int DecidedDwellTicks(int cylinder)
    {
        var i = FiringIndex(cylinder);
        return (int)(((uint)Xram16(0xE4B8) * Xram16(0xE4CCu + 2u * (uint)i)) >> 16 << 1);
    }

    double DecidedAdvance(int cylinder) => 72 - 0.375 * machine.Bus.Iram[0xFBAB - 0xF600 + FiringIndex(cylinder)];

    /// <summary>XRAM straight from its array, so the values are real even while XPEN is clear.</summary>
    ushort Xram16(uint a) => (ushort)(machine.Bus.Xram[a - 0xE000] | machine.Bus.Xram[a - 0xE000 + 1] << 8);
}
