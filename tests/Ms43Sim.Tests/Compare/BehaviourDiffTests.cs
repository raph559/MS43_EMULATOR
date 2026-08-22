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
        var rows = Enumerable.Range(0, 100).Select(n => Row(n / 10.0, trace)).ToList();
        var outputs = new List<OutputEvent>
        {
            new(0, OutputKind.Pwm, "PWM0", 300, null), new(0, OutputKind.Pwm, "PWM1", 200, null), new(0, OutputKind.Pwm, "PWM2", 500, null),
        };
        return new RunTrack(sparks, injections, rows, outputs, Array.Empty<FaultEntry>(), new[] { CycleEnd.DriveEnded }, new byte[2048]);
    }

    static TraceRow Row(double t, Func<string, double, double, double>? trace = null) =>
        new(t, VariableTrace.Columns.Select(c => (double?)(trace is null ? Value(c, t) : trace(c, t, Value(c, t)))).ToArray());

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
        // Cylinder 3 in the seventh and eighth engine cycles: two in a row.
        var gone = new[] { FirstSpark + 6 * EngineCycle + 2 * EngineCycle / 6, FirstSpark + 7 * EngineCycle + 2 * EngineCycle / 6 };
        var diffs = Diff(Track(), Track(s => gone.Contains(s.Fire) ? null : s));
        var missing = Assert.Single(diffs);
        Assert.Equal(("spark.missing", (int?)3, 2, gone[0], gone[1]), (missing.Signal, missing.Cylinder, missing.Count, missing.FromCycle, missing.ToCycle));
        Assert.Equal((1.0, 0.0), (missing.Reference, missing.Candidate));
    }

    static readonly ulong[] Cylinder3 = Enumerable.Range(0, 20).Select(k => FirstSpark + (ulong)k * EngineCycle + 2 * EngineCycle / 6).ToArray();

    [Fact] public void ALoneMissingSpark_InSteadyRunning_IsADifference()                // plan 5 final review I3
    {
        // Cylinder 3's spark in the seventh engine cycle is gone, with its sparks either side one engine cycle away.
        var missing = Assert.Single(Diff(Track(), Track(s => s.Fire == Cylinder3[6] ? null : s)));
        Assert.Equal(("spark.missing", (int?)3, 1, Cylinder3[6]), (missing.Signal, missing.Cylinder, missing.Count, missing.FromCycle));
    }

    [Fact] public void ALoneMissingSpark_AtACutsEdge_IsATimeShift_NotADifference()
    {
        // The reference cuts cylinder 3 from its eighth engine cycle to its twelfth, the candidate from its seventh: a cut
        // that began one event earlier leaves the reference's seventh unpaired, beside its own cut (R5 drift).
        bool Cut(Spark s, int from) => s.Cylinder == 3 && Array.IndexOf(Cylinder3, s.Fire) is var k && k >= from && k <= 11;
        Assert.Empty(Diff(Track(s => Cut(s, 7) ? null : s), Track(s => Cut(s, 6) ? null : s)));
    }

    [Fact] public void ALoneDecisionFarBeyondOneStep_IsADifference()
    {
        // A torn read or a race: one decision of cylinder 3, 10° off.
        var glitch = Assert.Single(Diff(Track(), Track(s => s.Fire == Cylinder3[6] ? s with { DecidedAdvance = 20.5 } : s)));
        Assert.Equal(("spark.decided_advance", (int?)3, 1, 10.0), (glitch.Signal, glitch.Cylinder, glitch.Count, glitch.MaxDifference));
    }

    [Fact] public void OneEngineCycleWrongOnEveryCylinder_IsADifferenceOnEach()
    {
        var cycle = (FirstSpark + 6 * EngineCycle, FirstSpark + 7 * EngineCycle);
        var diffs = Diff(Track(), Track(s => s.Fire >= cycle.Item1 && s.Fire < cycle.Item2 ? s with { DecidedAdvance = 20.5 } : s));
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5, 6 }, diffs.Where(d => d.Signal == "spark.decided_advance").Select(d => d.Cylinder).Order());
    }

    [Fact] public void ALoneSparkFiredFarFromItsDecision_IsADifference()
    {
        // As fired 3° off at one event, the decision unchanged: a missed or late compare.
        var glitch = Assert.Single(Diff(Track(), Track(s => s.Fire == Cylinder3[6] ? s with { Advance = 13.5 } : s)));
        Assert.Equal(("spark.advance", (int?)3, 1), (glitch.Signal, glitch.Cylinder, glitch.Count));
    }

    [Fact] public void ALoneInjectionHalfAgainAsLong_IsADifference()
    {
        var reference = Track();
        var open = reference.Injections.First(i => i.Cylinder == 3 && i.Open > Cylinder3[6] - EngineCycle / 2).Open;
        var candidate = reference with { Injections = reference.Injections.Select(i => i.Open == open ? i with { Close = i.Close + 48_000 } : i).ToList() };
        var glitch = Assert.Single(Diff(reference, candidate));
        Assert.Equal(("injection.duration", (int?)3, 1), (glitch.Signal, glitch.Cylinder, glitch.Count));
    }

    [Fact] public void ADecisionChangeOneEventEarlier_IsNoDifference_ButOneThatLasts_Is()
    {
        // The reference's advance steps from 10.5° to 15° at 6.0 s; the candidate's one event of each cylinder earlier.
        var step = 6.0 * Second;
        var reference = Track(s => s.Fire >= step ? s with { Advance = 15, DecidedAdvance = 15 } : s);
        Assert.Empty(Diff(reference, Track(s => s.Fire + EngineCycle >= step ? s with { Advance = 15, DecidedAdvance = 15 } : s)));
        var lasting = Diff(reference, Track(s => s.Fire >= step ? s with { Advance = 15.75, DecidedAdvance = 15.75 } : s));
        Assert.Equal(6, lasting.Count(d => d.Signal == "spark.decided_advance"));
    }

    [Fact] public void ADecisionThatFlickersOneStepAtOneEvent_IsNoDifference_ButTwoEventsInARowAre()
    {
        // An input on a breakpoint, sampled a few cycles later, tips one decision one step (0.375°), and the spark as fired
        // with it.
        Assert.Empty(Diff(Track(), Track(s => s.Fire == Cylinder3[6] ? s with { DecidedAdvance = 10.875, Advance = 10.875 } : s)));
        var two = Diff(Track(), Track(s => s.Fire == Cylinder3[6] || s.Fire == Cylinder3[7] ? s with { DecidedAdvance = 10.875 } : s));
        Assert.Equal(("spark.decided_advance", (int?)3, 2), (Assert.Single(two).Signal, two[0].Cylinder, two[0].Count));
    }

    [Fact] public void ATraceStepOneSampleLater_IsNoDifference()
    {
        // ti1_ms steps from 2 to 3 ms at 6.0 s in the reference, at 6.1 s in the candidate: a value a few cycles later.
        Assert.Empty(Diff(Track(trace: (c, t, v) => c == "ti1_ms" ? (t >= 5.95 ? 3 : 2) : v),
            Track(trace: (c, t, v) => c == "ti1_ms" ? (t >= 6.05 ? 3 : 2) : v)));
    }

    [Fact] public void APwmShare_IsAveragedOverEachSample_SoADitherOutOfPhase_IsNoDifference()
    {
        // The throttle's drive dithers every 5 ms between 1000 and 340 ‰; the candidate's dither is 2.5 ms later.
        static RunTrack Dither(ulong phase)
        {
            var track = Track();
            var events = Enumerable.Range(0, 1000).Select(k => new OutputEvent(5 * Second + phase + (ulong)k * 60_000,
                OutputKind.Pwm, "PWM2", k % 2 == 0 ? 1000 : 340, null));
            return track with { Outputs = track.Outputs.Concat(events).ToList() };
        }
        Assert.Empty(Diff(Dither(0), Dither(60_000)));
    }

    [Fact] public void TheEepromCopyOfTheFlashChecksums_IsLeftOut()
    {
        // Words 1EEh-1F0h: the program CRC, the calibration CRC and the record's CRC-16/ARC — any code change moves them.
        var copy = new byte[2048];
        copy[0x3DC] = 0x9F; copy[0x3E0] = 0xC3;
        Assert.Empty(Diff(Track(), Track() with { Eeprom = copy }));
        copy[0x3E2] = 0x01;
        Assert.Equal("eeprom", Assert.Single(Diff(Track(), Track() with { Eeprom = copy })).Signal);
    }

    [Fact] public void ATraceDifferenceOfOneSample_IsNotADifference_ButTwoSamplesAre()
    {
        Assert.Empty(Diff(Track(), Track(trace: (c, t, v) => c == "iga1_deg" && Math.Abs(t - 6.0) < 1e-9 ? v + 0.375 : v)));
        var two = Assert.Single(Diff(Track(), Track(trace: (c, t, v) => c == "iga1_deg" && t is > 5.95 and < 6.15 ? v + 0.375 : v)));
        Assert.Equal(("iga1_deg", (int?)null, 2), (two.Signal, two.Cylinder, two.Count));
        Assert.Equal((6.0, 6.1), (two.FromCycle / 24e6, two.ToCycle / 24e6));
    }

    [Fact] public void SamplesPairByDriveTime_NotByTheirIndex()                            // plan 5 final review I5
    {
        // ti1_ms holds the drive time. The candidate's ECU is off from 5.0 s to 5.25 s and samples again from its power-on:
        // by index, every later sample would be set beside one 0.25 s earlier.
        static double Clock(string c, double t, double v) => c == "ti1_ms" ? t : v;
        var reference = Track(trace: Clock);
        var candidate = reference with
        {
            Trace = reference.Trace.Where(r => r.Seconds < 4.95).Concat(Enumerable.Range(0, 47).Select(n => Row(5.25 + n / 10.0, Clock))).ToList(),
        };
        Assert.Empty(Diff(reference, candidate));
    }

    [Fact] public void APowerCycleStartingOrEndingAtAnotherTime_IsADifference()
    {
        var reference = Track() with
        {
            Ends = new[] { CycleEnd.AfterRun, CycleEnd.DriveEnded }, Cycles = new[] { (0UL, 6 * Second), (7 * Second, 10 * Second) },
        };
        var end = Assert.Single(Diff(reference, reference with { Cycles = new[] { (0UL, 6 * Second + Second / 2), (7 * Second, 10 * Second) } }));
        Assert.Equal(("power_cycles.end", 6 * Second, 0.5, 6.0, 6.5), (end.Signal, end.FromCycle, end.MaxDifference, end.Reference, end.Candidate));
        var start = Assert.Single(Diff(reference, reference with { Cycles = new[] { (0UL, 6 * Second), (7 * Second + Second / 2, 10 * Second) } }));
        Assert.Equal(("power_cycles.start", 0.5), (start.Signal, start.MaxDifference));
        Assert.Empty(Diff(reference, reference with { Cycles = new[] { (0UL, 6 * Second + Second / 20), (7 * Second, 10 * Second) } }));
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
