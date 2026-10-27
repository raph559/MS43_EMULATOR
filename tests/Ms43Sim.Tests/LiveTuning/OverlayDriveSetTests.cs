using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The overlay drives (plan 6 Task 10), sim/drives/overlay: the seven standard drives with the app's live session and
/// identical copies on from the idle, the DS2 command and its abuse, silence, a stall and a quick key cycle. Each runs once
/// on the overlay as flashed and once on the car file, in parallel; every seasoned drive starts from the car's warm-start
/// EEPROM, as the compare does (plan 5 C6). The runs are shared with the acceptance (Task 11).
/// </summary>
public sealed class OverlayDriveSet
{
    static readonly Lazy<OverlayDriveSet> shared = new(() => new OverlayDriveSet());

    public static OverlayDriveSet Shared => shared.Value;

    public static string Directory => Path.Combine(StandardDrives.Directory, "overlay");

    /// <summary>The standard seven first, in the order <c>ms43sim check</c> runs them, then the overlay's own.</summary>
    public static IReadOnlyList<string> Names { get; } = StandardDrives.All.Concat(new[] { "ds2", "silence", "stall", "quick-key-cycle" }).ToArray();

    public static Drive Load(string name) => DriveParser.Parse(File.ReadAllText(Path.Combine(Directory, name + ".drive")));

    OverlayDriveSet()
    {
        Drives = Names.ToDictionary(name => name, Load);
        Images = new Dictionary<string, FlashedImage> { ["car"] = TestImages.Car, ["overlay"] = LiveOverlayBin.Image };
        var runs = new[] { "car", "overlay" }.AsParallel().Select(bin => (Key: (bin, "warm-start"), Run: DriveRun.RunWithChecks(Images[bin], Drives["warm-start"])))
            .ToDictionary(p => p.Key, p => p.Run);
        var seasoned = runs[("car", "warm-start")].Eeprom.FinalImage;
        var rest = Images.Keys.SelectMany(bin => Names.Skip(1).Select(drive => (Bin: bin, Drive: drive)));
        foreach (var (key, run) in rest.AsParallel().Select(key => (key, DriveRun.RunWithChecks(Images[key.Bin], Drives[key.Drive],
                     new DriveRunOptions(Eeprom: Drives[key.Drive].Start == StartEeprom.Seasoned ? seasoned : null)))).ToList())
            runs[key] = run;
        Runs = runs;
    }

    public IReadOnlyDictionary<string, Drive> Drives { get; }

    public IReadOnlyDictionary<string, FlashedImage> Images { get; }

    public IReadOnlyDictionary<(string Bin, string Drive), DriveResult> Runs { get; }

    /// <summary>The 0x6D exchanges of a run, in order.</summary>
    public static IReadOnlyList<KLineExchange> Overlay(DriveResult run) =>
        run.KLine.Where(e => e.Request.Count > 2 && e.Request[2] == LiveOverlay.Command).ToList();

    /// <summary>A status request: the app's keep-alive.</summary>
    public static bool IsStatus(KLineExchange e) => e.Request.SequenceEqual(Ds2.Frame(LiveOverlay.Status()));
}

public sealed class OverlayDriveSetFixture
{
    public OverlayDriveSet Set => OverlayDriveSet.Shared;
}

[Trait("Category", "StandardDrives")]
public class OverlayDriveSetTests(OverlayDriveSetFixture f) : IClassFixture<OverlayDriveSetFixture>
{
    static string Hex(IEnumerable<byte>? bytes) => bytes is null ? "nothing" : Convert.ToHexString(bytes.ToArray());

    static string Payload(params byte[] bytes)
    {
        if (bytes.Length == 4 && bytes[0] == 3)
        {
            var crc = bytes[2] == 0 && bytes[3] == 0
                ? LiveOverlay.Crc32(OverlayEditRuns.Flash(bytes[1])) : 1u;
            return Hex(LiveOverlay.On(bytes[1], crc));
        }
        return Hex(new byte[] { LiveOverlay.Command }.Concat(bytes));
    }

    /// <summary>ds2.drive's requests other than the statuses and the raw frames, in order, with the reply each must get.</summary>
    static IEnumerable<(string Payload, string Reply)> Ds2Expected()
    {
        static string A0() => Hex(Ds2.Frame(new byte[] { 0xA0 }));
        static string Refused(byte status, OverlayReason reason) => Hex(Ds2.Frame(new[] { status, (byte)reason }));
        var af98Sum = OverlayEditRuns.Flash(2)[0] == 0xAA ? A0() : Refused(0xB1, OverlayReason.Integrity);
        return new[]
        {
            (Payload(0x01, 0x00, 0x00, 0x00), A0()), (Payload(0x01, 0x01, 0x01, 0x40), A0()), (Payload(0x01, 0x02, 0x01, 0x64), A0()),
            (Payload(0x02, 0x02, 0x00, 0x00, 0xAA), A0()), (Payload(0x03, 0x00, 0x00, 0x00), A0()), (Payload(0x03, 0x01, 0x00, 0x00), A0()),
            (Payload(0x04, 0x00), A0()), (Payload(0x06), A0()), (Payload(0x05), A0()), (Payload(0x04, 0x00), A0()), (Payload(0x07, 0x00), A0()),
            (Payload(0x01, 0x00, 0x00, 0x00), A0()), (Payload(0x03, 0x00, 0x00, 0x00), A0()),
            (Payload(), Refused(0xB0, OverlayReason.Length)), (Payload(0x00, 0x00), Refused(0xB0, OverlayReason.Length)),
            (Payload(0x09), Refused(0xB0, OverlayReason.Sub)), (Payload(0xFF), Refused(0xB0, OverlayReason.Sub)),
            (Payload(0x01, 0x05, 0x00, 0x00), Refused(0xB0, OverlayReason.Table)), (Payload(0x07, 0xFF), Refused(0xB0, OverlayReason.Table)),
            (Payload(0x02, 0x01, 0x00, 0x24, 0xAA), Refused(0xB0, OverlayReason.Range)), (Payload(0x01, 0x03, 0x01, 0xF5), Refused(0xB0, OverlayReason.Range)),
            (Payload(0x01, 0x03, 0x01, 0x2C), Refused(0xB0, OverlayReason.Overlap)),
            (Payload(0x07, 0x01), Refused(0xB1, OverlayReason.TableOn)),
            (Payload(0x02, 0x03, 0x00, 0x00, 0xAA), Refused(0xB1, OverlayReason.NotLoaded)), (Payload(0x03, 0x03, 0x00, 0x00), Refused(0xB1, OverlayReason.NotLoaded)),
            (Payload(0x03, 0x01, 0x00, 0x01), Refused(0xB1, OverlayReason.Integrity)), (Payload(0x03, 0x02, 0x00, 0x00), af98Sum),
            (Payload(0x08), A0()),
        };
    }

    static bool Framed(KLineExchange e) => e.Request[0] == 0x12 && e.Request.Aggregate((byte)0, (x, b) => (byte)(x ^ b)) == 0
        && e.Request[1] == e.Request.Count;

    [Fact] public void EveryOverlayDrive_PassesOnTheOverlay_WithEveryOnAnswered()
    {
        var found = new List<string>();
        foreach (var name in OverlayDriveSet.Names)
        {
            var run = f.Set.Runs[("overlay", name)];
            if (run.Verdict != RunVerdict.Pass)
                found.Add($"{name}: {run.Verdict}: " + string.Join("; ", Verdict.Reasons(run.Checks, run.Coverage!)));
            var exchanges = OverlayDriveSet.Overlay(run);
            if (!exchanges.Any(OverlayDriveSet.IsStatus))
                found.Add($"{name}: no status");
            foreach (var status in exchanges.Where(e => OverlayDriveSet.IsStatus(e) && e.Outcome == KLineOutcome.Replied))
                if (status.Reply![2] != 0xA0)
                    found.Add($"{name}: a status answered {Hex(status.Reply)}");
            var told = exchanges.Where(e => !OverlayDriveSet.IsStatus(e) && Framed(e)).ToList();
            if (name == "ds2")
            {
                var got = told.Select(e => (Hex(e.Request.Skip(2).SkipLast(1)), e.Outcome == KLineOutcome.Replied ? Hex(e.Reply) : e.Outcome.ToString())).ToList();
                var expected = Ds2Expected().ToList();
                if (!got.SequenceEqual(expected))
                    found.Add("ds2: " + string.Join(", ", got.Zip(expected).Where(p => p.First != p.Second).Take(4).Select(p => $"{p.First} (expected {p.Second.Reply})"))
                        + $" ({got.Count} requests, {expected.Count} expected)");
                foreach (var raw in exchanges.Where(e => !Framed(e)))
                    if (raw.Outcome == KLineOutcome.Replied && raw.Reply![2] is 0xA0 or 0xB0 or 0xB1)
                        found.Add($"ds2: {Hex(raw.Request)} was answered {Hex(raw.Reply)}");
                continue;
            }
            foreach (var e in told)
                if (e.Outcome != KLineOutcome.Replied || LiveOverlay.Refusal(e.Reply!) is { } reason)
                    found.Add($"{name}: {Hex(e.Request)} → {(e.Outcome == KLineOutcome.Replied ? Hex(e.Reply) : e.Outcome.ToString())}");
            if (!told.Any(e => e.Request[3] == 0x03))
                found.Add($"{name}: nothing switched on");
        }
        Assert.True(found.Count == 0, string.Join(" | ", found));
    }

    [Fact] public void EveryOverlayDrive_PassesOnTheCarFile()
    {
        var unknown = Hex(Ds2.Frame(new byte[] { 0xFF }));
        var found = new List<string>();
        foreach (var name in OverlayDriveSet.Names)
        {
            var run = f.Set.Runs[("car", name)];
            if (run.Verdict != RunVerdict.Pass)
                found.Add($"{name}: {run.Verdict}: " + string.Join("; ", Verdict.Reasons(run.Checks, run.Coverage!)));
            var exchanges = OverlayDriveSet.Overlay(run);
            foreach (var e in exchanges.Where(e => e.Outcome == KLineOutcome.Replied))
                if (Hex(e.Reply) != unknown)
                    found.Add($"{name}: {Hex(e.Request)} answered {Hex(e.Reply)}");
            foreach (var e in exchanges.Where(e => !OverlayDriveSet.IsStatus(e) && Framed(e) && e.Outcome != KLineOutcome.Replied))
                found.Add($"{name}: {Hex(e.Request)} {e.Outcome}");
        }
        Assert.True(found.Count == 0, string.Join(" | ", found));
    }
}
