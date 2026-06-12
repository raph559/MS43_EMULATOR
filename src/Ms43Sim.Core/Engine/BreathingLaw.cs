namespace Ms43Sim.Core;

/// <summary>
/// How much air the engine draws, from the firmware's own air model (plan 4 R12) [obs: the car's
/// <c>ip_maf_1_diag__n__tps_av</c> (0x7AAEE) frozen as the engine's law; ver as "law 4"]:
/// <list type="bullet">
/// <item>M(n, θ), mg/stroke, bilinear in the speed and the throttle's degrees above its lower stop,
/// flat outside the axes;</item>
/// <item>ρ = (p_amb / 95.5 kPa) · f_air(T_intake) · f_cool(T_coolant), both factors linear and flat
/// outside their points (1.0186 at the warm set);</item>
/// <item>Q_full(n) = ρ · M(n, 69.71) · n / 5555.6, the full-load ceiling; pr = Q / Q_full(n) for the
/// lagged airflow Q, clamped to 0…1 (1 when the engine stands); ψ(pr) = 1 up to 0.60, then 0.969 at
/// 0.70, 0.813 at 0.80, 0.594 at 0.90 and 0 at 1.0, linear between;</item>
/// <item>Q_valve = 50 · d · ψ(pr), plan 3's idle valve with d its low share; Q_thr = ρ · max(0,
/// M(n, θ) − M(n, 0)) · n / 5555.6, the throttle's air above closed;</item>
/// <item>Q_cmd = min(Q_valve + Q_thr, max(Q_full(n), 50 · d)), and Q follows it with a 50 ms lag.</item>
/// </list>
/// At a closed throttle Q = 50 · d, plan 3's valve air.
/// </summary>
public sealed class BreathingLaw
{
    public const double FullThrottle = 69.71, ReferenceKpa = 95.5, Lag = 0.05;

    const double MgPerStrokeFactor = 5555.6, ValveFlow = 50;

    static readonly double[] PressureRatio = { 0.60, 0.70, 0.80, 0.90, 1.0 };
    static readonly double[] Psi = { 1.0, 0.969, 0.813, 0.594, 0.0 };

    readonly double[] rpmAxis, angleAxis;
    readonly double[,] map;
    readonly double[] intakeC, intakeFactor, coolantC, coolantFactor;

    /// <param name="map">mg/stroke, [rpm row, angle column].</param>
    /// <param name="intake">f_air: (°C, factor), °C rising.</param>
    /// <param name="coolant">f_cool: (°C, factor), °C rising.</param>
    public BreathingLaw(double[] rpmAxis, double[] angleAxis, double[,] map,
        IReadOnlyList<(double C, double Factor)> intake, IReadOnlyList<(double C, double Factor)> coolant)
    {
        if (map.GetLength(0) != rpmAxis.Length || map.GetLength(1) != angleAxis.Length)
            throw new ArgumentException("the map's rows and columns must match its axes", nameof(map));
        this.rpmAxis = rpmAxis;
        this.angleAxis = angleAxis;
        this.map = map;
        intakeC = intake.Select(p => p.C).ToArray();
        intakeFactor = intake.Select(p => p.Factor).ToArray();
        coolantC = coolant.Select(p => p.C).ToArray();
        coolantFactor = coolant.Select(p => p.Factor).ToArray();
    }

    /// <summary>The car's law (R12).</summary>
    public static BreathingLaw Car { get; } = new(
        new double[] { 416, 640, 896, 1248, 1504, 2016, 2816, 3008, 3744, 4000, 4256, 4736, 4992, 5504, 6016, 6496 },
        new[] { 0.00, 2.49, 4.98, 7.47, 9.96, 12.45, 14.94, 17.43, 19.92, 23.90, 27.89, 31.87, 35.85, 39.84, 49.80, 69.71 },
        new double[,]
        {
            { 80, 280, 332, 346, 372, 384, 388, 400, 408, 410, 416, 418, 420, 422, 424, 424 },
            { 40, 260, 300, 320, 336, 340, 350, 356, 356, 356, 356, 356, 356, 356, 356, 356 },
            { 32, 200, 250, 270, 292, 314, 332, 344, 348, 348, 352, 354, 356, 356, 356, 356 },
            { 22, 110, 150, 196, 236, 268, 280, 306, 332, 354, 364, 368, 374, 376, 380, 382 },
            { 14,  90, 140, 160, 220, 240, 276, 308, 340, 376, 400, 408, 416, 416, 424, 432 },
            { 10,  70, 110, 148, 184, 226, 272, 308, 332, 370, 388, 400, 420, 424, 432, 440 },
            { 10,  44,  82, 116, 152, 196, 240, 272, 318, 356, 388, 400, 412, 414, 420, 424 },
            { 10,  36,  76, 110, 144, 188, 232, 264, 316, 352, 388, 416, 426, 436, 444, 448 },
            {  8,  32,  58,  80, 124, 160, 208, 250, 290, 346, 388, 436, 456, 472, 490, 500 },
            {  8,  28,  52,  76, 116, 150, 196, 220, 270, 316, 356, 400, 436, 456, 484, 496 },
            {  8,  28,  48,  74, 110, 144, 188, 222, 260, 312, 358, 392, 418, 436, 456, 470 },
            {  8,  28,  44,  68,  96, 130, 172, 208, 236, 304, 352, 390, 428, 444, 476, 496 },
            {  8,  26,  40,  62,  94, 124, 162, 204, 248, 300, 344, 382, 420, 444, 480, 508 },
            {  8,  24,  32,  56,  84, 110, 148, 178, 222, 280, 328, 370, 410, 436, 476, 508 },
            {  8,  24,  32,  52,  76, 100, 138, 164, 212, 264, 308, 350, 396, 428, 478, 512 },
            {  8,  20,  32,  50,  70,  96, 128, 156, 176, 228, 292, 340, 376, 400, 448, 484 },
        },
        new[] { (-30.0, 1.203), (-9.75, 1.117), (9.75, 1.031), (30.0, 0.969), (60.0, 0.883), (90.0, 0.805) },
        new[] { (-30.0, 1.203), (-20.0, 1.102), (-9.8, 1.047), (0.0, 1.016), (20.2, 1.0), (90.0, 1.0) });

    /// <summary>M(n, θ), mg/stroke: bilinear, flat outside the axes.</summary>
    public double MgPerStroke(double rpm, double throttleAngle)
    {
        var (r0, r1, rt) = Bracket(rpmAxis, rpm);
        var (a0, a1, at) = Bracket(angleAxis, throttleAngle);
        double Row(int r) => map[r, a0] + (map[r, a1] - map[r, a0]) * at;
        return Row(r0) + (Row(r1) - Row(r0)) * rt;
    }

    /// <summary>ρ = (p_amb / 95.5 kPa) · f_air(T_intake) · f_cool(T_coolant).</summary>
    public double Density(double pAmbientKpa, double intakeC, double coolantC) =>
        pAmbientKpa / ReferenceKpa * Linear(this.intakeC, intakeFactor, intakeC) * Linear(this.coolantC, coolantFactor, coolantC);

    /// <summary>The lagged airflow after <paramref name="dtSeconds"/> from <paramref name="lastAirflow"/>, kg/h.</summary>
    public double AirflowKgPerHour(double rpm, double throttleAngle, double valveLowShare, double pAmbientKpa,
        double intakeC, double coolantC, double lastAirflow, double dtSeconds)
    {
        var rho = Density(pAmbientKpa, intakeC, coolantC);
        var perStroke = rpm / MgPerStrokeFactor;
        var full = rho * MgPerStroke(rpm, FullThrottle) * perStroke;
        var pr = full > 0 ? Math.Clamp(lastAirflow / full, 0, 1) : 1;
        var valve = ValveFlow * valveLowShare * Linear(PressureRatio, Psi, pr);
        var throttle = rho * Math.Max(0, MgPerStroke(rpm, throttleAngle) - MgPerStroke(rpm, 0)) * perStroke;
        var command = Math.Min(valve + throttle, Math.Max(full, ValveFlow * valveLowShare));
        return lastAirflow + (command - lastAirflow) * (1 - Math.Exp(-dtSeconds / Lag));
    }

    /// <summary>The two points around <paramref name="at"/> and its fraction between them; flat outside the axis.</summary>
    static (int Low, int High, double Fraction) Bracket(double[] axis, double at)
    {
        if (at <= axis[0])
            return (0, 0, 0);
        if (at >= axis[^1])
            return (axis.Length - 1, axis.Length - 1, 0);
        var i = Array.BinarySearch(axis, at);
        if (i >= 0)
            return (i, i, 0);
        i = ~i - 1;
        return (i, i + 1, (at - axis[i]) / (axis[i + 1] - axis[i]));
    }

    static double Linear(double[] x, double[] y, double at)
    {
        var (low, high, fraction) = Bracket(x, at);
        return y[low] + (y[high] - y[low]) * fraction;
    }
}
