namespace Ms43Sim.Core;

/// <summary>PSW bits (UM §5.1). ILVL is bits 15:12.</summary>
[Flags]
public enum PswFlags : ushort
{
    N = 0x0001,
    C = 0x0002,
    V = 0x0004,
    Z = 0x0008,
    E = 0x0010,
    MULIP = 0x0020,
    USR0 = 0x0040,
    HLDEN = 0x0400,
    IEN = 0x0800,
}
