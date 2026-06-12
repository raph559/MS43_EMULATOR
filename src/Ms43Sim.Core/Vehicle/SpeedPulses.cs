namespace Ms43Sim.Core;

/// <summary>
/// The wired road speed (plan 4 R9): P7.6 (CC30), where the firmware counts falling edges, 25,096 a
/// kilometre, so 6.971 a second per km/h. The pin rests high and is low for half a period after each
/// fall, always rising again before the next one [assumption: only the falls count]. A new speed
/// changes the rate from now on without restarting the period, so a speed set again at every step of
/// a ramp still pulses.
/// </summary>
public sealed class SpeedPulses
{
    public const double PulsesPerKm = 25_096;

    /// <summary>Falls a second per km/h.</summary>
    public const double HzPerKmh = PulsesPerKm / 3600;

    const int Port = 7, Bit = 6;

    readonly Scheduler scheduler;
    readonly ISignalPins pins;
    readonly double cpuHz;
    double rate;                                   // falls per cycle
    double progress;                               // the way to the next fall, 0…1, at cycle `since`
    ulong since, riseAt;
    long nextFall = -1, rise = -1;

    public SpeedPulses(Scheduler scheduler, ISignalPins pins, double cpuHz = 24e6)
    {
        this.scheduler = scheduler;
        this.pins = pins;
        this.cpuHz = cpuHz;
    }

    public double Kmh { get; private set; }

    /// <summary>Pulses at <paramref name="kmh"/> from now on; 0 (or less) stops them.</summary>
    public void SetSpeed(double kmh)
    {
        kmh = Math.Max(0, kmh);
        if (kmh == Kmh)
            return;
        var now = scheduler.Now;
        progress = Math.Min(1, progress + (now - since) * rate);
        since = now;
        Kmh = kmh;
        rate = kmh * HzPerKmh / cpuHz;
        if (nextFall >= 0)
            scheduler.Cancel(nextFall);
        nextFall = -1;
        if (rate > 0)
            ScheduleFall();
    }

    void ScheduleFall()
    {
        var wait = (ulong)Math.Max(1, Math.Ceiling((1 - progress) / rate));
        var at = since + wait;
        if (rise >= 0 && riseAt >= at)
        {
            scheduler.Cancel(rise);                // a faster speed: rise before the next fall
            Rise(scheduler.Now + (at - scheduler.Now) / 2);
        }
        nextFall = scheduler.Schedule(at, "speed pulse", () =>
        {
            progress = Math.Max(0, progress + wait * rate - 1);
            since = at;
            pins.Drive(Port, Bit, false);
            Rise(at + (ulong)Math.Max(1, Math.Round(0.5 / rate)));
            ScheduleFall();
        });
    }

    void Rise(ulong at)
    {
        riseAt = at;
        rise = scheduler.Schedule(at, "speed pulse end", () =>
        {
            rise = -1;
            pins.Drive(Port, Bit, true);
        });
    }
}
