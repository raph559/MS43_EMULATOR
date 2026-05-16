using Ms43Sim.Core;

namespace Ms43Sim.Tests.EngineModel;

public class EngineShaftTests
{
    sealed class Pins : ISignalPins
    {
        public Scheduler S = null!;
        public readonly List<(ulong Cycle, int Port, int Bit, bool Level)> Edges = new();
        public void Drive(int port, int bit, bool level) => Edges.Add((S.Now, port, bit, level));
    }

    static (Scheduler S, Pins P, EngineShaft E) Shaft(double rpm, double start)
    {
        var s = new Scheduler();
        var p = new Pins { S = s };
        var e = new EngineShaft(s, p, start);
        p.Edges.Clear();                                    // keep only the edges, not the start levels
        e.SetSpeed(rpm);
        return (s, p, e);
    }

    static double[] Degrees(Pins p, int port, int bit, bool level) =>     // at 1,000 rpm one degree is 4,000 cycles
        p.Edges.Where(x => x.Port == port && x.Bit == bit && x.Level == level).Select(x => x.Cycle / 4000.0).ToArray();

    [Fact] public void CrankFalls_Every6Degrees_ExceptTheTwoMissingTeethBeforeEachGap()
    {
        var (s, p, _) = Shaft(1000, 0.0);
        s.Advance(4000UL * 720);
        Assert.Equal(Enumerable.Range(1, 58).Concat(Enumerable.Range(61, 58)).Select(n => 6.0 * n), Degrees(p, 2, 15, false));
    }

    [Fact] public void P3_0_AndP3_2_CarryTheSameCrankWaveform()
    {
        var (s, p, _) = Shaft(1000, 0.0);
        s.Advance(4000UL * 720);
        Assert.Equal(Degrees(p, 2, 15, false), Degrees(p, 3, 0, false));
        Assert.Equal(Degrees(p, 2, 15, true), Degrees(p, 3, 2, true));
    }

    [Fact] public void Cams_AtRest_ChangeAtTheirAngles()
    {
        var (s, p, _) = Shaft(1000, 0.0);
        s.Advance(4000UL * 720);
        Assert.Equal((250.125, 610.125, 106.5, 466.5),
            (Degrees(p, 2, 14, true).Single(), Degrees(p, 2, 14, false).Single(), Degrees(p, 2, 13, true).Single(), Degrees(p, 2, 13, false).Single()));
    }

    [Fact] public void CamOffsets_MoveTheEdges()
    {
        var (s, p, e) = Shaft(0, 0.0);
        e.SetCamOffsets(20, 10);
        e.SetSpeed(1000);
        s.Advance(4000UL * 720);
        Assert.Equal((230.125, 116.5), (Degrees(p, 2, 14, true).Single(), Degrees(p, 2, 13, true).Single()));
    }

    [Fact] public void StartLevels_AreDrivenAtOnce_AndAStoppedShaftMakesNoEdge()
    {
        var s = new Scheduler();
        var p = new Pins { S = s };
        _ = new EngineShaft(s, p, 100.0);                   // 100°: after tooth 16's rise, before either cam opens
        s.Advance(10_000_000);
        Assert.Equal(new[] { (2, 15, true), (3, 0, true), (3, 2, true), (2, 14, false), (2, 13, false) },
            p.Edges.Select(x => (x.Port, x.Bit, x.Level)).ToArray());
    }

    [Fact] public void SpeedSetAtTheCycleOfAnEdge_NeverSkipsOrRepeatsIt()
    {
        var s = new Scheduler();
        var p = new Pins { S = s };
        EngineShaft? e = null;
        for (ulong t = 12_000; t <= 4000UL * 720; t += 12_000)          // every 3° at 1,000 rpm: on every crank edge, before it
            s.Schedule(t, "speed", () => e!.SetSpeed(1000));
        e = new EngineShaft(s, p, 0.0);
        p.Edges.Clear();
        e.SetSpeed(1000);
        s.Advance(4000UL * 720);
        var crank = p.Edges.Where(x => (x.Port, x.Bit) == (2, 15)).Select(x => x.Level).ToArray();
        Assert.Equal(232, crank.Length);
        Assert.All(crank.Zip(crank.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact] public void SpeedChange_KeepsTheAngleContinuous()
    {
        var (s, _, e) = Shaft(500, 0.0);
        s.Advance(2_400_000);                               // 0.1 s at 3,000 °/s
        Assert.Equal(300.0, e.Angle, 9);
        e.SetSpeed(1000);
        s.Advance(2_400_000 + 240_000);                     // 0.01 s at 6,000 °/s
        Assert.Equal(360.0, e.Angle, 9);
    }
}
