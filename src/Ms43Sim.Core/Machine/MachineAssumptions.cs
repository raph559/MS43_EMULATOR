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
        "not modelled: the second CAN controller on CS4 (00'EE00, 16 KiB development units); its accesses go to the inventory",
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
        "the battery and KL15 dividers are ÷ 5.2 [inf: 26/255 V per count of FBD5/FBD6]",
        "every digital input not listed reads high (pull-ups); a P5 pin reads 1 above 2.5 V; P5DIDIS and PICON are only stored [assumption]",
        "P8.6 (the throttle shut-off feedback) reads low for the whole run, with no edges [call]",
        "throttle body: velocity model, Gain 400 °/s, friction 0.03, spring preload 0.10 and the two spring rates (0.0025 per degree above limp-home, 0.01 below), limp-home 4.90°, stops 0° and 88°, tracks V1 = 0.5078 + θ/20.914, V2 = 4.5117 − θ/20.914; the drive is P7.2's share of time high minus P6.5, while P6.7 enables; it steps every 0.5 ms [assumption, constants from the research sweeps]",
        "the output latch on CS3 is a write-only latch on D0-D7: a byte write to the odd address does not load it, and a read gives FFFFh and is reported; it powers up as 78h (every chip select inactive) and a CPU reset leaves it [assumption]",
        // SSC and its devices
        "MRST has a pull-up: it reads 1 while no selected device drives it [assumption]",
        "SSC: no start latency; phase and baud-rate errors are never raised; clearing SSCEN aborts a transfer without SSCRIR; a write to SSCTB while disabled is only stored; a write in programming mode initialises the SSC and forgets an unread SSCRB (UM p. 12-4: it \"will initialize the SSC\"); SSCBR reads give the reload value; the shift register powers up as FFFFh; the clock edges of a master transfer reach the slaves at its end [assumption]",
        options.EepromImage == null
            ? "EEPROM M93C86: starts blank, every word FFFFh [owner]"
            : "EEPROM M93C86: loaded from a 2,048-byte image",
        "EEPROM M93C86: write cycle tW = 5 ms (120,000 cycles); ready (1) or busy (0) on DO while its chip select is high and no instruction is clocked; the write enable is cleared at power-on; a READ continues into the next word; instructions clocked during a write cycle are ignored; a power-on during a write cycle abandons it [assumption]",
        "output-driver chip A: a 16-bit shift register (SPI mode 1) whose \"no fault\" answer is the complement of the command it latched at its previous transfer [obs]",
        $"output-driver chip B: a 32-bit shift register answering the board's status word {options.Inputs.ChipBStatus:X8}h, D0 first (11 = healthy per channel; FFFFFAFAh: the car's four lambda heaters are unplugged and read open, a = 0, b = 1 [inf]); it releases MISO until the monitoring module has sent its first ADh frame [inf]",
        // on-chip peripherals
        "GPT1/2: the prescalers run from power-on, so timer steps fall on multiples of 8 × 2^TxI (GPT1) or 4 × 2^TxI (GPT2) cycles; not modelled, each reported: T3 incremental interface mode, T2/T4 reload, capture and incremental interface modes, T2/T4 counting T3OTL, the reserved mode codes, and CT3 = 1; T3OUT and T6OUT do not drive their pins [assumption]",
        "CAPCOM: the prescalers run from power-on (steps fall on multiples of 2^(TxI + 3) cycles); a match is the timer stepping, reloading or being written by software to CCx [UM p. 15-15], never a software write of CCx [assumption]; a change of a channel's CCM field clears its once-per-period lock [inf: the coil and knock channels depend on it]; not modelled, each reported: the double-register compare mode and the reserved counter inputs TxI = 1xx [assumption]",
        "ADC: fixed-channel single conversion only; the channel is sampled when the conversion starts; not modelled, each reported once: ADM ≠ 00, ADWR and ADCIN (the conversion then runs as a single one); ADDAT2 is plain storage and ADCRQ from CC31 is ignored",
        "PWM: mode 0 (edge aligned) only; PIRx is set when PTx restarts at 0000h, not when the timer starts; a counter started above its period runs to FFFFh first; not modelled, each reported once: mode 1, burst and single shot; the outputs are given as each pin's share of the period, not as edges; a PWx or PPx write takes effect at the next restart (the shadow), and the capture records the write's time [assumption]",
        "ASC0: asynchronous modes only; transmission starts without the divide-by-16 latency; received frames are clean (no framing or parity errors); a loopback frame arrives at the end of its stop bits; not modelled, each reported once: synchronous mode and the reserved modes [assumption]",
        "CAN: every transmitted frame is acknowledged and no other node sends [stub: CAN partners]; a frame lasts (44 + 8·DLC + 3) bit times, no stuff bits, extended frames alike; object 15 matches under the global mask only; not modelled: remote frames (each request reported), error counters, bus-off and the last-message mask [assumption]",
        "monitoring module: modelled from the firmware's protocol (12h, CFh, ADh frames; expected answers from the firmware's own routines); a frame whose error counter is not zero asks the previous question again [inf]; its own reaction to wrong answers is reported, not modelled; the bytes it leaves unused are 00h; first clock edge 20 µs after its chip select falls, 250 kbit/s, 4 µs between bytes; an early chip-select rise aborts the exchange [assumption]",
    };

    /// <summary>
    /// The engine's plants, which exist only while an engine is attached: a run adds these lines
    /// then. Tasks that model an engine plant add their own line here.
    /// </summary>
    public static IReadOnlyList<string> ForEngine() => new List<string>
    {
        "VANOS: x' = 400 °CRK/s × (drive − hold) above 500 rpm, −200 °CRK/s below (no oil pressure); ranges 40.1 and 25.1 °CRK; hold duties from the scenario; a PWM width written while the channel runs counts from the first 0.5 ms step after the restart that loads it [assumption]",
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
            : ReferenceEquals(inputs, BoardInputs.WarmIdle)
                ? "warm idle (coolant and oil 90 °C, intake air 33 °C, 101.3 kPa, 12.6 V with the key on, pedal released, lambda sensors and heaters unplugged; plan 3, Reference facts) [call]"
                : "as given";
        return $"inputs: {name}: AN0-AN15 = {Volts(inputs.AnalogVolts)} V; mux A (AN13) = {Volts(inputs.MuxAVolts)} V; " +
               $"mux B (AN4) = {Volts(inputs.MuxBVolts)} V; low: {string.Join(", ", low)}" +
               (high.Any() ? $"; high: {string.Join(", ", high)}" : "");
    }
}
