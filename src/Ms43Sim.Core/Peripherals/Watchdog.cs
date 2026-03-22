namespace Ms43Sim.Core;

/// <summary>
/// The watchdog timer (UM §13): a 16-bit up-counter at fCPU/2 (WDTIN = 0) or fCPU/128
/// (WDTIN = 1). It starts at 0000h after every reset; SRVWDT clears the low byte, loads WDTREL
/// into the high byte and clears WDTR; an overflow resets the chip and sets WDTR. DISWDT works
/// only before the first EINIT or SRVWDT. The count is computed from the clock when read; the
/// overflow is one scheduled event.
/// WDTCON: bit 0 WDTIN, bit 1 WDTR, bit 2 SWR, bit 3 SHWR, bit 4 LHWR, bits 15:8 WDTREL.
/// </summary>
public sealed class Watchdog
{
    const uint WdtAddress = 0xFEAE, WdtconAddress = 0xFFAE;
    const ushort Wdtin = 0x0001, Wdtr = 0x0002, Swr = 0x0004, ResetFlags = 0x001E;

    readonly Scheduler scheduler;
    readonly Action<ResetKind> reset;
    readonly Func<bool> initDone;

    ushort wdtcon;
    ushort baseCount;
    ulong baseCycle;
    bool serviced;
    long overflowEvent = -1;

    public Watchdog(Scheduler scheduler, SfrFile sfrs, Action<ResetKind> reset, Func<bool> initDone)
    {
        this.scheduler = scheduler;
        this.reset = reset;
        this.initDone = initDone;
        sfrs.Register(WdtAddress, "watchdog", () => Count, _ => { }, () => Count);   // WDT is read-only
        sfrs.Register(WdtconAddress, "watchdog", () => wdtcon, WriteWdtcon, () => wdtcon);
        OnReset(ResetKind.PowerOn);
    }

    public bool Enabled { get; private set; }

    public int ServiceCount { get; private set; }

    public ushort Count => Enabled
        ? (ushort)Math.Min(0xFFFFul, baseCount + (scheduler.Now - baseCycle) / (ulong)Prescale)
        : baseCount;

    int Prescale => (wdtcon & Wdtin) != 0 ? 128 : 2;

    /// <summary>SRVWDT.</summary>
    public void Service()
    {
        ServiceCount++;
        serviced = true;
        wdtcon &= unchecked((ushort)~Wdtr);
        Rebase((ushort)(wdtcon & 0xFF00));
    }

    /// <summary>DISWDT: refused after EINIT or the first SRVWDT.</summary>
    public bool Disable()
    {
        if (initDone() || serviced)
            return false;
        Rebase(Count);
        Enabled = false;
        Cancel();
        return true;
    }

    /// <summary>EINIT clears SWR, SHWR and LHWR. [assumption] It leaves WDTR: the boot reads WDTR after its EINIT.</summary>
    public void OnEinit() => wdtcon &= unchecked((ushort)~(Swr | 0x0018));

    /// <summary>
    /// After any reset the watchdog runs from 0000h at fCPU/2 with WDTREL = 0. The source flag is
    /// set: WDTR for a watchdog reset, SWR for SRST. [assumption] Power-on sets no flag.
    /// </summary>
    public void OnReset(ResetKind kind)
    {
        wdtcon = kind switch { ResetKind.Watchdog => Wdtr, ResetKind.Software => Swr, _ => 0 };
        Enabled = true;
        serviced = false;
        Rebase(0);
    }

    void WriteWdtcon(ushort value)
    {
        var current = Count;
        wdtcon = (ushort)((value & (0xFF00 | Wdtin)) | (wdtcon & ResetFlags));   // the reset flags are read-only
        Rebase(current);
    }

    void Rebase(ushort count)
    {
        baseCount = count;
        baseCycle = scheduler.Now;
        Cancel();
        if (Enabled)
            overflowEvent = scheduler.Schedule(baseCycle + (0x10000ul - count) * (ulong)Prescale, "watchdog overflow", Overflow);
    }

    void Cancel()
    {
        if (overflowEvent >= 0)
            scheduler.Cancel(overflowEvent);
        overflowEvent = -1;
    }

    void Overflow()
    {
        overflowEvent = -1;
        reset(ResetKind.Watchdog);
    }
}
