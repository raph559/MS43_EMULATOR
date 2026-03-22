namespace Ms43Sim.Core;

/// <summary>Jumps (ISM §6.7). Code targets never go through the DPPs.</summary>
public sealed partial class C167Cpu
{
    /// <summary>Every taken branch goes through here: odd targets trap, and the branch is traced.</summary>
    void BranchTo(byte newCsp, ushort target, BranchKind kind)
    {
        if ((target & 1) != 0)
            IllegalInstructionAccess(newCsp, target);
        trace.Add(new BranchRecord(cycles(), instructionAddress, ((uint)newCsp << 16) | target, kind));
        branchTaken = true;
        csp = newCsp;
        ip = target;
    }

    /// <summary>A branch to an odd address: the ILLINA trap, pushed IP = the odd target (UM §5.4).</summary>
    void IllegalInstructionAccess(byte targetCsp, ushort target) =>
        throw new TrapException(TrapKind.IllegalInstructionAccess, targetCsp, target);

    /// <summary>Calls push the return address (and CSP for CALLS, the register for PCALL); returns pop them.</summary>
    void ExecuteCallReturn(byte op, byte b1, ushort w1)
    {
        switch (op)
        {
            case 0xCA:  // CALLA cc, caddr
                if (Conditions.Holds(b1 >> 4, psw))
                {
                    Push(ip);
                    BranchTo(csp, w1, BranchKind.Call);
                }
                return;
            case 0xAB:  // CALLI cc, [Rw]
                if (Conditions.Holds(b1 >> 4, psw))
                {
                    var target = R(b1 & 0xF);
                    Push(ip);
                    BranchTo(csp, target, BranchKind.Call);
                }
                return;
            case 0xBB:  // CALLR rel
                Push(ip);
                BranchTo(csp, (ushort)(ip + 2 * (sbyte)b1), BranchKind.Call);
                return;
            case 0xDA:  // CALLS seg, caddr
                Push(csp);
                Push(ip);
                BranchTo(b1, w1, BranchKind.Call);
                return;
            case 0xE2:  // PCALL reg, caddr: E, Z, N from the pushed register
            {
                var value = ReadReg(b1);
                MoveFlags(value, false);
                Push(value);
                Push(ip);
                BranchTo(csp, w1, BranchKind.Call);
                return;
            }
            case 0xCB:  // RET
                BranchTo(csp, Pop(), BranchKind.Return);
                return;
            case 0xDB:  // RETS
            {
                var target = Pop();
                var targetCsp = (byte)Pop();
                BranchTo(targetCsp, target, BranchKind.Return);
                return;
            }
            case 0xEB:  // RETP reg: E, Z, N from the popped register value
            {
                var target = Pop();
                var value = Pop();
                MoveFlags(value, false);
                WriteReg(b1, value);
                BranchTo(csp, target, BranchKind.Return);
                return;
            }
            default:    // 0xFB RETI
                ExecuteReti();
                return;
        }
    }

    void ExecuteJump(byte op, byte b1, ushort w1)
    {
        switch (op)
        {
            case 0xEA:  // JMPA cc, caddr
                if (Conditions.Holds(b1 >> 4, psw))
                    BranchTo(csp, w1, BranchKind.Jump);
                return;
            case 0x9C:  // JMPI cc, [Rw]
                if (Conditions.Holds(b1 >> 4, psw))
                    BranchTo(csp, R(b1 & 0xF), BranchKind.Jump);
                return;
            case 0xFA:  // JMPS seg, caddr
                BranchTo(b1, w1, BranchKind.Jump);
                return;
            default:    // cD: JMPR cc, rel
                if (Conditions.Holds(op >> 4, psw))
                    BranchTo(csp, (ushort)(ip + 2 * (sbyte)b1), BranchKind.Jump);
                return;
        }
    }
}
