namespace Ms43Sim.Tests.Support;

/// <summary>
/// The firmware's lookups over a 512 KiB image, read independently of the emulator (plan 3,
/// Reference facts, "Lookups", 0D:0002–0D:0356): file offsets, little-endian; a u8 axis is
/// [n:u8][n × u8], a u16 axis [n:u16][n × u16]; values carry no header; a 2-D map is X-major,
/// v[ix × nY + iy].
/// </summary>
public sealed class Calibration
{
    readonly byte[] image;

    public Calibration(byte[] image) => this.image = image;

    public int U8(int offset) => image[offset];

    public int U16(int offset) => image[offset] | image[offset + 1] << 8;

    public int[] Axis8(int offset) => Values8(offset + 1, U8(offset));

    public int[] Axis16(int offset) => Values16(offset + 2, U16(offset));

    public int[] Values8(int offset, int n) => Enumerable.Range(0, n).Select(i => U8(offset + i)).ToArray();

    public int[] Values16(int offset, int n) => Enumerable.Range(0, n).Select(i => U16(offset + 2 * i)).ToArray();

    /// <summary>
    /// x at or above the last point: (n − 1, 0); at or below the first: (0, 0); otherwise the largest
    /// i with a[i] ≤ x and f = floor((x − a[i]) × 65536 / (a[i+1] − a[i])), 0 when x = a[i].
    /// </summary>
    public static (int Index, int Fraction) Search(int[] axis, int x)
    {
        var n = axis.Length;
        if (x >= axis[n - 1])
            return (n - 1, 0);
        if (x <= axis[0])
            return (0, 0);
        var i = 0;
        while (axis[i + 1] <= x)
            i++;
        return (i, x == axis[i] ? 0 : (int)((long)(x - axis[i]) * 65536 / (axis[i + 1] - axis[i])));
    }

    /// <summary>v0 when f = 0; else v0 moved towards v1 by (|v1 − v0| × f + 8000h) >> 16.</summary>
    public static int Lerp(int v0, int v1, int fraction)
    {
        if (fraction == 0)
            return v0;
        var step = (int)(((long)Math.Abs(v1 - v0) * fraction + 0x8000) >> 16);
        return v1 >= v0 ? v0 + step : v0 - step;
    }

    public static int Curve(int[] axis, int[] values, int x)
    {
        var (i, f) = Search(axis, x);
        return f == 0 ? values[i] : Lerp(values[i], values[i + 1], f);
    }

    /// <summary>Along Y first, a = ix·nY + iy.</summary>
    public static int Map(int[] xAxis, int[] yAxis, int[] values, int x, int y)
    {
        var (ix, fx) = Search(xAxis, x);
        var (iy, fy) = Search(yAxis, y);
        var nY = yAxis.Length;
        var a = ix * nY + iy;
        if (fy == 0)
            return fx == 0 ? values[a] : Lerp(values[a], values[a + nY], fx);
        var r0 = Lerp(values[a], values[a + 1], fy);
        return fx == 0 ? r0 : Lerp(r0, Lerp(values[a + nY], values[a + nY + 1], fy), fx);
    }

    /// <summary>((((v − 80h)·f) mod 2¹⁶ + 8000h) mod 2¹⁶) >> 8 (0A:BBA2).</summary>
    public static int Scale(int v, int f) => ((((v - 0x80) * f) & 0xFFFF) + 0x8000 & 0xFFFF) >> 8;
}
