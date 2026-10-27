namespace Ms43Sim.Core;

public enum CalibrationSafetyStatus { NotEvaluated }

public sealed record MissingSafetyModel(string Id, string Description);

/// <summary>The current emulator cannot issue physical engine or calibration safety clearance.</summary>
public sealed class CalibrationSafetyAssessment
{
    CalibrationSafetyAssessment() { }

    public static CalibrationSafetyAssessment Current { get; } = new();

    public CalibrationSafetyStatus Status => CalibrationSafetyStatus.NotEvaluated;

    public bool EngineSafeClearance => false;

    public IReadOnlyList<MissingSafetyModel> MissingModels { get; } = Array.AsReadOnly(new[]
    {
        new MissingSafetyModel("knock_and_cylinder_pressure", "Combustion pressure, detonation and fuel-octane response to ignition and mixture are not modelled; knock input is a function of RPM."),
        new MissingSafetyModel("mixture_and_fuel_delivery", "Measured AFR, injector flow, fuel pressure and fuel delivery under load are not modelled."),
        new MissingSafetyModel("thermal_response", "Cylinder, exhaust and cooling-system temperatures do not respond to the calibration's combustion or heat release."),
        new MissingSafetyModel("torque_and_mechanical_limits", "The scripted shaft speed has no torque model; mechanical load, durability and component limits are not evaluated."),
        new MissingSafetyModel("physical_ecu_timing", "Instruction timing uses a minimum-state model; physical ECU timing and scheduling margins have not been measured."),
    });
}
