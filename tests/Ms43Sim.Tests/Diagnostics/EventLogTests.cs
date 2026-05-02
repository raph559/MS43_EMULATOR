using Ms43Sim.Core;

namespace Ms43Sim.Tests.Diagnostics;

public class EventLogTests
{
    static void Add(EventLog log, int times, Func<ulong, uint, SimEvent> make)
    {
        for (var i = 0; i < times; i++)
            log.Add(make);
    }

    [Fact] public void ATrapAfterTenThousandFlashWrites_IsKeptAndCounted()
    {
        var log = new EventLog();
        Add(log, 10_001, (c, pc) => new FlashWriteEvent(c, pc, 0x10000, 0, false));
        Add(log, 1, (c, pc) => new TrapEvent(c, pc, TrapKind.UndefinedOpcode, 0x1000, 0x0080));
        Assert.Single(log.All.OfType<TrapEvent>());
        Assert.Equal((10_001L, 1L, 9_001L), (log.CountOf<FlashWriteEvent>(), log.CountOf<TrapEvent>(), log.Dropped));   // flash writes keep 1,000 (plan 3)
    }

    [Fact] public void FlashWrites_HaveTheirOwnCapacity()
    {
        var log = new EventLog();
        for (var i = 0; i < 20_000; i++)
            log.Add((c, pc) => new FlashWriteEvent(c, pc, 0x4286C, 0, false));
        log.Add((c, pc) => new UnsupportedEvent(c, pc, "after the flood"));
        Assert.Equal(EventLog.FlashWriteCapacity, log.All.OfType<FlashWriteEvent>().Count());
        Assert.Single(log.All.OfType<UnsupportedEvent>());
        Assert.Equal((20_000L, 19_000L), (log.CountOf<FlashWriteEvent>(), log.Dropped));
    }

    [Fact] public void TrapsAndResets_HaveTheirOwnCap_AndEveryEventIsCounted()
    {
        var log = new EventLog();
        Add(log, EventLog.TrapAndResetCapacity + 1, (c, pc) => new ResetEvent(c, pc, ResetKind.Watchdog));
        Add(log, 1, (c, pc) => new ErasedFlashExecutionEvent(c, pc));
        Assert.Equal((1_001, 1_001L, 1L), (log.All.Count, log.CountOf<ResetEvent>(), log.Dropped));
        Assert.IsType<ErasedFlashExecutionEvent>(log.All[^1]);
    }
}
