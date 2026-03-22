using System.Numerics;

namespace Ms43Sim.Core;

/// <summary>
/// The 56 xxIC registers and their arbitration (UM §5.1). xxIC: GLVL = bits 1:0, ILVL = bits
/// 5:2, IE = bit 6, IR = bit 7; bits 15:8 read 0 and ignore writes. A bitmask of the
/// requesting and enabled nodes keeps arbitration free when nothing is pending.
/// </summary>
public sealed class InterruptController
{
    const uint PeccBase = 0xFEC0;

    readonly byte[] ic = new byte[InterruptSources.All.Count];
    ulong enabledRequests;

    public InterruptController(SfrFile sfrs, PhysicalBus bus)
    {
        foreach (var source in InterruptSources.All)
        {
            var index = source.Index;
            sfrs.Register(source.IcAddress, "interrupts", () => ic[index], v => Write(index, (byte)v), () => ic[index]);
        }
        for (uint channel = 0; channel < 8; channel++)
            sfrs.Register(PeccBase + 2 * channel, "pec", null, null);   // PECCx: plain modelled storage
    }

    /// <summary>A request is pending and individually enabled (IR and IE), at any level: this wakes IDLE.</summary>
    public bool AnyEnabledRequest => enabledRequests != 0;

    /// <summary>What a peripheral uses to raise its node: the same as setting xxIR.</summary>
    public void Request(InterruptSource source)
    {
        ic[source.Index] |= 0x80;
        Update(source.Index);
    }

    public void Reset()
    {
        Array.Clear(ic);
        enabledRequests = 0;
    }

    /// <summary>The highest ILVL, then the highest GLVL, among the enabled requests above <paramref name="cpuLevel"/>.</summary>
    internal bool TryArbitrate(int cpuLevel, out int winner, out int level, out int group)
    {
        winner = -1;
        level = group = -1;
        for (var pending = enabledRequests; pending != 0; pending &= pending - 1)
        {
            var i = BitOperations.TrailingZeroCount(pending);
            var l = (ic[i] >> 2) & 0xF;
            var g = ic[i] & 3;
            if (l > level || (l == level && g > group))
            {
                winner = i;
                level = l;
                group = g;
            }
        }
        return winner >= 0 && level > cpuLevel;
    }

    internal void ClearRequest(int index)
    {
        ic[index] &= 0x7F;
        Update(index);
    }

    void Write(int index, byte value)
    {
        ic[index] = value;
        Update(index);
    }

    void Update(int index)
    {
        if ((ic[index] & 0xC0) == 0xC0)
            enabledRequests |= 1UL << index;
        else
            enabledRequests &= ~(1UL << index);
    }
}
