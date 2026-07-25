namespace Ms43Sim.Core;

/// <summary>The engine's phases: before the starter, cranking, running on its own, coasting down.</summary>
public enum EnginePhase { Stopped, Starter, Running, Stopping }

public sealed record EnginePhaseChange(ulong Cycle, EnginePhase Phase);

/// <summary>
/// The engine around the ECU (plan 3, Reference facts): the shaft, the VANOS units, a starter, a
/// combustion rule and a crude idle-air plant. It reads only what the hardware sees: the pins and
/// the time.
/// <list type="bullet">
/// <item>Combustion [assumption]: a spark — a falling edge of an output coil pin P2.6–P2.11 after at
/// least 0.5 ms high (<see cref="MinSparkDwellCycles"/>; an ignition cut's 44–125 µs pulses store no
/// energy, plan 4 R1) — and a rising edge (a closing) of an output injector pin P2.0–P2.5, each within
/// the last 0.25 s. Only edges of pins that are outputs before and after count (<see cref="Ports.OutputEdge"/>):
/// a direction set-up is not an edge. A starved engine (<see cref="Starve"/>, a drive's stall) never combusts.</item>
/// <item>Phases [assumption]: Stopped until the scenario's crank time (or until a drive starts the
/// starter, <see cref="StartStarter"/>); Starter for its starter time (or until <see cref="StopStarter"/>),
/// 0 → the starter speed in 0.2 s; then Running if combusting, else Stopping. Running without
/// combustion becomes Stopping; Stopping with combustion at 300 rpm or more runs again, and below
/// 30 rpm it stops. The battery reads the scenario's value for the phase.</item>
/// <item>The idle-air plant, every 0.5 ms [assumption; constants from the research's verified plant]:
/// d is the share of time P2.12 (the idle valve) is low; Qcmd = min(50 kg/h · d, 480 mg/stroke at
/// n), or the scenario's <see cref="BreathingLaw"/> with the throttle's angle and the temperatures
/// given (plan 4 R12); Q follows Qcmd with a 50 ms lag; running, n follows Q · 5555.6 / 105 mg/stroke
/// with a 0.3 s lag; stopping, n decays with a 0.5 s lag. At d = 0.34 it settles at 899 rpm with
/// Q = 17.0 kg/h.</item>
/// <item>A commanded speed (<see cref="SetCommandedSpeed"/>, a drive's scripted speed off idle; plan 4
/// owner decision) turns the shaft at that speed like a dyno: no speed law and no stall, so a cut
/// shows only as missing sparks or injections; the engine counts as Running (Stopped at 0 rpm).</item>
/// <item>Sensors: AN0 the MAF for Q, AN14 the coil current, AN15 the knock envelope, P3.6 the burn
/// pulse after each spark (<see cref="EngineSensors"/>).</item>
/// </list>
/// </summary>
public sealed class Engine
{
    public const double ValveFlow = 50, IdleLoad = 105, FullLoad = 480;
    public const double FlowLag = 0.05, SpeedLag = 0.3, StopLag = 0.5, CombustionWindow = 0.25;
    public const ulong StepCycles = 12_000;

    /// <summary>0.5 ms: a coil's fall is a spark only after this much dwell (plan 4 C7).</summary>
    public const ulong MinSparkDwellCycles = 12_000;

    const double MgPerStroke = 5555.6;                  // mg/stroke = kg/h × 5555.6 / rpm
    const double StarterRamp = 0.2, StallRpm = 30, RestartRpm = 300;

    readonly Machine machine;
    readonly PinDrive valve;
    readonly List<EnginePhaseChange> phases = new();
    readonly double cpuHz, stepSeconds;
    readonly ulong combustionWindow;
    ulong crankAt, releaseAt;
    double starterRpm, intakeC, coolantC;
    double? commanded;
    ulong? lastSpark, lastClosing;
    bool starved;
    readonly ulong?[] coilRise = new ulong?[6];
    long burnEnd = -1;

    Engine(Machine machine, EngineScenario scenario)
    {
        this.machine = machine;
        Scenario = scenario;
        cpuHz = machine.Options.CpuHz;
        stepSeconds = StepCycles / cpuHz;
        crankAt = scenario.CrankAtSeconds is { } crank ? (ulong)Math.Round(crank * cpuHz) : ulong.MaxValue;
        releaseAt = scenario.CrankAtSeconds is null ? ulong.MaxValue : crankAt + (ulong)Math.Round(scenario.StarterSeconds * cpuHz);
        starterRpm = scenario.StarterRpm;
        (intakeC, coolantC) = (scenario.IntakeC, scenario.CoolantC);
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
        machine.Ports.OutputEdge += OnOutputEdge;

        phases.Add(new EnginePhaseChange(machine.Cycles, EnginePhase.Stopped));
        if (scenario.CrankAtSeconds is not null)
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

    /// <summary>Raised on each phase change after the attach, with the new phase.</summary>
    public event Action<EnginePhase>? PhaseChanged;

    public double Rpm { get; private set; }

    public double AirflowKgPerHour { get; private set; }

    public bool Combusting { get; private set; }

    /// <summary>The speed a drive holds the shaft at, or null when the idle plant and the starter decide it.</summary>
    public double? CommandedRpm => commanded;

    /// <summary>
    /// Non-null: the shaft turns at <paramref name="rpm"/> from the next step, whatever burns (a dyno);
    /// null: the idle plant takes the engine back from its speed now.
    /// </summary>
    public void SetCommandedSpeed(double? rpm) => commanded = rpm is { } r ? Math.Max(0, r) : null;

    /// <summary>The starter turns the engine from now, up to <paramref name="rpm"/> in 0.2 s, until <see cref="StopStarter"/>.</summary>
    public void StartStarter(double rpm)
    {
        starterRpm = rpm;
        crankAt = machine.Cycles;
        releaseAt = ulong.MaxValue;
        Change(EnginePhase.Starter);
    }

    /// <summary>The starter releases at the next step: the engine runs on if it is combusting.</summary>
    public void StopStarter() => releaseAt = machine.Cycles;

    /// <summary>
    /// While <paramref name="starved"/>, nothing burns, whatever the ECU sparks and injects: the engine runs down and stops
    /// with the key on (a drive's stall, plan 5 Task 5). A crank feeds it again.
    /// </summary>
    public void Starve(bool starved) => this.starved = starved;

    /// <summary>The intake air and coolant temperatures the breathing law's density uses (the scenario's until set).</summary>
    public void SetTemperatures(double intakeC, double coolantC) => (this.intakeC, this.coolantC) = (intakeC, coolantC);

    void OnOutputEdge(int port, int bit, bool level)
    {
        if (port != 2)
            return;
        if (bit is >= 6 and <= 11)
        {
            var coil = bit - 6;
            if (level)
                coilRise[coil] = machine.Cycles;
            else
            {
                if (coilRise[coil] is { } rise && machine.Cycles - rise >= MinSparkDwellCycles)
                    Spark();
                coilRise[coil] = null;
            }
        }
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
        if (Scenario.Law is { } law)
            AirflowKgPerHour = law.AirflowKgPerHour(Rpm, machine.Throttle.Angle, d, Scenario.AmbientKpa, intakeC, coolantC,
                AirflowKgPerHour, stepSeconds);
        else
        {
            var command = Math.Min(ValveFlow * d, FullLoad * Rpm / MgPerStroke);
            AirflowKgPerHour += (command - AirflowKgPerHour) * (1 - Math.Exp(-stepSeconds / FlowLag));
        }
        Combusting = !starved && Recent(lastSpark) && Recent(lastClosing);

        if (commanded is { } scripted)
        {
            Change(scripted > 0 ? EnginePhase.Running : EnginePhase.Stopped);
            Rpm = scripted;
        }
        else
            FollowThePlant(now);

        Vanos.Step(stepSeconds, Rpm);
        if (Vanos.IntakeAdvance != Shaft.IntakeAdvance || Vanos.ExhaustRetard != Shaft.ExhaustRetard)
            Shaft.SetCamOffsets(Vanos.IntakeAdvance, Vanos.ExhaustRetard);
        if (Rpm != Shaft.Rpm)
            Shaft.SetSpeed(Rpm);

        bool Recent(ulong? at) => at is { } t && now - t <= combustionWindow;
    }

    /// <summary>Plan 3's phases and speed laws: the starter, the idle plant, the run-down.</summary>
    void FollowThePlant(ulong now)
    {
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
                Rpm = starterRpm * Math.Min(1, (now - crankAt) / cpuHz / StarterRamp);
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
        PhaseChanged?.Invoke(phase);
    }
}
