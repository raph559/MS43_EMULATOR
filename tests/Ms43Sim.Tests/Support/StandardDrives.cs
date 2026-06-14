using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>The seven standard drives (plan 4 Task 12), read from sim/drives: the nearest one above the test assembly.</summary>
public static class StandardDrives
{
    static readonly Lazy<string> directory = new(Find);

    /// <summary>In the order <c>ms43sim check</c> runs them: warm-start first, then by name order of the set.</summary>
    public static IReadOnlyList<string> All { get; } =
        new[] { "warm-start", "cold-start", "idle", "part-load", "full-load", "overrun", "key-cycle" };

    public static string Directory => directory.Value;

    public static Drive Load(string name) => DriveParser.Parse(File.ReadAllText(Path.Combine(Directory, name + ".drive")));

    static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "sim", "drives");
            if (File.Exists(Path.Combine(candidate, "warm-start.drive")))
                return candidate;
        }
        throw new InvalidOperationException("no sim/drives/warm-start.drive above the test assembly");
    }
}
