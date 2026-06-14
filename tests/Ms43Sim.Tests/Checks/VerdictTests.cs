using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Checks;

public class VerdictTests
{
    sealed record FakeCheck(string Name, CheckStatus Status) : ICheck
    {
        public IReadOnlyList<Finding> Findings { get; } =
            Status == CheckStatus.Fail ? new[] { new Finding(Name, "found", 0, 0) } : Array.Empty<Finding>();
    }

    static IReadOnlyList<ICheck> Checks(params CheckStatus[] statuses) =>
        statuses.Select((s, i) => (ICheck)new FakeCheck($"check{i}", s)).ToArray();

    static readonly StubInfo Clear = new("chip", "passes the pins through", false, "upstream of the checked pins");

    static Coverage CleanCoverage => new(new[] { Clear }, Array.Empty<string>(), Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    static Coverage CoverageWith(string? unmodelled = null, string? unexplained = null, StubInfo? stub = null) =>
        CleanCoverage with
        {
            UnmodelledAccesses = unmodelled is null ? Array.Empty<string>() : new[] { unmodelled },
            UnexplainedFaults = unexplained is null ? Array.Empty<string>() : new[] { unexplained },
            Stubs = stub is null ? new[] { Clear } : new[] { Clear, stub },
        };

    [Fact] public void FailBeatsIncompleteBeatsPass()
    {
        Assert.Equal(RunVerdict.Pass, Verdict.Of(Checks(CheckStatus.Pass), CleanCoverage));
        Assert.Equal(RunVerdict.Incomplete, Verdict.Of(Checks(CheckStatus.NotEvaluated), CleanCoverage));
        Assert.Equal(RunVerdict.Incomplete, Verdict.Of(Checks(CheckStatus.Pass), CoverageWith(unmodelled: "0x080000")));
        Assert.Equal(RunVerdict.Fail, Verdict.Of(Checks(CheckStatus.Fail), CoverageWith(unmodelled: "0x080000")));
        Assert.Equal(RunVerdict.Fail, Verdict.Of(Checks(CheckStatus.Pass, CheckStatus.NotEvaluated, CheckStatus.Fail), CleanCoverage));
        Assert.Equal((0, 1, 4), ((int)RunVerdict.Pass, (int)RunVerdict.Fail, (int)RunVerdict.Incomplete));   // the exit codes
    }

    [Fact] public void AStoredFault_OrAStubBetweenTheCpuAndACheckedOutput_MakesARunIncomplete()
    {
        Assert.Equal(RunVerdict.Incomplete, Verdict.Of(Checks(CheckStatus.Pass), CoverageWith(unexplained: "power cycle 1: maf stored")));
        Assert.Equal(RunVerdict.Incomplete, Verdict.Of(Checks(CheckStatus.Pass),
            CoverageWith(stub: new StubInfo("valve driver", "always healthy", true, "it sits between the CPU and the valve the checks watch"))));
    }

    [Fact] public void TheBenignAccesses_DoNotMakeARunIncomplete()                 // R4: F024h, the 0x4286C byte writes
    {
        var r = DriveRun.RunWithChecks(TestImages.Car, DriveBuilder.Named("warm-start"));
        Assert.True(r.Verdict == RunVerdict.Pass, string.Join("; ", r.Coverage!.UnmodelledAccesses.Concat(r.Coverage.UnexplainedFaults)
            .Concat(r.Checks.Where(c => c.Status != CheckStatus.Pass).Select(c => c.Name))));
        Assert.Contains(r.Coverage!.BenignNotes, n => n.Contains("F024"));
        Assert.Contains(r.Coverage.BenignNotes, n => n.Contains("4286C"));
        Assert.All(r.Coverage.Stubs, s => Assert.False(s.BetweenCpuAndCheckedOutput));
        Assert.All(r.Coverage.Stubs, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
    }
}
