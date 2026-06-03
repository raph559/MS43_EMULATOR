using Ms43Sim.Core;

namespace Ms43Sim.Tests.EngineModel;

public class EngineOutcomeTests
{
    static EnginePhaseChange[] Phases(params (ulong Cycle, EnginePhase Phase)[] p) => p.Select(x => new EnginePhaseChange(x.Cycle, x.Phase)).ToArray();

    static readonly EnginePhaseChange[] Ran = Phases((0, EnginePhase.Stopped), (72_000_000, EnginePhase.Starter), (96_000_000, EnginePhase.Running),
        (1_806_000_000, EnginePhase.Stopping), (1_842_000_000, EnginePhase.Stopped));

    [Fact] public void NoCombustionAtTheStartersRelease_NeverStarted() =>
        Assert.Equal(EngineOutcome.NeverStarted, EngineSummary.OutcomeOf(Phases((0, EnginePhase.Stopped), (72_000_000, EnginePhase.Starter),
            (96_000_000, EnginePhase.Stopping), (120_000_000, EnginePhase.Stopped)), keyOffCycle: null));

    [Fact] public void StillRunningAtTheEnd_IsRunning() =>
        Assert.Equal(EngineOutcome.Running, EngineSummary.OutcomeOf(Ran[..3], keyOffCycle: null));

    [Fact] public void StoppingFromTheKeyOff_StoppedAfterTheKeyOff() =>
        Assert.Equal(EngineOutcome.StoppedAfterKeyOff, EngineSummary.OutcomeOf(Ran, keyOffCycle: 1_800_000_000));

    [Fact] public void StoppingWithTheKeyStillOn_Stalled()
    {
        Assert.Equal(EngineOutcome.Stalled, EngineSummary.OutcomeOf(Ran, keyOffCycle: null));
        Assert.Equal(EngineOutcome.Stalled, EngineSummary.OutcomeOf(Ran, keyOffCycle: 2_000_000_000));   // the key went off after it had stopped
    }
}
