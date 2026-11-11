using System.Globalization;
using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>
/// The assembler's Intel HEX output as the tests read it, and the checkout it is read from. A record that would touch the
/// boot block, a validity tag or the programming signature at 0x4FFE6–0x4FFFF is refused.
/// </summary>
public static class OverlayHex
{
    static readonly (int Start, int End)[] Forbidden =
    {
        (0x00000, 0x08000),                                  // the boot block
        (0x4FFE6, 0x50000),                                  // the programming signature
        (0x6FFF0, 0x70000), (0x7FFF0, 0x80000),              // the validity tags
    };

    /// <summary>The folder holding sim/: the checkout the test assembly was built in.</summary>
    public static string RepositoryRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "sim", "Ms43Sim.sln")))
                    return dir.FullName;
            }
            throw new InvalidOperationException("no sim/Ms43Sim.sln above the test assembly");
        }
    }

    /// <summary>Reads Intel HEX records (data, extended segment and linear addresses), refusing the forbidden ranges.</summary>
    public static IReadOnlyList<(int Offset, byte[] Bytes)> Parse(string text)
    {
        var result = new List<(int, byte[])>();
        uint upper = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
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
                    if (Forbidden.Any(f => offset < f.End && offset + data.Length > f.Start))
                        throw new InvalidOperationException($"the overlay would write file 0x{offset:X5}: the boot block, a tag or the signature");
                    result.Add((offset, data));
                    break;
                case 0x01:
                    return result;
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
        return result;
    }
}
