using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

/// <summary>The car file, warm restart, 20 s: the firmware runs the engine on its own.</summary>
public sealed class CarWarmStart
{
    public CarWarmStart()
    {
        M = Machine.Create(TestImages.Car, new MachineOptions { Inputs = EngineScenario.WarmIdle.Inputs });
        M.PowerOn();
        E = Engine.Attach(M, EngineScenario.WarmIdle);
        M.Run(480_000_000);
    }

    public Machine M { get; }
    public Engine E { get; }
}

public class CarEngineTests : IClassFixture<CarWarmStart>
{
    readonly Machine m; readonly Engine e;
    public CarEngineTests(CarWarmStart f) => (m, e) = (f.M, f.E);

    ushort Xram(uint a) => (ushort)(m.Bus.Xram[a - 0xE000] | m.Bus.Xram[a - 0xE000 + 1] << 8);
    ushort Ext16(uint a) => (ushort)(m.Bus.ExternalRam[a - 0x8000] | m.Bus.ExternalRam[a - 0x8000 + 1] << 8);

    [Fact] public void Engine_StartsAt4s_AndRunsOnItsOwn()
    {
        Assert.Equal(new[] { EnginePhase.Stopped, EnginePhase.Starter, EnginePhase.Running }, e.Phases.Select(p => p.Phase).ToArray());
        Assert.Equal((72_000_000UL, 96_000_000UL), (e.Phases[1].Cycle, e.Phases[2].Cycle));
        Assert.Equal(2, m.Bus.Peek16(0xF900) & 0xFF);
    }

    [Fact] public void Speed_SettlesOnTheFirmwaresIdleTarget()
    {
        Assert.Equal(900, Ext16(0x8878));                               // ip_n_sp_is__tco at 90 °C
        Assert.InRange(e.Rpm, 875, 925);
        Assert.InRange(Math.Abs(Xram(0xE0E4) - e.Rpm), 0, e.Rpm * 0.01 + 2);
    }
}
