using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using static Ms43Sim.Tests.Support.CheckRuns;

namespace Ms43Sim.Tests.Checks;

public class CheckSetTests
{
    /// <summary>Finds "power cycle N" at each power-on and, in the first cycle, the same cause twice more.</summary>
    sealed class EveryPowerOn() : CheckBase("every-power-on")
    {
        protected override void Start(Machine machine, int index)
        {
            Find($"cycle {index}", $"power cycle {index + 1}");
            if (index == 0)
            {
                machine.Scheduler.Schedule(24_000_000, "probe", () => Find("cycle 0", "again"));
                machine.Scheduler.Schedule(48_000_000, "probe", () => Find("cycle 0", "and again"));
            }
        }
    }

    [Fact] public void ChecksSeeEventsTheCaptureDropped()                          // Review Focus 2, final review I7
    {
        // the capture keeps only the first 8 output events; the long-dwell bin's dwells come after them, so
        // dwell can fail only on events the capture dropped: the checks subscribe to the machine
        var image = InMemoryBin.With(TestBins.CarBytes, (0x7C92E, new byte[] { 0x53, 0x07 }));   // R3
        var r = DriveRunWithChecks(DriveBuilder.Named("warm-start"), image, captureCapacity: 8);
        Assert.True(r.OutputEventsDropped > 0);
        Assert.Equal(CheckStatus.Fail, r.Check("dwell").Status);
        Assert.True(r.Check("dwell").Findings[0].FirstCycle > r.OutputEvents[^1].Cycle);
    }

    [Fact] public void Findings_AccumulateAcrossPowerCycles_AndMergeByCause()
    {
        var probe = new EveryPowerOn();
        var r = DriveRunWithChecks(DriveBuilder.Named("key-cycle"), extra: probe);
        Assert.Equal(2, r.PowerCycles.Count);
        Assert.Equal(CheckStatus.Fail, probe.Status);
        var first = Assert.Single(probe.Findings, f => f.Message == "power cycle 1");
        Assert.Equal((3, 0UL, 48_000_000UL), (first.Count, first.FirstCycle, first.LastCycle));   // merged, in the drive's time
        var second = Assert.Single(probe.Findings, f => f.Message == "power cycle 2");
        Assert.Equal(r.PowerCycles[1].StartCycle, second.FirstCycle);
        Assert.Same(probe, r.Check("every-power-on"));
        foreach (var name in new[] { "traps", "resets", "execution", "boot", "checksums", "monitoring" })
            Assert.Equal(CheckStatus.Pass, r.Check(name).Status);              // both cycles clean
    }
}
