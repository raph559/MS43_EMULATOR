namespace Ms43Sim.Core;

/// <summary>
/// The car's temperature sensors, volts against °C, linear between the points and flat outside them
/// (plan 4 R10) [ver, plan 3 sensors research]. The coolant sensor (mux A 3) and the radiator outlet
/// (mux A 0) share one curve; the intake air (mux A 1) and the oil (mux A 2) have their own.
/// </summary>
public static class SensorCurves
{
    static readonly Curve Coolant = new(
        new[] { 0.283, 0.381, 0.610, 0.781, 1.001, 1.279, 1.621, 2.021, 2.964, 3.867, 4.224, 4.497, 4.688, 4.819, 4.897, 4.971 },
        new[] { 142.5, 129.8, 110.2, 99.8, 90.0, 80.2, 69.8, 60.0, 39.8, 20.2, 9.8, 0.0, -9.8, -20.2, -30.0, -48.0 });

    static readonly Curve Intake = new(
        new[] { 0.215, 0.273, 0.469, 0.605, 0.781, 1.016, 1.309, 1.660, 2.090, 3.086, 4.004, 4.336, 4.590, 4.746, 4.922, 4.961 },
        new[] { 142.5, 129.8, 110.2, 99.8, 90.0, 80.2, 69.8, 60.0, 50.2, 30.0, 9.8, 0.0, -9.8, -20.2, -39.8, -48.0 });

    static readonly Curve Oil = new(
        new[] { 0.195, 0.293, 0.488, 0.605, 0.781, 0.996, 1.270, 1.621, 2.012, 3.418, 3.848, 4.199, 4.473, 4.668, 4.883, 4.941 },
        new[] { 155.0, 139.9, 120.0, 109.6, 100.1, 89.7, 80.2, 70.6, 60.3, 30.0, 19.7, 10.1, -0.2, -9.8, -29.7, -48.0 });

    public static double CoolantVolts(double celsius) => Coolant.Volts(celsius);

    public static double IntakeVolts(double celsius) => Intake.Volts(celsius);

    public static double OilVolts(double celsius) => Oil.Volts(celsius);

    public static double CoolantC(double volts) => Coolant.Celsius(volts);

    public static double IntakeC(double volts) => Intake.Celsius(volts);

    public static double OilC(double volts) => Oil.Celsius(volts);

    /// <summary>Volts rising, °C falling.</summary>
    sealed class Curve(double[] volts, double[] celsius)
    {
        readonly double[] risingC = celsius.Reverse().ToArray(), fallingV = volts.Reverse().ToArray();

        public double Celsius(double v) => Interpolate(volts, celsius, v);

        public double Volts(double c) => Interpolate(risingC, fallingV, c);
    }

    /// <summary>Linear between the points of the rising <paramref name="x"/>, flat outside them; a point gives its own value exactly.</summary>
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
