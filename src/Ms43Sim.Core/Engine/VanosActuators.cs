namespace Ms43Sim.Core;

/// <summary>
/// The two VANOS units (plan 3, Reference facts) [assumption]. Each cam's offset from rest x, in
/// °CRK, follows its solenoid's drive (the share of time its P7 pin is low): with oil pressure,
/// above 500 rpm, x' = 400 °CRK/s × (drive − hold); without it the unit returns to rest at
/// 200 °CRK/s. The intake advances up to 40.1 °CRK, the exhaust retards up to 25.1 °CRK. The hold
/// duties are the scenario's (the car's hold map at its oil temperature and battery voltage).
/// </summary>
public sealed class VanosActuators
{
    public const double IntakeRange = 40.1, ExhaustRange = 25.1, Gain = 400, ReturnRate = 200, OilPressureRpm = 500;

    readonly Func<double> intakeDrive, exhaustDrive;
    readonly double holdIntake, holdExhaust;

    public VanosActuators(Func<double> intakeDrive, Func<double> exhaustDrive, double holdIntake, double holdExhaust)
    {
        this.intakeDrive = intakeDrive;
        this.exhaustDrive = exhaustDrive;
        this.holdIntake = holdIntake;
        this.holdExhaust = holdExhaust;
    }

    /// <summary>x_in: the intake cam's advance from rest, 0…40.1 °CRK.</summary>
    public double IntakeAdvance { get; private set; }

    /// <summary>x_ex: the exhaust cam's retard from rest, 0…25.1 °CRK.</summary>
    public double ExhaustRetard { get; private set; }

    /// <summary>One step of both units over <paramref name="seconds"/>; each drive is sampled once.</summary>
    public void Step(double seconds, double rpm)
    {
        var oil = rpm > OilPressureRpm;
        IntakeAdvance = Move(IntakeAdvance, intakeDrive(), holdIntake, IntakeRange);
        ExhaustRetard = Move(ExhaustRetard, exhaustDrive(), holdExhaust, ExhaustRange);

        double Move(double x, double drive, double hold, double range) =>
            Math.Clamp(x + (oil ? Gain * (drive - hold) : -ReturnRate) * seconds, 0, range);
    }
}
