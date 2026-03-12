# sim — the MS43 bench emulator

A purpose-built emulator for the Siemens MS43 (Infineon C167CR). It runs an MS43 bin from
power-on the way the ECU does, so a bin can be tested before it is flashed.

- Design: `docs/superpowers/specs/2026-10-20-ms43-emulator-design.md`
- Current plan: `docs/superpowers/plans/2026-10-20-ms43-emulator-plan1-cpu-boot.md`

**Status:** plan 1 (CPU core, bus, interrupts, boot) in progress.

## Layout

| Path | Content |
|---|---|
| `src/Ms43Sim.Core/` | image tools, memory map, CPU, interrupts, peripherals, runners |
| `src/Ms43Sim.Cli/` | the `ms43sim` command |
| `tests/Ms43Sim.Tests/` | xUnit tests |
| `tools/` | Python helpers (manual text extraction, SFR table, decoder cross-check) |
| `refs/` | the two Infineon manuals and their text — local only, not committed |
| `runs/` | `ms43sim` output — local only, not committed |

## Build and test

Needs the .NET 10 SDK. The test packages (xUnit 2.5.3, Microsoft.NET.Test.Sdk 17.8.0) are
the versions already in the local NuGet cache, so a restore works offline.

```bash
dotnet build sim/Ms43Sim.sln
dotnet test sim/Ms43Sim.sln
```

Run both from the repository root.

## Inputs that are not in version control

- **Bins:** the tests read `bins/` and `candidates/` from the main MS43LAB checkout. They walk
  up from the test assembly to find it, or use the `MS43_BINS_ROOT` environment variable (the
  folder that contains `bins/`). Every bin is checked against its SHA-256 before use.
- **Manuals:** `refs/c166ism.pdf` (C166 Family Instruction Set Manual V2.0) and
  `refs/c167cr_um.pdf` (C167CR Derivatives User's Manual V3.1). The plan's Task 1 gives their
  sources and SHA-256. `tools/pdf_to_text.py` turns them into the `.txt` files the other
  helpers read.
