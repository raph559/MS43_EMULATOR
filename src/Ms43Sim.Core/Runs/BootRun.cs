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
/// ID-check result. The handover target is CSP:IP from the words at 0x6ED08:0x6ED06. A target
/// inside the boot block (00:0000-00:7FFF) is not watched: the boot's own code runs there, so
/// the hook would fire without a handover (a zeroed pointer names the reset vector).
/// </summary>
public static class BootRun
{
    const uint SelfTestPassed = 0x002E30, RamTestWord = 0x002FA2, IdCheck = 0x002CE0;
    const uint BootBlockEnd = 0x008000;

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
        var targetInBoot = target < BootBlockEnd;
        if (!targetInBoot)
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
            Assumptions(machineOptions, recognised, targetInBoot ? target : null));
    }

    static IReadOnlyList<string> Assumptions(MachineOptions options, bool recognised, uint? targetInBoot)
    {
        var list = new List<string>
        {
            // clock and chip
            $"fCPU = {options.CpuHz / 1e6:0.###} MHz [inf: the boot's T1 loop counts 375 ticks of fCPU/64 per ms; ASC0 S0BG = 4Dh gives 9,615 baud]",
            "chip: C167CR — no XPERCON, so F024h is plain storage reading 0000h at reset",
            // memory map and devices
            $"external RAM: {options.ExternalRamBytes} bytes on CS2, mirrored inside a larger window",
            $"a chip-select window with no device reads {options.UnmodelledReadValue:X4}h [assumption]",
            "the reserved area 00'F200-00'F5FF reads 0000h and ignores writes [assumption]",
            "flash writes are recorded and ignored: the AM29F400 command set (program, erase, status reads) is not modelled",
            "CAN module modelled as plain RAM",
            "ADC, ASC0, SSC, GPT1/2, CAPCOM compare/capture, PWM and ports are not modelled (plain SFR storage)",
            "no NMI source and no ILLBUS trap: the NMI pin and bus errors are not modelled",
            // reset state
            "reset values of the pin-dependent registers: SYSCON = 0000h, BUSCON0 = 0680h [assumption]",
            "other reset-value digits given as X or U in the UM (pin-dependent or undefined) are 0, e.g. in RP0H [assumption]",
            "RAM powers up as 00h (for determinism)",
            "WDTCON reset flags differ from UM Table 13-2: power-on sets none (UM: LHWR, SHWR, SWR), a watchdog reset only WDTR (UM: also SWR), and SRST clears WDTR (UM: leaves it). EINIT and SRVWDT follow the table. The boot reads WDTR at 00:2E5C",
            "SYSCON cannot be written after EINIT (UM); BUSCONx and ADDRSELx still can (the program rewrites BUSCON4) [assumption]",
            // CPU
            "MUL/DIV are not interruptible: MULIP stays 0 and a pending interrupt waits for the instruction to end (the ISM says they are interruptible) [assumption]",
            "division: on V (a zero divisor, or a DIVL/DIVLU quotient too large for a word) MDH and MDL keep their old values; signed division truncates toward zero and the remainder takes the dividend's sign; DIV -32768/-1 gives MDL = 8000h without V (the ISM sets V only for a zero divisor) [assumption]",
            "class A traps wait for the end of an ATOMIC/EXT* range and a class B trap ends it (UM §21.8); the rest of the range then runs without the lock or the override [assumption]",
            "ATOMIC/EXT* still lock and override while a class B trap flag is pending (UM §21.8: they then do not work)",
            "an ATOMIC or EXT* inside a running range replaces it, so an ATOMIC inside an EXTP range ends the page override [assumption: the ISM is ambiguous]",
            "a faulting instruction's flag updates are undone [assumption]",
            "stack-limit traps push the IP of the instruction after the one that moved SP (UM). Every SP write is checked, MOV SP included (UM: only pushes and subtractions for STKOV, pops and additions for STKUN), and SP, STKOV, STKUN and CP keep bits 15:12 as written (UM: tied to 1)",
            "SYSCON.STKSZ is not modelled: the system stack never wraps",
            // timing
            "instruction timing: ISM Table 11 plus bus states; interrupt, PEC and trap entries cost 4 states [assumption]",
            "bus states: ACT = 2 (3 on a multiplexed bus) + (15 - MCTC) + (1 - MTTC) per access, RWDC and ALECTL add nothing [assumption];XRAM and the CAN module are external accesses of 2 states (no waitstates) [assumption]",
            "not modelled: the second-order timing effects of ISM §7.3, and pipeline effects (CP/SP write latency, the interrupt acceptance delay after IEN/xxIC writes, the stall after a PSW write)",
        };
        if (!recognised)
            list.Add("boot block not recognised: checkpoints skipped (the MS43 boot-block bytes are not at 0x2E30/0x2FA2/0x2CE0)");
        if (targetInBoot is { } t)
            list.Add($"handover target {BootReportWriter.Cpu(t)} lies inside the boot block (00:0000-00:7FFF), where the boot's own code runs: it is not watched, so no handover is reported");
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
