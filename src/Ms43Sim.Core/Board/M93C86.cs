namespace Ms43Sim.Core;

/// <summary>
/// The M93C86 serial EEPROM in its x16 organisation: 1,024 words, Microwire instructions, on
/// the SSC with its chip select on output-latch bit 7 (active high) [obs]. The chip acts on
/// rising SCLK edges only. While its chip select is high it ignores 0 bits before the start bit,
/// then takes 2 opcode bits and A9…A0: READ (10), WRITE (01), ERASE (11), or 00 with A9A8 =
/// EWEN (11), EWDS (00), ERAL (10), WRAL (01). WRITE and WRAL then take 16 data bits, D15 first.
/// A READ drives a dummy 0 right after the edge that latched A0, then one data bit after each
/// following rising edge, D15 first, and continues into the next word while clocking continues.
/// WRITE, ERASE, ERAL and WRAL start their write cycle when the chip select falls, if writes are
/// enabled and the instruction is complete.
/// [assumption] The write cycle tW is 5 ms (120,000 cycles); the firmware tolerates about 11 ms.
/// [assumption] While the chip select is high and no instruction is being clocked, DO shows ready
/// (1) or busy (0); with the chip select low it releases the line, which then reads 1.
/// [assumption] The write-enable latch is cleared at power-on; a sequential READ continues into
/// the next word; instructions clocked during a write cycle are ignored; a power-on during a
/// write cycle abandons it.
/// </summary>
public sealed class M93C86 : ISscSlave
{
    public const int WordCount = 1024;
    public const int ImageBytes = 2 * WordCount;
    public const int WriteCycles = 120_000;

    enum State { Idle, Instruction, Data, Reading, Complete }

    enum Operation { None, Write, Erase, EraseAll, WriteAll }

    readonly Scheduler scheduler;
    readonly Func<bool> chipSelect;
    readonly ushort[] words = new ushort[WordCount];
    readonly HashSet<int> addressesRead = new();

    State state;
    Operation pending;
    int bits, count, address;
    ushort data;
    int readBit;            // -1: the dummy 0; 0-15: D15…D0 of the word at address
    bool? output;
    bool selected;
    long writeEvent = -1;

    public M93C86(Scheduler scheduler, Func<bool> chipSelect)
    {
        this.scheduler = scheduler;
        this.chipSelect = chipSelect;
        Array.Fill(words, (ushort)0xFFFF);
    }

    public string Name => "EEPROM (M93C86)";

    public bool IsSelected => chipSelect();

    public bool Busy { get; private set; }

    public bool WriteEnabled { get; private set; }

    /// <summary>Words fully shifted out by READs.</summary>
    public int WordsRead { get; private set; }

    /// <summary>The distinct addresses of the words fully shifted out by READs.</summary>
    public IReadOnlyCollection<int> AddressesRead => addressesRead;

    /// <summary>Completed write cycles, counted per word.</summary>
    public int WordsWritten { get; private set; }

    public bool? Miso
    {
        get
        {
            if (!IsSelected)
                return null;
            return state switch
            {
                State.Idle => !Busy,
                State.Reading => output,
                _ => null,
            };
        }
    }

    public ushort Word(int address) => words[address];

    /// <summary>Loads an image: word i little-endian at byte 2i. It must be exactly 2,048 bytes.</summary>
    public void Load(ReadOnlySpan<byte> image)
    {
        if (image.Length != ImageBytes)
            throw new InvalidDataException(
                $"An M93C86 EEPROM image must be 2,048 bytes (1,024 words); this one is {image.Length:N0} bytes.");
        for (var i = 0; i < WordCount; i++)
            words[i] = (ushort)(image[2 * i] | (image[2 * i + 1] << 8));
    }

    public byte[] ToImage()
    {
        var image = new byte[ImageBytes];
        for (var i = 0; i < WordCount; i++)
        {
            image[2 * i] = (byte)words[i];
            image[2 * i + 1] = (byte)(words[i] >> 8);
        }
        return image;
    }

    /// <summary>Clears the write-enable latch and the instruction decoder; the words stay.</summary>
    public void PowerOn()
    {
        if (writeEvent >= 0)
            scheduler.Cancel(writeEvent);
        writeEvent = -1;
        Busy = false;
        WriteEnabled = false;
        selected = chipSelect();
        Restart();
    }

    /// <summary>The board calls this on every output-latch change.</summary>
    public void ChipSelectChanged()
    {
        var now = chipSelect();
        if (now == selected)
            return;
        selected = now;
        if (!now && state == State.Complete && pending != Operation.None && WriteEnabled)
            StartWriteCycle();
        Restart();
    }

    public void ClockEdge(bool rising, bool mosi)
    {
        if (!rising || Busy)
            return;
        switch (state)
        {
            case State.Idle:
                if (mosi)
                {
                    state = State.Instruction;
                    bits = count = 0;
                }
                return;
            case State.Instruction:
                bits = (bits << 1) | (mosi ? 1 : 0);
                if (++count == 12)
                    Decode();
                return;
            case State.Data:
                data = (ushort)((data << 1) | (mosi ? 1 : 0));
                if (++count == 16)
                    state = State.Complete;
                return;
            case State.Reading:
                NextReadBit();
                return;
        }
    }

    void Decode()
    {
        address = bits & 0x3FF;
        switch (bits >> 10)
        {
            case 0b10:
                state = State.Reading;
                readBit = -1;
                output = false;     // the dummy 0
                return;
            case 0b01:
                Expect(Operation.Write, State.Data);
                return;
            case 0b11:
                Expect(Operation.Erase, State.Complete);
                return;
        }
        switch (address >> 8)
        {
            case 0b11: WriteEnabled = true; state = State.Complete; return;
            case 0b00: WriteEnabled = false; state = State.Complete; return;
            case 0b10: Expect(Operation.EraseAll, State.Complete); return;
            default: Expect(Operation.WriteAll, State.Data); return;
        }
    }

    void Expect(Operation operation, State next)
    {
        pending = operation;
        state = next;
        count = 0;
    }

    void NextReadBit()
    {
        if (++readBit == 16)
        {
            address = (address + 1) & (WordCount - 1);
            readBit = 0;
        }
        output = (words[address] & (0x8000 >> readBit)) != 0;
        if (readBit == 15)
        {
            WordsRead++;
            addressesRead.Add(address);
        }
    }

    void StartWriteCycle()
    {
        var operation = pending;
        var target = address;
        var value = data;
        Busy = true;
        writeEvent = scheduler.Schedule(scheduler.Now + WriteCycles, "EEPROM write cycle end", () =>
        {
            writeEvent = -1;
            Busy = false;
            switch (operation)
            {
                case Operation.Write: words[target] = value; WordsWritten++; break;
                case Operation.Erase: words[target] = 0xFFFF; WordsWritten++; break;
                case Operation.EraseAll: Array.Fill(words, (ushort)0xFFFF); WordsWritten += WordCount; break;
                case Operation.WriteAll: Array.Fill(words, value); WordsWritten += WordCount; break;
            }
        });
    }

    void Restart()
    {
        state = State.Idle;
        pending = Operation.None;
        output = null;
        bits = count = 0;
    }
}
