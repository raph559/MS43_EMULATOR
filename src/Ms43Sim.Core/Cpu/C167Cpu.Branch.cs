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
        csp = newCsp;
        ip = target;
    }

    /// <summary>Task 11 turns this into the ILLINA trap (pushed IP = the odd target).</summary>
    void IllegalInstructionAccess(byte targetCsp, ushort target) =>
        throw new NotImplementedException($"branch to odd address {targetCsp:X2}:{target:X4} (ILLINA arrives in Task 11)");

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
