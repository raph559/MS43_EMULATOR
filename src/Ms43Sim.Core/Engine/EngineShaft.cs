namespace Ms43Sim.Core;

/// <summary>
/// The crankshaft and both camshafts as the ECU's pins see them (plan 3, Reference facts). θ is
/// the crank angle, 0…720°, in the firmware's own frame: tooth N's falling edge is at 6N°, and
/// teeth 59, 60, 119 and 120 are missing (the 60−2 wheel's two gaps per engine cycle). One crank
/// waveform drives P2.15 (CC15 capture and T7IN), P3.0 (T0IN) and P3.2 (CAPIN) [obs]: low on
/// [6N, 6N + 3) for each tooth N, high elsewhere, so the level is high through each gap
/// [assumption: the rising edges at 6N + 3; the firmware ignores rising edges]. The intake cam
/// (P2.14) is high on [250.125° − a, 610.125° − a) and the exhaust cam (P2.13) on
/// [106.5° + r, 466.5° + r), a being the intake advance and r the exhaust retard from their rest
/// positions [obs, ver]. Cylinder 1's firing TDC is θ = 690° [ver]; the others follow every 120°
/// in the firing order 1-5-3-6-2-4.
/// The speed is piecewise constant between <see cref="SetSpeed"/> calls. One scheduler event at a
/// time drives the next edge at the cycle the speed in force gives; the crank edges are walked in
/// order, so none is ever skipped or repeated, and each edge sets its level from its own type.
/// </summary>
public sealed class EngineShaft
{
    public const double TdcCylinder1 = 690.0;
    public static readonly int[] FiringOrder = { 1, 5, 3, 6, 2, 4 };

    const double CyclesPerDegreeAtOneRpm = 4_000_000;      // 24e6 cycles/s ÷ 6 °/s per rpm
    const double Cycle = 720.0, Epsilon = 1e-9;
    const double IntakeRise = 250.125, ExhaustRise = 106.5, CamWindow = 360.0;
    static readonly (int Port, int Bit)[] CrankPins = { (2, 15), (3, 0), (3, 2) };
    const int CamPort = 2, IntakeBit = 14, ExhaustBit = 13;

    /// <summary>The 232 crank edges of one engine cycle in angle order, and whether each one falls.</summary>
    static readonly (double Angle, bool Falls)[] CrankEdges =
        Enumerable.Range(1, 118).Where(IsTooth).SelectMany(n => new[] { (6.0 * n, true), (6.0 * n + 3.0, false) }).ToArray();

    readonly Scheduler scheduler;
    readonly ISignalPins pins;
    double thetaRef;             // the angle at tRef
    ulong tRef;
    bool intake, exhaust;        // the cam levels driven last
    int nextCrank;               // the index of the next crank edge
    long pending = -1;
    double pendingAngle;
    bool pendingCrank, pendingIntake, pendingExhaust;

    /// <summary>
    /// A shaft standing at <paramref name="startAngle"/> (this plan uses 100° [call]: at least 13
    /// teeth before the next gap, as the firmware's acquisition needs). The start levels are driven
    /// at once: P2.15, P3.0, P3.2, P2.14, P2.13.
    /// </summary>
    public EngineShaft(Scheduler scheduler, ISignalPins pins, double startAngle)
    {
        this.scheduler = scheduler;
        this.pins = pins;
        thetaRef = Normalize(startAngle);
        tRef = scheduler.Now;
        nextCrank = Array.FindIndex(CrankEdges, e => e.Angle > thetaRef + Epsilon);
        if (nextCrank < 0)
            nextCrank = 0;
        intake = InWindow(thetaRef, IntakeRise);
        exhaust = InWindow(thetaRef, ExhaustRise);
        var crank = CrankLevel(thetaRef);
        foreach (var (port, bit) in CrankPins)
            pins.Drive(port, bit, crank);
        pins.Drive(CamPort, IntakeBit, intake);
        pins.Drive(CamPort, ExhaustBit, exhaust);
    }

    /// <summary>The firing TDC of <paramref name="cylinder"/> (1-6): 690° for cylinder 1, then every 120° in the firing order.</summary>
    public static double FiringTdc(int cylinder) => (TdcCylinder1 + 120.0 * Array.IndexOf(FiringOrder, cylinder)) % Cycle;

    public double Rpm { get; private set; }

    /// <summary>θ at the scheduler's current cycle, 0 ≤ θ &lt; 720.</summary>
    public double Angle => Normalize(thetaRef + Rpm * (scheduler.Now - tRef) / CyclesPerDegreeAtOneRpm);

    public double IntakeAdvance { get; private set; }

    public double ExhaustRetard { get; private set; }

    /// <summary>Crank falling edges driven so far.</summary>
    public long CrankTeeth { get; private set; }

    /// <summary>The speed from now on; 0 stops the shaft where it is.</summary>
    public void SetSpeed(double rpm)
    {
        Freeze();
        Rpm = Math.Max(0, rpm);
        Reschedule();
    }

    /// <summary>The cam offsets from rest, from now on: a cam whose level at θ changes is driven at once.</summary>
    public void SetCamOffsets(double intakeAdvance, double exhaustRetard)
    {
        Freeze();
        IntakeAdvance = intakeAdvance;
        ExhaustRetard = exhaustRetard;
        var newIntake = InWindow(thetaRef, IntakeRise - IntakeAdvance);
        var newExhaust = InWindow(thetaRef, ExhaustRise + ExhaustRetard);
        if (newIntake != intake)
        {
            intake = newIntake;
            pins.Drive(CamPort, IntakeBit, intake);
        }
        if (newExhaust != exhaust)
        {
            exhaust = newExhaust;
            pins.Drive(CamPort, ExhaustBit, exhaust);
        }
        Reschedule();
    }

    static double Normalize(double angle)
    {
        var a = angle % Cycle;
        return a < 0 ? a + Cycle : a;
    }

    static bool IsTooth(int n) => n is >= 1 and <= 58 or >= 61 and <= 118;

    /// <summary>Low on [6N, 6N + 3) for each tooth N.</summary>
    static bool CrankLevel(double theta)
    {
        var n = (int)Math.Floor(theta / 6.0);
        return !(IsTooth(n) && theta - 6.0 * n < 3.0);
    }

    static bool InWindow(double theta, double start) => Normalize(theta - start) < CamWindow;

    /// <summary>Moves the reference to now, so the speed or the offsets can change.</summary>
    void Freeze()
    {
        thetaRef = Angle;
        tRef = scheduler.Now;
    }

    /// <summary>
    /// The distance from θ to <paramref name="edge"/>. An edge a float's noise behind θ (it is due
    /// now and not driven yet) is 0, never almost a whole cycle.
    /// </summary>
    double Ahead(double edge)
    {
        var distance = Normalize(edge - thetaRef);
        return distance > Cycle - 1e-6 ? 0 : distance;
    }

    void Reschedule()
    {
        if (pending >= 0)
            scheduler.Cancel(pending);
        pending = -1;
        if (Rpm <= 0)
            return;
        var dCrank = Ahead(CrankEdges[nextCrank].Angle);
        var intakeRise = IntakeRise - IntakeAdvance;
        var exhaustRise = ExhaustRise + ExhaustRetard;
        var dIntake = Ahead(intake ? intakeRise + CamWindow : intakeRise);
        var dExhaust = Ahead(exhaust ? exhaustRise + CamWindow : exhaustRise);
        var d = Math.Min(dCrank, Math.Min(dIntake, dExhaust));
        pendingCrank = dCrank - d <= Epsilon;
        pendingIntake = dIntake - d <= Epsilon;
        pendingExhaust = dExhaust - d <= Epsilon;
        pendingAngle = Normalize(thetaRef + d);
        var cycles = (ulong)Math.Max(0, Math.Ceiling(d * CyclesPerDegreeAtOneRpm / Rpm - 1e-6));
        pending = scheduler.Schedule(tRef + cycles, "engine edge", Fire);
    }

    void Fire()
    {
        pending = -1;
        thetaRef = pendingAngle;
        tRef = scheduler.Now;
        if (pendingCrank)
        {
            var edge = CrankEdges[nextCrank];
            foreach (var (port, bit) in CrankPins)
                pins.Drive(port, bit, !edge.Falls);
            if (edge.Falls)
                CrankTeeth++;
            nextCrank = (nextCrank + 1) % CrankEdges.Length;
        }
        if (pendingIntake)
        {
            intake = !intake;
            pins.Drive(CamPort, IntakeBit, intake);
        }
        if (pendingExhaust)
        {
            exhaust = !exhaust;
            pins.Drive(CamPort, ExhaustBit, exhaust);
        }
        Reschedule();
    }
}
