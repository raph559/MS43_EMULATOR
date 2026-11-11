using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using Xunit.Abstractions;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The overlay's CPU cost: the cycles a housekeeping pass (0C:hk to 0D:80DE) and a throttle store (09:B138 to 09:B13C)
/// take, with the logger polling sub 11 at full speed and a watch set; and during a pops window and a blip. It may not
/// store a fault or overflow a task queue (FD84 &amp; 01C0h). The observers only read.
/// </summary>
public sealed class V10CostTests(ITestOutputHelper output)
{
    private sealed record Costs(List<(double Seconds, ulong Cycles, int Fdf8)> Passes, List<ulong> Stores, int Queues, DriveResult Result,
        List<(double Seconds, ulong Cycles, int Fdf8)> Gates);

    private static Costs Measure(byte[] image, byte[]? settings, string drive)
    {
        var passes = new List<(double, ulong, int)>(); var stores = new List<ulong>(); var queues = 0;
        var gates = new List<(double, ulong, int)>();
        ulong hkStart = 0, storeStart = 0, gateStart = 0; var gateFlags = 0;
        var hk = (uint)V10Fixture.Symbol("hk");
        void Attach(DriveContext context) => context.PowerCycleStarted += (machine, _) =>
        {
            machine.OnInstruction(hk, m => hkStart = m.Cycles);
            machine.OnInstruction(0xD80DE, m =>
            {
                if (hkStart == 0) return;
                passes.Add((m.Cycles / 24e6, m.Cycles - hkStart, m.Bus.Peek16(0xFDF8)));
                queues |= m.Bus.Peek16(0xFD84) & 0x01C0;
                hkStart = 0;
            });
            machine.OnInstruction(0x9B138, m => storeStart = m.Cycles);
            machine.OnInstruction(0x9B13C, m => { if (storeStart != 0) stores.Add(m.Cycles - storeStart); storeStart = 0; });
            // The overrun-cut gate (0A:A7BC, pops' hook) to its stock exits, fuelled (0A:B0F2) or cut (0A:A7C8).
            machine.OnInstruction(0xAA7BC, m => { gateStart = m.Cycles; gateFlags = m.Bus.Peek16(0xFDF8); });
            void GateEnd(Machine m) { if (gateStart != 0) gates.Add((m.Cycles / 24e6, m.Cycles - gateStart, gateFlags)); gateStart = 0; }
            machine.OnInstruction(0xAB0F2, GateEnd);
            machine.OnInstruction(0xAA7C8, GateEnd);
        };
        settings?.CopyTo(image, V10Fixture.Defaults);
        var result = DriveRun.RunWithChecks(TestImages.AsFlashed(image),
            DriveParser.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot, "sim/drives/v10", drive + ".drive"))),
            new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        return new Costs(passes, stores, queues, result, gates);
    }

    private static string[] Stored(DriveResult run) =>
        run.PowerCycles.SelectMany(c => c.Faults.Stored).Select(f => f.Label).Distinct().Order().ToArray();

    [Fact]
    public void LoggingAtFullLoadStoresNoFaultAndOverflowsNoQueue()
    {
        var costs = Measure(V10Fixture.Bytes, null, "cost-full-load");
        double Mean(IEnumerable<ulong> xs) => xs.Select(x => (double)x).DefaultIfEmpty().Average();
        output.WriteLine($"full load, logging and a watch: housekeeping {Mean(costs.Passes.Select(p => p.Cycles)):F0} cycles a pass " +
            $"({costs.Passes.Count} passes); throttle store {Mean(costs.Stores):F1} ({costs.Stores.Count} stores)");
        output.WriteLine($"queues: {costs.Queues:X4}h; stored: [{string.Join(", ", Stored(costs.Result))}]");
        // Polling the log at full speed overflows two task queues (FD84 bits 7 and 8) at full load, as it did before the
        // blip was added; no other queue may overflow.
        Assert.Equal(0, costs.Queues & ~0x0180);
        Assert.Empty(Stored(costs.Result));
    }

    [Fact]
    public void TheCostDuringAPopsWindowAndABlip()
    {
        var stabs = Measure(V10Fixture.Bytes, V10DriveFixture.Pops, "pops-window");
        var blip = Measure(V10Fixture.Bytes, V10DriveFixture.Blip, "blip-braking");
        static double Mean(IEnumerable<(double, ulong Cycles, int)> xs) => xs.Select(x => (double)x.Cycles).DefaultIfEmpty().Average();
        var window = stabs.Passes.Where(p => (p.Fdf8 & 8) != 0).ToList();
        var outside = stabs.Passes.Where(p => (p.Fdf8 & 8) == 0 && p.Seconds > 10).ToList();
        var blipping = blip.Passes.Where(p => (p.Fdf8 & 1 << V10Fixture.BlipOn) != 0).ToList();
        output.WriteLine($"housekeeping: {Mean(outside):F0} cycles a pass outside a window, {Mean(window):F0} in a pops window " +
            $"({window.Count} passes), {Mean(blipping):F0} during a blip ({blipping.Count} passes)");
        Assert.NotEmpty(window);
        Assert.NotEmpty(blipping);
    }

    /// <summary>
    /// The V10.1 review: the overrun-cut gate now calls rise_allowance in a running window, in the fuel task. On V10.1's
    /// own lift (3rd at 120 km/h, its defaults) a gate pass inside the window may take no more than 1 % of the CPU.
    /// </summary>
    [Fact]
    public void TheGateCostInAV101Window()
    {
        var run = Measure(V10Fixture.Bytes, V10DriveFixture.V101Pops, "pops-v101");
        static double Mean(IEnumerable<(double, ulong Cycles, int)> xs) => xs.Select(x => (double)x.Cycles).DefaultIfEmpty().Average();
        var inside = run.Gates.Where(g => (g.Fdf8 & 8) != 0).ToList();
        var outside = run.Gates.Where(g => (g.Fdf8 & 8) == 0 && g.Seconds > 10).ToList();
        Assert.NotEmpty(inside);
        var seconds = inside[^1].Seconds - inside[0].Seconds;
        var share = inside.Sum(g => (double)g.Cycles) / 24e6 / seconds;
        output.WriteLine($"gate: {Mean(outside):F0} cycles a pass outside a window ({outside.Count}), {Mean(inside):F0} inside " +
            $"({inside.Count} passes in {seconds:F2} s, {share:P2} of the CPU)");
        Assert.True(share < 0.01, $"the gate takes {share:P2} of the CPU in the window");
    }
}
