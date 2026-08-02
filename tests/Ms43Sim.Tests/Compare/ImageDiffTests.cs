using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Compare;

/// <summary>The compare's calibration and code differences (plan 5 Task 8), on the car file and edits of it, as flashed.</summary>
public class ImageDiffTests
{
    static readonly Lazy<string> Csv = new(() =>
        CalibrationCatalogue.FindUpwards("calmaps_ms43x.csv", AppContext.BaseDirectory) ?? throw new FileNotFoundException("calmaps_ms43x.csv"));

    static readonly Lazy<CalibrationCatalogue> Tables = new(() => CalibrationCatalogue.Load(Csv.Value));

    static FirmwareImage Car => TestImages.Car.Image;

    static FirmwareImage Flashed(byte[] bytes) => TestImages.AsFlashed(bytes).Image;

    static byte[] CarWith(int offset, params byte[] bytes)
    {
        var edited = TestBins.CarBytes;
        bytes.CopyTo(edited, offset);
        return edited;
    }

    [Fact] public void SameImage_HasNoDifference()
    {
        Assert.Empty(CalibrationDiff.Of(Car, Car, Tables.Value));
        Assert.Empty(CodeDiff.Of(Car, Car));
    }

    [Fact] public void CarAgainstRelease_NamesTheTunesTables()
    {
        // TUNE_ANALYSIS.md lists both as tuned; its counts are the cells moved by hand after the axes were re-interpolated,
        // where the diff counts every changed byte.
        var (release, car) = (TestBins.ReleaseBytes, TestBins.CarBytes);
        int Changed(int at, int bytes) => Enumerable.Range(at, bytes).Count(i => release[i] != car[i]);
        var diffs = CalibrationDiff.Of(Flashed(release), Car, Tables.Value);
        Assert.Equal(new CalibrationDifference("ip_iga_ron98_pl__n__maf", 0x77315, 320, Changed(0x77315, 320)), diffs.Single(d => d.Name == "ip_iga_ron98_pl__n__maf"));
        Assert.Equal(new CalibrationDifference("ip_af_target_ron98__maf__n", 0x7D69C, 192, Changed(0x7D69C, 192)), diffs.Single(d => d.Name == "ip_af_target_ron98__maf__n"));
        Assert.True(Changed(0x77315, 320) >= 95, "the hand-moved cells change at least");
        Assert.All(diffs, d => Assert.InRange(d.Address, 0x70000u, 0x7FFFFu));
    }

    [XdfFact] public void WithTheXdf_TheFuelConstantIsNamed()
    {
        var catalogue = CalibrationCatalogue.Load(Csv.Value, CalibrationCatalogue.FindUpwards(XdfFactAttribute.XdfPath, AppContext.BaseDirectory));
        var edited = Flashed(CarWith(0x70E14, (byte)(TestBins.CarBytes[0x70E14] ^ 1)));
        Assert.Equal(new CalibrationDifference("c_inj_flow", 0x70E14, 2, 1), Assert.Single(CalibrationDiff.Of(Car, edited, catalogue)));
    }

    [Fact] public void WithoutTheXdf_AnEditedConstantIsUnnamed()
    {
        var edited = Flashed(CarWith(0x70E14, (byte)(TestBins.CarBytes[0x70E14] ^ 1)));
        Assert.Equal(new CalibrationDifference("unnamed", 0x70E14, 1, 1), Assert.Single(CalibrationDiff.Of(Car, edited, Tables.Value)));
    }

    [Fact] public void ChecksumFields_AreLeftOut()
    {
        // One cell of the RON98 ignition map edited; both as flashed, so the flasher's corrected calibration CRC differs too.
        var edited = Flashed(CarWith(0x77315 + 100, (byte)(TestBins.CarBytes[0x77315 + 100] + 1)));
        Assert.False(Car.Bytes.Slice(0x73FE0, 2).SequenceEqual(edited.Bytes.Slice(0x73FE0, 2)));
        Assert.Equal(new CalibrationDifference("ip_iga_ron98_pl__n__maf", 0x77315, 320, 1), Assert.Single(CalibrationDiff.Of(Car, edited, Tables.Value)));
        Assert.Empty(CodeDiff.Of(Car, edited));
    }

    [Fact] public void TransparentStub_ShowsTheCallSiteAndTheStub()
    {
        // R8: the RON98 ignition read at 0x2C650 calls a stub in free flash at 0x4B544, which makes the same call and returns.
        Assert.Equal(new byte[] { 0xDA, 0x0D, 0x9A, 0x02 }, TestBins.CarBytes[0x2C650..0x2C654]);
        var bytes = CarWith(0x2C650, 0xDA, 0x0C, 0x44, 0xB5);
        new byte[] { 0xDA, 0x0D, 0x9A, 0x02, 0xDB, 0x00 }.CopyTo(bytes, 0x4B544);
        var stubbed = Flashed(bytes);
        var code = CodeDiff.Of(Car, stubbed);
        Assert.Equal(new[] { (0x2C651u, 0x2C654u), (0x4B544u, 0x4B54Au) }, code.Select(d => (d.Start, d.End)));
        Assert.Equal(new[] { "0A:C650  DA 0C 44 B5  CALLS" }, code[0].Listing);
        Assert.Equal(new[] { "0C:B544  DA 0D 9A 02  CALLS", "0C:B548  DB 00        RETS" }, code[1].Listing);
        Assert.Empty(CalibrationDiff.Of(Car, stubbed, Tables.Value));
    }

    [Fact] public void Differences_LessThan16BytesApart_AreOneCodeRange()
    {
        var bytes = TestBins.CarBytes;
        bytes[0x50000] ^= 1;
        bytes[0x5000F] ^= 1;                                             // 15 bytes after: one range
        bytes[0x50030] ^= 1;                                             // 33 bytes after: another
        Assert.Equal(new[] { (0x50000u, 0x50010u), (0x50030u, 0x50031u) }, CodeDiff.Of(Car, Flashed(bytes)).Select(d => (d.Start, d.End)));
    }
}
