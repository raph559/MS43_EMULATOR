using Ms43Sim.Core;

namespace Ms43Sim.Tests.Compare;

/// <summary>
/// The compare's behaviour diff (plan 5 Task 7) on tracks built in memory: 20 engine cycles at a steady 900 rpm from
/// 5.0 s, the six cylinders in the firing order 1-5-3-6-2-4, and a trace every 100 ms for 10 s whose rpm climbs
/// 10 rpm a second from 4.5 s.
/// </summary>
public class BehaviourDiffTests
{
    const double Rpm = 900;
    const ulong Second = 24_000_000;
    const ulong EngineCycle = 3_200_000;                                 // 120 × 24 MHz / 900 rpm
    const ulong FirstSpark = 5 * Second;
    static readonly int[] FiringOrder = { 1, 5, 3, 6, 2, 4 };

    static readonly Drive Drive = DriveParser.Parse("drive t\n3 on\n1.5 crank rpm=200\n10 idle\n");

    static double Value(string column, double t) => column switch
    {
        "rpm_fed" or "rpm_ecu" => t >= 4.5 ? Rpm + 10 * (t - 4.5) : 0,
        "maf_kgh" => 17,
        "coolant_c" => 90,
        "f900" => t >= 4.5 ? 2 : 0,
        _ => 0,
    };

    static RunTrack Track(Func<Spark, Spark?>? spark = null, Func<string, double, double, double>? trace = null)
    {
        var sparks = new List<Spark>();
        var injections = new List<Injection>();
        for (var cycle = 0; cycle < 20; cycle++)
        {
            for (var i = 0; i < 6; i++)
            {
                var fire = FirstSpark + (ulong)cycle * EngineCycle + (ulong)i * EngineCycle / 6;
                var s = new Spark(FiringOrder[i], fire - 72_000, fire, 10.5, true, 10.5, 562);
                if ((spark is null ? s : spark(s)) is { } kept)
                    sparks.Add(kept);
                injections.Add(new Injection(FiringOrder[i], fire - 1_600_000, fire - 1_600_000 + 96_000, 360, 750, 750));
            }
        }
        var rows = Enumerable.Range(0, 100).Select(n =>
        {
            var t = n / 10.0;
            return new TraceRow(t, VariableTrace.Columns.Select(c => (double?)(trace is null ? Value(c, t) : trace(c, t, Value(c, t)))).ToArray());
        }).ToList();
        var outputs = new List<OutputEvent>
        {
            new(0, OutputKind.Pwm, "PWM0", 300, null), new(0, OutputKind.Pwm, "PWM1", 200, null), new(0, OutputKind.Pwm, "PWM2", 500, null),
        };
        return new RunTrack(sparks, injections, rows, outputs, Array.Empty<FaultEntry>(), new[] { CycleEnd.DriveEnded }, new byte[2048]);
    }

    static IReadOnlyList<BehaviourDifference> Diff(RunTrack reference, RunTrack candidate) =>
        BehaviourDiff.Of(reference, candidate, Drive, CompareTolerances.Default);

    static bool Within(Spark s, double from, double to) => s.Fire >= from * Second && s.Fire < to * Second;

    [Fact] public void SameTrackTwice_HasNoDifference() => Assert.Empty(Diff(Track(), Track()));

    [Fact] public void EveryTraceColumn_HasATolerance() =>
        Assert.Equal(VariableTrace.Columns.Order(), CompareTolerances.Default.TraceColumns.Keys.Order());

    [Fact] public void ARealDriveTwice_HasNoDifference()
    {
        // One bin and one drive give byte-identical runs (R5): the car file, a warm start and 3 s of idle.
        var drive = DriveParser.Parse("drive twice\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n3 on\n1.5 crank rpm=200\n3 idle\n");
        RunTrack Run() => RunTrack.From(DriveRun.Run(Support.TestImages.Car, drive));
        var reference = Run();
        Assert.NotEmpty(reference.Sparks);
        Assert.Empty(BehaviourDiff.Of(reference, Run(), drive, CompareTolerances.Default));
    }

    [Fact] public void SparksShiftedByAFewCycles_AreNoDifference()
    {
        // Two bins drift by cycles (R5): 37 cycles at 900 rpm is 0.008°.
        var candidate = Track(s => s with { DwellStart = s.DwellStart + 37, Fire = s.Fire + 37, Advance = s.Advance - 0.008 });
        Assert.Empty(Diff(Track(), candidate));
    }

    [Fact] public void AnAdvanceChangeBeyondTolerance_IsOneRangePerCylinder()
    {
        var candidate = Track(s => Within(s, 5.5, 6.5) ? s with { Advance = s.Advance + 0.5, DecidedAdvance = s.DecidedAdvance + 0.5 } : s);
        var advance = Diff(Track(), candidate).Where(d => d.Signal == "spark.advance").OrderBy(d => d.Cylinder).ToList();
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5, 6 }, advance.Select(d => d.Cylinder));
        Assert.All(advance, d =>
        {
            Assert.InRange(d.FromCycle, 5.5 * Second, 5.5 * Second + EngineCycle);
            Assert.InRange(d.ToCycle, 6.5 * Second - EngineCycle, 6.5 * Second);
            Assert.Equal(0.5, d.MaxDifference, 6);
            Assert.Equal((10.5, 11.0), (d.Reference, d.Candidate));
        });
        Assert.Equal(6, Diff(Track(), candidate).Count(d => d.Signal == "spark.decided_advance"));
    }

    [Fact] public void AMissingSpark_IsADifference_NotAnException()
    {
        var gone = FirstSpark + 6 * EngineCycle + 2 * EngineCycle / 6;   // cylinder 3 in the seventh engine cycle
        var diffs = Diff(Track(), Track(s => s.Fire == gone ? null : s));
        var missing = Assert.Single(diffs);
        Assert.Equal(("spark.missing", (int?)3, 1, gone, gone), (missing.Signal, missing.Cylinder, missing.Count, missing.FromCycle, missing.ToCycle));
        Assert.Equal((1.0, 0.0), (missing.Reference, missing.Candidate));
    }

    [Fact] public void ATraceDifferenceOfOneSample_IsNotADifference_ButTwoSamplesAre()
    {
        Assert.Empty(Diff(Track(), Track(trace: (c, t, v) => c == "iga1_deg" && Math.Abs(t - 6.0) < 1e-9 ? v + 0.375 : v)));
        var two = Assert.Single(Diff(Track(), Track(trace: (c, t, v) => c == "iga1_deg" && t is > 5.95 and < 6.15 ? v + 0.375 : v)));
        Assert.Equal(("iga1_deg", (int?)null, 2), (two.Signal, two.Cylinder, two.Count));
        Assert.Equal((6.0, 6.1), (two.FromCycle / 24e6, two.ToCycle / 24e6));
    }

    [Fact] public void TheOperatingPoint_IsTheReferencesAtTheLargestDifference()
    {
        // ti1_ms differs from 6.0 s to 6.4 s, the most at 6.2 s, where the reference turns at 917 rpm in the idle at line 4.
        var candidate = Track(trace: (c, t, v) => c == "ti1_ms" && t is > 5.95 and < 6.45 ? v + 1 - Math.Abs(t - 6.2) : v);
        var d = Assert.Single(Diff(Track(), candidate));
        Assert.Equal("ti1_ms", d.Signal);
        Assert.Equal(6.2, d.At.Seconds, 6);
        Assert.Equal((917.0, 17.0, 90.0, 0.0), (d.At.Rpm, d.At.MafKgh, d.At.CoolantC, d.At.SpeedKmh));
        Assert.Contains("line 4", d.At.Segment);
        Assert.Contains("idle", d.At.Segment);
    }

    [Fact] public void DifferentFaultMemories_AreADifference()
    {
        var fault = new FaultEntry(42, 0x2A, "a fault", 0x01, 1, "P0100", true, true, false, false, false, false, 1, 0, new byte[4]);
        var d = Assert.Single(Diff(Track(), Track() with { Faults = new[] { fault } }));
        Assert.Equal(("faults", 0.0, 1.0), (d.Signal, d.Reference, d.Candidate));
    }

    [Fact] public void DifferentPowerCycleEnds_AndEndEeproms_AreDifferences()
    {
        var eeprom = new byte[2048];
        eeprom[0x56] = 0x12;
        var diffs = Diff(Track(), Track() with { Ends = new[] { CycleEnd.AfterRun, CycleEnd.DriveEnded }, Eeprom = eeprom });
        Assert.Equal(new[] { "eeprom", "power_cycles" }, diffs.Select(d => d.Signal).Order());
    }

    [Fact] public void APwmShareDifferenceOfMoreThanOnePerMille_ForTwoSamples_IsADifference()
    {
        var reference = Track();
        var candidate = reference with { Outputs = reference.Outputs.Append(new OutputEvent(6 * Second, OutputKind.Pwm, "PWM0", 302, null)).ToList() };
        var d = Assert.Single(Diff(reference, candidate));
        Assert.Equal(("pwm.vanos_in", 300.0, 302.0), (d.Signal, d.Reference, d.Candidate));
        Assert.Equal(6.0, d.FromCycle / 24e6, 6);
    }
}
