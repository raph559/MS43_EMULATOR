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
        "ports: a pin's direction change is a set-up, never a signal edge: the output capture records it as PinDirection, and the engine, the cylinder events and the checks take only edges of pins that are outputs before and after; the capture keeps a PWM share or chip A command equal to the last one kept only as a count [plan 4 R1]",
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
        "PWM: mode 0 (edge aligned) only; PIRx is set when PTx restarts at 0000h, not when the timer starts; a counter started above its period runs to FFFFh first; not modelled, each reported once: mode 1, burst and single shot; the outputs are given as each pin's share of the period, not as edges; a PWx or PPx write takes effect at the next restart (the shadow), where the capture and the plants see it [assumption]",
        "ASC0: asynchronous modes only; transmission starts without the divide-by-16 latency; received frames are clean (no framing or parity errors); a loopback frame arrives at the end of its stop bits; not modelled, each reported once: synchronous mode and the reserved modes [assumption]",
        "K-line: one wire joins ASC0 and a tester with no transceiver delay; a byte from either end reaches both at the end of its stop bit, and ASC0 hears its own frames back, taking any frame only if its receiver was on at the frame's start bit (UM §11.1.2) [obs]; a tester byte is 8E1, and one whose bit time is more than 3 % from ASC0's is lost, as are bytes that overlap in time, whichever began first (a real UART would receive garbage and raise errors: not exercised); ECU bytes reach the tester whatever its baud rate; the echo is not combined with S0LB, which neither firmware set-up uses (S0CON = 80F7h, 80D1h): a loopback frame would arrive twice [assumption]",
        "CAN: every transmitted frame is acknowledged; the other nodes are the CAN partners stub; a frame lasts (44 + 8·DLC + 3) bit times, no stuff bits, extended frames alike; object 15 matches under the global mask only; not modelled: remote frames (each request reported), error counters, bus-off and the last-message mask [assumption]",
        "monitoring module: modelled from the firmware's protocol (12h, CFh, ADh frames; expected answers from the firmware's own routines); a frame whose error counter is not zero asks the previous question again [inf]; its own reaction to wrong answers is reported, not modelled; the bytes it leaves unused are 00h; first clock edge 20 µs after its chip select falls, 250 kbit/s, 4 µs between bytes; an early chip-select rise aborts the exchange [assumption]",
    };

    /// <summary>
    /// The engine's laws, which exist only while an engine is attached: a run adds these lines
    /// then (plan 3, Reference facts; the constants are in the classes' summaries).
    /// </summary>
    public static IReadOnlyList<string> ForEngine(EngineScenario scenario)
    {
        static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            scenario.CrankAtSeconds is { } crank
                ? $"engine scenario {scenario.Name}: the starter turns the engine at {N(scenario.StarterRpm)} rpm (reached in 0.2 s) for {N(scenario.StarterSeconds)} s from {N(crank)} s; the shaft starts at {N(scenario.StartAngle)}° [call]"
                : $"engine scenario {scenario.Name}: the starter turns the engine when the drive cranks, up to the drive's speed in 0.2 s; the shaft starts at {N(scenario.StartAngle)}° [call]",
            "combustion: a spark (a falling edge of an output coil pin P2.6–P2.11 after at least 0.5 ms high; an ignition cut's shorter pulses store no energy) and a rising edge (a closing) of an output injector pin P2.0–P2.5, each within the last 0.25 s; only edges of pins that are outputs before and after count; the engine runs on when the starter releases while combusting; without combustion it coasts down (a 0.5 s lag) and stops below 30 rpm; combustion at 300 rpm or more makes it run again [assumption]",
            "idle-air plant, every 0.5 ms: the valve opening d is the share of time P2.12 is low; Qcmd = min(50 kg/h × d, 480 mg/stroke at the speed); Q follows Qcmd with a 50 ms lag; running, the speed follows Q × 5555.6 / 105 mg/stroke with a 0.3 s lag; no torque model, throttle air not modelled [assumption, constants from the research's verified plant]",
            $"battery: {N(scenario.BatteryOff)} V with the engine stopped, {N(scenario.BatteryStarter)} V while the starter turns, {N(scenario.BatteryRunning)} V running (the alternator); AN5, and AN1 while the key is on, read it ÷ 5.2 [assumption]",
            "MAF (AN0): the car's id_maf_tab__v_maf (0x70EC6) frozen as the sensor's law, linear between its points [obs; assumption below 0.10 V]; knock envelope (AN15): (c(n) + 0.5) × 5 / 256 V, c(n) from the car's ip_nl_sp__n (0x77CBB), 0 V below 50 rpm [obs, ver]; coil current (AN14): 2.7 V while the shaft turns (≥ 50 rpm), 0 V stopped [assumption]; burn feedback (P3.6): high for 1.2 ms after each spark (at least 0.5 ms of dwell), retriggered by a later one [assumption]",
            $"VANOS: x' = 400 °CRK/s × (drive − hold) above 500 rpm, −200 °CRK/s below (no oil pressure); ranges 40.1 and 25.1 °CRK; hold duties {N(scenario.HoldIntake)} (intake) and {N(scenario.HoldExhaust)} (exhaust) from the scenario; a CPU reset is seen only at the next pin or PWM change [assumption]",
        };
        if (scenario.Law is not null)
            lines.Add($"breathing law: the airflow is the firmware's own air model, the car's ip_maf_1_diag__n__tps_av (0x7AAEE) frozen as M(n, θ), ρ = p_amb / 95.5 kPa × f_air(intake) × f_cool(coolant) at {N(scenario.AmbientKpa)} kPa; Q_cmd = min(50 kg/h × d × ψ(pr) + the throttle's air above closed, max(the full-load ceiling, 50 kg/h × d)) with a 50 ms lag, in place of the idle-air plant's Qcmd [obs; ver as law 4]");
        return lines;
    }

    /// <summary>The car's inputs, and the K-line tester's session, while a drive runs it (<see cref="CarControls"/>, <see cref="KLineTester"/>; plan 4 R9, R10; plan 5 C4).</summary>
    public static IReadOnlyList<string> ForDrive() => new List<string>
    {
        "car temperatures: coolant on mux A 3, intake air on mux A 1 and oil on mux A 2 through the car's sensor curves, linear between the points [ver]; the radiator outlet on mux A 0 reads the ambient + 0.9 × (coolant − ambient), plan 3's 1.20 V at 90 °C and 20 °C [assumption]",
        "car controls: pedal AN10 = 0.75 + 3.65 p V and AN6 = AN10 / 2 [obs]; clutch on mux B 2, 4.5 V pressed and 0 V released [obs]; brakes: P3.1 and P3.5 both high when pressed [obs]; a battery override reads on AN5, and on AN1 while the key is on [assumption]",
        "road speed: P7.6 falls 6.971 times a second per km/h (25,096 a km), low for half a period after each fall; a new speed changes the rate without restarting the period [obs; assumption: only the falls count]; CAN 153h carries round(16 × km/h) in bytes 1–2 [obs]",
        "steering wheel: the \"no button\" frame train on P7.4 every 50 ms while the key is on, FEh and FFh alternating every 1.0 s; the line idles low and stays low with the key off [obs; assumption]",
        "cluster: 613h bytes 3–4 the clock in minutes; 615h byte 0 C0h with the A/C on, byte 3 the ambient in sign-magnitude °C; their other bytes are plan 3's [obs]",
        "fault injection: a forced channel reads its volts instead of its sensor until the force is lifted; a silenced partner sends nothing while it is silenced [call]",
        "K-line tester (a drive's kline lines; plan 5 C4): the app's DS2 session frame for frame, one request at a time, its bytes back to back at 9,600 or 125,000 baud, each when the echo of the one before returns; the next request leaves 20 ms after a reply, the app's pause after each sample [obs] (after a connect the app polls at once: the 20 ms stand for its serial cable's latency there [assumption]), 1 ms after a timeout or a malformed answer, the app sending again at once [assumption], or at once after a longer idle; it listens 500 ms for a 0x91 command and 400 ms for any other, from the end of the request [obs: the logging ADX's listen timeouts]; the reply is the first whole frame from the ECU after the request (whole when its length byte says so), malformed if its address is not 12h or its length or xor is wrong; connect and disconnect move the tester's baud only on an A0 reply; an every is due as it is told, then at each multiple of its period, and leaves at the first free turn [call]",
        "engine speed: in a drive segment the drive sets the shaft's speed (a dyno, no torque model), so the engine turns through any cut and the ECU's cuts show as missing sparks or injections; the starter, the idle plant and the combustion rule carry the crank and idle segments [owner]; a stall segment starves the engine, nothing burning whatever the ECU does, so it runs down with the key on until a crank feeds it again [call]",
        "CPU load at speed: the instruction timing is the ISM's minimum for external execution (Table 11 + 1 ACT per instruction word, ACT = 2 states at BUSCON0 = 45BFh: ISM §7.2), so a real ECU is at least as loaded; under it the car's periodic tasks stretch from about 2,500 rpm, its lowest queue (Q3) went up to 4 s without running at 5,000 rpm in a full-load pull, and the pull set the queue-overflow flag FD84.7; the scheduler check therefore judges R2 and R4 only below 2,000 rpm (R1 and R3 at every speed) and reports the time above; whether the real ECU is as loaded is unknown (a car probe, declined, would settle it) [assumption: owner decision 3; plan 4 Task 11]",
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
