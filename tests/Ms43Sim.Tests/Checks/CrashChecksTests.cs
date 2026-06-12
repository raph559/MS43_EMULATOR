using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using static Ms43Sim.Tests.Support.CheckRuns;

namespace Ms43Sim.Tests.Checks;

public class CrashChecksTests
{
    static readonly string[] Crash = { "traps", "resets", "execution", "boot", "checksums", "monitoring" };

    [Fact] public void CarWarmStart_PassesEveryCrashCheck()
    {
        var r = DriveRunWithChecks(DriveBuilder.Named("warm-start"));
        foreach (var name in Crash)
            Assert.True(r.Check(name).Status == CheckStatus.Pass, $"{name}: {string.Join("; ", r.Check(name).Findings.Select(f => f.Message))}");
    }

    [Fact] public void ErasedSector_FailsResets_AndExecution()
    {
        var image = InMemoryBin.With(TestBins.CarBytes, (0x50000, Enumerable.Repeat((byte)0xFF, 0x10000).ToArray()));
        var r = DriveRunWithChecks(DriveBuilder.Named("warm-start"), image);
        Assert.Equal(CheckStatus.Fail, r.Check("resets").Status);
        Assert.Contains(r.Check("execution").Findings, f => f.Message.Contains("0D:F4F2"));
    }

    [Fact] public void RawCarFile_FailsChecksums_EvenThoughTheMainLoopRunsAfterTheResets()
    {
        var r = DriveRunWithChecks(DriveBuilder.Named("warm-start"), FlashImageBuilder.Raw(FirmwareImage.FromBytes(TestBins.CarBytes)));
        Assert.Equal(CheckStatus.Fail, r.Check("checksums").Status);
        Assert.Equal(CheckStatus.Fail, r.Check("resets").Status);
    }

    [Fact] public void TheStockProgram_IsNotEvaluatedByTheChecksThatReadItsRam()
    {
        var r = DriveRunWithChecks(DriveParser.Parse("drive short\n3 on"), TestImages.AsFlashed(TestBins.StockBytes));
        Assert.Equal(CheckStatus.NotEvaluated, r.Check("checksums").Status);
        Assert.Contains("MS43X001", ((CheckBase)r.Check("checksums")).NotEvaluatedReason);
    }
}
