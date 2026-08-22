using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Drives;

/// <summary>
/// A stall with the key on and a restart in the same power cycle, run once with every check: the car file as flashed,
/// from a blank EEPROM, warm. The stall runs from 9.5 s to 12.5 s; the second crank starts at 12.5 s.
/// </summary>
public sealed class RestartRuns
{
    public const string Head = "drive restart\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n";

    public const string Body = "3 on\n1.5 crank rpm=200\n5 idle\n3 stall\n1.5 crank rpm=200\n5 idle\n";

    readonly Lazy<DriveResult> run = new(() => Run(Body));

    public static DriveResult Run(string body, params Action<DriveContext>[] observers) =>
        DriveRun.RunWithChecks(TestImages.Car, DriveParser.Parse(Head + body), new DriveRunOptions(Observers: observers));

    public DriveResult Restart => run.Value;
}

public class RestartTests : IClassFixture<RestartRuns>
{
    const double StallAt = 9.5, CrankAgainAt = 12.5;
    const ulong Second = 24_000_000;

    readonly RestartRuns runs;

    public RestartTests(RestartRuns runs) => this.runs = runs;

    static double Seconds(ulong cycle) => cycle / 24e6;

    static string Why(DriveResult r) => string.Join("; ", r.Checks.SelectMany(c => c.Findings.Select(f => $"{c.Name}: {f.Message}")));

    static double? F900(TraceRow row) => row.Values[VariableTrace.Columns.ToList().IndexOf("f900")];

    /// <summary>Runs <paramref name="act"/> on the power cycle's machine and engine at <paramref name="seconds"/> into the drive.</summary>
    static Action<DriveContext> At(double seconds, Action<Machine, Engine> act) => context =>
        context.PowerCycleStarted += (machine, _) =>
            machine.Scheduler.Schedule((ulong)(seconds * Second), "test", () => act(machine, context.Engine!));

    [Fact] public void Stall_RunsTheEngineDown_WithTheKeyOn()
    {
        var r = runs.Restart;
        var cycle = Assert.Single(r.PowerCycles);
        var stopped = cycle.Engine!.Phases.First(p => p.Phase == EnginePhase.Stopped && Seconds(p.Cycle) > StallAt);
        Assert.True(Seconds(stopped.Cycle) < CrankAgainAt - 0.5, $"the engine stopped at {Seconds(stopped.Cycle):0.000} s");
        var still = r.Trace.Where(t => t.Seconds > Seconds(stopped.Cycle) + 0.3 && t.Seconds < CrankAgainAt).ToList();
        Assert.NotEmpty(still);
        Assert.All(still, t => Assert.NotEqual(2.0, F900(t)));           // the firmware saw it stop: the crank is no longer synchronised
        Assert.Null(cycle.Milestones.KeyOff);
        Assert.Null(cycle.Milestones.PowerOff);                         // the key stayed on and the main relay held
    }

    [Fact] public void Crank_AfterAStall_StartsTheEngineAgain_InTheSamePowerCycle()
    {
        var r = runs.Restart;
        var cycle = Assert.Single(r.PowerCycles);
        var phases = cycle.Engine!.Phases;
        var starter = phases.Last(p => p.Phase == EnginePhase.Starter);
        Assert.Equal(CrankAgainAt, Seconds(starter.Cycle), 2);
        Assert.Contains(phases, p => p.Phase == EnginePhase.Running && p.Cycle > starter.Cycle);
        Assert.Equal(EngineOutcome.Running, cycle.Engine.Outcome);
        Assert.Equal(2.0, F900(r.Trace[^1]));
    }

    [Fact] public void Checks_JudgeARestartAfterAStall_LikeAFirstStart()
    {
        var r = runs.Restart;
        Assert.True(r.Checks.All(c => c.Findings.Count == 0), Why(r));
        Assert.Equal(RunVerdict.Pass, r.Verdict);
    }

    [Fact] public void AStopInAnIdleSegment_StillFailsTheEngineCheck()
    {
        // No stall line: the plant is starved 2 s into the idle (line 6), so the engine stops in an idle segment.
        var r = RestartRuns.Run("3 on\n1.5 crank rpm=200\n5 idle\n", At(6.5, (_, engine) => engine.Starve(true)));
        var engine = r.Check("engine");
        Assert.Equal(CheckStatus.Fail, engine.Status);
        Assert.Contains(engine.Findings, f => f.Message.Contains("the engine stopped during the idle at line 6"));
    }

    [Fact] public void TheScheduler_RestartsItsWindow_AtTheRestartsFirstTooth()
    {
        // The dyno stops the shaft at 900 rpm, so the crank task's last run before the stop is at 900 rpm: R3 must not
        // judge the gap from it to the first run after the restart.
        var r = RestartRuns.Run("3 on\n1.5 crank rpm=200\n5 idle\n1 drive gear=N rpm=900 speed=0\n0.5 drive gear=N rpm=0 speed=0\n"
            + "2 stall\n1.5 crank rpm=200\n5 idle\n");
        Assert.Equal(EngineOutcome.Running, Assert.Single(r.PowerCycles).Engine!.Outcome);
        var check = r.Check("scheduler");
        Assert.True(check.Findings.Count == 0, string.Join("; ", check.Findings.Select(f => f.Message)));
    }

    // before-rotation's re-arm at a stop has its failing side on a synthetic program (OutputChecksTests): no test writes a
    // pin while the car file runs (plan 5 final review I2).
}
