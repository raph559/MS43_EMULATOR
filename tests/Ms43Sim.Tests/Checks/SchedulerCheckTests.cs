using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using static Ms43Sim.Tests.Support.CheckRuns;

namespace Ms43Sim.Tests.Checks;

public class SchedulerCheckTests
{
    static string Why(ICheck c) => string.Join("; ", c.Findings.Select(f => f.Message));

    [Fact] public void CarWarmStartAndIdle_PassTheScheduler()
    {
        var check = DriveRunWithChecks(DriveBuilder.Named("warm-start")).Check("scheduler");
        Assert.True(check.Status == CheckStatus.Pass, Why(check));
    }

    [Fact] public void APowerCycle_RestartsTheWindow()                      // a key-off then key-on must not read a gap across the off time
    {
        var r = DriveRunWithChecks(DriveBuilder.Named("key-cycle"));
        Assert.Equal(2, r.PowerCycles.Count);
        Assert.True(r.Check("scheduler").Status == CheckStatus.Pass, Why(r.Check("scheduler")));
        Assert.Empty(r.Check("scheduler").Findings);
    }

    [Fact] public void TheAppsConnect_PassesTheScheduler()
    {
        // The serial-line task T10ms:08h (09:FAAE) holds the connect's reply by arming itself for 40 ms (F910 = 4), and every
        // 10 ms again once the session runs: R2 judges each gap by the period the task armed, not by 10 ms.
        var r = DriveRunWithChecks(DriveParser.Parse("drive connect\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n"
            + "3 on\n1.5 crank rpm=200\n5 idle\nkline connect\nkline poll\n3 idle\n"));
        Assert.Contains(r.KLine, e => e.Outcome == KLineOutcome.Replied && e.Request[2] == 0x91);
        Assert.True(r.Check("scheduler").Status == CheckStatus.Pass, Why(r.Check("scheduler")));
    }

    [Fact] public void StockFile_NotEvaluated_ForTheScheduler()             // the stock program's hook sites differ
    {
        var r = DriveRunWithChecks(DriveBuilder.Named("warm-start"), TestImages.AsFlashed(TestBins.StockBytes));
        Assert.Equal(CheckStatus.NotEvaluated, r.Check("scheduler").Status);
    }
}
