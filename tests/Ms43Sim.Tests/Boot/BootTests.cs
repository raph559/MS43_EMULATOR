using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Boot;

public class BootTests
{
    static FirmwareImage Car => FirmwareImage.FromBytes(TestBins.CarBytes);

    static FlashedImage Erased(int start, int end)
    {
        var b = FlashImageBuilder.AsFlashedFull(Car).Image.ToArray();
        Array.Fill(b, (byte)0xFF, start, end - start);
        return FlashImageBuilder.Raw(FirmwareImage.FromBytes(b));
    }

    [Fact] public void Car_AsFlashed_PassesTheBootChecksAndHandsOver()
    {
        var r = BootRun.Run(FlashImageBuilder.AsFlashedFull(Car), new(MaxCycles: 2_000_000, StopAtFirstHandover: true));
        var p = Assert.Single(r.Passes);
        Assert.True(r.CheckpointsRecognised);
        Assert.True(p.SelfTestPassed);
        Assert.Equal((ushort)0x4000, p.RamTestWord);                  // 8 KiB unit: only the 16 KiB test bit
        Assert.Equal((byte)0x20, p.IdCheck);                          // genuine, not forced
        Assert.Equal(0x0E366Au, r.HandoverTarget);
        Assert.NotNull(p.HandoverCycle);
        Assert.Empty(r.Events.OfType<TrapEvent>());
    }

    [Fact] public void Car_Raw_AlsoHandsOver_TheBootDoesNotCheckTheCalibrationCrc() =>
        Assert.NotNull(BootRun.Run(FlashImageBuilder.Raw(Car), new(MaxCycles: 2_000_000, StopAtFirstHandover: true)).Passes[0].HandoverCycle);

    [Fact] public void ProgramAreaErased_StaysInBootRecovery()
    {
        var r = BootRun.Run(Erased(0x10000, 0x70000), new(MaxCycles: 20_000_000));
        Assert.Equal(BootOutcome.StayedInBoot, r.Outcome);
        Assert.Equal(0, r.WatchdogResets);
        Assert.True(r.SrvwdtCount >= 100);
        Assert.Equal((ushort)0xC000, r.Passes[0].RamTestWord);        // tag invalid (bit 15) + 16 KiB fail (bit 14)
        Assert.Null(r.Passes[0].IdCheck);
    }

    [Fact] public void ProgramSectorErased_HandsOverThenResetLoops()
    {
        var r = BootRun.Run(Erased(0x50000, 0x60000), new(MaxCycles: 15_000_000));
        Assert.Equal(BootOutcome.ResetLoop, r.Outcome);
        Assert.True(r.WatchdogResets >= 2);
        Assert.Contains(r.Events.OfType<ErasedFlashExecutionEvent>(), e => e.Pc >> 16 == 0x0D);
        Assert.NotNull(r.Passes[0].HandoverCycle);
        Assert.Equal((ushort)0x4001, r.Passes[1].RamTestWord);        // second boot sees WDTR
    }

    [Fact] public void OddHandoverPointer_TrapsIllina()
    {
        var b = FlashImageBuilder.AsFlashedFull(Car).Image.ToArray();
        b[0x6ED06] = 0x6B;                                            // 366Ah → 366Bh
        var r = BootRun.Run(FlashImageBuilder.Raw(FirmwareImage.FromBytes(b)), new(MaxCycles: 2_000_000));
        Assert.Contains(r.Events.OfType<TrapEvent>(), e => e.Kind == TrapKind.IllegalInstructionAccess && e.PushedIp == 0x366B);
    }

    [Fact] public void ZeroedHandoverPointer_IsNotReportedAsAHandover()
    {
        var b = Car.ToArray();
        Array.Clear(b, 0x6ED06, 4);                                   // target 00:0000, the reset vector
        var r = BootRun.Run(FlashImageBuilder.AsFlashedFull(FirmwareImage.FromBytes(b)), new(MaxCycles: 2_000_000));
        Assert.NotEqual(BootOutcome.HandedOver, r.Outcome);
        Assert.All(r.Passes, p => Assert.Null(p.HandoverCycle));
        Assert.Contains(r.Assumptions, a => a.Contains("inside the boot block"));
    }

    [Fact] public void EndlessTraps_AreCountedPastTheLogCap_AndTheReportSaysHowMany()
    {
        var b = new byte[0x80000];
        b[0x00] = 0x8B;                                               // 00:0000: undefined opcode
        b[0x28] = 0xFB; b[0x29] = 0x88;                               // class B vector: RETI with TFR still set
        var r = BootRun.Run(FlashImageBuilder.Raw(FirmwareImage.FromBytes(b)), new(MaxCycles: 500_000));
        Assert.True(r.CountOf<TrapEvent>() > EventLog.TrapAndResetCapacity, $"{r.CountOf<TrapEvent>()} traps");
        Assert.Equal(EventLog.TrapAndResetCapacity, r.Events.Count(e => e is TrapEvent or ResetEvent));
        var md = BootReportWriter.Report(r);
        Assert.Contains($"Traps: {r.CountOf<TrapEvent>():N0}", md);
        Assert.Contains($"events not kept because the log was full: {r.EventsDropped:N0}", md);
    }

    [Fact] public void UnknownBootBlock_SkipsCheckpoints_WithoutThrowing()
    {
        var b = new byte[0x80000];                                    // all zero: 0000 = ADD R0,R0 forever
        var r = BootRun.Run(FlashImageBuilder.Raw(FirmwareImage.FromBytes(b)), new(MaxCycles: 1_000_000));
        Assert.False(r.CheckpointsRecognised);
        Assert.Contains(r.Assumptions, a => a.Contains("checkpoints"));
    }

    [Fact] public void SameImage_SameResult()
    {
        var o = new BootRunOptions(MaxCycles: 2_000_000, StopAtFirstHandover: true);
        var a = BootRun.Run(FlashImageBuilder.AsFlashedFull(Car), o);
        var b = BootRun.Run(FlashImageBuilder.AsFlashedFull(Car), o);
        Assert.Equal(a.StateHash, b.StateHash);
        Assert.Equal(a.Events, b.Events);
        Assert.Equal(a.Cycles, b.Cycles);
    }
}
