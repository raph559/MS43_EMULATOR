# sim — the MS43 bench emulator

A purpose-built emulator for the Siemens MS43 (Infineon C167CR). It runs an MS43 bin from
power-on the way the ECU does, so a bin can be tested before it is flashed.

- Design: `docs/superpowers/specs/2026-10-20-ms43-emulator-design.md`
- Plans: `docs/superpowers/plans/2026-10-20-ms43-emulator-plan1-cpu-boot.md` and
  `docs/superpowers/plans/2026-10-20-ms43-emulator-plan2-startup-peripherals.md`; their records
  (rulings, review findings, open items) beside them
- Devices and what is modelled: `HARDWARE.md`
- Results on the car file: REPORT.md §9.6 (boot) and §9.7 (start-up)

**Status:** plan 2 done (2026-10-20). Plan 1 gave the CPU, bus, traps, interrupts, PEC,
watchdog, timing and the boot runner. Plan 2 adds the ports, SSC, GPT1/2, CAPCOM, ADC, PWM, ASC0
and CAN, the board (output latch, M93C86 EEPROM, output-driver chips A and B, the monitoring
module, the key-on inputs), the start-up runner and one key-off.

The car file starts up with the key on and the engine stopped: its CRC and monitor-sum tasks
pass, the monitoring handshake completes, and its scheduler runs. A key-off runs the after-run,
writes the EEPROM and drops the main relay; the next key-on reads that EEPROM back.

## Run it

```bash
dotnet build -c Release sim/Ms43Sim.sln
dotnet sim/src/Ms43Sim.Cli/bin/Release/net10.0/ms43sim.dll boot ../../../bins/CAR_current_43X001_SP95_STAGE1.bin --out sim/runs/boot-car
```

`ms43sim run BIN` runs a bin with the key on, the engine stopped unless `--engine` names a scenario:

```bash
dotnet sim/src/Ms43Sim.Cli/bin/Release/net10.0/ms43sim.dll run ../../../bins/CAR_current_43X001_SP95_STAGE1.bin --seconds 5 --out sim/runs/run-car
```

| Option | Default | Effect |
|---|---|---|
| (none) | | Runs BIN as the ECU holds it after an MS4X Flasher **full write** |
| `--raw`, `--partial-onto ECU.bin`, `--ecu-boot BOOT.bin` | | As for `boot` (below) |
| `--seconds S` | 5 (60 with `--engine`) | How long to run, above 0 and at most 600 |
| `--key-off-at K` | | Turns the key off (AN1 to 0 V) at K seconds, 0 < K < S; the run stops when the main relay drops |
| `--eeprom FILE` | blank | The EEPROM's contents: 2,048 bytes, word *i* little-endian at byte 2*i*. The file is only read |
| `--engine NAME` | | Puts an engine around the ECU: `warm-idle` (warm engine, the car's ASC partners on CAN). The starter turns it at 3.0 s and it should run on its own from 4.0 s. Without it the engine stays stopped |
| `--out DIR` | `runs/run-<name>` | Where the five output files go: `run-report.md`, `inventory.csv`, `eeprom.bin` (the chip at the end of the run), `events.csv` (every output change, with the crank angle in an engine run) and `trace.csv` (the firmware's variables every 100 ms). An output that is also an input (an `--eeprom` file in that folder) is a usage error |

The outcome is the first that holds: `Trapped`, `ResetLoop` (any reset), `PoweredOff` (the main
relay released after the handover), `SchedulerRunning` (the main loop's SRVWDT at 0E:0144 in the
last 30 ms), `Stalled` (handed over, no scheduler), `StayedInBoot`, `NoProgress`. The report
gives the milestones; with an engine, the engine's phases and the inputs fed beside what the
ECU reads; the outputs (with an engine, per cylinder: injections, sparks and the ECU's own
decisions); the plants (throttle, VANOS, idle); the fault memory, with each detected path's
explanation or UNEXPLAINED; the CAN partners; scheduler health, checksum tasks, the monitoring
module, the firmware's RAM read-outs, CAN transmissions, resets, events, stubs, the unmodelled
hardware and the assumptions. An engine run starts the engine at 3.0 s and expects it to run from
4.0 s:

```bash
dotnet sim/src/Ms43Sim.Cli/bin/Release/net10.0/ms43sim.dll run ../../../bins/CAR_current_43X001_SP95_STAGE1.bin --engine warm-idle --out sim/runs/run-car-idle
```

`ms43sim boot BIN` takes these options:

| Option | Default | Effect |
|---|---|---|
| (none) | | Runs BIN as the ECU holds it after an MS4X Flasher **full write**: all checksums corrected, the ECU's boot block kept (design §3) |
| `--raw` | | Runs the file's bytes exactly as they are |
| `--partial-onto ECU.bin` | | Runs BIN as a **partial write** (calibration only) over ECU.bin |
| `--ecu-boot BOOT.bin` | | Takes the boot block 0x0000-0x7FFF from BOOT.bin |
| `--max-cycles N` | 30,000,000 (1.25 s at 24 MHz) | How long to run |
| `--stop-at-handover` | | Stops when the boot hands over to the program |
| `--out DIR` | `runs/boot-<name>` | Where `boot-report.md` and `inventory.csv` go |

Exit codes, both commands: 0 the run completed (any outcome), 2 usage error, 3 unreadable or
wrong-size image or EEPROM file.

`ms43sim decode --listing BIN` prints the decoder's linear sweep. Compare it with
`python -I sim/tools/decode_listing.py BIN`, which uses `tools/c167dis.py`.

## Build and test

Needs the .NET 10 SDK. The test packages (xUnit 2.5.3, Microsoft.NET.Test.Sdk 17.8.0) are the
versions already in the local NuGet cache, so a restore works offline.

```bash
dotnet test sim/Ms43Sim.sln
```

Run it from the repository root. 378 tests: instruction semantics from the manuals, the memory
map, interrupts, PEC, every peripheral and board device, the boot of the real bins, the car's
5 s start-up and a 40 s key-off (about 15 s in all).

**Speed:** 138.7 M emulated cycles per second on this PC (Release, a 60 s run of the car's
start-up), 5.8× real time. Plan 1's boot-only figure was 166 M.

## Assumptions and simplifications in plan 2

Every run report lists them (`MachineAssumptions.For`), with the run's own lines (a key-off,
unrecognised program checkpoints). Beyond plan 1's (below):

- **Inputs:** the key-on set (12.6 V, 20 °C, pedal released, throttle at limp-home, lambda
  sensors unplugged); VAREF 5.0 V and an ideal converter; unlisted digital inputs read high;
  P8.6 stays low; a key-off drops only AN1.
- **Board:** the CS3 latch is write-only on D0-D7 and powers up as 78h; MRST has a pull-up; the
  EEPROM starts blank (owner), its write cycle is 5 ms and DO shows ready/busy; chip A answers
  the complement of its previous command and chip B answers "no fault" once the module has sent
  its first ADh frame; the monitoring module's timing (20 µs, 250 kbit/s, 4 µs gaps) and unused
  bytes (00h) are assumptions, and its shut-off reaction is reported, not modelled.
- **Peripherals:** the GPT and CAPCOM prescalers run from power-on; SSC transfers start without
  latency and raise no phase or baud-rate errors; ADC fixed-channel single conversion only; PWM
  mode 0 only; ASC0 asynchronous modes only, clean reception; CAN frames last 44 + 8·DLC + 3 bit
  times and every frame is acknowledged. Each unmodelled mode raises an UnsupportedEvent.
- **Stubs** (design §7): CAN partners silent; chips A/B always "no fault"; the monitoring module's
  shut-off path; the throttle body static at limp-home; knock and coil feedback 0 V; no K-line
  tester. The report's Stubs section says which sit between the CPU and a checked output.

## Assumptions and simplifications from plan 1

Every boot and run report lists these too (`MachineAssumptions.For`). A boot report adds the
lines that apply to that run only: an unrecognised boot block, or a handover target inside the
boot block (00:0000-00:7FFF), which is not watched because the boot's own code runs there.

**Clock and chip**
- **fCPU = 24 MHz** [inf]. The boot counts 375 T1 ticks (fCPU/64) per ms, and ASC0 S0BG = 4Dh
  gives 9,615 baud.
- **No XPERCON.** The chip is a C167CR, so F024h is plain storage that reads 0000h at reset.

**Memory map and devices**
- External RAM: 8 KiB on CS2, mirrored inside a larger window.
- A chip-select window with no device reads FFFFh. The reserved area 00'F200-00'F5FF reads
  0000h and ignores writes.
- Flash writes are recorded and ignored: the AM29F400 command set is not modelled.
- The second CAN controller on CS4 (16 KiB development units) is not modelled.
- No NMI source and no ILLBUS trap.

**Reset state**
- Pin-dependent reset values: SYSCON = 0000h, BUSCON0 = 0680h (16-bit demux, 15 waitstates).
  The boot rewrites both. Other reset-value digits given as X or U in the UM are 0 (for
  example in RP0H).
- RAM powers up as 00h.
- The WDTCON reset flags differ from UM Table 13-2:
  - power-on sets none (UM: LHWR, SHWR, SWR);
  - a watchdog reset sets only WDTR (UM: also SWR);
  - SRST clears WDTR (UM: leaves it).

  EINIT and SRVWDT follow the table. The boot reads WDTR at 00:2E5C.
- SYSCON cannot be written after EINIT (UM). BUSCONx and ADDRSELx still can (the program
  rewrites BUSCON4).

**CPU**
- MUL/DIV are not interruptible (MULIP stays 0). The ISM says they are.
- Division:
  - on V (a zero divisor, or a DIVL/DIVLU quotient too large for a word), MDH and MDL keep
    their old values;
  - signed division truncates toward zero, and the remainder takes the dividend's sign;
  - DIV −32768/−1 gives MDL = 8000h without V (the ISM sets V only for a zero divisor).
- Traps:
  - class A traps wait for the end of an EXT*/ATOMIC range and a class B trap ends it; the
    rest of the range then runs without the lock or the override (UM §21.8, ISM p. 39);
  - EXT*/ATOMIC work even while a class B flag is pending (UM §21.8 says they do not);
  - an ATOMIC or EXT* inside a running range reloads the single counter (ISM p. 39) and
    replaces the override, so an ATOMIC inside an EXTP range ends the page override (the ISM
    does not say; the car's code nests only ATOMIC → EXTP/EXTR and EXTR → EXTR);
  - a faulting instruction's flag updates are undone;
  - stack-limit traps push the IP of the instruction after the one that moved SP (UM).
- Stack limits:
  - every SP write is checked, MOV SP included (the UM checks only pushes and subtractions
    against STKOV, pops and additions against STKUN);
  - SP, STKOV, STKUN and CP keep bits 15:12 as written (UM: tied to 1);
  - SYSCON.STKSZ is not modelled, so the stack never wraps.

**Timing**
- ISM Table 11 plus bus states. Interrupt, PEC and trap entries cost 4 states.
- ACT = 2 (3 on a multiplexed bus) + (15 − MCTC) + (1 − MTTC) per access. RWDC and ALECTL add
  nothing. XRAM and the CAN module are external accesses of 2 states.
- Not modelled: the second-order effects of ISM §7.3, and pipeline effects (CP/SP write
  latency, the interrupt acceptance delay after IEN/xxIC writes, the stall after a PSW write).

## Layout

| Path | Content |
|---|---|
| `src/Ms43Sim.Core/` | image tools, memory map, CPU, interrupts, peripherals, boot runner |
| `src/Ms43Sim.Cli/` | the `ms43sim` command |
| `tests/Ms43Sim.Tests/` | xUnit tests |
| `tools/` | Python helpers: manual text extraction, SFR table generation, decoder cross-check |
| `refs/` | the two Infineon manuals and their text — local only, not committed |
| `runs/` | `ms43sim` output — local only, not committed |

## Inputs that are not in version control

- **Bins:** the tests read `bins/` and `candidates/` from the main MS43LAB checkout. They walk
  up from the test assembly to find it, or use the `MS43_BINS_ROOT` environment variable (the
  folder that contains `bins/`). Every bin is checked against its SHA-256 before use.
- **Manuals:** `refs/c166ism.pdf` (C166 Family Instruction Set Manual V2.0) and
  `refs/c167cr_um.pdf` (C167CR Derivatives User's Manual V3.1). The plan's Task 1 gives their
  sources and SHA-256. `tools/pdf_to_text.py` turns them into the `.txt` files the other
  helpers read.
