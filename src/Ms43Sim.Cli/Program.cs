using Ms43Sim.Core;

namespace Ms43Sim.Cli;

public static class Program
{
    const string Usage =
        "usage:\n" +
        "  ms43sim decode --listing BIN    linear-sweep listing of the code regions (offset size mnemonic)";

    /// <summary>The code regions of tools/xref.py: boot block, service module, program.</summary>
    static readonly (int Start, int End)[] CodeRegions = { (0x0000, 0x3C00), (0x8000, 0xBA34), (0x10000, 0x6EA7A) };

    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

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
