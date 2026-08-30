using Ms43Sim.Core;
using Ms43Sim.Tests.Support;

namespace Ms43Sim.Tests.Vehicle;

/// <summary>
/// The tester against a fake ECU end of the line, an <see cref="IKLinePort"/> of the test's own, so every cycle
/// is the test's. A byte lasts 11 bit times: 27,500 cycles at 9,600 baud (2,500 a bit) and 2,112 at 125,000
/// baud (192 a bit). The fake echoes each tester byte at its end, and answers a whole request with the frame the
/// test scripted, a byte at a time, after a delay the test sets. A reply byte lasts 27,456 cycles by default,
/// the ECU's own DS2 rate (2,496 a bit).
/// </summary>
public class KLineTesterTests
{
    const ulong Gap = 24_000;                                            // 1 ms, after a timeout or a malformed answer
    const ulong Pause = 480_000;                                         // 20 ms, after a reply: the app's pause after each sample
    const string ConnectFrame = "12 08 91 01 E8 48 00 2A", DisconnectFrame = "12 08 91 00 25 80 00 2E";
    const string PollFrame = "12 05 0B B0 AC", Ack = "12 04 A0 B6";
    const uint S0Rbuf = 0xFEB2;

    sealed record Wire(ulong Start, ulong End, byte Value, double BitCycles);

    /// <summary>
    /// The ECU's end of the line. It hears what the tester puts on the line, echoes each byte back when its
    /// stop bit ends (and refuses a byte put on the line while another is still on it), and once a whole request is
    /// in (its length byte says when) answers the next scripted frame, or says nothing.
    /// </summary>
    sealed class EcuEnd(Scheduler scheduler) : IKLinePort
    {
        readonly List<byte> request = new();
        readonly Queue<(byte[] Frame, ulong Delay, ulong ByteCycles)> script = new();
        (byte[] Frame, ulong Delay, ulong ByteCycles)? always;
        IKLineDevice? device;
        ulong requestStart;

        /// <summary>Every byte the tester put on the line.</summary>
        public List<Wire> Heard { get; } = new();

        /// <summary>Each whole request frame heard: where its first byte began, and its bytes.</summary>
        public List<(ulong Start, byte[] Frame)> Requests { get; } = new();

        /// <summary>How long one reply byte lasts on the line, read when a reply is scheduled.</summary>
        public ulong ByteCycles { get; set; } = 27_456;

        public event Action<KLineNote>? Noted;

        public bool Busy => Heard.Any(w => w.End > scheduler.Now);

        public void Attach(IKLineDevice device) => this.device = device;

        public void Send(byte value, double bitCycles)
        {
            if (Busy)
                throw new InvalidOperationException("the tester put a byte on the line while its last one was still on it");
            var start = scheduler.Now;
            var wire = new Wire(start, start + (ulong)Math.Round(11 * bitCycles), value, bitCycles);
            Heard.Add(wire);
            scheduler.Schedule(wire.End, "fake ECU hears", () => Hear(wire));
        }

        void Hear(Wire wire)
        {
            if (request.Count == 0)
                requestStart = wire.Start;
            request.Add(wire.Value);
            device!.OnLineByte(wire.Value, own: true, scheduler.Now);       // the tester's own echo
            if (request.Count < 2 || request.Count < request[1])
                return;
            var frame = request.ToArray();
            request.Clear();
            Requests.Add((requestStart, frame));
            if (script.Count > 0 || always is not null)
                Answer(script.Count > 0 ? script.Dequeue() : always!.Value);
        }

        void Answer((byte[] Frame, ulong Delay, ulong ByteCycles) reply)
        {
            for (var i = 0; i < reply.Frame.Length; i++)
                Say(reply.Frame[i], scheduler.Now + reply.Delay + (ulong)(i + 1) * reply.ByteCycles);
        }

        /// <summary>The ECU answers the next request it has no answer queued for with this frame, <paramref name="delay"/> cycles after the request ends.</summary>
        public void Reply(string hex, ulong delay = 0) => script.Enqueue((Hex.Bytes(hex), delay, ByteCycles));

        /// <summary>The ECU answers every request that has no answer queued with this frame.</summary>
        public void Always(string hex, ulong delay = 0) => always = (Hex.Bytes(hex), delay, ByteCycles);

        /// <summary>One byte from the ECU arrives in cycle <paramref name="at"/>, asked for or not.</summary>
        public void Say(byte value, ulong at) =>
            scheduler.Schedule(at, "fake ECU byte", () => device!.OnLineByte(value, own: false, scheduler.Now));

        public void Note(KLineNote note) => Noted?.Invoke(note);
    }

    static (Scheduler S, EcuEnd Ecu, KLineTester Tester) Rig()
    {
        var s = new Scheduler();
        var ecu = new EcuEnd(s);
        var tester = new KLineTester(s);
        tester.Plug(ecu);
        return (s, ecu, tester);
    }

    static KLineAction Do(KLineVerb verb, string hex = "") => new(verb, Hex.Bytes(hex), 0, 1);

    static KLineAction Every(double seconds, string hex) => new(KLineVerb.Every, Hex.Bytes(hex), seconds, 1);

    static IEnumerable<string> Commands(EcuEnd ecu) => ecu.Requests.Select(r => Convert.ToHexString(r.Frame, 2, 2));

    // ---- DS2 frames ----

    [Fact] public void Ds2Frame_HasLengthAndXor()
    {
        Assert.Equal(Hex.Bytes("12 05 0B B0 AC"), Ds2.Frame(Hex.Bytes("0B B0")));
        Assert.Equal(Hex.Bytes(ConnectFrame), Ds2.Frame(Hex.Bytes("91 01 E8 48 00")));
        Assert.Equal(Hex.Bytes(DisconnectFrame), Ds2.Frame(Hex.Bytes("91 00 25 80 00")));
        Assert.Equal(Hex.Bytes("12 03 11"), Ds2.Frame(Array.Empty<byte>()));
        Assert.Equal(Hex.Bytes("00 04 A0 A4"), Ds2.Frame(Hex.Bytes("A0"), address: 0x00));
    }

    [Fact] public void Ds2Frame_CountsEveryByteInItsLength_SoThePayloadHoldsAtMost252()
    {
        var most = Ds2.Frame(new byte[252]);
        Assert.Equal((255, 255), (most.Length, most[1]));
        Assert.True(Ds2.IsFrame(most));
        Assert.Throws<ArgumentException>(() => Ds2.Frame(new byte[253]));
    }

    [Theory]
    [InlineData("12 05 0B B0 AC", true)]
    [InlineData("12 04 A0 B6", true)]
    [InlineData("12 03 11", true)]               // the shortest frame: no payload
    [InlineData("13 04 A0 B7", true)]            // the address is not IsFrame's business
    [InlineData("12 05 0B B0 AD", false)]        // the xor is wrong
    [InlineData("12 06 0B B0 AC", false)]        // the length byte says 6 on 5 bytes
    [InlineData("12 04 0B B0 AC", false)]        // the length byte says 4 on 5 bytes
    [InlineData("12 04 A0", false)]              // cut off
    [InlineData("12 02 10", false)]              // the length byte says 2 on 3 bytes, though the xor is right
    [InlineData("02 02", false)]                 // the length and the xor are right, but a frame has at least 3 bytes
    [InlineData("12", false)]
    [InlineData("", false)]
    public void IsFrame_MeansTheLengthAndTheXorAreRight(string hex, bool frame) =>
        Assert.Equal(frame, Ds2.IsFrame(Hex.Bytes(hex)));

    // ---- connect, disconnect and the baud rate ----

    [Fact] public void Connect_SendsTheAppsFrame_At9600_AndMovesTo125000OnTheReply()
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply(Ack, delay: 10_000);
        tester.Apply(Do(KLineVerb.Connect));
        s.Advance(339_823);                                              // 8 bytes of 27,500 end at 220,000; the ECU starts 10,000 later and its 4 bytes of 27,456 end at 339,824
        Assert.Equal(9_600, tester.Baud);
        Assert.Empty(tester.Exchanges);
        s.Advance(339_824);
        Assert.Equal(125_000, tester.Baud);

        Assert.Equal(Hex.Bytes(ConnectFrame), ecu.Heard.Select(w => w.Value));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (ulong)i * 27_500), ecu.Heard.Select(w => w.Start));   // back to back
        Assert.All(ecu.Heard, w => Assert.Equal(2_500.0, w.BitCycles));
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal((0UL, 9_600, 339_824UL, KLineOutcome.Replied), (exchange.SentAt, exchange.Baud, exchange.RepliedAt, exchange.Outcome));
        Assert.Equal(Hex.Bytes(ConnectFrame), exchange.Request);
        Assert.Equal(Hex.Bytes(Ack), exchange.Reply);
    }

    [Fact] public void Connect_AnsweredWithAnotherStatus_StaysAt9600()
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply("12 04 B0 A6", delay: 10_000);                         // B0: a parameter error
        tester.Apply(Do(KLineVerb.Connect));
        s.Advance(339_824);
        Assert.Equal(KLineOutcome.Replied, Assert.Single(tester.Exchanges).Outcome);
        Assert.Equal(9_600, tester.Baud);
    }

    [Fact] public void Connect_AnsweredWithAnA0FrameThatIsMalformed_StaysAt9600()
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply("12 04 A0 B7", delay: 10_000);                         // the xor is wrong
        tester.Apply(Do(KLineVerb.Connect));
        s.Advance(339_824);
        Assert.Equal(KLineOutcome.Malformed, Assert.Single(tester.Exchanges).Outcome);
        Assert.Equal(9_600, tester.Baud);
    }

    [Fact] public void Connect_NotAnswered_TimesOutAfter500ms_AndStaysAt9600()
    {
        var (s, ecu, tester) = Rig();
        tester.Apply(Do(KLineVerb.Connect));
        s.Advance(12_219_999);                                           // the request ends at 220,000; 500 ms is 12,000,000 cycles
        Assert.Empty(tester.Exchanges);
        s.Advance(12_220_000);
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal(KLineOutcome.TimedOut, exchange.Outcome);
        Assert.Equal(9_600, tester.Baud);
        s.Advance(60_000_000);
        Assert.Equal(8, ecu.Heard.Count);                                // nothing is polling: nothing more was sent
    }

    [Fact] public void AfterTheConnect_TheTesterPollsAt125000()
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply(Ack, delay: 10_000);                                   // the connect gets its answer, the polls none
        tester.Apply(Do(KLineVerb.Connect));
        tester.Apply(Do(KLineVerb.Poll));
        s.Advance(819_824 + 5 * 2_112);                                  // connected at 339,824; the poll leaves 20 ms later, 5 bytes of 2,112
        var poll = ecu.Heard.Skip(8).ToArray();
        Assert.Equal(Hex.Bytes(PollFrame), poll.Select(w => w.Value));
        Assert.Equal(819_824UL, poll[0].Start);
        Assert.All(poll, w => Assert.Equal(192.0, w.BitCycles));
    }

    [Fact] public void AConnect_AlwaysLeavesAt9600_EvenFromHighSpeed()
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply(Ack, delay: 10_000);
        tester.Apply(Do(KLineVerb.Connect));
        s.Advance(339_824);
        Assert.Equal(125_000, tester.Baud);
        tester.Apply(Do(KLineVerb.Connect));
        s.Advance(819_824);
        Assert.Equal(9_600, tester.Baud);
        Assert.Equal(2_500.0, ecu.Heard[8].BitCycles);
        Assert.Equal(Hex.Bytes(ConnectFrame)[0], ecu.Heard[8].Value);
    }

    [Fact] public void Disconnect_SendsTheAppsFrame_AtTheCurrentBaud_AndMovesTo9600OnTheReply()
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply(Ack, delay: 10_000);
        tester.Apply(Do(KLineVerb.Connect));
        s.Advance(339_824);                                              // connected: 125,000 baud
        ecu.ByteCycles = 2_112;                                          // and the ECU answers at 125,000 too
        ecu.Reply(Ack, delay: 1_000);
        tester.Apply(Do(KLineVerb.Disconnect));                          // leaves 20 ms after the reply, at 819,824
        s.Advance(846_167);                                              // 8 bytes of 2,112 end at 836,720; the ECU starts 1,000 later; 4 bytes of 2,112
        Assert.Equal(125_000, tester.Baud);
        s.Advance(846_168);
        Assert.Equal(9_600, tester.Baud);

        var sent = ecu.Heard.Skip(8).ToArray();
        Assert.Equal(Hex.Bytes(DisconnectFrame), sent.Select(w => w.Value));
        Assert.Equal(819_824UL, sent[0].Start);
        Assert.All(sent, w => Assert.Equal(192.0, w.BitCycles));
        var exchange = tester.Exchanges[1];
        Assert.Equal((125_000, 846_168UL, KLineOutcome.Replied), (exchange.Baud, exchange.RepliedAt, exchange.Outcome));
    }

    [Fact] public void ASendOfTheConnectPayload_IsNotAConnect_ItLeavesTheBaudAlone()
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply(Ack, delay: 10_000);
        tester.Apply(Do(KLineVerb.Send, "91 01 E8 48 00"));
        s.Advance(339_824);
        Assert.Equal(KLineOutcome.Replied, Assert.Single(tester.Exchanges).Outcome);
        Assert.Equal(9_600, tester.Baud);
    }

    [Fact] public void ASendOfTheDisconnectPayload_IsNotADisconnect_ItLeavesTheBaudAlone()
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply(Ack, delay: 10_000);
        tester.Apply(Do(KLineVerb.Connect));
        s.Advance(339_824);                                              // connected: 125,000 baud
        ecu.ByteCycles = 2_112;
        ecu.Reply(Ack, delay: 1_000);
        tester.Apply(Do(KLineVerb.Send, "91 00 25 80 00"));              // leaves at 819,824; answered at 846,168, as the disconnect is
        s.Advance(846_168);
        Assert.Equal(KLineOutcome.Replied, tester.Exchanges[1].Outcome);
        Assert.Equal(125_000, tester.Baud);
    }

    // ---- the poll, the timeout and the pace ----

    [Fact] public void Poll_RepeatsTheAppsRequest_20MillisecondsAfterEachReply()
    {
        var (s, ecu, tester) = Rig();
        ecu.Always(Ack, delay: 10_000);
        tester.Apply(Do(KLineVerb.Poll));
        s.Advance(1_612_148);                                            // a request is 137,500 cycles, the ECU's answer 10,000 + 109,824 more, then 480,000: 737,324 in all
        Assert.Equal(new[] { 0UL, 737_324UL, 1_474_648UL }, ecu.Requests.Select(r => r.Start));
        Assert.All(ecu.Requests, r => Assert.Equal(Hex.Bytes(PollFrame), r.Frame));
        Assert.Equal(new[] { 257_324UL, 994_648UL }, tester.Exchanges.Select(x => x.RepliedAt!.Value));
        Assert.All(tester.Exchanges, x => Assert.Equal(KLineOutcome.Replied, x.Outcome));
        for (var i = 0; i < 2; i++)
            Assert.Equal(Pause, ecu.Requests[i + 1].Start - tester.Exchanges[i].RepliedAt!.Value);
    }

    [Fact] public void NoReply_TimesOutAfter400ms_AndPollingGoesOn()
    {
        var (s, ecu, tester) = Rig();
        tester.Apply(Do(KLineVerb.Poll));
        s.Advance(9_737_499);                                            // the request ends at 137,500; 400 ms is 9,600,000 cycles
        Assert.Empty(tester.Exchanges);
        s.Advance(9_737_500);
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal(KLineOutcome.TimedOut, exchange.Outcome);
        Assert.Null(exchange.RepliedAt);
        Assert.Null(exchange.Reply);
        Assert.Equal((0UL, 9_600), (exchange.SentAt, exchange.Baud));
        Assert.Equal(Hex.Bytes(PollFrame), exchange.Request);

        s.Advance(9_761_499);
        Assert.Equal(5, ecu.Heard.Count);                                // the next request leaves 1 ms after the timeout
        s.Advance(9_761_500);
        Assert.Equal(9_761_500UL, ecu.Heard[5].Start);
        s.Advance(19_499_000);                                           // and times out in its turn
        Assert.Equal(new[] { KLineOutcome.TimedOut, KLineOutcome.TimedOut }, tester.Exchanges.Select(x => x.Outcome));
        s.Advance(19_523_000);
        Assert.Equal(19_523_000UL, ecu.Heard[10].Start);
    }

    [Fact] public void OwnEcho_IsNeverTakenForTheReply()
    {
        // The echo of the poll is 12 05 0B B0 AC, itself a whole DS2 frame with the right xor.
        var (s, _, tester) = Rig();
        tester.Apply(Do(KLineVerb.Poll));
        s.Advance(9_737_500);
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal(KLineOutcome.TimedOut, exchange.Outcome);
        Assert.Null(exchange.Reply);

        var (s2, ecu2, tester2) = Rig();
        ecu2.Reply(Ack, delay: 10_000);
        tester2.Apply(Do(KLineVerb.Poll));
        s2.Advance(257_324);
        Assert.Equal(Hex.Bytes(Ack), Assert.Single(tester2.Exchanges).Reply);   // the ECU's frame, not the request's echo
    }

    [Theory]
    [InlineData("12 04 A0 B7", 4)]               // the xor is wrong
    [InlineData("13 04 A0 B7", 4)]               // the xor is right but the address is not the ECU's
    [InlineData("12 02", 2)]                     // a length byte that no frame has: the frame ends there
    [InlineData("12 00", 2)]
    public void AMalformedReply_IsLoggedAsMalformed(string reply, int length)
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply(reply, delay: 10_000);
        tester.Apply(Do(KLineVerb.Poll));
        var arrives = 137_500 + 10_000 + (ulong)length * 27_456;
        s.Advance(arrives - 1);
        Assert.Empty(tester.Exchanges);
        s.Advance(arrives);
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal(KLineOutcome.Malformed, exchange.Outcome);
        Assert.Equal(Hex.Bytes(reply), exchange.Reply);
        Assert.Equal(arrives, exchange.RepliedAt);

        s.Advance(arrives + Gap);                                        // a malformed frame is still the end of the exchange: the poll goes on 1 ms later
        Assert.Equal(arrives + Gap, ecu.Heard[5].Start);
    }

    [Fact] public void ATimeoutsPartialFrame_IsKept()                    // plan 5 review M3
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply("12 4D A0", delay: 10_000);                            // the length byte promises 77 bytes, 3 come
        tester.Apply(Do(KLineVerb.Send, "0B B0"));
        s.Advance(9_737_500);
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal((KLineOutcome.TimedOut, null, null), (exchange.Outcome, exchange.Reply, exchange.RepliedAt));
        Assert.Equal(Hex.Bytes("12 4D A0"), exchange.Partial);
        Assert.Empty(tester.Notes);                                      // a partial frame is the exchange's, not stray
        Assert.Equal(5, ecu.Heard.Count);
    }

    [Fact] public void AByteFromTheEcu_BeforeTheRequestHasGone_IsNotPartOfTheReply()
    {
        var (s, ecu, tester) = Rig();
        ecu.Say(0x55, at: 50_000);                                       // while the poll's bytes are still going out (it ends at 137,500)
        ecu.Reply(Ack, delay: 10_000);
        tester.Apply(Do(KLineVerb.Poll));
        s.Advance(257_324);
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal((KLineOutcome.Replied, 257_324UL), (exchange.Outcome, exchange.RepliedAt));
        Assert.Equal(Hex.Bytes(Ack), exchange.Reply);
        Assert.Equal(new[] { new KLineNote(50_000, "stray", 0x55, "ecu") }, tester.Notes);   // plan 5 review M3
    }

    [Fact] public void BytesWhileNothingIsAsked_AreStray()               // plan 5 review M3
    {
        // A whole frame from the ECU with no request out is not the answer to a request sent afterwards: it is noted.
        var (s, _, tester) = Rig();
        foreach (var b in Hex.Bytes(Ack))
            tester.OnLineByte(b, own: false, 0);
        tester.Apply(Do(KLineVerb.Send, "0B B0"));
        s.Advance(9_737_500);
        Assert.Equal(KLineOutcome.TimedOut, Assert.Single(tester.Exchanges).Outcome);
        Assert.Equal(Hex.Bytes(Ack).Select(b => new KLineNote(0, "stray", b, "ecu")), tester.Notes);
    }

    [Fact] public void BytesAfterAWholeFrame_AreStray()                  // plan 5 review M3
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply("12 04 A0 B6 55", delay: 10_000);                      // the frame ends at 257,324; a stray 55h follows at 284,780
        ecu.Reply("12 05 A0 00 B7", delay: 10_000);                      // 12 ^ 05 ^ A0 ^ 00 = B7
        tester.Apply(Do(KLineVerb.Send, "0B B0"));
        s.Advance(300_000);
        tester.Apply(Do(KLineVerb.Send, "0B B0"));                       // leaves 20 ms after the reply, at 737,324, the stray byte long gone
        s.Advance(737_324 + 137_500 + 10_000 + 5 * 27_456);              // the second answer ends at 1,022,104
        Assert.Equal(2, tester.Exchanges.Count);
        Assert.Equal(Hex.Bytes(Ack), tester.Exchanges[0].Reply);
        Assert.Equal(Hex.Bytes("12 05 A0 00 B7"), tester.Exchanges[1].Reply);   // not 55 12 05 A0 00
        Assert.Equal(new[] { new KLineNote(284_780, "stray", 0x55, "ecu") }, tester.Notes);
    }

    [Fact] public void AByteAtTheDeadline_IsStray()                       // plan 5 review M3
    {
        var (s, ecu, tester) = Rig();
        tester.Apply(Do(KLineVerb.Send, "0B B0"));                       // out by 137,500: the deadline is 9,737,500
        ecu.Say(0x12, at: 9_737_500);
        s.Advance(9_737_501);
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal(KLineOutcome.TimedOut, exchange.Outcome);
        Assert.Empty(exchange.Partial);
        Assert.Equal(new[] { new KLineNote(9_737_500, "stray", 0x12, "ecu") }, tester.Notes);
    }

    [Fact] public void AReplyThatEndsOneCycleBeforeTheDeadline_IsTaken()
    {
        var (s, ecu, tester) = Rig();
        ecu.Reply(Ack, delay: 9_490_175);                                // the last byte arrives at 137,500 + 9,490,175 + 109,824 = 9,737,499
        tester.Apply(Do(KLineVerb.Poll));
        s.Advance(9_737_500);
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal((KLineOutcome.Replied, 9_737_499UL), (exchange.Outcome, exchange.RepliedAt));
    }

    [Theory]
    [InlineData(false)]                          // the reply is scheduled after the timeout: the timeout runs first
    [InlineData(true)]                           // the reply is scheduled before the request is out: its byte runs first
    public void AReplyThatEndsInTheDeadlineCycle_IsLate_WhicheverEventRunsFirst(bool byteScheduledFirst)
    {
        var (s, ecu, tester) = Rig();
        if (byteScheduledFirst)
        {
            for (var i = 0; i < 4; i++)
                ecu.Say(Hex.Bytes(Ack)[i], at: 9_737_500 - (ulong)(3 - i) * 27_456);
        }
        else
        {
            ecu.Reply(Ack, delay: 9_490_176);                            // the last byte arrives at 9,737,500, the deadline
        }
        tester.Apply(Do(KLineVerb.Poll));
        s.Advance(9_737_500);
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal((KLineOutcome.TimedOut, null), (exchange.Outcome, exchange.Reply));
    }

    [Theory]
    [InlineData(KLineVerb.Send, "91 01 E8 48 00", 12_220_000UL)]         // a 0x91 command listens 500 ms
    [InlineData(KLineVerb.Send, "92 01 E8 48 00", 9_820_000UL)]          // anything else 400 ms
    [InlineData(KLineVerb.Raw, "12 08 91 01 E8 48 00 2A", 12_220_000UL)] // as raw bytes, the same frame
    [InlineData(KLineVerb.Raw, "AA BB", 9_655_000UL)]                    // 2 bytes end at 55,000
    [InlineData(KLineVerb.Raw, "AA", 9_627_500UL)]                       // one byte ends at 27,500
    public void TheListenTime_Is500msFor0x91_AndOtherwise400ms(KLineVerb verb, string hex, ulong timesOutAt)
    {
        var (s, _, tester) = Rig();
        tester.Apply(Do(verb, hex));
        s.Advance(timesOutAt - 1);
        Assert.Empty(tester.Exchanges);
        s.Advance(timesOutAt);
        Assert.Equal(KLineOutcome.TimedOut, Assert.Single(tester.Exchanges).Outcome);
    }

    // ---- send, raw, every and the queue ----

    [Fact] public void Send_PutsThePayloadInADs2Frame()
    {
        var (s, ecu, tester) = Rig();
        tester.Apply(Do(KLineVerb.Send, "6D 00"));
        s.Advance(137_500);
        Assert.Equal(Hex.Bytes("12 05 6D 00 7A"), ecu.Heard.Select(w => w.Value));
        Assert.Equal(new[] { 0UL, 27_500UL, 55_000UL, 82_500UL, 110_000UL }, ecu.Heard.Select(w => w.Start));
    }

    [Fact] public void Raw_SendsTheExactBytes_Unframed_AtTheTestersBaud()
    {
        var (s, ecu, tester) = Rig();
        tester.Apply(Do(KLineVerb.Raw, "AA BB CC"));                     // not a DS2 frame: no address, no length, no xor
        s.Advance(9_682_500);                                            // 3 bytes end at 82,500; 400 ms later it times out
        Assert.Equal(Hex.Bytes("AA BB CC"), ecu.Heard.Select(w => w.Value));
        Assert.All(ecu.Heard, w => Assert.Equal(2_500.0, w.BitCycles));
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal(Hex.Bytes("AA BB CC"), exchange.Request);
        Assert.Equal(KLineOutcome.TimedOut, exchange.Outcome);
    }

    [Fact] public void Raw_WithNoBytes_IsRefused()
    {
        var (_, _, tester) = Rig();
        Assert.Throws<ArgumentException>(() => tester.Apply(Do(KLineVerb.Raw)));
    }

    [Fact] public void QueuedSends_GoBeforeThePoll_AndEveryRepeats()
    {
        // An exchange and its pause take 756,000 cycles here: 137,500 for the request, 38,500 and 4 bytes of
        // 25,000 for the answer, 480,000 for the pause. So requests leave at multiples of 756,000.
        var (s, ecu, tester) = Rig();
        ecu.ByteCycles = 25_000;
        ecu.Always(Ack, delay: 38_500);
        tester.Apply(Do(KLineVerb.Poll));                                // the poll was told first
        tester.Apply(Do(KLineVerb.Send, "6D 00"));
        tester.Apply(Do(KLineVerb.Send, "6D 01"));
        tester.Apply(Every(0.126, "6D 02"));                             // 3,024,000 cycles: every fourth request
        s.Advance(6_048_000 + 137_500);
        Assert.Equal(new[] { "6D00", "6D01", "6D02", "0BB0", "6D02", "0BB0", "0BB0", "0BB0", "6D02" }, Commands(ecu));
        Assert.Equal(Enumerable.Range(0, 9).Select(i => (ulong)i * 756_000), ecu.Requests.Select(r => r.Start));
    }

    [Fact] public void Every_KeepsItsRhythm_ALateSendDoesNotDelayTheNextOne()
    {
        // The same 756,000-cycle exchanges, an every of 0.1008 s = 2,419,200 cycles: due at 0, 2,419,200, 4,838,400, 7,257,600.
        // It leaves at the first request after it falls due: 0, 3,024,000, 5,292,000, 7,560,000. A period counted
        // from each send would give 6,048,000 for the third.
        var (s, ecu, tester) = Rig();
        ecu.ByteCycles = 25_000;
        ecu.Always(Ack, delay: 38_500);
        tester.Apply(Do(KLineVerb.Poll));
        tester.Apply(Every(0.1008, "6D 02"));
        s.Advance(7_560_000 + 137_500);
        var at = ecu.Requests.Where(r => r.Frame[2] == 0x6D).Select(r => r.Start);
        Assert.Equal(new[] { 0UL, 3_024_000UL, 5_292_000UL, 7_560_000UL }, at);
    }

    [Fact] public void Every_AloneWithNoPoll_LeavesWhenItFallsDue()
    {
        var (s, ecu, tester) = Rig();
        ecu.Always(Ack);
        tester.Apply(Every(0.5, "6D 02"));                               // 12,000,000 cycles
        s.Advance(12_000_000 + 137_500);
        Assert.Equal(new[] { 0UL, 12_000_000UL }, ecu.Requests.Select(r => r.Start));
    }

    [Fact] public void AnEveryWaitingForItsMoment_StillWaitsThePauseAfterARequestThatEndedJustBefore()
    {
        var (s, ecu, tester) = Rig();
        ecu.Always(Ack, delay: 10_000);
        tester.Apply(Every(0.5, "6D 02"));                               // due at 0 and then at 12,000,000
        s.Advance(11_732_676);
        tester.Apply(Do(KLineVerb.Send, "6D 00"));                       // takes 257,324: answered at 11,990,000, 10,000 before the every falls due
        s.Advance(12_470_000 + 137_500);
        Assert.Equal(new[] { 0UL, 11_732_676UL, 12_470_000UL }, ecu.Requests.Select(r => r.Start));   // the every waits for the pause: 11,990,000 + 480,000
    }

    [Fact] public void AnEveryPeriodUnderOneCycle_IsRaisedToOneCycle_AndNeverStopsTime()
    {
        var (s, ecu, tester) = Rig();
        ecu.ByteCycles = 25_000;                                         // 756,000-cycle exchanges, as in QueuedSends_GoBeforeThePoll_AndEveryRepeats
        ecu.Always(Ack, delay: 38_500);
        tester.Apply(Do(KLineVerb.Poll));
        tester.Apply(Every(1e-9, "6D 02"));                              // 0.024 of a cycle
        s.Advance(2_268_000 + 137_500);                                  // always due, so it takes every slot and the poll none
        Assert.Equal(new[] { "6D02", "6D02", "6D02", "6D02" }, Commands(ecu));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(60.5)]
    public void AnEveryPeriodThatIsNotInZeroToSixtySeconds_IsRefused(double seconds)
    {
        var (s, _, tester) = Rig();
        Assert.Throws<ArgumentOutOfRangeException>(() => tester.Apply(Every(seconds, "6D 02")));
        s.Advance(1_000_000);
        Assert.Empty(tester.Exchanges);                                  // a refused action changes nothing
        Assert.Equal(ulong.MaxValue, s.NextDue);
    }

    [Fact] public void OneRequestAtATime_ASendTold_WhileAnotherIsInFlight_WaitsForItsExchange()
    {
        var (s, ecu, tester) = Rig();
        tester.Apply(Do(KLineVerb.Poll));                                // nobody answers: the poll is in flight for 400 ms
        s.Advance(1_000_000);
        tester.Apply(Do(KLineVerb.Send, "6D 00"));
        s.Advance(9_761_500 + 137_500);                                  // the poll times out at 9,737,500; 1 ms later the send goes, before the next poll
        Assert.Equal(new[] { "0BB0", "6D00" }, Commands(ecu));
        Assert.Equal(9_761_500UL, ecu.Requests[1].Start);
    }

    [Fact] public void ARequestTold_WhileIdle_LeavesAtOnce_ButNotSoonerThan20MillisecondsAfterTheLastReply()
    {
        var (s, ecu, tester) = Rig();
        ecu.Always(Ack, delay: 10_000);
        tester.Apply(Do(KLineVerb.Send, "6D 00"));
        s.Advance(257_324 + 10_000);                                     // answered at 257,324; now 10,000 cycles later
        tester.Apply(Do(KLineVerb.Send, "6D 01"));                       // told too soon: it waits for the pause after the reply
        s.Advance(257_324 + Pause + 137_500);
        Assert.Equal(257_324 + Pause, ecu.Requests[1].Start);
        s.Advance(2_000_000);                                            // and a long idle spell later
        tester.Apply(Do(KLineVerb.Send, "6D 02"));                       // told in good time: it leaves at once
        s.Advance(2_000_000 + 137_500);
        Assert.Equal(2_000_000UL, ecu.Requests[2].Start);
    }

    // ---- quiet ----

    [Fact] public void Quiet_StopsAllTraffic()
    {
        var (s, ecu, tester) = Rig();
        ecu.ByteCycles = 25_000;
        ecu.Always(Ack, delay: 38_500);
        tester.Apply(Do(KLineVerb.Poll));
        tester.Apply(Every(0.05, "6D 02"));
        s.Advance(2_500_000);                                            // requests left at 0, 756,000, 1,512,000 and 2,268,000; the fourth is in flight
        tester.Apply(Do(KLineVerb.Quiet));
        s.Advance(2_600_000);                                            // the fourth is answered at 2,544,000
        Assert.Equal((20, 4), (ecu.Heard.Count, tester.Exchanges.Count));
        s.Advance(200_000_000);
        Assert.Equal((20, 4), (ecu.Heard.Count, tester.Exchanges.Count));
        Assert.Equal(ulong.MaxValue, s.NextDue);                         // and nothing is left scheduled
    }

    [Fact] public void Quiet_LetsTheRequestInFlightFinish_ButDropsTheQueue()
    {
        var (s, ecu, tester) = Rig();
        ecu.Always(Ack, delay: 10_000);
        tester.Apply(Do(KLineVerb.Poll));
        tester.Apply(Do(KLineVerb.Send, "6D 00"));
        s.Advance(1_000);                                                // the send is in flight
        tester.Apply(Do(KLineVerb.Send, "6D 01"));
        tester.Apply(Do(KLineVerb.Quiet));
        s.Advance(100_000_000);
        Assert.Equal(new[] { "6D00" }, Commands(ecu));
        Assert.Equal(KLineOutcome.Replied, Assert.Single(tester.Exchanges).Outcome);
    }

    [Fact] public void Quiet_ToldTogetherWithASend_DropsTheSendBeforeItLeaves()
    {
        var (s, ecu, tester) = Rig();
        tester.Apply(Do(KLineVerb.Send, "6D 00"));
        tester.Apply(Do(KLineVerb.Quiet));
        s.Advance(100_000_000);
        Assert.Empty(ecu.Heard);
    }

    [Fact] public void PollAgain_AfterQuiet_StartsTheSessionAgain()
    {
        var (s, ecu, tester) = Rig();
        ecu.Always(Ack, delay: 10_000);
        tester.Apply(Do(KLineVerb.Poll));
        s.Advance(300_000);
        tester.Apply(Do(KLineVerb.Quiet));
        s.Advance(10_000_000);
        var before = ecu.Requests.Count;
        tester.Apply(Do(KLineVerb.Poll));
        s.Advance(11_000_000);
        Assert.True(ecu.Requests.Count > before);
    }

    // ---- plugging ----

    [Fact] public void Unplugged_SendsNothing_AndLogsNothing()
    {
        var s = new Scheduler();
        var tester = new KLineTester(s);
        tester.Apply(Do(KLineVerb.Connect));
        tester.Apply(Do(KLineVerb.Poll));
        tester.Apply(Do(KLineVerb.Send, "6D 00"));
        tester.Apply(Every(1, "6D 02"));
        Assert.Equal(ulong.MaxValue, s.NextDue);                         // it did not even ask the scheduler for a moment
        s.Advance(100_000_000);
        Assert.Empty(tester.Exchanges);
        Assert.Empty(tester.Notes);
        Assert.Equal(9_600, tester.Baud);
    }

    [Fact] public void ATesterThatIsPluggedAndToldNothing_SchedulesNothing()
    {
        var (s, ecu, _) = Rig();
        Assert.Equal(ulong.MaxValue, s.NextDue);                         // plugging asks the scheduler for nothing
        s.Advance(100_000_000);
        Assert.Empty(ecu.Heard);
    }

    [Fact] public void Unplugged_ThenPlugged_ActsOnWhatItWasToldMeanwhile()
    {
        var s = new Scheduler();
        var tester = new KLineTester(s);
        tester.Apply(Do(KLineVerb.Send, "6D 00"));
        var ecu = new EcuEnd(s);
        s.Advance(5_000);
        tester.Plug(ecu);
        s.Advance(5_000 + 137_500);
        Assert.Equal(5_000UL, Assert.Single(ecu.Requests).Start);
    }

    [Fact] public void Notes_AreCollectedFromThePort()
    {
        var (_, ecu, tester) = Rig();
        ecu.Note(new KLineNote(5, "mismatch", 0x12, "tester"));
        ecu.Note(new KLineNote(9, "collision", 0x34, "ecu"));
        Assert.Equal(new[] { new KLineNote(5, "mismatch", 0x12, "tester"), new KLineNote(9, "collision", 0x34, "ecu") }, tester.Notes);
    }

    [Fact] public void PluggedAgainAfterAPowerCycle_TheSessionGoesOnAt125000_OnTheNewMachinesClock()
    {
        var s1 = new Scheduler();
        var first = new EcuEnd(s1);
        var tester = new KLineTester(s1);
        tester.Plug(first);
        first.Reply(Ack, delay: 10_000);
        tester.Apply(Do(KLineVerb.Connect));
        tester.Apply(Do(KLineVerb.Poll));
        s1.Advance(900_000);                                             // connected at 339,824; the poll left 20 ms later, at 819,824, at 125,000 baud, and nobody answers it
        Assert.Equal((125_000, 1), (tester.Baud, tester.Exchanges.Count));

        var s2 = new Scheduler();                                        // a power cycle: a new machine with a clock of its own; it starts at drive cycle 50,000,000
        var second = new EcuEnd(s2);
        tester.Plug(second, s2, origin: 50_000_000);
        Assert.Equal(new[] { KLineOutcome.Replied, KLineOutcome.TimedOut }, tester.Exchanges.Select(x => x.Outcome));   // the poll in flight went with the old machine
        Assert.Equal((819_824UL, 125_000), (tester.Exchanges[1].SentAt, tester.Exchanges[1].Baud));
        Assert.Equal(900_000UL, tester.Exchanges[1].EndedAt);           // it ended when its machine went away
        Assert.Equal(tester.Exchanges[0].RepliedAt, tester.Exchanges[0].EndedAt);

        s2.Advance(20_000);
        Assert.Equal(5, second.Heard.Count);                             // it polls again at once, still at 125,000 baud: 5 bytes of 2,112
        Assert.Equal(0UL, second.Heard[0].Start);
        Assert.All(second.Heard, w => Assert.Equal(192.0, w.BitCycles));
        s2.Advance(10_560 + 9_600_000);                                  // the deaf ECU: 400 ms after the request ended
        var exchange = tester.Exchanges[2];
        Assert.Equal((50_000_000UL, KLineOutcome.TimedOut, 125_000), (exchange.SentAt, exchange.Outcome, exchange.Baud));
        Assert.Equal(50_000_000UL + 10_560 + 9_600_000, exchange.EndedAt);   // when the tester gave up, in the drive's cycles
        s2.Advance(10_560 + 9_600_000 + Gap);
        Assert.Equal(9_634_560UL, second.Heard[5].Start);

        second.Note(new KLineNote(123, "mismatch", 0x12, "tester"));
        first.Note(new KLineNote(7, "mismatch", 0x12, "tester"));        // the old machine's line is no longer listened to
        Assert.Equal(new[] { new KLineNote(50_000_123, "mismatch", 0x12, "tester") }, tester.Notes);
    }

    [Fact] public void AfterAPlug_TheExchangesAreLoggedInTheDrivesCycles()
    {
        var s1 = new Scheduler();
        var tester = new KLineTester(s1);
        tester.Plug(new EcuEnd(s1));
        var s2 = new Scheduler();
        var second = new EcuEnd(s2);
        second.Always(Ack, delay: 10_000);
        tester.Plug(second, s2, origin: 50_000_000);                     // the new machine's cycle 0 is the drive's 50,000,000
        tester.Apply(Do(KLineVerb.Send, "0B B0"));
        s2.Advance(257_324);                                             // sent at 0 on the new clock, answered at 257,324
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal((50_000_000UL, 50_257_324UL, KLineOutcome.Replied), (exchange.SentAt, exchange.RepliedAt, exchange.Outcome));
    }

    [Fact] public void PluggedAgain_AnEveryIsDueAtOnce_OnTheNewClock()
    {
        var s1 = new Scheduler();
        var first = new EcuEnd(s1);
        var tester = new KLineTester(s1);
        tester.Plug(first);
        first.Always(Ack);
        tester.Apply(Every(10, "6D 02"));                                // 240,000,000 cycles
        s1.Advance(1_000_000);
        Assert.Single(first.Requests);

        var s2 = new Scheduler();
        var second = new EcuEnd(s2);
        second.Always(Ack);
        tester.Plug(second, s2);
        s2.Advance(1_000_000);
        Assert.Equal(0UL, Assert.Single(second.Requests).Start);
    }

    // ---- the real bus ----

    [Fact] public void PluggedIntoTheRealBus_TheRequestGoesOutBackToBack_AndTheEchoPacesIt()
    {
        var t = TestMachine.Create("0DFF").WriteSfr(0xFEB4, 0x004D).WriteSfr(0xFFB0, 0x80F7);   // ASC0 as the firmware sets it for DS2
        t.M.Watchdog.Disable();                                          // nothing here services it, and at 131,072 cycles it would reset ASC0
        var tester = new KLineTester(t.M.Scheduler);
        tester.Plug(t.M.KLine);                                          // plugging attaches the tester to the bus
        tester.Apply(Do(KLineVerb.Poll));
        t.M.Scheduler.Advance(137_500);                                  // 5 bytes of 27,500
        Assert.Empty(tester.Notes);                                      // no byte lost, none collided
        Assert.Equal(0x0AC, t.Sfr(S0Rbuf));                              // the last byte, ACh, reached ASC0 (four 1s: parity 0)
        Assert.Empty(tester.Exchanges);
        t.M.Scheduler.Advance(137_500 + 9_600_000);                      // nothing answers: 400 ms after the request ended
        var exchange = Assert.Single(tester.Exchanges);
        Assert.Equal((0UL, KLineOutcome.TimedOut, null), (exchange.SentAt, exchange.Outcome, exchange.Reply));
    }

    // ---- the run's assumption list ----

    [Fact] public void TheDriveAssumptions_NameTheTestersGapAndListenTimes() =>
        Assert.Contains(MachineAssumptions.ForDrive(),
            line => line.Contains("K-line tester") && line.Contains("20 ms") && line.Contains("1 ms") && line.Contains("500 ms") && line.Contains("400 ms"));
}
