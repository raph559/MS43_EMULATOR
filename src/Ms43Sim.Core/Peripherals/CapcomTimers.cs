namespace Ms43Sim.Core;

/// <summary>
/// CAPCOM1 timers T0 and T1 (UM §15.1). In timer mode a timer counts up at fCPU / 2^(TxI+3);
/// on overflow FFFFh → 0000h it reloads TxREL and requests its interrupt. T01CON: T0I = bits
/// 2:0, T0M = bit 3, T0R = bit 6, T1I = bits 10:8, T1M = bit 11, T1R = bit 14. Values are
/// computed from the clock when read; each overflow is one scheduled event.
/// Counter mode (TxM = 1) is not modelled: it is reported, and that timer stops.
/// </summary>
public sealed class CapcomTimers
{
    const uint T01conAddress = 0xFF50;

    readonly Scheduler scheduler;
    readonly EventLog log;
    readonly Timer[] timers;
    ushort t01con;

    public CapcomTimers(Scheduler scheduler, SfrFile sfrs, InterruptController interrupts, EventLog log)
    {
        this.scheduler = scheduler;
        this.log = log;
        timers = new[]
        {
            new Timer(this, interrupts, InterruptSources.ByName("T0"), "T0"),
            new Timer(this, interrupts, InterruptSources.ByName("T1"), "T1"),
        };
        for (var i = 0; i < 2; i++)
        {
            var timer = timers[i];
            sfrs.Register(0xFE50 + 2u * (uint)i, "capcom", () => timer.Value, timer.SetValue, () => timer.Value);
            sfrs.Register(0xFE54 + 2u * (uint)i, "capcom", () => timer.Reload, v => timer.Reload = v, () => timer.Reload);
        }
        sfrs.Register(T01conAddress, "capcom", () => t01con, WriteT01con, () => t01con);
    }

    public ushort T0 => timers[0].Value;

    public ushort T1 => timers[1].Value;

    public void OnReset()
    {
        t01con = 0;
        foreach (var timer in timers)
            timer.Reset();
    }

    void WriteT01con(ushort value)
    {
        t01con = value;
        timers[0].Configure(run: (value & 0x0040) != 0, counterMode: (value & 0x0008) != 0, input: value & 7);
        timers[1].Configure(run: (value & 0x4000) != 0, counterMode: (value & 0x0800) != 0, input: (value >> 8) & 7);
    }

    sealed class Timer
    {
        readonly CapcomTimers owner;
        readonly InterruptController interrupts;
        readonly InterruptSource source;
        readonly string name;
        ushort baseValue;
        ulong baseCycle;
        bool running;
        int shift = 3;
        long overflowEvent = -1;
        bool counterModeReported;

        public Timer(CapcomTimers owner, InterruptController interrupts, InterruptSource source, string name)
        {
            this.owner = owner;
            this.interrupts = interrupts;
            this.source = source;
            this.name = name;
        }

        public ushort Reload { get; set; }

        Scheduler Clock => owner.scheduler;

        public ushort Value => running
            ? (ushort)Math.Min(0xFFFFul, baseValue + ((Clock.Now - baseCycle) >> shift))
            : baseValue;

        public void SetValue(ushort value) => Rebase(value);

        public void Reset()
        {
            running = false;
            shift = 3;
            Reload = 0;
            Rebase(0);
        }

        public void Configure(bool run, bool counterMode, int input)
        {
            var current = Value;
            if (run && counterMode)
            {
                if (!counterModeReported)
                {
                    counterModeReported = true;
                    owner.log.Add((c, pc) => new UnsupportedEvent(c, pc, $"{name} counter mode (TxM = 1)"));
                }
                run = false;
            }
            running = run;
            shift = input + 3;
            Rebase(current);
        }

        void Rebase(ushort value)
        {
            baseValue = value;
            baseCycle = Clock.Now;
            if (overflowEvent >= 0)
                Clock.Cancel(overflowEvent);
            overflowEvent = running
                ? Clock.Schedule(baseCycle + ((0x10000ul - value) << shift), name + " overflow", Overflow)
                : -1;
        }

        void Overflow()
        {
            overflowEvent = -1;
            interrupts.Request(source);
            Rebase(Reload);
        }
    }
}
