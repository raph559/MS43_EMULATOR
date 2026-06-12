using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Vehicle;

public class SteeringWheelLineTests
{
    [Fact] public void NoButtonTrain_IsAcceptedAsNoButton_OverThirtySeconds()
    {
        Machine? machine = null;
        var r = StartupRun.Run(TestImages.Car, new(Seconds: 32, Instrument: m =>
        {
            machine = m;
            new SteeringWheelLine(m.Scheduler, m.Board, () => true).Start();
        }));
        Assert.True((r.Firmware.Fd9c & 0x20) != 0);                            // FD9C.5: the wheel learned (R9)
        Assert.Equal(7, machine!.Bus.ExternalRam[0x80B6 - 0x8000]);            // no button
        Assert.DoesNotContain(r.Faults.Detected, f => f.Path is >= 122 and <= 124);   // no steering-wheel fault
        Assert.DoesNotContain(r.Faults.Stored, f => f.Path is >= 122 and <= 124);
    }
}
