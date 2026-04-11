namespace Ms43Sim.Core;

/// <summary>A device on the board's SSC lines: it sees SCLK edges while selected and may drive MRST.</summary>
public interface ISscSlave
{
    string Name { get; }

    bool IsSelected { get; }

    /// <summary>The level it drives on MRST now; null when it releases the line.</summary>
    bool? Miso { get; }

    /// <summary>One SCLK edge while selected; <paramref name="mosi"/> is the level on MTSR.</summary>
    void ClockEdge(bool rising, bool mosi);
}

/// <summary>The C167's SSC as an external master sees it when the SSC runs in slave mode.</summary>
public interface ISscPort
{
    /// <summary>One character clocked by an external master; returns the bits the SSC shifts out, right-aligned.</summary>
    ushort ExternalTransfer(ushort fromMaster);
}

/// <summary>The board's SSC lines (SCLK, MTSR, MRST) and the slaves on them.</summary>
public sealed class SscBus
{
    readonly List<ISscSlave> slaves = new();

    public IReadOnlyList<ISscSlave> Slaves => slaves;

    /// <summary>MRST: the AND of the levels the selected slaves drive; 1 when none drives [assumption: pull-up].</summary>
    public bool MrstLevel
    {
        get
        {
            foreach (var slave in slaves)
            {
                if (slave.IsSelected && slave.Miso == false)
                    return false;
            }
            return true;
        }
    }

    public void Attach(ISscSlave slave) => slaves.Add(slave);

    /// <summary>One SCLK edge, seen by every selected slave.</summary>
    public void ClockEdge(bool rising, bool mosi)
    {
        foreach (var slave in slaves)
        {
            if (slave.IsSelected)
                slave.ClockEdge(rising, mosi);
        }
    }
}
