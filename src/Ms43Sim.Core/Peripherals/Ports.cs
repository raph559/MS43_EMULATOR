using System.Numerics;

namespace Ms43Sim.Core;

/// <summary>What drives the pins from outside the chip: the board.</summary>
public interface IPinEnvironment
{
    /// <summary>The level on an input pin, or on an open-drain output whose latch releases it.</summary>
    bool InputLevel(int port, int bit);
}

/// <summary>
/// The parallel ports P2-P8 (UM §7). Px is the data latch, DPx the direction (1 = output) and
/// ODPx the open-drain select (ports 2, 3, 6, 7 and 8). A read gives the pin for an input and the
/// latch for an output; a write always loads the latch, so a read-modify-write copies input pins
/// into the latch (UM p. 7-2). P3 has no bit 14; P4, P6, P7 and P8 are 8 bits wide. P5 is
/// input-only. P5DIDIS and PICON are only stored.
/// Reads ask the environment for the live level. <see cref="PinChanged"/> fires on every change of
/// a pin's level from a latch, direction or open-drain write, or from an input change the
/// environment reports through <see cref="InputChanged"/>.
/// </summary>
public sealed class Ports
{
    // Indexed by port number; ports 0 and 1 carry the external bus and are not modelled here.
    static readonly ushort[] Mask = { 0, 0, 0xFFFF, 0xBFFF, 0x00FF, 0xFFFF, 0x00FF, 0x00FF, 0x00FF };
    static readonly uint[] DataAddress = { 0, 0, 0xFFC0, 0xFFC4, 0xFFC8, 0xFFA2, 0xFFCC, 0xFFD0, 0xFFD4 };
    static readonly uint[] DirectionAddress = { 0, 0, 0xFFC2, 0xFFC6, 0xFFCA, 0, 0xFFCE, 0xFFD2, 0xFFD6 };
    static readonly uint[] OpenDrainAddress = { 0, 0, 0xF1C2, 0xF1C6, 0, 0, 0xF1CE, 0xF1D2, 0xF1D6 };
    const uint P5didisAddress = 0xFFA4, PiconAddress = 0xF1C4;
    const int InputOnlyPort = 5;

    readonly IPinEnvironment environment;
    readonly ushort[] latch = new ushort[9], direction = new ushort[9], openDrain = new ushort[9];
    readonly ushort[] notified = new ushort[9];   // the pin levels PinChanged last reported

    public Ports(SfrFile sfrs, IPinEnvironment environment)
    {
        this.environment = environment;
        for (var port = 2; port <= 8; port++)
        {
            var p = port;
            if (p == InputOnlyPort)
            {
                sfrs.Register(DataAddress[p], "ports", () => Pins(p), _ => { }, () => Pins(p));   // P5 is read-only
                continue;
            }
            sfrs.Register(DataAddress[p], "ports", () => Read(p), v => Set(latch, p, v), () => Read(p));
            sfrs.Register(DirectionAddress[p], "ports", () => direction[p], v => Set(direction, p, v), () => direction[p]);
            if (OpenDrainAddress[p] != 0)
                sfrs.Register(OpenDrainAddress[p], "ports", () => openDrain[p], v => Set(openDrain, p, v), () => openDrain[p]);
        }
        sfrs.Register(P5didisAddress, "ports", null, null);
        sfrs.Register(PiconAddress, "ports", null, null);
        OnReset();
    }

    /// <summary>(port, bit, new level) for every pin whose level changes.</summary>
    public event Action<int, int, bool>? PinChanged;

    public ushort Latch(int port) => latch[port];

    public bool IsOutput(int port, int bit) => (direction[port] & (1 << bit)) != 0;

    /// <summary>
    /// The level on the pin: the latch for an output, the environment's level for an input or for
    /// an open-drain output whose latch is 1 (released).
    /// </summary>
    public bool Pin(int port, int bit)
    {
        var m = 1 << bit;
        var driven = (direction[port] & m) != 0 && !((openDrain[port] & m) != 0 && (latch[port] & m) != 0);
        return driven ? (latch[port] & m) != 0 : environment.InputLevel(port, bit);
    }

    /// <summary>A CAPCOM compare output sets or clears the port latch (UM §15.5).</summary>
    public void SetLatchBit(int port, int bit, bool level)
    {
        var m = (ushort)(1 << bit);
        Set(latch, port, level ? (ushort)(latch[port] | m) : (ushort)(latch[port] & ~m));
    }

    public void ToggleLatchBit(int port, int bit) => Set(latch, port, (ushort)(latch[port] ^ (1 << bit)));

    /// <summary>The environment changed the level it puts on an input.</summary>
    public void InputChanged(int port, int bit) => Update(port);

    /// <summary>Every latch, direction and open-drain bit to 0 (UM §7). The pins follow without events.</summary>
    public void OnReset()
    {
        Array.Clear(latch);
        Array.Clear(direction);
        Array.Clear(openDrain);
        for (var port = 2; port <= 8; port++)
            notified[port] = Pins(port);
    }

    /// <summary>The latch for outputs, the pin for inputs.</summary>
    ushort Read(int port)
    {
        var value = latch[port] & direction[port];
        var inputs = Mask[port] & ~direction[port];
        for (; inputs != 0; inputs &= inputs - 1)
        {
            var bit = BitOperations.TrailingZeroCount(inputs);
            if (environment.InputLevel(port, bit))
                value |= 1 << bit;
        }
        return (ushort)value;
    }

    void Set(ushort[] register, int port, ushort value)
    {
        register[port] = (ushort)(value & Mask[port]);
        Update(port);
    }

    void Update(int port)
    {
        var now = Pins(port);
        var changed = notified[port] ^ now;
        notified[port] = now;
        for (; changed != 0; changed &= changed - 1)
        {
            var bit = BitOperations.TrailingZeroCount(changed);
            PinChanged?.Invoke(port, bit, (now & (1 << bit)) != 0);
        }
    }

    ushort Pins(int port)
    {
        var value = 0;
        for (int bits = Mask[port]; bits != 0; bits &= bits - 1)
        {
            var bit = BitOperations.TrailingZeroCount(bits);
            if (Pin(port, bit))
                value |= 1 << bit;
        }
        return (ushort)value;
    }
}
