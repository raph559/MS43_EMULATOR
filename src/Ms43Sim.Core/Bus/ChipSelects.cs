namespace Ms43Sim.Core;

/// <summary>
/// The four programmable address windows (ADDRSEL1-4 with BUSCON1-4) and the bus timing of
/// each chip select (UM §9). A window is active only when its BUSCONx.BUSACT bit is set.
/// Priority: window 2 before 1, window 4 before 3 (UM "Address Window Arbitration").
/// </summary>
public sealed class ChipSelects
{
    static readonly uint[] AddrSel = { 0, 0xFE18, 0xFE1A, 0xFE1C, 0xFE1E };
    static readonly uint[] BusCon = { 0xFF0C, 0xFF14, 0xFF16, 0xFF18, 0xFF1A };
    static readonly int[] MatchOrder = { 2, 4, 1, 3 };

    readonly SfrFile sfrs;
    readonly uint[] start = new uint[5], size = new uint[5];
    readonly bool[] active = new bool[5];
    readonly int[] act = new int[5];

    public ChipSelects(SfrFile sfrs)
    {
        this.sfrs = sfrs;
        for (var cs = 0; cs <= 4; cs++)
        {
            var index = cs;   // a for-loop variable is shared by every closure; copy it
            if (index > 0)
                sfrs.Register(AddrSel[index], "bus", null, v => { sfrs.Poke(AddrSel[index], v); Recompute(); });
            sfrs.Register(BusCon[index], "bus", null, v => { sfrs.Poke(BusCon[index], v); Recompute(); });
        }
        Recompute();
    }

    /// <summary>Re-reads every ADDRSELx/BUSCONx. Call after the SFRs are reset.</summary>
    public void Recompute()
    {
        for (var cs = 0; cs <= 4; cs++)
        {
            var buscon = sfrs.Peek(BusCon[cs]);
            act[cs] = ActFor(buscon);
            if (cs == 0)
                continue;
            var addrsel = sfrs.Peek(AddrSel[cs]);
            var rgsz = addrsel & 0xF;
            active[cs] = (buscon & 0x0400) != 0 && rgsz <= 11;
            size[cs] = 0x1000u << Math.Min(rgsz, 11);
            start[cs] = ((uint)(addrsel >> 4) << 12) & ~(size[cs] - 1);
        }
    }

    /// <summary>The window serving a physical address: 1-4, or null for BUSCON0 (the flash).</summary>
    public int? Match(uint address)
    {
        foreach (var cs in MatchOrder)
        {
            if (active[cs] && address - start[cs] < size[cs])
                return cs;
        }
        return null;
    }

    public uint WindowStart(int cs) => start[cs];

    /// <summary>States per 16-bit access on chip select <paramref name="cs"/> (ISM §7.1 ACT).</summary>
    public int ActStates(int cs) => act[cs];

    static int ActFor(ushort buscon)
    {
        var mctc = buscon & 0xF;
        var mttc = (buscon >> 5) & 1;
        var btyp = (buscon >> 6) & 3;          // 00 8-bit demux, 01 8-bit mux, 10 16-bit demux, 11 16-bit mux
        var multiplexed = (btyp & 1) == 1;
        var states = (multiplexed ? 3 : 2) + (15 - mctc) + (1 - mttc);
        return btyp < 2 ? 2 * states : states;  // a word over an 8-bit bus takes two cycles
    }
}
