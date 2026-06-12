namespace Ms43Sim.Core;

/// <summary>
/// Engine speed per road speed in each gear (plan 4 R9) [inf: an E46 325i 5-speed with a 3.15 final
/// drive and a 1.913 m rolling circumference; ver for 3rd and 4th]. Each lands in the firmware's
/// gear band for 81AC: under 29 rpm per km/h is 5th, 29–39 4th, 40–55 3rd, 56–85 2nd, 86–179 1st.
/// </summary>
public static class GearRatios
{
    static readonly double[] Ratios = { 0, 115.5, 68.3, 45.6, 34.0, 27.4 };

    /// <summary>rpm per km/h in <paramref name="gear"/> (1–5); 0 in neutral (0).</summary>
    public static double RpmPerKmh(int gear)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gear);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(gear, 5);
        return Ratios[gear];
    }
}
