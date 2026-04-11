using Ms43Sim.Core;

namespace Ms43Sim.Tests.Peripherals;

public class PortsTests
{
    sealed class Pins : IPinEnvironment
    {
        public readonly HashSet<(int, int)> High = new();
        public bool InputLevel(int port, int bit) => High.Contains((port, bit));
    }

    static (Ports Ports, SfrFile Sfrs, Pins Env) Make()
    {
        var sfrs = new SfrFile(new AccessInventory());
        sfrs.ResetAll();
        var env = new Pins();
        return (new Ports(sfrs, env), sfrs, env);
    }

    [Fact] public void Read_GivesPinsForInputs_AndTheLatchForOutputs()
    {
        var (_, s, e) = Make();
        e.High.Add((3, 7));
        s.Write16(0xFFC6, 0x0200);                       // DP3: P3.9 is an output
        s.Write16(0xFFC4, 0x0300);                       // latch bits 8 and 9
        Assert.Equal(0x0280, s.Read16(0xFFC4));          // 9 from the latch, 8 from its low pin, 7 from its high pin
    }

    [Fact] public void ReadModifyWrite_CopiesInputPinsIntoTheLatch()
    {
        var (p, s, e) = Make();
        e.High.Add((2, 0));
        s.Write16(0xFFC0, (ushort)(s.Read16(0xFFC0) | 0x0002));   // what BSET P2.1 does
        Assert.Equal(0x0003, p.Latch(2));
    }

    [Fact] public void LatchWrittenAsInput_ShowsOnceThePinBecomesAnOutput()
    {
        var (_, s, _) = Make();
        s.Write16(0xFFD4, 0x0040);
        Assert.Equal(0x0000, s.Read16(0xFFD4));
        s.Write16(0xFFD6, 0x0040);
        Assert.Equal(0x0040, s.Read16(0xFFD4));
    }

    [Fact] public void OpenDrainOutputAtOne_ReleasesThePin()
    {
        var (p, s, _) = Make();
        s.Write16(0xFFC2, 0x0001); s.Write16(0xF1C2, 0x0001); s.Write16(0xFFC0, 0x0001);
        Assert.Equal((false, 0x0001), (p.Pin(2, 0), s.Read16(0xFFC0) & 1));
    }

    [Fact] public void PinChanged_FiresForOutputWrites_AndForInputChanges()
    {
        var (p, s, e) = Make();
        var seen = new List<(int, int, bool)>();
        p.PinChanged += (port, bit, level) => seen.Add((port, bit, level));
        s.Write16(0xFFD2, 0x0001); s.Write16(0xFFD0, 0x0001);
        e.High.Add((8, 6)); p.InputChanged(8, 6);
        Assert.Equal(new[] { (7, 0, true), (8, 6, true) }, seen);
    }

    [Fact] public void P3Bit14_DoesNotExist()
    {
        var (_, s, e) = Make();
        e.High.Add((3, 14));
        s.Write16(0xFFC4, 0x4000);
        Assert.Equal(0, s.Read16(0xFFC4) & 0x4000);
    }
}
