using System.Numerics;

namespace Ms43Sim.Core;

/// <summary>The device end of the K-line, a tester: <see cref="KLineBus"/> tells it every byte that reaches it.</summary>
public interface IKLineDevice
{
    /// <summary>
    /// A byte reached the device at the end of its stop bit, in <paramref name="cycle"/>: the device's own
    /// byte coming back (<paramref name="own"/>), or one the ECU sent.
    /// </summary>
    void OnLineByte(byte value, bool own, ulong cycle);

    /// <summary>The bit time, in CPU cycles, the device listens at now: an ECU byte at another rate does not reach it (plan 6, M4).</summary>
    double BitCycles { get; }
}

/// <summary>What a device needs of the line. <see cref="KLineBus"/> is the car's; a test can stand in for the ECU end.</summary>
public interface IKLinePort
{
    /// <summary>
    /// One 8E1 byte from the device, on the line from now at <paramref name="bitCycles"/> CPU cycles a bit. A device that sends
    /// several bytes back to back sends each when the echo of the one before comes back (<c>own: true</c>), or when
    /// <see cref="Busy"/> goes false: the port rounds a byte's length its own way.
    /// </summary>
    void Send(byte value, double bitCycles);

    /// <summary>A device byte is on the line at this moment.</summary>
    bool Busy { get; }

    /// <summary>A byte the line has lost, in the cycle it would have arrived.</summary>
    event Action<KLineNote>? Noted;

    /// <summary>Puts the one device on the line; attaching another replaces it.</summary>
    void Attach(IKLineDevice device);
}

/// <summary>
/// A byte the line or the tester did not take: <see cref="Kind"/> is "mismatch" (the two ends' bit times are more than
/// 3 % apart), "collision" (it overlapped another byte) or "stray" (an ECU byte that reached the tester but was no part of
/// a reply, plan 6 M3); <see cref="Cycle"/> is when it arrived or would have; <see cref="From"/> is the end that sent it,
/// "tester" (the device) or "ecu".
/// </summary>
public sealed record KLineNote(ulong Cycle, string Kind, byte Value, string From);

/// <summary>
/// The car's K-line: one wire between the ECU's ASC0 and a tester (plan 5 C1–C3). A byte from either end
/// reaches both ends at the end of its stop bit. A device byte (8E1: 11 bit times at the device's bit
/// time) goes into ASC0 with its even parity bit in bit 8, if the two bit times are within 3 %, and back to
/// the device as its own echo. An ASC0 frame goes back into ASC0's own receiver, as the wire echoes it, whether or not a
/// device is attached, and in the same cycle to the device if the two bit times are within 3 % (plan 6 M4: else it is
/// lost to the device and noted); ASC0 takes a frame or not as S0R
/// and S0REN were at its start bit (the firmware turns its receiver off for its replies). Bytes that overlap in time are both lost, whichever began first, and each is noted as a
/// collision in the cycle it would have arrived. Overlap is judged on exact cycles from ASC0's frame in
/// progress: a byte that begins where the other ends does not overlap it, whichever event of that cycle runs first.
/// [assumption] There is no transceiver delay. A byte at a bit time more than 3 % from ASC0's, and bytes
/// that collide, are lost and noted; a real UART would receive garbage and run the firmware's error path,
/// which is not exercised here. The echo is not combined
/// with S0LB, which would deliver each ECU frame twice: neither of the firmware's set-ups uses it
/// (S0CON = 80F7h for DS2, 80D1h for KWP).
/// </summary>
public sealed class KLineBus : IKLinePort
{
    const int DeviceFrameBits = 11;          // start bit, 8 data bits, even parity, one stop bit
    const double BitTimeTolerance = 0.03;
    const string Mismatch = "mismatch", Collision = "collision", FromTester = "tester", FromEcu = "ecu";

    readonly Scheduler scheduler;
    readonly Asc0 ecu;
    readonly List<DeviceByte> onLine = new();
    IKLineDevice? device;
    ulong? collidedFrameEnd;

    public KLineBus(Scheduler scheduler, Asc0 ecu)
    {
        this.scheduler = scheduler;
        this.ecu = ecu;
        ecu.FrameTransmitted += OnEcuFrame;
    }

    /// <summary>A byte the bus has lost, in the cycle it would have arrived.</summary>
    public event Action<KLineNote>? Noted;

    /// <summary>
    /// A device byte is on the line: one whose stop bit has not yet ended. A byte that ends in this cycle
    /// is already off, whichever event of the cycle runs first.
    /// </summary>
    public bool Busy
    {
        get
        {
            foreach (var sent in onLine)
            {
                if (sent.End > scheduler.Now)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Puts the one device on the line; attaching another replaces it.</summary>
    public void Attach(IKLineDevice device) => this.device = device;

    /// <summary>
    /// One 8E1 byte from the device, on the line from now for 11 bit times. At its end it goes into ASC0 and
    /// back to the device, or is lost and noted; a byte sent while another is on the line collides with it.
    /// </summary>
    public void Send(byte value, double bitCycles)
    {
        if (!double.IsFinite(bitCycles) || bitCycles < 1)
            throw new ArgumentOutOfRangeException(nameof(bitCycles), bitCycles, "a bit lasts at least one CPU cycle");
        var start = scheduler.Now;
        var end = start + (ulong)Math.Round(DeviceFrameBits * bitCycles, MidpointRounding.AwayFromZero);   // 24 MHz / baud need not be whole
        var sent = new DeviceByte(value, bitCycles, start, end);
        foreach (var other in onLine)
        {
            if (other.End > start)
                other.Collided = sent.Collided = true;
        }
        onLine.Add(sent);
        scheduler.Schedule(end, "K-line device byte", () => EndOfDeviceByte(sent));
    }

    void EndOfDeviceByte(DeviceByte sent)
    {
        onLine.Remove(sent);
        if (ecu.FrameInProgress is { } frame && frame.Start < sent.End)
        {
            sent.Collided = true;                // ASC0 is sending, and began before this byte ended: they overlap,
            collidedFrameEnd = frame.End;        // and the frame is lost with the byte when it ends
        }
        if (sent.Collided)
            Note(Collision, sent.Value, FromTester);
        else if (Math.Abs(sent.BitCycles - ecu.BitCycles) > BitTimeTolerance * ecu.BitCycles)
            Note(Mismatch, sent.Value, FromTester);
        else
            ecu.Receive((ushort)(sent.Value | (EvenParityBit(sent.Value) << 8)), sent.Start);
        device?.OnLineByte(sent.Value, own: true, scheduler.Now);
    }

    void OnEcuFrame(ushort frame, ulong start)
    {
        var now = scheduler.Now;
        var collided = collidedFrameEnd == now;  // a device byte ended while this frame was on the line
        collidedFrameEnd = null;
        foreach (var sent in onLine)
        {
            if (sent.Start < now)                // still on the line as the frame ends, and began before: they overlap
                sent.Collided = collided = true;
        }
        var value = (byte)frame;
        if (collided)
        {
            Note(Collision, value, FromEcu);
            return;
        }
        ecu.Receive(frame, start);
        if (device is null)
            return;
        if (Math.Abs(device.BitCycles - ecu.BitCycles) > BitTimeTolerance * ecu.BitCycles)
            Note(Mismatch, value, FromEcu);
        else
            device.OnLineByte(value, own: false, now);
    }

    void Note(string kind, byte value, string from) => Noted?.Invoke(new KLineNote(scheduler.Now, kind, value, from));

    static int EvenParityBit(byte value) => BitOperations.PopCount(value) & 1;

    sealed class DeviceByte(byte value, double bitCycles, ulong start, ulong end)
    {
        public byte Value { get; } = value;
        public double BitCycles { get; } = bitCycles;
        public ulong Start { get; } = start;
        public ulong End { get; } = end;
        public bool Collided { get; set; }
    }
}
