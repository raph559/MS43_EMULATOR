namespace Ms43Sim.Core;

/// <summary>Bit instructions and the JB family (ISM ch. 5; encodings from tools/c167dis.py).</summary>
public sealed partial class C167Cpu
{
    /// <summary>Short 'bitoff' (ISM §6.1): 00-7F IRAM FD00+2b; 80-EF SFR FF00 (ESFR F100 under EXTR); F0-FF GPR.</summary>
    ushort ReadBitWord(byte bitoff) =>
        bitoff >= 0xF0 ? R(bitoff & 0xF) : bus.Read16(BitWordAddress(bitoff));

    void WriteBitWord(byte bitoff, ushort value)
    {
        if (bitoff >= 0xF0)
            SetR(bitoff & 0xF, value);
        else
            bus.Write16(BitWordAddress(bitoff), value);
    }

    uint BitWordAddress(byte bitoff) => bitoff < 0x80
        ? 0xFD00u + 2u * bitoff
        : (extrActive ? 0xF100u : 0xFF00u) + 2u * (bitoff & 0x7Fu);

    /// <summary>BCLR/BSET/JBC/JNBS flags: E = V = C = 0, Z = ¬old, N = old.</summary>
    void SingleBitFlags(bool old)
    {
        SetFlag(PswFlags.E, false);
        SetFlag(PswFlags.V, false);
        SetFlag(PswFlags.C, false);
        SetFlag(PswFlags.Z, !old);
        SetFlag(PswFlags.N, old);
    }

    /// <summary>qE / qF: BCLR / BSET bitoff.q.</summary>
    void ExecuteBitSetClear(byte op, byte bitoff)
    {
        var bit = op >> 4;
        var word = ReadBitWord(bitoff);
        var old = ((word >> bit) & 1) != 0;
        SingleBitFlags(old);
        var mask = (ushort)(1 << bit);
        WriteBitWord(bitoff, (op & 0xF) == 0xF ? (ushort)(word | mask) : (ushort)(word & ~mask));
    }

    /// <summary>0A BFLDL QQ mask data, 1A BFLDH QQ data mask: Z and N on the word result.</summary>
    void ExecuteBitField(byte op, byte bitoff, ushort w1)
    {
        var word = ReadBitWord(bitoff);
        ushort result;
        if (op == 0x0A)
        {
            byte mask = (byte)w1, data = (byte)(w1 >> 8);
            result = (ushort)((word & 0xFF00) | (((word & 0xFF) & ~mask) | data) & 0xFF);
        }
        else
        {
            byte data = (byte)w1, mask = (byte)(w1 >> 8);
            result = (ushort)((((word >> 8) & ~mask | data) << 8) & 0xFF00 | (word & 0x00FF));
        }
        SetFlag(PswFlags.E, false);
        SetFlag(PswFlags.V, false);
        SetFlag(PswFlags.C, false);
        SetFlag(PswFlags.Z, result == 0);
        SetFlag(PswFlags.N, (result & 0x8000) != 0);
        WriteBitWord(bitoff, result);
    }

    /// <summary>
    /// xA src dst (q&lt;&lt;4|z) for x = 2..7: BCMP, BMOVN, BMOV, BOR, BAND, BXOR. BMOV/BMOVN set
    /// Z = ¬source, N = source; the logic ones set Z = NOR, V = OR, C = AND, N = XOR of the two
    /// old bits. E is always 0.
    /// </summary>
    void ExecuteBitLogic(byte op, byte sourceBitoff, ushort w1)
    {
        var destinationBitoff = (byte)w1;
        var sourceBit = (w1 >> 12) & 0xF;
        var destinationBit = (w1 >> 8) & 0xF;
        var source = ((ReadBitWord(sourceBitoff) >> sourceBit) & 1) != 0;
        var destinationWord = ReadBitWord(destinationBitoff);
        var destination = ((destinationWord >> destinationBit) & 1) != 0;

        bool result;
        SetFlag(PswFlags.E, false);
        if (op is 0x3A or 0x4A)
        {
            SetFlag(PswFlags.V, false);
            SetFlag(PswFlags.C, false);
            SetFlag(PswFlags.Z, !source);
            SetFlag(PswFlags.N, source);
            result = op == 0x4A ? source : !source;
        }
        else
        {
            SetFlag(PswFlags.Z, !(source || destination));
            SetFlag(PswFlags.V, source || destination);
            SetFlag(PswFlags.C, source && destination);
            SetFlag(PswFlags.N, source ^ destination);
            if (op == 0x2A)
                return;   // BCMP writes nothing
            result = op switch { 0x5A => destination || source, 0x6A => destination && source, _ => destination ^ source };
        }
        var mask = (ushort)(1 << destinationBit);
        WriteBitWord(destinationBitoff, result ? (ushort)(destinationWord | mask) : (ushort)(destinationWord & ~mask));
    }

    /// <summary>8A JB, 9A JNB, AA JBC, BA JNBS: QQ rel q0. JBC/JNBS change the bit and set flags.</summary>
    void ExecuteJumpOnBit(byte op, byte bitoff, ushort w1)
    {
        var rel = (sbyte)(byte)w1;
        var bit = (w1 >> 12) & 0xF;
        var word = ReadBitWord(bitoff);
        var set = ((word >> bit) & 1) != 0;
        var mask = (ushort)(1 << bit);
        bool taken;
        switch (op)
        {
            case 0x8A: taken = set; break;
            case 0x9A: taken = !set; break;
            case 0xAA:
                SingleBitFlags(set);
                taken = set;
                if (taken) WriteBitWord(bitoff, (ushort)(word & ~mask));
                break;
            default:
                SingleBitFlags(set);
                taken = !set;
                if (taken) WriteBitWord(bitoff, (ushort)(word | mask));
                break;
        }
        if (taken)
            BranchTo(csp, (ushort)(ip + 2 * rel), BranchKind.Jump);
    }
}
