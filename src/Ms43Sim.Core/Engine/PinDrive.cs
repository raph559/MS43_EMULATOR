namespace Ms43Sim.Core;

/// <summary>
/// The share of time an output pin spent low, integrated between calls. While the pin's PWM
/// channel has its output enabled (PENx) the instantaneous low share is 1 − the channel's high
/// share; otherwise it is 1 while the pin is low. The share is re-read at every change of the pin
/// (<see cref="Ports.PinChanged"/>) and of the channel's settings (<see cref="Pwm.SettingsChanged"/>),
/// so latch changes and PWM set-ups are exact; a PWx or PPx written while the channel runs is seen at
/// the restart that loads it, which raises SettingsChanged too.
/// </summary>
public sealed class PinDrive
{
    readonly Scheduler scheduler;
    readonly Ports ports;
    readonly int port, bit;
    readonly Pwm? pwm;
    readonly int channel;
    double share;           // the low share since `last`
    double lowCycles;       // low time integrated from `since` to `last`
    ulong since, last;

    public PinDrive(Scheduler scheduler, Ports ports, int port, int bit, Pwm? pwm = null, int pwmChannel = -1)
    {
        this.scheduler = scheduler;
        this.ports = ports;
        this.port = port;
        this.bit = bit;
        this.pwm = pwm;
        channel = pwmChannel;
        since = last = scheduler.Now;
        share = LowShareNow();
        ports.PinChanged += (p, b, _) =>
        {
            if (p == port && b == bit)
                Reread();
        };
        if (pwm != null)
            pwm.SettingsChanged += x =>
            {
                if (x == channel)
                    Reread();
            };
    }

    /// <summary>The share of time low since the last call (or construction), 0…1; the next call counts from now.</summary>
    public double TakeLowShare()
    {
        Integrate();
        var span = last - since;
        var result = span == 0 ? share : lowCycles / span;
        lowCycles = 0;
        since = last;
        share = LowShareNow();
        return result;
    }

    double LowShareNow() =>
        pwm != null && pwm.OutputEnabled(channel)
            ? 1 - pwm.PinHighFraction(channel, (ports.Latch(port) & (1 << bit)) != 0)
            : ports.Pin(port, bit) ? 0 : 1;

    void Reread()
    {
        Integrate();
        share = LowShareNow();
    }

    void Integrate()
    {
        var now = scheduler.Now;
        lowCycles += share * (now - last);
        last = now;
    }
}
