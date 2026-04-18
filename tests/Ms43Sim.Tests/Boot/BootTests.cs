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
        Assert.True(r.SrvwdtCount >= 70, $"{r.SrvwdtCount} SRVWDT");  // key on: about every 10 ms (79 in 20 M cycles)
        Assert.Equal((ushort)0xC000, r.Passes[0].RamTestWord);        // tag invalid (bit 15) + 16 KiB fail (bit 14)
        Assert.Null(r.Passes[0].IdCheck);
    }

    [Fact] public void ProgramAreaErased_KeyOff_DropsTheMainRelay_AndWaitsForKl15()
    {
        // 00:1F5A: latch := 58h (relay released), then SRVWDT and an AN1 sample until it reaches
        // C3h, then SRST. Plan 1, without an ADC, read AN1 as 0 V and only ever saw this path.
        var m = Machine.Create(Erased(0x10000, 0x70000),
            new MachineOptions { Inputs = BoardInputs.KeyOnEngineStopped.WithIgnition(false) });
        m.PowerOn();
        m.Run(20_000_000);
        Assert.Equal((0, false), (m.WatchdogResets + m.SoftwareResets, m.Board.Latch.MainRelayHeld));
        Assert.True(m.Watchdog.ServiceCount >= 100, $"{m.Watchdog.ServiceCount} SRVWDT");
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

    [Fact] public void Assumptions_ListEverySimplification()
    {
        var r = BootRun.Run(FlashImageBuilder.Raw(FirmwareImage.FromBytes(new byte[0x80000])), new(MaxCycles: 1_000));
        var all = string.Join("\n", r.Assumptions);
        var phrases = new[]
        {
            "00'F200-00'F5FF reads 0000h", "XRAM and the CAN module are external accesses of 2 states",
            "RWDC and ALECTL", "MUL/DIV are not interruptible", "division: on V", "given as X or U",
            "UM Table 13-2", "SYSCON cannot be written after EINIT", "class A traps wait for the end",
            "class B trap flag is pending", "flag updates are undone", "stack-limit traps push the IP",
            "STKSZ", "AM29F400", "ISM §7.3", "pipeline effects",
        };
        Assert.Empty(phrases.Where(p => !all.Contains(p)));
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
