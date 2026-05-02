namespace Ms43Sim.Core;

/// <summary>
/// The high-speed synchronous serial interface (UM §12).
/// SSCCON has two views, chosen by SSCEN before the access: programming (bits 3-0 SSCBM,
/// 4 SSCHB, 5 SSCPH, 6 SSCPO, 8-11 the error enables, 12 SSCAREN) and operating (bits 3-0 SSCBC,
/// 8 SSCTE, 9 SSCRE, 10 SSCPE, 11 SSCBE, 12 SSCBSY); 14 SSCMS and 15 SSCEN are in both.
/// Master: a write to SSCTB starts a transfer at once if none is active (SSCTIR); one bit takes
/// 2 × (SSCBR + 1) cycles; the end is one scheduled event, which plays the clock edges to the
/// selected slaves and moves the received bits to SSCRB (SSCRIR). A reloaded SSCTB follows with
/// no gap. Slave: an external master clocks one character at a time through
/// <see cref="ExternalTransfer"/>.
/// [assumption] No start latency; phase and baud-rate errors are never raised; clearing SSCEN
/// aborts a transfer without SSCRIR; a write to SSCTB while disabled is only stored; a write in
/// programming mode initialises the SSC, so an unread SSCRB is forgotten (UM p. 12-4: such a
/// write "will initialize the SSC"; without it job 0 fails after every job 3, plan 3).
/// </summary>
public sealed class Ssc : ISscPort
{
    const uint ConAddress = 0xFFB2, TbAddress = 0xF0B0, RbAddress = 0xF0B2, BrAddress = 0xF0B4;
    const ushort ControlBits = 0x1F7F, Hb = 0x0010, Ph = 0x0020, Po = 0x0040, Ten = 0x0100, Ren = 0x0200;
    const ushort Te = 0x0100, Re = 0x0200, FlagBits = 0x0F00, Bsy = 0x1000, Ms = 0x4000, En = 0x8000;

    readonly Scheduler scheduler;
    readonly InterruptController interrupts;
    readonly EventLog log;
    readonly SscBus bus;
    readonly InterruptSource transmit = InterruptSources.ByName("SSCT");
    readonly InterruptSource receive = InterruptSources.ByName("SSCR");
    readonly InterruptSource error = InterruptSources.ByName("SSCE");

    ushort control, flags, tb, rb, br, shift;
    bool master, enabled;
    bool tbFull;        // SSCTB holds data not yet moved to the shift register
    bool shiftLoaded;   // slave: the shift register holds new data for the next character
    bool rbUnread;
    bool mtsr;          // the level on MTSR
    bool busy;          // master: a transfer is running
    ulong transferStart;
    long completionEvent = -1;

    public Ssc(Scheduler scheduler, SfrFile sfrs, InterruptController interrupts, EventLog log, SscBus bus)
    {
        this.scheduler = scheduler;
        this.interrupts = interrupts;
        this.log = log;
        this.bus = bus;
        sfrs.Register(ConAddress, "ssc", ReadCon, WriteCon, ReadCon);
        sfrs.Register(TbAddress, "ssc", () => tb, WriteTb, () => tb);
        sfrs.Register(RbAddress, "ssc", ReadRb, _ => { }, () => rb);   // SSCRB is read-only
        sfrs.Register(BrAddress, "ssc", () => br, WriteBr, () => br);  // [assumption] reads give the reload value
        OnReset();
    }

    public bool Enabled => enabled;

    public bool IsMaster => master;

    /// <summary>Characters completed since power-on, as master or as slave.</summary>
    public long Transfers { get; private set; }

    int Width => (control & 0xF) + 1;

    int BitCycles => 2 * (br + 1);

    /// <summary>
    /// [assumption] The shift register powers up as FFFFh, so a slave clocked before its first
    /// SSCTB write shifts out ones.
    /// </summary>
    public void OnReset()
    {
        Cancel();
        control = flags = tb = rb = br = 0;
        shift = 0xFFFF;
        master = enabled = tbFull = shiftLoaded = rbUnread = busy = false;
        mtsr = true;
    }

    /// <summary>
    /// Slave mode: the external master clocks one character. Without a reloaded SSCTB this is a
    /// transmit error, and the old shift register goes out: normally the data received last
    /// (UM §12.6); no SSCTIR then. Disabled or in master mode it returns all ones and changes nothing.
    /// </summary>
    public ushort ExternalTransfer(ushort fromMaster)
    {
        if (!enabled || master)
            return 0xFFFF;
        var mask = (ushort)((1 << Width) - 1);
        if (!shiftLoaded)
            RaiseError(Te, Ten);
        var sent = (ushort)(shift & mask);
        shiftLoaded = false;
        Complete((ushort)(fromMaster & mask));
        if (tbFull)
        {
            tbFull = false;
            shift = tb;
            shiftLoaded = true;
            interrupts.Request(transmit);
        }
        return sent;
    }

    ushort ReadCon()
    {
        if (!enabled)
            return (ushort)((master ? Ms : 0) | control);
        var busyNow = master ? busy : shiftLoaded;
        return (ushort)(En | (master ? Ms : 0) | (busyNow ? Bsy : 0) | flags | BitCount());
    }

    /// <summary>SSCBC: the bits shifted so far in the running master transfer.</summary>
    int BitCount() => busy ? (int)Math.Min((ulong)(Width - 1), (scheduler.Now - transferStart) / (ulong)BitCycles) & 0xF : 0;

    void WriteCon(ushort value)
    {
        if (enabled)
        {
            flags = (ushort)(value & FlagBits);     // software clears the error flags
        }
        else
        {
            control = (ushort)(value & ControlBits);
            rbUnread = false;                       // [assumption] UM p. 12-4: a programming-mode write "will initialize the SSC"
        }
        master = (value & Ms) != 0;
        var enable = (value & En) != 0;
        if (enabled && !enable)
            Cancel();
        enabled = enable;
    }

    void WriteTb(ushort value)
    {
        tb = value;
        if (!enabled)
            return;
        if (master)
        {
            if (busy)
                tbFull = true;
            else
                StartMasterTransfer();
            return;
        }
        if (shiftLoaded)
        {
            tbFull = true;
            return;
        }
        shift = tb;
        shiftLoaded = true;
        interrupts.Request(transmit);
    }

    ushort ReadRb()
    {
        rbUnread = false;
        return rb;
    }

    void WriteBr(ushort value)
    {
        if (enabled)
            log.Add((c, pc) => new UnsupportedEvent(c, pc, $"write of {value:X4}h to SSCBR while the SSC is enabled"));
        br = value;
    }

    void StartMasterTransfer()
    {
        if ((control & 0xF) == 0)
            log.Add((c, pc) => new UnsupportedEvent(c, pc, "SSC transfer with the reserved data width SSCBM = 0"));
        tbFull = false;
        shift = tb;
        busy = true;
        transferStart = scheduler.Now;
        interrupts.Request(transmit);
        completionEvent = scheduler.Schedule(transferStart + (ulong)(Width * BitCycles), "SSC transfer end", CompleteMasterTransfer);
    }

    void CompleteMasterTransfer()
    {
        completionEvent = -1;
        Complete(PlayBits(shift));
        if (tbFull)
            StartMasterTransfer();
        else
            busy = false;
    }

    /// <summary>
    /// The clock edges of one master character, in order. Each edge: on the latch edge (leading
    /// when SSCPH = 1, trailing when 0) the master samples MRST; then every selected slave sees the
    /// edge; on the shift edge the master puts the next bit on MTSR (SSCPH = 1: the trailing edge
    /// of bit i puts bit i + 1; SSCPH = 0: the leading edge of bit i puts bit i). With SSCPH = 1
    /// the first bit is on MTSR before the first edge. The leading edge rises when SSCPO = 0.
    /// </summary>
    ushort PlayBits(ushort data)
    {
        var width = Width;
        var msbFirst = (control & Hb) != 0;
        var latchOnLeading = (control & Ph) != 0;
        var leadingRises = (control & Po) == 0;
        int Position(int i) => msbFirst ? width - 1 - i : i;
        bool Bit(int i) => (data & (1 << Position(i))) != 0;

        if (latchOnLeading)
            mtsr = Bit(0);
        var received = 0;
        for (var i = 0; i < width; i++)
        {
            for (var edge = 0; edge < 2; edge++)
            {
                var leading = edge == 0;
                var latchEdge = leading == latchOnLeading;
                if (latchEdge && bus.MrstLevel)
                    received |= 1 << Position(i);
                bus.ClockEdge(leading == leadingRises, mtsr);
                if (!latchEdge)
                {
                    if (!latchOnLeading)
                        mtsr = Bit(i);
                    else if (i + 1 < width)
                        mtsr = Bit(i + 1);
                }
            }
        }
        return (ushort)received;
    }

    /// <summary>The end of a character: SSCRB (receive error if it was not read), SSCRIR.</summary>
    void Complete(ushort received)
    {
        if (rbUnread)
            RaiseError(Re, Ren);
        rb = received;
        shift = received;
        rbUnread = true;
        Transfers++;
        interrupts.Request(receive);
    }

    /// <summary>The flag is always set; SSCEIR only when its enable bit is set (UM §12.6).</summary>
    void RaiseError(ushort flag, ushort enable)
    {
        flags |= flag;
        if ((control & enable) != 0)
            interrupts.Request(error);
    }

    void Cancel()
    {
        if (completionEvent >= 0)
            scheduler.Cancel(completionEvent);
        completionEvent = -1;
        busy = tbFull = shiftLoaded = false;
    }
}
