using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using static Ms43Sim.Tests.Support.CheckRuns;

namespace Ms43Sim.Tests.Drives;

/// <summary>
/// The app's pause after each sample (owner, 2026-10-28: it was there so the cable and the CPU would not work too hard). With
/// <c>kline pause 0</c> the tester polls again as soon as a reply is whole: the car file must answer as before and its timed
/// tasks keep their schedule, at idle and through a pull.
/// </summary>
public class KLinePauseTests
{
    static readonly byte[] Poll = { 0x12, 0x05, 0x0B, 0xB0, 0xAC };

    const string Head = "drive kline-pause\nstart blank\nset coolant=90 oil=90 intake=33 ambient=20 clock=60\n"
        + "3 on\n1.5 crank rpm=200\n5 idle\n";

    static string Why(ICheck c) => string.Join("; ", c.Findings.Select(f => f.Message));

    static DriveResult Run(string pause) => DriveRunWithChecks(DriveParser.Parse(Head + "kline connect\n" + pause + "kline poll\n"
        + "5 idle\n8 drive gear=3 speed=50..150 pedal=95\n"));

    static (int Polls, int Missed, double PerSecond) Rate(DriveResult r)
    {
        var polls = r.KLine.Where(e => e.Request.SequenceEqual(Poll)).ToList();
        var replied = polls.Where(p => p.Outcome == KLineOutcome.Replied).ToList();
        var seconds = (replied[^1].RepliedAt!.Value - replied[0].RepliedAt!.Value) / 24e6;
        return (polls.Count, polls.Count - replied.Count, (replied.Count - 1) / seconds);
    }

    [Fact] public void DriveParser_ReadsThePause()
    {
        var drive = DriveParser.Parse(Head + "kline pause 0\n1 idle\n");
        var pause = drive.Segments[^1].KLine.Single();
        Assert.Equal((KLineVerb.Pause, 0.0), (pause.Verb, pause.Period));
        Assert.Equal(0.005, DriveParser.Parse(Head + "kline pause 5\n1 idle\n").Segments[^1].KLine.Single().Period);
        Assert.Throws<DriveFormatException>(() => DriveParser.Parse(Head + "kline pause -1\n1 idle\n"));
        Assert.Throws<DriveFormatException>(() => DriveParser.Parse(Head + "kline pause\n1 idle\n"));
    }

    [Fact] public void WithoutThePause_TheCarAnswers_AndKeepsItsSchedule()
    {
        var paused = Run("");
        var flat = Run("kline pause 0\n");
        var (pausedPolls, pausedMissed, pausedRate) = Rate(paused);
        var (polls, missed, rate) = Rate(flat);

        Assert.True(flat.Check("scheduler").Status == CheckStatus.Pass, Why(flat.Check("scheduler")));
        Assert.True(missed * 50 <= polls, $"{missed} of {polls} polls missed without the pause ({pausedMissed} of {pausedPolls} with it)");
        Assert.True(rate > pausedRate, $"{rate:F1}/s without the pause, {pausedRate:F1}/s with it");
        Console.WriteLine($"kline pause: 20 ms {pausedRate:F1}/s ({pausedMissed}/{pausedPolls} missed), 0 ms {rate:F1}/s ({missed}/{polls} missed)");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "ms43-kline-pause.txt"),
            $"20 ms: {pausedRate:F2}/s, {pausedMissed}/{pausedPolls} missed\n0 ms: {rate:F2}/s, {missed}/{polls} missed\n");
    }
}
