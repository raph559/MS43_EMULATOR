using System.Globalization;
using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>
/// The first live-tuning overlay (plan 6, protocol 2) as the tests run it: the assembler's Intel HEX output of its source,
/// kept as fixtures/live_overlay_v2.hex (the source is no longer in the repository), applied to the car file in memory and
/// built as flashed. No image is written to disk here. A record that would touch the boot block, a validity tag or the
/// programming signature at 0x4FFE6–0x4FFFF is refused. The fixture is pinned by the program identity it gives
/// (<see cref="ProgramIdentity.Ms43X001LiveOverlay"/>).
/// </summary>
public static class LiveOverlayBin
{
    public const string FixtureRelative = "sim/tests/Ms43Sim.Tests/fixtures/live_overlay_v2.hex";

    static readonly (int Start, int End)[] Forbidden =
    {
        (0x00000, 0x08000),                                  // the boot block
        (0x4FFE6, 0x50000),                                  // the programming signature
        (0x6FFF0, 0x70000), (0x7FFF0, 0x80000),              // the validity tags
    };

    static readonly Lazy<IReadOnlyList<(int Offset, byte[] Bytes)>> records = new(() => Parse(File.ReadAllText(FixturePath)));
    static readonly Lazy<byte[]> bytes = new(() => Apply(TestBins.CarBytes, records.Value));

    public static string FixturePath => Path.Combine(RepositoryRoot, FixtureRelative);

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

    /// <summary>The fixture's data records as (file offset, bytes): the CPU address &amp; 7FFFFh.</summary>
    public static IReadOnlyList<(int Offset, byte[] Bytes)> Records() => records.Value;

    /// <summary>The car file's bytes with the overlay's records, before any checksum is fixed.</summary>
    public static byte[] Bytes => (byte[])bytes.Value.Clone();

    /// <summary>The overlay over the car file, as the ECU holds it after a full write.</summary>
    public static FlashedImage Image => TestImages.AsFlashed(Bytes);

    /// <summary>The overlay with calibration edits baked over it, as flashed (in memory only).</summary>
    public static FlashedImage With(params (int Offset, byte[] New)[] edits)
    {
        var copy = Bytes;
        foreach (var (offset, edit) in edits)
            edit.CopyTo(copy, offset);
        return TestImages.AsFlashed(copy);
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

    static byte[] Apply(byte[] car, IEnumerable<(int Offset, byte[] Bytes)> patch)
    {
        foreach (var (offset, data) in patch)
            data.CopyTo(car, offset);
        return car;
    }
}
