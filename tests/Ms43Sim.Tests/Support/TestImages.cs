using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>Images as the ECU holds them after an MS4X Flasher full write.</summary>
public static class TestImages
{
    public static FlashedImage Car => AsFlashed(TestBins.CarBytes);

    public static string CarPath => Path.Combine(TestBins.Root, "bins", "CAR_current_43X001_SP95_STAGE1.bin");

    public static FlashedImage AsFlashed(byte[] bytes) => FlashImageBuilder.AsFlashedFull(FirmwareImage.FromBytes(bytes));
}
