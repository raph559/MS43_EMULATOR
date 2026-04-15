using System.Globalization;

namespace Ms43Sim.Core;

/// <summary>
/// Every simplification a machine runs under, one line each, for the run reports. Tasks that
/// model a peripheral take it off the "not modelled yet" line and add their own lines.
/// </summary>
public static class MachineAssumptions
{
    public static IReadOnlyList<string> For(MachineOptions options) => new List<string>
    {
        // clock and chip
        $"fCPU = {options.CpuHz / 1e6:0.###} MHz [inf: the boot's T1 loop counts 375 ticks of fCPU/64 per ms; ASC0 S0BG = 4Dh gives 9,615 baud]",
        "chip: C167CR — no XPERCON, so F024h is plain storage reading 0000h at reset",
        // memory map and devices
        $"external RAM: {options.ExternalRamBytes} bytes on CS2, mirrored inside a larger window",
        $"a chip-select window with no device reads {options.UnmodelledReadValue:X4}h [assumption]",
        "the reserved area 00'F200-00'F5FF reads 0000h and ignores writes [assumption]",
        "flash writes are recorded and ignored: the AM29F400 command set (program, erase, status reads) is not modelled",
        "not modelled yet (plain SFR storage): ADC, PWM, ASC0; the CAN module is plain RAM",
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
        "class A traps wait for the end of an ATOMIC/EXT* range and a class B trap ends it; the rest of the range then runs without the lock or the override (UM §21.8, ISM p. 39)",
        "ATOMIC/EXT* still lock and override while a class B trap flag is pending (UM §21.8: they then do not work)",
        "an ATOMIC or EXT* inside a running range reloads the single counter (ISM p. 39) and replaces the override, so an ATOMIC inside an EXTP range ends the page override [assumption: the ISM does not say; the car's code nests only ATOMIC → EXTP/EXTR and EXTR → EXTR]",
        "a faulting instruction's flag updates are undone [assumption]",
        "stack-limit traps push the IP of the instruction after the one that moved SP (UM). Every SP write is checked, MOV SP included (UM: only pushes and subtractions for STKOV, pops and additions for STKUN), and SP, STKOV, STKUN and CP keep bits 15:12 as written (UM: tied to 1)",
        "SYSCON.STKSZ is not modelled: the system stack never wraps",
        "IDLE: a PEC request is transferred while the CPU stays idle (UM §20.1); an IDLE inside an ATOMIC/EXT* range serves no PEC and wakes instead (UM §21.8), and a PEC transfer that raises ILLOPA during IDLE ends it in the trap routine [assumption]",
        // timing
        "instruction timing: ISM Table 11 plus bus states; interrupt, PEC and trap entries cost 4 states [assumption]",
        "bus states: ACT = 2 (3 on a multiplexed bus) + (15 - MCTC) + (1 - MTTC) per access, RWDC and ALECTL add nothing [assumption];XRAM and the CAN module are external accesses of 2 states (no waitstates) [assumption]",
        "not modelled: the second-order timing effects of ISM §7.3, and pipeline effects (CP/SP write latency, the interrupt acceptance delay after IEN/xxIC writes, the stall after a PSW write)",
        // board inputs and pins
        Inputs(options.Inputs),
        "ADC codes: VAREF = 5.0 V and an ideal converter, code = floor(V / 5.0 × 1024 + 0.5), clamped to 0…1023 [assumption]",
        "every digital input not listed reads high (pull-ups); a P5 pin reads 1 above 2.5 V; P5DIDIS and PICON are only stored [assumption]",
        "P8.6 (the throttle shut-off feedback) reads low for the whole run, with no edges [call]",
        "the output latch on CS3 is a write-only latch on D0-D7: a byte write to the odd address does not load it, and a read gives FFFFh and is reported; it powers up as 78h (every chip select inactive) and a CPU reset leaves it [assumption]",
        // SSC and its devices
        "MRST has a pull-up: it reads 1 while no selected device drives it [assumption]",
        "SSC: no start latency; phase and baud-rate errors are never raised; clearing SSCEN aborts a transfer without SSCRIR; a write to SSCTB while disabled is only stored; SSCBR reads give the reload value; the shift register powers up as FFFFh; the clock edges of a master transfer reach the slaves at its end [assumption]",
        options.EepromImage == null
            ? "EEPROM M93C86: starts blank, every word FFFFh [owner]"
            : "EEPROM M93C86: loaded from a 2,048-byte image",
        "EEPROM M93C86: write cycle tW = 5 ms (120,000 cycles); ready (1) or busy (0) on DO while its chip select is high and no instruction is clocked; the write enable is cleared at power-on; a READ continues into the next word; instructions clocked during a write cycle are ignored; a power-on during a write cycle abandons it [assumption]",
        "output-driver chip A: a 16-bit shift register (SPI mode 1) whose \"no fault\" answer is the complement of the command it latched at its previous transfer [obs]",
        "output-driver chip B: a 32-bit shift register answering FFFF FFFFh (no fault); it releases MISO until the monitoring module has sent its first ADh frame [inf]",
        // on-chip peripherals
        "GPT1/2: the prescalers run from power-on, so timer steps fall on multiples of 8 × 2^TxI (GPT1) or 4 × 2^TxI (GPT2) cycles; not modelled, each reported: T3 incremental interface mode, T2/T4 reload, capture and incremental interface modes, T2/T4 counting T3OTL, the reserved mode codes, and CT3 = 1; T3OUT and T6OUT do not drive their pins [assumption]",
        "CAPCOM: the prescalers run from power-on (steps fall on multiples of 2^(TxI + 3) cycles); a match is the timer stepping or reloading to CCx, never a software write of the timer or of CCx; not modelled, each reported: the double-register compare mode and the reserved counter inputs TxI = 1xx [assumption]",
        "monitoring module: modelled from the firmware's protocol (12h, CFh, ADh frames; expected answers from the firmware's own routines); its own reaction to wrong answers is reported, not modelled; the bytes it leaves unused are 00h; first clock edge 20 µs after its chip select falls, 250 kbit/s, 4 µs between bytes; an early chip-select rise aborts the exchange [assumption]",
    };

    static string Inputs(BoardInputs inputs)
    {
        static string Volts(IEnumerable<double> volts) =>
            string.Join(" ", volts.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
        var low = inputs.DigitalInputs.Where(p => !p.Value).Select(p => p.Key).Order()
            .Select(p => $"P{p.Port}.{p.Bit}");
        var high = inputs.DigitalInputs.Where(p => p.Value).Select(p => p.Key).Order()
            .Select(p => $"P{p.Port}.{p.Bit}");
        var name = ReferenceEquals(inputs, BoardInputs.KeyOnEngineStopped)
            ? "key on, engine stopped (12.6 V, 20 °C, pedal released, throttle at limp-home, lambda sensors unplugged) [call]"
            : "as given";
        return $"inputs: {name}: AN0-AN15 = {Volts(inputs.AnalogVolts)} V; mux A (AN13) = {Volts(inputs.MuxAVolts)} V; " +
               $"mux B (AN4) = {Volts(inputs.MuxBVolts)} V; low: {string.Join(", ", low)}" +
               (high.Any() ? $"; high: {string.Join(", ", high)}" : "");
    }
}
