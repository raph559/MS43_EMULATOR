using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>One table's edit for the acceptance (plan 6 Task 11): its copy, and the drive segments that read it.</summary>
public sealed record TableEdit(int Table, string What, byte[] Copy, string Segments)
{
    public byte[] Flash => OverlayEditRuns.Flash(Table);

    public uint Crc => LiveOverlay.Crc32(Copy);

    /// <summary>The writes that carry the edit: its changed cells, in runs of at most 120 bytes.</summary>
    public IReadOnlyList<byte[]> Writes => LiveOverlay.Edit(Table, Copy, Flash);

    /// <summary>The same writes with the flashed values: the identical run's traffic is the edit's, byte for byte in length.</summary>
    public IReadOnlyList<byte[]> FlashedWrites => Writes.Select(w =>
    {
        var at = w[3] << 8 | w[4];
        return w.Take(5).Concat(Flash.Skip(at).Take(w.Length - 5)).ToArray();
    }).ToList();

    /// <summary>The overlay with this edit baked into the flash (in memory).</summary>
    public FlashedImage Baked => LiveOverlayBin.With((LiveOverlay.Tables[Table].FileOffset, Copy));
}

/// <summary>
/// The overlay's acceptance (plan 6 Task 11; overlay design §8 items 1–5), every bin and drive run once, in parallel, as
/// <see cref="CompareAcceptance"/> does:
/// <list type="bullet">
/// <item>the car file and the overlay with nothing loaded on the seven standard drives, every seasoned drive from the car's
/// warm-start EEPROM;</item>
/// <item>the overlay drives (<see cref="OverlayDriveSet"/>, shared with Task 10);</item>
/// <item>per table, from a blank EEPROM, warm, a status every second at 9,600 baud: the edit live (written over the K-line and
/// switched on with its sum at idle, before its region), the same edit baked into the flash (the same writes, which change
/// nothing, and an on with sum 0), and the identical copy (the same cells written with their flashed values, sum 0). The
/// three runs execute the same program with the same requests, so they differ only by the edit (D15);</item>
/// <item>A/B and revert at idle on ign98_is.</item>
/// </list>
/// </summary>
public sealed class OverlayAcceptance
{
    static string Send(byte[] payload) => OverlayHookRuns.Send(payload);

    static string Head(string name) => $"drive {name}\n" + OverlayRuns.Warm + "3 on\nkline every 1 6D 00\n1.5 crank rpm=200\n3 idle\n";

    /// <summary>full-load.drive from its pull on, after the 3 s of idle the head leaves short of its 6.</summary>
    const string FullLoad = "3 idle\n8 drive gear=3 speed=50..150 pedal=95\n2 drive gear=3 rpm=6900\n1 drive gear=3 rpm=7100\n"
        + "0.4 drive gear=3 speed=150 pedal=95 clutch=1\n3 drive gear=4 speed=150..90 pedal=95 clutch=0\n6 drive gear=4 speed=90..20 pedal=0\n3 idle\n";

    /// <summary>part-load.drive from its pull-away on.</summary>
    const string PartLoad = "3 idle\n10 drive gear=3 speed=40..80 pedal=15\n20 drive gear=4 speed=90 pedal=12\n5 drive gear=4 speed=90..20 pedal=0\n5 idle\n";

    static byte[] Edited(int table, Func<int, byte, byte> cell) => OverlayEditRuns.Flash(table).Select((b, i) => cell(i, b)).ToArray();

    static byte Up(byte b, int steps) => (byte)Math.Min(255, b + steps);

    /// <summary>The edits: one per table (design §8.3).</summary>
    public static IReadOnlyList<TableEdit> Edits { get; } = new[]
    {
        new TableEdit(0, "plan 5's block: +3 steps (1.125°) at 4,000–4,750 rpm × the four top loads (R11)",
            Edited(0, (i, b) => Compare.CompareAcceptance.Rows.Contains(i / 16) && Compare.CompareAcceptance.LoadColumns.Contains(i % 16) ? Up(b, 3) : b), FullLoad),
        new TableEdit(1, "every cell +2 steps (0.75°)", Edited(1, (_, b) => Up(b, 2)), "10 idle\n"),
        new TableEdit(2, "+8 (richer) on the four highest loads (rows 8–11)", Edited(2, (i, b) => i / 16 >= 8 ? Up(b, 8) : b), FullLoad),
        // the part-load drive reads both cam tables at their end stops (intake 160 = 120°, exhaust 120), where +4 is clamped
        new TableEdit(3, "every cell −4 steps (3°)", Edited(3, (_, b) => (byte)(b - 4)), PartLoad),
        new TableEdit(4, "every cell −4 steps (3°)", Edited(4, (_, b) => (byte)(b - 4)), PartLoad),
    };

    public enum EditRun { Live, Baked, Identical }

    public static string EditDrive(TableEdit edit, EditRun run)
    {
        var writes = run == EditRun.Identical ? edit.FlashedWrites : edit.Writes;
        var sum = LiveOverlay.Crc32(run == EditRun.Identical ? edit.Flash : edit.Copy);
        return Head($"edit-{LiveOverlay.Tables[edit.Table].Name.Replace('_', '-')}") + Send(LiveOverlay.Load(edit.Table, 0)) + string.Concat(writes.Select(Send))
            + Send(LiveOverlay.On(edit.Table, sum)) + edit.Segments;
    }

    /// <summary>ign98_is +2 at idle: on, all off, all on, off, on, each 2 s; then a revert and 3 s more.</summary>
    public static readonly string AB = Head("ab") + Send(LiveOverlay.Load(1, 0)) + string.Concat(Edits[1].Writes.Select(Send))
        + Send(LiveOverlay.On(1, Edits[1].Crc)) + "2 idle\n" + Send(LiveOverlay.AllOff()) + "2 idle\n" + Send(LiveOverlay.AllOn()) + "2 idle\n"
        + Send(LiveOverlay.Off(1)) + "2 idle\n" + Send(LiveOverlay.On(1, Edits[1].Crc)) + "2 idle\n" + Send(LiveOverlay.Revert()) + "3 idle\n";

    public OverlayAcceptance()
    {
        Catalogue = CalibrationCatalogue.Load(CalibrationCatalogue.FindUpwards("calmaps_ms43x.csv", AppContext.BaseDirectory)!);
        Standard = StandardDrives.All.ToDictionary(name => name, StandardDrives.Load);
        Images = new Dictionary<string, FlashedImage> { ["car"] = TestImages.Car, ["overlay"] = LiveOverlayBin.Image };

        var warm = new[] { "car", "overlay" }.AsParallel().Select(bin => (Key: (bin, "warm-start"), Run: DriveRun.RunWithChecks(Images[bin], Standard["warm-start"])))
            .ToDictionary(p => p.Key, p => p.Run);
        var seasoned = warm[("car", "warm-start")].Eeprom.FinalImage;
        var standard = Images.Keys.SelectMany(bin => StandardDrives.All.Skip(1).Select(drive => (Bin: bin, Drive: drive)))
            .Select(key => (Kind: "standard", key.Bin, key.Drive, Edit: -1, Run: EditRun.Live));
        var edits = Edits.SelectMany(e => Enum.GetValues<EditRun>().Select(run => (Kind: "edit", Bin: "", Drive: "", Edit: e.Table, Run: run)));
        var jobs = standard.Concat(edits).Append((Kind: "ab", Bin: "", Drive: "", Edit: -1, Run: EditRun.Live))
            .Append((Kind: "watch", Bin: "", Drive: "", Edit: -1, Run: EditRun.Live)).ToList();

        var runs = new Dictionary<(string Bin, string Drive), DriveResult>(warm);
        var editRuns = new Dictionary<(int Table, EditRun Run), OverlayRun>();
        OverlayRun? ab = null, watch = null;
        var sets = new System.Collections.Concurrent.ConcurrentBag<(int Index, object Result)>();
        jobs.Select((job, index) => (job, index)).AsParallel().ForAll(p =>
        {
            var job = p.job;
            object result = job.Kind switch
            {
                "standard" => DriveRun.RunWithChecks(Images[job.Bin], Standard[job.Drive],
                    new DriveRunOptions(Eeprom: Standard[job.Drive].Start == StartEeprom.Seasoned ? seasoned : null)),
                "edit" => OverlayRuns.Run(job.Run == EditRun.Baked ? Edits[job.Edit].Baked : Images["overlay"], EditDrive(Edits[job.Edit], job.Run),
                    checks: true),
                "ab" => OverlayRuns.Run(Images["overlay"], AB, checks: true),
                _ => OverlayRuns.Run(Images["overlay"], File.ReadAllText(Path.Combine(StandardDrives.Directory, "part-load.drive")),
                    eeprom: seasoned, watchWrites: true),
            };
            sets.Add((p.index, result));
        });
        foreach (var (index, result) in sets.OrderBy(s => s.Index))
        {
            var job = jobs[index];
            switch (job.Kind)
            {
                case "standard": runs[(job.Bin, job.Drive)] = (DriveResult)result; break;
                case "edit": editRuns[(job.Edit, job.Run)] = (OverlayRun)result; break;
                case "ab": ab = (OverlayRun)result; break;
                default: watch = (OverlayRun)result; break;
            }
        }
        Runs = runs;
        EditRuns = editRuns;
        ABRun = ab!;
        Untouched = watch!;
    }

    public CalibrationCatalogue Catalogue { get; }

    public IReadOnlyDictionary<string, Drive> Standard { get; }

    public IReadOnlyDictionary<string, FlashedImage> Images { get; }

    /// <summary>The standard drives on the car file and on the overlay with nothing loaded.</summary>
    public IReadOnlyDictionary<(string Bin, string Drive), DriveResult> Runs { get; }

    /// <summary>The edit runs, their hooked reads observed.</summary>
    public IReadOnlyDictionary<(int Table, EditRun Run), OverlayRun> EditRuns { get; }

    public OverlayRun ABRun { get; }

    /// <summary>The standard part-load drive on the overlay with nothing loaded, its hooked reads and the overlay's writes watched.</summary>
    public OverlayRun Untouched { get; }

    public OverlayDriveSet Set => OverlayDriveSet.Shared;

    public CompareResult Off(string drive) =>
        Core.Compare.Of(Images["car"], Images["overlay"], Standard[drive], Runs[("car", drive)], Runs[("overlay", drive)], new CompareOptions(Catalogue: Catalogue));

    public CompareResult IdenticalOn(string drive) =>
        Core.Compare.Of(Set.Images["car"], Set.Images["overlay"], Set.Drives[drive], Set.Runs[("car", drive)], Set.Runs[("overlay", drive)],
            new CompareOptions(Catalogue: Catalogue));

    /// <summary><paramref name="reference"/> against <paramref name="candidate"/>, both of one table's edit runs.</summary>
    public CompareResult Edit(int table, EditRun reference, EditRun candidate)
    {
        FlashedImage Image(EditRun run) => run == EditRun.Baked ? Edits[table].Baked : Images["overlay"];
        return Core.Compare.Of(Image(reference), Image(candidate), DriveParser.Parse(EditDrive(Edits[table], candidate)),
            EditRuns[(table, reference)].Result, EditRuns[(table, candidate)].Result, new CompareOptions(Catalogue: Catalogue));
    }
}

[Trait("Category", "StandardDrives")]
public class OverlayAcceptanceTests(OverlayAcceptance f, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<OverlayAcceptance>
{
    static string Describe(IEnumerable<BehaviourDifference> differences) => string.Join("; ", differences.Take(8).Select(d =>
        $"{d.Signal}{(d.Cylinder is { } c ? $" cylinder {c}" : "")} {d.FromCycle / 24e6:0.000}–{d.ToCycle / 24e6:0.000} s ×{d.Count}: {d.Reference} → {d.Candidate} at {d.At.Rpm:0} rpm, {d.At.Segment}"));

    /// <summary>What a run keeps: its faults and its power cycles.</summary>
    static bool Kept(BehaviourDifference d) => d.Signal.StartsWith("faults", StringComparison.Ordinal) || d.Signal.StartsWith("power_cycles", StringComparison.Ordinal);


    [Fact] public void Off_IsTransparent_KeepsEveryCheck_AndChangesNoFaultOrPowerCycle()          // §8.1
    {
        // With nothing loaded the overlay reads every table through to the flash and writes nothing but its stacks. Its
        // housekeeping runs every 10 ms from the boot, so, unlike plan 5's R8 stub (which never runs at idle), it moves the
        // program's timing on every drive: the warm idle's timing-sensitive after-start steps land at other moments, and the
        // learned EEPROM bytes the after-run writes follow them, as six more status requests do on the car file alone (plan 6
        // Task 5's ruling). So the steady drives are judged as the drives in gear are: what a run keeps, and every check.
        var spans = new List<(int Start, int End)>();                          // the fixture's records, contiguous ones merged
        foreach (var (offset, bytes) in LiveOverlayBin.Records().OrderBy(rec => rec.Offset))
        {
            if (spans.Count > 0 && spans[^1].End == offset)
                spans[^1] = (spans[^1].Start, offset + bytes.Length);
            else
                spans.Add((offset, offset + bytes.Length));
        }
        var found = new List<string>();
        foreach (var drive in StandardDrives.All)
        {
            var r = f.Off(drive);
            Assert.Empty(r.Calibration);
            Assert.All(r.Code, d => Assert.True(spans.Any(span => d.Start >= span.Start && d.End <= span.End),
                $"{d.Start:X5}–{d.End:X5} is outside the write list"));
            Assert.Equal(11, r.Code.Count);                                        // 7 sites, the 0x6D slot, task 09h, range 4's end, the block
            Assert.Equal(RunVerdict.Pass, r.Reference.Verdict);
            Assert.True(r.Candidate.Verdict == RunVerdict.Pass, $"{drive}: {r.Candidate.Verdict}: " + string.Join("; ", Verdict.Reasons(r.Candidate.Checks, r.Candidate.Coverage!)));
            var kept = r.Behaviour.Where(Kept).ToList();
            if (kept.Count > 0)
                found.Add($"{drive}: {Describe(kept)}");
            output.WriteLine($"{drive}: the drift — " + string.Join(", ", r.Behaviour.GroupBy(d => d.Signal).Select(g => $"{g.Key} ×{g.Sum(d => d.Count)} ≤ {g.Max(d => d.MaxDifference):0.###}")));
        }
        Assert.True(found.Count == 0, string.Join(" | ", found));

        var untouched = f.Untouched;
        // part load reads every table but the idle ignition
        Assert.All(new[] { 0, 2, 3, 4 }, t => Assert.Contains(untouched.Reads, r => r.Table.Index == t));
        Assert.All(untouched.Reads, r => Assert.False(r.FromCopy));
        Assert.All(untouched.OverlayWrites, w => Assert.True(w.Address < 0x10000 && ((int)w.Address - w.R0 is >= -16 and < 16 || (int)w.Address - w.Sp is >= -8 and <= 8),
            $"{w.Address:X6} from {w.Pc:X6}"));                                  // not even the window: no frame, no magic
    }

    [Fact] public void IdenticalCopiesOn_KeepEveryCheck_AndChangeNoFaultOrPowerCycle()          // §8.2
    {
        // Identical copies on are judged by what a run keeps, not by equality (plan 6 Task 5's ruling): the overlay answers
        // each 0x6D request where the car answers FF, and that work moves the warm idle's timing-sensitive decisions.
        var found = new List<string>();
        foreach (var drive in OverlayDriveSet.Names)
        {
            var r = f.IdenticalOn(drive);
            var kept = r.Behaviour.Where(Kept).ToList();
            if (kept.Count > 0)
                found.Add($"{drive}: {Describe(kept)}");
            if (r.Candidate.Verdict != RunVerdict.Pass)
                found.Add($"{drive}: {r.Candidate.Verdict}");
            output.WriteLine($"{drive}: " + (r.Behaviour.Count == 0 ? "no difference" : string.Join(", ",
                r.Behaviour.GroupBy(d => d.Signal).Select(g => $"{g.Key} ×{g.Sum(d => d.Count)} ≤ {g.Max(d => d.MaxDifference):0.###}"))));
        }
        Assert.True(found.Count == 0, string.Join(" | ", found));
    }

    [Fact] public void ALiveEdit_IsExactlyTheBakedEdit()                                           // §8.3, D15
    {
        // ign98_is is read from the first idle on, before the engine runs long enough for an on to be accepted: its live edit
        // cannot come before its region, and Task 6 proves it against the identical copy from the moment it goes on.
        var found = new List<string>();
        foreach (var edit in OverlayAcceptance.Edits.Where(e => e.Table != 1))
        {
            var live = f.EditRuns[(edit.Table, OverlayAcceptance.EditRun.Live)].Result;
            var on = live.KLine.Single(e => e.Request.Count > 3 && e.Request[2] == LiveOverlay.Command && e.Request[3] == 0x03);
            Assert.True(LiveOverlay.Refusal(on.Reply!) is null, $"table {edit.Table}: the live on was refused");
            var r = f.Edit(edit.Table, OverlayAcceptance.EditRun.Baked, OverlayAcceptance.EditRun.Live);
            Assert.Empty(r.Code);
            Assert.Equal(new[] { LiveOverlay.Tables[edit.Table].FileOffset }, r.Calibration.Select(d => (int)d.Address & 0x7FFFF));
            if (r.Behaviour.Count > 0)
                found.Add($"{LiveOverlay.Tables[edit.Table].Name}: {Describe(r.Behaviour)}");
        }
        Assert.True(found.Count == 0, string.Join(" | ", found));
    }

    /// <summary>The decision each table's edit moves: the decided advance, the injection's ticks, the VANOS targets.</summary>
    static string Signal(int table) => table switch
    {
        0 or 1 => "spark.decided_advance",
        2 => "injection.ticks",
        3 => "cam_in_sp_deg",
        _ => "cam_ex_sp_deg",
    };

    /// <summary>The largest difference <paramref name="table"/>'s edit can make to its decision: its steps, and one for a breakpoint.</summary>
    static double Most(int table) => table switch
    {
        0 => 4 * 0.375,
        1 => 3 * 0.375,
        2 => double.MaxValue,
        _ => 5 * 0.75,
    };

    /// <summary>
    /// Whether a difference lies where the edit's cells are read: ign98_pl's block between the rows either side (3,750 to
    /// 5,000 rpm, plan 5) with the pedal down; ign98_is (every cell) in the 0.3 s after its first read, before the edit has
    /// changed the engine's course; af98's top loads, the pedal down where it differs most; a cam table (every cell) within
    /// 0.2 s after one of its reads.
    /// </summary>
    static bool InRegion(TableEdit edit, OverlayRun run, Drive drive, BehaviourDifference d)
    {
        var rpm = VariableTrace.Columns.ToList().IndexOf("rpm_fed");
        double RpmAt(ulong cycle) => run.Result.Trace.MinBy(t => Math.Abs(t.Seconds - cycle / 24e6))!.Values[rpm] ?? 0;
        double Pedal(ulong cycle) => DriveTimeline.At(drive, cycle / 24e6).Values["pedal"];
        var ends = new[] { d.FromCycle, d.ToCycle };
        return edit.Table switch
        {
            0 => ends.All(c => RpmAt(c) is >= 3750 - 100 and <= 5000 + 100 && Pedal(c) >= 90),
            1 => ends.All(c => c < run.Reads.Where(r => r.Table.Index == 1).Min(r => r.Cycle) + 7_200_000),
            2 => DriveTimeline.At(drive, d.At.Seconds).Values["pedal"] >= 90,
            _ => ends.All(c => run.Reads.Any(r => r.Table.Index == edit.Table && r.Cycle <= c && c - r.Cycle < 4_800_000)),
        };
    }

    /// <summary>The standard drive an edit's drive is cut from, whose drift bounds what follows the edit's region.</summary>
    static string Standard(TableEdit edit) => edit.Table switch { 0 or 2 => "full-load", 1 => "idle", _ => "part-load" };

    [Fact] public void EachEdit_ActsWhereItsCellsAreRead_AndNowhereElse()                       // §8.3
    {
        // The baked edit against the identical copy: the same program and the same requests, so the runs are equal until the
        // table is first read. Where its edited cells are read the matching decision moves the edit's way, by its steps at
        // most. Elsewhere, once the engine's course has changed, that decision differs by no more than one step beyond the
        // drift: the larger of the overlay's two baselines on the standard drive the edit's drive is cut from (nothing loaded,
        // §8.1; identical copies on, §8.2), one step being the compare's for a lone event.
        var found = new List<string>();
        foreach (var edit in OverlayAcceptance.Edits)
        {
            var r = f.Edit(edit.Table, OverlayAcceptance.EditRun.Identical, OverlayAcceptance.EditRun.Baked);
            var run = f.EditRuns[(edit.Table, OverlayAcceptance.EditRun.Baked)];
            var name = LiveOverlay.Tables[edit.Table].Name;
            var drive = DriveParser.Parse(OverlayAcceptance.EditDrive(edit, OverlayAcceptance.EditRun.Baked));
            var first = run.Reads.Where(x => x.Table.Index == edit.Table).Min(x => x.Cycle);
            var early = r.Behaviour.Where(d => d.FromCycle < first).ToList();
            if (early.Count > 0)
                found.Add($"{name}: before its first read, {Describe(early)}");
            var signal = Signal(edit.Table);
            var inRegion = r.Behaviour.Where(d => d.Signal == signal && InRegion(edit, run, drive, d)).ToList();
            if (inRegion.Count == 0 && edit.Table != 1)                          // ign98_is: spark by spark, below
                found.Add($"{name}: no {signal} difference where its cells are read");
            var wrongWay = inRegion.Where(d => (edit.Table < 3 ? d.Candidate < d.Reference : edit.Table == 3 && d.Candidate > d.Reference)
                || d.MaxDifference > Most(edit.Table) + 1e-6).ToList();
            if (wrongWay.Count > 0)
                found.Add($"{name}: where read, against or beyond the edit: {Describe(wrongWay)}");
            if (edit.Table == 1)
            {
                // spark by spark, as Task 6 compares the live edit: every cell two steps up is 0.75° more advance at every decision
                var (b, i) = (run.Result.Sparks.Where(x => x.Main && x.Fire > first && x.Fire < first + 7_200_000).ToList(),
                    f.EditRuns[(1, OverlayAcceptance.EditRun.Identical)].Result.Sparks.Where(x => x.Main && x.Fire > first && x.Fire < first + 7_200_000).ToList());
                if (b.Count < 10 || !b.Select(x => x.Cylinder).SequenceEqual(i.Select(x => x.Cylinder))
                    || b.Zip(i).Any(p => p.First.DecidedAdvance != p.Second.DecidedAdvance + 0.75))
                    found.Add($"{name}: the 0.3 s after its first read are not 0.75° up at every spark");
            }
            var off = f.Off(Standard(edit)).Behaviour.Concat(f.IdenticalOn(Standard(edit)).Behaviour)
                .Where(d => d.Signal == signal).Select(d => d.MaxDifference).DefaultIfEmpty(0).Max();
            double Step(BehaviourDifference d) => edit.Table switch
            {
                0 or 1 => CompareTolerances.Default.LoneAdvanceDeg,
                2 => CompareTolerances.Default.LoneShare * d.Reference,
                _ => 0.75,                                                          // one cell of a cam table
            };
            var beyond = r.Behaviour.Where(d => d.Signal == signal && !InRegion(edit, run, drive, d) && d.MaxDifference > off + Step(d) + 1e-6).ToList();
            if (beyond.Count > 0)
                found.Add($"{name}: elsewhere beyond the drift ({off:0.###}): {Describe(beyond)}");
            output.WriteLine($"{name}: first read {first / 24e6:0.000} s; {signal} where read ×{inRegion.Sum(d => d.Count)} ≤ "
                + $"{inRegion.Select(d => d.MaxDifference).DefaultIfEmpty(0).Max():0.###}, elsewhere ≤ {r.Behaviour.Where(d => d.Signal == signal).Except(inRegion).Select(d => d.MaxDifference).DefaultIfEmpty(0).Max():0.###} (drift {off:0.###}); "
                + string.Join(", ", r.Behaviour.GroupBy(d => d.Signal).Select(g => $"{g.Key} ×{g.Sum(d => d.Count)} ≤ {g.Max(d => d.MaxDifference):0.###}")));
        }
        Assert.True(found.Count == 0, string.Join(" | ", found));
    }

    /// <summary>ign98_is's state in the window the handler left last before <paramref name="cycle"/>.</summary>
    static OverlayState StateAt(OverlayRun run, ulong cycle) => run.Windows.Last(w => w.Cycle < cycle).Window.Tables[1].State;

    [Fact] public void AB_FollowsTheWindow_AtEveryRead()                                           // §8.4
    {
        var run = f.ABRun;
        Assert.All(run.Overlay, e => Assert.True(e.Outcome == KLineOutcome.Replied && LiveOverlay.Refusal(e.Reply!) is null, Convert.ToHexString(e.Request.ToArray())));
        var reads = run.Reads.Where(r => r.Table.Index == 1 && r.Cycle > run.Windows[0].Cycle).ToList();
        Assert.All(reads, r => Assert.Equal(StateAt(run, r.Cycle) == OverlayState.On, r.FromCopy));
        var phases = reads.GroupBy(r => run.Windows.Count(w => w.Cycle < r.Cycle)).Select(g => g.First().FromCopy).ToList();
        Assert.Contains(true, phases);
        Assert.Contains(false, phases);
        // through every on phase the copy holds the edit
        Assert.All(run.Windows.Where(w => w.Window.Tables[1].State == OverlayState.On),
            w => Assert.Equal(Convert.ToHexString(OverlayAcceptance.Edits[1].Copy), Convert.ToHexString(w.Window.Copies[..36])));
        // the order the drive switches in: on, all off, all on, off, on
        var switches = run.Overlay.Where(e => e.Request[3] is 0x03 or 0x04 or 0x05 or 0x06).Select(e => e.Request[3]).ToList();
        Assert.Equal(new byte[] { 0x03, 0x06, 0x05, 0x04, 0x03 }, switches);
    }

    [Fact] public void Revert_IsTheFlashedMapsAgain()                                              // §8.5
    {
        var run = f.ABRun;
        var revert = run.Overlay.Select((e, i) => (e, i)).Single(p => p.e.Request[3] == 0x08);
        var after = run.Windows[revert.i];
        Assert.All(after.Window.Tables, t => Assert.Equal(new OverlayEntry(OverlayState.Free, 0, 0), t));
        var reads = run.Reads.Where(r => r.Cycle > after.Cycle).ToList();
        Assert.True(reads.Count > 10, $"{reads.Count} reads after the revert");
        Assert.All(reads, r => Assert.False(r.FromCopy));
        Assert.Equal(LiveOverlay.CopyBytes, LiveOverlay.ParseStatus(run.Overlay.Last(e => e.Request[3] == 0x00).Reply!).FreeBytes);
        Assert.Equal(RunVerdict.Pass, run.Result.Verdict);
    }
}
