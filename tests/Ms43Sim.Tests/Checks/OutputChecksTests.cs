using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using static Ms43Sim.Tests.Support.CheckRuns;

namespace Ms43Sim.Tests.Checks;

public class OutputChecksTests
{
    static readonly string[] Outputs = { "dwell", "injector", "throttle", "before-rotation", "stuck" };

    static string Why(ICheck c) => $"{c.Name}: {string.Join("; ", c.Findings.Select(f => f.Message))}";

    /// <summary>
    /// A program that misdrives its outputs from reset, before any crank tooth: DISWDT; P2 latch 0001h
    /// (injector P2.0 closed); DP2 = 0041h (P2.0 and coil P2.6 outputs: a set-up, no edge); P2 = 0040h
    /// (the coil dwells, the injector opens); P6 = DP6 = 00A0h (the throttle bridge enabled and closing
    /// at full drive); then a loop.
    /// </summary>
    static FlashedImage Misdriver()
    {
        var image = Enumerable.Repeat((byte)0xFF, FirmwareImage.Size).ToArray();
        Hex.Bytes("A55AA5A5" + "E6E00100" + "E6E14100" + "E6E04000" + "E6E6A000" + "E6E7A000" + "0DFF").CopyTo(image, 0);
        return FlashImageBuilder.Raw(FirmwareImage.FromBytes(image));
    }

    [Theory]
    [InlineData("full-load")]
    [InlineData("overrun")]
    [InlineData("part-load")]
    public void CarDrives_PassEveryOutputCheck(string name)
    {
        var r = DriveRunWithChecks(DriveBuilder.Named(name));
        foreach (var c in Outputs)
            Assert.True(r.Check(c).Status == CheckStatus.Pass, $"{name}: {Why(r.Check(c))}");
    }

    [Fact] public void ALongDwellEdit_FailsDwell()
    {
        var image = InMemoryBin.With(TestBins.CarBytes, (0x7C92E, new byte[] { 0x53, 0x07 }));   // R3
        var r = DriveRunWithChecks(DriveBuilder.Named("warm-start"), image);
        Assert.Equal(CheckStatus.Fail, r.Check("dwell").Status);
        Assert.Contains(r.Check("dwell").Findings, f => f.Message.Contains("ms"));
    }

    [Fact] public void CarWarmStart_PassesBeforeRotation_ItsKeyOnCoilSetUpIsNoEdge()
    {
        var r = DriveRunWithChecks(DriveBuilder.Named("warm-start"));
        Assert.True(r.Check("before-rotation").Status == CheckStatus.Pass, Why(r.Check("before-rotation")));
    }

    [Fact] public void AProgramThatMisdrivesItsOutputs_FailsDwellInjectorThrottleAndBeforeRotation()
    {
        var r = DriveRunWithChecks(DriveParser.Parse("drive x\n3 on"), Misdriver());
        Assert.Contains(r.Check("dwell").Findings, f => f.Message.Contains("8.0 ms"));
        Assert.Contains(r.Check("injector").Findings, f => f.Message.Contains("no crank tooth"));
        Assert.Contains(r.Check("injector").Findings, f => f.Message.Contains("into a stop of the crank"));
        Assert.Contains(r.Check("throttle").Findings, f => f.Message.Contains("300 ms"));
        Assert.Contains(r.Check("throttle").Findings, f => f.Message.Contains("stop"));
        Assert.Contains(r.Check("before-rotation").Findings, f => f.Message.Contains("P2.6"));
        Assert.Equal(CheckStatus.NotEvaluated, r.Check("stuck").Status);       // not the MS43X001 program
    }

    [Fact] public void BeforeRotation_ReArmsWhenTheEngineStops_UntilItsNextCrankTooth()      // plan 5 final review I2
    {
        // A synthetic program, not a poke into a car-file run: the starter turns the engine from 1 s to 2 s, and it runs
        // down and stops at about 2.9 s; at about 4.5 s the program opens and closes injector 1.
        var r = DriveRunWithChecks(DriveParser.Parse("drive x\n1 on\n1 crank rpm=200\n4 idle"),
            SyntheticPrograms.Image(handover: false, SyntheticPrograms.Wait(39), SyntheticPrograms.PulseInjector1, SyntheticPrograms.Hold));
        var finding = Assert.Single(r.Check("before-rotation").Findings);
        Assert.Contains("P2.0", finding.Message);
        Assert.Contains("while the engine was stopped, before its next crank tooth", finding.Message);
        Assert.InRange(finding.FirstCycle / 24e6, 4.0, 5.0);
    }

    const ulong Ms = 24_000;

    /// <summary>
    /// The release's idle drive: idling at 700 rpm, an injector switched 150 ms before the drive stepped
    /// the shaft to 2,500 rpm; three cycles at 2,500 rpm (144 ms) had passed, three at 700 (514 ms) had not.
    /// </summary>
    [Fact] public void AStepUpInSpeed_IsNoStuckOutput()
    {
        var rule = new StuckRule();
        rule.Switched(0);
        for (ulong t = 1; t <= 150; t++)
            Assert.Null(rule.Tick(t * Ms, true, 700));
        Assert.Null(rule.Tick(151 * Ms, true, 2500));
    }

    [Fact] public void AnOutputThatStopsAtSpeed_IsStuck_OnceAWindow()
    {
        var rule = new StuckRule();
        Assert.Null(rule.Tick(0, true, 2500));                                // the predicate holds from 0
        rule.Switched(0);
        for (ulong t = 1; t <= 144; t++)
            Assert.Null(rule.Tick(t * Ms, true, 2500));                       // three cycles at 2,500 rpm: 144 ms
        Assert.Equal(0UL, rule.Tick(145 * Ms, true, 2500));                   // stuck since its last switch
        Assert.Null(rule.Tick(146 * Ms, true, 2500));                         // reported once
        Assert.Null(rule.Tick(147 * Ms, false, 2500));                        // the predicate drops: no rule
        for (ulong t = 148; t <= 292; t++)
            Assert.Null(rule.Tick(t * Ms, true, 2500));                       // it holds again: a new window from 148 ms
        Assert.Equal(148 * Ms, rule.Tick(293 * Ms, true, 2500));
    }
}

