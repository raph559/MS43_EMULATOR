# MS43 Emulator: reference

A purpose-built emulator for the Siemens MS43 (Infineon C167CR). It runs an MS43 bin from
power-on the way the ECU does, so a bin can be tested before it is flashed.

It was built as the `sim/` folder of the author's lab, together with
[MS43 Tuner](https://github.com/raph559/MS43_TUNER) and the live-tuning overlay
([MS43X001_CUSTOM](https://github.com/raph559/MS43X001_CUSTOM)); this repository keeps that folder's
full history. The design notes, plans and reports it cites (`docs/superpowers/…`, `REPORT.md`) stayed
in the lab and are not published.

- Devices and what is modelled: [HARDWARE.md](../HARDWARE.md)
- No bin and no firmware is included: you bring your own (see the end of this page).

**Status:** plan 6 done (2026-10-24).
- Plan 1 gave the CPU, bus, traps, interrupts, PEC, watchdog, timing and the boot runner.
- Plan 2 added:
  - the ports, SSC, GPT1/2, CAPCOM, ADC, PWM, ASC0 and CAN;
  - the board: output latch, M93C86 EEPROM, output-driver chips A and B, the monitoring module,
    the key-on inputs;
  - the start-up runner and one key-off.
- Plan 3 adds:
  - the engine: crank and cams, starter, combustion, an idle-air plant, VANOS and the engine's
    sensors;
  - the throttle body;
  - the CAN partners of an ASC car;
  - output capture with the ECU's decisions beside each injection and spark;
  - the fault-memory reader;
  - a 10 Hz variable trace;
  - `--engine warm-idle`.
- Plan 4 adds:
  - drive files and the drive runner, across power cycles;
  - the car's driving inputs (speed pulses, the steering-wheel line, pedal, clutch, brakes,
    temperatures, A/C, the clock) and fault injection;
  - the engine off idle at the drive's speed, breathing from the car's throttle-air table;
  - 16 checks, a verdict with design §7's coverage, `run --drive` and `check`;
  - the seven standard drives in `drives/`.
- Plan 5 adds:
  - the K-line: one wire between ASC0 and a tester, and the MS43 logger app's DS2 session scripted
    by a drive's `kline` lines (kline.csv, the report's K-line section);
  - a `stall` drive mode, and restarts inside one power cycle (after a stall, or a quick key cycle
    in the after-run, as the firmware does it);
  - `ms43sim compare`: two bins through the same drives, with their calibration, code and
    behaviour differences.
- Plan 6 adds:
  - the live-tuning overlay bin (`patches/live_overlay.asm`, built by
    `tools/build_live_overlay.py`), its contract in C# (`LiveOverlay`, DS2 command 0x6D) and its
    program identity (`ProgramIdentity.Ms43X001LiveOverlayV2` for v2; the historical
    `ProgramIdentity.Ms43X001LiveOverlay` remains recognized), so the checks that read the
    firmware's RAM run on it and it can pass;
  - the overlay drives in `drives/overlay/`: the standard seven with the app's session and tables
    on, the DS2 command and its abuse (`ds2`), `silence`, `stall` and `quick-key-cycle`;
  - the K-line tester's three blind spots from plan 5's review: a restart no longer applies its
    segment's `kline` lines again (M1), kline.csv keeps a timeout's partial frame and every ECU byte
    that was no part of a reply (`stray`, M3), and an ECU byte reaches the tester only at the
    tester's own baud (M4);
  - `PhysicalBus.DataWatch`, an observer of data reads and writes.

The car file starts up with the key on and the engine stopped: its CRC and monitor-sum tasks
pass, the monitoring handshake completes, and its scheduler runs. A key-off runs the after-run,
writes the EEPROM and drops the main relay; the next key-on reads that EEPROM back.

With `--engine warm-idle` the car file starts warm and idles on its 900 rpm target:
- the ECU sees what it is fed;
- its sparks, dwells and injections carry out its own decisions;
- its VANOS and throttle plants track;
- every detected fault code is explained, and none is stored.

`ms43sim check` passes the car file and the release file on all seven standard drives, and fails
three known-bad bins (REPORT.md §9.9). The car file answers the app's DS2 session on the K-line,
restarts after a stall or a quick key cycle, and `compare` shows an ignition edit's decided advance
exactly where its cells are read (REPORT.md §9.10). The overlay bin passes the standard drives and
its own, and a live edit gives exactly the run of the same edit baked into the flash (REPORT.md
§9.11); it has not been flashed.

## Run it

```bash
dotnet build -c Release Ms43Sim.sln
dotnet src/Ms43Sim.Cli/bin/Release/net10.0/ms43sim.dll boot bins/CAR_current_43X001_SP95_STAGE1.bin --out runs/boot-car
```

`ms43sim run BIN` runs a bin with the key on, the engine stopped unless `--engine` names a scenario:

```bash
dotnet src/Ms43Sim.Cli/bin/Release/net10.0/ms43sim.dll run bins/CAR_current_43X001_SP95_STAGE1.bin --seconds 5 --out runs/run-car
```

| Option | Default | Effect |
|---|---|---|
| (none) | | Runs BIN as the ECU holds it after an MS4X Flasher **full write** |
| `--raw`, `--partial-onto ECU.bin`, `--ecu-boot BOOT.bin` | | As for `boot` (below) |
| `--seconds S` | 5 (60 with `--engine`) | How long to run, above 0 and at most 600 |
| `--key-off-at K` | | Turns the key off (AN1 to 0 V) at K seconds, 0 < K < S; the run stops when the main relay drops |
| `--eeprom FILE` | blank | The EEPROM's contents: 2,048 bytes, word *i* little-endian at byte 2*i*. The file is only read |
| `--engine NAME` | | Puts an engine around the ECU: `warm-idle` (warm engine, the car's ASC partners on CAN). The starter turns it at 3.0 s and it should run on its own from 4.0 s. Without it the engine stays stopped |
| `--out DIR` | `runs/run-<name>` | Where the output files go: `run-report.md`, `inventory.csv`, `eeprom.bin` (the chip at the end of the run), `events.csv` (every output change, with the crank angle in an engine run), `trace.csv` (the firmware's variables every 100 ms), and `assessment.json` (software-check scope and physical safety status). An output that is also an input (an `--eeprom` file in that folder) is a usage error |

The outcome is the first that holds: `Trapped`, `ResetLoop` (any reset), `PoweredOff` (the main
relay released after the handover), `SchedulerRunning` (the main loop's SRVWDT at 0E:0144 in the
last 30 ms), `Stalled` (handed over, no scheduler), `StayedInBoot`, `NoProgress`. The report
gives the milestones; with an engine, the engine's phases, how it ended (never started,
running, stalled, or stopped after the key-off) and the inputs fed beside what the ECU reads; the outputs (with an engine, per cylinder: injections, sparks and the ECU's own
decisions); the plants (throttle, VANOS, idle); the fault memory, with each detected path's
explanation or UNEXPLAINED; the CAN partners; scheduler health, checksum tasks, the monitoring
module, the firmware's RAM read-outs, CAN transmissions, resets, events, stubs, the unmodelled
hardware and the assumptions. An engine run starts the engine at 3.0 s and expects it to run from
4.0 s:

```bash
dotnet src/Ms43Sim.Cli/bin/Release/net10.0/ms43sim.dll run bins/CAR_current_43X001_SP95_STAGE1.bin --engine warm-idle --out runs/run-car-idle
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
wrong-size image or EEPROM file. A drive (`run --drive`, `check`) exits with its verdict (below).

`ms43sim decode --listing BIN` prints the decoder's linear sweep. Compare it with
`python -I tools/decode_listing.py BIN`, which uses the lab's `tools/c167dis.py` (not published).

## Drive it: `run --drive` and `check`

`ms43sim run BIN --drive FILE` runs one drive file on BIN with every check and gives a verdict;
`ms43sim check BIN` runs every drive in `drives/` (the seven standard drives):

```bash
dotnet src/Ms43Sim.Cli/bin/Release/net10.0/ms43sim.dll check bins/CAR_current_43X001_SP95_STAGE1.bin --jobs 6 --out runs/check-car
dotnet src/Ms43Sim.Cli/bin/Release/net10.0/ms43sim.dll run bins/CAR_current_43X001_SP95_STAGE1.bin --drive drives/full-load.drive --eeprom runs/check-car/warm-start/eeprom.bin --out runs/full-load
```

| Option | Default | Effect |
|---|---|---|
| `--drive FILE` | | `run` only: the drive to run. It does not take `--seconds`, `--key-off-at` or `--engine`: the drive sets the time, the key and the engine |
| `--eeprom FILE` | blank | `run` only: the EEPROM at the first key-on. A drive that starts `seasoned` needs one: a warm-start run's `eeprom.bin` |
| `--drives DIR` | `drives` above the current folder | `check` only: the folder of `*.drive` files. The one named `warm-start` runs first from a blank EEPROM; its final EEPROM is where every `start seasoned` drive begins (C5) |
| `--jobs N` | 1 | `check` only: how many drives run at once after the warm start; each run is deterministic |
| `--raw`, `--partial-onto ECU.bin`, `--ecu-boot BOOT.bin` | | As for `boot` |
| `--out DIR` | `runs/drive-<drive>-<bin>`, `runs/check-<bin>` | Where the files go (`check`: one folder per drive; two drive files of one name are refused): `drive-report.md` (the software verdict and its reasons, the separate physical safety status, the drive, power cycles, checks, coverage and K-line), `events.csv`, `trace.csv`, `eeprom.bin`, `verdict.txt` (the software verdict), `assessment.json` (both assessments and unjudged software properties), and `kline.csv` (the tester's requests, what came of each, and the bytes the line lost). `check` also writes an aggregate `assessment.json` |

The exit code is the verdict (the worst one for `check`): **0 pass, 1 fail, 4 incomplete**; 2 is a
usage error (a mistake in a drive file is reported with its line), 3 an unreadable or wrong-size
input. Fail beats incomplete beats pass:
- **fail:** a check found something;
- **incomplete:** a check could not run (the checks that read the firmware's RAM need the MS43X001
  program, so the stock file and any code patch are incomplete at best), a stub sits between the CPU
  and a checked output, the run touched something not modelled, a fault is stored or detected
  without an explanation, or part of the drive did not run (the main relay dropped while the drive
  had the key on, or the drive never cranks the engine);
- **pass:** none of these.

These are **software/output-check verdicts**. The API retains `RunVerdict.Pass`, and `verdict.txt`
and the exit code retain their existing values for automation. They provide no physical engine
or calibration safety clearance. Every run, drive and compare report states **physical calibration
safety: NotEvaluated**, and `assessment.json` carries both results separately. The result API exposes
`PhysicalCalibrationSafety` with `EngineSafeClearance = false` and named missing models.

The engine model has no combustion-pressure or octane response, measured mixture/fuel-delivery
model, thermal response to the calibration, or torque/mechanical-limit model. Its knock input is
a function of RPM. Physical ECU timing is also unverified. Consequently, a calibration can pass
all modeled output checks while requesting physically inappropriate ignition or fueling. Use the
software verdict to assess the exercised firmware mechanisms; the simulator cannot validate SP95
octane suitability or clear a calibration for an engine.

**A drive file** (`drives/*.drive`, plan 4 R17) is text: `#` starts a comment.

```
drive part-load                          # first: the name
start seasoned                           # the EEPROM it expects: blank (default) or seasoned
set coolant=90 oil=90 intake=33 ambient=20
3 on                                     # DURATION MODE KEY=VALUE ...
1.5 crank rpm=200
6 idle
10 drive gear=3 speed=40..80 pedal=15    # A..B ramps across the segment
```

- **Modes:** `on` (key on, engine stopped), `crank` (the starter at `rpm=`), `idle` (plan 3's idle
  plant, in neutral at a standstill), `drive` (the shaft at the drive's speed, like a dyno), `stall`
  (the engine burns nothing, whatever the ECU does, and runs down with the key on; it follows an idle
  or a drive that ends at a standstill, and a `crank` starts the engine again in the same power
  cycle), `off` (key off; the after-run runs; a later `on` powers the ECU again from the EEPROM it
  wrote; a key-on inside the after-run is what the firmware makes of it, REPORT.md §9.10).
- **Kept keys** (they hold until changed): `coolant`, `oil`, `intake` (−30…140 °C), `ambient`
  (−40…50 °C), `clock` (0…1439 min), `ac`, `pedal` (0…95 %), `gear` (`N` or 1–5), `clutch`, `brake`
  (0 or 1).
- **Segment keys:** `rpm` (0…7600), `speed` (0…250 km/h), `battery` (6…16 V), `force=CHANNEL:VOLTS`
  (`AN0`…`AN15` but AN4/AN13, `muxA0`…`muxA7`, `muxB0`…`muxB7`), `silence=ID` (153, 613 or 615).
- **In a drive segment:** `rpm` and `speed` both given are independent; one given, in gear with the
  clutch out, the gear ratio gives the other; otherwise the missing one holds.
- **Mistakes are errors with their line:** an unknown key, a value out of range, a ramp on a key that
  cannot ramp, a pedal in a crank or idle, an off while moving or twice, an idle or drive before a
  crank, an on or crank while the engine may run, a stall without a running engine.
- **K-line lines** (`kline VERB ...`) script the MS43 logger app's DS2 session on the K-line. They
  act at the start of the next segment, in order:
  - `kline connect`: the app's `12 08 91 01 E8 48 00 2A` at 9,600 baud; an answer with status A0
    moves the tester to 125,000;
  - `kline disconnect`: `12 08 91 00 25 80 00 2E` at the current baud; A0 moves it to 9,600;
  - `kline poll`: the app's `12 05 0B B0 AC`, again and again until `kline quiet`, which drops what
    is queued (a request on the line still finishes); `kline poll BYTES` polls with that payload
    instead, e.g. `kline poll 6D 0B` for the V9 overlay's log with the watch record;
  - `kline watch ADDR`: the V9 overlay's `6D 0C a2 a1 a0`, which watches the map at the hex data
    address ADDR (below 80000; 0 stops the watch). It is queued like a send, so it goes before the
    next poll. The first request after the connect's speed switch can be lost, so a drive sends it
    once polling runs (see `drives/watch`);
  - `kline pause MS`: the wait after each reply, 0 to 1000 ms (default 20);
  - `kline send BYTES`: one DS2 frame with these payload bytes; `kline raw BYTES`: these exact bytes,
    unframed; `kline every SECONDS BYTES`: that frame every SECONDS (above 0, at most 60). The first
    token after `every` is always the period, so `kline every 12 05 0B B0` is every 12 s with
    payload `05 0B B0`.

  The tester works like the app: one request at a time, each byte when its echo comes back. It
  listens 500 ms after a 0x91 command and 400 ms after anything else. The next request goes 20 ms
  after a reply (the app's pause after each sample) and 1 ms after a timeout. A byte at a baud rate
  more than 3 % from the receiving end's (a tester byte for ASC0, an ECU byte for the tester), or
  overlapping another, is lost and noted in kline.csv. An ECU byte that is no part of a reply is
  noted `stray`, and a timeout keeps the part of a frame that came. A restart inside a power cycle
  does not apply its segment's `kline` lines again.

**The checks** (plan 4 R16) watch the machine's events, never the output capture: `traps`,
`resets` (and a main-relay drop with the key read on), `execution` (erased flash, code outside
flash), `boot` (the handover within 1 s),
`checksums`, `monitoring`, `scheduler` (the watchdog service and the crank task at every speed; the
periodic tasks and the overflow flags below 2,000 rpm), `dwell` (8.0 ms), `injector` (98 % of the
engine cycle; no opening with the crank stopped), `throttle` (the bridge drive), `before-rotation`,
`stuck`, `spark-window` (30° ATDC…75° BTDC), `rev-limit`, `engine` (it starts and idles) and `power`
(every cylinder fires while the drive asks for power). The report lists, apart from the benign
notes, what the checks declare they did not judge (the scheduler's periodic tasks above 2,000 rpm);
the verdict does not count it.

## Compare two bins: `compare`

`ms43sim compare REF CANDIDATE` runs both bins, flashed the same way, through one drive or the
standard set, and reports how they differ (design §6.3):

```bash
dotnet src/Ms43Sim.Cli/bin/Release/net10.0/ms43sim.dll compare bins/MS43X001_release_E46_M54B25.bin bins/CAR_current_43X001_SP95_STAGE1.bin --jobs 6 --xdf defs/Siemens_MS43_MS43X001_512K_v1.0.0.xdf --out runs/compare-release-car
dotnet src/Ms43Sim.Cli/bin/Release/net10.0/ms43sim.dll compare REF.bin CANDIDATE.bin --drive drives/full-load.drive --eeprom runs/check-car/warm-start/eeprom.bin --out runs/compare-full-load
```

| Option | Default | Effect |
|---|---|---|
| `--drive FILE` | | One drive. A drive that starts `seasoned` needs `--eeprom`; both bins start from it |
| `--drives DIR` | `drives` above the current folder | A set: the warm-start drive first on both bins from a blank EEPROM, then the others, each seasoned one on both bins from the **reference's** warm-start EEPROM, so a seasoned drive's differences come from the bins (C6) |
| `--jobs N` | 1 | A set only: how many drives run at once after the warm start |
| `--xdf FILE` | | The MS43X001 XDF (local only): names its single constants |
| `--raw`, `--partial-onto ECU.bin`, `--ecu-boot BOOT.bin` | | As for `boot`, for both bins |
| `--out DIR` | `runs/compare-<drive or set>-<ref>-vs-<candidate>` | `compare-report.md`, `diff.csv`, `assessment.json`, and each run's own drive files under `reference/` and `candidate/`; a set writes one such folder per drive, `summary.md`, and an aggregate `assessment.json` |

The report gives both bins' SHA-256 and the drive, both verdicts, and three kinds of difference:
- **calibration** (0x70000–0x7FFFF): every table, axis and constant with changed bytes, named from
  `calmaps_ms43x.csv` (found in the current folder or above) and the XDF's constants; changed bytes
  nothing names are unnamed ranges;
- **code** (0x00000–0x6FFFF): the changed byte ranges, those less than 16 bytes apart together,
  each with the candidate's listing;
- **behaviour:** where the two runs part, per cylinder for sparks and injections, per 100 ms sample
  for the trace and the PWM shares, and whole for the fault memory, the power-cycle ends and the end
  EEPROM. Each range is given with its largest difference and the reference's operating point
  there (rpm, air mass, coolant, road speed, the drive's line).

The checksum fields the flasher rewrites are left out of both image diffs, and the EEPROM's copy of
them (words 1EEh–1F0h) out of the behaviour diff. The tolerances (R6, amended in Task 10) are printed
in every report:
- the ECU's decisions exact;
- the spark as fired within 0.1° and its dwell within 2 %;
- an injection as driven within 1 CAPCOM tick, and its angle within 0.1°;
- the firmware's trace columns exact, the plant's within a stated step;
- the PWM shares within 1 ‰.

Two bins drift by cycles, so the same behaviour can come a moment earlier or later:
- a value differs only beyond the reference's range within 100 ms and the neighbouring events or
  samples;
- an event alone, its neighbours agreeing, differs only beyond one step more: 0.375° of advance, 2 %
  of a dwell or of the ticks, 0.75° of injection angle. A sample alone never differs: samples count
  two or more in a row;
- an event one run has and the other has not is a difference, except one alone beside a gap of that
  cylinder's events in its own run (a cut that began or ended an event earlier);
- the trace samples pair by drive time, and a sample taken while the other run's ECU is off is not
  compared; each power cycle's start and end compare within 0.1 s;
- a PWM share is its average over each sample's 100 ms.

The exit code is the worse of the two runs' verdicts (0, 1 or 4), 2 a usage error, 3 an unreadable
input. Compare reports; the owner judges (design §6.3).

**Read a code change on part load, full load or overrun against a baseline.** On those drives any
change of the program's timing drifts the runs apart: a transparent stub anywhere, which changes no
value, shifts the phase of rotating cut patterns and the VANOS and throttle controllers. On the steady
drives (warm start, cold start, idle, key cycle) two runs agree. A calibration-only change does not
move the code, but its own effect can still ripple through the controllers. For a code change,
compare-report.md gives design §6.3's advice, and on a drive in gear the drift note; summary.md and the
console say so too. See the plan 5 record's open items.

## Build and test

Needs the .NET 10 SDK. The test packages (xUnit 2.5.3, Microsoft.NET.Test.Sdk 17.8.0) are the
versions already in the local NuGet cache, so a restore works offline.

```bash
dotnet test Ms43Sim.sln
```

Run it from the repository root. 1,004 tests (about 17 minutes in all):
- instruction semantics from the manuals, the memory map, interrupts and PEC;
- every peripheral, board device and engine plant;
- the boot of the real bins, the car's 5 s start-up and a 40 s key-off;
- a 100 s warm idle with a key-off at 75 s, then a 30 s second key-on;
- the drive parser and the runner; every check passing, and failing on a known-bad, raw or
  synthetic run, except `scheduler` and `boot` (no bin trips them yet) and the `rev-limit`, `power`
  and `spark-window` rules, which fail as pure rules only (the plan 4 record);
- the K-line (the bus, the tester, the car file on the app's session), stalls and restarts, a quick
  key cycle, and the compare's parts and command;
- the live-tuning overlay (`LiveTuning/`): the facts it leans on, its contract, its image, hooks,
  edits, rules and command set;
- in the `StandardDrives` category (49 tests): the car and release files on all seven standard
  drives, the three known-bad bins, the compare's acceptance (the car against itself, the
  transparent stub, an ignition edit, the release), and the overlay's safety, abuse, drive set and
  acceptance.

`dotnet test Ms43Sim.sln --filter "Category!=StandardDrives"` leaves the standard drives out
(955 tests, about 7 minutes).

**Speed:** 122 M emulated cycles per second on this PC (Release, the warm-start drive with every
check, plan 5; plan 4 measured 95–111 M; it varies with the PC's load), 5.1× real time; plan 6
measured the overlay bin at the car file's speed (116.9 M against 111.6 M, 95.2 M against 95.1 M); `check` of
the car's seven drives takes about 32 s with `--jobs 6`. Plan 3's 60 s warm-idle run, without checks, ran at 132.2 M.

## Assumptions and simplifications in plan 5

A drive run with `kline` lines lists these too (`MachineAssumptions.ForDrive`):
- the K-line: one wire between ASC0 and a tester with no transceiver delay. A byte reaches both ends
  at the end of its stop bit, and ASC0 hears its own frames back. Its receiver takes a frame only if
  S0R and S0REN were set at the frame's start bit (UM §11.1.2) [obs]. A tester byte more than 3 %
  from ASC0's bit time, or overlapping another byte, is lost and noted; a real UART would take garbage
  and raise errors, which is not exercised [assumption];
- the tester: the app's DS2 session frame for frame. The 20 ms pause after a reply is the app's
  [obs]; after a connect it stands for the serial cable's latency, and the 1 ms after a timeout for
  the app's immediate retry [assumption];
- a stall: the engine plant burns nothing until a crank feeds it again [call];
- power after a relay drop: with the key off the ECU is off. With the key on, KL15 keeps the CPU
  powered until the firmware resets itself, which starts the next power cycle on a fresh machine (a
  real SRST keeps the RAM; the boot sets it up again) [obs; assumption].

The rulings behind them are in the plan 5 record.

## Assumptions and simplifications in plan 4

A drive run lists the car's inputs too (`MachineAssumptions.ForDrive`):
- the temperatures through the car's sensor curves (R10), the radiator outlet as the ambient plus
  0.9 × (coolant − ambient) [assumption];
- the pedal tracks, the clutch and brake switches, and a battery override on AN5 (and AN1 with the
  key on);
- the speed pulses (6.971 per second per km/h on P7.6) and 153h's speed;
- the steering-wheel "no button" train on P7.4 while the key is on, low with the key off
  [assumption];
- the cluster's clock, A/C and ambient on 613h/615h;
- forced channels and silenced partners (fault injection);
- the engine at the drive's speed off idle, a dyno with no torque model [owner];
- the breathing law: the firmware's own air model (`ip_maf_1_diag__n__tps_av`) frozen as the
  engine's (R12);
- the CPU load at speed: the instruction timing is the ISM's minimum for external execution, so the
  scheduler check judges the task cadence below 2,000 rpm only (plan 4 Task 11).

The rulings behind them are in the plan 4 record.

## Assumptions and simplifications in plan 3

An engine run lists the engine's laws too (`MachineAssumptions.ForEngine`), with the CAN
partners' timing:
- the starter (200 rpm for 1 s from 3.0 s);
- the combustion rule (a spark and an injector closing within 0.25 s);
- the idle-air plant (50 kg/h per unit valve opening, 105 mg/stroke, 50 ms and 0.3 s lags, a
  480 mg/stroke cap);
- the battery in each phase;
- the frozen MAF and knock laws, the coil current and the burn pulse;
- the VANOS velocity model and its hold duties;
- the shaft's start angle.

Every run also lists the throttle body's velocity model. The inputs, chip B and the CAN line
changed as follows:
- **Inputs:** the warm-idle set (coolant and oil 90 °C, intake air 33 °C, 101.3 kPa); the
  engine and the throttle drive AN0, AN11, AN12, AN14, AN15 and P3.6, and the shaft P2.13-P2.15,
  P3.0 and P3.2.
- **Chip B:** answers the input set's status word, FFFF FAFAh in both car sets (the lambda
  heaters read open).
- **CAN:** the other nodes are the CAN partners stub, which sends from 100 ms after power-on
  while the key is on.

The rulings behind them are in the plan 3 record.

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
- **Stubs** (design §7), as plan 2 left them: CAN partners silent; chips A/B always "no fault";
  the monitoring module's shut-off path; the throttle body static at limp-home; knock and coil
  feedback 0 V; no K-line tester. Plan 3 replaced the throttle stub with the throttle body, and
  gave the CAN partners and the engine runs their own rows. The report's Stubs section says which
  sit between the CPU and a checked output.

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
| `src/Ms43Sim.Core/` | image tools, memory map, CPU, interrupts, peripherals, the board, the engine and the car (`Engine/`, `Vehicle/`, the K-line and its tester), capture, fault memory, drive files (`Drive/`), the checks and the verdict (`Checks/`), the boot, start-up and drive runners, and the compare (`Compare/`) |
| `src/Ms43Sim.Cli/` | the `ms43sim` command |
| `tests/Ms43Sim.Tests/` | xUnit tests; `fixtures/overlay.hex`, the frozen known-bad overlay build; `fixtures/live_overlay_v2.hex`, the current live-tuning overlay; and `fixtures/live_overlay.hex`, the retained historical v1 overlay (assembler output applied in memory) |
| `drives/` | the seven standard drive files; `drives/overlay/`, the overlay's eleven |
| `tools/` | Python helpers: manual text extraction, SFR table generation, decoder cross-check |
| `refs/` | the two Infineon manuals and their text — local only, not committed |
| `runs/` | `ms43sim` output — local only, not committed |

## Inputs that are not in version control

- **Bins:** the tests read `bins/` and `candidates/` from the author's lab, which are not published. They walk
  up from the test assembly to find them, or use the `MS43_BINS_ROOT` environment variable (the
  folder that contains `bins/`). Every bin is checked against its SHA-256 before use.
- **Manuals:** `refs/c166ism.pdf` (C166 Family Instruction Set Manual V2.0) and
  `refs/c167cr_um.pdf` (C167CR Derivatives User's Manual V3.1). The plan's Task 1 gives their
  sources and SHA-256. `tools/pdf_to_text.py` turns them into the `.txt` files the other
  helpers read.
