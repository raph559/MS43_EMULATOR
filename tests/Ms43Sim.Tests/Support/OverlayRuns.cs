using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>
/// A live table read as its reader's first instruction saw it (plan 6): the drive cycle, the reader (0D:029A), R12/R13,
/// and the return address the site's CALLS left on the stack (the site + 4).
/// </summary>
public sealed record TableRead(ulong Cycle, uint Reader, ushort R12, ushort R13, uint Return)
{
    /// <summary>The live table whose site made the read.</summary>
    public OverlayTable Table => LiveOverlay.Tables.First(t => t.Sites.Any(s => Return == 0x80000 + (uint)s + 4));

    /// <summary>The read went to a copy in the window (page 10h) rather than to the flash.</summary>
    public bool FromCopy => R13 == 0x10;

    public double Seconds => Cycle / 24e6;
}

/// <summary>A 10 ms sample of an overlay run: the drive cycle, the running signal (D3) and the tables' states in the window.</summary>
/// <summary>A data write made with the PC in the overlay (plan 6 Task 8).</summary>
public sealed record OverlayWrite(ulong Cycle, uint Pc, uint Address, ushort R0, ushort Sp, ushort Cp);

public sealed record OverlayRunSample(ulong Cycle, bool Running, OverlayState[] States);

/// <summary>
/// One observed run with the overlay's K-line traffic: the result, every live table read, and the window after each 0x6D
/// request the dispatcher handled.
/// </summary>
public sealed record OverlayRun(DriveResult Result, IReadOnlyList<TableRead> Reads, IReadOnlyList<(ulong Cycle, OverlayWindow Window)> Windows)
{
    /// <summary>
    /// Every 10 ms from each power cycle's start, a sample whenever the running signal (F900 = 2 and FD2C bits 9–12) or a
    /// table's state in the window changed since the last one.
    /// </summary>
    public IReadOnlyList<OverlayRunSample> Samples { get; init; } = Array.Empty<OverlayRunSample>();

    /// <summary>
    /// With <c>watchWindow</c>: every byte the reader (0D:029A) read from the window, as its offset in the copy area, with
    /// the table whose site called it (the last reader entry before it).
    /// </summary>
    public IReadOnlyList<(ulong Cycle, int Offset, OverlayTable Table)> WindowReads { get; init; } = Array.Empty<(ulong, int, OverlayTable)>();

    /// <summary>
    /// With <c>watchWrites</c>: every data write made with the PC in the overlay's block (0C:B544–0C:BFFF), with its physical
    /// address, R0, SP and CP as they were. An interrupt entry or a PEC transfer between two of its instructions is not the
    /// overlay's.
    /// </summary>
    public IReadOnlyList<OverlayWrite> OverlayWrites { get; init; } = Array.Empty<OverlayWrite>();

    /// <summary>The 0x6D exchanges, in order.</summary>
    public IReadOnlyList<KLineExchange> Overlay =>
        Result.KLine.Where(e => e.Request.Count > 2 && e.Request[2] == LiveOverlay.Command).ToList();

    /// <summary>The last window seen at or before <paramref name="seconds"/>.</summary>
    public OverlayWindow? WindowAt(double seconds) =>
        Windows.LastOrDefault(w => w.Cycle / 24e6 <= seconds) is { Window: { } window } ? window : null;
}

/// <summary>Runs drives on an image with the overlay's observers: they only read registers and memory.</summary>
public static class OverlayRuns
{
    /// <summary>A warm engine from a blank EEPROM: the head of the drives the overlay tests build.</summary>
    public const string Warm = "start blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n";

    const uint Reader = 0x0D029A;

    public static OverlayRun Run(FlashedImage image, string driveText, byte[]? eeprom = null, bool checks = false, bool watchWindow = false,
        bool watchWrites = false)
    {
        var overlayWrites = new List<OverlayWrite>();
        var windowReads = new List<(ulong, int, OverlayTable)>();
        var reads = new List<TableRead>();
        var windows = new List<(ulong, OverlayWindow)>();
        var samples = new List<OverlayRunSample>();
        void Attach(DriveContext context) => context.PowerCycleStarted += (machine, _) =>
        {
            var origin = context.PowerCycleStart;
            void Sample(ulong at) => machine.Scheduler.Schedule(at, "overlay-sample", () =>
            {
                var running = (machine.Bus.Peek16(0xF900) & 0xFF) == 2 && (machine.Bus.Peek16(0xFD2C) & 0x1E00) != 0;
                var states = LiveOverlay.Tables.Select(t => (OverlayState)machine.Bus.ExternalRam[0x1D08 + 6 * t.Index]).ToArray();
                if (samples.Count == 0 || samples[^1].Running != running || !samples[^1].States.SequenceEqual(states))
                    samples.Add(new OverlayRunSample(origin + machine.Cycles, running, states));
                Sample(at + 240_000);
            });
            Sample(0);
            machine.OnInstruction(Reader, m =>
            {
                var back = (uint)m.Bus.Peek16((uint)(m.Cpu.Sp + 2)) << 16 | m.Bus.Peek16(m.Cpu.Sp);
                if (LiveOverlay.Tables.Any(t => t.Sites.Any(s => back == 0x80000 + (uint)s + 4)))
                    reads.Add(new TableRead(origin + m.Cycles, Reader, m.Cpu.R(12), m.Cpu.R(13), back));
            });
            if (watchWindow || watchWrites)
            {
                machine.Bus.DataWatch = (address, write) =>
                {
                    var pc = machine.Cpu.InstructionAddress;
                    if (watchWrites && write && pc is >= 0x0CB544 and < 0x0CC000 && !machine.Cpu.LastStepWasEntry)
                        overlayWrites.Add(new OverlayWrite(origin + machine.Cycles, pc, address, machine.Cpu.R(0), (ushort)machine.Cpu.Sp, machine.Cpu.Cp));
                    if (watchWindow && !write && pc is >= Reader and < 0x0D0358
                        && machine.Bus.Decode(address) == Region.ExternalRam && reads.Count > 0)
                        windowReads.Add((origin + machine.Cycles, (int)(address & 0x1FFF) - (LiveOverlay.CopyAddress - 0x8000), reads[^1].Table));
                };
            }
            // One window per dispatcher call. A hook runs as the CPU reaches its instruction, and again if an interrupt was
            // taken there first, so the return point alone would record a request twice; the call site arms it.
            var dispatched = false;
            foreach (var call in new uint[] { 0x09FAD2, 0x09FB00 })                 // the serial-line task calls the dispatcher
                machine.OnInstruction(call, _ => dispatched = true);
            foreach (var back in new uint[] { 0x09FAD6, 0x09FB04 })                 // and is back from it
            {
                machine.OnInstruction(back, m =>
                {
                    if (!dispatched)
                        return;
                    dispatched = false;
                    if (m.Bus.Peek16(0xF7CC) >> 8 == LiveOverlay.Command)          // F7CD: the command it dispatched
                        windows.Add((origin + m.Cycles, LiveOverlay.ReadWindow(m.Bus.ExternalRam)));
                });
            }
        };
        var drive = DriveParser.Parse(driveText);
        var options = new DriveRunOptions(Eeprom: eeprom, Observers: new Action<DriveContext>[] { Attach });
        var result = checks ? DriveRun.RunWithChecks(image, drive, options) : DriveRun.Run(image, drive, options);
        return new OverlayRun(result, reads, windows) { WindowReads = windowReads, Samples = samples, OverlayWrites = overlayWrites };
    }
}
