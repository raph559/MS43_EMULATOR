# Contributing

Thanks for helping. Issues and pull requests are both welcome.

## Reporting a problem

Open an issue with:

- the command you ran and its options;
- the report's outcome or verdict, and the lines of the report that look wrong;
- the bin's firmware (stock MS430069 family, MS43X001, a patch of either) and its SHA-256.

**Never attach a bin, an EEPROM dump from a car or an XDF.** Bins carry someone's software and tune; the
SHA-256 is enough to tell which one it is.

If the emulator touches hardware it does not model, the report lists it under the unmodelled hardware.
Those lines, with the code address, are the most useful part of a report.

## Changing the code

- Build with the .NET 10 SDK: `dotnet build -c Release Ms43Sim.sln`.
- Add a test for what you change, next to the tests of that part, and run the affected test classes:
  `dotnet test Ms43Sim.sln --filter "FullyQualifiedName~YourTests"`.
- A device model follows the Infineon manuals and what the firmware does with it. Give the evidence
  (the manual's section, the firmware's code address) in the code and in [HARDWARE.md](HARDWARE.md).
- Keep runs deterministic: the same bin and drive give the same output, byte for byte.
- Do not loosen a check to make a run pass. If a check is wrong, show why in the pull request.

By contributing you agree that your work is licensed under GPL-3.0-only, like the rest of the project. Only
submit work you have the right to license that way: no firmware, no code or data from sources whose license
does not allow it.
