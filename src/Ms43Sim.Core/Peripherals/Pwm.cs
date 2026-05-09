namespace Ms43Sim.Core;

/// <summary>
/// The PWM module (UM §16) in mode 0, edge aligned. PWMCON0: bits 3-0 PTRx (run), 7-4 PTIx
/// (1 = fCPU/64), 11-8 PIEx, 15-12 PIRx; PWMCON1: bits 3-0 PENx, 7-4 PMx, 12 PB01, 15-14 PS2/PS3.
/// PTx counts up to its period shadow, then restarts at 0000h; at each restart the period and
/// pulse-width shadows load from PPx and PWx (a write while the timer is stopped loads them at once)
/// and PIRx is set; PWMIR is set too when PIEx and PWMIE are both 1. The comparator output is
/// PTx ≥ PWx shadow. Values are computed from the clock; a restart is scheduled only for a running
/// channel with PIEx = 1.
/// [assumption] PIRx is set when PTx restarts at 0000h, not when the timer starts; a counter
/// started above its period shadow runs to FFFFh and restarts at the wrap.
/// Not modelled, each reported once: mode 1 (center aligned), burst mode (PB01) and single shot
/// (PS2/PS3). <see cref="PinHighFraction"/> is the P7.x pin's share of time high in the current
/// period (UM §16: the comparator XORed with the latch while PENx = 1, the latch otherwise); a port
/// read still returns the latch. <see cref="SettingsChanged"/> follows every write that can change
/// an output; a PWx or PPx write takes effect at the next restart (the shadow).
/// </summary>
public sealed class Pwm
{
    const uint Pwmcon0Address = 0xFF30, Pwmcon1Address = 0xFF32, PwmicAddress = 0xF17E;
    const ushort Pwmie = 0x0040;

    readonly Scheduler scheduler;
    readonly SfrFile sfrs;
    readonly InterruptController interrupts;
    readonly EventLog log;
    readonly InterruptSource request = InterruptSources.ByName("PWM");
    readonly Channel[] channels;
    readonly HashSet<string> reported = new();
    ushort pwmcon1;

    public Pwm(Scheduler scheduler, SfrFile sfrs, InterruptController interrupts, EventLog log)
    {
        this.scheduler = scheduler;
        this.sfrs = sfrs;
        this.interrupts = interrupts;
        this.log = log;
        channels = Enumerable.Range(0, 4).Select(x => new Channel(this, x)).ToArray();
        foreach (var channel in channels)
        {
            var c = channel;
            var offset = 2u * (uint)c.Number;
            sfrs.Register(0xF030 + offset, "pwm", () => c.Counter, c.WriteCounter, () => c.Counter);
            sfrs.Register(0xF038 + offset, "pwm", () => c.Period, v => { c.WritePeriod(v); Changed(c.Number); }, () => c.Period);
            sfrs.Register(0xFE30 + offset, "pwm", () => c.Width, v => { c.WriteWidth(v); Changed(c.Number); }, () => c.Width);
        }
        sfrs.Register(Pwmcon0Address, "pwm", ReadPwmcon0, v => { WritePwmcon0(v); ChangedAll(); }, ReadPwmcon0);
        sfrs.Register(Pwmcon1Address, "pwm", () => pwmcon1, v => { WritePwmcon1(v); ChangedAll(); }, () => pwmcon1);
        OnReset();
    }

    /// <summary>Raised with the channel after every write to its PPx or PWx, and for channels 0-3 after a PWMCON0/1 write.</summary>
    public event Action<int>? SettingsChanged;

    public ushort Counter(int channel) => channels[channel].Counter;

    /// <summary>PENx: the channel's output reaches its P7 pin.</summary>
    public bool OutputEnabled(int channel) => (pwmcon1 & (1 << channel)) != 0;

    /// <summary>
    /// The P7.x pin's share of time high in the current period (UM §16): the latch while PENx = 0;
    /// the frozen comparator XOR the latch while the timer is stopped; otherwise the share of the
    /// period with PTx ≥ PWx shadow, complemented when the latch is 1.
    /// </summary>
    public double PinHighFraction(int channel, bool latch) =>
        OutputEnabled(channel) ? channels[channel].HighFraction(latch) : latch ? 1.0 : 0.0;

    void Changed(int channel) => SettingsChanged?.Invoke(channel);

    void ChangedAll()
    {
        for (var x = 0; x < 4; x++)
            Changed(x);
    }

    /// <summary>The comparator output PTx ≥ PWx shadow (before the XOR with the P7 latch).</summary>
    public bool Output(int channel) => channels[channel].Output;

    /// <summary>Restarts of channel x since reset.</summary>
    public long Periods(int channel) => channels[channel].Periods;

    public void OnReset()
    {
        pwmcon1 = 0;
        foreach (var channel in channels)
            channel.Reset();
    }

    ushort ReadPwmcon0()
    {
        var value = 0;
        foreach (var c in channels)
            value |= c.ControlBits;
        return (ushort)value;
    }

    void WritePwmcon0(ushort value)
    {
        foreach (var c in channels)
            c.WriteControl(value);
    }

    void WritePwmcon1(ushort value)
    {
        pwmcon1 = value;
        for (var x = 0; x < 4; x++)
        {
            if ((value & (0x10 << x)) != 0)
                ReportOnce($"PWM channel {x} mode 1 (center aligned) is not modelled; it runs edge aligned");
        }
        if ((value & 0x1000) != 0)
            ReportOnce("PWM burst mode (PB01) is not modelled");
        if ((value & 0xC000) != 0)
            ReportOnce("PWM single-shot mode (PS2/PS3) is not modelled");
    }

    void ReportOnce(string what)
    {
        if (reported.Add(what))
            log.Add((c, pc) => new UnsupportedEvent(c, pc, what));
    }

    sealed class Channel
    {
        readonly Pwm owner;
        ushort periodShadow, widthShadow;
        bool shadowPending;     // PPx or PWx written while running: the shadows load at the next restart
        bool running, prescaled, interruptEnabled;
        ulong baseTime;         // a moment at which the counter was baseCount
        ushort baseCount;
        long basePeriods;
        bool pirSet;            // PIRx as written by software
        long pirMark;           // restarts already acknowledged by that write
        long restartEvent = -1;

        public Channel(Pwm owner, int number)
        {
            this.owner = owner;
            Number = number;
        }

        public int Number { get; }

        public ushort Period { get; private set; }

        public ushort Width { get; private set; }

        int Prescale => prescaled ? 64 : 1;

        ulong Now => owner.scheduler.Now;

        public ushort Counter
        {
            get
            {
                Settle();
                return running ? (ushort)(baseCount + (Now - baseTime) / (ulong)Prescale) : baseCount;
            }
        }

        public bool Output => Counter >= widthShadow;

        public double HighFraction(bool latch)
        {
            Settle();                       // the shadows of the current period
            if (!running)
                return Output ^ latch ? 1.0 : 0.0;
            var share = widthShadow > periodShadow ? 0.0 : (periodShadow + 1.0 - widthShadow) / (periodShadow + 1.0);
            return latch ? 1.0 - share : share;
        }

        public long Periods
        {
            get
            {
                Settle();
                return basePeriods;
            }
        }

        public int ControlBits
        {
            get
            {
                Settle();
                var pir = pirSet || basePeriods > pirMark;
                return ((running ? 1 : 0) << Number) | ((prescaled ? 1 : 0) << (4 + Number))
                     | ((interruptEnabled ? 1 : 0) << (8 + Number)) | ((pir ? 1 : 0) << (12 + Number));
            }
        }

        public void Reset()
        {
            Cancel();
            Period = Width = periodShadow = widthShadow = baseCount = 0;
            running = prescaled = interruptEnabled = shadowPending = pirSet = false;
            baseTime = 0;
            basePeriods = pirMark = 0;
        }

        public void WriteCounter(ushort value)
        {
            Settle();
            baseCount = value;
            baseTime = Now;
            Schedule();
        }

        public void WritePeriod(ushort value)
        {
            Period = value;
            WroteShadowedRegister();
        }

        public void WriteWidth(ushort value)
        {
            Width = value;
            WroteShadowedRegister();
        }

        public void WriteControl(ushort pwmcon0)
        {
            var counter = Counter;          // settles first
            var run = (pwmcon0 & (1 << Number)) != 0;
            prescaled = (pwmcon0 & (0x10 << Number)) != 0;
            interruptEnabled = (pwmcon0 & (0x100 << Number)) != 0;
            pirSet = (pwmcon0 & (0x1000 << Number)) != 0;
            pirMark = basePeriods;
            running = run;
            baseCount = counter;
            baseTime = Now;
            Schedule();
        }

        void WroteShadowedRegister()
        {
            Settle();
            if (running)
            {
                shadowPending = true;
                return;
            }
            periodShadow = Period;
            widthShadow = Width;
            Schedule();
        }

        /// <summary>Counts from the base to the next restart.</summary>
        ulong CountsToRestart() => baseCount <= periodShadow
            ? (ulong)(periodShadow - baseCount) + 1
            : 0x10000ul - baseCount;

        /// <summary>Moves the base to the latest restart at or before now, loading pending shadows at the first.</summary>
        void Settle()
        {
            if (!running)
                return;
            var counts = (Now - baseTime) / (ulong)Prescale;
            var first = CountsToRestart();
            if (counts < first)
                return;
            baseTime += first * (ulong)Prescale;
            baseCount = 0;
            basePeriods++;
            if (shadowPending)
            {
                periodShadow = Period;
                widthShadow = Width;
                shadowPending = false;
            }
            var length = (ulong)periodShadow + 1;
            var more = (Now - baseTime) / (ulong)Prescale / length;
            baseTime += more * length * (ulong)Prescale;
            basePeriods += (long)more;
        }

        void Schedule()
        {
            Cancel();
            if (!running || !interruptEnabled)
                return;
            var at = baseTime + CountsToRestart() * (ulong)Prescale;
            restartEvent = owner.scheduler.Schedule(at, $"PWM{Number} restart", Restart);
        }

        void Restart()
        {
            restartEvent = -1;
            Settle();
            if ((owner.sfrs.Peek(PwmicAddress) & Pwmie) != 0)
                owner.interrupts.Request(owner.request);
            Schedule();
        }

        void Cancel()
        {
            if (restartEvent >= 0)
                owner.scheduler.Cancel(restartEvent);
            restartEvent = -1;
        }
    }
}
