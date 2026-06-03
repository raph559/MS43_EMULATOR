using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Capture;

public class OutputCaptureTests
{
    [Fact] public void OutputPinEdges_AreRecorded_WithTheCrankAngle()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFC2, 0x0001);          // P2.0 output
        var c = new OutputCapture(t.M, () => 123.0);
        t.WriteSfr(0xFFC0, 0x0001); t.WriteSfr(0xFFC0, 0x0000);
        Assert.Equal(new[] { ("P2.0", 1, 123.0), ("P2.0", 0, 123.0) },
            c.Events.Where(e => e.Kind == OutputKind.Pin).Select(e => (e.Source, e.Value, e.Angle!.Value)));
    }

    [Fact] public void InputPins_AreNotOutputs()
    {
        var t = TestMachine.Create("0DFF");
        var c = new OutputCapture(t.M);
        t.M.Board.Drive(2, 15, false);
        Assert.DoesNotContain(c.Events, e => e.Source == "P2.15");
    }

    [Fact] public void PwmSettings_AreRecordedAsTheirHighShare()
    {
        var t = TestMachine.Create("0DFF");
        var c = new OutputCapture(t.M);
        t.WriteSfr(0xF03C, 0xEA5F).WriteSfr(0xFE34, 0x3A98).WriteSfr(0xFF32, 0x0004).WriteSfr(0xFF30, 0x0004);
        Assert.Equal(750, c.Events.Last(e => e.Source == "PWM2").Value);       // latch 0: 75 % high
    }

    [Fact] public void PwmWidthWrittenWhileRunning_IsRecordedAtTheRestartThatLoadsIt()
    {
        var t = TestMachine.Create("0DFF")                                   // PWM2 at fCPU: a period of 1,875 cycles
            .WriteSfr(0xF03C, 0x0752).WriteSfr(0xFE34, 0x02C4).WriteSfr(0xFF32, 0x0004).WriteSfr(0xFF30, 0x0004);
        var c = new OutputCapture(t.M);
        t.M.Scheduler.Schedule(937, "width", () => t.WriteSfr(0xFE34, 0x03E8));
        t.M.Scheduler.Advance(5_000);
        var last = c.Events.Last(e => e.Source == "PWM2");
        Assert.Equal((1875UL, 467), (last.Cycle, last.Value));              // 875 of 1,875 high from the restart
    }

    [Fact] public void EdgesBeyondTheCapacity_AreCountedNotKept()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFFC2, 0x0001);
        var c = new OutputCapture(t.M, capacity: 4);
        for (var i = 0; i < 10; i++) t.WriteSfr(0xFFC0, (ushort)(i & 1 ^ 1));
        Assert.Equal((4, 6L, 10L), (c.Events.Count, c.Dropped, c.Counts["P2.0"]));
    }
}
