namespace Ms43Sim.Core;

public enum FlashMode { Raw, AsFlashedFull, AsFlashedPartial }

/// <summary>An image as the ECU would hold it, with how it was built.</summary>
public sealed record FlashedImage(
    FirmwareImage Image,
    FlashMode Mode,
    string SourceSha256,
    IReadOnlyList<ChecksumResult> BeforeCorrection,
    string Description);

/// <summary>
/// Builds the flash contents the ECU holds after an MS4X Flasher v1.6.0 write (design §3):
/// the Flasher corrects all five checksums in its buffer, then a full write programs
/// 0x8000-0x7FFFF (never the boot block) and a partial write programs 0x70000-0x7FFFF.
/// </summary>
public static class FlashImageBuilder
{
    const int BootBlockEnd = 0x8000;
    const int CalibrationStart = 0x70000;

    public static FlashedImage Raw(FirmwareImage file) =>
        new(file, FlashMode.Raw, file.Sha256, Checksums.Verify(file.Bytes),
            "raw: the file's bytes exactly as given, checksums not corrected");

    public static FlashedImage AsFlashedFull(FirmwareImage file, FirmwareImage? ecuBoot = null)
    {
        var corrected = file.ToArray();
        Checksums.Fix(corrected);
        var bootSource = ecuBoot ?? file;
        bootSource.Bytes[..BootBlockEnd].CopyTo(corrected);
        var bootFrom = ecuBoot is null ? "the file's own" : $"the ECU image {ecuBoot.Sha256[..12]}…'s";
        return new FlashedImage(FirmwareImage.FromBytes(corrected), FlashMode.AsFlashedFull, file.Sha256,
            Checksums.Verify(file.Bytes),
            $"as flashed (full write): 0x8000-0x7FFFF from the file with checksums corrected; boot block 0x0000-0x7FFF is {bootFrom}");
    }

    public static FlashedImage AsFlashedPartial(FirmwareImage file, FirmwareImage ecuImage)
    {
        var corrected = file.ToArray();
        Checksums.Fix(corrected);
        var result = ecuImage.ToArray();
        corrected.AsSpan(CalibrationStart).CopyTo(result.AsSpan(CalibrationStart));
        return new FlashedImage(FirmwareImage.FromBytes(result), FlashMode.AsFlashedPartial, file.Sha256,
            Checksums.Verify(file.Bytes),
            $"as flashed (partial write): calibration 0x70000-0x7FFFF from the file with checksums corrected, over the ECU image {ecuImage.Sha256[..12]}…");
    }
}
