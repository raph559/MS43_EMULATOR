using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// The watch's 24 hooks reach W_WOFF, W_WPAGE and the record through DPP2, against the overlay's rule of EXTP #10h. That
/// holds only while DPP2 is 10h at every hook site. The stock code sets 14h..17h only in its copy at 0E:2FB6..0E:3044;
/// these drives check that no hooked lookup runs there, from the cold start to full load, with and without a watch.
/// The observers only read registers.
/// </summary>
public sealed class WatchDppTests
{
    static FlashedImage Image => TestImages.AsFlashed(V10Fixture.Bytes);

    public static TheoryData<string> Drives => new()
    {
        "watch/part-load-watch", "watch/idle-watch", "watch/overrun-watch", "cold-start", "full-load", "key-cycle",
    };

    [Theory, MemberData(nameof(Drives))]
    public void EveryHookedLookupRunsWithDpp2At10h(string name)
    {
        var passes = 0L; var watched = 0L; var copies = 0;
        var wrong = new List<string>();
        void Attach(DriveContext context) => context.PowerCycleStarted += (machine, _) =>
        {
            foreach (var (at, _) in WatchOracle.Readers.Concat(WatchOracle.Descriptors))
                machine.OnInstruction(at, m =>
                {
                    passes++;
                    if ((m.Bus.Peek16(0xFDF8) & 1 << 5) != 0) watched++;
                    if (m.Cpu.Dpp(2) != 0x10 && wrong.Count < 10) wrong.Add($"{at:X5} DPP2 = {m.Cpu.Dpp(2):X} at cycle {m.Cycles}");
                });
            machine.OnInstruction(0xE2FAE, _ => copies++);
        };
        var drive = DriveParser.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot, "sim/drives", name + ".drive")));
        DriveRun.Run(Image, drive, new DriveRunOptions(Observers: new Action<DriveContext>[] { Attach }));
        Console.WriteLine($"{name}: {passes} hooked lookups, {watched} with a watch armed, the 0E:2FAE copy ran {copies} times");
        Assert.True(passes > 100_000, $"only {passes} hooked lookups");
        if (name.StartsWith("watch/")) Assert.True(watched > 10_000, $"only {watched} lookups with a watch armed");
        Assert.Empty(wrong);
    }
}
