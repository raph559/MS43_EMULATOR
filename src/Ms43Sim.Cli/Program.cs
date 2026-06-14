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
        "                  [--seconds S] [--key-off-at K] [--eeprom FILE] [--engine NAME] [--out DIR]\n" +
        "      runs BIN with the key on for S seconds (default 5, or 60 with --engine; at most\n" +
        "      600), the engine stopped unless --engine names a scenario (warm-idle: the starter\n" +
        "      at 3.0 s, running from 4.0 s); --key-off-at turns the key off at K seconds\n" +
        "      (0 < K < S), and the run stops when the main relay drops; --eeprom loads a\n" +
        "      2,048-byte EEPROM image (default: blank) and is only read; writes run-report.md,\n" +
        "      inventory.csv, eeprom.bin (the chip at the end of the run), events.csv (the\n" +
        "      outputs) and trace.csv (the variables every 100 ms)\n" +
        "  ms43sim run BIN --drive FILE [--raw | --partial-onto ECU.bin] [--ecu-boot BOOT.bin]\n" +
        "                  [--eeprom FILE] [--out DIR]\n" +
        "      runs the drive FILE on BIN with every check; a drive that starts seasoned needs\n" +
        "      --eeprom (a warm-start run's eeprom.bin); writes drive-report.md, events.csv,\n" +
        "      trace.csv, eeprom.bin and verdict.txt; the exit code is the verdict: 0 pass,\n" +
        "      1 fail, 4 incomplete\n" +
        "  ms43sim check BIN [--drives DIR] [--jobs N] [--raw | --partial-onto ECU.bin]\n" +
        "                    [--ecu-boot BOOT.bin] [--out DIR]\n" +
        "      runs every *.drive in DIR (default: sim/drives) on BIN with every check, the\n" +
        "      warm-start drive first: its final EEPROM is where the seasoned drives start;\n" +
        "      --jobs N runs the others N at a time; one folder and one line per drive; the\n" +
        "      exit code is the worst verdict\n" +
        "  ms43sim decode --listing BIN\n" +
        "      linear-sweep listing of the code regions (offset size mnemonic)";

    /// <summary>The code regions of tools/xref.py: boot block, service module, program.</summary>
    static readonly (int Start, int End)[] CodeRegions = { (0x0000, 0x3C00), (0x8000, 0xBA34), (0x10000, 0x6EA7A) };

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>
    /// Exit codes: 0 the command ran (for a drive or a check: pass), 1 fail, 4 incomplete, 2 usage error
    /// (a drive file's mistake included), 3 unreadable or wrong-size image or EEPROM file.
    /// </summary>
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
                "run" when args.Contains("--drive") => DriveCommand(args[1..], output, error),
                "run" => RunCommand(args[1..], output, error),
                "check" => CheckCommand(args[1..], output, error),
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

        public IEnumerable<string?> Inputs => new[] { Bin, PartialOnto, EcuBoot };

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

        outDir ??= Path.Combine("runs", "boot-" + Path.GetFileNameWithoutExtension(image.Bin));
        if (Overwritten(image.Inputs, outDir, "boot-report.md", "inventory.csv") is { } clash)
            return UsageError(error, $"the run would write over its input {clash}: choose another --out");

        var flashed = image.Build();
        var clock = Stopwatch.StartNew();
        var result = BootRun.Run(flashed, new BootRunOptions(maxCycles, stopAtHandover));
        clock.Stop();

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
        double? seconds = null, keyOffAt = null;
        EngineScenario? engine = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--eeprom" when i + 1 < args.Length: eepromFile = args[++i]; break;
                case "--seconds" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
                        || !(s > 0) || s > 600)
                        return UsageError(error, "--seconds needs a number above 0 and at most 600");
                    seconds = s;
                    break;
                case "--engine" when i + 1 < args.Length:
                    engine = EngineScenario.Named(args[++i]);
                    if (engine == null)
                        return UsageError(error, $"unknown engine '{args[i]}'; known: {string.Join(", ", EngineScenario.All.Select(e => e.Name))}");
                    break;
                case "--key-off-at" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var keyOff))
                        return UsageError(error, "--key-off-at needs a number of seconds");
                    keyOffAt = keyOff;
                    break;
                default:
                    if (!image.Take(args, ref i))
                        return UsageError(error, $"unexpected argument '{args[i]}'");
                    break;
            }
        }
        if (image.Problem("run") is { } problem)
            return UsageError(error, problem);
        var runSeconds = seconds ?? (engine != null ? 60 : 5);
        if (keyOffAt is { } off && !(off > 0 && off < runSeconds))
            return UsageError(error, "--key-off-at needs a time above 0 and before the end of the run (--seconds)");

        outDir ??= Path.Combine("runs", "run-" + Path.GetFileNameWithoutExtension(image.Bin));
        if (Overwritten(image.Inputs.Append(eepromFile), outDir, "run-report.md", "inventory.csv", "eeprom.bin", "events.csv", "trace.csv") is { } clash)
            return UsageError(error, $"the run would write over its input {clash}: choose another --out");

        var flashed = image.Build();
        var eeprom = eepromFile != null ? File.ReadAllBytes(eepromFile) : null;
        var options = new StartupRunOptions(runSeconds, keyOffAt, new MachineOptions { EepromImage = eeprom }, Engine: engine);
        var clock = Stopwatch.StartNew();
        var result = StartupRun.Run(flashed, options);
        clock.Stop();

        RunReportWriter.Write(result, outDir);

        output.WriteLine($"outcome: {result.Outcome}");
        if (result.Engine is { } e)
            output.WriteLine($"engine: {e.Describe()}");
        output.WriteLine($"report: {Path.Combine(outDir, "run-report.md")}");
        Speed(output, result.Instructions, result.Cycles, clock.Elapsed.TotalSeconds);
        return 0;
    }

    static readonly string[] DriveOutputs = { "drive-report.md", "events.csv", "trace.csv", "eeprom.bin", "verdict.txt" };

    static int DriveCommand(string[] args, TextWriter output, TextWriter error)
    {
        var image = new ImageArgs();
        string? outDir = null, eepromFile = null, driveFile = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--drive" when i + 1 < args.Length: driveFile = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--eeprom" when i + 1 < args.Length: eepromFile = args[++i]; break;
                case "--seconds" or "--key-off-at" or "--engine":
                    return UsageError(error, $"{args[i]} does not go with --drive: the drive sets the time, the key and the engine");
                default:
                    if (!image.Take(args, ref i))
                        return UsageError(error, $"unexpected argument '{args[i]}'");
                    break;
            }
        }
        if (driveFile is null)
            return UsageError(error, "--drive needs a FILE");
        if (image.Problem("run") is { } problem)
            return UsageError(error, problem);
        if (ReadDrive(driveFile, error) is not { } drive)
            return 2;
        if (drive.Start == StartEeprom.Seasoned && eepromFile is null)
            return UsageError(error, $"the drive {drive.Name} starts seasoned: give --eeprom FILE (the eeprom.bin of a warm-start run)");
        outDir ??= Path.Combine("runs", $"drive-{drive.Name}-{Path.GetFileNameWithoutExtension(image.Bin)}");
        if (Overwritten(image.Inputs.Append(eepromFile).Append(driveFile), outDir, DriveOutputs) is { } clash)
            return UsageError(error, $"the run would write over its input {clash}: choose another --out");

        var flashed = image.Build();
        var eeprom = eepromFile != null ? File.ReadAllBytes(eepromFile) : null;
        var clock = Stopwatch.StartNew();
        var result = DriveRun.RunWithChecks(flashed, drive, new DriveRunOptions(Eeprom: eeprom));
        clock.Stop();

        DriveReportWriter.Write(result, outDir);
        output.WriteLine($"{drive.Name}: {DriveReportWriter.Word(result.Verdict)}");
        foreach (var reason in Verdict.Reasons(result.Checks, result.Coverage!).Take(5))
            output.WriteLine($"  {reason}");
        output.WriteLine($"report: {Path.Combine(outDir, "drive-report.md")}");
        Speed(output, result.Instructions, result.Cycles, clock.Elapsed.TotalSeconds);
        return (int)result.Verdict;
    }

    static int CheckCommand(string[] args, TextWriter output, TextWriter error)
    {
        var image = new ImageArgs();
        string? outDir = null, drivesDir = null;
        var jobs = 1;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--drives" when i + 1 < args.Length: drivesDir = args[++i]; break;
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--jobs" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out jobs) || jobs < 1)
                        return UsageError(error, "--jobs needs a whole number of 1 or more");
                    break;
                default:
                    if (!image.Take(args, ref i))
                        return UsageError(error, $"unexpected argument '{args[i]}'");
                    break;
            }
        }
        if (image.Problem("check") is { } problem)
            return UsageError(error, problem);
        drivesDir ??= FindStandardDrives();
        if (drivesDir is null || !Directory.Exists(drivesDir))
            return UsageError(error, drivesDir is null ? "no --drives DIR, and no sim/drives above the current folder" : $"no folder {drivesDir}");
        var drives = new List<Drive>();
        foreach (var file in Directory.GetFiles(drivesDir, "*.drive").Order(StringComparer.Ordinal))
        {
            if (ReadDrive(file, error) is not { } drive)
                return 2;
            drives.Add(drive);
        }
        if (drives.Count == 0)
            return UsageError(error, $"no *.drive files in {drivesDir}");
        var warm = drives.FirstOrDefault(d => d.Name == "warm-start");
        if (warm is null && drives.Any(d => d.Start == StartEeprom.Seasoned))
            return UsageError(error, "a seasoned drive needs the warm-start drive in the folder: its final EEPROM is where they start (C5)");
        outDir ??= Path.Combine("runs", "check-" + Path.GetFileNameWithoutExtension(image.Bin));
        if (Overwritten(image.Inputs, outDir, DriveOutputs) is { } clash)
            return UsageError(error, $"the check would write over its input {clash}: choose another --out");

        var flashed = image.Build();
        var verdicts = new Dictionary<string, RunVerdict>();
        byte[]? seasoned = null;
        DriveResult RunOne(Drive drive)
        {
            var result = DriveRun.RunWithChecks(flashed, drive,
                new DriveRunOptions(Eeprom: drive.Start == StartEeprom.Seasoned ? seasoned : null));
            DriveReportWriter.Write(result, Path.Combine(outDir, drive.Name));
            return result;
        }
        if (warm != null)
        {
            var first = RunOne(warm);
            seasoned = first.Eeprom.FinalImage;
            verdicts[warm.Name] = first.Verdict;
            output.WriteLine($"{warm.Name}: {DriveReportWriter.Word(first.Verdict)}");
        }
        var rest = drives.Where(d => d != warm).ToArray();
        var results = new RunVerdict[rest.Length];
        Parallel.For(0, rest.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i => results[i] = RunOne(rest[i]).Verdict);
        for (var i = 0; i < rest.Length; i++)
        {
            verdicts[rest[i].Name] = results[i];
            output.WriteLine($"{rest[i].Name}: {DriveReportWriter.Word(results[i])}");
        }
        var worst = verdicts.Values.Contains(RunVerdict.Fail) ? RunVerdict.Fail
            : verdicts.Values.Contains(RunVerdict.Incomplete) ? RunVerdict.Incomplete
            : RunVerdict.Pass;
        output.WriteLine($"check: {DriveReportWriter.Word(worst)} ({verdicts.Count} drives; reports in {outDir})");
        return (int)worst;
    }

    /// <summary>The drive in <paramref name="file"/>, or null after printing its first mistake with its line.</summary>
    static Drive? ReadDrive(string file, TextWriter error)
    {
        if (DriveParser.TryParse(File.ReadAllText(file), out var drive, out var mistake))
            return drive;
        error.WriteLine($"ms43sim: {file}: {mistake}");
        return null;
    }

    /// <summary>sim/drives in the current folder or the nearest folder above it.</summary>
    static string? FindStandardDrives()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "sim", "drives");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// The first output file that is also an input (an EEPROM image passed back in from an earlier
    /// run's folder, say), or null. Inputs are only ever read.
    /// </summary>
    static string? Overwritten(IEnumerable<string?> inputs, string outDir, params string[] outputs)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var read = inputs.OfType<string>().Select(Path.GetFullPath).ToHashSet(comparer);
        return outputs.Select(name => Path.Combine(outDir, name)).FirstOrDefault(o => read.Contains(Path.GetFullPath(o)));
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
