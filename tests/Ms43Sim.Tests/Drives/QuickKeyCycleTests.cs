using System.Collections.Concurrent;
using System.Globalization;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Drives;

/// <summary>
/// A quick key cycle inside the after-run (plan 5 Task 6), each run once with every check: the car file as flashed, from a
/// blank EEPROM, warm. The key goes off at 14.5 s for 1 s (the engine still coasting), 3 s (the after-run under way) or
/// 11 s (the power-down write under way: it begins about 10 s after the key-off, and the relay drops about 12.1 s after
/// it), then a 3 s crank and 5 s of idle.
/// </summary>
public sealed class QuickKeyCycleRuns
{
    const string Head = "drive quick\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n3 on\n1.5 crank rpm=200\n10 idle\n";

    readonly ConcurrentDictionary<string, Lazy<DriveResult>> runs = new();

    public static string Body(double off) => $"{off.ToString(CultureInfo.InvariantCulture)} off\n3 crank rpm=200\n5 idle\n";

    public DriveResult Run(string body) =>
        runs.GetOrAdd(body, b => new Lazy<DriveResult>(() => DriveRun.RunWithChecks(TestImages.Car, DriveParser.Parse(Head + b)))).Value;

    public DriveResult Run(double off) => Run(Body(off));
}

public class QuickKeyCycleTests : IClassFixture<QuickKeyCycleRuns>
{
    const double KeyOffAt = 14.5;

    readonly QuickKeyCycleRuns runs;

    public QuickKeyCycleTests(QuickKeyCycleRuns runs) => this.runs = runs;

    static double Seconds(ulong cycle) => cycle / 24e6;

    static string Why(DriveResult r) => string.Join("; ", r.Checks.SelectMany(c => c.Findings.Select(f => $"{c.Name}: {f.Message}")));

    [Fact, Trait("Category", "StandardDrives")] public void ARestart_AppliesTheSegmentsKLineLinesOnce()   // plan 5 review M1
    {
        // The key comes back during the power-down write: the crank segment begins in the first power cycle, and the
        // firmware's reset starts the second inside it. Its kline lines were applied once, at the segment's start.
        var r = runs.Run("11 off\nkline connect\nkline every 1 6D 00\n3 crank rpm=200\n5 idle\n");
        Assert.Equal(CycleEnd.Restarted, r.PowerCycles[0].End);
        Assert.Single(r.KLine, e => e.Request.SequenceEqual(Hex.Bytes("12 08 91 01 E8 48 00 2A")));
        var restart = r.PowerCycles[1].StartCycle;
        var keepAlives = r.KLine.Count(e => e.Request.SequenceEqual(Hex.Bytes("12 05 6D 00 7A"))
            && e.SentAt >= restart && e.SentAt < restart + 4 * 24_000_000UL);
        Assert.InRange(keepAlives, 3, 5);                                // one a second: applied twice, they came two a second
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    [InlineData(11.0)]
    public void QuickKeyCycle_InsideTheAfterRun_StartsTheEngineAgain(double off)
    {
        var last = runs.Run(off).PowerCycles[^1];
        Assert.Equal((EngineOutcome.Running, CycleEnd.DriveEnded), (last.Engine!.Outcome, last.End));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    [InlineData(11.0)]
    public void TheCarFile_PassesAQuickKeyCycle(double off)
    {
        var r = runs.Run(off);
        Assert.True(r.Checks.All(c => c.Findings.Count == 0), Why(r));
        Assert.Equal(RunVerdict.Pass, r.Verdict);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    public void BeforeThePowerDownWrite_TheFirmwareTakesTheKeyBack_InOnePowerCycle(double off)
    {
        var cycle = Assert.Single(runs.Run(off).PowerCycles);
        Assert.Equal(new[] { (KeyOffAt, false), (KeyOffAt + off, true) }, cycle.KeySwitches.Select(k => (Seconds(k.Cycle), k.On)));
        Assert.Null(cycle.Milestones.PowerOff);                          // the main relay held throughout
    }

    [Fact] public void DuringThePowerDownWrite_TheFirmwareDropsTheRelay_AndResetsItself_IntoANewPowerCycle()
    {
        var r = runs.Run(11.0);
        Assert.Equal(2, r.PowerCycles.Count);
        var first = r.PowerCycles[0];
        Assert.Equal(CycleEnd.Restarted, first.End);
        var dropped = first.StartCycle + first.Milestones.PowerOff!.Value;
        Assert.InRange(Seconds(dropped), KeyOffAt + 11, KeyOffAt + 14);   // the key came back on before the relay dropped
        Assert.InRange(Seconds(first.EndCycle - dropped), 0.005, 0.05);  // the firmware's SRST, about 10 ms after the drop
        Assert.Equal(first.EndCycle, r.PowerCycles[1].StartCycle);
    }

    [Fact] public void TheReport_ListsWhatTheQuickKeyCycleDid()
    {
        var report = DriveReportWriter.Report(runs.Run(11.0));
        Assert.Contains("- the key: off at 14.500 s, on at 25.500 s", report);
        Assert.Contains("the ECU reset itself (SRST) and booted again", report);
    }

    [Fact] public void AStallAfterAQuickKeyCycle_IsAStall_NotAStopAfterTheKeyOff()
    {
        var r = runs.Run(QuickKeyCycleRuns.Body(1.0) + "3 stall\n");
        var cycle = Assert.Single(r.PowerCycles);
        Assert.Equal(EngineOutcome.Stalled, cycle.Engine!.Outcome);
    }
}
