using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.BoardDevices;

public class BoardInputsTests
{
    [Theory]
    [InlineData(0.0, 0)] [InlineData(2.42, 496)] [InlineData(2.5, 512)] [InlineData(0.45, 92)] [InlineData(5.0, 1023)]
    public void AdcCode_RoundsToTheNearestStep(double volts, int code) => Assert.Equal(code, BoardInputs.AdcCode(volts));

    [Fact] public void KeyOn_IgnitionAndSupply_SitInsideTheFirmwaresWindows()
    {
        var k = BoardInputs.KeyOnEngineStopped;
        var an1 = BoardInputs.AdcCode(k.AnalogVolts[1]);
        var an5 = BoardInputs.AdcCode(k.AnalogVolts[5]);
        Assert.True(an1 > 0xEB);                          // ignition on (0E:B60A)
        Assert.True(an1 >> 2 <= 0xA0 && an5 >> 2 <= 0xA0); // no over-voltage flag (cal 0x70D8B)
        Assert.True(BoardInputs.AdcCode(k.WithIgnition(false).AnalogVolts[1]) < 0x9C);
    }

    [Fact] public void KeyOn_Table_MatchesTheReferenceFacts()
    {
        var k = BoardInputs.KeyOnEngineStopped;
        Assert.Equal(new[] { 205, 496, 92, 92, 0, 496, 77, 92, 92, 0, 154, 876, 152, 0, 0, 0 },
            k.AnalogVolts.Select(BoardInputs.AdcCode).ToArray());          // AN4 and AN13 entries unused: 0 V
        Assert.Equal(new[] { 793, 725, 784, 793, 0, 0, 512, 0 }, k.MuxAVolts.Select(BoardInputs.AdcCode).ToArray());
        Assert.Equal(new[] { 512, 512, 0, 836, 0, 0, 0, 0 }, k.MuxBVolts.Select(BoardInputs.AdcCode).ToArray());
        Assert.Equal((false, false, false), (k.DigitalInputs[(3, 1)], k.DigitalInputs[(3, 5)], k.DigitalInputs[(8, 6)]));
    }

    [Fact] public void Board_UnlistedPins_ReadHigh_ListedPinsReadTheirLevel()
    {
        var b = new Board(BoardInputs.KeyOnEngineStopped, new EventLog());
        Assert.Equal((true, false, true), (b.InputLevel(3, 11), b.InputLevel(3, 1), b.InputLevel(5, 11)));
    }

    [Fact] public void Board_SetInputs_ReachesThePortPins_ThroughTheMachine()
    {
        var t = TestMachine.Create("CC00");
        var seen = new List<(int, int, bool)>();
        t.M.Ports.PinChanged += (port, bit, level) => seen.Add((port, bit, level));
        t.M.Board.SetInputs(t.M.Board.Inputs.WithInput(3, 1, true).WithInput(3, 11, true));
        Assert.Equal(new[] { (3, 1, true) }, seen);
        Assert.True(t.M.Ports.Pin(3, 1));
    }
}
