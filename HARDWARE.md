# MS43 hardware as the emulator models it

One entry per device: what it is, the evidence, and what is modelled or stubbed (design §5.3).

"UM" is the C167CR Derivatives User's Manual V3.1; "ISM" is the C166 Family Instruction Set
Manual V2.0 (local copies in `refs/`). Code addresses are CPU addresses (`SS:OOOO`) in the car
file. Status as of plan 1 (2026-10-20).

| Device | Evidence | Model |
|---|---|---|
| **CPU (C167CR)** | REPORT.md §1; ISM; UM ch. 4-5 | Full instruction set, flags per ISM ch. 5, traps (class A/B, TFR), interrupt arbitration, PEC. Timing: ISM Table 11 plus bus states. MUL/DIV complete in one step (not interruptible) |
| **SFR / ESFR** | UM Table 22-4 (`src/Ms43Sim.Core/Bus/SfrTable.g.cs`) | Registers owned by a model go through it. Every other SFR is plain storage and each access goes into the inventory |
| **Flash (AM29F400, CS0)** | REPORT.md §2; BUSCON0 = 45BFh written at 00:2D5A | 512 KiB, mirrored every 512 KiB. Writes are recorded and ignored: no command state machine |
| **External RAM (CS2)** | ADDRSEL2 = 0401h at 00:2D4E; 8 KiB on the car (REPORT.md §9.5) | 8 KiB, mirrored in a larger window: the boot's 16 KiB test fails, as on the car |
| **IRAM** | UM §3 | 00'F600-00'FDFF, holds the register banks and the system stack |
| **XRAM** | UM §3; SYSCON.XPEN set by the boot | 00'E000-00'E7FF when XPEN = 1 |
| **CAN module** | UM ch. 18; the boot's RAM test at 00:2F12 | **Stub:** 00'EF00-00'EFFF is plain RAM, enough for the boot's message-RAM test. No CAN behaviour |
| **Watchdog** | UM ch. 13; WDTCON = 9201h at 00:2E64 | Full: fCPU/2 or /128, WDTREL, SRVWDT, DISWDT rules, reset with WDTR |
| **CAPCOM T0/T1** | UM §15.1; T01CON = 4300h at 00:1FF2 | Timer mode, reload, interrupt request. Counter mode is reported, not modelled. Compare/capture registers are not modelled yet |
| **CS1 window (1 MiB at 0x100000)** | ADDRSEL1 = 1008h at 00:2D4A | No device. Reads FFFFh, accesses inventoried |
| **CS3 window (4 KiB at 0x080000)** | ADDRSEL3 = 0800h at 00:2D52; the boot writes 0078h to 0x080000 at 00:2DA0 | No device. Reads FFFFh, accesses inventoried |
| **CS4 window (4 KiB at 0x00E000)** | ADDRSEL4 = 00E0h, BUSCON4 = 0648h (boot 00:2D7E, program 0E:367C) | XRAM and CAN take priority; the gap 00'E800-00'EEFF has no device |
| **SSC + M93C86 EEPROM** | EEPROM driver at 0D:F61x-F6D2 (REPORT.md §9.5) | **Not modelled.** The car's start-up stops at 0B:D80C (`JB FDE8h.11, $`) waiting for it. Plan 2 |
| **ADC, ASC0, GPT1/2, PWM, ports** | SFR accesses in `inventory.csv` | **Not modelled** (plain SFR storage). Plan 2 |
