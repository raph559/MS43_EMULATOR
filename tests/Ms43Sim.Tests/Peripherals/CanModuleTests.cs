using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Peripherals;

public class CanModuleTests
{
    static TestMachine Can() => TestMachine.Create("0DFF").WriteSfr(0xFF12, 0x0004);   // SYSCON.XPEN

    /// <summary>CSR := 0041h, BTR := 3601h, object obj's UAR, MCFG 88h and MCR, then CSR := 0000h; the cycle of that last write.</summary>
    static ulong Transmit(TestMachine t, int obj, ushort uar, ushort mcr)
    {
        var bus = t.M.Bus;
        var b = 0xEF00u + 16u * (uint)obj;
        bus.Write16(0xEF00, 0x0041);
        bus.Write16(0xEF04, 0x3601);
        bus.Write16(b + 2, uar);
        bus.Write8(b + 6, 0x88);
        bus.Write16(b, mcr);
        bus.Write16(0xEF00, 0x0000);
        return t.M.Cycles;
    }

    [Fact] public void AfterReset_CsrReads0001h() => Assert.Equal(0x0001, Can().M.Bus.Peek16(0xEF00));

    [Fact] public void McrElements_AreSetResetOrLeft()
    {
        var t = Can();
        t.M.Bus.Write16(0xEF10, 0x5555); t.M.Bus.Write16(0xEF10, 0xFFBF);
        Assert.Equal(0x5595, t.M.Bus.Read16(0xEF10));
    }

    [Fact] public void TransmitObject_IsSentAfter111BitTimes()
    {
        var t = Can(); var bus = t.M.Bus;
        var start = Transmit(t, 12, 0xC062, 0x6695);
        t.M.Scheduler.Advance(start + 5_327); Assert.Equal(0x2000, bus.Peek16(0xEFC0) & 0x3000);
        t.M.Scheduler.Advance(start + 5_328);
        Assert.Equal((0x1000, 0x0100, 0x0800), (bus.Peek16(0xEFC0) & 0x3000, bus.Peek16(0xEFC0) & 0x0300, bus.Peek16(0xEF00) & 0x0800));
        Assert.Equal((0x316u, 1L), (t.M.Can.Transmitted[0].Id, t.M.Can.Transmitted[0].Count));
    }

    [Fact] public void CpuUpd_HoldsTheTransmission()
    {
        var t = Can();
        var start = Transmit(t, 12, 0xC062, 0x6A95);
        t.M.Scheduler.Advance(start + 20_000);
        Assert.Equal((0x2000, 0), (t.M.Bus.Peek16(0xEFC0) & 0x3000, t.M.Can.Transmitted.Count));
    }

    [Fact] public void LowestObject_IsSentFirst()
    {
        var t = Can(); var bus = t.M.Bus;
        bus.Write16(0xEFD2, 0x2065); bus.Write8(0xEFD6, 0x88); bus.Write16(0xEFD0, 0x6695);   // object 13: 329h, while INIT = 1
        var start = Transmit(t, 12, 0xC062, 0x6695);
        t.M.Scheduler.Advance(start + 5_328);
        Assert.Equal((0x316u, 0x2000), (t.M.Can.Transmitted.Single().Id, bus.Peek16(0xEFD0) & 0x3000));
        t.M.Scheduler.Advance(start + 10_656);
        Assert.Equal(0x329u, t.M.Can.Transmitted[1].Id);
    }

    [Fact] public void Deliver_StoresIntoTheMatchingReceiveObject_AndFlagsAnOverrun()
    {
        var t = Can(); var bus = t.M.Bus;
        bus.Write16(0xEF06, 0xFFFF);                                        // GMS: all identifier bits compared
        bus.Write16(0xEF12, 0x602A); bus.Write8(0xEF16, 0x80);              // object 1: ID 153h, DLC 8, receive
        bus.Write16(0xEF10, 0x5599); bus.Write16(0xEF00, 0x0000);           // MSGVAL and RXIE set
        t.M.Can.Deliver(new CanFrame(0x153, false, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Equal((0x0200, 0x01, 0x1000), (bus.Peek16(0xEF10) & 0x0300, (int)bus.Read8(0xEF17), bus.Peek16(0xEF00) & 0x1000));
        t.M.Can.Deliver(new CanFrame(0x153, false, new byte[8]));
        Assert.Equal(0x0800, bus.Peek16(0xEF10) & 0x0C00);                  // MSGLST
    }

    [Fact] public void InterruptLine_RequestsXp0_WhenIeIsSet()
    {
        var t = Can();
        var start = Transmit(t, 12, 0xC062, 0x66A5);
        t.M.Bus.Write16(0xEF00, 0x0006);                                    // IE and SIE, INIT stays 0
        t.M.Scheduler.Advance(start + 5_328);
        Assert.Equal(0x80, t.Sfr(0xF186) & 0x80);
    }

    [Fact] public void MessageRam_SurvivesACpuReset()
    {
        var t = Can();
        t.M.Bus.Write8(0xEF17, 0x5A);
        t.M.Reset(ResetKind.Software);
        t.WriteSfr(0xFF12, 0x0004);
        Assert.Equal((0x5A, 0x0001), ((int)t.M.Bus.Read8(0xEF17), (int)t.M.Bus.Peek16(0xEF00)));
    }

    [Fact] public void RemoteRequest_IsReported()
    {
        var t = Can();
        t.M.Bus.Write16(0xEF12, 0x602A); t.M.Bus.Write8(0xEF16, 0x80);      // receive object
        t.M.Bus.Write16(0xEF10, 0x6695); t.M.Bus.Write16(0xEF00, 0x0000);   // TXRQ on it: a remote frame
        t.M.Scheduler.Advance(t.M.Cycles + 10_000);
        Assert.Contains(t.M.Events.All.OfType<UnsupportedEvent>(), e => e.What.Contains("CAN"));
    }

    [Fact] public void ReadingTheStatusByte_ClearsTheStatusChangeInterrupt()
    {
        var t = Can(); var bus = t.M.Bus;
        var start = Transmit(t, 12, 0xC062, 0x6695);
        bus.Write16(0xEF00, 0x0004);                                        // SIE
        t.M.Scheduler.Advance(start + 5_328);
        Assert.Equal(0x01, bus.Peek16(0xEF02) & 0xFF);                       // INTID: status change
        bus.Read8(0xEF00);                                                  // the control byte: no effect
        Assert.Equal(0x01, bus.Peek16(0xEF02) & 0xFF);
        bus.Read8(0xEF01);                                                  // the status byte
        Assert.Equal(0x00, bus.Peek16(0xEF02) & 0xFF);
    }

    [Fact] public void Btr_IsWritableOnlyWithCce()
    {
        var t = Can(); var bus = t.M.Bus;
        bus.Write16(0xEF04, 0x3601);                                        // CSR = 0001h: no CCE
        Assert.Equal(0x0000, bus.Peek16(0xEF04));
        bus.Write16(0xEF00, 0x0041); bus.Write16(0xEF04, 0x3601);
        Assert.Equal(0x3601, bus.Peek16(0xEF04));
    }

    [Fact] public void Deliver_ExtendedFrame_UsesTheLongMask()
    {
        var t = Can(); var bus = t.M.Bus;
        bus.Write16(0xEF08, 0xFFFF); bus.Write16(0xEF0A, 0xF8FF);           // UGML/LGML: every identifier bit compared
        bus.Write16(0xEF22, 0x3412); bus.Write16(0xEF24, 0x0856);           // object 2: ID 02468AC1h (ID28-21 12h, ID20-13 34h, ID12-5 56h, ID4-0 01h)
        bus.Write8(0xEF26, 0x84);                                           // DLC 8, receive, extended
        bus.Write16(0xEF20, 0x5595); bus.Write16(0xEF00, 0x0000);
        t.M.Can.Deliver(new CanFrame(0x02468AC1 ^ 0x80, true, new byte[] { 9 }));   // one bit off: no match
        t.M.Can.Deliver(new CanFrame(0x02468AC1, true, new byte[] { 7 }));
        Assert.Equal((0x0200, 7, 0x10), (bus.Peek16(0xEF20) & 0x0300, (int)bus.Read8(0xEF27), bus.Read8(0xEF26) & 0xF0));
    }
}
