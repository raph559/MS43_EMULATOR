namespace Ms43Sim.Core;

/// <summary>
/// The multi-function steering wheel's line on P7.4 (CC28), sending "no button" (plan 4 R9) [ver:
/// 32 s accepted, FD9C.5 learned, no steering-wheel fault]. The firmware decodes only the high times:
/// 139–197 µs is a 0, 203–363 µs a 1, and 9.88–11.14 ms ends a frame. The line idles low; every 50 ms
/// a frame: 7 × (277 µs high, 200 µs low), the toggle bit (165 µs high for FEh, 277 µs for FFh),
/// 200 µs low, 10.5 ms high, then low. FEh and FFh alternate every 1.0 s (the firmware wants the
/// toggle within 0.3–1.7 s). Frames go only while the key is on; the line stays low otherwise
/// [assumption]. Once the wheel is learned (EEPROM byte 52h = 40h) a silent line stores P1567 about
/// 4 s after key-on, so a drive runs it whenever the key is on. Times assume fCPU = 24 MHz.
/// </summary>
public sealed class SteeringWheelLine
{
    public const ulong FramePeriod = 1_200_000, TogglePeriod = 24_000_000;

    const int Port = 7, Bit = 4;
    const ulong One = 6_648, Zero = 3_960, Low = 4_800, End = 252_000;

    readonly Scheduler scheduler;
    readonly ISignalPins pins;
    readonly Func<bool> keyOn;
    ulong started;

    public SteeringWheelLine(Scheduler scheduler, ISignalPins pins, Func<bool> keyOn)
    {
        this.scheduler = scheduler;
        this.pins = pins;
        this.keyOn = keyOn;
    }

    /// <summary>Frames sent.</summary>
    public long Frames { get; private set; }

    /// <summary>The line low now, and a frame now and every 50 ms while the key is on.</summary>
    public void Start()
    {
        started = scheduler.Now;
        pins.Drive(Port, Bit, false);
        FrameAt(started);
    }

    void FrameAt(ulong at) => scheduler.Schedule(at, "steering-wheel frame", () =>
    {
        if (keyOn())
        {
            Frames++;
            var t = at;
            for (var i = 0; i < 7; i++)
                t = Pulse(t, One);
            t = Pulse(t, (at - started) / TogglePeriod % 2 == 1 ? One : Zero);
            Level(t, true);
            Level(t + End, false);
        }
        FrameAt(at + FramePeriod);
    });

    /// <summary>High for <paramref name="length"/> from <paramref name="at"/>, then low for 200 µs; returns the next rise.</summary>
    ulong Pulse(ulong at, ulong length)
    {
        Level(at, true);
        Level(at + length, false);
        return at + length + Low;
    }

    void Level(ulong at, bool level) => scheduler.Schedule(at, "steering-wheel line", () => pins.Drive(Port, Bit, level));
}
