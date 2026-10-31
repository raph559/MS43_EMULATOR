using System.Buffers.Binary;
using System.Text.Json;
using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>V8.5: V8 with the pops throttle opening up to 20.0 deg TPS, and nothing else.</summary>
public static class V85Fixture
{
    public static byte[] Bytes
    {
        get
        {
            var image = TestBins.CarBytes;
            foreach (var (at, data) in LiveOverlayBin.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot,
                         "sim/tests/Ms43Sim.Tests/fixtures/live_overlay_v85.hex")))) data.CopyTo(image, at);
            return image;
        }
    }

    public static int Symbol(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(LiveOverlayBin.RepositoryRoot,
            "defs/live_overlay_v85_symbols.json")));
        return json.RootElement.GetProperty(name).GetInt32();
    }
}

public sealed class OverlayV85Tests
{
    [Fact]
    public void TheHeaderIsV8WithMinor5()
    {
        var image = V85Fixture.Bytes;
        Assert.Equal(0x0508, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B548)));
        Assert.Equal(0xC0D8, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x4B552)));
    }

    [Fact]
    public void ItIsAKnownProgramWhateverItsPopsSettings()
    {
        var settings = V85Fixture.Bytes;
        PopsDriveFixture.On.CopyTo(settings, V8Fixture.Defaults);
        Assert.Equal("MS43X001 with the live-tuning overlay 8.5", ProgramIdentity.Name(TestImages.AsFlashed(V85Fixture.Bytes).Image));
        Assert.Equal("MS43X001 with the live-tuning overlay 8.5", ProgramIdentity.Name(TestImages.AsFlashed(settings).Image));
    }

    [Fact]
    public void OnlyTheThrottleOpeningsLimitDiffersFromV8()
    {
        var v8 = V8Fixture.Bytes.AsSpan(V8Fixture.Symbol("pops_limits") - 0x80000, 16).ToArray();
        var v85 = V85Fixture.Bytes.AsSpan(V85Fixture.Symbol("pops_limits") - 0x80000, 16).ToArray();
        Assert.Equal(50, v8[13]);
        Assert.Equal(200, v85[13]);
        v8[13] = 200;
        Assert.Equal(v8, v85);
    }

    [Fact]
    public void TheCalibrationAndTheBootBlockAreV8s()
    {
        var (v8, v85) = (V8Fixture.Bytes, V85Fixture.Bytes);
        Assert.True(v8.AsSpan(0x70000).SequenceEqual(v85.AsSpan(0x70000)));
        Assert.True(v8.AsSpan(0, 0x10000).SequenceEqual(v85.AsSpan(0, 0x10000)));
    }
}

public sealed class PopsGateTestsOnV85 : PopsGateTests { protected override OverlayFixture Fixture => OverlayFixture.V85; }

public sealed class PopsHookTestsOnV85 : PopsHookTests
{
    protected override OverlayFixture Fixture => OverlayFixture.V85;
    protected override (byte Limit, int Add) ThrottleLimit => (200, 10925);
}
