namespace Ms43Sim.Core;

/// <summary>
/// The car's other CAN nodes as the ECU hears them (plan 3, Reference facts; owner: an ASC car).
/// ASC1 153h every 10 ms (no intervention, 0 km/h, speed valid), the cluster's INSTR2 613h
/// (odometer 100,000 km, fuel level 40, clock 60 min, tank sensors 20/20) and INSTR3 615h (A/C and
/// lights off, no fan request, +20 °C) every 200 ms; standard identifiers, DLC 8. Never 43Fh
/// (gearbox), 43Dh (SMG), 1F3h (DSC) or 1F5h (steering angle): one such frame makes the firmware
/// expect that partner [obs].
/// [assumption] The first frames go 100, 105 and 110 ms after <see cref="Start"/>; a frame is
/// delivered only while the key is on, and the timers keep running either way. Times assume
/// fCPU = 24 MHz.
/// </summary>
public sealed class CanPartners
{
    public static readonly CanFrame Asc1 = new(0x153, false, new byte[] { 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x00 });
    public static readonly CanFrame Instr2 = new(0x613, false, new byte[] { 0x10, 0x27, 0x28, 0x3C, 0x00, 0x14, 0x05, 0x00 });
    public static readonly CanFrame Instr3 = new(0x615, false, new byte[] { 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0x00 });

    const ulong CyclesPerMs = 24_000;

    readonly Scheduler scheduler;
    readonly Action<CanFrame> deliver;
    readonly Func<bool> keyOn;
    readonly Dictionary<uint, long> sent = new();

    public CanPartners(Scheduler scheduler, Action<CanFrame> deliver, Func<bool> keyOn)
    {
        this.scheduler = scheduler;
        this.deliver = deliver;
        this.keyOn = keyOn;
    }

    /// <summary>Frames delivered, per identifier.</summary>
    public IReadOnlyDictionary<uint, long> Sent => sent;

    /// <summary>The first frames 100, 105 and 110 ms from now; then every 10, 200 and 200 ms.</summary>
    public void Start()
    {
        Every(Asc1, 100, 10);
        Every(Instr2, 105, 200);
        Every(Instr3, 110, 200);
    }

    void Every(CanFrame frame, ulong firstMs, ulong periodMs)
    {
        var period = periodMs * CyclesPerMs;
        void At(ulong at) => scheduler.Schedule(at, $"CAN partner {frame.Id:X3}h", () =>
        {
            if (keyOn())
            {
                sent[frame.Id] = sent.GetValueOrDefault(frame.Id) + 1;
                deliver(frame with { Data = frame.Data.ToArray() });
            }
            At(at + period);
        });
        At(scheduler.Now + firstMs * CyclesPerMs);
    }
}
