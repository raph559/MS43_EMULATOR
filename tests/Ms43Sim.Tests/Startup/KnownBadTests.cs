using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

/// <summary>
/// Spec §8.8: three known-bad bins fail, never pass. Each is built in memory from the car file and never
/// written to disk (owner rule).
/// </summary>
public sealed class KnownBad
{
    public KnownBad()
    {
        var runs = new Func<DriveResult>[]
        {
            () => DriveRun.RunWithChecks(InMemoryBin.With(TestBins.CarBytes, (0x50000, Enumerable.Repeat((byte)0xFF, 0x10000).ToArray())), StandardDrives.Load("warm-start")),
            () => DriveRun.RunWithChecks(InMemoryBin.With(TestBins.CarBytes, (0x7C92E, new byte[] { 0x53, 0x07 })), StandardDrives.Load("warm-start")),
            () => DriveRun.RunWithChecks(Overlay.Over(TestBins.CarBytes), StandardDrives.Load("warm-start")),
            () => DriveRun.RunWithChecks(Overlay.Over(TestBins.CarBytes), DriveParser.Parse("drive k\n20 on")),
        }.AsParallel().AsOrdered().Select(run => run()).ToArray();
        (ErasedSector, LongDwell, OverlayWarm, OverlayKeyOn) = (runs[0], runs[1], runs[2], runs[3]);
    }

    public DriveResult ErasedSector { get; }

    public DriveResult LongDwell { get; }

    public DriveResult OverlayWarm { get; }

    public DriveResult OverlayKeyOn { get; }
}

[Trait("Category", "StandardDrives")]
public class KnownBadTests(KnownBad f) : IClassFixture<KnownBad>
{
    [Fact] public void ErasedProgramSector_FailsTheWarmStart() => Assert.Equal(RunVerdict.Fail, f.ErasedSector.Verdict);

    [Fact] public void LongDwellEdit_FailsTheWarmStart()                           // R3
    {
        Assert.Equal(RunVerdict.Fail, f.LongDwell.Verdict);
        Assert.Equal(CheckStatus.Fail, f.LongDwell.Check("dwell").Status);
    }

    [Fact] public void Overlay_FailsAWarmStart_ButAKeyOnRunCannotSeeIt()           // Review Focus 1
    {
        Assert.Equal(RunVerdict.Fail, f.OverlayWarm.Verdict);                     // ILLINA trap / reset at ~4.31 s (R3)
        Assert.Contains(f.OverlayWarm.Check("traps").Findings, x => x.Message.Contains("IllegalInstructionAccess"));   // each on its own (final review I8)
        Assert.Equal(CheckStatus.Fail, f.OverlayWarm.Check("resets").Status);
        Assert.Contains(f.OverlayWarm.Check("engine").Findings, x => x.Message.Contains("stopped during the idle"));   // the stall rule's failing side
        Assert.NotEqual(RunVerdict.Pass, f.OverlayKeyOn.Verdict);                 // incomplete: a code patch makes the RAM checks not evaluated
        Assert.Contains(f.OverlayKeyOn.Coverage!.NotEvaluated, s => s.Contains("program"));
    }

    [Fact] public void TheOverlayFixture_IsTheFrozenBuild_AndSparesTheBootBlockAndTags() =>
        Assert.NotEmpty(Overlay.Records());                                       // the SHA-256 and the boot-block and tag guard are checked on load
}
