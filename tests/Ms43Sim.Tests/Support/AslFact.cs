using System.Diagnostics;

namespace Ms43Sim.Tests.Support;

/// <summary>
/// A fact that needs the Macroassembler AS in WSL (Ubuntu-24.04, ~/src/asl_dl/asl-current), as tools/ms43_patch.py runs it:
/// it is skipped, never passed, where that is not installed.
/// </summary>
public sealed class AslFactAttribute : FactAttribute
{
    public AslFactAttribute()
    {
        if (!Asl.Available)
            Skip = "the AS assembler is not available in WSL (Ubuntu-24.04, ~/src/asl_dl/asl-current)";
    }
}

/// <summary>The AS assembler, run in a WSL temporary folder of its own: it never writes beside the source.</summary>
public static class Asl
{
    const string Tools = "~/src/asl_dl/asl-current";

    static readonly Lazy<bool> available = new(() =>
    {
        try
        {
            return Run($"test -x {Tools}/asl && test -x {Tools}/p2hex", "").ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;                                    // no wsl.exe
        }
    });

    public static bool Available => available.Value;

    /// <summary>
    /// The Intel HEX that <c>asl -q -L</c> and <c>p2hex -q -F Intel32 -l 32</c> make of <paramref name="source"/>, the flags
    /// tools/ms43_patch.py uses.
    /// </summary>
    public static string Assemble(string source)
    {
        var dir = $"/tmp/ms43lab-asl-{Guid.NewGuid():N}";
        var (exit, output, error) = Run(
            $"set -e; mkdir -p {dir}; trap 'rm -rf {dir}' EXIT; cat > {dir}/patch.asm; cd {dir}; " +
            $"{Tools}/asl -q -L -olist patch.lst patch.asm >/dev/null; {Tools}/p2hex -q -F Intel32 -l 32 patch.p patch.hex >/dev/null; cat patch.hex",
            source);
        if (exit != 0)
            throw new InvalidOperationException($"AS failed ({exit}): {output}{error}");
        return output;
    }

    static (int ExitCode, string Output, string Error) Run(string script, string input)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-d", "Ubuntu-24.04", "--exec", "bash", "-lc", script })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Write(input.Replace("\r\n", "\n"));
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("AS in WSL did not finish in 120 s");
        }
        return (process.ExitCode, output.Result, error.Result);
    }
}
