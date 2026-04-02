namespace Ms43Sim.Core;

public sealed record BootRunOptions(ulong MaxCycles = 30_000_000, bool StopAtFirstHandover = false, MachineOptions? Machine = null);

/// <summary>One boot from reset: what its checkpoints saw.</summary>
public sealed record BootPass(ulong StartCycle, bool SelfTestPassed, ushort? RamTestWord, byte? IdCheck, ulong? HandoverCycle);

/// <summary>In order of precedence: two or more resets, a hardware trap, a handover, the watchdog serviced, nothing.</summary>
public enum BootOutcome { ResetLoop, Trapped, HandedOver, StayedInBoot, NoProgress }

public sealed record BootResult(
    FlashedImage Image,
    BootOutcome Outcome,
    IReadOnlyList<BootPass> Passes,
    uint HandoverTarget,
    bool CheckpointsRecognised,
    int WatchdogResets,
    int SoftwareResets,
    int SrvwdtCount,
    IReadOnlyList<SimEvent> Events,
    IReadOnlyDictionary<Type, long> EventCounts,
    long EventsDropped,
    IReadOnlyList<AccessRecord> Inventory,
    IReadOnlyList<BranchRecord> LastBranches,
    ulong Cycles,
    ulong Instructions,
    string StateHash,
    IReadOnlyList<string> Assumptions)
{
    /// <summary>Events of type T the run raised, including those the log did not keep.</summary>
    public long CountOf<T>() where T : SimEvent => EventCounts.GetValueOrDefault(typeof(T));
}

/// <summary>
/// Runs an image from power-on and watches the MS43 boot block (`MS43000624141549`, shared by
/// the car, release and stock files). Checkpoints, CPU addresses in segment 0:
/// 0x2E30 the register self-test passed; 0x2FA2 R15 is the RAM-test word; 0x2CE0 RL4 is the
/// ID-check result. The handover target is CSP:IP from the words at 0x6ED08:0x6ED06.
/// </summary>
public static class BootRun
{
    const uint SelfTestPassed = 0x002E30, RamTestWord = 0x002FA2, IdCheck = 0x002CE0;

    public static BootResult Run(FlashedImage image, BootRunOptions? options = null)
    {
        options ??= new BootRunOptions();
        var machineOptions = options.Machine ?? new MachineOptions();
        var machine = Machine.Create(image, machineOptions);
        machine.PowerOn();

        var bytes = image.Image;
        var recognised = Matches(bytes, 0x2E30, 0x6E, 0xE6)
                         && Matches(bytes, 0x2FA2, 0xF6, 0xFF, 0xDC, 0xF8)
                         && Matches(bytes, 0x2CE0, 0x47, 0xF8, 0x20, 0x00);
        var target = ((uint)bytes.Read16(0x6ED08) << 16) | bytes.Read16(0x6ED06);

        var passes = new List<PassBuilder> { new(0) };
        var handedOver = false;
        if (recognised)
        {
            machine.OnInstruction(SelfTestPassed, _ => passes[^1].SelfTestPassed = true);
            machine.OnInstruction(RamTestWord, m => passes[^1].RamTestWord ??= m.Cpu.R(15));
            machine.OnInstruction(IdCheck, m => passes[^1].IdCheck ??= (byte)m.Cpu.R(4));
        }
        machine.OnInstruction(target, m =>
        {
            passes[^1].HandoverCycle ??= m.Cycles;
            handedOver = true;
        });

        var resets = 0;
        machine.Run(options.MaxCycles, m =>
        {
            var now = m.WatchdogResets + m.SoftwareResets;
            if (now != resets)
            {
                resets = now;
                passes.Add(new PassBuilder(m.Cycles));
            }
            return options.StopAtFirstHandover && handedOver;
        });

        var events = machine.Events.All.ToList();
        var outcome = resets >= 2 ? BootOutcome.ResetLoop
            : machine.Events.CountOf<TrapEvent>() > 0 ? BootOutcome.Trapped
            : handedOver ? BootOutcome.HandedOver
            : machine.Watchdog.ServiceCount > 0 ? BootOutcome.StayedInBoot
            : BootOutcome.NoProgress;

        return new BootResult(
            image, outcome, passes.Select(p => p.Build()).ToList(), target, recognised,
            machine.WatchdogResets, machine.SoftwareResets, machine.Watchdog.ServiceCount,
            events, new Dictionary<Type, long>(machine.Events.Counts), machine.Events.Dropped,
            machine.Inventory.Records, machine.Branches.Last(),
            machine.Cycles, machine.Instructions, machine.StateHash(),
            Assumptions(machineOptions, recognised));
    }

    static IReadOnlyList<string> Assumptions(MachineOptions options, bool recognised)
    {
        var list = new List<string>
        {
            $"fCPU = {options.CpuHz / 1e6:0.###} MHz [inf: the boot's T1 loop counts 375 ticks of fCPU/64 per ms; ASC0 S0BG = 4Dh gives 9,615 baud]",
            "chip: C167CR — no XPERCON, so F024h is plain storage reading 0000h at reset",
            $"external RAM: {options.ExternalRamBytes} bytes on CS2, mirrored inside a larger window",
            $"a chip-select window with no device reads {options.UnmodelledReadValue:X4}h [assumption]",
            "reset values of the pin-dependent registers: SYSCON = 0000h, BUSCON0 = 0680h [assumption]",
            "RAM powers up as 00h (for determinism)",
            "CAN module modelled as plain RAM",
            "ADC, ASC0, SSC, GPT1/2, CAPCOM compare/capture, PWM and ports are not modelled (plain SFR storage)",
            "instruction timing: ISM Table 11 plus bus states; interrupt, PEC and trap entries cost 4 states [assumption]",
        };
        if (!recognised)
            list.Add("boot block not recognised: checkpoints skipped (the MS43 boot-block bytes are not at 0x2E30/0x2FA2/0x2CE0)");
        return list;
    }

    static bool Matches(FirmwareImage image, int offset, params byte[] expected) =>
        image.Bytes.Slice(offset, expected.Length).SequenceEqual(expected);

    sealed class PassBuilder
    {
        public PassBuilder(ulong startCycle) => StartCycle = startCycle;

        public ulong StartCycle { get; }
        public bool SelfTestPassed { get; set; }
        public ushort? RamTestWord { get; set; }
        public byte? IdCheck { get; set; }
        public ulong? HandoverCycle { get; set; }

        public BootPass Build() => new(StartCycle, SelfTestPassed, RamTestWord, IdCheck, HandoverCycle);
    }
}
