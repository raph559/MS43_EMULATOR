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
}
