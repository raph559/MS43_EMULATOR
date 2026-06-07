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

    [Fact] public void InjectionDecision_IsReadAsItOpens_AndAgainAsItCloses()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFC2, 0x0001).WriteSfr(0xFFC0, 0x0001);
        void Xram(uint a, ushort v) { t.M.Bus.Xram[a - 0xE000] = (byte)v; t.M.Bus.Xram[a - 0xE000 + 1] = (byte)(v >> 8); }
        Xram(0xE46A, 514); Xram(0xE470, 0x1000); Xram(0xE47C, 104);         // cylinder 1: bank 1, firing index 0
        var ev = new CylinderEvents(t.M, () => 0.0);
        t.M.Scheduler.Advance(1_000); t.WriteSfr(0xFFC0, 0x0000);             // opens with 514 + 104 decided
        t.M.Scheduler.Advance(20_000); Xram(0xE46A, 500);                      // the injection task recomputes mid-pulse
        t.M.Scheduler.Advance(1_000 + 128 * 604); t.WriteSfr(0xFFC0, 0x0001); // and the pulse follows it
        var i = Assert.Single(ev.Injections);
        Assert.Equal((618.0, 604.0), (i.ExpectedTicks, i.ExpectedTicksAtClose));
    }

    [Fact] public void ACoilPulseUnderHalfAMillisecond_IsNotASpark()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFC2, 0x0040);        // P2.6 (coil 1) output
        var ev = new CylinderEvents(t.M, () => EngineShaft.FiringTdc(1));
        t.WriteSfr(0xFFC0, 0x0040);                                         // dwell start
        t.M.Scheduler.Advance(6_000);                                       // 0.25 ms: an ignition cut's pulse (plan 4 R1)
        t.WriteSfr(0xFFC0, 0x0000);                                         // fall
        Assert.Empty(ev.Sparks);
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
