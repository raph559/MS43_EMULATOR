namespace Ms43Sim.Core;

/// <summary>
/// The SFR area (00'FE00-00'FFFF) and ESFR area (00'F000-00'F1FF). A component that models a
/// register registers read/write hooks for it; every other address is plain storage, and each
/// access to it is noted in the access inventory.
/// </summary>
public sealed class SfrFile
{
    const uint SfrBase = 0xFE00, EsfrBase = 0xF000, AreaWords = 256;

    // [assumption] Reset values that depend on configuration pins: BUSCON0 = 16-bit
    // demultiplexed bus, 15 waitstates, active (the boot writes 45BFh at 0x2D5A); SYSCON = 0
    // (the boot rewrites the pin-dependent bits at 0x2D38).
    const uint Buscon0 = 0xFF0C, Syscon = 0xFF12;
    const ushort Buscon0Reset = 0x0680, SysconReset = 0x0000;

    readonly AccessInventory inventory;
    readonly ushort[] sfr = new ushort[AreaWords], esfr = new ushort[AreaWords];
    readonly Func<ushort>?[] sfrRead = new Func<ushort>?[AreaWords], esfrRead = new Func<ushort>?[AreaWords];
    readonly Action<ushort>?[] sfrWrite = new Action<ushort>?[AreaWords], esfrWrite = new Action<ushort>?[AreaWords];
    readonly string?[] sfrOwner = new string?[AreaWords], esfrOwner = new string?[AreaWords];
    readonly Func<ushort>?[] sfrPeek = new Func<ushort>?[AreaWords], esfrPeek = new Func<ushort>?[AreaWords];

    public SfrFile(AccessInventory inventory) => this.inventory = inventory;

    public static bool Contains(uint address) =>
        address is >= SfrBase and <= 0xFFFF || address is >= EsfrBase and <= 0xF1FF;

    /// <summary>
    /// Hands an address to a component. <paramref name="peek"/> returns the live value without
    /// side effects (for reports, hashes and tests); without it, Peek reads plain storage.
    /// </summary>
    public void Register(uint address, string owner, Func<ushort>? read, Action<ushort>? write,
        Func<ushort>? peek = null)
    {
        var (_, _, reads, writes, owners, i) = Locate(address);
        reads[i] = read;
        writes[i] = write;
        owners[i] = owner;
        (address >= SfrBase ? sfrPeek : esfrPeek)[i] = peek;
    }

    public bool IsModelled(uint address)
    {
        var (_, _, _, _, owners, i) = Locate(address);
        return owners[i] != null;
    }

    public ushort Read16(uint address)
    {
        var (area, store, reads, _, owners, i) = Locate(address);
        if (reads[i] is { } read)
            return read();
        if (owners[i] == null)
            inventory.Note(area, address & ~1u, AccessKind.Read);
        return store[i];
    }

    public byte Read8(uint address)
    {
        var word = Read16(address & ~1u);
        return (address & 1) == 0 ? (byte)word : (byte)(word >> 8);
    }

    public void Write16(uint address, ushort value)
    {
        var (area, store, _, writes, owners, i) = Locate(address);
        if (writes[i] is { } write)
        {
            write(value);
            return;
        }
        if (owners[i] == null)
            inventory.Note(area, address & ~1u, AccessKind.Write);
        store[i] = value;
    }

    /// <summary>UM §4: a byte write to an SFR clears the other byte of the register.</summary>
    public void Write8(uint address, byte value) =>
        Write16(address & ~1u, (address & 1) == 0 ? value : (ushort)(value << 8));

    public ushort Peek(uint address)
    {
        var (_, store, _, _, _, i) = Locate(address);
        var peek = (address >= SfrBase ? sfrPeek : esfrPeek)[i];
        return peek != null ? peek() : store[i];
    }

    public void Poke(uint address, ushort value)
    {
        var (_, store, _, _, _, i) = Locate(address);
        store[i] = value;
    }

    /// <summary>Loads every reset value from UM Table 22-4, then the two pin-dependent overrides.</summary>
    public void ResetAll()
    {
        Array.Clear(sfr);
        Array.Clear(esfr);
        foreach (var s in SfrTable.All)
            Poke(s.Address, s.ResetValue);
        Poke(Buscon0, Buscon0Reset);
        Poke(Syscon, SysconReset);
    }

    (AccessArea Area, ushort[] Store, Func<ushort>?[] Reads, Action<ushort>?[] Writes, string?[] Owners, int Index)
        Locate(uint address)
    {
        if (address is >= SfrBase and <= 0xFFFF)
            return (AccessArea.Sfr, sfr, sfrRead, sfrWrite, sfrOwner, (int)((address - SfrBase) >> 1));
        if (address is >= EsfrBase and <= 0xF1FF)
            return (AccessArea.Esfr, esfr, esfrRead, esfrWrite, esfrOwner, (int)((address - EsfrBase) >> 1));
        throw new ArgumentOutOfRangeException(nameof(address), $"0x{address:X6} is not an SFR or ESFR address.");
    }
}
