namespace Ms43Sim.Core;

/// <summary>
/// The monitoring module on SSC job 3 (output-latch bit 6 low), modelled from the protocol the
/// firmware implements [obs; answers ver]. The module is the SPI master: when its chip select
/// falls it builds a 9-byte frame and clocks it through the CPU's SSC, byte 8 being the XOR of
/// bytes 0-7. Exchange 1 after a CPU reset is 12h, exchange 2 is CFh, every later one is an ADh
/// question: q = (k − 2) mod 28h, a toggle byte whose bits 1-5 invert at every 8th ADh frame,
/// its error counter C, and its own reading of ADC channel 6 (code >> 2). After byte 9 it checks
/// the CPU's XOR and frame type (3Bh frames, the CPU's after-run shutdown or degraded state, are
/// noted once) and, in an ADh exchange, the answer to the question it asked one
/// exchange earlier (the CFh frame counts as question 0): C rises by 1 for a wrong answer and
/// falls by 1 otherwise, never below 0. A wrong answer that is not the question's seed (the
/// CPU's deliberate wrong answer), a bad XOR and an unexpected type are reported. The module's
/// own reaction (cutting the throttle power stage) is not modelled.
/// [assumption] The "xx" bytes are 00h. Timing: first clock edge 20 µs (480 cycles) after the
/// chip select falls, 250 kbit/s (768 cycles per byte), 4 µs (96 cycles) between bytes; an
/// exchange aborts if the chip select rises early.
/// </summary>
public sealed class MonitoringModule
{
    const ulong FirstByteEnd = 480 + 768, ByteSpacing = 768 + 96;
    const int FrameBytes = 9, Questions = 0x28;
    const byte Hello = 0x12, Confirm = 0xCF, Question = 0xAD, Shutdown = 0x3B, ToggleBits = 0x3E;

    /// <summary>The seed of question q is entry q >> 2 (0E:E4D2).</summary>
    static readonly ushort[] Seeds =
        { 0x3E80, 0xFFFF, 0x2839, 0x8000, 0x155C, 0xCBA5, 0x0000, 0x9376, 0x4791, 0xDFDB };

    /// <summary>Expected answers by seed (q >> 2) and routine (q &amp; 3): the firmware's own routines, run in the plan-1 core.</summary>
    static readonly ushort[,] Answers =
    {
        { 0xB246, 0x09BE, 0xE037, 0x7F1B },
        { 0x6A1C, 0xC302, 0x7AA7, 0x0119 },
        { 0x16FE, 0x1185, 0x8A0E, 0x8ABE },
        { 0x32C0, 0xCDDC, 0x821A, 0x4F66 },
        { 0x82EB, 0xDB6B, 0xA80F, 0x5819 },
        { 0x6A0C, 0x83CF, 0x600C, 0x6E47 },
        { 0xEB28, 0xBF2B, 0x0C2F, 0x033A },
        { 0xACCD, 0x1016, 0x9FBD, 0x54FF },
        { 0xD07E, 0x7407, 0x85F1, 0xA2BE },
        { 0x81DD, 0x9F6A, 0x1426, 0x9BDD },
    };

    readonly Scheduler scheduler;
    readonly ISscPort port;
    readonly Func<bool> chipSelect;
    readonly Func<int> adcChannel6Code;
    readonly EventLog log;
    readonly byte[] sending = new byte[FrameBytes];
    readonly byte[] received = new byte[FrameBytes];

    bool selected;
    bool shutdownSeen;
    int byteIndex;
    long byteEvent = -1;

    public MonitoringModule(Scheduler scheduler, ISscPort port, Func<bool> chipSelect, Func<int> adcChannel6Code, EventLog log)
    {
        this.scheduler = scheduler;
        this.port = port;
        this.chipSelect = chipSelect;
        this.adcChannel6Code = adcChannel6Code;
        this.log = log;
    }

    /// <summary>Completed exchanges since the last CPU reset.</summary>
    public int ExchangesSinceReset { get; private set; }

    /// <summary>Completed exchanges since power-on.</summary>
    public int Exchanges { get; private set; }

    /// <summary>[inf] The module releases chip B once it has sent its first ADh frame.</summary>
    public bool ShutOffReleased => ExchangesSinceReset >= 3;

    /// <summary>C, the byte the module sends in each ADh frame.</summary>
    public int ErrorCounter { get; private set; }

    /// <summary>Answers that differed from the expected answer, deliberate ones included.</summary>
    public int WrongAnswers { get; private set; }

    /// <summary>Wrong answers that were not the seed, bad XORs and unexpected frame types.</summary>
    public int UnexpectedAnswers { get; private set; }

    public static ushort ExpectedAnswer(int question) => Answers[question >> 2, question & 3];

    public static ushort Seed(int question) => Seeds[question >> 2];

    /// <summary>The board calls this on every output-latch change.</summary>
    public void ChipSelectChanged()
    {
        var now = chipSelect();
        if (now == selected)
            return;
        selected = now;
        if (now)
        {
            BuildFrame();
            byteIndex = 0;
            byteEvent = scheduler.Schedule(scheduler.Now + FirstByteEnd, "monitoring module byte", TransferByte);
        }
        else if (byteEvent >= 0)
        {
            var sent = byteIndex;
            Cancel();
            log.Add((c, pc) => new MonitoringEvent(c, pc, $"exchange aborted: the chip select rose after {sent} of 9 bytes"));
        }
    }

    /// <summary>After every CPU reset the protocol restarts at 12h [obs], and the module with it [inf].</summary>
    public void OnCpuReset()
    {
        Cancel();
        ExchangesSinceReset = 0;
        ErrorCounter = 0;
        shutdownSeen = false;
        selected = chipSelect();
    }

    public void PowerOn()
    {
        OnCpuReset();
        Exchanges = WrongAnswers = UnexpectedAnswers = 0;
    }

    void BuildFrame()
    {
        Array.Clear(sending);
        var k = ExchangesSinceReset + 1;
        var m = (byte)(Math.Clamp(adcChannel6Code(), 0, 1023) >> 2);
        if (k == 1)
        {
            sending[0] = Hello;
            sending[2] = 0x32;
        }
        else if (k == 2)
        {
            sending[0] = Confirm;
            sending[4] = m;
        }
        else
        {
            var adhFrame = k - 2;
            sending[0] = Question;
            sending[1] = (byte)((k - 2) % Questions);
            sending[2] = (adhFrame / 8 & 1) != 0 ? ToggleBits : (byte)0;
            sending[3] = (byte)Math.Min(ErrorCounter, 0xFF);
            sending[4] = m;
        }
        sending[8] = Xor(sending);
    }

    void TransferByte()
    {
        byteEvent = -1;
        received[byteIndex] = (byte)port.ExternalTransfer(sending[byteIndex]);
        if (++byteIndex < FrameBytes)
            byteEvent = scheduler.Schedule(scheduler.Now + ByteSpacing, "monitoring module byte", TransferByte);
        else
            Complete();
    }

    void Complete()
    {
        var k = ExchangesSinceReset + 1;
        var expectedType = k switch { 1 => Hello, 2 => Confirm, _ => Question };
        var xorOk = Xor(received) == received[8];
        if (!xorOk)
            Report($"CPU frame {received[0]:X2}h with XOR {received[8]:X2}h; its bytes give {Xor(received):X2}h");
        if (received[0] == Shutdown && expectedType == Question)
            NoteShutdown();
        else if (received[0] != expectedType)
            Report($"unexpected CPU frame type {received[0]:X2}h in exchange {k} (expected {expectedType:X2}h)");
        else if (expectedType == Question && xorOk)
            CheckAnswer((k - 3) % Questions, (ushort)(received[1] << 8 | received[2]));
        ExchangesSinceReset++;
        Exchanges++;
    }

    void CheckAnswer(int question, ushort answer)
    {
        var expected = ExpectedAnswer(question);
        if (answer == expected)
        {
            ErrorCounter = Math.Max(0, ErrorCounter - 1);
            return;
        }
        ErrorCounter++;
        WrongAnswers++;
        var seed = Seed(question);
        if (answer != seed)
            Report($"unexpected answer {answer:X4} to question {question:X2} (expected {expected:X4}, seed {seed:X4})");
    }

    /// <summary>
    /// The CPU's 3Bh frames in the question phase: its after-run shutdown handshake (0E:1156 sets
    /// E027 = 3Bh; F8F8 lets the EEPROM write go once enough have been sent) or its degraded state.
    /// Not an error: noted once, not counted, and no answer is checked.
    /// </summary>
    void NoteShutdown()
    {
        if (shutdownSeen)
            return;
        shutdownSeen = true;
        log.Add((c, pc) => new MonitoringEvent(c, pc, "the CPU sends 3Bh frames: its after-run shutdown or its degraded state"));
    }

    void Report(string what)
    {
        UnexpectedAnswers++;
        log.Add((c, pc) => new MonitoringEvent(c, pc, what));
    }

    void Cancel()
    {
        if (byteEvent >= 0)
            scheduler.Cancel(byteEvent);
        byteEvent = -1;
    }

    /// <summary>The XOR of bytes 0-7.</summary>
    static byte Xor(byte[] frame)
    {
        byte x = 0;
        for (var i = 0; i < 8; i++)
            x ^= frame[i];
        return x;
    }
}
