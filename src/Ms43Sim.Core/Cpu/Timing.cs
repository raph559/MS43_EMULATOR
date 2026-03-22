namespace Ms43Sim.Core;

/// <summary>
/// Minimum instruction state times, ISM §7.2 Table 11. A step's total adds the fetch cost
/// (1 ACT per instruction word from external memory, or +4/+6 states from IRAM) and 1 ACT per
/// external data operand. [assumption] The second-order effects of ISM §7.3 (IRAM read after an
/// auto-increment, PSW-write stalls, misaligned jump targets) are not modelled.
/// </summary>
public static class Timing
{
    /// <summary>[assumption] An interrupt, PEC or trap entry costs as much as TRAP.</summary>
    public const int EntryStates = 4;

    public static int MinStates(byte op, byte b1, bool taken)
    {
        if ((op & 0xF) == 0xD)                          // JMPR
            return taken ? 4 : 2;
        return op switch
        {
            0xCA or 0xAB => taken ? 4 : 2,              // CALLA, CALLI
            0xDA or 0xBB or 0xE2 or 0xFA => 4,          // CALLS, CALLR, PCALL, JMPS
            0x8A or 0x9A or 0xAA or 0xBA => taken ? 4 : 2,   // JB, JNB, JBC, JNBS
            0xEA or 0x9C => taken ? 4 : 2,              // JMPA, JMPI
            0x0B or 0x1B => 10,                         // MUL, MULU
            0x4B or 0x5B or 0x6B or 0x7B => 20,         // DIV, DIVU, DIVL, DIVLU
            0xCB or 0xDB or 0xEB or 0xFB => 4,          // RET, RETS, RETP, RETI
            0xD4 or 0xF4 => 4,                          // MOV[B] Rn, [Rm+#data16]
            0x9B => 4,                                  // TRAP
            _ => 2,
        };
    }
}
