namespace Ms43Sim.Core;

public enum CpuMode { Running, Idle, PowerDown }

/// <summary>
/// The C167CR CPU core. Semantics follow the C166 Family Instruction Set Manual V2.0 (ISM) and
/// the C167CR User's Manual V3.1 (UM). Convention in every handler: compute the result, set the
/// flags, then write the destination — so an explicit write to PSW supersedes the implicit flag
/// update, as UM §4 requires.
/// </summary>
public sealed partial class C167Cpu
{
    const uint IramBase = 0xF600;
    const ushort Mdriu = 0x0010;

    readonly PhysicalBus bus;
    readonly SfrFile sfrs;
    readonly EventLog log;
    readonly BranchTrace trace;
    readonly ICpuHost host;
    readonly Func<ulong> cycles;

    ushort ip, psw, cp, sp, stkov, stkun, mdh, mdl, mdc, syscon, tfr;
    byte csp;
    readonly ushort[] dpp = new ushort[4];
    uint instructionAddress;
    readonly bool[] erasedSegmentSeen = new bool[256];
    bool outsideFlash;
    int dataStates;
    bool branchTaken;

    public C167Cpu(PhysicalBus bus, SfrFile sfrs, EventLog log, BranchTrace trace, ICpuHost host, Func<ulong> cycles)
    {
        this.bus = bus;
        this.sfrs = sfrs;
        this.log = log;
        this.trace = trace;
        this.host = host;
        this.cycles = cycles;
        RegisterSfrs();
        Reset();
    }

    public ushort IP => ip;
    public byte CSP => csp;

    /// <summary>The CPU address CSP:IP of the next instruction.</summary>
    public uint Address => ((uint)csp << 16) | ip;

    /// <summary>The CPU address of the instruction being executed (or last executed).</summary>
    public uint InstructionAddress => instructionAddress;

    /// <summary>PSW. The setter is a debug write: it bypasses the explicit-write rules.</summary>
    public ushort Psw
    {
        get => psw;
        set => psw = value;
    }

    public ushort Cp => cp;
    public ushort Sp => sp;
    public ushort Dpp(int i) => dpp[i];

    public CpuMode Mode { get; private set; }

    /// <summary>
    /// The last <see cref="Step"/> (or <see cref="TryServePecWhileIdle"/>) entered a trap or an
    /// interrupt, or made a PEC transfer, instead of executing an instruction. It is already set while that entry or
    /// transfer accesses memory, so a <see cref="PhysicalBus.DataWatch"/> observer can tell its accesses from the
    /// interrupted instruction's.
    /// </summary>
    public bool LastStepWasEntry { get; private set; }

    /// <summary>EINIT has executed since the last reset.</summary>
    public bool InitDone { get; private set; }

    /// <summary>The CPU part of a reset: registers to their UM reset values, IP = CSP = 0.</summary>
    public void Reset()
    {
        ip = 0;
        csp = 0;
        psw = 0;
        cp = 0xFC00;
        sp = 0xFC00;
        stkov = 0xFA00;
        stkun = 0xFC00;
        for (var i = 0; i < 4; i++)
            dpp[i] = (ushort)i;
        mdh = mdl = mdc = 0;
        syscon = 0;
        tfr = 0;
        bus.XperipheralsEnabled = false;
        Mode = CpuMode.Running;
        InitDone = false;
        instructionAddress = 0;
        Array.Clear(erasedSegmentSeen);
        ResetExtend();
        inServiceA = inServiceB = 0;
        stackLimitHit = false;
    }

    /// <summary>Leaves IDLE mode (an enabled interrupt request ends it, UM §5.1).</summary>
    public void Wake()
    {
        if (Mode == CpuMode.Idle)
            Mode = CpuMode.Running;
    }

    /// <summary>Places the CPU at CSP:IP (tests only).</summary>
    public void Jump(byte newCsp, ushort newIp)
    {
        csp = newCsp;
        ip = newIp;
        instructionAddress = Address;
    }

    /// <summary>
    /// One step (plan Task 8, "What one Step() does"): enter a pending trap, or else execute one
    /// instruction, entering its trap within the same step if it faults. Returns the states.
    /// </summary>
    public int Step()
    {
        LastStepWasEntry = false;
        if (Mode != CpuMode.Running)
            return 2;
        instructionAddress = Address;
        dataStates = 0;
        branchTaken = false;
        if (TryEnterPendingTrap() || TryServeInterrupt())
        {
            LastStepWasEntry = true;
            return FinishStep(Timing.EntryStates + dataStates);
        }

        var w0 = Fetch(ip);
        var region = bus.Decode(instructionAddress);
        if (w0 == 0xFFFF)
            NoteErasedFlash(region);
        NoteOutsideFlash(region);
        var op = (byte)w0;
        var b1 = (byte)(w0 >> 8);
        var size = OpcodeTable.Size(op, b1);
        var w1 = size == 4 ? Fetch((ushort)(ip + 2)) : (ushort)0;
        ip = (ushort)(ip + size);

        var pswBefore = psw;
        var extendBefore = extendCount;
        extendJustSet = false;
        try
        {
            Execute(op, b1, w1);
            if (!extendJustSet && extendBefore > 0 && --extendCount == 0)
                ResetExtend();
        }
        catch (TrapException trap)
        {
            // The faulting instruction is not executed: its flag updates are undone.
            if (trap.Kind != TrapKind.IllegalInstructionAccess)
                psw = pswBefore;
            var classA = trap.Kind is TrapKind.Nmi or TrapKind.StackOverflow or TrapKind.StackUnderflow;
            if (inServiceA == 0 && (classA || inServiceB == 0))
            {
                EnterTrap(trap.Kind, trap.PushedCsp, trap.PushedIp);
                LastStepWasEntry = true;
            }
            else
            {
                tfr |= TrapBit(trap.Kind);
            }
        }

        return FinishStep(Timing.MinStates(op, b1, branchTaken) + FetchStates(region, size) + dataStates);
    }

    /// <summary>
    /// A stack limit crossed during this step is entered now, unless a class A trap is in service
    /// or an ATOMIC/EXT* range is still open (then it stays pending in TFR).
    /// </summary>
    int FinishStep(int states)
    {
        if (stackLimitHit)
        {
            stackLimitHit = false;
            if (inServiceA == 0 && !InterruptsLocked && (tfr & ClassAMask) != 0)
                EnterTrap(HighestClassA(tfr), csp, ip);
        }
        return states;
    }

    /// <summary>ISM §7.2: +4/+6 states from internal RAM, else 1 ACT per instruction word.</summary>
    int FetchStates(Region region, int size)
    {
        if (region is Region.Iram or Region.Sfr or Region.Esfr or Region.Reserved)
            return size == 2 ? 4 : 6;
        return size / 2 * bus.AccessStates(instructionAddress);
    }

    ushort Fetch(ushort at) => bus.Read16(((uint)csp << 16) | at, AccessKind.Fetch);

    void NoteErasedFlash(Region region)
    {
        if (erasedSegmentSeen[csp] || region != Region.Flash)
            return;
        erasedSegmentSeen[csp] = true;
        log.Add((c, pc) => new ErasedFlashExecutionEvent(c, pc));
    }

    /// <summary>One event each time execution enters code outside flash.</summary>
    void NoteOutsideFlash(Region region)
    {
        if (region == Region.Flash)
        {
            outsideFlash = false;
            return;
        }
        if (outsideFlash)
            return;
        outsideFlash = true;
        var at = instructionAddress;
        log.Add((c, _) => new CodeOutsideFlashEvent(c, at));
    }

    void Execute(byte op, byte b1, ushort w1)
    {
        if (OpcodeTable.IsUndefined(op, b1))
            throw new TrapException(TrapKind.UndefinedOpcode, (byte)(instructionAddress >> 16), (ushort)instructionAddress);
        var hi = op >> 4;
        var lo = op & 0xF;
        switch (lo)
        {
            case 0xD:
                ExecuteJump(op, b1, w1);
                return;
            case 0xE:
            case 0xF:
                ExecuteBitSetClear(op, b1);
                return;
            case 0xA when hi < 2:
                ExecuteBitField(op, b1, w1);
                return;
            case 0xA when hi < 8:
                ExecuteBitLogic(op, b1, w1);
                return;
        }
        if (hi < 8)
        {
            if (lo <= 9)
            {
                ExecuteArithmetic(op, b1, w1);
                return;
            }
            if (lo == 0xB && op != 0x3B)
            {
                ExecuteMulDiv(op, b1);
                return;
            }
            if (lo == 0xC)
            {
                ExecuteShift(op, b1);
                return;
            }
        }
        switch (op)
        {
            case 0x81: case 0x91: case 0xA1: case 0xB1:
                ExecuteNegateComplement(op, b1);
                return;
            case 0x80: case 0x90: case 0xA0: case 0xB0: case 0x82: case 0x92: case 0xA2: case 0xB2:
            case 0x86: case 0x96: case 0xA6: case 0xB6:
                ExecuteCompareStep(op, b1, w1);
                return;
            case 0xAC: case 0xBC:
                ExecuteShift(op, b1);
                return;
            case 0xCA: case 0xAB: case 0xBB: case 0xDA: case 0xE2: case 0xCB: case 0xDB: case 0xEB: case 0xFB:
                ExecuteCallReturn(op, b1, w1);
                return;
            case 0xEC: case 0xFC: case 0xC6: case 0xD6:
                ExecuteStack(op, b1, w1);
                return;
            case 0x9B:
                ExecuteTrapInstruction(b1);
                return;
            case 0xA7: case 0xA5: case 0xB5: case 0xB7: case 0x87: case 0x97:
                ExecuteProtected(op, b1, w1);
                return;
            case 0xD1:
                ExecuteAtomicExtr(b1);
                return;
            case 0xD7: case 0xDC:
                ExecuteExtend(op, b1, w1);
                return;
            case 0xCC:  // NOP
                return;
            case 0x8A: case 0x9A: case 0xAA: case 0xBA:
                ExecuteJumpOnBit(op, b1, w1);
                return;
            case 0xEA: case 0x9C: case 0xFA:
                ExecuteJump(op, b1, w1);
                return;
            case 0xF0: case 0xE0: case 0xE6: case 0xF2: case 0xF6: case 0xA8: case 0x98: case 0xB8:
            case 0x88: case 0xC8: case 0xD8: case 0xE8: case 0xD4: case 0xC4: case 0x84: case 0x94:
                ExecuteMoveWord(op, b1, w1);
                return;
            case 0xF1: case 0xE1: case 0xE7: case 0xF3: case 0xF7: case 0xA9: case 0x99: case 0xB9:
            case 0x89: case 0xC9: case 0xD9: case 0xE9: case 0xF4: case 0xE4: case 0xA4: case 0xB4:
                ExecuteMoveByte(op, b1, w1);
                return;
            case 0xC0: case 0xC2: case 0xC5: case 0xD0: case 0xD2: case 0xD5:
                ExecuteMoveExtend(op, b1, w1);
                return;
            default:
                throw new NotImplementedException($"opcode {op:X2}h ({OpcodeTable.Mnemonic(op, b1)})");
        }
    }

    // ---- GPRs: the register bank lives in IRAM at CP ----

    public ushort R(int n) => ReadIramWord((uint)(cp + 2 * n) & 0xFFFF);

    public void SetR(int n, ushort value) => WriteIramWord((uint)(cp + 2 * n) & 0xFFFF, value);

    /// <summary>Byte register i: RL0, RH0, RL1, … (UM Table 22-2).</summary>
    public byte Rb(int i)
    {
        var a = (uint)(cp + i) & 0xFFFF;
        var o = a - IramBase;
        return o < (uint)bus.Iram.Length ? bus.Iram[o] : bus.Read8(a);
    }

    /// <summary>Writing a GPR byte leaves the other byte alone (UM Table 22-2 note).</summary>
    public void SetRb(int i, byte value)
    {
        var a = (uint)(cp + i) & 0xFFFF;
        var o = a - IramBase;
        if (o < (uint)bus.Iram.Length)
            bus.Iram[o] = value;
        else
            bus.Write8(a, value);
    }

    ushort ReadIramWord(uint a)
    {
        var o = a - IramBase;
        return o < (uint)bus.Iram.Length - 1 ? (ushort)(bus.Iram[o] | (bus.Iram[o + 1] << 8)) : bus.Read16(a);
    }

    void WriteIramWord(uint a, ushort value)
    {
        var o = a - IramBase;
        if (o < (uint)bus.Iram.Length - 1)
        {
            bus.Iram[o] = (byte)value;
            bus.Iram[o + 1] = (byte)(value >> 8);
        }
        else
        {
            bus.Write16(a, value);
        }
    }

    // ---- flags ----

    bool Flag(PswFlags f) => (psw & (ushort)f) != 0;

    void SetFlag(PswFlags f, bool on)
    {
        if (on)
            psw |= (ushort)f;
        else
            psw &= (ushort)~f;
    }

    /// <summary>MOV/MOVB/PUSH/POP: E, Z, N from the moved value; V and C unchanged (ISM ch. 5).</summary>
    void MoveFlags(ushort value, bool isByte)
    {
        var msb = isByte ? 0x80 : 0x8000;
        SetFlag(PswFlags.E, value == msb);
        SetFlag(PswFlags.Z, value == 0);
        SetFlag(PswFlags.N, (value & msb) != 0);
    }

    // ---- the CPU's own SFRs ----

    void RegisterSfrs()
    {
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            sfrs.Register(0xFE00 + 2u * (uint)i, "cpu", () => dpp[index], v => dpp[index] = (ushort)(v & 0x03FF), () => dpp[index]);
        }
        sfrs.Register(0xFE08, "cpu", () => csp, _ => { }, () => csp);                       // CSP: not directly writable
        sfrs.Register(0xFE0C, "cpu", () => mdh, v => { mdh = v; mdc |= Mdriu; }, () => mdh);
        sfrs.Register(0xFE0E, "cpu", () => { mdc &= unchecked((ushort)~Mdriu); return mdl; },
            v => { mdl = v; mdc |= Mdriu; }, () => mdl);                                     // reading MDL clears MDRIU
        sfrs.Register(0xFE10, "cpu", () => cp, v => cp = (ushort)(v & 0xFFFE), () => cp);
        sfrs.Register(0xFE12, "cpu", () => sp, v => WriteSp((ushort)(v & 0xFFFE)), () => sp);
        sfrs.Register(0xFE14, "cpu", () => stkov, v => stkov = (ushort)(v & 0xFFFE), () => stkov);
        sfrs.Register(0xFE16, "cpu", () => stkun, v => stkun = (ushort)(v & 0xFFFE), () => stkun);
        sfrs.Register(0xFF0E, "cpu", () => mdc, v => mdc = v, () => mdc);
        sfrs.Register(0xFF10, "cpu", () => psw, v => psw = v, () => psw);
        sfrs.Register(0xFF12, "cpu", () => syscon, WriteSyscon, () => syscon);
        sfrs.Register(0xFF1C, "cpu", () => 0x0000, _ => { }, () => 0x0000);                  // ZEROS
        sfrs.Register(0xFF1E, "cpu", () => 0xFFFF, _ => { }, () => 0xFFFF);                  // ONES
        sfrs.Register(0xFFAC, "cpu", () => tfr, v => tfr = v, () => tfr);
    }

    /// <summary>SYSCON cannot be changed after EINIT (UM §4); BUSCONx and ADDRSELx stay writable.</summary>
    void WriteSyscon(ushort value)
    {
        if (InitDone)
        {
            log.Add((c, pc) => new ConfigLockedWriteEvent(c, pc, 0xFF12, value));
            return;
        }
        syscon = value;
        bus.XperipheralsEnabled = (value & 0x0004) != 0;   // XPEN
    }

    /// <summary>An explicit write to SP is checked against STKOV/STKUN like a push or pop.</summary>
    void WriteSp(ushort value) => ChangeSp(value);
}
