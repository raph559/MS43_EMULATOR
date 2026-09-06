using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>
/// A live table read as its reader's first instruction saw it (plan 6): the drive cycle, the reader (0D:029A or 0D:0168),
/// R12/R13, and the return address the site's CALLS left on the stack (the site + 4).
/// </summary>
public sealed record TableRead(ulong Cycle, uint Reader, ushort R12, ushort R13, uint Return)
{
    /// <summary>The live table whose site made the read.</summary>
    public OverlayTable Table => LiveOverlay.Tables.First(t => t.Sites.Any(s => Return == 0x80000 + (uint)s + 4));

    /// <summary>The read went to a copy in the window (page 10h) rather than to the flash.</summary>
    public bool FromCopy => R13 == 0x10;

    public double Seconds => Cycle / 24e6;
}

/// <summary>
/// One observed run with the overlay's K-line traffic: the result, every live table read, every K the injection computation
/// was given (R14 at 0C:AB34, the CALLS 0Bh, FCBAh after the K read), and the window after each 0x6D request the dispatcher
/// handled.
/// </summary>
public sealed record OverlayRun(DriveResult Result, IReadOnlyList<TableRead> Reads, IReadOnlyList<(ulong Cycle, ushort K)> KReads,
    IReadOnlyList<(ulong Cycle, OverlayWindow Window)> Windows)
{
    /// <summary>
    /// With <c>watchWindow</c>: every byte a reader (0D:029A, 0D:0168) read from the window, as its offset in the copy area,
    /// with the table whose site called it (the last reader entry before it).
    /// </summary>
    public IReadOnlyList<(ulong Cycle, int Offset, OverlayTable Table)> WindowReads { get; init; } = Array.Empty<(ulong, int, OverlayTable)>();

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

    const uint KCall = 0x0CAB34;                                          // CALLS 0Bh, FCBAh: R14 holds the K the computation uses

    public static OverlayRun Run(FlashedImage image, string driveText, byte[]? eeprom = null, bool checks = false, bool watchWindow = false)
    {
        var windowReads = new List<(ulong, int, OverlayTable)>();
        var reads = new List<TableRead>();
        var kReads = new List<(ulong, ushort)>();
        var windows = new List<(ulong, OverlayWindow)>();
        void Attach(DriveContext context) => context.PowerCycleStarted += (machine, _) =>
        {
            var origin = context.PowerCycleStart;
            foreach (var reader in new uint[] { 0x0D029A, 0x0D0168 })
            {
                machine.OnInstruction(reader, m =>
                {
                    var back = (uint)m.Bus.Peek16((uint)(m.Cpu.Sp + 2)) << 16 | m.Bus.Peek16(m.Cpu.Sp);
                    if (LiveOverlay.Tables.Any(t => t.Sites.Any(s => back == 0x80000 + (uint)s + 4)))
                        reads.Add(new TableRead(origin + m.Cycles, reader, m.Cpu.R(12), m.Cpu.R(13), back));
                });
            }
            machine.OnInstruction(KCall, m => kReads.Add((origin + m.Cycles, m.Cpu.R(14))));
            if (watchWindow)
            {
                machine.Bus.DataWatch = (address, write) =>
                {
                    var pc = machine.Cpu.InstructionAddress;
                    if (!write && (pc is >= 0x0D029A and < 0x0D0358 or >= 0x0D0168 and < 0x0D022A)
                        && machine.Bus.Decode(address) == Region.ExternalRam && reads.Count > 0)
                        windowReads.Add((origin + machine.Cycles, (int)(address & 0x1FFF) - (LiveOverlay.CopyAddress - 0x8000), reads[^1].Table));
                };
            }
            foreach (var back in new uint[] { 0x09FAD6, 0x09FB04 })                 // the serial-line task, back from the dispatcher
            {
                machine.OnInstruction(back, m =>
                {
                    if (m.Bus.Peek16(0xF7CC) >> 8 == LiveOverlay.Command)          // F7CD: the command it dispatched
                        windows.Add((origin + m.Cycles, LiveOverlay.ReadWindow(m.Bus.ExternalRam)));
                });
            }
        };
        var drive = DriveParser.Parse(driveText);
        var options = new DriveRunOptions(Eeprom: eeprom, Observers: new Action<DriveContext>[] { Attach });
        var result = checks ? DriveRun.RunWithChecks(image, drive, options) : DriveRun.Run(image, drive, options);
        return new OverlayRun(result, reads, kReads, windows) { WindowReads = windowReads };
    }
}
