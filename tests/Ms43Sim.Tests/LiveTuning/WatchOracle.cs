using Ms43Sim.Core;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>One read of the watched map as the library saw it, from the CPU state at the hook site.</summary>
public sealed record WatchRead(ulong Cycle, byte Kind, ushort Row, ushort RowF, ushort Col, ushort ColF, byte Cols)
{
    /// <summary>The record bytes in the wire order, with <paramref name="count"/>.</summary>
    public byte[] Wire(byte count) =>
        [Kind, count, (byte)(Row >> 8), (byte)Row, (byte)(RowF >> 8), (byte)RowF, (byte)(Col >> 8), (byte)Col,
         (byte)(ColF >> 8), (byte)ColF, Cols];
}

/// <summary>
/// An independent observer of every library read of one flash address: the 8 reader entries and the 16 descriptor
/// lookups' MOV DPP0 sites (the plan's Reference facts), plus the V9 handler's copy of the record and each watch set.
/// It only reads registers and memory.
/// </summary>
public sealed class WatchOracle
{
    internal static readonly (uint At, bool Map)[] Readers =
    [
        (0xD022A, false), (0xD0262, false), (0xD0454, false), (0xD042E, false),
        (0xD0168, true), (0xD029A, true), (0xD03D2, true), (0xD043A, true),
    ];

    internal static readonly (uint At, bool Map)[] Descriptors =
    [
        (0xD04B0, true), (0xD05C8, true), (0xD0776, true), (0xD089E, true), (0xD09BC, true), (0xD0B60, true),
        (0xD0C88, true), (0xD0EC2, true), (0xD1092, true),
        (0xD06E4, false), (0xD0AD6, false), (0xD0DA4, false), (0xD0E30, false), (0xD0FE2, false), (0xD1018, false),
        (0xD1052, false),
    ];

    readonly ushort page, offset;

    public WatchOracle(uint address) => (page, offset) = ((ushort)(address / 0x4000), (ushort)(address % 0x4000));

    public List<WatchRead> Reads { get; } = new();

    /// <summary>The cycles the handler started copying the record into a log reply (wl_rec).</summary>
    public List<ulong> Copies { get; } = new();

    /// <summary>The cycles a watch was set (watch_set's entry): the count restarts there.</summary>
    public List<ulong> Sets { get; } = new();

    public void Attach(DriveContext context) => context.PowerCycleStarted += (machine, _) =>
    {
        var origin = context.PowerCycleStart;
        static byte B(Machine m, uint a) => (byte)(m.Bus.Peek16(a & ~1u) >> (int)(a & 1) * 8);
        // A hook runs before the CPU's step, and that step may enter an interrupt or a PEC transfer instead: the instruction
        // then runs later, and the hook runs again. A firing right after such an entry or a RETI, at the stack depth of this
        // site's last firing, is that same read.
        var last = new Dictionary<uint, (int Sp, ushort R0)>();
        bool Again(Machine m, uint at)
        {
            var now = ((int)m.Cpu.Sp, m.Cpu.R(0));
            // The step before was an entry taken here (an interrupt or a PEC transfer), or the RETI back to here.
            var reentered = (m.Cpu.LastStepWasEntry && m.Cpu.InstructionAddress == at) || m.Bus.Peek16(m.Cpu.InstructionAddress) == 0x88FB;
            var again = reentered && last.TryGetValue(at, out var before) && before == now;
            last[at] = now;
            return again;
        }
        foreach (var (at, map) in Readers)
            machine.OnInstruction(at, m =>
            {
                if (m.Cpu.R(13) != page || m.Cpu.R(12) != offset || Again(m, at)) return;
                Reads.Add(new WatchRead(origin + m.Cycles, map ? (byte)1 : (byte)2, B(m, 0xFB6B), m.Bus.Peek16(0xE390),
                    map ? B(m, 0xFB6C) : (ushort)0, map ? m.Bus.Peek16(0xE392) : (ushort)0, map ? B(m, 0xFB6D) : (byte)0));
            });
        foreach (var (at, map) in Descriptors)
            machine.OnInstruction(at, m =>
            {
                if (m.Cpu.R(12) != page || m.Cpu.R(10) != offset || Again(m, at)) return;
                Reads.Add(new WatchRead(origin + m.Cycles, map ? (byte)3 : (byte)4, m.Cpu.R(13), 0, map ? m.Cpu.R(14) : (ushort)0, 0, 0));
            });
        machine.OnInstruction((uint)V10Fixture.Symbol("wl_rec"), m => Copies.Add(origin + m.Cycles));
        machine.OnInstruction((uint)V10Fixture.Symbol("watch_set"), m => Sets.Add(origin + m.Cycles));
    };

    /// <summary>The record a copy at <paramref name="cycle"/> must carry: the last read before it, and the reads since the last set.</summary>
    public byte[] Expected(ulong cycle)
    {
        var since = Sets.Where(s => s < cycle).DefaultIfEmpty(0UL).Max();
        var reads = Reads.Where(r => r.Cycle > since && r.Cycle < cycle).ToList();
        return reads.Count == 0 ? new byte[11] : reads[^1].Wire((byte)reads.Count);
    }
}
