namespace Ms43Sim.Core;

public enum TrapKind
{
    Nmi, StackOverflow, StackUnderflow, UndefinedOpcode, ProtectionFault,
    IllegalOperandAccess, IllegalInstructionAccess, IllegalBusAccess,
}

public enum ResetKind { PowerOn, Watchdog, Software }

/// <summary>Something a run did that a report must show. Cycle and Pc say when and where.</summary>
public abstract record SimEvent(ulong Cycle, uint Pc);

public sealed record TrapEvent(ulong Cycle, uint Pc, TrapKind Kind, uint PushedIp, ushort Tfr) : SimEvent(Cycle, Pc);

public sealed record ResetEvent(ulong Cycle, uint Pc, ResetKind Kind) : SimEvent(Cycle, Pc);

/// <summary>The first fetch of an erased word (FFFFh) in a 64 KiB segment since the last reset.</summary>
public sealed record ErasedFlashExecutionEvent(ulong Cycle, uint Pc) : SimEvent(Cycle, Pc);

/// <summary>An instruction fetched from outside flash (RAM, SFRs, a chip select): one per entry into such code (plan 4 R16).</summary>
public sealed record CodeOutsideFlashEvent(ulong Cycle, uint Pc) : SimEvent(Cycle, Pc);

public sealed record FlashWriteEvent(ulong Cycle, uint Pc, uint Address, ushort Value, bool IsByte) : SimEvent(Cycle, Pc);

public sealed record ConfigLockedWriteEvent(ulong Cycle, uint Pc, uint Address, ushort Value) : SimEvent(Cycle, Pc);

public sealed record UnsupportedEvent(ulong Cycle, uint Pc, string What) : SimEvent(Cycle, Pc);

/// <summary>Something the monitoring module saw on its side of the exchange: a wrong answer that is not the seed, a bad frame, an abort.</summary>
public sealed record MonitoringEvent(ulong Cycle, uint Pc, string What) : SimEvent(Cycle, Pc);
