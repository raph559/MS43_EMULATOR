using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

/// <summary>
/// R14: above 6,000 rpm the firmware's segment task can run late in the emulator; in a 60 s research run
/// at full throttle with the limiter bouncing, one late read of an empty airflow buffer stored P0103.
/// The acceptance drive holds just below the limiter and crosses it for one second. Task 11 found the
/// emulator's timing to be the ISM's minimum for external execution (§7.2), so it stands: no airflow
/// fault appears in this drive, and the car's own task cadence above 2,000 rpm is declared, not judged.
/// </summary>
public class HighRpmTests
{
    [Fact] public void CarFullLoad_IsCleanAtTheLimiter()
    {
        var r = DriveRun.RunWithChecks(TestImages.Car, DriveBuilder.Named("full-load"));
        Assert.True(r.Verdict == RunVerdict.Pass, string.Join("; ", Verdict.Reasons(r.Checks, r.Coverage!)));
        Assert.DoesNotContain(r.PowerCycles[^1].Faults.Stored, f => f.Path == 0);    // path 0 = maf (P0102/P0103)
        Assert.DoesNotContain(r.PowerCycles[^1].Faults.Detected, f => f.Path == 0);
        Assert.DoesNotContain(r.Coverage!.BenignNotes, n => n.Contains("R2 and R4 were not judged"));   // a gap, not a benign access (final review I5)
        Assert.Contains(r.Coverage.NotJudged, n => n.Contains("R2 and R4 were not judged"));             // the narrowing is declared (Task 11)
        Assert.True(((CheckBase)r.Check("scheduler")).Exercised);                                // R1 and R3 judged throughout
    }
}
