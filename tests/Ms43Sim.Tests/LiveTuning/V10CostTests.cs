using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using Xunit.Abstractions;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V10's CPU cost on top of V9's logging (Task 7 step 6): the cycles a housekeeping pass (0C:hk to 0D:80DE) and a
/// throttle store (09:B138 to 09:B13C) take, on V9 and on V10, with the logger polling sub 11 at full speed and a watch
/// set; and during a stab window and a blip on V10's own drives. V10 may not store a fault or overflow a task queue
/// (FD84 &amp; 01C0h) that V9 does not on the same drive. The observers only read.
/// </summary>
public sealed class V10CostTests(ITestOutputHelper output)
{
    private sealed record Costs(List<(double Seconds, ulong Cycles, int Fdf8)> Passes, List<ulong> Stores, int Queues, DriveResult Result);

    private static Costs Measure(byte[] image, Func<string, int> symbol, byte[]? settings, string drive)
    {
        var passes = new List<(double, ulong, int)>(); var stores = new List<ulong>(); var queues = 0;
        ulong hkStart = 0, storeStart = 0;
        var hk = (uint)symbol("hk");
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
        };
        settings?.CopyTo(image, V10Fixture.Defaults);
        var result = DriveRun.RunWithChecks(TestImages.AsFlashed(image),
            DriveParser.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot, "sim/drives/v10", drive + ".drive"))),
            new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        return new Costs(passes, stores, queues, result);
    }

    private static string[] Stored(DriveResult run) =>
        run.PowerCycles.SelectMany(c => c.Faults.Stored).Select(f => f.Label).Distinct().Order().ToArray();

    [Fact]
    public void V10AddsLittleToV9sLoggingAndNoFaultOrOverflow()
    {
        var costs = new[] { ("V9", (Func<Costs>)(() => Measure(V9Fixture.Bytes, V9Fixture.Symbol, null, "cost-full-load"))),
                            ("V10", () => Measure(V10Fixture.Bytes, V10Fixture.Symbol, null, "cost-full-load")) }
            .AsParallel().WithDegreeOfParallelism(2).Select(c => (c.Item1, Costs: c.Item2())).ToDictionary(c => c.Item1, c => c.Costs);
        var (v9, v10) = (costs["V9"], costs["V10"]);
        double Mean(IEnumerable<ulong> xs) => xs.Select(x => (double)x).DefaultIfEmpty().Average();
        output.WriteLine($"full load, logging and a watch: housekeeping {Mean(v9.Passes.Select(p => p.Cycles)):F0} cycles a pass on V9, " +
            $"{Mean(v10.Passes.Select(p => p.Cycles)):F0} on V10 ({v9.Passes.Count} and {v10.Passes.Count} passes); throttle store " +
            $"{Mean(v9.Stores):F1} on V9, {Mean(v10.Stores):F1} on V10 ({v9.Stores.Count} and {v10.Stores.Count} stores)");
        output.WriteLine($"queues: V9 {v9.Queues:X4}h, V10 {v10.Queues:X4}h; stored: V9 [{string.Join(", ", Stored(v9.Result))}], " +
            $"V10 [{string.Join(", ", Stored(v10.Result))}]");
        Assert.Equal(0, v10.Queues & ~v9.Queues);
        Assert.Empty(Stored(v10.Result).Except(Stored(v9.Result)));
    }

    [Fact]
    public void TheCostDuringAStabWindowAndABlip()
    {
        var stabs = Measure(V10Fixture.Bytes, V10Fixture.Symbol, V10DriveFixture.Stabs, "pops-stabs");
        var blip = Measure(V10Fixture.Bytes, V10Fixture.Symbol, V10DriveFixture.Blip, "blip-braking");
        static double Mean(IEnumerable<(double, ulong Cycles, int)> xs) => xs.Select(x => (double)x.Cycles).DefaultIfEmpty().Average();
        var window = stabs.Passes.Where(p => (p.Fdf8 & 8) != 0).ToList();
        var outside = stabs.Passes.Where(p => (p.Fdf8 & 8) == 0 && p.Seconds > 10).ToList();
        var blipping = blip.Passes.Where(p => (p.Fdf8 & 1 << V10Fixture.BlipOn) != 0).ToList();
        output.WriteLine($"housekeeping: {Mean(outside):F0} cycles a pass outside a window, {Mean(window):F0} in a stab window " +
            $"({window.Count} passes), {Mean(blipping):F0} during a blip ({blipping.Count} passes)");
        Assert.NotEmpty(window);
        Assert.NotEmpty(blipping);
    }
}
