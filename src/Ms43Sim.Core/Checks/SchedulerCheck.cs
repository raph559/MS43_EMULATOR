namespace Ms43Sim.Core;

/// <summary>
/// The scheduler check (plan 4 R2, R16): every task run passes a call site that holds its identity in a
/// register — queues Q1/Q2/Q3 at 0E:0944/0976/09A8 (R10 &gt;&gt; 2), the FDEE requests at 0E:0910 (bit
/// 15 − R9), the 5 ms, 10 ms, 100 ms and 1 s one-shot timers at 0E:0116/05C4/075A/08DA (RL6).
/// The window opens at a power cycle's first SRVWDT at 0E:0144 and closes at a reset or at the
/// power-off; at the first SRVWDT at 0B:D9AC (the power-down write) R2–R4 stop and R1 watches 0B:D9AC.
/// <list type="bullet">
/// <item>R1: a gap above 15 ms between SRVWDTs at 0E:0144 (before the power-down write) or at 0B:D9AC (during it);</item>
/// <item>R2: a periodic task with a gap above 2 × period + 5 ms, from its own first run, whose first run
/// is due by 2 × period + 1.1 s after power-on — judged while the shaft turns below 2,000 rpm at both
/// ends of the gap (plan 4 Task 11: at the ISM's minimum timing the car's own tasks stretch from about
/// 2,500 rpm, and at full load its lowest queue goes seconds without running; the time above is
/// declared in the report). The serial-line task T10ms:08h (09:FAAE) re-arms its own 10 ms timer, E3AE,
/// with F904: one tick while the K-line is idle, four (F910) to hold the reply to a DS2 connect (plan 5
/// Task 4). Its gap is judged by the period it armed, read from E3AE when it returns to 0E:05C8, and not
/// at all while that is 0 (the timer is off);</item>
/// <item>R3: while F900 = 2 and the shaft turns at 150 rpm or more, a gap between runs of Q1:0Dh above
/// three segment periods (20,000 / rpm ms) + 5 ms;</item>
/// <item>R4: a flag newly set in FDF2 or FD84 &amp; 01C0h (both sticky), read every 10 ms, with the shaft
/// below 2,000 rpm; one set at 2,000 rpm or more is a note, not a finding (the car's full-load pull sets
/// FD84.7 at the ISM's minimum timing, Task 11).</item>
/// </list>
/// R1 and R3 are judged at every speed. On the shaft's first tooth after the engine stopped inside the power cycle (a
/// stall, plan 5 Task 5), R2 and R3 start again as at power-on: no gap is judged across the stop, and a periodic task's
/// first run is due by 2 × period + 1.1 s after that tooth. R4's flags are sticky, so one seen before the stop stays seen.
/// Not evaluated when the program is not MS43X001 nor a known build on it, or the hook sites do not hold the car file's bytes.
/// </summary>
public sealed class SchedulerCheck() : CheckBase("scheduler")
{
    const ulong Ms = 24_000;
    const uint MainLoopService = RunState.MainLoopService, PowerDownService = RunState.PowerDownService;
    const uint Q1 = 0x0E0944, Q2 = 0x0E0976, Q3 = 0x0E09A8, Requests = 0x0E0910;
    const uint T5ms = 0x0E0116, T10ms = 0x0E05C4, T100ms = 0x0E075A, T1s = 0x0E08DA;
    const uint T10msReturn = 0x0E05C8, SerialTimer = 0xE3AE;                    // the 10 ms timers count down at E39E + 2 × id
    const int SerialTask = 0x08;
    const int CrankTask = 0x0D;
    const double HighRpm = 2_000;

    static readonly byte[] CallDispatcher = { 0xDA, 0x0B, 0xAE, 0xFB };          // CALLS 0Bh, FBAEh
    static readonly byte[] ServiceWatchdog = { 0xA7, 0x58, 0xA7, 0xA7 };         // SRVWDT
    static readonly byte[] NextTimer = { 0x0D, 0x04 };                            // JMPR cc_UC, 0E:05D2: back from a 10 ms task

    /// <summary>R2's periodic tasks: (kind, id) → period in ms.</summary>
    static readonly Dictionary<(string Kind, int Id), int> Periods = Build(
        ("FDEE", 5, new[] { 15 }),
        ("Q1", 5, new[] { 0x33 }),
        ("Q1", 10, new[] { 0x09, 0x19, 0x24, 0x25, 0x34, 0x35, 0x37 }),
        ("Q2", 10, new[] { 0x03, 0x04, 0x05, 0x0A, 0x1B, 0x1C, 0x26, 0x29, 0x2D, 0x32, 0x36 }),
        ("Q3", 20, new[] { 0x30 }),
        ("Q1", 50, new[] { 0x22 }),
        ("Q3", 100, new[] { 0x02, 0x06, 0x0B, 0x1D, 0x1F }),
        ("T100ms", 200, new[] { 0x23 }),
        ("Q3", 500, new[] { 0x18 }),
        ("T100ms", 500, new[] { 0x08 }),
        ("Q3", 1000, new[] { 0x07, 0x08, 0x1A, 0x1E, 0x27 }),
        ("T10ms", 10, new[] { 0x08 }));

    static Dictionary<(string, int), int> Build(params (string Kind, int Period, int[] Ids)[] groups) =>
        groups.SelectMany(g => g.Ids.Select(id => (Key: (g.Kind, id), g.Period))).ToDictionary(p => p.Key, p => p.Period);

    /// <summary>The hook sites hold the car file's bytes.</summary>
    public static bool Recognises(FirmwareImage image)
    {
        static bool Holds(FirmwareImage image, uint cpu, byte[] bytes) =>
            image.Bytes.Slice((int)(cpu & 0x7FFFF), bytes.Length).SequenceEqual(bytes);
        return new[] { Q1, Q2, Q3, Requests, T5ms, T10ms, T100ms, T1s }.All(site => Holds(image, site, CallDispatcher))
            && Holds(image, MainLoopService, ServiceWatchdog) && Holds(image, PowerDownService, ServiceWatchdog)
            && Holds(image, T10msReturn, NextTimer);
    }

    protected override void Start(Machine machine, int index)
    {
        if (!ProgramIdentity.HasMs43X001Ram(machine.Image.Image))
        {
            NotEvaluated("the program is not MS43X001 nor a known build on it (R15)");
            return;
        }
        if (!Recognises(machine.Image.Image))
        {
            NotEvaluated("the scheduler's hook sites (0E:0944, 0E:0976, 0E:09A8, 0E:0910, the four timer sites, 0E:05C8, 0E:0144, 0B:D9AC) are not the car file's");
            return;
        }
        window = new Window(this, machine, index);
    }

    Window? window;

    protected override void End(Machine machine, int index) => window?.Close();

    /// <summary>One power cycle's window.</summary>
    sealed class Window
    {
        readonly SchedulerCheck check;
        readonly Machine machine;
        readonly int index;
        readonly Dictionary<(string Kind, int Id), ulong> lastRun = new();
        readonly Dictionary<(string Kind, int Id), double> rpmAtRun = new();
        readonly Dictionary<(string Kind, int Id), int> armed = new();          // ms: the period a self-arming task set at its last run
        int fdf2Seen, fd84Seen;
        ulong aboveSince, aboveCycles;
        bool above;
        readonly HashSet<(string Kind, int Id)> overdue = new();
        readonly HashSet<(string Kind, int Id)> missed = new();
        bool open, closed, powerDown;
        ulong? lastService, lastCrankTask;
        double crankRpmAtLastRun;
        bool crankOverdue;
        ulong windowStart;                                                      // power-on, or the restart's first tooth
        bool engineStopped;

        public Window(SchedulerCheck check, Machine machine, int index)
        {
            this.check = check;
            this.machine = machine;
            this.index = index;
            machine.WatchdogServiced += Serviced;
            machine.Events.Added += e =>
            {
                if (e is ResetEvent { Kind: ResetKind.Watchdog or ResetKind.Software })
                    closed = true;
            };
            machine.OnInstruction(Q1, m => Ran("Q1", m.Cpu.R(10) >> 2));
            machine.OnInstruction(Q2, m => Ran("Q2", m.Cpu.R(10) >> 2));
            machine.OnInstruction(Q3, m => Ran("Q3", m.Cpu.R(10) >> 2));
            machine.OnInstruction(Requests, m => Ran("FDEE", 15 - m.Cpu.R(9)));
            machine.OnInstruction(T5ms, m => Ran("T5ms", m.Cpu.R(6) & 0xFF));
            machine.OnInstruction(T10ms, m => Ran("T10ms", m.Cpu.R(6) & 0xFF));
            machine.OnInstruction(T100ms, m => Ran("T100ms", m.Cpu.R(6) & 0xFF));
            machine.OnInstruction(T1s, m => Ran("T1s", m.Cpu.R(6) & 0xFF));
            machine.OnInstruction(T10msReturn, m =>
            {
                if ((m.Cpu.R(6) & 0xFF) == SerialTask)
                    armed[("T10ms", SerialTask)] = RunState.XramWord(m, SerialTimer) * 10;
            });
            Poll(5 * Ms);
            Flags(10 * Ms);
            machine.Scheduler.Schedule(0, "scheduler restart", () =>
            {
                if (check.Context.Engine is { } engine)
                    engine.PhaseChanged += phase => engineStopped |= phase == EnginePhase.Stopped;
                if (check.Context.Shaft is { } shaft)
                    shaft.ToothPassed += () =>
                    {
                        if (engineStopped)
                            Restart();
                    };
            });
        }

        /// <summary>The restart's first tooth: R2 and R3 start again as at power-on.</summary>
        void Restart()
        {
            engineStopped = false;
            windowStart = machine.Cycles;
            lastRun.Clear();
            rpmAtRun.Clear();
            overdue.Clear();
            missed.Clear();
            (lastCrankTask, crankOverdue) = (null, false);
        }

        bool Watching => open && !closed;

        void Serviced(uint address)
        {
            var now = machine.Cycles;
            if (address == MainLoopService && !powerDown)
            {
                open = true;
                check.Exercise();
                Gap(lastService, now, "the main loop's SRVWDT at 0E:0144");
                lastService = now;
            }
            else if (address == PowerDownService && open)
            {
                if (!powerDown)
                {
                    powerDown = true;                  // the power-down write: R2–R4 stop, R1 watches 0B:D9AC
                    lastService = null;
                }
                Gap(lastService, now, "the power-down write's SRVWDT at 0B:D9AC");
                lastService = now;
            }
        }

        void Gap(ulong? previous, ulong now, string what)
        {
            if (!Watching || previous is not { } before || now - before <= 15 * Ms)
                return;
            check.Find($"R1 {what}", $"R1: {what} was {(now - before) / (double)Ms:0.0} ms apart (the limit is 15 ms)",
                check.Context.PowerCycleStart + before, check.Context.PowerCycleStart + now);
        }

        void Ran(string kind, int id)
        {
            var now = machine.Cycles;
            var key = (kind, id);
            if (Watching && !powerDown)
            {
                if (Period(key) is > 0 and var period && lastRun.TryGetValue(key, out var before)
                    && now - before > Limit(period) && !overdue.Contains(key) && Judged(key))
                    Late(key, period, before, now);
                if (kind == "Q1" && id == CrankTask)
                    CrankRan(now);
            }
            lastRun[key] = now;
            rpmAtRun[key] = Rpm();
            overdue.Remove(key);
        }

        double Rpm() => check.Context.Shaft?.Rpm ?? 0;

        /// <summary>R2 judges a task's gap only with the shaft below 2,000 rpm at both of its ends.</summary>
        bool Judged((string Kind, int Id) key) => rpmAtRun.GetValueOrDefault(key) < HighRpm && Rpm() < HighRpm;

        static ulong Limit(int period) => (ulong)(2 * period + 5) * Ms;

        /// <summary>A task's period in ms: the one it armed at its last run, for a self-arming task, else its fixed one; 0 for none.</summary>
        int Period((string Kind, int Id) key) =>
            armed.TryGetValue(key, out var set) ? set : Periods.GetValueOrDefault(key);

        void Late((string Kind, int Id) key, int period, ulong before, ulong now) =>
            check.Find($"R2 {key.Kind}:{key.Id:X2}",
                $"R2: {key.Kind}:{key.Id:X2}h ({(armed.ContainsKey(key) ? "armed for" : "every")} {period} ms) went {(now - before) / (double)Ms:0.0} ms between runs (the limit is {2 * period + 5} ms)",
                check.Context.PowerCycleStart + before, check.Context.PowerCycleStart + now);

        void CrankRan(ulong now)
        {
            var rpm = check.Context.Shaft?.Rpm ?? 0;
            if (lastCrankTask is { } before && Synced() && rpm >= 150 && crankRpmAtLastRun >= 150)
            {
                var limit = CrankLimit(Math.Min(rpm, crankRpmAtLastRun));
                if (now - before > limit && !crankOverdue)
                    CrankLate(before, now, limit);
            }
            lastCrankTask = now;
            crankRpmAtLastRun = rpm;
            crankOverdue = false;
        }

        static ulong CrankLimit(double rpm) => (ulong)((3 * 20_000 / rpm + 5) * Ms);

        void CrankLate(ulong before, ulong now, ulong limit) =>
            check.Find("R3", $"R3: the crank task Q1:0Dh went {(now - before) / (double)Ms:0.0} ms between runs (the limit, three segments + 5 ms, was {limit / (double)Ms:0.0} ms)",
                check.Context.PowerCycleStart + before, check.Context.PowerCycleStart + now);

        bool Synced() => (machine.Bus.Peek16(0xF900) & 0xFF) == 2;

        /// <summary>Every 5 ms: a task overdue now (it stopped), a first run missed, the crank task stopped.</summary>
        void Poll(ulong at) => machine.Scheduler.Schedule(at, "scheduler check", () =>
        {
            if (Watching && !powerDown)
            {
                var now = machine.Cycles;
                foreach (var (key, fixedPeriod) in Periods)
                {
                    var period = Period(key);
                    if (lastRun.TryGetValue(key, out var before))
                    {
                        if (period > 0 && now - before > Limit(period) && Judged(key) && overdue.Add(key))
                            Late(key, period, before, now);
                    }
                    else if (now > windowStart + (ulong)(2 * fixedPeriod + 1100) * Ms && missed.Add(key))
                        check.Find($"R2 {key.Kind}:{key.Id:X2} first",
                            $"R2: {key.Kind}:{key.Id:X2}h (every {fixedPeriod} ms) had not run {2 * fixedPeriod + 1100} ms after {(windowStart == 0 ? "power-on" : "the restart's first tooth")}");
                }
                var rpm = check.Context.Shaft?.Rpm ?? 0;
                Above(now, rpm >= HighRpm);
                if (lastCrankTask is { } crank && Synced() && rpm >= 150 && crankRpmAtLastRun >= 150 && !crankOverdue)
                {
                    var limit = CrankLimit(Math.Min(rpm, crankRpmAtLastRun));
                    if (now - crank > limit)
                    {
                        crankOverdue = true;
                        CrankLate(crank, now, limit);
                    }
                }
            }
            Poll(at + 5 * Ms);
        });

        /// <summary>R4, every 10 ms.</summary>
        void Flags(ulong at) => machine.Scheduler.Schedule(at, "scheduler flags", () =>
        {
            if (Watching && !powerDown)
            {
                var fdf2 = (int)machine.Bus.Peek16(0xFDF2);
                var fd84 = machine.Bus.Peek16(0xFD84) & 0x01C0;
                var rpm = Rpm();
                if ((fdf2 & ~fdf2Seen) != 0)
                    Flag("FDF2", $"FDF2 = {fdf2:X4}h, a request came while still pending", rpm);
                if ((fd84 & ~fd84Seen) != 0)
                    Flag("FD84", $"FD84 & 01C0h = {fd84:X4}h, a task queue overflowed", rpm);
                (fdf2Seen, fd84Seen) = (fdf2, fd84);
            }
            Flags(at + 10 * Ms);
        });

        /// <summary>Counts the time the shaft spends at 2,000 rpm or more inside the window, for the report.</summary>
        void Above(ulong now, bool high)
        {
            if (high && !above)
                aboveSince = now;
            else if (!high && above)
                aboveCycles += now - aboveSince;
            above = high;
        }

        /// <summary>The window's end: the time above 2,000 rpm, where R2 and R4 were not judged, goes to the report.</summary>
        public void Close()
        {
            Above(machine.Cycles, false);
            if (aboveCycles > 0)
                check.Gap($"scheduler: R2 and R4 were not judged for {Seconds(aboveCycles)} s of power cycle {index + 1}, with the shaft at 2,000 rpm or more: at the ISM's minimum timing the car's own tasks stretch there and its lowest queue can wait seconds; whether the real ECU is as loaded is unknown (a car probe, declined, would settle it) [Task 11]");
        }

        void Flag(string word, string what, double rpm)
        {
            if (rpm < HighRpm)
                check.Find($"R4 {word}", $"R4: {what} (power cycle {index + 1}, at {rpm:0} rpm)");
            else
                check.Gap($"scheduler: {what} at {rpm:0} rpm (power cycle {index + 1}, {Seconds(check.Context.PowerCycleStart + machine.Cycles)} s): at the ISM's minimum timing the car's own full-load pull does this; whether the real ECU is as loaded is unknown (a car probe, declined, would settle it) [Task 11]");
        }

        static string Seconds(ulong cycle) => (cycle / 24e6).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }
}
