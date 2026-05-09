namespace Ms43Sim.Core;

/// <summary>
/// The two CAPCOM units (UM §15): timers T0/T1 and T7/T8 with their reload registers, and the 32
/// capture/compare channels CC0-CC31 with CCM0-CCM7 (per channel: CCMODx in 3 bits and ACCx,
/// 1 = T1/T8). The timers count up; at FFFFh → 0000h a timer reloads TxREL and sets TxIR.
/// T01CON/T78CON bytes: bits 2-0 TxI, 3 TxM, 6 TxR. Timer mode steps every 2^(TxI + 3) cycles;
/// counter mode steps on T6 over/underflows (TxI = 000) or on T0IN (P3.0) / T7IN (P2.15) edges
/// (001 rising, 010 falling, 011 both; T1 and T8 stop on these).
/// Capture (CCMOD 001/010/011) copies the allocated timer into CCx at the selected edge of CCxIO,
/// latch changes of an output pin included, and sets CCxIR. Compare: a match is the allocated
/// timer stepping (or reloading) to CCx. Mode 0 sets CCxIR; mode 1 also toggles the port latch;
/// mode 2 sets CCxIR at the first match of a timer period; mode 3 then also sets the latch, and
/// resets it at the timer's overflow. Pins: CC0-15 P2.0-15, CC16-23 P8.0-7, CC28-31 P7.4-7;
/// CC24-27 have none. Timer values are computed from the clock; each timer schedules only its
/// earliest overflow or compare match.
/// A software write of a timer is compared with the channels like a step (UM p. 15-15: compare
/// events are re-enabled when the timer is "written to by software"; the firmware's gap teeth
/// advance T7 with SUB T7, ONES and rely on it). A change of a channel's CCM field clears its
/// once-per-period lock [inf: the coil and knock channels switch off and back to mode 2 within a
/// period and rely on it; plan 3, Reference facts].
/// [assumption] The prescalers run from power-on, so steps fall on multiples of the prescale; a
/// software write of CCx is not a match. Not modelled, and reported: the double-register compare
/// mode, and the reserved counter inputs TxI = 1xx (the timer stops), each once per machine. In
/// mode 3 a CCx equal to the reload value raises its interrupts but leaves the pin (UM p. 15-18).
/// </summary>
public sealed class Capcom
{
    const uint T01conAddress = 0xFF50, T78conAddress = 0xFF20;
    static readonly uint[] CcmAddress = { 0xFF52, 0xFF54, 0xFF56, 0xFF58, 0xFF22, 0xFF24, 0xFF26, 0xFF28 };

    readonly Scheduler scheduler;
    readonly InterruptController interrupts;
    readonly Ports ports;
    readonly EventLog log;
    readonly CapTimer[] timers;                 // T0, T1, T7, T8
    readonly InterruptSource[] channelRequest = new InterruptSource[32];
    readonly ushort[] cc = new ushort[32];
    readonly ushort[] ccm = new ushort[8];
    readonly bool[] matchedThisPeriod = new bool[32];
    readonly HashSet<string> reported = new();

    public Capcom(Scheduler scheduler, SfrFile sfrs, InterruptController interrupts, Ports ports, Gpt gpt, EventLog log)
    {
        this.scheduler = scheduler;
        this.interrupts = interrupts;
        this.ports = ports;
        this.log = log;
        timers = new[]
        {
            new CapTimer(this, "T0", 0, edgeInput: (3, 0)),
            new CapTimer(this, "T1", 0, edgeInput: null),
            new CapTimer(this, "T7", 16, edgeInput: (2, 15)),
            new CapTimer(this, "T8", 16, edgeInput: null),
        };
        RegisterTimer(sfrs, timers[0], 0xFE50, 0xFE54);
        RegisterTimer(sfrs, timers[1], 0xFE52, 0xFE56);
        RegisterTimer(sfrs, timers[2], 0xF050, 0xF054);
        RegisterTimer(sfrs, timers[3], 0xF052, 0xF056);
        sfrs.Register(T01conAddress, "capcom", () => Control(0), v => WriteControl(0, v), () => Control(0));
        sfrs.Register(T78conAddress, "capcom", () => Control(2), v => WriteControl(2, v), () => Control(2));
        for (var x = 0; x < 32; x++)
        {
            var channel = x;
            channelRequest[x] = InterruptSources.ByName("CC" + x);
            var address = x < 16 ? 0xFE80 + 2u * (uint)x : 0xFE60 + 2u * (uint)(x - 16);
            sfrs.Register(address, "capcom", () => cc[channel], v => WriteCc(channel, v), () => cc[channel]);
        }
        for (var i = 0; i < 8; i++)
        {
            var index = i;
            sfrs.Register(CcmAddress[i], "capcom", () => ccm[index], v => WriteCcm(index, v), () => ccm[index]);
        }
        gpt.T6OverOrUnderflow += OnT6OverOrUnderflow;
        ports.PinChanged += OnPinChanged;
        OnReset();
    }

    public ushort T0 => timers[0].Value;

    public ushort T1 => timers[1].Value;

    public ushort T7 => timers[2].Value;

    public ushort T8 => timers[3].Value;

    public void OnReset()
    {
        Array.Clear(cc);
        Array.Clear(ccm);
        Array.Clear(matchedThisPeriod);
        foreach (var timer in timers)
            timer.Reset();
    }

    static void RegisterTimer(SfrFile sfrs, CapTimer timer, uint valueAddress, uint reloadAddress)
    {
        sfrs.Register(valueAddress, "capcom", () => timer.Value, timer.Write, () => timer.Value);
        sfrs.Register(reloadAddress, "capcom", () => timer.Reload, v =>
        {
            timer.Reload = v;
            timer.Reschedule();
        }, () => timer.Reload);
    }

    ushort Control(int first) => (ushort)(timers[first].Control | (timers[first + 1].Control << 8));

    void WriteControl(int first, ushort value)
    {
        timers[first].Configure((byte)value);
        timers[first + 1].Configure((byte)(value >> 8));
    }

    int Mode(int x) => (ccm[x >> 2] >> (4 * (x & 3))) & 7;

    CapTimer TimerOf(int x) => timers[(x < 16 ? 0 : 2) + ((ccm[x >> 2] >> (4 * (x & 3) + 3)) & 1)];

    static bool IsCompare(int mode) => mode >= 4;

    static (int Port, int Bit)? PinOf(int x) => x switch
    {
        < 16 => (2, x),
        < 24 => (8, x - 16),
        >= 28 => (7, x - 24),
        _ => null,
    };

    void WriteCc(int x, ushort value)
    {
        cc[x] = value;
        if (IsCompare(Mode(x)))
            TimerOf(x).Reschedule();
    }

    void WriteCcm(int index, ushort value)
    {
        var changed = ccm[index] ^ value;
        for (var k = 0; k < 4; k++)
        {
            if ((changed >> (4 * k) & 0xF) != 0)
                matchedThisPeriod[4 * index + k] = false;      // a rewritten mode re-arms the channel [inf]
        }
        ccm[index] = value;
        var unit = index < 4 ? 0 : 16;
        for (var x = unit + 8; x < unit + 16; x++)
        {
            if (Mode(x) == 4 && Mode(x - 8) == 5)
                ReportOnce($"double-register compare mode on CC{x - 8}/CC{x} is not modelled: each channel runs alone");
        }
        var first = unit == 0 ? 0 : 2;
        timers[first].Reschedule();
        timers[first + 1].Reschedule();
    }

    /// <summary>Each unmodelled mode is reported once per machine.</summary>
    void ReportOnce(string what)
    {
        if (reported.Add(what))
            log.Add((c, pc) => new UnsupportedEvent(c, pc, what));
    }

    void OnT6OverOrUnderflow()
    {
        foreach (var timer in timers)
            timer.T6OverOrUnderflow();
    }

    void OnPinChanged(int port, int bit, bool level)
    {
        foreach (var timer in timers)
            timer.EdgeInput(port, bit, level);
        var x = port switch
        {
            2 => bit,
            8 when bit < 8 => 16 + bit,
            7 when bit >= 4 => 24 + bit,
            _ => -1,
        };
        if (x < 0)
            return;
        var mode = Mode(x);
        if (mode is 1 or 2 or 3 && (mode == 3 || (mode == 1) == level))
        {
            cc[x] = TimerOf(x).Value;
            interrupts.Request(channelRequest[x]);
        }
    }

    /// <summary>The compare channels of <paramref name="timer"/> whose value <paramref name="value"/> matches.</summary>
    void Matches(CapTimer timer, ushort value)
    {
        for (var x = timer.FirstChannel; x < timer.FirstChannel + 16; x++)
        {
            var mode = Mode(x);
            if (!IsCompare(mode) || cc[x] != value || TimerOf(x) != timer)
                continue;
            if (mode >= 6)
            {
                if (matchedThisPeriod[x])
                    continue;
                matchedThisPeriod[x] = true;
            }
            interrupts.Request(channelRequest[x]);
            if (PinOf(x) is { } pin)
            {
                if (mode == 5)
                    ports.ToggleLatchBit(pin.Port, pin.Bit);
                else if (mode == 7 && cc[x] != timer.Reload)
                    ports.SetLatchBit(pin.Port, pin.Bit, true);
            }
        }
    }

    /// <summary>
    /// A new period of <paramref name="timer"/>: modes 2 and 3 may match again; mode 3 resets its
    /// latch, unless CCx equals the reload value (UM p. 15-18: set and clear would coincide, so
    /// the output does not change).
    /// </summary>
    void Overflowed(CapTimer timer)
    {
        for (var x = timer.FirstChannel; x < timer.FirstChannel + 16; x++)
        {
            if (TimerOf(x) != timer)
                continue;
            matchedThisPeriod[x] = false;
            if (Mode(x) == 7 && cc[x] != timer.Reload && PinOf(x) is { } pin)
                ports.SetLatchBit(pin.Port, pin.Bit, false);
        }
    }

    /// <summary>
    /// Steps from <paramref name="value"/> until compare channel x next matches: in this period if
    /// CCx lies ahead (and mode 2/3 has not matched yet), else after the reload; never when CCx lies
    /// below the reload value.
    /// </summary>
    ulong StepsToMatch(int x, ushort value, ushort reload)
    {
        var compare = cc[x];
        var available = Mode(x) < 6 || !matchedThisPeriod[x];
        if (available && compare > value)
            return (ulong)(compare - value);
        if (compare >= reload)
            return 0x10000ul - value + (ulong)(compare - reload);
        return ulong.MaxValue;
    }

    sealed class CapTimer
    {
        readonly Capcom owner;
        readonly (int Port, int Bit)? edgeInput;
        ushort value;           // the value at baseCycle
        ulong baseCycle;
        bool clocked;
        bool counting;
        int countInput;
        int prescale = 8;
        long stepEvent = -1;

        public CapTimer(Capcom owner, string name, int firstChannel, (int, int)? edgeInput)
        {
            this.owner = owner;
            Name = name;
            FirstChannel = firstChannel;
            this.edgeInput = edgeInput;
            Request = InterruptSources.ByName(name);
        }

        public string Name { get; }

        public int FirstChannel { get; }

        public InterruptSource Request { get; }

        public byte Control { get; private set; }

        public ushort Reload { get; set; }

        Scheduler Clock => owner.scheduler;

        public ushort Value => clocked ? (ushort)(value + Steps(Clock.Now)) : value;

        public void Reset()
        {
            Cancel();
            Control = 0;
            value = 0;
            Reload = 0;
            clocked = counting = false;
            prescale = 8;
        }

        /// <summary>A software write is compared like a step (UM p. 15-15), before the next step is scheduled.</summary>
        public void Write(ushort v)
        {
            Cancel();
            value = v;
            baseCycle = Clock.Now;
            owner.Matches(this, value);
            Schedule();
        }

        public void Configure(byte control)
        {
            Freeze();
            Control = control;
            var run = (control & 0x40) != 0;
            var counterMode = (control & 0x08) != 0;
            countInput = control & 7;
            prescale = 8 << countInput;
            clocked = run && !counterMode;
            counting = run && counterMode;
            if (counting && countInput >= 4)
            {
                owner.ReportOnce($"{Name}: the reserved counter input TxI = 1xx; {Name} stops");
                counting = false;
            }
            else if (counting && countInput != 0 && edgeInput == null)
            {
                counting = false;   // UM: T1 and T8 stop on an edge selection
            }
            Schedule();
        }

        public void Reschedule()
        {
            Freeze();
            Schedule();
        }

        public void T6OverOrUnderflow()
        {
            if (counting && countInput == 0)
                Step();
        }

        public void EdgeInput(int port, int bit, bool rising)
        {
            if (!counting || edgeInput is not { } pin || pin.Port != port || pin.Bit != bit)
                return;
            if (countInput == 3 || (countInput == 1 && rising) || (countInput == 2 && !rising))
                Step();
        }

        /// <summary>One counter-mode step: it checks the compare values directly.</summary>
        void Step()
        {
            if (value == 0xFFFF)
            {
                Overflow();
                return;
            }
            value++;
            owner.Matches(this, value);
        }

        void Overflow()
        {
            value = Reload;
            owner.interrupts.Request(Request);
            owner.Overflowed(this);
            owner.Matches(this, value);
        }

        ulong Steps(ulong now) => now / (ulong)prescale - baseCycle / (ulong)prescale;

        void Freeze()
        {
            value = Value;
            baseCycle = Clock.Now;
            Cancel();
        }

        /// <summary>The earliest overflow or compare match of this timer, if it runs with the clock.</summary>
        void Schedule()
        {
            Cancel();
            if (!clocked)
                return;
            var steps = 0x10000ul - value;
            for (var x = FirstChannel; x < FirstChannel + 16; x++)
            {
                if (IsCompare(owner.Mode(x)) && owner.TimerOf(x) == this)
                    steps = Math.Min(steps, owner.StepsToMatch(x, value, Reload));
            }
            var at = (baseCycle / (ulong)prescale + steps) * (ulong)prescale;
            stepEvent = Clock.Schedule(at, Name + " step", Fire);
        }

        void Fire()
        {
            stepEvent = -1;
            var steps = Steps(Clock.Now);
            baseCycle = Clock.Now;
            if (value + steps >= 0x10000)
            {
                Overflow();
            }
            else
            {
                value = (ushort)(value + steps);
                owner.Matches(this, value);
            }
            Schedule();
        }

        void Cancel()
        {
            if (stepEvent >= 0)
                Clock.Cancel(stepEvent);
            stepEvent = -1;
        }
    }
}
