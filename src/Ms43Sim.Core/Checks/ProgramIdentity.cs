using System.Security.Cryptography;

namespace Ms43Sim.Core;

/// <summary>
/// Which program a file holds (plan 4 R15): SHA-256 over file 0x10000–0x4FFE5 followed by
/// 0x50000–0x6FFFF, leaving out the 26-byte programming signature at 0x4FFE6–0x4FFFF that a flashing
/// tool writes. The car, release and E85CAMPORT files share the MS43X001 identity; the checks that read
/// the firmware's RAM run only on it.
/// </summary>
public static class ProgramIdentity
{
    public const string Ms43X001 = "32141e6358bfbfa0cc58bf6bbab72516f04ea769afbbba67e572923e3bb10d35";

    public static string Of(FirmwareImage image)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(image.Bytes[0x10000..0x4FFE6]);
        sha.AppendData(image.Bytes[0x50000..0x70000]);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool IsMs43X001(FirmwareImage image) => Of(image) == Ms43X001;
}
