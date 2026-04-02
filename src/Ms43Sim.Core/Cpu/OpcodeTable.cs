namespace Ms43Sim.Core;

/// <summary>
/// Size and mnemonic of every C167 instruction, keyed by its first byte (and, for D1/D7/DC, the
/// sub-operation in bits 7:6 of the second byte). This is the only source of instruction sizes:
/// the CPU advances IP with it. The table mirrors tools/c167dis.py, which reassembles the whole
/// program area byte-identically through AS (REPORT.md §9.2).
/// </summary>
public static class OpcodeTable
{
    public const string Undefined = "db?";

    static readonly string[] Arith = { "ADD", "ADDC", "SUB", "SUBC", "CMP", "XOR", "AND", "OR" };
    static readonly string?[] MulDiv = { "MUL", "MULU", "PRIOR", null, "DIV", "DIVU", "DIVL", "DIVLU" };
    static readonly string[] Shift = { "ROL", "ROL", "ROR", "ROR", "SHL", "SHL", "SHR", "SHR" };
    static readonly string[] BitLogic = { "BFLDL", "BFLDH", "BCMP", "BMOVN", "BMOV", "BOR", "BAND", "BXOR" };
    static readonly string[] AtomicExtr = { "ATOMIC", "?", "EXTR", "?" };
    static readonly string[] Extend = { "EXTS", "EXTP", "EXTSR", "EXTPR" };

    static readonly string?[] mnemonic = new string?[256];
    static readonly byte[] size = new byte[256];

    static OpcodeTable()
    {
        for (var op = 0; op < 256; op++)
        {
            var hi = op >> 4;
            var lo = op & 0xF;
            if (hi < 8 && lo <= 9)
                Set(op, Arith[hi] + ((lo & 1) != 0 ? "B" : ""), lo is 0 or 1 or 8 or 9 ? 2 : 4);
            else if (lo == 0xD)
                Set(op, "JMPR", 2);
            else if (lo == 0xE)
                Set(op, "BCLR", 2);
            else if (lo == 0xF)
                Set(op, "BSET", 2);
            else if (lo == 0xA && hi < 8)
                Set(op, BitLogic[hi], 4);
            else if (lo == 0xB && hi < 8)
                Set(op, MulDiv[hi], 2);
            else if (lo == 0xC && hi < 8)
                Set(op, Shift[hi], 2);
        }

        foreach (var (op, name, length) in Upper)
            Set(op, name, length);
    }

    /// <summary>The upper half (rows 8-F, columns 0-C) of tools/c167dis.py's table.</summary>
    static readonly (int Op, string Name, int Size)[] Upper =
    {
        (0x80, "CMPI1", 2), (0x90, "CMPI2", 2), (0xA0, "CMPD1", 2), (0xB0, "CMPD2", 2),
        (0xC0, "MOVBZ", 2), (0xD0, "MOVBS", 2), (0xE0, "MOV", 2), (0xF0, "MOV", 2),
        (0x81, "NEG", 2), (0x91, "CPL", 2), (0xA1, "NEGB", 2), (0xB1, "CPLB", 2),
        (0xD1, "ATOMIC", 2), (0xE1, "MOVB", 2), (0xF1, "MOVB", 2),
        (0x82, "CMPI1", 4), (0x92, "CMPI2", 4), (0xA2, "CMPD1", 4), (0xB2, "CMPD2", 4),
        (0xC2, "MOVBZ", 4), (0xD2, "MOVBS", 4), (0xE2, "PCALL", 4), (0xF2, "MOV", 4), (0xF3, "MOVB", 4),
        (0x84, "MOV", 4), (0x94, "MOV", 4), (0xA4, "MOVB", 4), (0xB4, "MOVB", 4),
        (0xC4, "MOV", 4), (0xD4, "MOV", 4), (0xE4, "MOVB", 4), (0xF4, "MOVB", 4),
        (0xA5, "DISWDT", 4), (0xB5, "EINIT", 4), (0xC5, "MOVBZ", 4), (0xD5, "MOVBS", 4),
        (0x86, "CMPI1", 4), (0x96, "CMPI2", 4), (0xA6, "CMPD1", 4), (0xB6, "CMPD2", 4),
        (0xC6, "SCXT", 4), (0xD6, "SCXT", 4), (0xE6, "MOV", 4), (0xF6, "MOV", 4),
        (0x87, "IDLE", 4), (0x97, "PWRDN", 4), (0xA7, "SRVWDT", 4), (0xB7, "SRST", 4),
        (0xD7, "EXTS", 4), (0xE7, "MOVB", 4), (0xF7, "MOVB", 4),
        (0x88, "MOV", 2), (0x89, "MOVB", 2), (0x98, "MOV", 2), (0x99, "MOVB", 2),
        (0xA8, "MOV", 2), (0xA9, "MOVB", 2), (0xB8, "MOV", 2), (0xB9, "MOVB", 2),
        (0xC8, "MOV", 2), (0xC9, "MOVB", 2), (0xD8, "MOV", 2), (0xD9, "MOVB", 2),
        (0xE8, "MOV", 2), (0xE9, "MOVB", 2),
        (0x8A, "JB", 4), (0x9A, "JNB", 4), (0xAA, "JBC", 4), (0xBA, "JNBS", 4),
        (0xCA, "CALLA", 4), (0xDA, "CALLS", 4), (0xEA, "JMPA", 4), (0xFA, "JMPS", 4),
        (0x9B, "TRAP", 2), (0xAB, "CALLI", 2), (0xBB, "CALLR", 2), (0xCB, "RET", 2),
        (0xDB, "RETS", 2), (0xEB, "RETP", 2), (0xFB, "RETI", 2),
        (0x9C, "JMPI", 2), (0xAC, "ASHR", 2), (0xBC, "ASHR", 2), (0xCC, "NOP", 2),
        (0xDC, "EXTS", 2), (0xEC, "PUSH", 2), (0xFC, "POP", 2),
    };

    /// <summary>2 or 4. An undefined opcode counts as 2 bytes, as the sweep in tools/xref.py does.</summary>
    public static int Size(byte op, byte b1) => mnemonic[op] == null ? 2 : size[op];

    public static string Mnemonic(byte op, byte b1) => op switch
    {
        _ when mnemonic[op] == null => Undefined,
        0xD1 => AtomicExtr[b1 >> 6],
        0xD7 or 0xDC => Extend[b1 >> 6],
        _ => mnemonic[op]!,
    };

    /// <summary>
    /// True for opcodes the C167 does not define: the gaps of the table, the reserved ATOMIC/EXTR
    /// sub-operations, and 44h/45h. tools/c167dis.py lists 44h/45h as "CMP mem,reg", but the ISM
    /// has no such form (CMP stores nothing), so they keep that spelling in listings and raise
    /// UNDOPC when executed.
    /// </summary>
    public static bool IsUndefined(byte op, byte b1) =>
        mnemonic[op] == null || op is 0x44 or 0x45 || (op == 0xD1 && (b1 >> 6) is 1 or 3);

    static void Set(int op, string? name, int length)
    {
        mnemonic[op] = name;
        size[op] = (byte)length;
    }
}
