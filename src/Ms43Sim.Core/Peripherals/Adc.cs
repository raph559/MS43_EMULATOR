namespace Ms43Sim.Core;

/// <summary>
/// The 10-bit A/D converter (UM §17) in fixed-channel single-conversion mode, the only mode the car
/// uses [obs]. ADCON: bits 3-0 ADCH, 5-4 ADM, 7 ADST, 8 ADBSY (read-only), 9 ADWR, 10 ADCIN,
/// 11 ADCRQ, 13-12 ADSTC, 15-14 ADCTC. ADST going 0 → 1 samples channel ADCH (aborting a running
/// conversion) and sets ADBSY; setting ADST while it is already 1 does nothing, and clearing it
/// lets the running conversion finish. A conversion takes tS + 40·tBC + 2 cycles, tBC = 4, 2, 16
/// or 8 cycles for ADCTC = 00…11 and tS = tBC × 8, 16, 32 or 64 for ADSTC = 00…11. At its end
/// ADDAT = CHNR/ADRES, ADCIR is set, ADBSY and ADST clear; a result that overwrites an unread
/// ADDAT sets ADEIR.
/// Not modelled, each reported once: ADM ≠ 00, ADWR = 1 and ADCIN = 1 (the conversion then runs
/// as a single conversion); ADDAT2 is plain storage; ADCRQ from CC31 is ignored.
/// </summary>
public sealed class Adc
{
    const uint AdconAddress = 0xFFA0, AddatAddress = 0xFEA0, Addat2Address = 0xF0A0;
    const ushort Adst = 0x0080, Adbsy = 0x0100, Adwr = 0x0200, Adcin = 0x0400;
    static readonly int[] ConversionClock = { 4, 2, 16, 8 };
    static readonly int[] SampleClocks = { 8, 16, 32, 64 };

    readonly Scheduler scheduler;
    readonly InterruptController interrupts;
    readonly Func<int, double> volts;
    readonly EventLog log;
    readonly InterruptSource end = InterruptSources.ByName("ADC");
    readonly InterruptSource overrun = InterruptSources.ByName("ADE");
    readonly HashSet<string> reported = new();

    ushort adcon, addat;
    bool unread;
    int sampledCode, sampledChannel;
    long endEvent = -1;

    public Adc(Scheduler scheduler, SfrFile sfrs, InterruptController interrupts, Func<int, double> volts, EventLog log)
    {
        this.scheduler = scheduler;
        this.interrupts = interrupts;
        this.volts = volts;
        this.log = log;
        sfrs.Register(AdconAddress, "adc", ReadAdcon, WriteAdcon, ReadAdcon);
        sfrs.Register(AddatAddress, "adc", ReadAddat, v => addat = v, () => addat);
        sfrs.Register(Addat2Address, "adc", null, null);
        OnReset();
    }

    /// <summary>Conversions completed since power-on.</summary>
    public long Conversions { get; private set; }

    public void OnReset()
    {
        if (endEvent >= 0)
            scheduler.Cancel(endEvent);
        endEvent = -1;
        adcon = addat = 0;
        unread = false;
    }

    ushort ReadAdcon() => (ushort)(adcon | (endEvent >= 0 ? Adbsy : 0));

    ushort ReadAddat()
    {
        unread = false;
        return addat;
    }

    void WriteAdcon(ushort value)
    {
        var starts = (adcon & Adst) == 0 && (value & Adst) != 0;
        adcon = (ushort)(value & ~Adbsy);
        if ((value & 0x0030) != 0)
            ReportOnce($"ADC mode ADM = {(value >> 4) & 3} (only fixed-channel single conversion is modelled; it runs as one)");
        if ((value & Adwr) != 0)
            ReportOnce("ADC wait-for-ADDAT-read mode (ADWR = 1) is not modelled");
        if ((value & Adcin) != 0)
            ReportOnce("ADC channel injection (ADCIN = 1) is not modelled");
        if (starts)
            Start();
    }

    /// <summary>Samples the channel's voltage now; the result comes after the conversion time.</summary>
    void Start()
    {
        if (endEvent >= 0)
            scheduler.Cancel(endEvent);
        sampledChannel = adcon & 0xF;
        sampledCode = BoardInputs.AdcCode(volts(sampledChannel));
        var tbc = ConversionClock[(adcon >> 14) & 3];
        var ts = tbc * SampleClocks[(adcon >> 12) & 3];
        endEvent = scheduler.Schedule(scheduler.Now + (ulong)(ts + 40 * tbc + 2), "ADC conversion end", End);
    }

    void End()
    {
        endEvent = -1;
        if (unread)
            interrupts.Request(overrun);
        addat = (ushort)((sampledChannel << 12) | sampledCode);
        unread = true;
        Conversions++;
        adcon &= unchecked((ushort)~Adst);
        interrupts.Request(end);
    }

    void ReportOnce(string what)
    {
        if (reported.Add(what))
            log.Add((c, pc) => new UnsupportedEvent(c, pc, what));
    }
}
