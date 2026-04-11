namespace Ms43Sim.Core;

/// <summary>
/// The write-only output latch on CS3 at 0x080000 [obs]. The firmware writes it with EXTP #020h
/// from the shadow FDCA and never reads it. Bits: 0-2 select the external analog multiplexers,
/// 3 chip B chip select (active low), 4 chip A chip select (active low), 5 main-relay hold,
/// 6 monitoring-module chip select (active low), 7 EEPROM chip select (active high).
/// [assumption] The latch sits on D0-D7: a word write or a byte write to the even address loads
/// it, a byte write to the odd address does not. A read gives FFFFh and is reported.
/// [assumption] It powers up as 78h, every chip select inactive; the boot writes 78h at 00:2DA0
/// before any SSC transfer, so the firmware cannot tell.
/// </summary>
public sealed class OutputLatch : IExternalDevice
{
    const byte PowerOnValue = 0x78;

    readonly EventLog log;

    public OutputLatch(EventLog log)
    {
        this.log = log;
        Value = PowerOnValue;
    }

    public string Name => "output latch (CS3)";

    public byte Value { get; private set; }

    public int MuxSelect => Value & 0x07;

    public bool ChipBSelected => (Value & 0x08) == 0;

    public bool ChipASelected => (Value & 0x10) == 0;

    public bool MainRelayHeld => (Value & 0x20) != 0;

    public bool MonitorSelected => (Value & 0x40) == 0;

    public bool EepromSelected => (Value & 0x80) != 0;

    /// <summary>(old, new) each time a write changes the value.</summary>
    public event Action<byte, byte>? Changed;

    /// <summary>Back to the power-up value, without a <see cref="Changed"/> event: every device powers up with it.</summary>
    public void PowerOn() => Value = PowerOnValue;

    public ushort Read16(uint address)
    {
        log.Add((c, pc) => new UnsupportedEvent(c, pc, $"read of the write-only latch at 0x{address:X6}"));
        return 0xFFFF;
    }

    public byte Read8(uint address) => (byte)(Read16(address & ~1u) >> (int)(8 * (address & 1)));

    public void Write16(uint address, ushort value) => Load((byte)value);

    public void Write8(uint address, byte value)
    {
        if ((address & 1) == 0)
            Load(value);
    }

    void Load(byte value)
    {
        var before = Value;
        Value = value;
        if (value != before)
            Changed?.Invoke(before, value);
    }
}
