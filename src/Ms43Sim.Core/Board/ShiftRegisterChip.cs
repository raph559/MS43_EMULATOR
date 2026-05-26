namespace Ms43Sim.Core;

/// <summary>
/// An output-driver chip on the SSC: a shift register in SPI mode 1 [obs: jobs 0-2 use SSCPO = 0,
/// SSCPH = 0]. While enabled: when selected, the register loads the chip's status word; a rising
/// edge drives the register's top bit on MISO; a falling edge shifts MOSI in at the bottom; when
/// deselected, the register is latched as the new command. While disabled it releases MISO and
/// neither shifts nor latches.
/// </summary>
public sealed class ShiftRegisterChip : ISscSlave
{
    readonly Func<bool> chipSelect;
    readonly Func<bool> enabled;
    readonly Func<ulong, ulong> status;
    readonly int length;
    readonly ulong mask;
    ulong register;
    bool selected;
    bool output;

    public ShiftRegisterChip(string name, int lengthBits, Func<bool> chipSelect, Func<bool> enabled, Func<ulong, ulong> status)
    {
        Name = name;
        length = lengthBits;
        mask = (1UL << lengthBits) - 1;
        this.chipSelect = chipSelect;
        this.enabled = enabled;
        this.status = status;
    }

    /// <summary>
    /// Chip A (latch bit 4 low): 16 bits. "No fault" is the complement of the command it latched
    /// at its previous transfer [obs].
    /// </summary>
    public static ShiftRegisterChip ChipA(Func<bool> chipSelect) =>
        new("output driver A", 16, chipSelect, () => true, latched => ~latched & 0xFFFF);

    /// <summary>
    /// Chip B (latch bit 3 low): 32 bits, 16 channels × 2 diagnostic bits, 11 = OK whatever the
    /// output's state [obs]. It answers <paramref name="status"/>, the loads on its outputs, D0
    /// (bits 31-24) shifted out first, and only once <paramref name="released"/> [inf: the monitoring
    /// module holds it off until its first ADh frame].
    /// </summary>
    public static ShiftRegisterChip ChipB(Func<bool> chipSelect, Func<bool> released, Func<uint> status) =>
        new("output driver B", 32, chipSelect, released, _ => status());

    public string Name { get; }

    public bool IsSelected => chipSelect();

    public bool? Miso => IsSelected && enabled() ? output : null;

    /// <summary>The register at the last deselect: the command the chip acts on.</summary>
    public ulong Latched { get; private set; }

    /// <summary>Completed transfers (deselects while enabled).</summary>
    public int Transfers { get; private set; }

    /// <summary>The latched word, at every deselect while enabled.</summary>
    public event Action<ulong>? CommandLatched;

    public void PowerOn()
    {
        Latched = 0;
        register = 0;
        selected = chipSelect();
    }

    /// <summary>The board calls this on every output-latch change.</summary>
    public void ChipSelectChanged()
    {
        var now = chipSelect();
        if (now == selected)
            return;
        selected = now;
        if (!enabled())
            return;
        if (now)
        {
            register = status(Latched) & mask;
            output = TopBit;
        }
        else
        {
            Latched = register;
            Transfers++;
            CommandLatched?.Invoke(Latched);
        }
    }

    public void ClockEdge(bool rising, bool mosi)
    {
        if (!enabled())
            return;
        if (rising)
            output = TopBit;
        else
            register = ((register << 1) | (mosi ? 1UL : 0UL)) & mask;
    }

    bool TopBit => (register >> (length - 1) & 1) != 0;
}
