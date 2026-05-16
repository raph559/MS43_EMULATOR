namespace Ms43Sim.Core;

/// <summary>
/// The electronic throttle: a DC motor on an H-bridge turning a spring-loaded plate, and two
/// complementary position tracks (plan 3, Reference facts). Every 0.5 ms the plant takes the
/// bridge's drive u (−1…+1, positive opening) and moves the plate as a velocity model: the motor
/// against a spring that pulls toward limp-home, with Coulomb friction. At limp-home the spring's
/// preload makes a detent that a small drive cannot leave. The plate stops at 0° and 88°.
/// </summary>
public sealed class ThrottleBody
{
    public const double LimpHome = 4.90, MaxAngle = 88.0;
    public const double Gain = 400, Friction = 0.03, Preload = 0.10, SpringAbove = 0.0025, SpringBelow = 0.01;
    public const ulong StepCycles = 12_000;
    const double StepSeconds = StepCycles / 24e6;
    const double Detent = Preload + Friction;

    readonly Scheduler scheduler;
    readonly Func<double> drive;
    long stepHandle = -1;

    public ThrottleBody(Scheduler scheduler, Func<double> drive)
    {
        this.scheduler = scheduler;
        this.drive = drive;
        Angle = LimpHome;
    }

    /// <summary>Degrees above the lower stop.</summary>
    public double Angle { get; private set; }

    /// <summary>AN12: 0.5078 V at the lower stop, rising 1 V per 20.914°.</summary>
    public static double Track1Volts(double angle) => 0.5078 + angle / 20.914;

    /// <summary>AN11: 4.5117 V at the lower stop, falling 1 V per 20.914°.</summary>
    public static double Track2Volts(double angle) => 4.5117 - angle / 20.914;

    /// <summary>
    /// The bridge's drive: while P6.7 is an output at 1 (enable), P7.2's share of time high minus
    /// 1 when P6.5 is high (closing); 0 otherwise.
    /// </summary>
    public static double Drive(Ports ports, Pwm pwm)
    {
        var enabled = ports.IsOutput(6, 7) && (ports.Latch(6) & 0x80) != 0;
        if (!enabled)
            return 0;
        var pin = pwm.PinHighFraction(2, (ports.Latch(7) & 0x04) != 0);
        return pin - (ports.Pin(6, 5) ? 1 : 0);
    }

    /// <summary>The plate rests at limp-home; the plant steps every 0.5 ms from now on.</summary>
    public void PowerOn()
    {
        if (stepHandle >= 0)
            scheduler.Cancel(stepHandle);
        Angle = LimpHome;
        ScheduleStep();
    }

    /// <summary>One step of the plant over <paramref name="seconds"/>, with the drive sampled now.</summary>
    public void Step(double seconds)
    {
        var u = drive();
        var d = Angle - LimpHome;
        double net;
        if (d == 0)
            net = Math.Abs(u) <= Detent ? 0 : u - Math.Sign(u) * Detent;
        else
        {
            var spring = d > 0 ? Preload + SpringAbove * d : -(Preload + SpringBelow * -d);
            var v = u - spring;
            net = Math.Abs(v) <= Friction ? 0 : v - Math.Sign(v) * Friction;
        }
        var next = Angle + Gain * net * seconds;
        var crossed = (d > 0 && next <= LimpHome) || (d < 0 && next >= LimpHome);
        if (crossed && Math.Abs(u) <= Detent)
            next = LimpHome;
        Angle = Math.Clamp(next, 0, MaxAngle);
    }

    void ScheduleStep() =>
        stepHandle = scheduler.Schedule(scheduler.Now + StepCycles, "throttle", () =>
        {
            Step(StepSeconds);
            ScheduleStep();
        });
}
