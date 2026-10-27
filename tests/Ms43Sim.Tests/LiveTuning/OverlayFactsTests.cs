using Ms43Sim.Core;
using Ms43Sim.Tests.Drives;
using Ms43Sim.Tests.Support;
using Xunit.Abstractions;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// What the car file does that the live-tuning overlay leans on (plan 6 Task 1, R1–R7), observed and never forced: the
/// seven standard drives, the stall drive and a quick key cycle, each run once on the car file as flashed, with a probe
/// that reads the bus's data accesses, the readers' entries, the seven sites' interrupt level, the 10 ms tick counter and
/// the engine-running signal.
/// </summary>
public sealed class OverlayFacts
{
    public OverlayFacts()
    {
        const string Warm = "start blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n3 on\n1.5 crank rpm=200\n";
        var warm = Probe.Run(StandardDrives.Load("warm-start"));
        var seasoned = warm.Result.Eeprom.FinalImage;
        var others = new Func<Probe>[]
        {
            () => Probe.Run(DriveParser.Parse(RestartRuns.Head + RestartRuns.Body)),
            () => Probe.Run(DriveParser.Parse("drive quick\n" + Warm + "10 idle\n" + QuickKeyCycleRuns.Body(1.0))),
            () => Probe.Run(DriveParser.Parse("drive session\n" + Warm + "5 idle\nkline connect\nkline poll\nkline every 1 6D 00\n5 idle\n")),
        };
        var runs = StandardDrives.All.Skip(1).Select(name => (Func<Probe>)(() => Probe.Run(StandardDrives.Load(name), seasoned)))
            .Concat(others).AsParallel().AsOrdered().Select(run => run()).ToList();
        Standard = new[] { warm }.Concat(runs.Take(6)).ToDictionary(p => p.Result.Drive.Name);
        (Stall, QuickKeyCycle, Session) = (runs[6], runs[7], runs[8]);
    }

    public IReadOnlyDictionary<string, Probe> Standard { get; }

    public Probe Stall { get; }

    public Probe QuickKeyCycle { get; }

    /// <summary>The app's session at a warm idle, with a 0x6D request every second: the dispatcher runs.</summary>
    public Probe Session { get; }
}

/// <summary>One observed run.</summary>
public sealed class Probe
{
    const ulong Second = 24_000_000, TenMs = 240_000;
    const uint Handover = 0x0E366A;                                      // the boot's jump into the program (plan 1)

    readonly List<(ulong Cycle, int File, uint Pc)> tableReads = new();
    readonly List<(ulong Cycle, uint Reader, int File, uint Return)> entries = new();
    readonly Dictionary<int, HashSet<int>> siteLevels = new();
    readonly HashSet<int> dispatcherLevels = new();
    readonly List<(ulong Cycle, int Offset, uint Pc)> windowWrites = new();
    readonly List<ushort> magicAtHandover = new();
    readonly List<(double Seconds, int Ticks)> ticks = new();
    readonly List<(double Seconds, bool Running)> running = new();

    public DriveResult Result { get; private set; } = null!;

    /// <summary>Flash data reads inside a live table: the drive cycle, the table byte's file offset and the reading instruction.</summary>
    public IReadOnlyList<(ulong Cycle, int File, uint Pc)> TableReads => tableReads;

    /// <summary>Reader entries (0D:029A, 0D:0168) whose R12/R13 point into a live table, with the return address on the stack.</summary>
    public IReadOnlyList<(ulong Cycle, uint Reader, int File, uint Return)> Entries => entries;

    /// <summary>PSW.ILVL seen at each live table's call site (file offset).</summary>
    public IReadOnlyDictionary<int, HashSet<int>> SiteLevels => siteLevels;

    /// <summary>PSW.ILVL seen where the serial-line task calls the DS2 dispatcher (09:FAD2, 09:FB00).</summary>
    public IReadOnlySet<int> DispatcherLevels => dispatcherLevels;

    /// <summary>Writes into the window (external RAM 0x1D00–0x1FDF) after a power cycle's handover.</summary>
    public IReadOnlyList<(ulong Cycle, int Offset, uint Pc)> WindowWrites => windowWrites;

    /// <summary>The word at 9D00 at every handover.</summary>
    public IReadOnlyList<ushort> MagicAtHandover => magicAtHandover;

    /// <summary>E2A6 every 100 ms after the handover.</summary>
    public IReadOnlyList<(double Seconds, int Ticks)> Ticks => ticks;

    /// <summary>D3's running signal every 10 ms: F900 = 2 and FD2C &amp; 1E00h ≠ 0.</summary>
    public IReadOnlyList<(double Seconds, bool Running)> Running => running;

    public static Probe Run(Drive drive, byte[]? eeprom = null)
    {
        var probe = new Probe();
        probe.Result = DriveRun.Run(TestImages.Car, drive, new DriveRunOptions(Eeprom: eeprom, Observers: new Action<DriveContext>[] { probe.Attach }));
        return probe;
    }

    void Attach(DriveContext context) => context.PowerCycleStarted += (machine, _) =>
    {
        var origin = context.PowerCycleStart;
        var handedOver = false;
        machine.OnInstruction(Handover, m =>
        {
            handedOver = true;
            magicAtHandover.Add((ushort)(m.Bus.ExternalRam[0x1D00] | m.Bus.ExternalRam[0x1D01] << 8));
            Sample(m, origin);
        });
        machine.Bus.DataWatch = (address, write) =>
        {
            var file = (int)(address & 0x7FFFF);
            if (!write && file >= 0x7629C && file < 0x7D75C && Live(file) && machine.Bus.Decode(address) == Region.Flash)
                tableReads.Add((origin + machine.Cycles, file, machine.Cpu.InstructionAddress));
            else if (write && handedOver && (address & 0x1FFF) is >= 0x1D00 and < 0x1FE0 && machine.Bus.Decode(address) == Region.ExternalRam)
                windowWrites.Add((origin + machine.Cycles, (int)(address & 0x1FFF), machine.Cpu.InstructionAddress));
        };
        foreach (var reader in new uint[] { 0x0D029A, 0x0D0168 })
        {
            machine.OnInstruction(reader, m =>
            {
                var file = (m.Cpu.R(13) * 0x4000 + (m.Cpu.R(12) & 0x3FFF)) & 0x7FFFF;
                if (Live(file))
                    entries.Add((origin + m.Cycles, reader, file, (uint)m.Bus.Peek16((uint)(m.Cpu.Sp + 2)) << 16 | m.Bus.Peek16(m.Cpu.Sp)));
            });
        }
        foreach (var table in LiveTables)
        {
            foreach (var site in table.Sites)
            {
                var levels = siteLevels.TryGetValue(site, out var set) ? set : siteLevels[site] = new HashSet<int>();
                machine.OnInstruction((uint)(0x80000 + site), m => levels.Add(m.Cpu.Psw >> 12));
            }
        }
        foreach (var call in new uint[] { 0x09FAD2, 0x09FB00 })
            machine.OnInstruction(call, m => dispatcherLevels.Add(m.Cpu.Psw >> 12));
        void Every(ulong at, int count)
        {
            machine.Scheduler.Schedule(at, "probe", () =>
            {
                var f900 = machine.Bus.Peek16(0xF900) & 0xFF;
                var fd2c = machine.Bus.Peek16(0xFD2C);
                running.Add(((origin + machine.Cycles) / 24e6, f900 == 2 && (fd2c & 0x1E00) != 0));
                if (handedOver && count % 10 == 0)
                    Sample(machine, origin);
                Every(at + TenMs, count + 1);
            });
        }
        Every(0, 0);
    };

    void Sample(Machine machine, ulong origin)
    {
        if (machine.Bus.XperipheralsEnabled)
            ticks.Add(((origin + machine.Cycles) / 24e6, machine.Bus.Peek16(0xE2A6)));
    }

    /// <summary>The five live tables (R1): name, file offset, bytes and call sites.</summary>
    public static readonly (string Name, int File, int Bytes, int[] Sites)[] LiveTables =
    {
        ("ign98_pl", 0x77315, 320, new[] { 0x2C650 }),
        ("ign98_is", 0x775D4, 36, new[] { 0x2C3A4 }),
        ("af98", 0x7D69C, 192, new[] { 0x3D40A }),
        ("cam_in", 0x76390, 192, new[] { 0x33B48, 0x33BA2 }),
        ("cam_ex", 0x7629C, 192, new[] { 0x33B74, 0x33BEA }),
    };

    public static bool Live(int file) => LiveTables.Any(t => file >= t.File && file < t.File + t.Bytes);

    public static (string Name, int File, int Bytes, int[] Sites) TableAt(int file) =>
        LiveTables.First(t => file >= t.File && file < t.File + t.Bytes);
}

public class OverlayFactsStaticTests
{
    [Fact] public void ThePatchTargets_HoldTheCarFilesBytes()
    {
        var car = TestBins.CarBytes;
        // Each site: MOV R12,#offset (E6 FC lo hi); MOV R13,#page (E6 FD page 00); CALLS 0Dh,reader (DA 0D lo hi).
        var sites = new (int Site, int Offset, int Page, byte[] Call)[]
        {
            (0x2C650, 0x3315, 0x1D, new byte[] { 0xDA, 0x0D, 0x9A, 0x02 }),
            (0x2C3A4, 0x35D4, 0x1D, new byte[] { 0xDA, 0x0D, 0x9A, 0x02 }),
            (0x3D40A, 0x169C, 0x1F, new byte[] { 0xDA, 0x0D, 0x9A, 0x02 }),
            (0x33B48, 0x2390, 0x1D, new byte[] { 0xDA, 0x0D, 0x9A, 0x02 }),
            (0x33BA2, 0x2390, 0x1D, new byte[] { 0xDA, 0x0D, 0x9A, 0x02 }),
            (0x33B74, 0x229C, 0x1D, new byte[] { 0xDA, 0x0D, 0x9A, 0x02 }),
            (0x33BEA, 0x229C, 0x1D, new byte[] { 0xDA, 0x0D, 0x9A, 0x02 }),
        };
        foreach (var (site, offset, page, call) in sites)
        {
            var expected = new byte[] { 0xE6, 0xFC, (byte)offset, (byte)(offset >> 8), 0xE6, 0xFD, (byte)page, 0x00 }.Concat(call).ToArray();
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(car, site - 8, 12));
        }
        Assert.Equal("DA09B6FF", Convert.ToHexString(car, 0x1FC8A, 4));      // the 0x6D slot: CALLS 09h, FFB6h
        Assert.Equal("DE800D00", Convert.ToHexString(car, 0x6E3E2, 4));      // queue-1 task 09h: 0D:80DE
        Assert.Equal("43B50C00", Convert.ToHexString(car, 0x6FE00, 4));      // the program CRC's range 4 ends at 0C:B543
        Assert.All(car[0x4B544..0x4FFE6], b => Assert.Equal(0xFF, b));     // free flash
    }

    [Fact] public void TheDeadSlot_AnswersUnknown()
    {
        var r = DriveRun.Run(TestImages.Car, DriveParser.Parse("drive slot\n3 on\nkline send 6D 00\n2 on"));
        var exchange = Assert.Single(r.KLine);
        Assert.Equal(KLineOutcome.Replied, exchange.Outcome);
        Assert.Equal("1204FFE9", Convert.ToHexString(exchange.Reply!.ToArray()));
    }
}

[Trait("Category", "StandardDrives")]
public class OverlayFactsTests(OverlayFacts f, ITestOutputHelper output) : IClassFixture<OverlayFacts>
{
    static readonly (uint Start, uint End)[] Readers = { (0x0D029A, 0x0D0358), (0x0D0168, 0x0D022A) };

    /// <summary>The checksum task's CRC-16 loop (0E:3642): it reads every byte of each CRC range, and its timing does not depend on them.</summary>
    const uint CrcLoopRead = 0x0E364C;

    [Fact] public void TheFiveTables_AreReadOnlyFromTheirSevenSites()
    {
        var probes = f.Standard.Values.ToList();
        var outside = probes.SelectMany(p => p.TableReads).Where(r => !Readers.Any(x => r.Pc >= x.Start && r.Pc < x.End))
            .Select(r => r.Pc).Distinct().Order().ToList();
        Assert.True(outside.SequenceEqual(new[] { CrcLoopRead }),
            "reads of a live table outside the readers, at " + string.Join(", ", outside.Select(pc => pc.ToString("X6"))));
        foreach (var (cycle, reader, file, back) in probes.SelectMany(p => p.Entries))
        {
            var table = Probe.TableAt(file);
            Assert.True(table.Sites.Any(site => back == 0x80000 + site + 4),
                $"{table.Name} entered at {reader:X6} from {back:X6} at {cycle / 24e6:0.000} s");
        }
        foreach (var table in Probe.LiveTables)
            Assert.True(probes.Any(p => p.Entries.Any(e => Probe.TableAt(e.File).Name == table.Name)), $"{table.Name} is never read");
    }

    [Fact] public void TheHookedReads_AndTheHandler_RunAtTaskLevel()                     // Review Focus 1
    {
        var levels = f.Standard.Values.SelectMany(p => p.SiteLevels)
            .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.SelectMany(kv => kv.Value).ToHashSet());
        Assert.Equal(new[] { 0x33B48, 0x33B74 }, levels.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).Order());   // FD9C.3's branch
        Assert.All(levels.Where(kv => kv.Value.Count > 0), kv => Assert.Equal(new[] { 0 }, kv.Value.Order()));
        Assert.Equal(new[] { 0 }, f.Session.DispatcherLevels.Order());
    }

    [Fact] public void E2A6_CountsTenMillisecondTicks()
    {
        static double Rate(Probe p, double from, double to)
        {
            var a = p.Ticks.First(t => t.Seconds >= from);
            var b = p.Ticks.Last(t => t.Seconds <= to);
            return (ushort)(b.Ticks - a.Ticks) / (b.Seconds - a.Seconds);
        }
        Assert.InRange(Rate(f.Standard["warm-start"], 10, 40), 99, 101);
        Assert.InRange(Rate(f.Standard["full-load"], 18.6, 20.4), 99, 101);              // the steady 6,900 rpm segment
    }

    [Fact] public void TheRunningSignal_IsFalseWhileStarting_AndAfterAStop()
    {
        static double FirstTrue(Probe p, double after) => p.Running.First(s => s.Seconds >= after && s.Running).Seconds;
        static double FirstFalse(Probe p, double after) => p.Running.First(s => s.Seconds >= after && !s.Running).Seconds;
        var warm = f.Standard["warm-start"];
        Assert.All(warm.Running.Where(s => s.Seconds < 4.0), s => Assert.False(s.Running));      // the boot, the crank, the start
        Assert.InRange(FirstTrue(warm, 0), 4.0, 6.0);
        Assert.All(warm.Running.Where(s => s.Seconds > 6.0 && s.Seconds < 44.5), s => Assert.True(s.Running));
        Assert.InRange(FirstFalse(warm, 44.5) - 44.5, 0, 0.02);                                  // the key-off: F900 3 at +7 ms
        var stall = f.Stall;
        Assert.All(stall.Running.Where(s => s.Seconds > 6.0 && s.Seconds < 9.5), s => Assert.True(s.Running));
        var stopped = stall.Result.PowerCycles[0].Engine!.Phases.First(p => p.Phase == EnginePhase.Stopped && p.Cycle / 24e6 > 9.5).Cycle / 24e6;
        Assert.InRange(FirstFalse(stall, 9.5) - stopped, -2.0, 0.3);                             // the stall from 9.5 s: by 0.3 s after the stop
        Assert.All(stall.Running.Where(s => s.Seconds > 12.0 && s.Seconds < 13.0), s => Assert.False(s.Running)); // the crank again
        Assert.InRange(FirstTrue(stall, 12.5), 13.0, 15.0);
        var quick = f.QuickKeyCycle;
        Assert.InRange(FirstFalse(quick, 14.5) - 14.5, 0, 0.02);                                 // the key off for 1 s, coasting
        output.WriteLine($"warm start: running from {FirstTrue(warm, 0):0.00} s, off {FirstFalse(warm, 44.5) - 44.5:0.000} s after the key-off");
        output.WriteLine($"stall: engine stopped {stopped:0.00} s, running false at {FirstFalse(stall, 9.5):0.00} s, true again at {FirstTrue(stall, 12.5):0.00} s");
        output.WriteLine($"quick key cycle (+1 s): false at {FirstFalse(quick, 14.5):0.00} s, true again at {FirstTrue(quick, FirstFalse(quick, 14.5)):0.00} s");
    }

    [Fact] public void TheWindow_IsNotWrittenByTheCarFile_AfterTheBoot()
    {
        var all = f.Standard.Values.Append(f.Stall).Append(f.QuickKeyCycle).ToList();
        var writers = all.SelectMany(p => p.WindowWrites).Select(w => w.Pc).Distinct().Order().ToList();
        Assert.True(writers.Count == 0, "the window written after the handover by " + string.Join(", ", writers.Select(pc => pc.ToString("X6"))));
        Assert.All(all.SelectMany(p => p.MagicAtHandover), magic => Assert.NotEqual(LiveOverlay.Magic, magic));
    }
}
