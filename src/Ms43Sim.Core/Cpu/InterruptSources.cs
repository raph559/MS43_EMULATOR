namespace Ms43Sim.Core;

/// <summary>One of the C167CR's 56 interrupt nodes. Vector = 4 × TrapNumber.</summary>
public sealed record InterruptSource(int Index, string Name, uint IcAddress, byte TrapNumber);

/// <summary>
/// The interrupt nodes of UM Table 5-1. Each name is its control register's name without
/// "IC" (T1 → T1IC); the address comes from the SFR table (UM Table 22-4).
/// </summary>
public static class InterruptSources
{
    static readonly (string Name, byte Trap)[] Nodes =
    {
        ("CC0", 0x10), ("CC1", 0x11), ("CC2", 0x12), ("CC3", 0x13), ("CC4", 0x14), ("CC5", 0x15),
        ("CC6", 0x16), ("CC7", 0x17), ("CC8", 0x18), ("CC9", 0x19), ("CC10", 0x1A), ("CC11", 0x1B),
        ("CC12", 0x1C), ("CC13", 0x1D), ("CC14", 0x1E), ("CC15", 0x1F),
        ("CC16", 0x30), ("CC17", 0x31), ("CC18", 0x32), ("CC19", 0x33), ("CC20", 0x34), ("CC21", 0x35),
        ("CC22", 0x36), ("CC23", 0x37), ("CC24", 0x38), ("CC25", 0x39), ("CC26", 0x3A), ("CC27", 0x3B),
        ("CC28", 0x3C), ("CC29", 0x44), ("CC30", 0x45), ("CC31", 0x46),
        ("T0", 0x20), ("T1", 0x21), ("T7", 0x3D), ("T8", 0x3E),
        ("T2", 0x22), ("T3", 0x23), ("T4", 0x24), ("T5", 0x25), ("T6", 0x26), ("CR", 0x27),
        ("ADC", 0x28), ("ADE", 0x29),
        ("S0T", 0x2A), ("S0TB", 0x47), ("S0R", 0x2B), ("S0E", 0x2C),
        ("SSCT", 0x2D), ("SSCR", 0x2E), ("SSCE", 0x2F),
        ("PWM", 0x3F), ("XP0", 0x40), ("XP1", 0x41), ("XP2", 0x42), ("XP3", 0x43),
    };

    static readonly InterruptSource[] all =
        Nodes.Select((n, i) => new InterruptSource(i, n.Name, SfrTable.AddressOf(n.Name + "IC"), n.Trap)).ToArray();

    static readonly Dictionary<string, InterruptSource> byName = all.ToDictionary(s => s.Name);

    public static IReadOnlyList<InterruptSource> All => all;

    public static InterruptSource ByName(string name) =>
        byName.TryGetValue(name, out var s) ? s : throw new ArgumentException($"No interrupt node '{name}'.", nameof(name));
}
