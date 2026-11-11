using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// V9's triggers for launch control (124), no-lift shift (125) and rolling anti-lag (126): an activation byte of
/// 80h..9Fh follows one bit of byte 0 of the ASC (153h, EF47) or A/C (615h, EF37) frame, as pops' switch does. The
/// trigger only makes the feature ready, and only gates engagement. Direct CPU tests from each feature's routine.
/// </summary>
public sealed class TriggerTests
{
    /// <summary>The overlay under test: V9, or V10, which keeps V9's triggers.</summary>

    private static Machine Start() => V10Fixture.Start();

    private const byte AirConOn = 0x97;                          // A/C frame, on while byte 0 bit 7 is 1

    private static byte[] With(int group, byte config)
    {
        var data = OverlayFeatureTests.FeatureData(group); data[0] = config; return data;
    }

    /// <summary>The feature's own action: the clutch at a standstill for LC, the clutch moving for NLS, "−" for RAL.</summary>
    private static void Action(Machine m, int group, bool pressed)
    {
        if (group == 126) OverlayFeatureTests.Inputs(m, cruise: pressed ? (byte)6 : (byte)5);
        else OverlayFeatureTests.Inputs(m, speed: group == 124 ? (byte)0 : (byte)30, clutch: pressed);
    }

    private static void AirCon(Machine m, byte value) => V10Fixture.PutByte(m, 0xEF37, value);

    public static TheoryData<int> Groups => new() { 124, 125, 126 };

    [Theory, MemberData(nameof(Groups))]
    public void ACanTriggerEngagesOnlyWhileItsBitMatches(int group)
    {
        var on = OverlayFeatureTests.Feature(group, With(group, AirConOn));
        Action(on, group, true); AirCon(on, 0x80);
        OverlayFeatureTests.Execute(on); OverlayFeatureTests.Active(on, group, true);

        var off = OverlayFeatureTests.Feature(group, With(group, AirConOn));
        Action(off, group, true); AirCon(off, 0x00);
        OverlayFeatureTests.Execute(off); OverlayFeatureTests.Active(off, group, false);
    }

    [Theory, MemberData(nameof(Groups))]
    public void ATriggerAloneNeverEngages(int group)
    {
        var m = OverlayFeatureTests.Feature(group, With(group, AirConOn));
        Action(m, group, false); AirCon(m, 0x80);
        OverlayFeatureTests.Execute(m); OverlayFeatureTests.Active(m, group, false);
    }

    [Theory, MemberData(nameof(Groups))] // RF6
    public void TheTriggerGoingAwayDoesNotEndAnEngagedFeature(int group)
    {
        var m = OverlayFeatureTests.Feature(group, With(group, AirConOn));
        Action(m, group, true); AirCon(m, 0x80);
        OverlayFeatureTests.Execute(m); OverlayFeatureTests.Active(m, group, true);
        AirCon(m, 0x00);
        for (var tick = 0; tick < 100; tick++) OverlayFeatureTests.Execute(m);
        OverlayFeatureTests.Active(m, group, true);
        switch (group)                                            // then the stock release ends it
        {
            case 124: OverlayFeatureTests.Inputs(m, speed: 30, clutch: false); break;
            case 125: OverlayFeatureTests.Inputs(m, clutch: false); break;
            default: OverlayFeatureTests.Inputs(m, pedal: 99); break;
        }
        OverlayFeatureTests.Execute(m); OverlayFeatureTests.Active(m, group, false);
    }

    [Fact]
    public void PolarityAndFrameFollowTheValue()
    {
        var low = OverlayFeatureTests.Feature(124, With(124, 0x9F));   // A/C, on while bit 7 is 0
        Action(low, 124, true); AirCon(low, 0x00);
        OverlayFeatureTests.Execute(low); OverlayFeatureTests.Active(low, 124, true);

        foreach (var (asc, engaged) in new[] { ((byte)0x04, true), ((byte)0x00, false) })
        {
            var m = OverlayFeatureTests.Feature(124, With(124, 0x82));  // ASC, on while bit 2 is 1
            Action(m, 124, true); V10Fixture.PutByte(m, 0xEF47, asc);
            OverlayFeatureTests.Execute(m); OverlayFeatureTests.Active(m, 124, engaged);
        }
    }

    private static byte[] Write(int id, byte[] bytes) => [0x6D, 2, (byte)id, 0, 0, .. bytes];

    [Theory]
    [InlineData(124, 0, true)] [InlineData(124, 1, true)] [InlineData(124, 2, true)] [InlineData(124, 0x80, true)]
    [InlineData(124, 0x97, true)] [InlineData(124, 0x9F, true)] [InlineData(124, 3, false)] [InlineData(124, 0x7F, false)]
    [InlineData(124, 0xA0, false)] [InlineData(124, 0xFF, false)] [InlineData(125, 0x97, true)] [InlineData(125, 3, false)]
    [InlineData(126, 0, true)] [InlineData(126, 1, true)] [InlineData(126, 0x97, true)] [InlineData(126, 2, false)]
    [InlineData(126, 0xA0, false)]
    public void ValidationAcceptsTriggersAndRefusesTheRest(int group, byte config, bool accepted)
    {
        var m = Start();
        Assert.Equal(0xA0, V10Fixture.Mutate(m, V10Fixture.Load(group, 0))[0]);
        var reply = V10Fixture.Mutate(m, Write(group, With(group, config)));
        if (accepted) Assert.Equal(0xA0, reply[0]);
        else Assert.Equal(new byte[] { 0xB1, 4 }, reply);
    }

    [Fact]
    public void RalsNeighbourRuleStillRefusesAChangedByte7()
    {
        var m = Start();
        Assert.Equal(0xA0, V10Fixture.Mutate(m, V10Fixture.Load(126, 0))[0]);
        var data = With(126, AirConOn); data[7] ^= 1;
        Assert.Equal(new byte[] { 0xB1, 4 }, V10Fixture.Mutate(m, Write(126, data)));
    }

    [Fact]
    public void AFlashedTriggerWorksWithoutTheApp()
    {
        var m = OverlayFeatureTests.Feature(124, state: "uninitialised", flashed: AirConOn);
        OverlayFeatureTests.Inputs(m, speed: 0, pedal: 255); AirCon(m, 0x80);     // the car's own flashed gates apply
        OverlayFeatureTests.Execute(m); OverlayFeatureTests.Active(m, 124, true);
    }
}
