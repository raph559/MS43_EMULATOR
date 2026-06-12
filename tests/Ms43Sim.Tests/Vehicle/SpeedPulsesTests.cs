using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Vehicle;

/// <summary>A machine whose CPU only spins; the pulses reach P7.6 through the board, as in a run.</summary>
public class SpeedPulsesTests
{
    static (TestMachine T, Func<int> Falls) Rig()
    {
        var t = TestMachine.Create("0DFF");
        t.M.Watchdog.Disable();
        var falls = 0;
        t.M.Ports.PinChanged += (port, bit, level) => { if (port == 7 && bit == 6 && !level) falls++; };
        return (t, () => falls);
    }

    [Fact] public void SpeedPulses_FallAtTheExpectedRate()                      // 6.971 Hz/(km/h): 50 km/h = 348.57 Hz
    {
        var (t, falls) = Rig();
        new SpeedPulses(t.M.Scheduler, t.M.Board).SetSpeed(50);
        t.M.Scheduler.Advance(24_000_000);                                    // 1 s
        Assert.InRange(falls(), 347, 350);
    }

    [Fact] public void ASpeedSetAgainEvery10ms_KeepsItsPhase_AndZeroStopsThePulses()
    {
        var (t, falls) = Rig();
        var pulses = new SpeedPulses(t.M.Scheduler, t.M.Board);
        for (var step = 0; step < 100; step++)                                // 1 s of a slow ramp, set every 10 ms (a drive's step)
        {
            pulses.SetSpeed(5 + step * 0.001);                                // 5 km/h = 34.9 Hz: a period of 28.7 ms
            t.M.Scheduler.Advance((ulong)(step + 1) * 240_000);
        }
        Assert.InRange(falls(), 34, 36);
        pulses.SetSpeed(0);
        var stopped = falls();
        t.M.Scheduler.Advance(72_000_000);
        Assert.Equal(stopped, falls());
        Assert.True(t.M.Board.InputLevel(7, 6));                              // the pin rests high
    }
}
