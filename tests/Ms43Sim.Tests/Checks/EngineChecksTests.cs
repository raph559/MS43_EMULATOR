using Ms43Sim.Core;
using Ms43Sim.Tests.Support;
using static Ms43Sim.Tests.Support.CheckRuns;

namespace Ms43Sim.Tests.Checks;

public class EngineChecksTests
{
    const ulong Ms = 24_000;

    static string Why(ICheck c) => $"{c.Name}: {string.Join("; ", c.Findings.Select(f => f.Message))}";

    [Fact] public void CarFullLoad_PassesSparkWindowRevLimitEngineAndPower()
    {
        var r = DriveRunWithChecks(DriveBuilder.Named("full-load"));
        foreach (var c in new[] { "spark-window", "rev-limit", "engine", "power" })
            Assert.True(r.Check(c).Status == CheckStatus.Pass, Why(r.Check(c)));
    }

    [Fact] public void CarDrivenAboveItsLimit_CutsItsSparks_AndPassesRevLimit()
    {
        var drive = DriveParser.Parse(string.Join('\n', "drive overspeed", "start blank", "set coolant=90 oil=90 intake=33 ambient=20",
            "3 on", "1.5 crank rpm=200", "6 idle", "4 drive gear=3 speed=20..150 pedal=95", "2 drive gear=3 rpm=7300 pedal=95",
            "4 drive gear=3 speed=150..20 pedal=0", "3 idle"));
        var r = DriveRunWithChecks(drive);
        Assert.True(r.Check("rev-limit").Status == CheckStatus.Pass, Why(r.Check("rev-limit")));
        Assert.DoesNotContain(r.Sparks, s => s.Fire > 16 * 24_000_000UL && s.Fire < 16.5 * 24e6);   // 1–1.5 s into 7,300 rpm: no spark
    }

    [Fact] public void AProgramThatNeverFires_FailsTheEngineCheck()
    {
        var image = Enumerable.Repeat((byte)0xFF, FirmwareImage.Size).ToArray();
        Hex.Bytes("A55AA5A5" + "0DFF").CopyTo(image, 0);                       // DISWDT; a loop
        var r = DriveRunWithChecks(DriveParser.Parse("drive x\n3 on\n1.5 crank rpm=200\n3 idle"), FlashImageBuilder.Raw(FirmwareImage.FromBytes(image)));
        Assert.Contains(r.Check("engine").Findings, f => f.Message.Contains("crank") && f.Message.Contains("line 3"));
        Assert.Equal(CheckStatus.NotEvaluated, r.Check("rev-limit").Status);  // its limit is in the MS43X001 program's RAM
        Assert.Equal(CheckStatus.NotEvaluated, r.Check("power").Status);
    }

    [Theory]
    [InlineData(1, 10, true)]                                                   // 10° BTDC of cylinder 1's firing TDC
    [InlineData(1, 75, true)]
    [InlineData(1, 80, false)]                                                 // 80° BTDC: too early
    [InlineData(1, -30, true)]
    [InlineData(1, -35, false)]                                                // 35° ATDC: too late
    [InlineData(4, 20, true)]                                                  // another cylinder, across the 720° wrap
    public void TheSparkWindow_Is30AtdcTo75Btdc_OfTheCylindersFiringTdc(int cylinder, double btdc, bool inside) =>
        Assert.Equal(inside, EngineChecks.InSparkWindow(cylinder, (EngineShaft.FiringTdc(cylinder) - btdc + 720) % 720));

    [Fact] public void RevLimitRule_FailsACylinderFuelledAndSparkedAboveTheLimitPast50ms()
    {
        var rule = new RevLimitRule();
        rule.Speed(0, 7300, 7008);
        Assert.Null(rule.Spark(40 * Ms, 1));                                   // over only 40 ms: not yet
        rule.Speed(60 * Ms, 7300, 7008);
        Assert.Null(rule.Spark(60 * Ms, 2));                                   // a spark alone
        Assert.NotNull(rule.Opening(61 * Ms, 2));                              // and its opening within the cycle (16.4 ms)
        Assert.Null(rule.Opening(70 * Ms, 3));                                 // fuel alone (a spark cut): fine
        Assert.Null(rule.Spark(70 * Ms + 17 * Ms, 3));                         // its spark a cycle later: not one cycle
        rule.Speed(100 * Ms, 7050, 7008);                                      // within L + 50
        Assert.Null(rule.Spark(101 * Ms, 4));
        Assert.Null(rule.Opening(101 * Ms, 4));
    }

    [Fact] public void PowerRule_FailsACylinderSilentForThreeEngineCycles_WhilePowerIsAsked()
    {
        var rule = new PowerRule();
        const double rpm = 3000;                                               // a cycle is 40 ms, three are 120 ms
        for (ulong t = 0; t <= 200; t += 10)
        {
            for (var c = 1; c <= 6; c++)
            {
                if (c != 4 || t < 50)                                          // cylinder 4 stops firing at 50 ms
                {
                    rule.Spark(t * Ms, c);
                    rule.Opening(t * Ms, c);
                }
            }
            var silent = rule.Tick(t * Ms, demanded: true, rpm).ToArray();
            Assert.Equal(t > 40 + 120 ? new[] { 4 } : Array.Empty<int>(), silent);
        }
        Assert.Empty(rule.Tick(210 * Ms, demanded: false, rpm));               // no demand, no rule
        Assert.Empty(rule.Tick(220 * Ms, demanded: true, rpm));                // a new demand waits three cycles again
    }

    [Fact] public void ABinWithItsIgnitionCut_FailsPowerUnderLoad()
    {
        var image = InMemoryBin.With(TestBins.CarBytes, (0x7C92E, new byte[] { 0x00, 0x00 }));   // the long-dwell cell (R3) at zero
        var drive = DriveParser.Parse(string.Join('\n', "drive cut", "start blank", "set coolant=90 oil=90 intake=33 ambient=20",
            "3 on", "1.5 crank rpm=200", "3 idle", "4 drive gear=3 speed=20..60 pedal=20"));
        var r = DriveRunWithChecks(drive, image);
        Assert.Equal(CheckStatus.Fail, r.Check("power").Status);
    }
}
