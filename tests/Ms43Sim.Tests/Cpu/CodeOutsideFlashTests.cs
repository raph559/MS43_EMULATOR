using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

public class CodeOutsideFlashTests
{
    [Fact] public void ExecutingFromIram_RaisesOneEventPerEntry()
    {
        var t = TestMachine.Create("0DFF");                                   // flash 00:1000: JMPR to itself
        t.Poke16(0xFA00, 0xFF0D);                                             // IRAM 00:FA00: the same loop
        t.Step(4);
        Assert.Equal(0, t.M.Events.CountOf<CodeOutsideFlashEvent>());         // flash: nothing
        t.Cpu.Jump(0x00, 0xFA00);
        t.Step(10);
        Assert.Equal(1, t.M.Events.CountOf<CodeOutsideFlashEvent>());         // one entry, many instructions
        Assert.Equal(0xFA00u, t.M.Events.All.OfType<CodeOutsideFlashEvent>().Single().Pc);
        t.Cpu.Jump(0x00, 0x1000);
        t.Step(2);
        t.Cpu.Jump(0x00, 0xFA00);
        t.Step(2);
        Assert.Equal(2, t.M.Events.CountOf<CodeOutsideFlashEvent>());         // back out to flash, then in again
    }

    [Fact] public void TheEventLog_TellsItsListeners_EvenWhenItKeepsNoMore()
    {
        var log = new EventLog();
        var heard = 0;
        log.Added += _ => heard++;
        for (var i = 0; i < EventLog.OtherCapacity + 5; i++)
            log.Add((c, pc) => new UnsupportedEvent(c, pc, "x"));
        Assert.Equal((EventLog.OtherCapacity + 5, 5L), (heard, log.Dropped));
    }
}
