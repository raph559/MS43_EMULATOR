namespace Ms43Sim.Core;

/// <summary>
/// The car's other CAN nodes as the ECU hears them (plan 3, Reference facts; owner: an ASC car).
/// ASC1 153h every 10 ms (no intervention, speed valid), the cluster's INSTR2 613h (odometer
/// 100,000 km, fuel level 40, tank sensors 20/20) and INSTR3 615h (lights off, no fan request) every
/// 200 ms; standard identifiers, DLC 8. Never 43Fh (gearbox), 43Dh (SMG), 1F3h (DSC) or 1F5h
/// (steering angle): one such frame makes the firmware expect that partner [obs].
/// What moves comes from the car's state as each frame goes (plan 4 R9): 153h bytes 1–2 the road
/// speed, round(16 × km/h) with byte 1 = (raw &amp; 1Fh) &lt;&lt; 3 and byte 2 = raw &gt;&gt; 5; 613h bytes
/// 3–4 the clock, minutes, little-endian; 615h byte 0 C0h with the A/C on (bit 7 the request, bit 6
/// the second A/C bit: the relay comes on with both) and byte 3 the ambient, sign-magnitude °C. The
/// default state gives plan 3's frames. A silenced identifier is not sent.
/// [assumption] The first frames go 100, 105 and 110 ms after <see cref="Start"/>; a frame is
/// delivered only while the key is on, and the timers keep running either way. Times assume
/// fCPU = 24 MHz.
/// </summary>
public sealed class CanPartners
{
    const ulong CyclesPerMs = 24_000;

    readonly Scheduler scheduler;
    readonly Action<CanFrame> deliver;
    readonly Func<bool> keyOn;
    readonly Func<CarState> state;
    readonly Dictionary<uint, long> sent = new();

    /// <summary>A null <paramref name="state"/> sends <see cref="CarState.Default"/>: plan 3's healthy frames.</summary>
    public CanPartners(Scheduler scheduler, Action<CanFrame> deliver, Func<bool> keyOn, Func<CarState>? state = null)
    {
        this.scheduler = scheduler;
        this.deliver = deliver;
        this.keyOn = keyOn;
        this.state = state ?? (() => CarState.Default);
    }

    /// <summary>Frames delivered, per identifier.</summary>
    public IReadOnlyDictionary<uint, long> Sent => sent;

    public static CanFrame Asc1(CarState car)
    {
        var raw = (int)Math.Round(16 * Math.Max(0, car.SpeedKmh), MidpointRounding.AwayFromZero);
        return new CanFrame(0x153, false, new byte[] { 0x00, (byte)((raw & 0x1F) << 3), (byte)(raw >> 5), 0xFF, 0x00, 0x00, 0xFF, 0x00 });
    }

    public static CanFrame Instr2(CarState car)
    {
        var minutes = (int)Math.Round(car.ClockMinutes, MidpointRounding.AwayFromZero);
        return new CanFrame(0x613, false, new byte[] { 0x10, 0x27, 0x28, (byte)minutes, (byte)(minutes >> 8), 0x14, 0x05, 0x00 });
    }

    public static CanFrame Instr3(CarState car)
    {
        var degrees = Math.Min(0x7F, (int)Math.Round(Math.Abs(car.AmbientC), MidpointRounding.AwayFromZero));
        var sign = car.AmbientC < 0 && degrees > 0 ? 0x80 : 0x00;
        return new CanFrame(0x615, false, new byte[] { (byte)(car.Ac ? 0xC0 : 0x00), 0x00, 0x00, (byte)(sign | degrees), 0x00, 0x00, 0x00, 0x00 });
    }

    /// <summary>The first frames 100, 105 and 110 ms from now; then every 10, 200 and 200 ms.</summary>
    public void Start()
    {
        Every(0x153, Asc1, 100, 10);
        Every(0x613, Instr2, 105, 200);
        Every(0x615, Instr3, 110, 200);
    }

    void Every(uint id, Func<CarState, CanFrame> frame, ulong firstMs, ulong periodMs)
    {
        var period = periodMs * CyclesPerMs;
        void At(ulong at) => scheduler.Schedule(at, $"CAN partner {id:X3}h", () =>
        {
            var car = state();
            if (keyOn() && !car.Silenced.Contains(id))
            {
                sent[id] = sent.GetValueOrDefault(id) + 1;
                deliver(frame(car));
            }
            At(at + period);
        });
        At(scheduler.Now + firstMs * CyclesPerMs);
    }
}
