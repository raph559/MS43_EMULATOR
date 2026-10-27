using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using Xunit.Abstractions;

namespace Ms43Sim.Tests.LiveTuning;

public class OverlayV2TimingTests(ITestOutputHelper output)
{
    static string Send(byte[] p) => OverlayHookRuns.Send(p);
    static string Head => "drive v2-max-set\n" + OverlayRuns.Warm + "3 on\nkline every 1 6D 00\n1.5 crank rpm=200\n3 idle\n";
    static string ReadySet()
    {
        var text = Head;
        var offset = 0;
        foreach (var table in new[] { 1, 2, 3, 4 })
        {
            text += Send(LiveOverlay.Load(table, offset)) + Send(LiveOverlay.On(table, LiveOverlay.Crc32(OverlayEditRuns.Flash(table)))) + "0.8 idle\n";
            offset += LiveOverlay.Tables[table].Bytes;
        }
        Assert.Equal(612, offset);
        return text + Send(LiveOverlay.AllOff()) + "1 idle\n";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LargestReadySet_AllOnIsTimed_ThenSilenceFallsBack(bool highRpm)
    {
        var phase = highRpm ? "drive gear=3 rpm=6500 pedal=85" : "idle";
        var approach = highRpm ? "8 drive gear=3 speed=50..150 pedal=95\n" : "";
        var text = ReadySet() + approach + $"2 {phase}\n" + Send(LiveOverlay.AllOn()) + $"1 {phase}\nkline quiet\n4 {phase}\n";
        var intervals = new List<ulong>();
        var samples = new List<(ulong At, ushort Ticks, ushort Seen, OverlayState[] States)>();
        void Attach(DriveContext context) => context.PowerCycleStarted += (m, _) =>
        {
            var bytes = LiveOverlayBin.Bytes;
            var handler = (uint)(0x0C0000 + (bytes[0x1FC8C] | bytes[0x1FC8D] << 8));
            ulong? entered = null;
            m.OnInstruction(handler, machine =>
            {
                if (machine.Bus.Peek16(0xF6D0) % 256 == 5)
                    entered ??= machine.Cycles;
            });
            foreach (var back in new uint[] { 0x09FAD6, 0x09FB04 })
                m.OnInstruction(back, machine =>
                {
                    if (entered is not { } start) return;
                    intervals.Add(machine.Cycles - start); entered = null;
                });
            void Sample(ulong at) => m.Scheduler.Schedule(at, "v2-timeout-probe", () =>
            {
                var w = LiveOverlay.ReadWindow(m.Bus.ExternalRam);
                samples.Add((m.Cycles, m.Bus.Peek16(0xE2A6), w.Seen, w.Tables.Select(t => t.State).ToArray()));
                Sample(at + 240_000);
            });
            Sample(0);
        };
        var r = DriveRun.RunWithChecks(LiveOverlayBin.Image, DriveParser.Parse(text), new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        Assert.Single(intervals);
        var milliseconds = intervals[0] / 24000.0;
        output.WriteLine($"largest set612bytes; highRpm={highRpm}; AllOn handler including interrupts {intervals[0]} cycles = {milliseconds:F6} ms");
        Assert.InRange(milliseconds, 0.001, 10.0);
        var allOn = r.KLine.Single(e => e.Request.SequenceEqual(Ds2.Frame(LiveOverlay.AllOn())));
        Assert.Null(LiveOverlay.Refusal(allOn.Reply!));
        var active = samples.First(s => s.At > allOn.SentAt && s.States.Skip(1).All(t => t == OverlayState.On));
        var expired = samples.First(s => s.At > active.At && s.States.Skip(1).All(t => t == OverlayState.Ready));
        output.WriteLine($"off after {(expired.At - active.At)/24e6:F6}s from active sample, age{(ushort)(expired.Ticks-expired.Seen)} processedticks");
        Assert.True((ushort)(expired.Ticks-expired.Seen) >= LiveOverlay.SilenceTicks);
        Assert.Equal((0, 0), (r.WatchdogResets, r.SoftwareResets));
        if (highRpm && r.Verdict != RunVerdict.Pass)
        {
            var control = DriveRun.RunWithChecks(LiveOverlayBin.Image, DriveParser.Parse(text.Replace(Send(LiveOverlay.AllOn()), Send(LiveOverlay.Status()))));
            output.WriteLine("paired same-speed control without AllOn: " + control.Verdict + "; " + string.Join("; ", control.Checks.SelectMany(c => c.Findings.Select(f => c.Name + ": " + f.Message))));
        }
        Assert.True(r.Verdict == RunVerdict.Pass, string.Join("; ", r.Checks.SelectMany(c => c.Findings.Select(f => c.Name + ": " + f.Message))));
    }

    [Fact] public void AllOn_ActivatesNone_WhenOneReadyCopyFailsCrc()
    {
        var changed = false;
        OverlayWindow? after = null;
        void Attach(DriveContext context) => context.PowerCycleStarted += (m, _) =>
        {
            var b = LiveOverlayBin.Bytes;
            var handler = (uint)(0x0C0000 + (b[0x1FC8C] | b[0x1FC8D] << 8));
            m.OnInstruction(handler, machine =>
            {
                if (changed || machine.Bus.Peek16(0xF6D0) % 256 != 5) return;
                changed = true;
                machine.Bus.ExternalRam[0x1D30 + 36] ^= 1;
            });
            foreach (var back in new uint[] { 0x09FAD6, 0x09FB04 })
                m.OnInstruction(back, machine =>
                {
                    if (changed && after is null) after = LiveOverlay.ReadWindow(machine.Bus.ExternalRam);
                });
        };
        var r = DriveRun.Run(LiveOverlayBin.Image, DriveParser.Parse(ReadySet() + Send(LiveOverlay.AllOn()) + "1 idle\n"),
            new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        var on = r.KLine.Single(e => e.Request.SequenceEqual(Ds2.Frame(LiveOverlay.AllOn())));
        Assert.Equal(OverlayReason.Integrity, LiveOverlay.Refusal(on.Reply!));
        Assert.True(changed);
        Assert.NotNull(after);
        Assert.All(after.Tables.Skip(1), e => Assert.Equal(OverlayState.Ready, e.State));
    }
}
