using System.Security.Cryptography;

namespace Ms43Sim.Core;

/// <summary>
/// Which program a file holds (plan 4 R15): SHA-256 over file 0x10000–0x4FFE5 followed by
/// 0x50000–0x6FFFF, leaving out the 26-byte programming signature at 0x4FFE6–0x4FFFF that a flashing
/// tool writes. The car, release and E85CAMPORT files share the MS43X001 identity. The live-tuning overlay
/// (plan 6) is MS43X001 with hooks that change no RAM the checks read: the checks that read the firmware's
/// RAM run on both, and on no other program.
/// </summary>
public static class ProgramIdentity
{
    public const string Ms43X001 = "32141e6358bfbfa0cc58bf6bbab72516f04ea769afbbba67e572923e3bb10d35";

    /// <summary>
    /// MS43X001 with the live-tuning overlay 1, as tools/build_live_overlay.py builds it from patches/live_overlay.asm (plan 6,
    /// D13). A rebuild that changes the program changes this pin.
    /// </summary>
    public const string Ms43X001LiveOverlay = "a188fb4fca566d20c04bb359f04601f0e4e01af04be5c17e2bc9202c711457f2";

    public const string Ms43X001LiveOverlayV2 = "47ec91bbad19a09f890ca514632e107ee501146edab98843dc279a41cf682dcf";
    public const string Ms43X001LiveOverlayV3 = "eac4af912730c8ce681ebfe7329b198365226b9bfabc870f9af3fb800516523a";
    public const string Ms43X001LiveOverlayV4 = "f5541e1edf954aa24fa1934db230b161dde7ab9ae005003b9892bf72b28f0d57";
    public const string Ms43X001LiveOverlayV5 = "a43d2af4acc6b1ae7ff8e02a5039fe5e222eea90ef4bffcfcc46346e96c76f8c";
    public const string Ms43X001LiveOverlayV6 = "926eee3dcfa194841424601167a298c4f233dcd3218410a49da6cfa944717c0e";
    public const string Ms43X001LiveOverlayV7 = "a55ba742d87193fd48b42e72261cc28826e683f4d7d590221b73141f93ee8a42";

    /// <summary>
    /// The live-tuning overlay 8 (pops on lift-off), as tools/build_live_overlay_v8.py builds it. Its eight flashed pops
    /// settings at 0x4FF80 are calibration the owner writes into the bin, so they do not change which program it is.
    /// </summary>
    public const string Ms43X001LiveOverlayV8 = "8aa73c9a28926becfee02a6424de42151f7daa1902283c7e2fc231e659b4aeb1";

    /// <summary>
    /// The live-tuning overlay 9 (V8, the watched map and the LC/NLS/RAL triggers), as tools/build_live_overlay_v9.py
    /// builds it. Its flashed pops settings are calibration, as V8's are.
    /// </summary>
    public const string Ms43X001LiveOverlayV9 = "5906167bdfeb39851fe1d59b2c32eab4197d6ba63d5296be60906b14d05569c1";

    static readonly byte[] PopsDefaults = [0x00, 0x14, 0x17, 0xB8, 0x32, 0x9E, 0x00, 0x14];

    public static string Of(FirmwareImage image)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(image.Bytes[0x10000..0x4FFE6]);
        sha.AppendData(image.Bytes[0x50000..0x70000]);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool IsMs43X001(FirmwareImage image) => Of(image) == Ms43X001;

    /// <summary>The known program's name, or null.</summary>
    public static string? Name(FirmwareImage image) => Of(image) switch
    {
        Ms43X001 => "MS43X001",
        Ms43X001LiveOverlay => "MS43X001 with the live-tuning overlay 1",
        Ms43X001LiveOverlayV2 => "MS43X001 with the live-tuning overlay 2",
        Ms43X001LiveOverlayV3 => "MS43X001 with the live-tuning overlay 3",
        Ms43X001LiveOverlayV4 => "MS43X001 with the live-tuning overlay 4",
        Ms43X001LiveOverlayV5 => "MS43X001 with the live-tuning overlay 5",
        Ms43X001LiveOverlayV6 => "MS43X001 with the live-tuning overlay 6",
        Ms43X001LiveOverlayV7 => "MS43X001 with the live-tuning overlay 7",
        Ms43X001LiveOverlayV8 => "MS43X001 with the live-tuning overlay 8",
        Ms43X001LiveOverlayV9 => "MS43X001 live overlay V9",
        _ => OfWithFlashedPops(image) switch
        {
            Ms43X001LiveOverlayV8 => "MS43X001 with the live-tuning overlay 8",
            Ms43X001LiveOverlayV9 => "MS43X001 live overlay V9",
            _ => null,
        },
    };

    /// <summary>The identity with the V8 pops settings put back to their built values, and the checksums with them.</summary>
    static string OfWithFlashedPops(FirmwareImage image)
    {
        var bytes = image.Bytes.ToArray();
        PopsDefaults.CopyTo(bytes, 0x4FF80);
        Checksums.Fix(bytes);
        return Of(FirmwareImage.FromBytes(bytes));
    }

    /// <summary>The program keeps MS43X001's RAM: the checks that read it can run.</summary>
    public static bool HasMs43X001Ram(FirmwareImage image) => Name(image) is not null;
}
