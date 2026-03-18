namespace Ms43Sim.Core;

/// <summary>MOV, MOVB, MOVBZ, MOVBS (ISM ch. 5). Operand nibbles: n = b1 bits 7:4, m = b1 bits 3:0.</summary>
public sealed partial class C167Cpu
{
    void ExecuteMoveWord(byte op, byte b1, ushort w1)
    {
        int n = b1 >> 4, m = b1 & 0xF;
        ushort value, pointer;
        switch (op)
        {
            case 0xF0:  // MOV Rn, Rm
                value = R(m); MoveFlags(value, false); SetR(n, value); return;
            case 0xE0:  // MOV Rm, #data4 (data in the high nibble)
                value = (ushort)n; MoveFlags(value, false); SetR(m, value); return;
            case 0xE6:  // MOV reg, #data16
                value = w1; MoveFlags(value, false); WriteReg(b1, value); return;
            case 0xF2:  // MOV reg, mem
                value = ReadWord(w1); MoveFlags(value, false); WriteReg(b1, value); return;
            case 0xF6:  // MOV mem, reg
                value = ReadReg(b1); MoveFlags(value, false); WriteWord(w1, value); return;
            case 0xA8:  // MOV Rn, [Rm]
                value = ReadWord(R(m)); MoveFlags(value, false); SetR(n, value); return;
            case 0x98:  // MOV Rn, [Rm+]
                pointer = R(m); value = ReadWord(pointer); MoveFlags(value, false);
                SetR(m, (ushort)(pointer + 2)); SetR(n, value); return;
            case 0xB8:  // MOV [Rm], Rn
                value = R(n); MoveFlags(value, false); WriteWord(R(m), value); return;
            case 0x88:  // MOV [-Rm], Rn
                pointer = (ushort)(R(m) - 2); value = R(n); MoveFlags(value, false);
                WriteWord(pointer, value); SetR(m, pointer); return;
            case 0xC8:  // MOV [Rn], [Rm]
                value = ReadWord(R(m)); MoveFlags(value, false); WriteWord(R(n), value); return;
            case 0xD8:  // MOV [Rn+], [Rm]
                value = ReadWord(R(m)); pointer = R(n); MoveFlags(value, false);
                WriteWord(pointer, value); SetR(n, (ushort)(pointer + 2)); return;
            case 0xE8:  // MOV [Rn], [Rm+]
                pointer = R(m); value = ReadWord(pointer); MoveFlags(value, false);
                WriteWord(R(n), value); SetR(m, (ushort)(pointer + 2)); return;
            case 0xD4:  // MOV Rn, [Rm+#data16]
                value = ReadWord((ushort)(R(m) + w1)); MoveFlags(value, false); SetR(n, value); return;
            case 0xC4:  // MOV [Rm+#data16], Rn
                value = R(n); MoveFlags(value, false); WriteWord((ushort)(R(m) + w1), value); return;
            case 0x84:  // MOV [Rm], mem
                value = ReadWord(w1); MoveFlags(value, false); WriteWord(R(m), value); return;
            case 0x94:  // MOV mem, [Rm]
                value = ReadWord(R(m)); MoveFlags(value, false); WriteWord(w1, value); return;
        }
    }

    void ExecuteMoveByte(byte op, byte b1, ushort w1)
    {
        int n = b1 >> 4, m = b1 & 0xF;
        byte value;
        ushort pointer;
        switch (op)
        {
            case 0xF1:  // MOVB Rbn, Rbm
                value = Rb(m); MoveFlags(value, true); SetRb(n, value); return;
            case 0xE1:  // MOVB Rbm, #data4 (data in the high nibble)
                value = (byte)n; MoveFlags(value, true); SetRb(m, value); return;
            case 0xE7:  // MOVB reg, #data8
                value = (byte)w1; MoveFlags(value, true); WriteRegByte(b1, value); return;
            case 0xF3:  // MOVB reg, mem
                value = ReadByte(w1); MoveFlags(value, true); WriteRegByte(b1, value); return;
            case 0xF7:  // MOVB mem, reg
                value = ReadRegByte(b1); MoveFlags(value, true); WriteByte(w1, value); return;
            case 0xA9:  // MOVB Rbn, [Rm]
                value = ReadByte(R(m)); MoveFlags(value, true); SetRb(n, value); return;
            case 0x99:  // MOVB Rbn, [Rm+]
                pointer = R(m); value = ReadByte(pointer); MoveFlags(value, true);
                SetR(m, (ushort)(pointer + 1)); SetRb(n, value); return;
            case 0xB9:  // MOVB [Rm], Rbn
                value = Rb(n); MoveFlags(value, true); WriteByte(R(m), value); return;
            case 0x89:  // MOVB [-Rm], Rbn
                pointer = (ushort)(R(m) - 1); value = Rb(n); MoveFlags(value, true);
                WriteByte(pointer, value); SetR(m, pointer); return;
            case 0xC9:  // MOVB [Rn], [Rm]
                value = ReadByte(R(m)); MoveFlags(value, true); WriteByte(R(n), value); return;
            case 0xD9:  // MOVB [Rn+], [Rm]
                value = ReadByte(R(m)); pointer = R(n); MoveFlags(value, true);
                WriteByte(pointer, value); SetR(n, (ushort)(pointer + 1)); return;
            case 0xE9:  // MOVB [Rn], [Rm+]
                pointer = R(m); value = ReadByte(pointer); MoveFlags(value, true);
                WriteByte(R(n), value); SetR(m, (ushort)(pointer + 1)); return;
            case 0xF4:  // MOVB Rbn, [Rm+#data16]
                value = ReadByte((ushort)(R(m) + w1)); MoveFlags(value, true); SetRb(n, value); return;
            case 0xE4:  // MOVB [Rm+#data16], Rbn
                value = Rb(n); MoveFlags(value, true); WriteByte((ushort)(R(m) + w1), value); return;
            case 0xA4:  // MOVB [Rm], mem
                value = ReadByte(w1); MoveFlags(value, true); WriteByte(R(m), value); return;
            case 0xB4:  // MOVB mem, [Rm]
                value = ReadByte(R(m)); MoveFlags(value, true); WriteByte(w1, value); return;
        }
    }

    /// <summary>MOVBZ (E = 0, N = 0) and MOVBS (E = 0, N = sign): a byte widened to a word.</summary>
    void ExecuteMoveExtend(byte op, byte b1, ushort w1)
    {
        var signed = op is 0xD0 or 0xD2 or 0xD5;
        byte source;
        switch (op)
        {
            case 0xC0: case 0xD0:  // MOVBZ/MOVBS Rw(m), Rb(n)
                source = Rb(b1 >> 4);
                SetR(b1 & 0xF, Widen(source, signed));
                return;
            case 0xC2: case 0xD2:  // MOVBZ/MOVBS reg, mem
                source = ReadByte(w1);
                WriteReg(b1, Widen(source, signed));
                return;
            case 0xC5: case 0xD5:  // MOVBZ/MOVBS mem, reg
                source = ReadRegByte(b1);
                WriteWord(w1, Widen(source, signed));
                return;
        }
    }

    ushort Widen(byte source, bool signed)
    {
        SetFlag(PswFlags.E, false);
        SetFlag(PswFlags.Z, source == 0);
        SetFlag(PswFlags.N, signed && (source & 0x80) != 0);
        return signed ? (ushort)(sbyte)source : source;
    }
}
