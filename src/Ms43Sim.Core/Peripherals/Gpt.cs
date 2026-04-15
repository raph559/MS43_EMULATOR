namespace Ms43Sim.Core;

/// <summary>
/// GPT1 (T2, T3, T4) and GPT2 (T5, T6 and CAPREL), UM §10. TxCON: bits 2-0 TxI, 5-3 TxM, 6 TxR,
/// 7 TxUD, 8 TxUDE; T3CON and T6CON add 9 TxOE and 10 TxOTL, T6CON adds 15 T6SR; T5CON adds
/// 10 CT3, 13-12 CI, 14 T5CLR and 15 T5SC. In timer and gated mode a GPT1 timer steps every
/// 8 × 2^TxI cycles and a GPT2 timer every 4 × 2^TxI; the value is computed from the clock and
/// only over/underflows are scheduled. Counter mode steps on TxIN edges, and T5 also on T6OTL
/// transitions. The count direction is TxUD, reversed by the TxEUD pin when TxUDE is set. An
/// over/underflow sets TxIR; for T3 and T6 it toggles TxOTL; T6 then reloads from CAPREL when T6SR
/// is set, and clocks the CAPCOM timers. A selected CAPIN edge captures T5 into CAPREL (T5SC),
/// clears T5 (T5CLR) and sets CRIR.
/// [assumption] The prescalers run from power-on, so steps fall on multiples of the prescale.
/// Not modelled, each reported with the timer's name: T3 incremental interface mode, T2/T4
/// reload, capture and incremental interface modes, T2/T4 counting T3OTL (concatenation), the
/// reserved mode codes (the timer then stops), and CT3 = 1 (no capture then). T3OUT and T6OUT do
/// not drive their pins.
/// </summary>
public sealed class Gpt
{
    const uint TimerBase = 0xFE40, ControlBase = 0xFF40, CaprelAddress = 0xFE4A;
    const ushort Run = 0x0040, Up = 0x0080, ExternalUpDown = 0x0100, Otl = 0x0400, T6sr = 0x8000;
    const ushort Ct3 = 0x0400, T5clr = 0x4000, T5sc = 0x8000;
    const int CapinPort = 3, CapinBit = 2;

    readonly Scheduler scheduler;
    readonly InterruptController interrupts;
    readonly Ports ports;
    readonly EventLog log;
    readonly GptTimer[] timers;
    readonly InterruptSource captureRequest = InterruptSources.ByName("CR");
    ushort caprel;

    public Gpt(Scheduler scheduler, SfrFile sfrs, InterruptController interrupts, Ports ports, EventLog log)
    {
        this.scheduler = scheduler;
        this.interrupts = interrupts;
        this.ports = ports;
        this.log = log;
        timers = new[]
        {
            new GptTimer(this, 2, 8, 0x01FF, (3, 7), (5, 15)),
            new GptTimer(this, 3, 8, 0x07FF, (3, 6), (3, 4)),
            new GptTimer(this, 4, 8, 0x01FF, (3, 5), (5, 14)),
            new GptTimer(this, 5, 4, 0xF5FF, (5, 13), (5, 11)),
            new GptTimer(this, 6, 4, 0x87FF, (5, 12), (5, 10)),
        };
        foreach (var timer in timers)
        {
            var t = timer;
            var offset = 2u * (uint)(t.Number - 2);
            sfrs.Register(TimerBase + offset, "gpt", () => t.Value, t.Write, () => t.Value);
            sfrs.Register(ControlBase + offset, "gpt", () => t.Control, t.WriteControl, () => t.Control);
        }
        sfrs.Register(CaprelAddress, "gpt", () => caprel, v => caprel = v, () => caprel);
        ports.PinChanged += OnPinChanged;
        OnReset();
    }

    /// <summary>Every T6 overflow or underflow: the clock of the CAPCOM timers in counter mode with TxI = 000.</summary>
    public event Action? T6OverOrUnderflow;

    public ushort Caprel => caprel;

    /// <summary>The value of Tn, n = 2…6.</summary>
    public ushort Timer(int n) => timers[n - 2].Value;

    public void OnReset()
    {
        caprel = 0;
        foreach (var timer in timers)
            timer.Reset();
    }

    GptTimer T5 => timers[3];

    void OnPinChanged(int port, int bit, bool level)
    {
        foreach (var timer in timers)
            timer.PinChanged(port, bit, level);
        if (port == CapinPort && bit == CapinBit)
            CapinEdge(level);
    }

    /// <summary>A CAPIN edge selected by CI: capture T5 (T5SC), clear it (T5CLR), set CRIR (UM §10.2.2).</summary>
    void CapinEdge(bool rising)
    {
        var control = T5.Control;
        if ((control & Ct3) != 0 || !EdgeSelected((control >> 12) & 3, rising))
            return;
        if ((control & T5sc) != 0)
            caprel = T5.Value;
        if ((control & T5clr) != 0)
            T5.Write(0);
        interrupts.Request(captureRequest);
    }

    /// <summary>The over/underflow consequences after <paramref name="timer"/> wrapped.</summary>
    void OnWrap(GptTimer timer)
    {
        interrupts.Request(timer.Request);
        if (timer.Number is 3 or 6)
            timer.Control ^= Otl;
        if (timer.Number != 6)
            return;
        if ((timer.Control & T6sr) != 0)
            timer.Write(caprel);
        T5.T6OtlChanged((timer.Control & Otl) != 0);
        T6OverOrUnderflow?.Invoke();
    }

    /// <summary>Edge selection 01 rising, 10 falling, 11 both, 00 none.</summary>
    static bool EdgeSelected(int selection, bool rising) => selection switch
    {
        1 => rising,
        2 => !rising,
        3 => true,
        _ => false,
    };

    /// <summary>What a TxCON value asks for that is not modelled, or null.</summary>
    static string? Unmodelled(int number, int mode, int input) => (number, mode) switch
    {
        (3, 6) => "incremental interface mode (T3M = 110)",
        (2 or 4, 4) => "reload mode (TxM = 100)",
        (2 or 4, 5) => "capture mode (TxM = 101)",
        (2 or 4, 6) => "incremental interface mode (TxM = 110)",
        (2 or 4, 1) when input >= 5 => "counting T3OTL transitions (concatenation, TxI = 1xx)",
        (3 or 6, 1) when input >= 4 => "the reserved counter input TxI = 1xx",
        (_, >= 4) => $"the reserved mode TxM = {Convert.ToString(mode, 2)}",
        _ => null,
    };

    sealed class GptTimer
    {
        readonly Gpt gpt;
        readonly int prescaleBase;
        readonly ushort mask;
        readonly (int Port, int Bit) input, upDown;
        ushort value;           // the value at baseCycle
        ulong baseCycle;
        bool clocked;           // steps with the clock: timer mode, or gated mode with the gate open
        bool counting;          // counter mode
        bool down;
        int prescale = 8;
        long wrapEvent = -1;

        public GptTimer(Gpt gpt, int number, int prescaleBase, ushort mask, (int, int) input, (int, int) upDown)
        {
            this.gpt = gpt;
            Number = number;
            this.prescaleBase = prescaleBase;
            this.mask = mask;
            this.input = input;
            this.upDown = upDown;
            Request = InterruptSources.ByName("T" + number);
        }

        public int Number { get; }

        public InterruptSource Request { get; }

        public ushort Control { get; set; }

        int Mode => (Control >> 3) & 7;

        int Input => Control & 7;

        public ushort Value => clocked ? (ushort)(value + (down ? -1 : 1) * (long)Steps(gpt.scheduler.Now)) : value;

        public void Reset()
        {
            CancelWrap();
            Control = 0;
            value = 0;
            clocked = counting = down = false;
            prescale = prescaleBase;
        }

        public void Write(ushort v)
        {
            CancelWrap();
            value = v;
            baseCycle = gpt.scheduler.Now;
            ScheduleWrap();
        }

        public void WriteControl(ushort v)
        {
            Freeze();
            Control = (ushort)(v & mask);
            var problem = Unmodelled(Number, Mode, Input);
            if (Number == 5 && (Control & Ct3) != 0)
                Report("CAPREL capture on T3IN/T3EUD (T5CON.CT3 = 1); CAPIN captures stop");
            if (problem != null)
            {
                Report($"{problem}; the timer stops");
                clocked = counting = false;
                return;
            }
            var run = (Control & Run) != 0;
            prescale = prescaleBase << Input;
            down = CountsDown();
            counting = run && Mode == 1;
            clocked = run && (Mode == 0 || (Mode is 2 or 3 && GateOpen()));
            ScheduleWrap();
        }

        public void PinChanged(int port, int bit, bool level)
        {
            if ((port, bit) == input)
            {
                if (Mode is 2 or 3 && (Control & Run) != 0)
                {
                    Freeze();
                    clocked = GateOpen();
                    ScheduleWrap();
                }
                else if (counting && EdgeSelected(Input & 3, level) && Input < 4)
                {
                    Count();
                }
            }
            if ((port, bit) == upDown && (Control & ExternalUpDown) != 0)
            {
                Freeze();
                down = CountsDown();
                ScheduleWrap();
            }
        }

        /// <summary>T5 in counter mode with T5I = 1xx counts the T6OTL transitions T6's over/underflows cause.</summary>
        public void T6OtlChanged(bool rising)
        {
            if (counting && Input >= 4 && EdgeSelected(Input & 3, rising))
                Count();
        }

        void Count()
        {
            var before = value;
            value = (ushort)(value + (down ? -1 : 1));
            if (down ? before == 0 : before == 0xFFFF)
                gpt.OnWrap(this);
        }

        /// <summary>Table 10-1: TxUD, reversed by a high TxEUD when TxUDE is set.</summary>
        bool CountsDown() => ((Control & Up) != 0) ^ ((Control & ExternalUpDown) != 0 && gpt.ports.Pin(upDown.Port, upDown.Bit));

        bool GateOpen() => gpt.ports.Pin(input.Port, input.Bit) == (Mode == 3);

        ulong Steps(ulong now) => now / (ulong)prescale - baseCycle / (ulong)prescale;

        /// <summary>Stops the clocked count at its current value (the prescaler phase is kept).</summary>
        void Freeze()
        {
            value = Value;
            baseCycle = gpt.scheduler.Now;
            CancelWrap();
        }

        void ScheduleWrap()
        {
            if (!clocked)
                return;
            var steps = down ? value + 1ul : 0x10000ul - value;
            var at = (baseCycle / (ulong)prescale + steps) * (ulong)prescale;
            wrapEvent = gpt.scheduler.Schedule(at, $"T{Number} over/underflow", Wrap);
        }

        void Wrap()
        {
            wrapEvent = -1;
            value = down ? (ushort)0xFFFF : (ushort)0;
            baseCycle = gpt.scheduler.Now;
            gpt.OnWrap(this);
            if (wrapEvent < 0)
                ScheduleWrap();
        }

        void CancelWrap()
        {
            if (wrapEvent >= 0)
                gpt.scheduler.Cancel(wrapEvent);
            wrapEvent = -1;
        }

        void Report(string what)
        {
            var name = Number;
            gpt.log.Add((c, pc) => new UnsupportedEvent(c, pc, $"T{name}: {what}"));
        }
    }
}
