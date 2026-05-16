using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Startup;

/// <summary>The car file, warm inputs, a scripted speed: 200 rpm from 3.0 s, a ramp to 900 rpm over 4.0–4.5 s, 8 s in all.</summary>
public sealed class CarScriptedCrank
{
    public CarScriptedCrank()
    {
        M = Machine.Create(TestImages.Car, new MachineOptions { Inputs = BoardInputs.WarmIdle });
        M.PowerOn();
        var shaft = new EngineShaft(M.Scheduler, M.Board, startAngle: 100.0);
        M.Scheduler.Schedule(72_000_000, "starter", () => shaft.SetSpeed(200));
        for (var k = 0; k <= 1000; k++)                                  // 4.0-4.5 s, every 0.5 ms
        {
            var rpm = 200 + 700 * k / 1000.0;
            M.Scheduler.Schedule(96_000_000 + (ulong)k * 12_000, "ramp", () => shaft.SetSpeed(rpm));
        }
        M.Ports.PinChanged += (port, bit, level) =>
        {
            if (port == 2 && bit < 12 && M.Ports.IsOutput(2, bit) && M.Cycles >= 144_000_000)
                Edges.Add((bit, level));
        };
        M.Run(192_000_000);
    }

    public Machine M { get; }
    public List<(int Bit, bool Level)> Edges { get; } = new();          // P2.0-P2.11 changes in the last 2 s
}

public class CarSyncTests : IClassFixture<CarScriptedCrank>
{
    readonly Machine m; readonly List<(int Bit, bool Level)> edges;
    public CarSyncTests(CarScriptedCrank f) => (m, edges) = (f.M, f.Edges);

    ushort Xram(uint a) => (ushort)(m.Bus.Xram[a - 0xE000] | m.Bus.Xram[a - 0xE000 + 1] << 8);
    byte Ext(uint a) => m.Bus.ExternalRam[a - 0x8000];

    [Fact] public void Engine_IsSynced_AndRunning_WithoutASyncLoss()
    {
        Assert.Equal(2, m.Bus.Peek16(0xF900) & 0xFF);                   // F900 = 2: synced
        Assert.NotEqual(0, m.Bus.Peek16(0xFD7A) & 0x0010);              // FD7A.4
        Assert.Equal(0, Xram(0xE5AE));                                   // no loss at a gap check
        Assert.NotEqual(0, m.Bus.Peek16(0xFD2C) & 0x0600);              // FD2C.9 or .10: running, out of the start state
    }

    [Fact] public void Rpm_IsTheCommandedSpeed() => Assert.InRange(Xram(0xE0E4), 891, 909);

    [Fact] public void Cams_ReadAtTheirRestPositions() =>
        Assert.Equal((true, true), (Ext(0x8973) is >= 159 and <= 161, Ext(0x8AE1) is >= 119 and <= 121));

    [Fact] public void EveryCoilSparks_AndEveryInjectorCloses()
    {
        for (var c = 0; c < 6; c++)
        {
            Assert.True(edges.Count(e => e.Bit == 6 + c && !e.Level) >= 15, $"coil P2.{6 + c}");
            Assert.True(edges.Count(e => e.Bit == c && e.Level) >= 14, $"injector P2.{c}");
        }
    }
}
