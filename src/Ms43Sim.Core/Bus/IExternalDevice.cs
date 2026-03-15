namespace Ms43Sim.Core;

/// <summary>A chip on one of the CS1/CS3/CS4 address windows. Plan 1 attaches none.</summary>
public interface IExternalDevice
{
    string Name { get; }

    ushort Read16(uint address);

    byte Read8(uint address);

    void Write16(uint address, ushort value);

    void Write8(uint address, byte value);
}
