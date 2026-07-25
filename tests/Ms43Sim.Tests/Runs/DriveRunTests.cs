using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Runs;

public class DriveRunTests
{
    static double V(TraceRow row, string column) => row.Values[VariableTrace.Columns.ToList().IndexOf(column)]!.Value;

    [Fact] public void AWarmStartDrive_MatchesTheEngineScenario_AtIdle()
    {
        var drive = DriveParser.Parse("drive warm\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20\n"
            + "3 on\n1.5 crank rpm=200\n25 idle");
        var r = DriveRun.Run(TestImages.Car, drive);
        Assert.Single(r.PowerCycles);
        Assert.Equal(EngineOutcome.Running, r.PowerCycles[0].Engine!.Outcome);
        var idle = r.Trace.Last();
        Assert.InRange(V(idle, "rpm_ecu"), 880, 920);
        Assert.Empty(r.PowerCycles[0].Faults.Stored);
        Assert.InRange(r.Trace.Last().Seconds, 29.4, 29.5);
    }

    [Fact] public void ADriveSegment_TurnsTheEngineAtTheScriptedSpeed()
    {
        var drive = DriveParser.Parse("drive pull\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20\n"
            + "3 on\n1.5 crank rpm=200\n6 idle\n4 drive gear=3 speed=20..60 pedal=15");
        var r = DriveRun.Run(TestImages.Car, drive);
        var end = r.Trace.Last();
        Assert.InRange(V(end, "rpm_fed"), 60 * GearRatios.RpmPerKmh(3) - 30, 60 * GearRatios.RpmPerKmh(3) + 30);
        Assert.InRange(V(end, "rpm_ecu"), 2600, 2850);                         // the ECU follows the shaft
        Assert.InRange(V(end, "pedal_pct"), 13, 15);
        Assert.Empty(r.PowerCycles[0].Faults.Stored);
    }

    [Fact] public void KeyCycle_PowersOnAgainFromTheWrittenEeprom()
    {
        var drive = DriveParser.Parse("drive cycle\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20\n"
            + "3 on\n1.5 crank rpm=200\n10 idle\n15 off\n3 on\n1.5 crank rpm=200\n10 idle");
        var r = DriveRun.Run(TestImages.Car, drive);
        Assert.Equal(2, r.PowerCycles.Count);
        Assert.True(r.PowerCycles[0].Eeprom.WordsWritten > 0);                 // the after-run wrote the EEPROM in cycle 1
        Assert.NotNull(r.PowerCycles[0].Milestones.PowerOff);
        Assert.Equal(7, r.PowerCycles[1].Firmware.ThrottleCheckState);        // cycle 2 takes the short throttle check
        Assert.DoesNotContain(r.PowerCycles[1].Faults.Detected, f => f.Path == 12);   // no t_es: the stop record exists now
        Assert.Equal(EngineOutcome.Running, r.PowerCycles[1].Engine!.Outcome);
        Assert.Equal((ulong)Math.Round(29.5 * 24e6), r.PowerCycles[1].StartCycle);   // the second key-on, in drive time
    }

    [Fact] public void Observers_SeeEveryPowerCycleBeforeItsPowerOn()
    {
        var drive = DriveParser.Parse("drive cycle\n3 on\n1.5 crank rpm=200\n4 idle\n15 off\n2 on");
        var seen = new List<(int Index, ulong Cycles, bool Same)>();
        Machine? last = null;
        var r = DriveRun.Run(TestImages.Car, drive, new DriveRunOptions(Observers: new Action<DriveContext>[]
        {
            context => context.PowerCycleStarted += (machine, index) =>
            {
                seen.Add((index, machine.Cycles, ReferenceEquals(machine, last)));
                last = machine;
            },
        }));
        Assert.Equal(new[] { (0, 0UL, false), (1, 0UL, false) }, seen);
        Assert.Equal(2, r.PowerCycles.Count);
    }

    [Fact] public void ARelayDropWhileTheDriveHasTheKeyOn_LeavesTheRestUnrun_SoTheRunIsIncomplete()   // final review I3
    {
        // AN1 forced to 0 V reads as the key off: the firmware runs its after-run and drops the main relay
        // while the drive still has the key on, and the drive's last segment never runs
        var drive = DriveParser.Parse(string.Join('\n', "drive dropped", "start blank", "set coolant=90 oil=90 intake=33 ambient=20",
            "3 on", "1.5 crank rpm=200", "6 idle", "30 idle force=AN1:0", "10 idle"));
        var r = DriveRun.RunWithChecks(TestImages.Car, drive);
        Assert.Equal(RunVerdict.Incomplete, r.Verdict);
        Assert.Equal(CycleEnd.RelayDroppedKeyReadOff, Assert.Single(r.PowerCycles).End);
        Assert.Contains(r.Coverage!.NotRun, line => line.Contains("main relay dropped") && line.EndsWith("did not run again"));
        Assert.Equal(CheckStatus.Pass, r.Check("resets").Status);                // the firmware read the key off: it did right
        Assert.Contains("- ended: **the main relay dropped while the drive had the key on**", DriveReportWriter.Report(r));
    }

    [Fact] public void ADriveWhoseEngineNeverRuns_IsIncomplete()                     // final review I6
    {
        // a key-on drive sees no start, spark or injection: a pass would vouch for outputs it never judged
        var r = DriveRun.RunWithChecks(TestImages.Car, DriveParser.Parse("drive k\n10 on"));
        Assert.Equal(RunVerdict.Incomplete, r.Verdict);
        Assert.Contains(r.Coverage!.NotRun, line => line.Contains("never cranks"));
        Assert.Equal(CycleEnd.DriveEnded, Assert.Single(r.PowerCycles).End);
    }

    [Theory]                                                                       // final review I3
    [InlineData(false, true, true, CycleEnd.DriveEnded)]
    [InlineData(true, false, false, CycleEnd.AfterRun)]
    [InlineData(true, true, true, CycleEnd.RelayDroppedKeyOn)]                     // the ECU switched itself off: resets fails it
    [InlineData(true, true, false, CycleEnd.RelayDroppedKeyReadOff)]               // AN1 forced: the drive could not go on
    public void ACycleEnds_ByTheRelay_TheDrivesKey_AndTheKeyAsRead(bool dropped, bool driveKeyOn, bool readOn, CycleEnd end) =>
        Assert.Equal(end, DriveRun.EndOf(dropped, driveKeyOn, readOn));

    [Theory]                                                                       // plan 5 Task 6: the firmware's SRST after the drop
    [InlineData(true, true, true, CycleEnd.Restarted)]
    [InlineData(true, false, false, CycleEnd.AfterRun)]                            // a drop with the key off powers the ECU off first
    [InlineData(false, true, true, CycleEnd.DriveEnded)]
    public void AReset_AfterADropWithTheKeyOn_IsARestart(bool dropped, bool driveKeyOn, bool readOn, CycleEnd end) =>
        Assert.Equal(end, DriveRun.EndOf(dropped, driveKeyOn, readOn, restarted: dropped && driveKeyOn));

    [Fact] public void AnOffWithItsAfterRun_EndsTheCycleNormally()
    {
        var r = DriveRun.RunWithChecks(TestImages.Car, DriveBuilder.Named("key-cycle"));
        Assert.Equal(new[] { CycleEnd.AfterRun, CycleEnd.DriveEnded }, r.PowerCycles.Select(c => c.End));
        Assert.Empty(r.Coverage!.NotRun);
    }

    [Fact] public void EqualDriveRuns_GiveTheSameHash()
    {
        var drive = DriveParser.Parse("drive x\nstart blank\n3 on\n1.5 crank rpm=200\n4 idle");
        Assert.Equal(DriveRun.Run(TestImages.Car, drive).StateHash, DriveRun.Run(TestImages.Car, drive).StateHash);
    }

    [Fact] public void InMemoryBin_EditsACopy_AndNeverTheSource()
    {
        var source = TestBins.CarBytes;
        var before = source[0x60000];
        var image = InMemoryBin.With(source, (0x60000, new byte[] { (byte)~before }));
        Assert.Equal((byte)~before, image.Image.Bytes[0x60000]);
        Assert.Equal(before, TestBins.CarBytes[0x60000]);
    }
}
