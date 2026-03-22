namespace Ms43Sim.Core;

/// <summary>Interrupt entry and PEC transfers at instruction boundaries (UM §5.1-5.4).</summary>
public sealed partial class C167Cpu
{
    const uint PeccBase = 0xFEC0, PecPointerBase = 0xFCE0;

    public InterruptController? Interrupts { get; set; }

    /// <summary>
    /// Serves one interrupt or PEC request if IEN is set, no ATOMIC/EXT* range is active, and the
    /// winner's level is above the CPU's. ILVL 14/15 with a PECCx COUNT ≠ 0 is a PEC transfer;
    /// anything else is an interrupt entry.
    /// </summary>
    bool TryServeInterrupt()
    {
        if (Interrupts is not { } irq || (psw & (ushort)PswFlags.IEN) == 0 || InterruptsLocked)
            return false;
        if (!irq.TryArbitrate(psw >> 12, out var index, out var level, out var group))
            return false;
        if (level >= 14)
        {
            var channel = (level == 15 ? 4 : 0) + group;
            var pecc = sfrs.Peek(PeccBase + 2u * (uint)channel);
            if ((pecc & 0xFF) != 0)
            {
                PecTransfer(irq, index, channel, pecc);
                return true;
            }
        }
        EnterInterrupt(irq, index, level);
        return true;
    }

    void EnterInterrupt(InterruptController irq, int index, int level)
    {
        irq.ClearRequest(index);
        var from = instructionAddress;
        Push(psw);
        if (!SegmentationDisabled)
            Push(csp);
        Push(ip);
        psw = (ushort)((psw & 0x0FFF) | (level << 12));
        csp = 0;
        ip = (ushort)(4 * InterruptSources.All[index].TrapNumber);
        trace.Add(new BranchRecord(cycles(), from, ip, BranchKind.Interrupt));
    }

    /// <summary>
    /// One PEC transfer (UM §5.2): byte or word between two segment-0 addresses held in IRAM at
    /// FCE0+4x (source) and FCE2+4x (destination), no DPPs. COUNT: FF continuous; 02-FE count
    /// down and clear IR; 01 → 00 and IR stays set, so the interrupt follows. A word transfer
    /// with an odd pointer raises ILLOPA.
    /// </summary>
    void PecTransfer(InterruptController irq, int index, int channel, ushort pecc)
    {
        var sourcePointer = PecPointerBase + 4u * (uint)channel;
        var destinationPointer = sourcePointer + 2;
        var source = ReadIramWord(sourcePointer);
        var destination = ReadIramWord(destinationPointer);
        var isByte = (pecc & 0x0100) != 0;
        var increment = (pecc >> 9) & 3;
        if (increment == 3)
            increment = 2;   // the reserved '11' is changed to '10' by hardware

        dataStates += bus.AccessStates(source) + bus.AccessStates(destination);
        if (isByte)
        {
            bus.Write8(destination, bus.Read8(source));
        }
        else
        {
            if (((source | destination) & 1) != 0)
            {
                EnterTrap(TrapKind.IllegalOperandAccess, csp, ip);
                return;
            }
            bus.Write16(destination, bus.Read16(source));
        }

        var step = (ushort)(isByte ? 1 : 2);
        if (increment == 1)
            WriteIramWord(destinationPointer, (ushort)(destination + step));
        else if (increment == 2)
            WriteIramWord(sourcePointer, (ushort)(source + step));

        var count = pecc & 0xFF;
        if (count != 0xFF)
            count--;
        sfrs.Poke(PeccBase + 2u * (uint)channel, (ushort)((pecc & 0xFF00 & ~0x0600) | (increment << 9) | count));
        if (count != 0)
            irq.ClearRequest(index);
    }
}
