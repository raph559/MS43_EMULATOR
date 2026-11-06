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
    /// The live-tuning overlay 8.5 (V8 with the pops throttle opening up to 20.0 deg TPS), as tools/build_live_overlay_v85.py
    /// builds it. Its flashed pops settings are calibration, as V8's are.
    /// </summary>
    public const string Ms43X001LiveOverlayV85 = "9765fe8be83a90bbe6c75c2c98cf07b446a45feb6e6157213852e677e2e94da9";

    /// <summary>
    /// The live-tuning overlay 9 (V8, the watched map and the LC/NLS/RAL triggers), as tools/build_live_overlay_v9.py
    /// builds it. Its flashed pops settings are calibration, as V8's are.
    /// </summary>
    public const string Ms43X001LiveOverlayV9 = "5906167bdfeb39851fe1d59b2c32eab4197d6ba63d5296be60906b14d05569c1";

    /// <summary>
    /// The live-tuning overlay 10 (V9, pops' throttle stabs and the downshift blip), as tools/build_live_overlay_v10.py
    /// builds it. Its flashed settings, groups 138..142 (40 bytes at 0x4FF80), are calibration, as V8's pops settings are.
    /// </summary>
    public const string Ms43X001LiveOverlayV10 = "51d7ddd61def3eaafd9453d2644a15b6630972fb447b5565f36ff34c5c8498fc";

    /// <summary>
    /// The live-tuning overlay 10.1 (V10 with the owner's pops and blip defaults, the rise allowed once stabbing and the
    /// blip lead), as tools/build_live_overlay_v10.py builds it since 2026-11-05. Its flashed settings are calibration.
    /// </summary>
    public const string Ms43X001LiveOverlayV101 = "309a5749ae0f856600d5170602b820841dd209e401f666e0f9c1a17125b3ed45";

    /// <summary>
    /// The live-tuning overlay 10.2 (V10.1 with the blip's spark cap above its target, ramped back toward advance after it,
    /// the real pops AFR and every AFR real, header minor 2), as tools/build_live_overlay_v10.py builds it since
    /// 2026-11-08 after the review: above the target the spark is the lower of the cap and the blip angle. Its flashed
    /// settings are calibration, and so are the stock start pre-injection and the X001 release's injector flow it
    /// carries (0x7DE64, 0x70E14, outside the program the identity covers).
    /// </summary>
    public const string Ms43X001LiveOverlayV102 = "b8bafce5f10d2b26a8095d7a71be094a634145bfd7a431bcc36c536c05de7173";

    /// <summary>
    /// V10.2's first build (453cd66), sent to the owner on 2026-11-08 before the review: a cap set above the blip angle
    /// fires that cap above the target.
    /// </summary>
    public const string Ms43X001LiveOverlayV102First = "42d0139998bbdd20ca73c49a208c0a29ec408dc60a54b057373d81f79636f4fd";

    /// <summary>
    /// The live-tuning overlay 10.3 (V10.2 without pops' throttle stabs: a window keeps pops' own opening and ends at a
    /// 160 rpm rise, group 139 reserved and 0, header minor 3; the spark ramped back after a blip), as tools/build_live_overlay_v10.py builds it since
    /// 2026-11-08. Its flashed settings are calibration, as V10.2's.
    /// </summary>
    public const string Ms43X001LiveOverlayV103 = "b7f5979312951b091c019e2d433b32e01de2c001e861f63ffae679c646603440";

    static readonly byte[] PopsDefaults = [0x00, 0x14, 0x17, 0xB8, 0x32, 0x9E, 0x00, 0x14];

    /// <summary>V10's built settings: pops, pops stabs, the blip, its gears and tap, its gear ratios.</summary>
    static readonly byte[] V10Defaults =
    [
        0x00, 0x28, 0x17, 0xB8, 0x32, 0x9E, 0x64, 0x14, 0x14, 0x4B, 0x0F, 0x19, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x03, 0x1E, 0xBB, 0x0F, 0x4B, 0x19, 0x5A, 0x01, 0x01, 0x01, 0x00, 0x15, 0x28, 0x00, 0x00,
        0xE2, 0x86, 0x59, 0x43, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>V10.1's built settings, in the same five groups.</summary>
    static readonly byte[] V101Defaults =
    [
        0x00, 0x28, 0x00, 0xD2, 0x7D, 0x9E, 0x1E, 0x14, 0x14, 0x19, 0x0F, 0x19, 0x0F, 0x00, 0x00, 0x00,
        0x00, 0x03, 0x1E, 0xBB, 0x0F, 0x96, 0x19, 0x75, 0x01, 0x01, 0x01, 0x00, 0x15, 0x28, 0x0F, 0x00,
        0xE2, 0x86, 0x59, 0x43, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>V10.2's built settings: V10.1's with the pops AFR a real 7.0 (70); 141 byte 7, the cap angle, is 0 (−23.625°).</summary>
    static readonly byte[] V102Defaults =
    [
        0x00, 0x28, 0x00, 0x46, 0x7D, 0x9E, 0x1E, 0x14, 0x14, 0x19, 0x0F, 0x19, 0x0F, 0x00, 0x00, 0x00,
        0x00, 0x03, 0x1E, 0xBB, 0x0F, 0x96, 0x19, 0x75, 0x01, 0x01, 0x01, 0x00, 0x15, 0x28, 0x0F, 0x00,
        0xE2, 0x86, 0x59, 0x43, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>V10.3's built settings: V10.2's with group 139 reserved, all 0.</summary>
    static readonly byte[] V103Defaults =
    [
        0x00, 0x28, 0x00, 0x46, 0x7D, 0x9E, 0x1E, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x03, 0x1E, 0xBB, 0x0F, 0x96, 0x19, 0x75, 0x01, 0x01, 0x01, 0x00, 0x15, 0x28, 0x0F, 0x00,
        0xE2, 0x86, 0x59, 0x43, 0x00, 0x00, 0x00, 0x00,
    ];

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
        Ms43X001LiveOverlayV85 => "MS43X001 with the live-tuning overlay 8.5",
        Ms43X001LiveOverlayV9 => "MS43X001 live overlay V9",
        Ms43X001LiveOverlayV10 => "MS43X001 live overlay V10",
        Ms43X001LiveOverlayV101 => "MS43X001 live overlay V10.1",
        Ms43X001LiveOverlayV102 => "MS43X001 live overlay V10.2",
        Ms43X001LiveOverlayV102First => "MS43X001 live overlay V10.2 (first build)",
        Ms43X001LiveOverlayV103 => "MS43X001 live overlay V10.3",
        _ => OfWithFlashedPops(image) switch
        {
            Ms43X001LiveOverlayV8 => "MS43X001 with the live-tuning overlay 8",
            Ms43X001LiveOverlayV85 => "MS43X001 with the live-tuning overlay 8.5",
            Ms43X001LiveOverlayV9 => "MS43X001 live overlay V9",
            _ => OfWithFlashed(image, V10Defaults) == Ms43X001LiveOverlayV10 ? "MS43X001 live overlay V10"
                : OfWithFlashed(image, V101Defaults) == Ms43X001LiveOverlayV101 ? "MS43X001 live overlay V10.1"
                : OfWithFlashed(image, V102Defaults) switch
                {
                    Ms43X001LiveOverlayV102 => "MS43X001 live overlay V10.2",
                    Ms43X001LiveOverlayV102First => "MS43X001 live overlay V10.2 (first build)",
                    _ => OfWithFlashed(image, V103Defaults) == Ms43X001LiveOverlayV103 ? "MS43X001 live overlay V10.3" : null,
                },
        },
    };

    /// <summary>The identity with the V8 pops settings put back to their built values, and the checksums with them.</summary>
    static string OfWithFlashedPops(FirmwareImage image) => OfWithFlashed(image, PopsDefaults);

    /// <summary>The identity with the flashed settings from 0x4FF80 put back to their built values, and the checksums with them.</summary>
    static string OfWithFlashed(FirmwareImage image, byte[] defaults)
    {
        var bytes = image.Bytes.ToArray();
        defaults.CopyTo(bytes, 0x4FF80);
        Checksums.Fix(bytes);
        return Of(FirmwareImage.FromBytes(bytes));
    }

    /// <summary>The program keeps MS43X001's RAM: the checks that read it can run.</summary>
    public static bool HasMs43X001Ram(FirmwareImage image) => Name(image) is not null;
}
