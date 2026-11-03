using System.Security.Cryptography;

namespace Ms43Sim.Tests.Support;

/// <summary>
/// The reference bins live outside version control (bins/ and candidates/ in the main
/// MS43LAB checkout). Every read is checked against the SHA-256 recorded in the plan, and
/// every access returns a fresh copy so no test can change another test's input.
/// </summary>
public static class TestBins
{
    const string CarRelative = "bins/CAR_current_43X001_SP95_STAGE1.bin";

    static readonly Lazy<string> root = new(FindRoot);
    static readonly Dictionary<string, byte[]> cache = new();

    public static string Root => root.Value;

    public static byte[] CarBytes => Read(CarRelative,
        "8cfe31241aa90717195077f1d931ec3bd28e0e76b435a89472dc1fbdfe6c0717");

    public static byte[] ReleaseBytes => Read("bins/MS43X001_release_E46_M54B25.bin",
        "11a9cd31d545bee2a3e391f7ea24007c465557ce0412e7125494c8f8262975aa");

    public static byte[] StockBytes => Read("bins/MS43_430069_stock.bin",
        "0f97b32f0c5ad517f8834e33f909beac6771764f0ac79a12569344bda3f4443d");

    public static byte[] E85CamPortBytes => Read("candidates/CAR_43X001_SP95_STAGE1_E85CAMPORT.bin",
        "d21849afaca64db518ae35155e024ab54f391d2d18f4ce31920f58d59df89466");

    /// <summary>The live overlay V10 as built at fe5054f and sent to the owner on 2026-11-05.</summary>
    public static byte[] LiveOverlayV10Bytes => Read("bins/CAR_43X001_SP95_STAGE1_LIVE_OVERLAY_V10.bin",
        "708f767fd673ef4390c2c067e5b147af0d6005e2164c0efda80a2e363d8581f0");

    static byte[] Read(string relative, string expectedSha256)
    {
        lock (cache)
        {
            if (!cache.TryGetValue(relative, out var bytes))
            {
                var path = Path.Combine(Root, relative);
                bytes = File.ReadAllBytes(path);
                var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (actual != expectedSha256)
                    throw new InvalidOperationException(
                        $"{path} has SHA-256 {actual}, expected {expectedSha256}.");
                cache[relative] = bytes;
            }
            return (byte[])bytes.Clone();
        }
    }

    static string FindRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("MS43_BINS_ROOT");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            if (File.Exists(Path.Combine(fromEnvironment, CarRelative)))
                return fromEnvironment;
            throw new InvalidOperationException(
                $"MS43_BINS_ROOT is '{fromEnvironment}', but {CarRelative} is not there.");
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, CarRelative)))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            $"Cannot find {CarRelative}: set MS43_BINS_ROOT to the folder that holds bins/, " +
            "or run the tests from a checkout inside that folder.");
    }
}
