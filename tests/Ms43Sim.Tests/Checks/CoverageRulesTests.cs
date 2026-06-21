using Ms43Sim.Core;

namespace Ms43Sim.Tests.Checks;

/// <summary>What the coverage declares benign, and only that (R4, R14; plan 4 Task 13).</summary>
public class CoverageRulesTests
{
    [Theory]
    [InlineData(0x4286Cu, 0x0C6CEEu, true, true)]                               // R4: 95.8 a second
    [InlineData(0x4289Au, 0x0E257Cu, true, true)]                               // once per after-run
    [InlineData(0x42000u, 0x0E28DCu, true, true)]                               // once per after-run
    [InlineData(0x4286Cu, 0x0E257Cu, true, false)]                              // the address from other code
    [InlineData(0x4286Cu, 0x0C6CEEu, false, false)]                             // a word write
    [InlineData(0x40000u, 0x0C6CEEu, true, false)]                              // another address
    public void OnlyTheCarsOwnLoneFlashByteWrites_AreBenign(uint address, uint pc, bool isByte, bool benign) =>
        Assert.Equal(benign, BenignAccesses.IsBenignFlashWrite(new FlashWriteEvent(0, pc, address, 0x5F, isByte)));

    static FaultEntry Entry(int path, int type, bool stored) =>
        new(path, 0x12, "cam_ex", 0, type, "P0365", stored, false, false, false, false, false, 1, 0, Array.Empty<byte>());

    [Fact] public void TheExhaustCamsNoSignal_IsExplainedOnlyDetectedAndAbove6000Rpm()
    {
        Assert.NotNull(FaultMemory.ExplanationAtSpeed(Entry(3, 2, stored: false), maxRpm: 6900));
        Assert.Null(FaultMemory.ExplanationAtSpeed(Entry(3, 2, stored: true), maxRpm: 6900));     // stored: never explained
        Assert.Null(FaultMemory.ExplanationAtSpeed(Entry(3, 2, stored: false), maxRpm: 5900));    // the cycle never reached 6,000
        Assert.Null(FaultMemory.ExplanationAtSpeed(Entry(3, 3, stored: false), maxRpm: 6900));    // another type
        Assert.Null(FaultMemory.ExplanationAtSpeed(Entry(2, 2, stored: false), maxRpm: 6900));    // another path
    }
}
