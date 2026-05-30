using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Diagnostics;

public class FaultMemoryTests
{
    static byte[] Record(params int[] b) => b.Select(x => (byte)x).Concat(new byte[12 - b.Length]).ToArray();

    /// <summary>CRC-16/ARC: reflected polynomial A001h, no final XOR, from <paramref name="crc"/>.</summary>
    static ushort Crc16Arc(ReadOnlySpan<byte> data, ushort crc)
    {
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        return crc;
    }

    [Fact] public void Decode_AStoredThrottleAdaptationFault()
    {
        var e = FaultMemory.Decode(TestImages.Car.Image, 114, Record(0x74, 0x00, 0x01, 0x28, 0x26, 0xDB, 0x7C, 0x5B));
        Assert.Equal(("tps_ad", (byte)0x73, 2, "P1634"), (e.Name, e.Ds2Code, e.Type, e.PCode));
        Assert.Equal((true, true, false, true, true), (e.Stored, e.Present, e.Sporadic, e.MilRequest, e.EmlRequest));
        Assert.Equal((1, 0x28), (e.Frequency, e.Heal));
        Assert.Equal(new byte[] { 0x26, 0xDB, 0x7C, 0x5B }, e.Environment);       // apart: a tuple compares an array by reference
    }

    [Fact] public void Decode_TheDscTimeoutOfAnAscCar_IsDetectedButNeverStored_AndExplained()
    {
        var e = FaultMemory.Decode(TestImages.Car.Image, 80, Record(0x08));
        Assert.Equal(("tout_asr_3", 3, "P1613", false, false), (e.Name, e.Type, e.PCode, e.Stored, e.Present));
        Assert.NotNull(FaultMemory.Explanation(e));
    }

    [Fact] public void Decode_TheEngineOffTimeOfAFirstStart_IsDetectedButNeverStored_AndExplained()
    {
        var e = FaultMemory.Decode(TestImages.Car.Image, 12, Record(0x14));
        Assert.Equal(("t_es", 2, false), (e.Name, e.Type, e.Stored));
        Assert.NotNull(FaultMemory.Explanation(e));
    }

    [Fact] public void Read_ListsAInOrder_AndBWithoutRepeats()
    {
        var m = Machine.Create(TestImages.Car);
        void Put(int a16, params int[] bytes) { for (var i = 0; i < bytes.Length; i++) m.Bus.ExternalRam[a16 - 0x8000 + i] = (byte)bytes[i]; }
        Put(0x81BB, 2, 3);                                                    // counts: A = 2, B = 3
        Put(0x9974, 114, 74); Put(0x9A16, 156, 114, 74);
        Put(0x91DA + 12 * 114, 0x74); Put(0x91DA + 12 * 74, 0x78); Put(0x91DA + 12 * 156, 0x14);
        var s = FaultMemory.Read(m);
        Assert.Equal(new[] { "tps_ad", "ecu" }, s.Stored.Select(e => e.Label));
        Assert.Equal(new[] { "tam_can", "tps_ad", "ecu" }, s.Detected.Select(e => e.Label));
    }

    [Fact] public void UnrecognisedLayout_IsNotDecoded()
    {
        var bytes = TestBins.CarBytes.ToArray();
        bytes[0x6D08E + 22 * 74] ^= 0xFF;                                     // a moved or edited descriptor table
        var image = FlashImageBuilder.Raw(FirmwareImage.FromBytes(bytes));
        Assert.False(FaultMemory.Recognises(image.Image));
        var s = FaultMemory.Read(Machine.Create(image));
        Assert.Equal((false, 0, 0), (s.Recognised, s.Stored.Count, s.Detected.Count));
    }

    [Fact] public void SavedInEeprom_DecodesTheAfterRunRecord()
    {
        var ee = Enumerable.Repeat((byte)0xFF, 2048).ToArray();
        var rec = new byte[0x80];
        rec[2] = 0x00; rec[3] = 0x04;                                         // 883Ch = 0400h
        rec[4] = 74;                                                          // the path of entry 0
        new byte[] { 0x78, 0x00, 0x01, 0x28, 0x5B, 0x7C, 0x00, 0x04, 0x00, 0x00, 0x21 }.CopyTo(rec, 0x0E);
        rec[0x7C] = 1;
        var crc = Crc16Arc(rec.AsSpan(0, 0x7E), 0x000F);                      // test helper: reflected A001h
        rec[0x7E] = (byte)crc; rec[0x7F] = (byte)(crc >> 8);
        rec.CopyTo(ee, 0x56);
        var e = Assert.Single(FaultMemory.SavedInEeprom(TestImages.Car.Image, ee));
        Assert.Equal(("ecu", "P0604", true), (e.Name, e.PCode, e.Stored));
        ee[0x60] ^= 1;
        Assert.Empty(FaultMemory.SavedInEeprom(TestImages.Car.Image, ee));    // a broken CRC: nothing
    }
}
