using System.Diagnostics;
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
        "  ms43sim decode --listing BIN\n" +
        "      linear-sweep listing of the code regions (offset size mnemonic)";

    /// <summary>The code regions of tools/xref.py: boot block, service module, program.</summary>
    static readonly (int Start, int End)[] CodeRegions = { (0x0000, 0x3C00), (0x8000, 0xBA34), (0x10000, 0x6EA7A) };

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>Exit codes: 0 the command ran, 2 usage error, 3 unreadable or wrong-size image.</summary>
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

    static int Boot(string[] args, TextWriter output, TextWriter error)
    {
        string? bin = null, partialOnto = null, ecuBoot = null, outDir = null;
        var raw = false;
        var stopAtHandover = false;
        ulong maxCycles = 30_000_000;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--raw": raw = true; break;
                case "--stop-at-handover": stopAtHandover = true; break;
                case "--partial-onto" when i + 1 < args.Length: partialOnto = args[++i]; break;
                case "--ecu-boot" when i + 1 < args.Length: ecuBoot = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--max-cycles" when i + 1 < args.Length:
                    if (!ulong.TryParse(args[++i], out maxCycles) || maxCycles == 0)
                        return UsageError(error, "--max-cycles needs a positive number");
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) || bin != null)
                        return UsageError(error, $"unexpected argument '{args[i]}'");
                    bin = args[i];
                    break;
            }
        }
        if (bin == null)
            return UsageError(error, "boot needs a BIN");
        if (raw && (partialOnto != null || ecuBoot != null))
            return UsageError(error, "--raw cannot be combined with --partial-onto or --ecu-boot");
        if (partialOnto != null && ecuBoot != null)
            return UsageError(error, "--ecu-boot cannot be combined with --partial-onto: a partial write keeps the ECU's program and boot block");

        var file = FirmwareImage.Load(bin);
        var image = raw ? FlashImageBuilder.Raw(file)
            : partialOnto != null ? FlashImageBuilder.AsFlashedPartial(file, FirmwareImage.Load(partialOnto))
            : FlashImageBuilder.AsFlashedFull(file, ecuBoot != null ? FirmwareImage.Load(ecuBoot) : null);

        var clock = Stopwatch.StartNew();
        var result = BootRun.Run(image, new BootRunOptions(maxCycles, stopAtHandover));
        clock.Stop();

        outDir ??= Path.Combine("runs", "boot-" + Path.GetFileNameWithoutExtension(bin));
        BootReportWriter.Write(result, outDir);

        output.WriteLine($"outcome: {result.Outcome}");
        output.WriteLine($"handover target {BootReportWriter.Cpu(result.HandoverTarget)}; boot passes: {result.Passes.Count}; " +
                         $"watchdog resets: {result.WatchdogResets}; traps: {result.CountOf<TrapEvent>()}");
        output.WriteLine($"report: {Path.Combine(outDir, "boot-report.md")}");
        var seconds = clock.Elapsed.TotalSeconds;
        output.WriteLine($"ran {result.Instructions:N0} instructions, {result.Cycles:N0} cycles in {seconds:0.00} s " +
                         $"({result.Cycles / Math.Max(seconds, 1e-9) / 1e6:0.0} M cycles/s; real time is 24.0)");
        return 0;
    }

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
