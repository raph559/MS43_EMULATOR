using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Capture;

/// <summary>P2.0 and P2.6 outputs; the test plays the ECU and sets the angle source.</summary>
public class CylinderEventsTests
{
    [Fact] public void InjectorPulse_IsLowFromOpenToClose()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFC2, 0x0001).WriteSfr(0xFFC0, 0x0001);
        var angle = 0.0; var ev = new CylinderEvents(t.M, () => angle);
        angle = 600.0; t.M.Scheduler.Advance(1_000); t.WriteSfr(0xFFC0, 0x0000);   // opens
        t.M.Scheduler.Advance(1_000 + 128 * 300); t.WriteSfr(0xFFC0, 0x0001);      // 300 ticks later
        var i = Assert.Single(ev.Injections);
        Assert.Equal((1, 600.0, 300.0), (i.Cylinder, i.OpenAngle, i.Ticks));
    }

    [Fact] public void Sparks_AreToldApartByTheirDwell_AndMeasuredFromTheFiringTdc()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFC2, 0x0040);
        var angle = 0.0; var ev = new CylinderEvents(t.M, () => angle);
        void Pulse(ulong from, ulong to, double at) { t.M.Scheduler.Advance(from); t.WriteSfr(0xFFC0, 0x0040); t.M.Scheduler.Advance(to); angle = at; t.WriteSfr(0xFFC0, 0x0000); }
        Pulse(10_000, 10_000 + 56_640, 680.0);                               // 2.36 ms dwell, fires at 680°: 10° BTDC
        Pulse(71_400, 71_400 + 17_520, 683.0);                               // a 0.73 ms re-dwell: a multi-spark
        Assert.Equal(new[] { (1, true, 10.0), (1, false, 7.0) }, ev.Sparks.Select(s => (s.Cylinder, s.Main, s.Advance)));
    }
}
