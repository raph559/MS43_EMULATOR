namespace Ms43Sim.Core;

/// <summary>The 16 branch condition codes (ISM Table 5).</summary>
public static class Conditions
{
    public static bool Holds(int cc, ushort psw)
    {
        bool n = (psw & 0x01) != 0, c = (psw & 0x02) != 0, v = (psw & 0x04) != 0;
        bool z = (psw & 0x08) != 0, e = (psw & 0x10) != 0;
        return cc switch
        {
            0x0 => true,              // UC
            0x1 => !(z || e),         // NET
            0x2 => z,                 // Z / EQ
            0x3 => !z,                // NZ / NE
            0x4 => v,                 // V
            0x5 => !v,                // NV
            0x6 => n,                 // N
            0x7 => !n,                // NN
            0x8 => c,                 // C / ULT
            0x9 => !c,                // NC / UGE
            0xA => !(z || (n ^ v)),   // SGT
            0xB => z || (n ^ v),      // SLE
            0xC => n ^ v,             // SLT
            0xD => !(n ^ v),          // SGE
            0xE => !(z || c),         // UGT
            _ => z || c,              // ULE
        };
    }
}
