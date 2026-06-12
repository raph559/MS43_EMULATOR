using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>Drive runs with the full check set attached (plan 4 Task 6), for the check tests.</summary>
public static class CheckRuns
{
    /// <summary>
    /// Runs <paramref name="drive"/> on <paramref name="image"/> (the car file when null) with every
    /// check, and any <paramref name="extra"/> ones, observing it; the result carries the checks.
    /// </summary>
    public static DriveResult DriveRunWithChecks(Drive drive, FlashedImage? image = null, int captureCapacity = OutputCapture.Capacity,
        params CheckBase[] extra)
    {
        CheckSet? set = null;
        var r = DriveRun.Run(image ?? TestImages.Car, drive, new DriveRunOptions(
            Observers: new Action<DriveContext>[] { context => set = CheckSet.Attach(context, extra) },
            CaptureCapacity: captureCapacity));
        return r with { Checks = set!.Checks };
    }
}
