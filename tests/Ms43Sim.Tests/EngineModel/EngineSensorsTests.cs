using Ms43Sim.Core;

namespace Ms43Sim.Tests.EngineModel;

public class EngineSensorsTests
{
    [Fact] public void MafVolts_InvertsTheCharacteristic()
    {
        Assert.Equal((0.87, 1.00, 0.0), (EngineSensors.MafVolts(17.0), EngineSensors.MafVolts(20.25), EngineSensors.MafVolts(0)));
        Assert.Equal(2.3, EngineSensors.MafVolts(EngineSensors.MafKgPerHour(2.3)), 9);
    }

    [Fact] public void KnockVolts_FollowsTheNoiseLevel() =>
        Assert.Equal((0.0, 8.5 * 5 / 256, 40.5 * 5 / 256), (EngineSensors.KnockVolts(0), EngineSensors.KnockVolts(900), EngineSensors.KnockVolts(9000)));
}
