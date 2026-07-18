namespace Ms43Sim.Core;

/// <summary>
/// ASC0, the asynchronous serial channel (UM §11), in its asynchronous modes. S0CON: bits 2-0 S0M
/// (001 8 data, 011 7 data + parity, 100 9 data, 101 8 data + wake-up, 111 8 data + parity),
/// 3 S0STP (2 stop bits), 4 S0REN, 5 S0PEN, 6 S0FEN, 7 S0OEN, 8 S0PE, 9 S0FE, 10 S0OE, 12 S0ODD,
/// 13 S0BRS, 14 S0LB, 15 S0R. A bit takes 16 × (2 + S0BRS) × (S0BG + 1) cycles; a frame is a start
/// bit, the data field LSB first (the generated parity bit in bit 7 or 8) and its stop bits.
/// A write to S0TBUF moves to the shift register at once when the transmitter is idle and S0R is
/// set (S0TBIR); S0TIR comes before the last bit of the frame; a loaded S0TBUF follows at the end
/// of the frame. A received frame moves to S0RBUF (S0RIR) if S0R and S0REN were set at its start bit
/// (UM §11.1.2: reception begins on the start bit's falling edge, and clearing S0REN completes the frame
/// being received); overwriting an unread S0RBUF sets S0OE and, with S0OEN, S0EIR. S0LB loops each
/// transmitted frame back.
/// [assumption] Transmission starts without the divide-by-16 latency; received frames are clean, so
/// framing and parity errors are never raised; a loopback frame arrives at the end of its stop bits.
/// Not modelled, each reported once: synchronous mode (S0M = 000 with S0R set) and the reserved
/// modes 010 and 110.
/// </summary>
public sealed class Asc0
{
    const uint ConAddress = 0xFFB0, TbufAddress = 0xFEB0, RbufAddress = 0xFEB2, BgAddress = 0xFEB4;
    const ushort Stp = 0x0008, Ren = 0x0010, Oen = 0x0080, Oe = 0x0400, Odd = 0x1000, Brs = 0x2000, Lb = 0x4000, Run = 0x8000;
    const ulong ReceiverMemory = 1UL << 26;   // 2.8 s, longer than any frame: an 11-bit frame at 4 baud

    readonly Scheduler scheduler;
    readonly InterruptController interrupts;
    readonly EventLog log;
    readonly InterruptSource transmitBuffer = InterruptSources.ByName("S0TB");
    readonly InterruptSource transmit = InterruptSources.ByName("S0T");
    readonly InterruptSource receive = InterruptSources.ByName("S0R");
    readonly InterruptSource error = InterruptSources.ByName("S0E");
    readonly HashSet<string> reported = new();
    readonly List<(ulong Cycle, bool On)> receiverSwitched = new();   // oldest first, from the one in force ReceiverMemory ago

    ushort con, tbuf, rbuf, bg;
    bool tbufFull, transmitting, rbufUnread;
    ushort shifting;
    ulong frameStart, frameEnd;
    long transmitEvent = -1, frameEndEvent = -1;

    public Asc0(Scheduler scheduler, SfrFile sfrs, InterruptController interrupts, EventLog log)
    {
        this.scheduler = scheduler;
        this.interrupts = interrupts;
        this.log = log;
        sfrs.Register(ConAddress, "asc0", () => con, WriteCon, () => con);
        sfrs.Register(TbufAddress, "asc0", () => tbuf, WriteTbuf, () => tbuf);
        sfrs.Register(RbufAddress, "asc0", ReadRbuf, _ => { }, () => rbuf);   // S0RBUF is read-only
        sfrs.Register(BgAddress, "asc0", () => bg, v => bg = (ushort)(v & 0x1FFF), () => bg);
        OnReset();
    }

    /// <summary>Each frame's data field as sent, the generated parity bit included, and the cycle its start bit began.</summary>
    public event Action<ushort, ulong>? FrameTransmitted;

    public long FramesTransmitted { get; private set; }

    int Mode => con & 7;

    /// <summary>Data bits per frame, the parity or wake-up bit included.</summary>
    int DataBits => Mode is 4 or 5 or 7 ? 9 : 8;

    /// <summary>Bits in a frame now: the start bit, the data field and its stop bits.</summary>
    public int FrameBits => 1 + DataBits + ((con & Stp) != 0 ? 2 : 1);

    /// <summary>The bit time now in CPU cycles, 16 × (2, or 3 with S0BRS) × (S0BG + 1): a frame takes it when it starts.</summary>
    public double BitCycles => 16.0 * ((con & Brs) != 0 ? 3 : 2) * (bg + 1);

    /// <summary>The frame being sent: the cycle it started and the cycle its last stop bit ends; null while the transmitter is idle.</summary>
    public (ulong Start, ulong End)? FrameInProgress => transmitting ? (frameStart, frameEnd) : null;

    public void OnReset()
    {
        Cancel(ref transmitEvent);
        Cancel(ref frameEndEvent);
        con = tbuf = rbuf = bg = shifting = 0;
        tbufFull = transmitting = rbufUnread = false;
        NoteReceiver();
    }

    /// <summary>
    /// A complete frame from outside, at the end of its stop bit: its data field, LSB first, as S0RBUF would hold it,
    /// and the cycle its start bit began. It is taken if the receiver was on then, whatever it is now.
    /// </summary>
    public void Receive(ushort frame, ulong startBit)
    {
        if (!ReceiverOnAt(startBit))
            return;
        if (Mode == 5 && (frame & 0x100) == 0)
            return;                         // wake-up mode keeps only frames with the 9th bit set
        if (rbufUnread)
        {
            con |= Oe;
            if ((con & Oen) != 0)
                interrupts.Request(error);
        }
        rbuf = (ushort)(frame & ((1 << DataBits) - 1));
        rbufUnread = true;
        interrupts.Request(receive);
    }

    void WriteCon(ushort value)
    {
        con = value;
        NoteReceiver();
        if ((value & Run) != 0)
        {
            if (Mode == 0)
                ReportOnce("ASC0 synchronous mode (S0M = 000) is not modelled");
            else if (Mode is 2 or 6)
                ReportOnce($"ASC0 reserved mode S0M = {Convert.ToString(Mode, 2).PadLeft(3, '0')} is not modelled");
        }
        TryStart();
    }

    void WriteTbuf(ushort value)
    {
        tbuf = value;
        tbufFull = true;
        TryStart();
    }

    ushort ReadRbuf()
    {
        rbufUnread = false;
        return rbuf;
    }

    void TryStart()
    {
        if (transmitting || !tbufFull || (con & Run) == 0 || Mode is 0 or 2 or 6)
            return;
        tbufFull = false;
        transmitting = true;
        shifting = DataField(tbuf);
        interrupts.Request(transmitBuffer);
        var now = scheduler.Now;
        var bits = (ulong)FrameBits;
        var bit = (ulong)BitCycles;
        frameStart = now;
        frameEnd = now + bits * bit;
        transmitEvent = scheduler.Schedule(now + (bits - 1) * bit, "ASC0 last bit", () =>
        {
            transmitEvent = -1;
            interrupts.Request(transmit);
        });
        frameEndEvent = scheduler.Schedule(frameEnd, "ASC0 frame end", FrameEnd);
    }

    void FrameEnd()
    {
        frameEndEvent = -1;
        transmitting = false;
        FramesTransmitted++;
        var frame = shifting;
        FrameTransmitted?.Invoke(frame, frameStart);
        if ((con & Lb) != 0)
            Receive(frame, frameStart);
        TryStart();
    }

    /// <summary>The receiver is on: S0R and S0REN are both set.</summary>
    bool ReceiverOn => (con & Run) != 0 && (con & Ren) != 0;

    /// <summary>Whether the receiver was on in <paramref name="cycle"/>: after the last switching in or before it; off before the first.</summary>
    bool ReceiverOnAt(ulong cycle)
    {
        for (var i = receiverSwitched.Count - 1; i >= 0; i--)
        {
            if (receiverSwitched[i].Cycle <= cycle)
                return receiverSwitched[i].On;
        }
        return false;
    }

    /// <summary>Writes down a switching of the receiver, the last of a cycle standing for it, and forgets those older than <see cref="ReceiverMemory"/>.</summary>
    void NoteReceiver()
    {
        var on = ReceiverOn;
        if (receiverSwitched.Count > 0 && receiverSwitched[^1].On == on)
            return;
        var now = scheduler.Now;
        if (receiverSwitched.Count > 0 && receiverSwitched[^1].Cycle == now)
            receiverSwitched[^1] = (now, on);
        else
            receiverSwitched.Add((now, on));
        while (receiverSwitched.Count > 1 && receiverSwitched[1].Cycle + ReceiverMemory <= now)
            receiverSwitched.RemoveAt(0);
    }

    /// <summary>The data field sent for an S0TBUF value: 7 or 8 data bits plus the generated parity, or 8 or 9 bits as written.</summary>
    ushort DataField(ushort value) => Mode switch
    {
        3 => (ushort)((value & 0x7F) | (Parity(value & 0x7F) << 7)),
        7 => (ushort)((value & 0xFF) | (Parity(value & 0xFF) << 8)),
        4 or 5 => (ushort)(value & 0x1FF),
        _ => (ushort)(value & 0xFF),
    };

    /// <summary>Even parity is 1 when the data bits hold an odd number of 1s; S0ODD inverts it.</summary>
    int Parity(int data) => (System.Numerics.BitOperations.PopCount((uint)data) & 1) ^ ((con & Odd) != 0 ? 1 : 0);

    void ReportOnce(string what)
    {
        if (reported.Add(what))
            log.Add((c, pc) => new UnsupportedEvent(c, pc, what));
    }

    void Cancel(ref long handle)
    {
        if (handle >= 0)
            scheduler.Cancel(handle);
        handle = -1;
    }
}
