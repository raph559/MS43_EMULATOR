namespace Ms43Sim.Core;

/// <summary>The engine's phases: before the starter, cranking, running on its own, coasting down.</summary>
public enum EnginePhase { Stopped, Starter, Running, Stopping }

public sealed record EnginePhaseChange(ulong Cycle, EnginePhase Phase);

/// <summary>
/// The engine around the ECU (plan 3, Reference facts): the shaft, the VANOS units, a starter, a
/// combustion rule and a crude idle-air plant. It reads only what the hardware sees: the pins and
/// the time.
/// <list type="bullet">
/// <item>Combustion [assumption]: a falling edge of an output coil pin P2.6–P2.11 and a rising
/// edge (a closing) of an output injector pin P2.0–P2.5, each within the last 0.25 s.</item>
/// <item>Phases [assumption]: Stopped until the scenario's crank time; Starter for its starter time
/// (0 → the starter speed in 0.2 s); then Running if combusting, else Stopping. Running without
/// combustion becomes Stopping; Stopping with combustion at 300 rpm or more runs again, and below
/// 30 rpm it stops. The battery reads the scenario's value for the phase.</item>
/// <item>The idle-air plant, every 0.5 ms [assumption; constants from the research's verified plant]:
/// d is the share of time P2.12 (the idle valve) is low; Qcmd = min(50 kg/h · d, 480 mg/stroke at
/// n); Q follows Qcmd with a 50 ms lag; running, n follows Q · 5555.6 / 105 mg/stroke with a 0.3 s
/// lag; stopping, n decays with a 0.5 s lag. At d = 0.34 it settles at 899 rpm with Q = 17.0 kg/h.</item>
/// <item>Sensors: AN0 the MAF for Q, AN14 the coil current, AN15 the knock envelope, P3.6 the burn
/// pulse after each spark (<see cref="EngineSensors"/>).</item>
/// </list>
/// </summary>
public sealed class Engine
{
    public const double ValveFlow = 50, IdleLoad = 105, FullLoad = 480;
    public const double FlowLag = 0.05, SpeedLag = 0.3, StopLag = 0.5, CombustionWindow = 0.25;
    public const ulong StepCycles = 12_000;
    const double MgPerStroke = 5555.6;                  // mg/stroke = kg/h × 5555.6 / rpm
    const double StarterRamp = 0.2, StallRpm = 30, RestartRpm = 300;

    readonly Machine machine;
    readonly PinDrive valve;
    readonly List<EnginePhaseChange> phases = new();
    readonly double cpuHz, stepSeconds;
    readonly ulong crankAt, releaseAt, combustionWindow;
    ulong? lastSpark, lastClosing;
    long burnEnd = -1;

    Engine(Machine machine, EngineScenario scenario)
    {
        this.machine = machine;
        Scenario = scenario;
        cpuHz = machine.Options.CpuHz;
        stepSeconds = StepCycles / cpuHz;
        crankAt = (ulong)Math.Round(scenario.CrankAtSeconds * cpuHz);
        releaseAt = crankAt + (ulong)Math.Round(scenario.StarterSeconds * cpuHz);
        combustionWindow = (ulong)Math.Round(CombustionWindow * cpuHz);

        machine.Board.SetInputs(scenario.Inputs.WithBattery(scenario.BatteryOff));
        Shaft = new EngineShaft(machine.Scheduler, machine.Board, scenario.StartAngle);
        valve = new PinDrive(machine.Scheduler, machine.Ports, 2, 12);
        var intake = new PinDrive(machine.Scheduler, machine.Ports, 7, 0, machine.Pwm, 0);
        var exhaust = new PinDrive(machine.Scheduler, machine.Ports, 7, 1, machine.Pwm, 1);
        Vanos = new VanosActuators(intake.TakeLowShare, exhaust.TakeLowShare, scenario.HoldIntake, scenario.HoldExhaust);
        machine.Board.AttachAnalog(0, () => EngineSensors.MafVolts(AirflowKgPerHour));
        machine.Board.AttachAnalog(14, () => Rpm >= EngineSensors.TurningRpm ? EngineSensors.CoilCurrentVolts : 0);
        machine.Board.AttachAnalog(15, () => EngineSensors.KnockVolts(Rpm));
        machine.Board.Drive(3, 6, false);
        machine.Ports.PinChanged += OnPinChanged;

        phases.Add(new EnginePhaseChange(machine.Cycles, EnginePhase.Stopped));
        machine.Scheduler.Schedule(crankAt, "starter", () => Change(EnginePhase.Starter));
        ScheduleStep(machine.Cycles + StepCycles);
    }

    /// <summary>The engine speed the valve opening <paramref name="valveOpening"/> holds at the idle load.</summary>
    public static double IdleSpeedFor(double valveOpening) => ValveFlow * valveOpening * MgPerStroke / IdleLoad;

    /// <summary>Puts an engine around <paramref name="machine"/>, after its power-on.</summary>
    public static Engine Attach(Machine machine, EngineScenario scenario) => new(machine, scenario);

    public EngineScenario Scenario { get; }

    public EngineShaft Shaft { get; }

    public VanosActuators Vanos { get; }

    public EnginePhase Phase { get; private set; }

    /// <summary>Every phase change, starting with (attach cycle, Stopped).</summary>
    public IReadOnlyList<EnginePhaseChange> Phases => phases;

    public double Rpm { get; private set; }

    public double AirflowKgPerHour { get; private set; }

    public bool Combusting { get; private set; }

    void OnPinChanged(int port, int bit, bool level)
    {
        if (port != 2 || !machine.Ports.IsOutput(2, bit))
            return;
        if (bit is >= 6 and <= 11 && !level)
            Spark();
        else if (bit <= 5 && level)
            lastClosing = machine.Cycles;
    }

    /// <summary>A spark: P3.6 goes high now and low 1.2 ms after the latest spark.</summary>
    void Spark()
    {
        var now = machine.Cycles;
        lastSpark = now;
        if (burnEnd >= 0)
            machine.Scheduler.Cancel(burnEnd);
        machine.Board.Drive(3, 6, true);
        burnEnd = machine.Scheduler.Schedule(now + EngineSensors.BurnPulseCycles, "burn pulse end", () =>
        {
            burnEnd = -1;
            machine.Board.Drive(3, 6, false);
        });
    }

    void ScheduleStep(ulong at) => machine.Scheduler.Schedule(at, "engine step", () =>
    {
        Step();
        ScheduleStep(at + StepCycles);
    });

    void Step()
    {
        var now = machine.Cycles;
        var d = valve.TakeLowShare();
        var command = Math.Min(ValveFlow * d, FullLoad * Rpm / MgPerStroke);
        AirflowKgPerHour += (command - AirflowKgPerHour) * (1 - Math.Exp(-stepSeconds / FlowLag));
        Combusting = Recent(lastSpark) && Recent(lastClosing);

        switch (Phase)
        {
            case EnginePhase.Starter when now >= releaseAt:
                Change(Combusting ? EnginePhase.Running : EnginePhase.Stopping);
                break;
            case EnginePhase.Running when !Combusting:
                Change(EnginePhase.Stopping);
                break;
            case EnginePhase.Stopping when Combusting && Rpm >= RestartRpm:
                Change(EnginePhase.Running);
                break;
        }
        switch (Phase)
        {
            case EnginePhase.Starter:
                Rpm = Scenario.StarterRpm * Math.Min(1, (now - crankAt) / cpuHz / StarterRamp);
                break;
            case EnginePhase.Running:
                Rpm += (AirflowKgPerHour * MgPerStroke / IdleLoad - Rpm) * (1 - Math.Exp(-stepSeconds / SpeedLag));
                break;
            case EnginePhase.Stopping:
                Rpm -= Rpm * (1 - Math.Exp(-stepSeconds / StopLag));
                if (Rpm < StallRpm)
                {
                    Rpm = 0;
                    Change(EnginePhase.Stopped);
                }
                break;
        }

        Vanos.Step(stepSeconds, Rpm);
        if (Vanos.IntakeAdvance != Shaft.IntakeAdvance || Vanos.ExhaustRetard != Shaft.ExhaustRetard)
            Shaft.SetCamOffsets(Vanos.IntakeAdvance, Vanos.ExhaustRetard);
        if (Rpm != Shaft.Rpm)
            Shaft.SetSpeed(Rpm);

        bool Recent(ulong? at) => at is { } t && now - t <= combustionWindow;
    }

    /// <summary>A new phase, with the battery the scenario gives it.</summary>
    void Change(EnginePhase phase)
    {
        if (phase == Phase)
            return;
        Phase = phase;
        phases.Add(new EnginePhaseChange(machine.Cycles, phase));
        var battery = phase switch
        {
            EnginePhase.Starter => Scenario.BatteryStarter,
            EnginePhase.Running => Scenario.BatteryRunning,
            _ => Scenario.BatteryOff,
        };
        machine.Board.SetInputs(machine.Board.Inputs.WithBattery(battery));
    }
}
