namespace Ms43Sim.Core;

/// <summary>A CAN frame: an 11-bit or 29-bit identifier and up to 8 data bytes.</summary>
public sealed record CanFrame(uint Id, bool Extended, byte[] Data);

/// <summary>What the module sent under one identifier.</summary>
public sealed record CanTxSummary(uint Id, long Count, ulong FirstCycle, ulong LastCycle, byte[] LastData);

/// <summary>
/// The on-chip CAN module at 00'EF00-00'EFFF (UM §18). CSR (EF00h): control byte bit 0 INIT,
/// 1 IE, 2 SIE, 3 EIE, 6 CCE; status byte bits 10-8 LEC, 11 TXOK, 12 RXOK, 14 EWRN, 15 BOFF.
/// IR (EF02h) holds INTID: 1 a status change, 2 object 15, 2 + n object n. BTR (EF04h), writable
/// with CCE: one bit = 2 × (BRP + 1) × (3 + TSEG1 + TSEG2) cycles. Masks: GMS, UGML/LGML, and the
/// last-message mask. Message object n (1-15) at EF00h + 16n: MCR, UAR, LAR, MCFG (DLC 7-4,
/// DIR 3, XTD 2) and 8 data bytes. MCR holds 2-bit elements (INTPND, RXIE, TXIE, MSGVAL, NEWDAT,
/// MSGLST/CPUUPD, TXRQ, RMTPND): a write of 01 resets an element, 10 sets it, 11 or 00 leaves it;
/// a read gives 01 or 10.
/// While INIT = 0 the lowest transmit object (1-14) with MSGVAL and TXRQ set and CPUUPD clear is
/// sent: NEWDAT := 0; after the frame INTPND := 1 if TXIE, TXRQ := 0 unless NEWDAT was set again,
/// TXOK := 1, LEC := 0. A delivered frame goes to the lowest receive object whose MSGVAL, XTD and
/// identifier (under the global mask) match: NEWDAT := 1, MSGLST := 1 if NEWDAT was already set,
/// INTPND := 1 if RXIE, RXOK := 1. With SIE a TXOK or RXOK is a status change, cleared by reading
/// the status byte. When IE is set, a 0 → 1 change of "INTID ≠ 0" requests XP0.
/// [stub: CAN partners] Every transmitted frame is acknowledged and nothing else is on the bus.
/// [assumption] A frame lasts (44 + 8·DLC + 3) bit times, no stuff bits, extended frames alike;
/// object 15 matches under the global mask only.
/// Not modelled: remote frames (a remote request is reported), error counters, bus-off and the
/// last-message mask.
/// </summary>
public sealed class CanModule
{
    const uint Base = 0xEF00;
    const int Csr = 0x00, Ir = 0x02, Btr = 0x04, Gms = 0x06, Ugml = 0x08, Lgml = 0x0A;
    const byte Init = 0x01, Ie = 0x02, Sie = 0x04, Cce = 0x40;
    const byte TxOk = 0x08, RxOk = 0x10, LecMask = 0x07;     // in the status byte
    const int IntPnd = 0, RxIe = 1, TxIe = 2, MsgVal = 3, NewDat = 4, MsgLst = 5, CpuUpd = 5, TxRq = 6;

    readonly Scheduler scheduler;
    readonly InterruptController interrupts;
    readonly EventLog log;
    readonly InterruptSource line = InterruptSources.ByName("XP0");
    readonly byte[] ram = new byte[0x100];
    readonly bool[] remoteReported = new bool[16];
    readonly List<Summary> transmitted = new();
    readonly Dictionary<uint, Summary> byId = new();

    bool statusChangePending;
    bool lineActive;
    int transmittingObject;           // 0 when idle
    long frameEndEvent = -1;
    CanFrame? frameOnBus;

    public CanModule(Scheduler scheduler, InterruptController interrupts, EventLog log)
    {
        this.scheduler = scheduler;
        this.interrupts = interrupts;
        this.log = log;
        PowerOn();
    }

    /// <summary>The module's 256 bytes as reads see them, registers included.</summary>
    public ReadOnlySpan<byte> MessageRam => ram;

    /// <summary>Per identifier, in order of first transmission.</summary>
    public IReadOnlyList<CanTxSummary> Transmitted =>
        transmitted.Select(s => new CanTxSummary(s.Id, s.Count, s.FirstCycle, s.LastCycle, s.LastData)).ToList();

    /// <summary>[assumption] Everything 00h, CSR = 0001h.</summary>
    public void PowerOn()
    {
        Array.Clear(ram);
        transmitted.Clear();
        byId.Clear();
        OnReset();
    }

    /// <summary>CSR = 0001h and IR = 0; the message objects keep their contents (UM: MCR is not affected by reset).</summary>
    public void OnReset()
    {
        CancelFrame();
        ram[Csr] = Init;
        ram[Csr + 1] = 0;
        statusChangePending = false;
        lineActive = false;
        Array.Clear(remoteReported);
        UpdateInterrupt();
    }

    public byte Read8(uint a)
    {
        var offset = Offset(a);
        if (offset == Csr + 1)
            ReadStatus();
        return ram[offset];
    }

    public ushort Read16(uint a)
    {
        var offset = Offset(a) & ~1;
        if (offset == Csr)
            ReadStatus();
        return Word(offset);
    }

    public ushort Peek16(uint a) => Word(Offset(a) & ~1);

    public void Poke16(uint a, ushort v)
    {
        var offset = Offset(a) & ~1;
        ram[offset] = (byte)v;
        ram[offset + 1] = (byte)(v >> 8);
    }

    public void Write16(uint a, ushort v)
    {
        var offset = Offset(a) & ~1;
        WriteByte(offset, (byte)v);
        WriteByte(offset + 1, (byte)(v >> 8));
        Changed();
    }

    public void Write8(uint a, byte v)
    {
        WriteByte(Offset(a), v);
        Changed();
    }

    /// <summary>A frame from the bus: it goes to the lowest matching receive object, if any.</summary>
    public void Deliver(CanFrame frame)
    {
        if ((ram[Csr] & Init) != 0)
            return;
        for (var n = 1; n <= 15; n++)
        {
            var o = 16 * n;
            var config = ram[o + 6];
            if (!Element(n, MsgVal) || (config & 0x08) != 0 || ((config & 0x04) != 0) != frame.Extended)
                continue;
            if (!Matches(n, frame))
                continue;
            var length = Math.Min(frame.Data.Length, 8);
            frame.Data.AsSpan(0, length).CopyTo(ram.AsSpan(o + 7, 8));
            ram[o + 6] = (byte)((config & 0x0F) | (length << 4));
            if (Element(n, NewDat))
                SetElement(n, MsgLst, true);
            SetElement(n, NewDat, true);
            if (Element(n, RxIe))
                SetElement(n, IntPnd, true);
            TransferDone(RxOk);
            return;
        }
    }

    static int Offset(uint a) => (int)(a - Base) & 0xFF;

    ushort Word(int offset) => (ushort)(ram[offset] | (ram[offset + 1] << 8));

    bool Element(int n, int element) => ((ram[16 * n + element / 4] >> (2 * (element % 4))) & 3) == 2;

    void SetElement(int n, int element, bool on)
    {
        var index = 16 * n + element / 4;
        var shift = 2 * (element % 4);
        ram[index] = (byte)((ram[index] & ~(3 << shift)) | ((on ? 2 : 1) << shift));
    }

    /// <summary>Reading the status byte clears a pending status-change interrupt (UM §18).</summary>
    void ReadStatus()
    {
        if (!statusChangePending)
            return;
        statusChangePending = false;
        UpdateInterrupt();
    }

    void WriteByte(int offset, byte value)
    {
        switch (offset)
        {
            case Csr:
                ram[Csr] = (byte)(value & 0x4F);     // INIT, IE, SIE, EIE, CCE; CPS and TM stay 0
                return;
            case Csr + 1:
                ram[Csr + 1] = (byte)((ram[Csr + 1] & 0xC0) | (value & 0x1F));   // LEC, TXOK, RXOK; EWRN and BOFF are read-only
                return;
            case Ir:
            case Ir + 1:
                return;                               // IR is read-only
            case Btr:
            case Btr + 1:
                if ((ram[Csr] & Cce) != 0)
                    ram[offset] = value;
                return;
        }
        if (offset >= 0x10 && (offset & 0x0F) <= 1)
        {
            WriteControlElements(offset, value);
            return;
        }
        ram[offset] = value;
    }

    /// <summary>MCR: per 2-bit element, 01 resets, 10 sets, 11 and 00 leave.</summary>
    void WriteControlElements(int offset, byte value)
    {
        var current = ram[offset];
        for (var shift = 0; shift < 8; shift += 2)
        {
            var code = (value >> shift) & 3;
            if (code is 1 or 2)
                current = (byte)((current & ~(3 << shift)) | (code << shift));
        }
        ram[offset] = current;
    }

    void Changed()
    {
        for (var n = 1; n <= 15; n++)
        {
            if (!Element(n, TxRq))
                remoteReported[n] = false;
        }
        UpdateInterrupt();
        TryTransmit();
    }

    void TryTransmit()
    {
        if ((ram[Csr] & Init) != 0 || transmittingObject != 0)
            return;
        for (var n = 1; n <= 15; n++)
        {
            if (!Element(n, MsgVal) || !Element(n, TxRq))
                continue;
            var receiveObject = (ram[16 * n + 6] & 0x08) == 0;
            if (receiveObject || n == 15)
            {
                if (!remoteReported[n])
                {
                    remoteReported[n] = true;
                    var number = n;
                    log.Add((c, pc) => new UnsupportedEvent(c, pc, $"CAN remote frame request on message object {number} is not modelled"));
                }
                continue;
            }
            if (Element(n, CpuUpd))
                continue;
            Send(n);
            return;
        }
    }

    void Send(int n)
    {
        var o = 16 * n;
        SetElement(n, NewDat, false);
        var config = ram[o + 6];
        var extended = (config & 0x04) != 0;
        var length = Math.Min(config >> 4, 8);
        frameOnBus = new CanFrame(Identifier(n, extended), extended, ram.AsSpan(o + 7, length).ToArray());
        transmittingObject = n;
        var bits = 44ul + 8ul * (ulong)length + 3ul;
        frameEndEvent = scheduler.Schedule(scheduler.Now + bits * BitCycles(), "CAN frame end", FrameEnd);
    }

    void FrameEnd()
    {
        frameEndEvent = -1;
        var n = transmittingObject;
        transmittingObject = 0;
        if (Element(n, TxIe))
            SetElement(n, IntPnd, true);
        if (!Element(n, NewDat))
            SetElement(n, TxRq, false);
        Record(frameOnBus!);
        TransferDone(TxOk);
        TryTransmit();
    }

    /// <summary>A successful transfer: TXOK or RXOK, LEC := 0, and a status change if SIE.</summary>
    void TransferDone(byte flag)
    {
        ram[Csr + 1] = (byte)((ram[Csr + 1] & ~LecMask) | flag);
        if ((ram[Csr] & Sie) != 0)
            statusChangePending = true;
        UpdateInterrupt();
    }

    void Record(CanFrame frame)
    {
        var now = scheduler.Now;
        if (!byId.TryGetValue(frame.Id, out var summary))
        {
            summary = new Summary { Id = frame.Id, FirstCycle = now };
            byId[frame.Id] = summary;
            transmitted.Add(summary);
        }
        summary.Count++;
        summary.LastCycle = now;
        summary.LastData = frame.Data;
    }

    /// <summary>INTID by priority, mirrored into IR; the XP0 request on a 0 → 1 change of the line.</summary>
    void UpdateInterrupt()
    {
        var intId = 0;
        if (statusChangePending)
            intId = 1;
        else if (Element(15, IntPnd))
            intId = 2;
        else
        {
            for (var n = 1; n <= 14; n++)
            {
                if (Element(n, IntPnd))
                {
                    intId = 2 + n;
                    break;
                }
            }
        }
        ram[Ir] = (byte)intId;
        ram[Ir + 1] = 0;
        var active = (ram[Csr] & Ie) != 0 && intId != 0;
        if (active && !lineActive)
            interrupts.Request(line);
        lineActive = active;
    }

    ulong BitCycles()
    {
        var btr = Word(Btr);
        var quantum = 2ul * ((ulong)(btr & 0x3F) + 1);
        return quantum * (3ul + (ulong)((btr >> 8) & 0xF) + (ulong)((btr >> 12) & 0x7));
    }

    /// <summary>UAR bits 7-0 are ID28-21, bits 15-8 ID20-13; LAR bits 7-0 ID12-5, bits 15-11 ID4-0.</summary>
    uint Identifier(int n, bool extended)
    {
        var o = 16 * n;
        var uar = Word(o + 2);
        var lar = Word(o + 4);
        return extended ? LongId(uar, lar) : ShortId(uar);
    }

    static uint ShortId(ushort uar) => (uint)((uar & 0xFF) << 3 | (uar >> 13));

    static uint LongId(ushort uar, ushort lar) =>
        (uint)((uar & 0xFF) << 21 | (uar >> 8) << 13 | (lar & 0xFF) << 5 | (lar >> 11));

    bool Matches(int n, CanFrame frame)
    {
        if (frame.Extended)
        {
            var mask = LongId(Word(Ugml), Word(Lgml));
            return ((Identifier(n, true) ^ frame.Id) & mask & 0x1FFFFFFF) == 0;
        }
        var shortMask = ShortId(Word(Gms));
        return ((Identifier(n, false) ^ frame.Id) & shortMask & 0x7FF) == 0;
    }

    void CancelFrame()
    {
        if (frameEndEvent >= 0)
            scheduler.Cancel(frameEndEvent);
        frameEndEvent = -1;
        transmittingObject = 0;
        frameOnBus = null;
    }

    sealed class Summary
    {
        public uint Id;
        public long Count;
        public ulong FirstCycle, LastCycle;
        public byte[] LastData = Array.Empty<byte>();
    }
}
