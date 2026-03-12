using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Image;

public class ChecksumTests
{
    [Fact]
    public void Car_StaleCalibrationCrcOnly()
    {
        var r = Checksums.Verify(TestBins.CarBytes).ToDictionary(c => c.Kind);
        Assert.Equal((0xB3DAAD17u, 0xB3DAAD17u), (r[ChecksumKind.ProgramSum].Stored, r[ChecksumKind.ProgramSum].Computed));
        Assert.Equal(0xA61BA67Eu, r[ChecksumKind.CalibrationSum].Computed);
        Assert.Equal(0xC6D5u, r[ChecksumKind.BootCrc].Computed);
        Assert.Equal(0xB49Fu, r[ChecksumKind.ProgramCrc].Computed);
        Assert.Equal((0x6669u, 0xFD96u), (r[ChecksumKind.CalibrationCrc].Stored, r[ChecksumKind.CalibrationCrc].Computed));
        Assert.Equal(4, r.Values.Count(c => c.Ok));
    }

    [Fact]
    public void Fix_MatchesToolOnMutatedCar()
    {
        var b = TestBins.CarBytes.ToArray();
        b[0x712D0] ^= 0x5A;
        Checksums.Fix(b);
        Assert.Equal(0xA61BA628u, BitConverter.ToUInt32(b, 0x72FFC));
        Assert.Equal(0xD559, BitConverter.ToUInt16(b, 0x73FE0));
        Assert.All(Checksums.Verify(b), c => Assert.True(c.Ok));
    }

    [Fact]
    public void Fix_LeavesCorrectCandidateUnchanged()
    {
        var b = TestBins.E85CamPortBytes.ToArray();
        Checksums.Fix(b);
        Assert.Equal(TestBins.E85CamPortBytes, b);
    }
}
