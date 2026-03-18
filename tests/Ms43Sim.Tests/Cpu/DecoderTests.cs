using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Cpu;

public class DecoderTests
{
    [Theory]
    [InlineData("E6F10080", 4, "MOV")]   [InlineData("F001", 2, "MOV")]     [InlineData("D180", 2, "EXTR")]
    [InlineData("D110", 2, "ATOMIC")]    [InlineData("D140", 2, "?")]       [InlineData("D7402000", 4, "EXTP")]
    [InlineData("DC07", 2, "EXTS")]      [InlineData("DC4D", 2, "EXTP")]    [InlineData("1A89E2FF", 4, "BFLDH")]
    [InlineData("8A030560", 4, "JB")]    [InlineData("9B42", 2, "TRAP")]    [InlineData("8B00", 2, "db?")]
    [InlineData("A758A7A7", 4, "SRVWDT")][InlineData("4AF2F1F0", 4, "BMOV")][InlineData("0124", 2, "ADDB")]
    [InlineData("D4521000", 4, "MOV")]   [InlineData("FB88", 2, "RETI")]    [InlineData("3B00", 2, "db?")]
    public void SizeAndMnemonic(string hex, int size, string mnemonic)
    {
        var b = Hex.Bytes(hex);
        var d = Decoder.Decode(b.Concat(new byte[4]).ToArray(), 0, 0x1000);
        Assert.Equal((size, mnemonic), (d.Size, d.Mnemonic));
    }
}
