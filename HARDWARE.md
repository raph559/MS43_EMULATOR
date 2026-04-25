# MS43 hardware as the emulator models it

One entry per device: what it is, the evidence, and what is modelled or stubbed (design §5.3).

"UM" is the C167CR Derivatives User's Manual V3.1; "ISM" is the C166 Family Instruction Set
Manual V2.0 (local copies in `refs/`). Code addresses are CPU addresses (`SS:OOOO`) in the car
file. Status as of plan 2 (2026-10-20). "Stub" names the side of design §7's line: whether the
stub sits between the CPU and an output a check reads.

## On-chip

| Device | Evidence | Model |
|---|---|---|
| **CPU (C167CR)** | REPORT.md §1; ISM; UM ch. 4-5 | Full instruction set, flags per ISM ch. 5, traps (class A/B, TFR), interrupt arbitration, PEC (the request is acknowledged before the data moves). IDLE serves PEC requests without waking (UM §20.1). Timing: ISM Table 11 plus bus states. MUL/DIV complete in one step (not interruptible) |
| **SFR / ESFR** | UM Table 22-4 (`src/Ms43Sim.Core/Bus/SfrTable.g.cs`) | Registers owned by a model go through it. Every other SFR is plain storage and each access goes into the inventory. On the car only F024h (XPERCON, absent on the C167CR) is left |
| **IRAM, XRAM** | UM §3; SYSCON.XPEN set by the boot | IRAM 00'F600-00'FDFF (register banks, system stack); XRAM 00'E000-00'E7FF while XPEN = 1 |
| **Watchdog** | UM ch. 13; WDTCON = 9201h at 00:2E64, EA01h at 0E:0140 | Full: fCPU/2 or /128, WDTREL, SRVWDT, DISWDT rules, reset with WDTR |
| **Ports P2-P8, P5** | UM ch. 7; P3.8 read as the EEPROM ready line at 0E:B2C2 | Latch, direction, open drain; a read gives the pin for an input and the latch for an output, so a read-modify-write copies input pins. P5 input-only; P5DIDIS and PICON stored. Pin changes drive captures and gates. CAPCOM compare outputs change the latch; PWM, SSC, ASC0 and T3OUT/T6OUT do not drive pins |
| **SSC** | UM ch. 12; jobs 0-4 dispatched by XP0 (0E:B076), Reference facts of plan 2 | Master and slave, programming and operating views of SSCCON, continuous transfers, receive and transmit errors. A master character ends as one scheduled event that plays the clock edges to the selected slaves. Slave characters come from an external master (the monitoring module). No start latency, no phase or baud-rate errors |
| **GPT1 (T2-T4), GPT2 (T5, T6, CAPREL)** | UM ch. 10; T4 is the 1 ms tick (`ADD T4,#0BB8h` at 0E:B590), T3/T5/T6 set up at key-on | Timer, gated and counter modes, up/down with TxEUD, over/underflow with TxOTL, T6 reload from CAPREL, T5 counting T6OTL, CAPIN capture with T5CLR. Reported: T3 incremental mode, T2/T4 reload, capture and concatenation, CT3, reserved modes |
| **CAPCOM (T0/T1, T7/T8, CC0-CC31)** | UM ch. 15; CC12/CC23 mode 1, CC16 mode 2, CC22/28/30 captures at key-on | Timer and counter modes (T6 over/underflows, T0IN/T7IN edges), every capture and compare mode, a match when the timer steps or reloads to CCx. Reported: double-register compare mode |
| **ADC** | UM ch. 17; start-up scan at 0D:E272, the ADC handler at 0E:5744 | Fixed-channel single conversion with its conversion time from ADCTC/ADSTC; the channel is sampled when the conversion starts. Reported: auto-scan, continuous, wait-for-read and injection modes |
| **PWM** | UM ch. 16; set up at 0D:3BF8 | Mode 0 (edge aligned), shadow registers, PIRx and PWMIR. Reported: mode 1, burst, single shot. Outputs not driven onto P7 |
| **ASC0** | UM ch. 11; DS2 (80F7h, S0BG 4Dh) and KWP (80D1h) set-ups | Asynchronous modes, frame lengths, parity generation, S0TBIR/S0TIR/S0RIR timing, overrun, loopback. Reported: synchronous mode. Received frames are clean |
| **CAN module (00'EF00-00'EFFF)** | UM ch. 18; set up at 0D:437C (CSR 0741h, BTR 3601h); the boot's message-RAM test at 00:2F12 | CSR, IR, BTR (with CCE), masks, the 2-bit MCR elements, transmission (lowest object first, 44 + 8·DLC + 3 bit times), reception into the matching object, the XP0 line. Not modelled: remote frames (reported), error counters, bus-off, the last-message mask. **Stub: CAN partners** — silent, every frame acknowledged; not between the CPU and a checked output |

## The board

| Device | Evidence | Model |
|---|---|---|
| **Flash (AM29F400, CS0)** | REPORT.md §2; BUSCON0 = 45BFh at 00:2D5A | 512 KiB, mirrored every 512 KiB. Writes are recorded and ignored: no command state machine |
| **External RAM (CS2)** | ADDRSEL2 = 0401h at 00:2D4E; 8 KiB on the car (REPORT.md §9.5) | 8 KiB, mirrored in a larger window: the boot's 16 KiB test fails, as on the car |
| **Output latch (CS3, 0x080000)** | 47 word writes with `EXTP #020h` from FDCA, no reads; 0078h at 00:2DA0 and 0D:F52A | Write-only latch on D0-D7: bits 0-2 the multiplexer select, 3 chip B CS, 4 chip A CS, 5 main-relay hold, 6 monitoring-module CS, 7 EEPROM CS. Powers up as 78h. A read gives FFFFh and is reported |
| **Analog inputs and multiplexers** | AN0-AN15 as the start-up scan and the ADC handler read them; mux A on AN13, mux B on AN4 (latch bits 0-2) | The key-on input set of plan 2 (12.6 V, 20 °C, pedal released, throttle at limp-home, lambda sensors unplugged); VAREF 5.0 V, ideal converter |
| **Digital inputs** | P3.1/P3.5 brake switches; P8.6 the throttle shut-off feedback (CC22) | Listed inputs at their level (P3.1, P3.5, P8.6 low); every other input reads high (pull-ups) |
| **M93C86 EEPROM (SSC job 4)** | Driver 0D:F616-F6D2, READ 0D:F63A; block loads under 0B:0418; after-run writes at 0D:82DE | x16 Microwire: READ, WRITE, ERASE, EWEN, EWDS, ERAL, WRAL; ready/busy on DO; write cycle 5 ms. Starts blank (owner) or from a 2,048-byte image. **Stub side:** not between the CPU and a checked output |
| **Output-driver chip A (SSC jobs 0-1)** | Job 0 at 0E:B116, result check FC1D = A6h | 16-bit shift register, SPI mode 1; "no fault" = the complement of its previous command. **Stub:** always no fault, outputs not captured; between the CPU and a checked output |
| **Output-driver chip B (SSC job 2)** | Job 2, result check FC22 = 96h (FD82.8) | 32-bit shift register answering FFFF FFFFh once the monitoring module has sent its first ADh frame. **Stub:** always no fault; between the CPU and a checked output |
| **Monitoring module (SSC job 3)** | Task 24h (0E:09D2), frames and answer routines of plan 2's Reference facts | SPI master, 9-byte frames (12h, CFh, ADh), questions, toggle byte, error counter, ADC 6 mirror; expected answers from the firmware's own routines. **Stub:** its shut-off path is not modelled, only reported; between the CPU and a checked output |
| **Throttle body** | E-gas start check; FD0C.10 | **Stub:** static at limp-home (both tracks fixed), so the throttle goes into limp mode; between the CPU and a checked output |
| **Knock chip, coil feedback** | AN14/AN15 | **Stub:** 0 V; not sampled at standstill |
| **K-line tester** | ASC0 DS2/KWP set-ups | **Stub:** none connected |
| **CS1 window (1 MiB at 0x100000)** | ADDRSEL1 = 1008h at 00:2D4A | No device. Reads FFFFh, accesses inventoried (none on the car) |
| **CS4 window, second CAN controller (00'EE00)** | ADDRSEL4 = 00E0h, BUSCON4 = 0648h; initialisation at 0E:1B6E is skipped on the car | Not modelled: it belongs to 16 KiB development units. XRAM and the on-chip CAN take priority; the rest of the window is inventoried |
