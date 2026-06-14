namespace Ms43Sim.Core;

/// <summary>
/// Every check of a drive (plan 4 R16), built once and attached to the drive's context, so one set
/// spans all its power cycles. Pass <c>context => CheckSet.Attach(context)</c> to a drive run as an observer.
/// </summary>
public sealed class CheckSet
{
    readonly List<CheckBase> checks;

    CheckSet(IEnumerable<CheckBase> checks) => this.checks = checks.ToList();

    public IReadOnlyList<ICheck> Checks => checks;

    /// <summary>The standard checks, then <paramref name="extra"/> ones, each attached to <paramref name="context"/>.</summary>
    public static CheckSet Attach(DriveContext context, IEnumerable<CheckBase>? extra = null)
    {
        var set = new CheckSet(CrashChecks.All().Append(new SchedulerCheck()).Concat(OutputChecks.All()).Concat(extra ?? Array.Empty<CheckBase>()));
        foreach (var check in set.checks)
            check.Attach(context);
        return set;
    }
}
