namespace Ms43Sim.Core;

/// <summary>Addressing modes (ISM ch. 6).</summary>
public sealed partial class C167Cpu
{
    enum ExtendKind { None, Page, Segment }

    // The EXTP/EXTS/EXTR override state (Task 11 fills in the instructions that set it).
    ExtendKind extendKind;
    uint extendValue;
    bool extrActive;

    void ResetExtend()
    {
        extendKind = ExtendKind.None;
        extendValue = 0;
        extrActive = false;
        extendCount = 0;
    }

    /// <summary>Short 'reg' (00-EF: SFR, or ESFR under EXTR; F0-FF: GPR) — physical SFR address.</summary>
    uint RegAddress(byte reg) => (extrActive ? 0xF000u : 0xFE00u) + 2u * reg;

    ushort ReadReg(byte reg) => reg >= 0xF0 ? R(reg & 0xF) : sfrs.Read16(RegAddress(reg));

    void WriteReg(byte reg, ushort value)
    {
        if (reg >= 0xF0)
            SetR(reg & 0xF, value);
        else
            sfrs.Write16(RegAddress(reg), value);
    }

    /// <summary>A byte operation on an SFR reaches its low byte only (ISM §6.1).</summary>
    byte ReadRegByte(byte reg) => reg >= 0xF0 ? Rb(reg & 0xF) : sfrs.Read8(RegAddress(reg));

    void WriteRegByte(byte reg, byte value)
    {
        if (reg >= 0xF0)
            SetRb(reg & 0xF, value);
        else
            sfrs.Write8(RegAddress(reg), value);
    }

    /// <summary>
    /// Long and indirect data addresses: DPPn (bits 9:0) || 14-bit page offset, unless an
    /// EXTP (page || offset) or EXTS (segment || 16-bit address) range is active (ISM §6.2-6.4).
    /// </summary>
    uint DataAddress(ushort logical) => extendKind switch
    {
        ExtendKind.Page => (extendValue << 14) | (logical & 0x3FFFu),
        ExtendKind.Segment => (extendValue << 16) | logical,
        _ => ((uint)(dpp[logical >> 14] & 0x03FF) << 14) | (logical & 0x3FFFu),
    };

    ushort ReadWord(ushort logical)
    {
        CheckWordAlignment(logical);
        return bus.Read16(Counted(DataAddress(logical)));
    }

    void WriteWord(ushort logical, ushort value)
    {
        CheckWordAlignment(logical);
        bus.Write16(Counted(DataAddress(logical)), value);
    }

    byte ReadByte(ushort logical) => bus.Read8(Counted(DataAddress(logical)));

    void WriteByte(ushort logical, byte value) => bus.Write8(Counted(DataAddress(logical)), value);

    /// <summary>Adds the bus states of one data access (ISM §7.3: 1 ACT for an external operand).</summary>
    uint Counted(uint physical)
    {
        dataStates += bus.AccessStates(physical);
        return physical;
    }

    /// <summary>Word operands at odd addresses are not executed (ISM §6.2): ILLOPA, pushed IP = the next instruction.</summary>
    void CheckWordAlignment(ushort logical)
    {
        if ((logical & 1) != 0)
            throw new TrapException(TrapKind.IllegalOperandAccess, csp, ip);
    }
}
