namespace Ms43Sim.Tests.Support;

/// <summary>Plays an ECU's outputs on P2 through SFR writes, for engine tests whose CPU only spins.</summary>
public static class FakeEcu
{
    const ulong ValvePeriod = 240_000;                                   // 10 ms

    /// <summary>
    /// Every <paramref name="every"/> cycles the spark pin goes high then low (a spark) and the
    /// injector pin low then high (a closing); P2.12 is low for <paramref name="valveLowShare"/> of
    /// every 10 ms. The pins must already be outputs.
    /// </summary>
    public static void Fire(TestMachine t, int sparkPin, int injectorPin, ulong every, double valveLowShare)
    {
        var scheduler = t.M.Scheduler;
        var lowCycles = (ulong)Math.Round(valveLowShare * ValvePeriod);

        void Set(int bit, bool level)
        {
            var latch = t.M.Ports.Latch(2);
            t.WriteSfr(0xFFC0, (ushort)(level ? latch | 1 << bit : latch & ~(1 << bit)));
        }

        void Cylinder(ulong at) => scheduler.Schedule(at, "fake spark and injection", () =>
        {
            Set(sparkPin, true);
            Set(sparkPin, false);
            Set(injectorPin, false);
            Set(injectorPin, true);
            Cylinder(at + every);
        });

        void Valve(ulong at) => scheduler.Schedule(at, "fake valve", () =>
        {
            Set(12, false);
            scheduler.Schedule(at + lowCycles, "fake valve", () => Set(12, true));
            Valve(at + ValvePeriod);
        });

        Cylinder(scheduler.Now + every);
        Valve(scheduler.Now);
    }
}
