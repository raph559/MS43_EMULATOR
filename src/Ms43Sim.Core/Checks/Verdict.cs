namespace Ms43Sim.Core;

/// <summary>A drive's software/output-check verdict; the value is the exit code. It is not physical calibration safety clearance.</summary>
public enum RunVerdict { Pass = 0, Fail = 1, Incomplete = 4 }

/// <summary>
/// What a run covered (design §7): the stubs with their side and reason, the checks that could not run
/// and those whose condition never occurred, what the run touched that is not modelled, the faults no
/// explanation covers, and the benign notes (R4). NotRun: what the drive asked for that never ran — its
/// time after a relay drop with the key on, or an engine it never cranks (final review I3, I6). NotJudged:
/// the gaps the checks declare, which the verdict does not count (Task 11; final review I5).
/// </summary>
public sealed record Coverage(IReadOnlyList<StubInfo> Stubs, IReadOnlyList<string> NotEvaluated,
    IReadOnlyList<string> NotExercised, IReadOnlyList<string> UnmodelledAccesses,
    IReadOnlyList<string> UnexplainedFaults, IReadOnlyList<string> BenignNotes)
{
    public IReadOnlyList<string> NotRun { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NotJudged { get; init; } = Array.Empty<string>();
}

public static class Verdict
{
    /// <summary>
    /// C2: fail if any check failed; else incomplete if any check is not evaluated, any stub sits between
    /// the CPU and a checked output (design §7), anything unmodelled was touched, a fault is unexplained,
    /// or part of the drive did not run; else pass.
    /// </summary>
    public static RunVerdict Of(IReadOnlyList<ICheck> checks, Coverage coverage)
    {
        if (checks.Any(c => c.Status == CheckStatus.Fail))
            return RunVerdict.Fail;
        if (checks.Any(c => c.Status == CheckStatus.NotEvaluated) || coverage.Stubs.Any(s => s.BetweenCpuAndCheckedOutput)
            || coverage.UnmodelledAccesses.Count > 0 || coverage.UnexplainedFaults.Count > 0 || coverage.NotRun.Count > 0)
            return RunVerdict.Incomplete;
        return RunVerdict.Pass;
    }

    /// <summary>The verdict's reasons, one line each, for the report.</summary>
    public static IEnumerable<string> Reasons(IReadOnlyList<ICheck> checks, Coverage coverage)
    {
        foreach (var check in checks.Where(c => c.Status == CheckStatus.Fail))
            yield return $"{check.Name} failed: {check.Findings[0].Message}{(check.Findings.Count > 1 ? $" (and {check.Findings.Count - 1} more)" : "")}";
        foreach (var line in coverage.NotEvaluated)
            yield return $"not evaluated: {line}";
        foreach (var stub in coverage.Stubs.Where(s => s.BetweenCpuAndCheckedOutput))
            yield return $"stub between the CPU and a checked output: {stub.Device}";
        foreach (var line in coverage.UnmodelledAccesses)
            yield return $"not modelled: {line}";
        foreach (var line in coverage.UnexplainedFaults)
            yield return $"unexplained fault: {line}";
        foreach (var line in coverage.NotRun)
            yield return $"not run: {line}";
    }
}
