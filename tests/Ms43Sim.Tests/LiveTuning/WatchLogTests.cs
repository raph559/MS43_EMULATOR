using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.LiveTuning;

/// <summary>
/// Sub 11: one reply with the stock 0B B0 payload and then the watch record (88 bytes on the wire).
/// </summary>
public sealed class WatchLogTests
{
    private const ushort Page = 0x1D, Offset = 0x3316;            // even, inside the RON98 part-load map's page
    private static readonly byte[] Record = [1, 7, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09];

    private static void SeedEngine(Machine m)
    {
        for (var a = 0x8000u; a < 0x8500u; a += 2) m.Bus.Poke16(a, (ushort)(a * 37 + 11));
    }

    private static void SeedRecord(Machine m)
    {
        V10Fixture.PutByte(m, V10Fixture.Kind, Record[0]); V10Fixture.PutByte(m, V10Fixture.Count, Record[1]);
        m.Bus.Poke16(V10Fixture.Row, 0x0102); m.Bus.Poke16(V10Fixture.RowF, 0x0304);
        m.Bus.Poke16(V10Fixture.Col, 0x0506); m.Bus.Poke16(V10Fixture.ColF, 0x0708);
        V10Fixture.PutByte(m, V10Fixture.Cols, Record[10]);
    }

    private static byte[] StockPayload()
    {
        var m = V10Fixture.Direct(V10Fixture.Bytes, [0xDA, 0x0C, 0x7E, 0x46]);
        m.Bus.Poke16(0xF900, 2); m.Bus.Poke16(0xFD2C, 0x0400);    // the state V10Fixture.Boot gives the log's machine
        SeedEngine(m);
        V10Fixture.Execute(m, cycles: 400_000);
        return V10Fixture.Read(m, 0xF750, 73);
    }

    [Fact]
    public void LogReplyIsTheStockPayloadThenTheRecord()
    {
        var m = V10Fixture.Boot();
        SeedEngine(m); SeedRecord(m);
        var reply = V10Fixture.Command(m, [0x6D, 0x0B]);
        Assert.Equal(0x58, m.Bus.Peek16(0xF74E) & 255);
        Assert.Equal(0xA0, reply[0]);
        Assert.Equal(StockPayload(), reply[1..74]);
        Assert.Equal(Record, reply[74..85]);
    }

    [Fact]
    public void LogNeedsNoSessionAndStampsTheKeepAlive()
    {
        var m = V10Fixture.Boot();
        m.Bus.Poke16(0xE2A6, 1234);
        Assert.Equal(0xA0, V10Fixture.Command(m, [0x6D, 0x0B])[0]);
        Assert.Equal(1234, m.Bus.Peek16(0xE6B2));
    }

    [Fact] // RF2
    public void LogCopyRunsWithInterruptsHeldOff()
    {
        var m = V10Fixture.Boot();
        SeedRecord(m);
        m.Cpu.Psw = 0x0800;
        var start = (uint)V10Fixture.Symbol("wl_rec"); var end = (uint)V10Fixture.Symbol("wl_end");
        var seen = 0;
        for (var at = start; at < end; at += 2)
            m.OnInstruction(at, x => { seen++; Assert.True((x.Cpu.Psw & 0x0800) == 0, $"IEN set at {x.Cpu.Address:X5}"); });
        V10Fixture.Command(m, [0x6D, 0x0B]);
        Assert.True(seen > 10, $"only {seen} instructions seen");
    }

    [Fact] // RF2
    public void AnInterruptReadDuringTheLogNeverTearsTheReply()
    {
        // T1's handler at 0C:1030 saves R1..R15, calls reader 0D:029A on the watched map, restores them and returns.
        var image = V10Fixture.Bytes;
        var isr = new List<byte>();
        for (var r = 1; r < 16; r++) isr.AddRange([0xEC, (byte)(0xF0 + r)]);
        isr.AddRange([0xE6, 0xFC, (byte)(Offset & 0xFF), (byte)(Offset >> 8), 0xE6, 0xFD, (byte)(Page & 0xFF), 0x00, 0xDA, 0x0D, 0x9A, 0x02]);
        for (var r = 15; r >= 1; r--) isr.AddRange([0xFC, (byte)(0xF0 + r)]);
        isr.AddRange([0xFB, 0x88]);
        isr.ToArray().CopyTo(image, 0x41030);
        var m = V10Fixture.Boot(image);
        SeedRecord(m);
        V10Fixture.Arm(m, Page, Offset);
        V10Fixture.PutByte(m, 0xFB6B, 3); m.Bus.Poke16(0xE390, 0x8000); V10Fixture.PutByte(m, 0xFB6C, 5);
        m.Bus.Poke16(0xE392, 0x4000); V10Fixture.PutByte(m, 0xFB6D, 12);
        m.Cpu.Psw = 0x0800;
        var entered = false;
        m.OnInstruction((uint)V10Fixture.Symbol("wl_rec"), x =>
        {
            x.Sfrs.Write16(0xFF9E, 0x004C);
            x.Interrupts.Request(InterruptSources.ByName("T1"));
        });
        m.OnInstruction(0x84, x => { entered = true; x.Cpu.Jump(0x0C, 0x1030); });
        var reply = V10Fixture.Command(m, [0x6D, 0x0B]);
        Assert.True(entered, "the interrupt never ran");
        byte[] after = [1, 8, 0, 3, 0x80, 0x00, 0, 5, 0x40, 0x00, 12];
        var record = reply[74..85];
        Assert.True(record.SequenceEqual(Record) || record.SequenceEqual(after),
            $"a torn record: {Convert.ToHexString(record)}");
        Assert.Equal(8, V10Fixture.Byte(m, V10Fixture.Count));        // the interrupt's read was counted once
    }
}
