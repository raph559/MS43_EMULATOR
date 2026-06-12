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
