using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Peripherals;

public class GptTests
{
    [Fact] public void T4_CountsDownAt3MHz_AndItsUnderflowSetsT4ir()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFE44, 0x0BB8).WriteSfr(0xFF44, 0x00C0);
        t.M.Scheduler.Advance(8 * 0x0BB8); Assert.Equal(0, t.Sfr(0xFF64) & 0x80);
        t.M.Scheduler.Advance(8 * 0x0BB9); Assert.Equal((0x80, 0xFFFF), (t.Sfr(0xFF64) & 0x80, t.M.Gpt.Timer(4)));
    }

    [Fact] public void T4_HandlerAdding0BB8h_GivesAOneMillisecondTick()
    {
        // T4 vector 90h: ADD T4,#0BB8h ; RETI. T4IC = 5Bh (IE, level 6). The firmware's own pattern (0E:B590).
        var t = TestMachine.Create("0DFF", (0x90, "0622B80B FB88"))
            .WriteSfr(0xFE44, 0x0BB8).WriteSfr(0xFF64, 0x005B).WriteSfr(0xFF44, 0x00C0);
        t.M.Watchdog.Disable();                                             // it would reset the machine at 131,072 cycles
        t.Psw = 0x0800;
        var ticks = 0; t.M.OnInstruction(0x000090, _ => ticks++);
        t.M.Run(24_000 * 10 + 1_000);
        Assert.Equal(10, ticks);
    }

    [Fact] public void T3_GatedHigh_CountsOnlyWhileT3inIsHigh()
    {
        var t = TestMachine.Create("0DFF");
        t.M.Board.SetInputs(t.M.Board.Inputs.WithInput(3, 6, false));
        t.WriteSfr(0xFF42, 0x005C);                                         // fCPU/128, gated active high, run, up
        t.M.Scheduler.Advance(128 * 100); Assert.Equal(0, t.M.Gpt.Timer(3));
        t.M.Board.SetInputs(t.M.Board.Inputs.WithInput(3, 6, true));
        t.M.Scheduler.Advance(t.M.Cycles + 128 * 100); Assert.Equal(100, t.M.Gpt.Timer(3));
    }

    [Fact] public void T6_Underflow_ReloadsFromCaprel_TogglesT6otl_AndSetsT6ir()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFE4A, 0x0010).WriteSfr(0xFE48, 0x0002).WriteSfr(0xFF48, 0x80C5);
        t.M.Scheduler.Advance(128 * 3);
        Assert.Equal((0x0010, 0x80, 0x0400), (t.M.Gpt.Timer(6), t.Sfr(0xFF68) & 0x80, t.Sfr(0xFF48) & 0x0400));
    }

    [Fact] public void T5_CapturesIntoCaprel_OnAFallingCapinEdge_AndClearsT5()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFF46, 0xE042);       // T5SC, T5CLR, CI = falling, run, fCPU/16
        t.M.Scheduler.Advance(16 * 50);
        t.M.Board.SetInputs(t.M.Board.Inputs.WithInput(3, 2, false));
        Assert.Equal((50, 0, 0x80), (t.M.Gpt.Caprel, t.M.Gpt.Timer(5), t.Sfr(0xFF6A) & 0x80));
    }

    [Fact] public void IncrementalInterfaceMode_IsReported()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFF42, 0x0070);
        Assert.Contains(t.M.Events.All.OfType<UnsupportedEvent>(), e => e.What.Contains("T3"));
    }

    [Fact] public void T3_CounterMode_CountsTheSelectedEdges()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFF42, 0x0049);       // counter, rising edges on T3IN, run
        foreach (var level in new[] { false, true, false, true })
            t.M.Board.SetInputs(t.M.Board.Inputs.WithInput(3, 6, level));
        Assert.Equal(2, t.M.Gpt.Timer(3));
    }

    [Fact] public void T2_ExternalUpDown_FollowsT2eud()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFF40, 0x0140);       // timer, fCPU/8, run, T2UDE
        t.M.Scheduler.Advance(80); Assert.Equal(10, t.M.Gpt.Timer(2));    // T2EUD = P5.15 low: up
        t.M.Board.SetInputs(t.M.Board.Inputs.WithAnalog(15, 5.0));         // high: down
        t.M.Scheduler.Advance(160); Assert.Equal(0, t.M.Gpt.Timer(2));
    }

    [Fact] public void T5_CountsT6otlTransitions_AndT6RaisesItsHook()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFF46, 0x004F)        // T5: counter on any T6OTL transition, run
            .WriteSfr(0xFE48, 0xFFFE).WriteSfr(0xFF48, 0x0040);            // T6: timer, fCPU/4, run, up
        t.M.Watchdog.Disable();                                             // it would reset the machine at 131,072 cycles
        var hooks = 0; t.M.Gpt.T6OverOrUnderflow += () => hooks++;
        t.M.Scheduler.Advance(8);
        Assert.Equal((1, 1, 0x0000), (t.M.Gpt.Timer(5), hooks, t.M.Gpt.Timer(6)));
        t.M.Scheduler.Advance(8 + 4 * 0x10000);
        Assert.Equal((2, 2), (t.M.Gpt.Timer(5), hooks));
    }

    [Theory]
    [InlineData(0xFF40, 0x0060, "T2")]           // T2 reload mode
    [InlineData(0xFF44, 0x0068, "T4")]           // T4 capture mode
    [InlineData(0xFF44, 0x004D, "T4")]           // T4 counting T3OTL (concatenation)
    [InlineData(0xFF46, 0x0400, "CT3")]          // CAPREL captures on T3IN/T3EUD (T5CON.10)
    public void UnmodelledModes_AreReported(int address, int value, string name)
    {
        var t = TestMachine.Create("0DFF").WriteSfr((uint)address, (ushort)value);
        Assert.Contains(t.M.Events.All.OfType<UnsupportedEvent>(), e => e.What.Contains(name));
    }
}
