namespace Ms43Sim.Core;

/// <summary>
/// Stack, SCXT, TRAP, hardware traps, protected instructions and the ATOMIC/EXT* ranges
/// (ISM ch. 5-6; UM §5).
/// </summary>
public sealed partial class C167Cpu
{
    const ushort ClassAMask = 0xE000;   // NMI, STKOF, STKUF
    const ushort ClassBMask = 0x008F;   // UNDOPC, PRTFLT, ILLOPA, ILLINA, ILLBUS

    int inServiceA, inServiceB;
    bool stackLimitHit;
    int extendCount;
    bool extendJustSet;

    /// <summary>Interrupts and PEC transfers are held off inside an ATOMIC or EXT* range (UM §5.3).</summary>
    public bool InterruptsLocked => extendCount > 0;

    bool SegmentationDisabled => (syscon & 0x0800) != 0;   // SYSCON.SGTDIS

    sealed class TrapException : Exception
    {
        public TrapException(TrapKind kind, byte pushedCsp, ushort pushedIp)
        {
            Kind = kind;
            PushedCsp = pushedCsp;
            PushedIp = pushedIp;
        }

        public TrapKind Kind { get; }
        public byte PushedCsp { get; }
        public ushort PushedIp { get; }
    }

    // ---- the system stack: SP addresses IRAM directly (UM §3), never through the DPPs ----

    void Push(ushort value)
    {
        var newSp = (ushort)(sp - 2);
        WriteIramWord(newSp, value);
        ChangeSp(newSp);
    }

    ushort Pop()
    {
        var value = ReadIramWord(sp);
        ChangeSp((ushort)(sp + 2));
        return value;
    }

    /// <summary>STKOF when SP is decremented below STKOV, STKUF when incremented above STKUN (UM §5).</summary>
    void ChangeSp(ushort newSp)
    {
        var old = sp;
        sp = newSp;
        if (newSp < old && newSp < stkov)
        {
            tfr |= TrapBit(TrapKind.StackOverflow);
            stackLimitHit = true;
        }
        else if (newSp > old && newSp > stkun)
        {
            tfr |= TrapBit(TrapKind.StackUnderflow);
            stackLimitHit = true;
        }
    }

    void ExecuteStack(byte op, byte b1, ushort w1)
    {
        switch (op)
        {
            case 0xEC:  // PUSH reg: E, Z, N from the pushed word
            {
                var value = ReadReg(b1);
                MoveFlags(value, false);
                Push(value);
                return;
            }
            case 0xFC:  // POP reg
            {
                var value = Pop();
                MoveFlags(value, false);
                WriteReg(b1, value);
                return;
            }
            case 0xC6:  // SCXT reg, #data16 (flags unaffected)
                Push(ReadReg(b1));
                WriteReg(b1, w1);
                return;
            default:    // D6: SCXT reg, mem
            {
                var value = ReadWord(w1);
                Push(ReadReg(b1));
                WriteReg(b1, value);
                return;
            }
        }
    }

    /// <summary>TRAP #trap7: like an interrupt entry, but ILVL is left as it is (UM §5.1).</summary>
    void ExecuteTrapInstruction(byte b1)
    {
        Push(psw);
        if (!SegmentationDisabled)
            Push(csp);
        Push(ip);
        var vector = (ushort)((b1 >> 1) * 4);
        trace.Add(new BranchRecord(cycles(), instructionAddress, vector, BranchKind.Trap));
        csp = 0;
        ip = vector;
    }

    void ExecuteReti()
    {
        var target = Pop();
        var targetCsp = SegmentationDisabled ? csp : (byte)Pop();
        psw = Pop();
        if (inServiceA > 0)
            inServiceA--;
        else if (inServiceB > 0)
            inServiceB--;
        BranchTo(targetCsp, target, BranchKind.Reti);
    }

    // ---- hardware traps ----

    static ushort TrapBit(TrapKind kind) => kind switch
    {
        TrapKind.Nmi => 0x8000,
        TrapKind.StackOverflow => 0x4000,
        TrapKind.StackUnderflow => 0x2000,
        TrapKind.UndefinedOpcode => 0x0080,
        TrapKind.ProtectionFault => 0x0008,
        TrapKind.IllegalOperandAccess => 0x0004,
        TrapKind.IllegalInstructionAccess => 0x0002,
        _ => 0x0001,
    };

    static ushort TrapVector(TrapKind kind) => kind switch
    {
        TrapKind.Nmi => 0x08,
        TrapKind.StackOverflow => 0x10,
        TrapKind.StackUnderflow => 0x18,
        _ => 0x28,
    };

    static TrapKind HighestClassA(ushort flags) =>
        (flags & 0x8000) != 0 ? TrapKind.Nmi : (flags & 0x4000) != 0 ? TrapKind.StackOverflow : TrapKind.StackUnderflow;

    static TrapKind HighestClassB(ushort flags) =>
        (flags & 0x0080) != 0 ? TrapKind.UndefinedOpcode
        : (flags & 0x0008) != 0 ? TrapKind.ProtectionFault
        : (flags & 0x0004) != 0 ? TrapKind.IllegalOperandAccess
        : (flags & 0x0002) != 0 ? TrapKind.IllegalInstructionAccess
        : TrapKind.IllegalBusAccess;

    /// <summary>
    /// A trap whose flag is still (or newly) set, entered at an instruction boundary as its own step.
    /// Class A waits for the end of an ATOMIC/EXT* range; class B does not, and its entry ends the
    /// range (UM §21.8, ISM p. 39).
    /// </summary>
    bool TryEnterPendingTrap()
    {
        if (tfr == 0)
            return false;
        if ((tfr & ClassAMask) != 0 && inServiceA == 0 && !InterruptsLocked)
        {
            EnterTrap(HighestClassA(tfr), csp, ip);
            return true;
        }
        if ((tfr & ClassBMask) != 0 && inServiceA == 0 && inServiceB == 0)
        {
            EnterTrap(HighestClassB(tfr), csp, ip);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Push PSW, CSP (unless SGTDIS) and the given IP; ILVL = 15; CSP = 0; IP = vector. The
    /// in-service counter goes up before the pushes, so a stack overflow caused by the entry
    /// itself only sets its flag. A class B trap ends any ATOMIC/EXT* range (UM §21.8); class A
    /// traps are only entered outside one.
    /// </summary>
    void EnterTrap(TrapKind kind, byte pushedCsp, ushort pushedIp)
    {
        var bit = TrapBit(kind);
        tfr |= bit;
        if ((bit & ClassAMask) != 0)
            inServiceA++;
        else
            inServiceB++;
        ResetExtend();
        var from = instructionAddress;
        Push(psw);
        if (!SegmentationDisabled)
            Push(pushedCsp);
        Push(pushedIp);
        psw = (ushort)((psw & 0x0FFF) | 0xF000);
        csp = 0;
        ip = TrapVector(kind);
        var flags = tfr;
        log.Add((c, pc) => new TrapEvent(c, pc, kind, pushedIp, flags));
        trace.Add(new BranchRecord(cycles(), from, ip, BranchKind.Trap));
    }

    // ---- protected instructions: op, ~op, op, op (UM §5.4 "Protection Fault Trap") ----

    void ExecuteProtected(byte op, byte b1, ushort w1)
    {
        if (b1 != (byte)~op || (byte)w1 != op || (byte)(w1 >> 8) != op)
            throw new TrapException(TrapKind.ProtectionFault, (byte)(instructionAddress >> 16), (ushort)instructionAddress);
        switch (op)
        {
            case 0xA7: host.Srvwdt(); return;
            case 0xA5: host.Diswdt(); return;
            case 0xB5: InitDone = true; host.Einit(); return;
            case 0xB7: host.SoftwareReset(); return;
            case 0x87: Mode = CpuMode.Idle; return;
            default: Mode = CpuMode.PowerDown; host.PowerDown(); return;   // 0x97 PWRDN
        }
    }

    // ---- ATOMIC / EXTR / EXTS / EXTP / EXTSR / EXTPR ----

    /// <summary>D1: ATOMIC (sub-op 0) or EXTR (sub-op 2) for the next 1-4 instructions.</summary>
    void ExecuteAtomicExtr(byte b1) =>
        BeginExtend(ExtendKind.None, 0, extr: b1 >> 6 == 2, range: ((b1 >> 4) & 3) + 1);

    /// <summary>D7 (#seg / #pag forms) and DC (Rw forms): EXTS, EXTP, EXTSR, EXTPR.</summary>
    void ExecuteExtend(byte op, byte b1, ushort w1)
    {
        var sub = b1 >> 6;
        var page = (sub & 1) == 1;
        uint value = op == 0xD7
            ? (page ? w1 & 0x03FFu : (uint)(byte)w1)
            : (page ? R(b1 & 0xF) & 0x03FFu : R(b1 & 0xF) & 0x00FFu);
        BeginExtend(page ? ExtendKind.Page : ExtendKind.Segment, value, extr: sub >= 2, range: ((b1 >> 4) & 3) + 1);
    }

    /// <summary>
    /// One counter for every ATOMIC/EXT* sequence: a new one inside a running range reloads it
    /// (ISM p. 39) and also replaces the override [assumption: the ISM does not say].
    /// </summary>
    void BeginExtend(ExtendKind kind, uint value, bool extr, int range)
    {
        extendKind = kind;
        extendValue = value;
        extrActive = extr;
        extendCount = range;
        extendJustSet = true;
    }
}
