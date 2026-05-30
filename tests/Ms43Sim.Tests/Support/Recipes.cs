namespace Ms43Sim.Tests.Support;

/// <summary>
/// The ECU's decisions at warm idle, computed by hand from the calibration (design §8.5; plan 3,
/// Reference facts, "Decisions and the calibration"): integer widths, floor divisions, the lerp's
/// round half up and saturation at FFFFh. Every RAM term comes from the task's entry snapshot,
/// read through <c>entry</c> (words as words, bytes as bytes).
/// </summary>
public static class Recipes
{
    /// <summary>E2B5 on the idle path (FD9A &amp; 7 = 2): the idle map plus the idle-speed intervention and the neutral terms.</summary>
    public static int BaseAngle(Calibration cal, Func<uint, int> entry)
    {
        var ron = Calibration.Map(cal.Axis16(0x7506E), cal.Axis16(0x7507C), cal.Values8(0x775D4, 36), entry(0xE0E4), entry(0xE32C));
        var nd = Calibration.Curve(cal.Axis16(0x74FAA), cal.Values8(0x76FCD, 12), (entry(0x88DE) + 0x8000) & 0xFFFF);
        var limit = Math.Min(Math.Min((entry(0x849C) + entry(0x849D) - 128) & 0xFF, entry(0xE383)), Math.Min(entry(0x8849), entry(0x883F)));
        var s = ron + (entry(0x8437) - 128) + (entry(0x81D1) - 128) + (entry(0x81D2) - 128)
                + (Calibration.Scale(nd, entry(0x81DE)) - 128)
                + ((entry(0x8846) >> 8) - 128) + ((entry(0xE2C6) >> 8) - 128)
                + (limit - 128);
        return Math.Clamp(s, 0, 255);
    }

    /// <summary>FBAB+i = NOT clamp(E2B5 + sat_s8((s8)(83A2+i − 80h) + (s8)(80E1+i)), 0, 255).</summary>
    public static int CylinderAngle(int baseAngle, int knock, int trim)
    {
        var correction = Math.Clamp((sbyte)(knock - 0x80) + (sbyte)trim, sbyte.MinValue, sbyte.MaxValue);
        return ~Math.Clamp(baseAngle + correction, 0, 255) & 0xFF;
    }

    /// <summary>K = floor(floor(c_inj_flow × c_af_stoich_ron98 / 10) × c_fuel_density_ron98 / 2¹⁵) (RAM 91B6).</summary>
    public static int InjectorConstant(Calibration cal) =>
        (int)((long)(cal.U16(0x70E14) * cal.U8(0x70E11) / 10) * cal.U16(0x70E12) / 32768);

    /// <summary>E46A (= E46C) from an injection entry; holds with FD48.0 = 0, FD2C.8 = 0, FD4E.1 = 0 and 919A = 0.</summary>
    public static int BankTime(Calibration cal, Func<uint, int> entry)
    {
        long b3 = Math.Min(entry(0xE33A) * 477L / InjectorConstant(cal), 0xFFFF);
        long e = Math.Min(entry(0x919A) + 0x1000L, 0xFFFF);
        if ((entry(0xFD36) & 0x0200) != 0)
            e = Math.Clamp(e + 16L * (Calibration.Curve(cal.Axis16(0x75798), cal.Values8(0x78CC0, 6), (entry(0x88DE) + 0x8000) & 0xFFFF) - 128), 0, 0xFFFF);
        var t = Math.Min(b3 * entry(0x8C84) / 4096, 0xFFFF);
        t = Math.Clamp(t + (short)entry(0x8C7E), 0, 0xFFFF);
        t = Math.Min(t * e / 4096, 0xFFFF);
        if ((entry(0xFD4E) & 1) != 0)
            t = Math.Clamp(t + (short)entry(0x9174), 0, 0xFFFF);
        var product = t * entry(0x9172);
        t = product >= 1L << 31 ? 0xFFFF : product >> 15;
        if (entry(0x8D0F) != 0x80)
            t = Math.Min(t * (entry(0x8D0F) << 1) / 256, 0xFFFF);
        return (int)Math.Max(t, cal.U16(0x7096E));
    }

    /// <summary>E47C = 8 × curve(u8 0x78753) over the u8 battery axis 0x7428D at FBD5.</summary>
    public static int DeadTime(Calibration cal, int fbd5) => 8 * Calibration.Curve(cal.Axis8(0x7428D), cal.Values8(0x78753, 8), fbd5);

    /// <summary>E4B8 = clamp(curve(u16 0x7C924 pencil coils, else 0x7C8F4) + 16 × (s8)80ED, 0, FFFFh) at FBD5.</summary>
    public static int BaseDwell(Calibration cal, int fbd5, bool pencilCoils, int tdAs) =>
        Math.Clamp(Calibration.Curve(cal.Axis8(0x7428D), cal.Values16(pencilCoils ? 0x7C924 : 0x7C8F4, 8), fbd5) + 16 * tdAs, 0, 0xFFFF);
}
