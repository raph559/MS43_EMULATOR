namespace Ms43Sim.Core;

public enum Region { Flash, ExternalRam, Iram, Sfr, Esfr, Xram, Can, ChipSelect1, ChipSelect3, ChipSelect4, Reserved }

/// <summary>
/// The C167CR's 24-bit physical address space as the MS43 board wires it (plan Reference facts):
/// IRAM, SFR and ESFR in segment 0; XRAM and the CAN module when SYSCON.XPEN is set; the
/// ADDRSEL windows (window 2 = the 8 KiB external RAM); everything else is the 512 KiB flash,
/// mirrored every 512 KiB. Callers guarantee word accesses are even.
/// </summary>
public sealed class PhysicalBus
{
    const uint IramBase = 0xF600, XramBase = 0xE000, CanBase = 0xEF00;

    readonly byte[] flash;
    readonly SfrFile sfrs;
    readonly AccessInventory inventory;
    readonly EventLog log;
    readonly ushort unmodelledReadValue;
    readonly int externalRamMask;
    readonly IExternalDevice?[] devices = new IExternalDevice?[5];
    CanModule? can;

    public PhysicalBus(FirmwareImage flash, int externalRamBytes, SfrFile sfrs, AccessInventory inv, EventLog log,
        ushort unmodelledReadValue)
    {
        if (externalRamBytes <= 0 || (externalRamBytes & (externalRamBytes - 1)) != 0)
            throw new ArgumentException("External RAM size must be a power of two.", nameof(externalRamBytes));
        this.flash = flash.ToArray();
        this.sfrs = sfrs;
        inventory = inv;
        this.log = log;
        this.unmodelledReadValue = unmodelledReadValue;
        externalRamMask = externalRamBytes - 1;
        ExternalRam = new byte[externalRamBytes];
        ChipSelects = new ChipSelects(sfrs);
    }

    public byte[] Iram { get; } = new byte[0x800];          // 00'F600-00'FDFF
    public byte[] Xram { get; } = new byte[0x800];          // 00'E000-00'E7FF
    public byte[] ExternalRam { get; }

    public ChipSelects ChipSelects { get; }

    /// <summary>SYSCON.XPEN: when set, XRAM and the CAN module answer in segment 0.</summary>
    public bool XperipheralsEnabled { get; set; }

    /// <summary>
    /// An observer of every data access (plan 6): the physical address, and whether it writes. Instruction fetches and
    /// <see cref="Peek16"/>/<see cref="Poke16"/> are not reported. It may read anything and must change nothing; null, the
    /// default, costs one check per access.
    /// </summary>
    public Action<uint, bool>? DataWatch { get; set; }

    public void AttachDevice(int chipSelect, IExternalDevice device) => devices[chipSelect] = device;

    /// <summary>The on-chip CAN module that answers at 00'EF00-00'EFFF while XPEN is set.</summary>
    public void AttachCan(CanModule module) => can = module;

    CanModule Can => can ?? throw new InvalidOperationException("No CAN module is attached to the bus.");

    public Region Decode(uint a)
    {
        a &= 0xFFFFFF;
        if (a < 0x10000)
        {
            if (a >= 0xFE00) return Region.Sfr;
            if (a >= IramBase) return Region.Iram;
            if (a >= 0xF200) return Region.Reserved;
            if (a >= 0xF000) return Region.Esfr;
            if (XperipheralsEnabled)
            {
                if (a is >= XramBase and < 0xE800) return Region.Xram;
                if (a >= CanBase) return Region.Can;
            }
        }
        return ChipSelects.Match(a) switch
        {
            1 => Region.ChipSelect1,
            2 => Region.ExternalRam,
            3 => Region.ChipSelect3,
            4 => Region.ChipSelect4,
            _ => Region.Flash,
        };
    }

    public ushort Read16(uint a, AccessKind kind = AccessKind.Read)
    {
        a &= 0xFFFFFF;
        if (kind != AccessKind.Fetch)
            DataWatch?.Invoke(a, false);
        switch (Decode(a))
        {
            case Region.Flash: return Word(flash, (int)(a & 0x7FFFF));
            case Region.ExternalRam: return Word(ExternalRam, (int)(a & (uint)externalRamMask));
            case Region.Iram: return Word(Iram, (int)(a - IramBase));
            case Region.Sfr:
            case Region.Esfr: return sfrs.Read16(a);
            case Region.Xram: return Word(Xram, (int)(a - XramBase));
            case Region.Can: return Can.Read16(a);
            case Region.Reserved:
                inventory.Note(AccessArea.Reserved, a, kind);
                return 0;   // [assumption] the reserved area reads 0000h
            default:
                return ReadWindow16(a, kind);
        }
    }

    public byte Read8(uint a, AccessKind kind = AccessKind.Read)
    {
        a &= 0xFFFFFF;
        if (kind != AccessKind.Fetch)
            DataWatch?.Invoke(a, false);
        switch (Decode(a))
        {
            case Region.Flash: return flash[a & 0x7FFFF];
            case Region.ExternalRam: return ExternalRam[a & (uint)externalRamMask];
            case Region.Iram: return Iram[a - IramBase];
            case Region.Sfr:
            case Region.Esfr: return sfrs.Read8(a);
            case Region.Xram: return Xram[a - XramBase];
            case Region.Can: return Can.Read8(a);
            case Region.Reserved:
                inventory.Note(AccessArea.Reserved, a, kind);
                return 0;
            default:
                var cs = (int)ChipSelects.Match(a)!;
                if (devices[cs] is { } device) return device.Read8(a);
                inventory.Note(WindowArea(cs), a, kind);
                return (byte)((a & 1) == 0 ? unmodelledReadValue : unmodelledReadValue >> 8);
        }
    }

    public void Write16(uint a, ushort value)
    {
        a &= 0xFFFFFF;
        DataWatch?.Invoke(a, true);
        switch (Decode(a))
        {
            case Region.Flash:
                log.Add((c, pc) => new FlashWriteEvent(c, pc, a, value, false));
                return;
            case Region.ExternalRam: SetWord(ExternalRam, (int)(a & (uint)externalRamMask), value); return;
            case Region.Iram: SetWord(Iram, (int)(a - IramBase), value); return;
            case Region.Sfr:
            case Region.Esfr: sfrs.Write16(a, value); return;
            case Region.Xram: SetWord(Xram, (int)(a - XramBase), value); return;
            case Region.Can: Can.Write16(a, value); return;
            case Region.Reserved:
                inventory.Note(AccessArea.Reserved, a, AccessKind.Write);
                return;
            default:
                var cs = (int)ChipSelects.Match(a)!;
                if (devices[cs] is { } device) device.Write16(a, value);
                else inventory.Note(WindowArea(cs), a, AccessKind.Write);
                return;
        }
    }

    public void Write8(uint a, byte value)
    {
        a &= 0xFFFFFF;
        DataWatch?.Invoke(a, true);
        switch (Decode(a))
        {
            case Region.Flash:
                log.Add((c, pc) => new FlashWriteEvent(c, pc, a, value, true));
                return;
            case Region.ExternalRam: ExternalRam[a & (uint)externalRamMask] = value; return;
            case Region.Iram: Iram[a - IramBase] = value; return;
            case Region.Sfr:
            case Region.Esfr: sfrs.Write8(a, value); return;
            case Region.Xram: Xram[a - XramBase] = value; return;
            case Region.Can: Can.Write8(a, value); return;
            case Region.Reserved:
                inventory.Note(AccessArea.Reserved, a, AccessKind.Write);
                return;
            default:
                var cs = (int)ChipSelects.Match(a)!;
                if (devices[cs] is { } device) device.Write8(a, value);
                else inventory.Note(WindowArea(cs), a, AccessKind.Write);
                return;
        }
    }

    /// <summary>Reads without side effects or inventory notes (reports, hashes, tests).</summary>
    public ushort Peek16(uint a)
    {
        a &= 0xFFFFFF;
        return Decode(a) switch
        {
            Region.Flash => Word(flash, (int)(a & 0x7FFFF)),
            Region.ExternalRam => Word(ExternalRam, (int)(a & (uint)externalRamMask)),
            Region.Iram => Word(Iram, (int)(a - IramBase)),
            Region.Sfr or Region.Esfr => sfrs.Peek(a),
            Region.Xram => Word(Xram, (int)(a - XramBase)),
            Region.Can => Can.Peek16(a),
            Region.Reserved => 0,
            _ => unmodelledReadValue,
        };
    }

    /// <summary>Writes without side effects; into flash it edits this machine's private copy.</summary>
    public void Poke16(uint a, ushort value)
    {
        a &= 0xFFFFFF;
        switch (Decode(a))
        {
            case Region.Flash: SetWord(flash, (int)(a & 0x7FFFF), value); break;
            case Region.ExternalRam: SetWord(ExternalRam, (int)(a & (uint)externalRamMask), value); break;
            case Region.Iram: SetWord(Iram, (int)(a - IramBase), value); break;
            case Region.Sfr or Region.Esfr: sfrs.Poke(a, value); break;
            case Region.Xram: SetWord(Xram, (int)(a - XramBase), value); break;
            case Region.Can: Can.Poke16(a, value); break;
        }
    }

    /// <summary>
    /// Bus states one 16-bit access to <paramref name="a"/> costs: the ACT of its chip select,
    /// 2 for XRAM and the CAN module [assumption: no waitstates], 0 for IRAM, SFRs and ESFRs.
    /// </summary>
    public int AccessStates(uint a) => Decode(a & 0xFFFFFF) switch
    {
        Region.Iram or Region.Sfr or Region.Esfr or Region.Reserved => 0,
        Region.Xram or Region.Can => 2,
        Region.Flash => ChipSelects.ActStates(0),
        Region.ExternalRam => ChipSelects.ActStates(2),
        Region.ChipSelect1 => ChipSelects.ActStates(1),
        Region.ChipSelect3 => ChipSelects.ActStates(3),
        _ => ChipSelects.ActStates(4),
    };

    ushort ReadWindow16(uint a, AccessKind kind)
    {
        var cs = (int)ChipSelects.Match(a)!;
        if (devices[cs] is { } device) return device.Read16(a);
        inventory.Note(WindowArea(cs), a, kind);
        return unmodelledReadValue;   // [assumption] a window with no device reads FFFFh
    }

    static AccessArea WindowArea(int cs) => cs switch
    {
        1 => AccessArea.ChipSelect1,
        2 => AccessArea.ChipSelect2,
        3 => AccessArea.ChipSelect3,
        _ => AccessArea.ChipSelect4,
    };

    static ushort Word(byte[] memory, int offset) => (ushort)(memory[offset] | (memory[offset + 1] << 8));

    static void SetWord(byte[] memory, int offset, ushort value)
    {
        memory[offset] = (byte)value;
        memory[offset + 1] = (byte)(value >> 8);
    }
}
