using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Image;

public class FirmwareImageTests
{
    [Fact]
    public void Load_RejectsWrongSize()
    {
        using var dir = new TempDirectory();
        var path = dir.File("short.bin");
        File.WriteAllBytes(path, new byte[0x10000]);
        var ex = Assert.Throws<InvalidDataException>(() => FirmwareImage.Load(path));
        Assert.Contains("524288", ex.Message);
    }

    [Fact]
    public void Car_HashAndWords()
    {
        var img = FirmwareImage.FromBytes(TestBins.CarBytes);
        Assert.Equal("8cfe31241aa90717195077f1d931ec3bd28e0e76b435a89472dc1fbdfe6c0717", img.Sha256);
        Assert.Equal(0x366A, img.Read16(0x6ED06));
        Assert.Equal(0x000E, img.Read16(0x6ED08));
    }
}
