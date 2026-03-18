using Ms43Sim.Core;

namespace Ms43Sim.Tests.Runtime;   // not ".Machine": that would hide the Machine type from other test files

public class SchedulerTests
{
    [Fact]
    public void RunsInTimeThenInsertionOrder_AndCancels()
    {
        var s = new Scheduler(); var seen = new List<string>();
        s.Schedule(10, "b", () => seen.Add("b"));
        s.Schedule(5, "a", () => seen.Add("a"));
        s.Schedule(10, "c", () => seen.Add("c"));
        var h = s.Schedule(7, "x", () => seen.Add("x"));
        s.Cancel(h);
        s.Advance(10);
        Assert.Equal(new[] { "a", "b", "c" }, seen);
        Assert.Equal(10UL, s.Now);
        Assert.Equal(ulong.MaxValue, s.NextDue);
    }

    [Fact]
    public void EventScheduledDuringAdvance_AtOrBeforeTarget_AlsoRuns()
    {
        var s = new Scheduler(); var n = 0;
        s.Schedule(3, "first", () => { n++; s.Schedule(4, "second", () => n++); });
        s.Advance(5);
        Assert.Equal(2, n);
    }
}
