namespace Ms43Sim.Core;

public sealed record SfrInfo(uint Address, string Name, ushort ResetValue, bool BitAddressable, bool Extended);

/// <summary>Every C167CR SFR and ESFR (UM Table 22-4). The data is in SfrTable.g.cs.</summary>
public static partial class SfrTable
{
    // Lazy: the initialisation order of static fields across partial files is not defined,
    // so the lookups must not be built before the generated array exists.
    static readonly Lazy<Dictionary<uint, SfrInfo>> byAddress = new(() => all.ToDictionary(s => s.Address));
    static readonly Lazy<Dictionary<string, SfrInfo>> byName = new(() => all.ToDictionary(s => s.Name));

    public static IReadOnlyList<SfrInfo> All => all;

    public static SfrInfo? ByAddress(uint address) => byAddress.Value.GetValueOrDefault(address);

    public static uint AddressOf(string name) =>
        byName.Value.TryGetValue(name, out var s) ? s.Address
            : throw new ArgumentException($"No C167CR register named '{name}'.", nameof(name));
}
