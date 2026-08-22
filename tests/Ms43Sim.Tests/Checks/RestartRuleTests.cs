using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using static Ms43Sim.Tests.Support.SyntheticPrograms;

namespace Ms43Sim.Tests.Checks;

/// <summary>
/// The firmware's restart and nothing more (plan 5 final review I1). The car file drops its relay with the key on only at
/// the end of a power-down write the key-off began (QuickKeyCycleTests); these failing sides run synthetic programs.
/// </summary>
public class RestartRuleTests
{
    static DriveResult Run(string drive, bool handover, params string[] code) =>
        DriveRun.RunWithChecks(SyntheticPrograms.Image(handover, code), DriveParser.Parse("drive synthetic\n" + drive));

    [Fact] public void AReleaseBeforeTheHandover_DoesNotExcuseAReset()
    {
        // released with the key on, then SRST, again and again: no handover, so no power cycle ends and no restart is due
        var r = Run("1 on", handover: false, ReleaseRelay, Wait(1), Reset);
        Assert.True(r.SoftwareResets >= 2, $"{r.SoftwareResets} SRST");
        Assert.Contains(r.Check("resets").Findings, f => f.Message.StartsWith("a software reset (SRST)"));
    }

    [Fact] public void ADropWithTheKeyOn_OutsideAPowerDownWrite_IsTheEcuSwitchingItselfOff_ThoughTheKeyWentOffBefore()
    {
        // the key off from 1 s to 1.5 s; at about 3 s, with the key on, the program drops the relay and resets itself
        var r = Run("1 on\n0.5 off\n3.5 on", handover: true, Wait(26), ReleaseRelay, Wait(1), Reset);
        var resets = r.Check("resets");
        Assert.Equal(CycleEnd.RelayDroppedKeyOn, r.PowerCycles[0].End);
        Assert.Contains(resets.Findings, f => f.Message.StartsWith("power cycle 1: the main relay dropped with the key on"));
        Assert.Contains(resets.Findings, f => f.Message.StartsWith("a software reset (SRST)"));
        Assert.Equal(r.PowerCycles[0].EndCycle, r.PowerCycles[1].StartCycle);     // the drive goes on from that reset
        Assert.DoesNotContain(r.Coverage!.NotRun, line => line.Contains("main relay dropped"));
    }

    [Fact] public void ADropWithTheKeyOn_IsJudgedAtTheDrop_NotWhenTheKeyGoesOffAfter()
    {
        // the program drops the relay at about 3 s with the key on and waits; the drive turns the key off at 3.5 s
        var r = Run("3.5 on\n1 off", handover: true, Wait(26), ReleaseRelay, Hold);
        Assert.Equal(CycleEnd.RelayDroppedKeyOn, Assert.Single(r.PowerCycles).End);
        Assert.Contains(r.Check("resets").Findings, f => f.Message.StartsWith("power cycle 1: the main relay dropped with the key on"));
    }
}
