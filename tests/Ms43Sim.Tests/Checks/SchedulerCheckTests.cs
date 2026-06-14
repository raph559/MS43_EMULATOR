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

    [Fact] public void StockFile_NotEvaluated_ForTheScheduler()             // the stock program's hook sites differ
    {
        var r = DriveRunWithChecks(DriveBuilder.Named("warm-start"), TestImages.AsFlashed(TestBins.StockBytes));
        Assert.Equal(CheckStatus.NotEvaluated, r.Check("scheduler").Status);
    }
}
