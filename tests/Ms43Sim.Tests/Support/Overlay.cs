using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>
/// The frozen known-bad overlay (plan 4 Task 14, owner decision 2): the assembler's Intel HEX output of
/// patches/overlay.asm (asl -L, p2hex -F Intel32 -l 32, 2026-10-21; two overlapping-allocation warnings),
/// committed as a fixture and applied to a bin in memory only — no overlay image is ever written to
/// disk, and none may be flashed. A record that would touch the boot block or a validity tag is refused.
/// Later fixes to overlay.asm do not change this fixture.
/// </summary>
public static class Overlay
{
    /// <summary>SHA-256 of the fixture with LF line endings.</summary>
    const string Sha256 = "d4385841bac577f0578e83f3c6473159399a6f71149747beba1d712bc02861c3";

    const int BootBlockEnd = 0x8000;
    static readonly (int Start, int End)[] ValidityTags = { (0x6FFF0, 0x70000), (0x7FFF0, 0x80000) };

    static readonly Lazy<string> path = new(Find);

    public static string FixturePath => path.Value;

    /// <summary>The fixture's data records as (file offset, bytes): the CPU address &amp; 7FFFFh.</summary>
    public static IReadOnlyList<(int Offset, byte[] Bytes)> Records()
    {
        var text = File.ReadAllText(FixturePath).Replace("\r\n", "\n");
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        if (actual != Sha256)
            throw new InvalidOperationException($"{FixturePath} has SHA-256 {actual}, not the frozen fixture's {Sha256}");
        var records = new List<(int, byte[])>();
        uint upper = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line[0] != ':')
                throw new InvalidDataException($"not an Intel HEX record: {line}");
            var raw = Convert.FromHexString(line[1..]);
            if (raw.Aggregate(0, (sum, b) => sum + b) % 256 != 0)
                throw new InvalidDataException($"bad checksum: {line}");
            var (length, address, type) = (raw[0], raw[1] << 8 | raw[2], raw[3]);
            var data = raw[4..(4 + length)];
            switch (type)
            {
                case 0x00:
                    var offset = (int)((upper + (uint)address) & 0x7FFFF);
                    if (offset < BootBlockEnd || ValidityTags.Any(t => offset < t.End && offset + data.Length > t.Start))
                        throw new InvalidOperationException($"the overlay would write file 0x{offset:X5}: the boot block or a validity tag");
                    records.Add((offset, data));
                    break;
                case 0x01:
                    return records;
                case 0x02:
                    upper = (uint)(data[0] << 8 | data[1]) << 4;
                    break;
                case 0x04:
                    upper = (uint)(data[0] << 8 | data[1]) << 16;
                    break;
                case 0x03 or 0x05:
                    break;
                default:
                    throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"record type {type:X2}: {line}"));
            }
        }
        return records;
    }

    /// <summary>The overlay over <paramref name="car"/>'s bytes, in memory, as the ECU would hold it after a full write.</summary>
    public static FlashedImage Over(byte[] car)
    {
        var copy = car.ToArray();
        foreach (var (offset, bytes) in Records())
            bytes.CopyTo(copy, offset);
        return FlashImageBuilder.AsFlashedFull(FirmwareImage.FromBytes(copy));
    }

    static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "sim", "tests", "Ms43Sim.Tests", "fixtures", "overlay.hex");
            if (File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("no sim/tests/Ms43Sim.Tests/fixtures/overlay.hex above the test assembly");
    }
}
