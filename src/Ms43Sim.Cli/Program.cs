using System.Diagnostics;
using System.Globalization;
using Ms43Sim.Core;

namespace Ms43Sim.Cli;

public static class Program
{
    const string Usage =
        "usage:\n" +
        "  ms43sim boot BIN [--raw | --partial-onto ECU.bin] [--ecu-boot BOOT.bin]\n" +
        "                   [--max-cycles N] [--stop-at-handover] [--out DIR]\n" +
        "      runs BIN from power-on as the ECU holds it after an MS4X Flasher full write\n" +
        "      (checksums corrected, boot block kept); writes boot-report.md and inventory.csv\n" +
        "  ms43sim run BIN [--raw | --partial-onto ECU.bin] [--ecu-boot BOOT.bin]\n" +
        "                  [--seconds S] [--eeprom FILE] [--out DIR]\n" +
        "      runs BIN with the key on and the engine stopped for S seconds (default 5, at most\n" +
        "      600); --eeprom loads a 2,048-byte EEPROM image (default: blank) and is only read;\n" +
        "      writes run-report.md, inventory.csv and eeprom.bin (the chip at the end of the run)\n" +
        "  ms43sim decode --listing BIN\n" +
        "      linear-sweep listing of the code regions (offset size mnemonic)";

    /// <summary>The code regions of tools/xref.py: boot block, service module, program.</summary>
    static readonly (int Start, int End)[] CodeRegions = { (0x0000, 0x3C00), (0x8000, 0xBA34), (0x10000, 0x6EA7A) };

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>Exit codes: 0 the command ran, 2 usage error, 3 unreadable or wrong-size image or EEPROM file.</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0)
        {
            error.WriteLine(Usage);
            return 2;
        }
        try
        {
            return args[0] switch
            {
                "boot" => Boot(args[1..], output, error),
                "run" => RunCommand(args[1..], output, error),
                "decode" => Decode(args[1..], output, error),
                _ => UsageError(error, $"unknown command '{args[0]}'"),
            };
        }
        catch (InvalidDataException ex)
        {
            error.WriteLine(ex.Message);
            return 3;
        }
        catch (IOException ex)
        {
            error.WriteLine(ex.Message);
            return 3;
        }
        catch (UnauthorizedAccessException ex)
        {
            error.WriteLine(ex.Message);
            return 3;
        }
    }

    /// <summary>The image a command runs: BIN and how it was flashed.</summary>
    sealed class ImageArgs
    {
        public string? Bin, PartialOnto, EcuBoot;
        public bool Raw;

        /// <summary>Takes args[i] (and its value) if it is an image option or BIN; false for anything else.</summary>
        public bool Take(string[] args, ref int i)
        {
            switch (args[i])
            {
                case "--raw": Raw = true; return true;
                case "--partial-onto" when i + 1 < args.Length: PartialOnto = args[++i]; return true;
                case "--ecu-boot" when i + 1 < args.Length: EcuBoot = args[++i]; return true;
            }
            if (args[i].StartsWith("--", StringComparison.Ordinal) || Bin != null)
                return false;
            Bin = args[i];
            return true;
        }

        public string? Problem(string command)
        {
            if (Bin == null)
                return $"{command} needs a BIN";
            if (Raw && (PartialOnto != null || EcuBoot != null))
                return "--raw cannot be combined with --partial-onto or --ecu-boot";
            if (PartialOnto != null && EcuBoot != null)
                return "--ecu-boot cannot be combined with --partial-onto: a partial write keeps the ECU's program and boot block";
            return null;
        }

        public FlashedImage Build()
        {
            var file = FirmwareImage.Load(Bin!);
            return Raw ? FlashImageBuilder.Raw(file)
                : PartialOnto != null ? FlashImageBuilder.AsFlashedPartial(file, FirmwareImage.Load(PartialOnto))
                : FlashImageBuilder.AsFlashedFull(file, EcuBoot != null ? FirmwareImage.Load(EcuBoot) : null);
        }
    }

    static int Boot(string[] args, TextWriter output, TextWriter error)
    {
        var image = new ImageArgs();
        string? outDir = null;
        var stopAtHandover = false;
        ulong maxCycles = 30_000_000;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--stop-at-handover": stopAtHandover = true; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--max-cycles" when i + 1 < args.Length:
                    if (!ulong.TryParse(args[++i], out maxCycles) || maxCycles == 0)
                        return UsageError(error, "--max-cycles needs a positive number");
                    break;
                default:
                    if (!image.Take(args, ref i))
                        return UsageError(error, $"unexpected argument '{args[i]}'");
                    break;
            }
        }
        if (image.Problem("boot") is { } problem)
            return UsageError(error, problem);

        var flashed = image.Build();
        var clock = Stopwatch.StartNew();
        var result = BootRun.Run(flashed, new BootRunOptions(maxCycles, stopAtHandover));
        clock.Stop();

        outDir ??= Path.Combine("runs", "boot-" + Path.GetFileNameWithoutExtension(image.Bin));
        BootReportWriter.Write(result, outDir);

        output.WriteLine($"outcome: {result.Outcome}");
        output.WriteLine($"handover target {BootReportWriter.Cpu(result.HandoverTarget)}; boot passes: {result.Passes.Count}; " +
                         $"watchdog resets: {result.WatchdogResets}; traps: {result.CountOf<TrapEvent>()}");
        output.WriteLine($"report: {Path.Combine(outDir, "boot-report.md")}");
        Speed(output, result.Instructions, result.Cycles, clock.Elapsed.TotalSeconds);
        return 0;
    }

    static int RunCommand(string[] args, TextWriter output, TextWriter error)
    {
        var image = new ImageArgs();
        string? outDir = null, eepromFile = null;
        var seconds = 5.0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--eeprom" when i + 1 < args.Length: eepromFile = args[++i]; break;
                case "--seconds" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                        || !(seconds > 0) || seconds > 600)
                        return UsageError(error, "--seconds needs a number above 0 and at most 600");
                    break;
                default:
                    if (!image.Take(args, ref i))
                        return UsageError(error, $"unexpected argument '{args[i]}'");
                    break;
            }
        }
        if (image.Problem("run") is { } problem)
            return UsageError(error, problem);

        var flashed = image.Build();
        var eeprom = eepromFile != null ? File.ReadAllBytes(eepromFile) : null;
        var options = new StartupRunOptions(seconds, null, new MachineOptions { EepromImage = eeprom });
        var clock = Stopwatch.StartNew();
        var result = StartupRun.Run(flashed, options);
        clock.Stop();

        outDir ??= Path.Combine("runs", "run-" + Path.GetFileNameWithoutExtension(image.Bin));
        RunReportWriter.Write(result, outDir);

        output.WriteLine($"outcome: {result.Outcome}");
        output.WriteLine($"report: {Path.Combine(outDir, "run-report.md")}");
        Speed(output, result.Instructions, result.Cycles, clock.Elapsed.TotalSeconds);
        return 0;
    }

    /// <summary>The host speed: the only wall-clock reading, and it stays on the console.</summary>
    static void Speed(TextWriter output, ulong instructions, ulong cycles, double seconds) =>
        output.WriteLine($"ran {instructions:N0} instructions, {cycles:N0} cycles in {seconds:0.00} s " +
                         $"({cycles / Math.Max(seconds, 1e-9) / 1e6:0.0} M cycles/s; real time is 24.0)");

    static int Decode(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 2 || args[0] != "--listing")
            return UsageError(error, "decode needs --listing BIN");
        var image = FirmwareImage.Load(args[1]).ToArray();
        foreach (var (start, end) in CodeRegions)
        {
            foreach (var d in Decoder.Sweep(image, start, end))
                output.WriteLine($"{d.Address:X6} {d.Size} {d.Mnemonic}");
        }
        return 0;
    }

    static int UsageError(TextWriter error, string message)
    {
        error.WriteLine($"ms43sim: {message}");
        error.WriteLine(Usage);
        return 2;
    }
}
