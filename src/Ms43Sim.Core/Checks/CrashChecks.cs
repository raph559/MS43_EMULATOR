namespace Ms43Sim.Core;

/// <summary>
/// The checks that say whether the program runs at all (plan 4 R16):
/// <list type="bullet">
/// <item>traps: any trap; a stack overflow or underflow is named;</item>
/// <item>resets: any watchdog or software reset (repeats are the reset loop);</item>
/// <item>execution: an instruction fetched from erased flash, or from outside flash;</item>
/// <item>boot: a power cycle with the key on that has not reached the handover (0E:366A) 1 s after power-on;</item>
/// <item>checksums: read 10 s after the main loop started with the key on and at the power cycle's
/// end: FD26 &amp; 00E0h ≠ 0 (a checksum failed), E7E9 = 08h or 0Ah (the last error), F8F4 &amp; 18C6h ≠ 0
/// once the three tasks have finished (F8F4 reads 0842h while they run), FD7E.8 once the monitoring
/// sum's first pass is done (FD7E.9 clear); and at the 10 s mark, FD26 &amp; 001Ch ≠ 001Ch (the three
/// checksum tasks not all finished);</item>
/// <item>monitoring: the module counts an unexpected answer; or the CPU sends 3Bh frames while the
/// key is on, before this power cycle's first key-off.</item>
/// </list>
/// boot needs the car file's checkpoints and checksums the MS43X001 program (R15); otherwise they are
/// not evaluated.
/// </summary>
public static class CrashChecks
{
    public static IEnumerable<CheckBase> All() =>
        new CheckBase[] { new Traps(), new Resets(), new Execution(), new Boot(), new Checksums(), new Monitoring() };

    sealed class Traps() : CheckBase("traps")
    {
        protected override void Start(Machine machine, int index) => machine.Events.Added += e =>
        {
            if (e is TrapEvent trap)
                Find($"{trap.Kind}@{trap.Pc:X}", $"{Describe(trap.Kind)} at {Address(trap.Pc)}");
        };

        static string Describe(TrapKind kind) => kind switch
        {
            TrapKind.StackOverflow => "a stack overflow",
            TrapKind.StackUnderflow => "a stack underflow",
            _ => $"a {kind} trap",
        };
    }

    sealed class Resets() : CheckBase("resets")
    {
        protected override void Start(Machine machine, int index) => machine.Events.Added += e =>
        {
            if (e is ResetEvent { Kind: ResetKind.Watchdog or ResetKind.Software } reset)
                Find($"{reset.Kind}", reset.Kind == ResetKind.Watchdog
                    ? $"a watchdog reset, first at {Address(reset.Pc)} (repeats are a reset loop)"
                    : $"a software reset (SRST), first at {Address(reset.Pc)}");
        };
    }

    sealed class Execution() : CheckBase("execution")
    {
        protected override void Start(Machine machine, int index) => machine.Events.Added += e =>
        {
            switch (e)
            {
                case ErasedFlashExecutionEvent erased:
                    Find($"erased@{erased.Pc:X}", $"an instruction fetched from erased flash at {Address(erased.Pc)}");
                    break;
                case CodeOutsideFlashEvent outside:
                    Find($"outside@{outside.Pc:X}", $"an instruction fetched outside flash at {Address(outside.Pc)}");
                    break;
            }
        };
    }

    sealed class Boot() : CheckBase("boot")
    {
        const ulong Limit = 24_000_000;                    // 1 s

        protected override void Start(Machine machine, int index)
        {
            if (!RunState.Recognises(machine.Image.Image))
            {
                NotEvaluated("the program's checkpoints (00:2EFC, 0E:366A, 0E:36BC, 0E:3700, 0E:3812, 0E:0094, 0E:0144) are not the car file's");
                return;
            }
            ulong? handover = null;
            machine.OnInstruction(RunState.Handover, m => handover ??= m.Cycles);
            machine.Scheduler.Schedule(Limit, "boot check", () =>
            {
                if (handover is null && CarControls.KeyIsOn(machine.Board))
                    Find($"cycle {index}", $"power cycle {index + 1}: no handover (0E:366A) 1 s after power-on");
            });
        }
    }

    sealed class Checksums() : CheckBase("checksums")
    {
        const ulong FinishBy = 240_000_000;                         // 10 s
        bool readable;

        protected override void Start(Machine machine, int index)
        {
            readable = ProgramIdentity.IsMs43X001(machine.Image.Image);
            if (!readable)
            {
                NotEvaluated("the program is not MS43X001 (R15), so its RAM is not the car file's");
                return;
            }
            var mainLoop = false;
            machine.OnInstruction(RunState.MainLoop, m =>
            {
                if (mainLoop)
                    return;
                mainLoop = true;
                m.Scheduler.Schedule(m.Cycles + FinishBy, "checksum finish check", () =>
                {
                    if (!CarControls.KeyIsOn(m.Board))
                        return;
                    var fd26 = m.Bus.Peek16(0xFD26);
                    if ((fd26 & 0x001C) != 0x001C)
                        Find("unfinished", $"the checksum tasks had not all finished 10 s after the main loop started (FD26 = {fd26:X4}h)");
                    Look(m);
                });
            });
        }

        protected override void End(Machine machine, int index)
        {
            if (readable)
                Look(machine);
        }

        void Look(Machine m)
        {
            var fd26 = m.Bus.Peek16(0xFD26);
            var f8f4 = m.Bus.Peek16(0xF8F4);
            var fd7e = m.Bus.Peek16(0xFD7E);
            var e7e9 = RunState.Xram(m, 0xE7E9);
            if ((fd26 & 0x00E0) != 0)
                Find("fd26", $"a checksum failed: FD26 = {fd26:X4}h (bits 5-7: boot, calibration, program)");
            if ((fd26 & 0x001C) == 0x001C && (f8f4 & 0x18C6) != 0)
                Find("f8f4", $"the CRC task reports a failure: F8F4 = {f8f4:X4}h");
            if ((fd7e & 0x0200) == 0 && (fd7e & 0x0100) != 0)
                Find("fd7e", "the monitoring sum does not match (FD7E.8)");
            if (e7e9 is 0x08 or 0x0A)
                Find($"e7e9 {e7e9:X2}", e7e9 == 0x0A
                    ? "the last error is the calibration CRC (E7E9 = 0Ah)"
                    : "the last error is the monitoring sum (E7E9 = 08h)");
        }
    }

    sealed class Monitoring() : CheckBase("monitoring")
    {
        protected override void Start(Machine machine, int index)
        {
            var unexpected = 0;
            var keyOffSeen = false;
            var monitor = machine.Board.Monitor;
            machine.Events.Added += e =>
            {
                if (e is MonitoringEvent seen && monitor.UnexpectedAnswers > unexpected)
                {
                    unexpected = monitor.UnexpectedAnswers;
                    Find("unexpected", $"the monitoring module counts an unexpected answer: {seen.What}");
                }
            };
            monitor.CpuFrameReceived += frame =>
            {
                var on = CarControls.KeyIsOn(machine.Board);
                keyOffSeen |= !on;
                if (frame[0] == 0x3B && on && !keyOffSeen)
                    Find("3B", "the CPU sends 3Bh frames (its shutdown or degraded state) with the key on, before any key-off");
            };
        }
    }
}
