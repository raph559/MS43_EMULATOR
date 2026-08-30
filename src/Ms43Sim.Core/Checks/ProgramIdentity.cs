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
    public const string Ms43X001LiveOverlay = "8992e80fbd4f3ab303c0a9840c53ced27e4accf9adbb4d1a5c5d110050606e02";

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
        _ => null,
    };

    /// <summary>The program keeps MS43X001's RAM: the checks that read it can run.</summary>
    public static bool HasMs43X001Ram(FirmwareImage image) => Name(image) is not null;
}
