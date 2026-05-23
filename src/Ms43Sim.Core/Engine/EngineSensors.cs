namespace Ms43Sim.Core;

/// <summary>
/// The engine's sensor laws (plan 3, Reference facts). They read no RAM and no calibration: where a
/// law is the car's own table, the table is frozen here as the sensor's characteristic.
/// <list type="bullet">
/// <item>MAF on AN0: linear interpolation through the car's <c>id_maf_tab__v_maf</c> (0x70EC6),
/// flat outside 0…4.95 V [obs: the car's table frozen as the sensor's law; assumption below 0.10 V].</item>
/// <item>Knock envelope on AN15: the 8-bit noise code c(n) of the car's <c>ip_nl_sp__n</c> (0x77CBB),
/// linear between its points and flat outside them, read as (c + 0.5) × 5 / 256 V; 0 V below
/// 50 rpm [obs, ver; frozen as the sensor's law].</item>
/// <item>Coil current on AN14: 2.7 V (a peak-hold) while the shaft turns, 0 V stopped [assumption].</item>
/// <item>Burn feedback on P3.6: high for 1.2 ms after each spark, retriggered by a later one [assumption].</item>
/// </list>
/// </summary>
public static class EngineSensors
{
    /// <summary>AN14 while the shaft turns (≥ <see cref="TurningRpm"/>).</summary>
    public const double CoilCurrentVolts = 2.7;

    /// <summary>P3.6 high for 1.2 ms after each spark.</summary>
    public const ulong BurnPulseCycles = 28_800;

    /// <summary>Below this speed the shaft counts as stopped: no coil current, no knock noise.</summary>
    public const double TurningRpm = 50;

    static readonly double[] MafV =
        { 0.00, 0.10, 0.25, 0.50, 0.75, 0.87, 1.00, 1.25, 1.50, 1.75, 2.00, 2.25, 2.50, 2.75, 3.00, 3.25, 3.50, 3.75, 4.00, 4.25, 4.50, 4.75, 4.95 };
    static readonly double[] MafQ =
        { 0.0, 5.1, 6.6, 10.0, 14.5, 17.0, 20.25, 27.6, 36.3, 47.3, 60.8, 77.9, 99.5, 124, 156, 194, 240, 294, 354, 435, 537, 655, 755 };
    static readonly double[] KnockRpm = { 640, 900, 992, 1504, 2016, 3008, 4000 };
    static readonly double[] KnockCode = { 7, 8, 9, 15, 23, 37, 40 };

    /// <summary>The airflow the MAF's characteristic gives for <paramref name="volts"/>, kg/h.</summary>
    public static double MafKgPerHour(double volts) => Interpolate(MafV, MafQ, volts);

    /// <summary>The characteristic's inverse, 0…4.95 V.</summary>
    public static double MafVolts(double kgPerHour) => Interpolate(MafQ, MafV, kgPerHour);

    /// <summary>(c(n) + 0.5) × 5 / 256 V; 0 V below 50 rpm.</summary>
    public static double KnockVolts(double rpm) =>
        rpm < TurningRpm ? 0 : (Interpolate(KnockRpm, KnockCode, rpm) + 0.5) * 5 / 256;

    /// <summary>Linear between the points, flat outside them; a point gives its own value exactly.</summary>
    static double Interpolate(double[] x, double[] y, double at)
    {
        if (at <= x[0])
            return y[0];
        if (at >= x[^1])
            return y[^1];
        var i = Array.BinarySearch(x, at);
        if (i >= 0)
            return y[i];
        i = ~i - 1;                                     // x[i] < at < x[i + 1]
        return y[i] + (at - x[i]) / (x[i + 1] - x[i]) * (y[i + 1] - y[i]);
    }
}
