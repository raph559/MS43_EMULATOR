namespace Ms43Sim.Tests.Support;

public class TestBinsTests
{
    [Fact]
    public void CarFile_IsFoundAndHashChecked()
    {
        Assert.Equal(0x80000, TestBins.CarBytes.Length);
        Assert.True(File.Exists(Path.Combine(TestBins.Root, "bins", "CAR_current_43X001_SP95_STAGE1.bin")));
    }

    [Fact]
    public void HexParser_AcceptsSpacesAndCase() =>
        Assert.Equal(new byte[] { 0xE6, 0xF1, 0x00, 0x80 }, Hex.Bytes("e6 F1 0080"));
}
