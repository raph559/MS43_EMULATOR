namespace Ms43Sim.Core;

/// <summary>
/// ADD..OR, NEG, CPL, CMPI/CMPD, shifts and rotates, MUL/DIV, PRIOR. Flags per ISM ch. 5.
/// </summary>
public sealed partial class C167Cpu
{
    enum AluOp { Add, Addc, Sub, Subc, Cmp, Xor, And, Or }

    enum ShiftOp { Rol, Ror, Shl, Shr, Ashr }

    /// <summary>
    /// Rows 0-7, columns 0-9: the eight operations in every addressing form. op1 is read once,
    /// and CMP stores nothing: on an SFR a write-back would have side effects (ISM: CMP is
    /// (op1) - (op2) with no destination).
    /// </summary>
    void ExecuteArithmetic(byte op, byte b1, ushort w1)
    {
        var operation = (AluOp)(op >> 4);
        var store = operation != AluOp.Cmp;
        var lo = op & 0xF;
        var isByte = (lo & 1) != 0;
        int n = b1 >> 4, m = b1 & 0xF;
        switch (lo)
        {
            case 0:  // Rn, Rm
            {
                var r = Alu(operation, R(n), R(m), false);
                if (store) SetR(n, r);
                return;
            }
            case 1:  // Rbn, Rbm
            {
                var r = Alu(operation, Rb(n), Rb(m), true);
                if (store) SetRb(n, (byte)r);
                return;
            }
            case 2:  // reg, mem
            {
                var a = ReadReg(b1);
                var r = Alu(operation, a, ReadWord(w1), false);
                if (store) WriteReg(b1, r);
                return;
            }
            case 3:
            {
                var a = ReadRegByte(b1);
                var r = Alu(operation, a, ReadByte(w1), true);
                if (store) WriteRegByte(b1, (byte)r);
                return;
            }
            case 4:  // mem, reg (44h/45h, CMP mem,reg, do not exist: they never get here)
            {
                var a = ReadWord(w1);
                var r = Alu(operation, a, ReadReg(b1), false);
                if (store) WriteWord(w1, r);
                return;
            }
            case 5:
            {
                var a = ReadByte(w1);
                var r = Alu(operation, a, ReadRegByte(b1), true);
                if (store) WriteByte(w1, (byte)r);
                return;
            }
            case 6:  // reg, #data16
            {
                var a = ReadReg(b1);
                var r = Alu(operation, a, w1, false);
                if (store) WriteReg(b1, r);
                return;
            }
            case 7:  // reg, #data8
            {
                var a = ReadRegByte(b1);
                var r = Alu(operation, a, (byte)w1, true);
                if (store) WriteRegByte(b1, (byte)r);
                return;
            }
            default: // 8/9: Rn, #data3 | [Ri] | [Ri+]
            {
                ushort operand;
                if ((m & 8) == 0)
                {
                    operand = (ushort)(m & 7);
                }
                else
                {
                    var pointer = R(m & 3);
                    operand = isByte ? ReadByte(pointer) : ReadWord(pointer);
                    if ((m & 0xC) == 0xC)
                        SetR(m & 3, (ushort)(pointer + (isByte ? 1 : 2)));
                }
                if (isByte)
                {
                    var r = Alu(operation, Rb(n), operand, true);
                    if (store) SetRb(n, (byte)r);
                }
                else
                {
                    var r = Alu(operation, R(n), operand, false);
                    if (store) SetR(n, r);
                }
                return;
            }
        }
    }

    /// <summary>Computes op1 OP op2, sets the flags, and returns the result (the caller does not store it for CMP).</summary>
    ushort Alu(AluOp operation, ushort a, ushort b, bool isByte)
    {
        uint mask = isByte ? 0xFFu : 0xFFFFu;
        uint msb = isByte ? 0x80u : 0x8000u;
        uint carryIn = Flag(PswFlags.C) ? 1u : 0u;
        uint r;
        switch (operation)
        {
            case AluOp.Add:
            case AluOp.Addc:
            {
                var c = operation == AluOp.Addc ? carryIn : 0;
                var full = a + (uint)b + c;
                r = full & mask;
                SetFlag(PswFlags.C, full > mask);
                SetFlag(PswFlags.V, ((a ^ r) & (b ^ r) & msb) != 0);
                break;
            }
            case AluOp.Sub:
            case AluOp.Subc:
            case AluOp.Cmp:
            {
                var c = operation == AluOp.Subc ? carryIn : 0;
                var full = (long)a - b - c;
                r = (uint)full & mask;
                SetFlag(PswFlags.C, full < 0);
                SetFlag(PswFlags.V, ((a ^ b) & (a ^ r) & msb) != 0);
                break;
            }
            case AluOp.Xor: r = (uint)(a ^ b) & mask; LogicCarryOverflow(); break;
            case AluOp.And: r = (uint)(a & b) & mask; LogicCarryOverflow(); break;
            default: r = (uint)(a | b) & mask; LogicCarryOverflow(); break;
        }
        var sticky = operation is AluOp.Addc or AluOp.Subc;
        SetFlag(PswFlags.Z, r == 0 && (!sticky || Flag(PswFlags.Z)));
        SetFlag(PswFlags.E, b == msb);
        SetFlag(PswFlags.N, (r & msb) != 0);
        return (ushort)r;
    }

    void LogicCarryOverflow()
    {
        SetFlag(PswFlags.C, false);
        SetFlag(PswFlags.V, false);
    }

    /// <summary>NEG/NEGB (0 - op1) and CPL/CPLB (one's complement); E tests op1 itself.</summary>
    void ExecuteNegateComplement(byte op, byte b1)
    {
        var n = b1 >> 4;
        var isByte = op is 0xA1 or 0xB1;
        var negate = op is 0x81 or 0xA1;
        uint mask = isByte ? 0xFFu : 0xFFFFu;
        uint msb = isByte ? 0x80u : 0x8000u;
        uint a = isByte ? Rb(n) : R(n);
        uint r;
        if (negate)
        {
            r = (0 - a) & mask;
            SetFlag(PswFlags.C, a != 0);
            SetFlag(PswFlags.V, a == msb);
        }
        else
        {
            r = ~a & mask;
            LogicCarryOverflow();
        }
        SetFlag(PswFlags.E, a == msb);
        SetFlag(PswFlags.Z, r == 0);
        SetFlag(PswFlags.N, (r & msb) != 0);
        if (isByte)
            SetRb(n, (byte)r);
        else
            SetR(n, (ushort)r);
    }

    /// <summary>CMPI1/CMPI2/CMPD1/CMPD2: compare op1 with op2 (flags as CMP), then step op1.</summary>
    void ExecuteCompareStep(byte op, byte b1, ushort w1)
    {
        var step = (op >> 4) switch { 0x8 => 1, 0x9 => 2, 0xA => -1, _ => -2 };
        switch (op & 0xF)
        {
            case 0x0:  // Rw(m), #data4(n)
            {
                var reg = b1 & 0xF;
                Alu(AluOp.Cmp, R(reg), (ushort)(b1 >> 4), false);
                SetR(reg, (ushort)(R(reg) + step));
                return;
            }
            case 0x2:  // Rw, mem
            {
                var a = ReadReg(b1);
                Alu(AluOp.Cmp, a, ReadWord(w1), false);
                WriteReg(b1, (ushort)(a + step));
                return;
            }
            default:   // 0x6: Rw, #data16
            {
                var a = ReadReg(b1);
                Alu(AluOp.Cmp, a, w1, false);
                WriteReg(b1, (ushort)(a + step));
                return;
            }
        }
    }

    /// <summary>
    /// ROL/ROR/SHL/SHR/ASHR. Count = op2 bits 3:0. SHR/ROR/ASHR set V when a 1 is shifted out
    /// of the carry, i.e. any of bits [0 .. count-2]; count 0 clears C and V (ISM ch. 5).
    /// </summary>
    void ExecuteShift(byte op, byte b1)
    {
        int target, count;
        if (((op >> 4) & 1) == 1)                    // the #data4 forms: 1C 3C 5C 7C BC
        {
            target = b1 & 0xF;
            count = b1 >> 4;
        }
        else                                         // the register forms: 0C 2C 4C 6C AC
        {
            target = b1 >> 4;
            count = R(b1 & 0xF) & 0xF;
        }
        uint a = R(target);
        uint r;
        bool carry = false, overflow = false;
        var kind = op switch
        {
            0x0C or 0x1C => ShiftOp.Rol,
            0x2C or 0x3C => ShiftOp.Ror,
            0x4C or 0x5C => ShiftOp.Shl,
            0x6C or 0x7C => ShiftOp.Shr,
            _ => ShiftOp.Ashr,
        };
        if (count == 0)
        {
            r = a;
        }
        else
        {
            var below = count >= 2 && (a & ((1u << (count - 1)) - 1)) != 0;
            switch (kind)
            {
                case ShiftOp.Shl:
                    r = (a << count) & 0xFFFF;
                    carry = ((a >> (16 - count)) & 1) != 0;
                    break;
                case ShiftOp.Rol:
                    r = ((a << count) | (a >> (16 - count))) & 0xFFFF;
                    carry = ((a >> (16 - count)) & 1) != 0;
                    break;
                case ShiftOp.Shr:
                    r = a >> count;
                    carry = ((a >> (count - 1)) & 1) != 0;
                    overflow = below;
                    break;
                case ShiftOp.Ror:
                    r = ((a >> count) | (a << (16 - count))) & 0xFFFF;
                    carry = ((a >> (count - 1)) & 1) != 0;
                    overflow = below;
                    break;
                default:
                    r = (uint)((short)a >> count) & 0xFFFF;
                    carry = ((a >> (count - 1)) & 1) != 0;
                    overflow = below;
                    break;
            }
        }
        SetFlag(PswFlags.E, false);
        SetFlag(PswFlags.C, carry);
        SetFlag(PswFlags.V, overflow);
        SetFlag(PswFlags.Z, r == 0);
        SetFlag(PswFlags.N, (r & 0x8000) != 0);
        SetR(target, (ushort)r);
    }

    /// <summary>MUL, MULU, DIV, DIVU, DIVL, DIVLU, PRIOR. [assumption] Not interruptible: MULIP stays 0.</summary>
    void ExecuteMulDiv(byte op, byte b1)
    {
        int n = b1 >> 4, m = b1 & 0xF;
        switch (op)
        {
            case 0x0B:  // MUL Rn, Rm
            {
                var product = (int)(short)R(n) * (short)R(m);
                SetProduct((uint)product, product is < short.MinValue or > short.MaxValue);
                return;
            }
            case 0x1B:  // MULU Rn, Rm
            {
                var product = (uint)R(n) * R(m);
                SetProduct(product, product > 0xFFFF);
                return;
            }
            case 0x2B:  // PRIOR Rn, Rm
            {
                var source = R(m);
                var shifts = 0;
                if (source != 0)
                    while ((source << shifts & 0x8000) == 0)
                        shifts++;
                SetFlag(PswFlags.E, false);
                SetFlag(PswFlags.V, false);
                SetFlag(PswFlags.C, false);
                SetFlag(PswFlags.N, false);
                SetFlag(PswFlags.Z, source == 0);
                SetR(n, (ushort)shifts);
                return;
            }
            default:    // 4B DIV, 5B DIVU, 6B DIVL, 7B DIVLU: op1 = Rn
                Divide(op, R(n));
                return;
        }
    }

    void SetProduct(uint product, bool overflow)
    {
        SetFlag(PswFlags.E, false);
        SetFlag(PswFlags.C, false);
        SetFlag(PswFlags.V, overflow);
        SetFlag(PswFlags.Z, product == 0);
        SetFlag(PswFlags.N, (product & 0x8000_0000) != 0);
        mdh = (ushort)(product >> 16);
        mdl = (ushort)product;
        mdc |= Mdriu;
    }

    /// <summary>
    /// MDL ← quotient, MDH ← remainder. [assumption] On V (division by zero, or a long-division
    /// quotient that does not fit a word) MDH and MDL are left unchanged — the ISM only says the
    /// result "is not valid" — and Z/N come from the unchanged MDL. [assumption] Signed division
    /// truncates toward zero; the remainder takes the dividend's sign.
    /// </summary>
    void Divide(byte op, ushort divisor)
    {
        long quotient = 0, remainder = 0;
        var overflow = divisor == 0;
        if (!overflow)
        {
            switch (op)
            {
                case 0x4B:  // DIV: signed MDL / op1
                    quotient = (short)mdl / (short)divisor;
                    remainder = (short)mdl % (short)divisor;
                    break;
                case 0x5B:  // DIVU
                    quotient = mdl / divisor;
                    remainder = mdl % divisor;
                    break;
                case 0x6B:  // DIVL: signed MD / op1
                {
                    var dividend = (int)(((uint)mdh << 16) | mdl);
                    quotient = (long)dividend / (short)divisor;
                    remainder = (long)dividend % (short)divisor;
                    overflow = quotient is < short.MinValue or > short.MaxValue;
                    break;
                }
                default:    // DIVLU
                {
                    var dividend = ((uint)mdh << 16) | mdl;
                    quotient = dividend / divisor;
                    remainder = dividend % divisor;
                    overflow = quotient > 0xFFFF;
                    break;
                }
            }
        }
        if (!overflow)
        {
            mdl = (ushort)quotient;
            mdh = (ushort)remainder;
        }
        mdc |= Mdriu;
        SetFlag(PswFlags.E, false);
        SetFlag(PswFlags.C, false);
        SetFlag(PswFlags.V, overflow);
        SetFlag(PswFlags.Z, mdl == 0);
        SetFlag(PswFlags.N, (mdl & 0x8000) != 0);
    }
}
