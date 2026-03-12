using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Image;

public class FlashImageBuilderTests
{
    static FirmwareImage Car => FirmwareImage.FromBytes(TestBins.CarBytes);

    [Fact]
    public void FullWrite_OfCar_ChangesOnlyTheCalibrationCrc()
    {
        var f = FlashImageBuilder.AsFlashedFull(Car).Image.Bytes.ToArray();
        var car = TestBins.CarBytes;
        var diffs = Enumerable.Range(0, 0x80000).Where(i => f[i] != car[i]).ToArray();
        Assert.Equal(new[] { 0x73FE0, 0x73FE1 }, diffs);
        Assert.Equal(0xFD96, BitConverter.ToUInt16(f, 0x73FE0));
    }

    [Fact]
    public void FullWrite_KeepsTheEcuBootBlock()
    {
        var boot = TestBins.CarBytes.ToArray();
        boot[0x0100] = 0x12;   // a marker inside 0x0000-0x7FFF
        boot[0x8000] = 0x34;   // outside: must NOT be taken
        var f = FlashImageBuilder.AsFlashedFull(Car, FirmwareImage.FromBytes(boot)).Image;
        Assert.Equal(0x12, f.Bytes[0x0100]);
        Assert.Equal(TestBins.CarBytes[0x8000], f.Bytes[0x8000]);
    }

    [Fact]
    public void PartialWrite_OfCandidateOntoCar_EqualsTheCandidate()
    {
        var f = FlashImageBuilder.AsFlashedPartial(FirmwareImage.FromBytes(TestBins.E85CamPortBytes), Car).Image;
        Assert.Equal(TestBins.E85CamPortBytes, f.Bytes.ToArray());
    }

    [Fact]
    public void Raw_IsTheFileUnchanged() =>
        Assert.Equal(TestBins.CarBytes, FlashImageBuilder.Raw(Car).Image.Bytes.ToArray());
}
