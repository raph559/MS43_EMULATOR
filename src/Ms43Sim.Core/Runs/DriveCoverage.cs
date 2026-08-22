using System.Globalization;

namespace Ms43Sim.Core;

/// <summary>
/// Builds a drive's <see cref="Coverage"/> (design §7, plan 4 Task 10). Benign, per R4: the read of
/// ESFR F024h (XPERCON, plain storage on a C167CR), the byte writes to physical 0x4286C (plan 2's write
/// beyond the 8 KiB RAM window; a real flash ignores a lone write), the 3Bh frames after a key-off (the
/// after-run's shutdown handshake) and the detected faults <see cref="FaultMemory.Explanation"/> explains.
/// Everything else the run touched that is not modelled, and every stored fault not on
/// <see cref="AllowedFaults"/>, makes the run incomplete.
/// </summary>
internal static class DriveCoverage
{
    /// <summary>Stored faults excused for one program and drive when a condition was observed in the same run (Task 11 may add).</summary>
    internal sealed record AllowedFault(string Identity, string Drive, int Path, Func<DriveResult, int, string?> Observed);

    internal static readonly IReadOnlyList<AllowedFault> AllowedFaults = Array.Empty<AllowedFault>();

    /// <summary>A drive's streaming tally of what the event log may not keep: flash writes, unsupported and locked writes, 3Bh frames.</summary>
    internal sealed class Watch
    {
        public readonly Dictionary<(uint Address, uint Pc), long> BenignFlashWrites = new();
        public readonly Dictionary<string, long> Unmodelled = new();
        public long ShutdownFrames;

        public void Attach(DriveContext context) => context.PowerCycleStarted += (machine, index) =>
        {
            var keyWentOff = false;
            machine.Events.Added += e =>
            {
                switch (e)
                {
                    case FlashWriteEvent write when BenignAccesses.IsBenignFlashWrite(write):
                        BenignFlashWrites[(write.Address, write.Pc)] = BenignFlashWrites.GetValueOrDefault((write.Address, write.Pc)) + 1;
                        break;
                    case FlashWriteEvent write:
                        Count($"a flash write of {write.Value:X4}h to physical {write.Address:X5}h{(write.IsByte ? " (byte)" : "")} from {Cpu(write.Pc)}");
                        break;
                    case UnsupportedEvent unsupported:
                        Count($"unsupported: {unsupported.What}");
                        break;
                    case ConfigLockedWriteEvent locked:
                        Count($"a write of {locked.Value:X4}h to SYSCON after EINIT, ignored, from {Cpu(locked.Pc)}");
                        break;
                }
            };
            machine.Board.Monitor.CpuFrameReceived += frame =>
            {
                keyWentOff |= !CarControls.KeyIsOn(machine.Board);
                if (frame[0] == 0x3B && keyWentOff)
                    ShutdownFrames++;
            };
        };

        void Count(string what) => Unmodelled[what] = Unmodelled.GetValueOrDefault(what) + 1;
    }

    internal static Coverage Of(DriveResult r, Watch watch)
    {
        var benign = new List<string>();
        var unmodelled = new List<string>();
        foreach (var access in r.Inventory.GroupBy(a => (a.Area, a.Address, a.Kind)))
        {
            var first = access.First();
            var count = access.Sum(a => a.Count);
            var line = $"{first.Area} {first.Address:X4}h {first.Name} {first.Kind.ToString().ToLowerInvariant()} ×{count:N0}, first at {Cpu(first.FirstPc)}";
            if (first is { Area: AccessArea.Esfr, Address: 0xF024, Kind: AccessKind.Read })
                benign.Add($"{line}: XPERCON, plain storage on a C167CR (plan 1)");
            else
                unmodelled.Add(line);
        }
        foreach (var ((address, pc), count) in watch.BenignFlashWrites.OrderBy(w => w.Key.Address))
            benign.Add($"{count:N0} byte writes to physical flash {address:X5}h from {Cpu(pc)}: a write beyond the 8 KiB RAM window; a real flash ignores a lone write (R4)");
        unmodelled.AddRange(watch.Unmodelled.Select(u => $"{u.Key} ×{u.Value:N0}"));
        if (watch.ShutdownFrames > 0)
            benign.Add($"{watch.ShutdownFrames:N0} 3Bh frames after a key-off: the after-run's shutdown handshake (R4)");

        var unexplained = new List<string>();
        var identity = ProgramIdentity.Of(r.Image.Image);
        foreach (var cycle in r.PowerCycles)
        {
            foreach (var fault in cycle.Faults.Stored)
            {
                var excused = AllowedFaults
                    .Where(a => a.Identity == identity && a.Drive == r.Drive.Name && a.Path == fault.Path)
                    .Select(a => a.Observed(r, cycle.Index)).FirstOrDefault(why => why != null);
                if (excused != null)
                    benign.Add($"power cycle {cycle.Index + 1}: {Describe(fault)} stored, excused: {excused}");
                else
                    unexplained.Add($"power cycle {cycle.Index + 1}: {Describe(fault)} stored");
            }
            var maxRpm = r.Trace.Where(t => t.Seconds * 24e6 >= cycle.StartCycle && t.Seconds * 24e6 <= cycle.EndCycle)
                .Select(t => t.Values[0] ?? 0).DefaultIfEmpty(0).Max();
            foreach (var fault in cycle.Faults.Detected.Where(f => !cycle.Faults.Stored.Any(s => s.Path == f.Path)))
            {
                if ((FaultMemory.Explanation(fault, cycle.FaultContext) ?? FaultMemory.ExplanationAtSpeed(fault, maxRpm)) is { } why)
                    benign.Add($"power cycle {cycle.Index + 1}: {Describe(fault)} detected: {why}");
                else
                    unexplained.Add($"power cycle {cycle.Index + 1}: {Describe(fault)} detected");
            }
        }

        var checks = r.Checks.OfType<CheckBase>().ToArray();
        benign.AddRange(checks.SelectMany(c => c.Notes));
        return new Coverage(r.Stubs,
            checks.Where(c => c.Status == CheckStatus.NotEvaluated).Select(c => $"{c.Name}: {c.NotEvaluatedReason}").ToArray(),
            checks.Where(c => c.Status == CheckStatus.Pass && !c.Exercised).Select(c => c.Name).ToArray(),
            unmodelled, unexplained, benign)
        {
            NotRun = NotRun(r),
            NotJudged = checks.SelectMany(c => c.Gaps).ToArray(),
        };
    }

    /// <summary>
    /// The drive's time after a main-relay drop with the key on, to its next key-on or its end (final
    /// review I3), and an engine the drive never cranks: nothing judged a start, a spark or an injection (I6).
    /// </summary>
    static string[] NotRun(DriveResult r)
    {
        var lines = new List<string>();
        var end = (ulong)Math.Round(DriveTimeline.Duration(r.Drive) * 24e6);
        for (var i = 0; i < r.PowerCycles.Count; i++)
        {
            var cycle = r.PowerCycles[i];
            if (cycle.End is not (CycleEnd.RelayDroppedKeyOn or CycleEnd.RelayDroppedKeyReadOff))
                continue;
            var next = i + 1 < r.PowerCycles.Count;
            var resumed = next ? r.PowerCycles[i + 1].StartCycle : end;
            if (resumed == cycle.EndCycle)
                continue;                                    // the firmware reset itself and the drive went on there: nothing was left unrun
            var read = cycle.End == CycleEnd.RelayDroppedKeyOn ? "the firmware read the key on" : "the firmware read the key off: AN1 forced";
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{cycle.EndCycle / 24e6:0.000}–{resumed / 24e6:0.000} s: the main relay dropped while the drive had the key on ({read}); the drive {(next ? "ran again from its next key-on" : "did not run again")}"));
        }
        if (!r.Drive.Segments.Any(s => s.Mode == DriveMode.Crank))
            lines.Add("the engine: the drive never cranks it, so no check judged a start, a spark or an injection");
        return lines.ToArray();
    }

    static string Describe(FaultEntry f) =>
        $"{f.Label} (path {f.Path}, DS2 {f.Ds2Code:X2}h, {f.PCode}, type {f.Type})";

    static string Cpu(uint address) => string.Create(CultureInfo.InvariantCulture, $"{address >> 16:X2}:{address & 0xFFFF:X4}");
}
