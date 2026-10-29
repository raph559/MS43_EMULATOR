namespace Ms43Sim.Core;

/// <summary>How a request ended.</summary>
public enum KLineOutcome
{
    /// <summary>A whole frame came from the ECU in time: address 12h, and the right length and xor.</summary>
    Replied,

    /// <summary>No whole frame came in time, or the line was unplugged (a power cycle) with the request in flight.</summary>
    TimedOut,

    /// <summary>A whole frame came in time, but its address, its length or its xor is wrong.</summary>
    Malformed,
}

/// <summary>
/// One request and what came of it. <see cref="SentAt"/> is the cycle its first byte went on the line, at
/// <see cref="Baud"/>. <see cref="RepliedAt"/> is the cycle the last byte of the ECU's frame arrived and
/// <see cref="Reply"/> the frame's bytes: both null when it <see cref="KLineOutcome.TimedOut"/>, and then
/// <see cref="Partial"/> holds whatever part of a frame came. <see cref="EndedAt"/> is the cycle it ended: the reply's, or
/// the end of the listen time, or the unplugging (a power cycle) for a request that went with its machine.
/// </summary>
public sealed record KLineExchange(ulong SentAt, IReadOnlyList<byte> Request, int Baud, ulong? RepliedAt,
    IReadOnlyList<byte>? Reply, KLineOutcome Outcome, ulong EndedAt)
{
    /// <summary>The bytes of a frame that never became whole (plan 6, M3): empty unless the request timed out.</summary>
    public IReadOnlyList<byte> Partial { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// The scripted K-line tester: the MS43 logger app's DS2 session on the line, frame for frame (plan 5 C4). It only puts
/// bytes on the line and reads what comes back; it never touches the ECU's state. All its timing is scheduler events.
/// <list type="bullet">
/// <item>One request at a time. Its bytes go back to back at the tester's <see cref="Baud"/>: each leaves when the
/// echo of the one before comes back (<c>own: true</c>), whatever became of it on the line.</item>
/// <item>The next request leaves 20 ms after a reply [obs: the app's LiveLogger pauses 20 ms after each sample; after a
/// connect the app polls at once, and the 20 ms stand for its serial cable's latency there, assumption], or the wait a
/// <c>kline pause MS</c> line set (owner, 2026-10-28: the app without its pause), 1 ms after a
/// timeout or a malformed answer [assumption: the app sends again at once], or at once if the tester has been idle longer.</item>
/// <item>The queue: the one-shot requests told so far (connect, disconnect, send, raw) in the order told, then an
/// <c>every</c> that is due, in the order told, then the poll while polling.</item>
/// <item>The reply is the first whole frame from the ECU (<c>own: false</c>) after the request's last byte has come back.
/// A frame is whole when its length byte, the second, says so; it is malformed if its address is not 12h, its length byte is
/// under 3, or its length or xor is wrong. The tester's own echo is never taken for a reply. An ECU byte that is no part
/// of a reply (nothing asked, the request still going out, the listen time over, the reply already whole) is noted
/// "stray" (plan 6, M3), and a timed-out request keeps the bytes of its unfinished frame.</item>
/// <item>The listen time runs from the end of the request: 400 ms, or 500 ms for a request whose command byte is 0x91 [obs: the
/// ADX's listen timeouts]. A reply that ends in the deadline's cycle is late, whichever event of that cycle runs first.</item>
/// <item><c>connect</c> sends <c>91 01 E8 48 00</c> at 9,600 baud, and on a reply with status A0 the tester moves to 125,000.
/// <c>disconnect</c> sends <c>91 00 25 80 00</c> at the current baud, and on A0 it moves to 9,600. A <c>send</c> of the same
/// payload is just a request: only the two verbs move the baud, as only the app's session does.</item>
/// <item>An <c>every</c> is due as it is told [call], then at each multiple of its period from that moment, and leaves
/// at the first free turn: a late send does not delay the next one, and a period shorter than a turn takes every turn.</item>
/// <item>Unplugged, the tester sends nothing and logs nothing; what it is told meanwhile it acts on once plugged.</item>
/// </list>
/// </summary>
public sealed class KLineTester : IKLineDevice
{
    const double CpuHz = 24_000_000;
    const int LowBaud = 9_600, HighBaud = 125_000;
    const ulong GapCycles = 24_000;                              // 1 ms: after a timeout or a malformed answer
    const ulong PauseCycles = 480_000;                           // 20 ms: after a reply, the app's pause after each sample
    ulong pauseCycles = PauseCycles;                             // what a kline pause line set
    const ulong NormalListenCycles = 9_600_000;                  // 400 ms
    const ulong SpeedSwitchListenCycles = 12_000_000;            // 500 ms
    const double LongestPeriodSeconds = 60;
    const byte SpeedSwitch = 0x91, Acknowledged = 0xA0;
    const string Stray = "stray", FromEcu = "ecu";

    static readonly Request ConnectRequest = new(Ds2.Frame(new byte[] { 0x91, 0x01, 0xE8, 0x48, 0x00 }), RequestKind.Connect, LowBaud);
    static readonly Request DisconnectRequest = new(Ds2.Frame(new byte[] { 0x91, 0x00, 0x25, 0x80, 0x00 }), RequestKind.Disconnect);
    static readonly Request PollRequest = new(Ds2.Frame(new byte[] { 0x0B, 0xB0 }));

    readonly List<KLineExchange> exchanges = new();
    readonly List<KLineNote> notes = new();
    readonly Queue<Request> told = new();                        // connect, disconnect, send and raw, in the order told
    readonly List<Every> everys = new();
    Scheduler scheduler;
    IKLinePort? port;
    ulong origin;                                                // the drive's cycle at which the scheduler reads 0
    bool polling;
    Flight? flight;
    ulong notBefore;                                             // no request leaves before this cycle: the gap or the pause after the last exchange
    long startToken;                                             // a start event of an older token has been superseded
    ulong? startAt;                                              // the cycle the live start event is due

    /// <param name="scheduler">The clock the tester's events run on, until <see cref="Plug"/> brings another.</param>
    public KLineTester(Scheduler scheduler) => this.scheduler = scheduler;

    /// <summary>The rate the tester's next request leaves at, in baud: 9,600 until a connect is answered with A0.</summary>
    public int Baud { get; private set; } = LowBaud;

    /// <summary>The bit time it listens at: <see cref="Baud"/>'s (plan 6, M4).</summary>
    public double BitCycles => CpuHz / Baud;

    /// <summary>Every request that has ended, in order: the cycles are the drive's (see <c>origin</c> of <see cref="Plug"/>).</summary>
    public IReadOnlyList<KLineExchange> Exchanges => exchanges;

    /// <summary>The bytes the line has lost, from every port the tester was plugged into, in the drive's cycles.</summary>
    public IReadOnlyList<KLineNote> Notes => notes;

    /// <summary>
    /// Plugs the tester into a line: it becomes the line's device, and collects its notes. It is called again at every
    /// power cycle, with the new machine's line, <paramref name="scheduler"/> and <paramref name="origin"/>: the session
    /// goes on as it was (the baud, polling, the queue and the everys), on the new clock. A request in flight went with
    /// the old machine and ends as <see cref="KLineOutcome.TimedOut"/>; an every is due at once; the first request leaves at once.
    /// </summary>
    /// <param name="port">The new line.</param>
    /// <param name="scheduler">The new line's clock, or null to go on with the tester's current one (a test's single clock).</param>
    /// <param name="origin">The drive's cycle at which that clock reads 0: the cycles the tester logs are the clock's plus this.</param>
    public void Plug(IKLinePort port, Scheduler? scheduler = null, ulong origin = 0)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (this.port is not null)
            this.port.Noted -= OnNote;
        if (flight is { } lost)
        {
            flight = null;
            Log(lost, KLineOutcome.TimedOut, null, null);
        }
        this.scheduler = scheduler ?? this.scheduler;
        this.origin = origin;
        this.port = port;
        startToken++;                                            // a start of the old line's clock means nothing here
        startAt = null;
        notBefore = this.scheduler.Now;
        foreach (var every in everys)
            every.NextDue = this.scheduler.Now;
        port.Noted += OnNote;
        port.Attach(this);
        PlanNext();
    }

    /// <summary>
    /// Takes one scripted action. Nothing leaves in this call: the request goes at the next turn, in the same cycle if the
    /// tester is free, so the actions told together (a segment's lines) are all in the queue before the first leaves, and
    /// a <c>quiet</c> among them drops the ones before it. A refused action (an every period outside 0 to 60 seconds,
    /// a raw with no bytes, a payload over 252 bytes) throws and changes nothing.
    /// </summary>
    public void Apply(KLineAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        switch (action.Verb)
        {
            case KLineVerb.Connect:
                told.Enqueue(ConnectRequest);
                break;
            case KLineVerb.Disconnect:
                told.Enqueue(DisconnectRequest);
                break;
            case KLineVerb.Poll:
                polling = true;
                break;
            case KLineVerb.Quiet:
                Quiet();
                return;
            case KLineVerb.Send:
                told.Enqueue(new Request(Ds2.Frame(action.Bytes)));
                break;
            case KLineVerb.Raw:
                if (action.Bytes.Count == 0)
                    throw new ArgumentException("a raw request needs at least one byte", nameof(action));
                told.Enqueue(new Request(action.Bytes.ToArray()));
                break;
            case KLineVerb.Every:
                everys.Add(new Every(new Request(Ds2.Frame(action.Bytes)), PeriodCycles(action.Period), scheduler.Now));
                break;
            case KLineVerb.Pause:
                pauseCycles = (ulong)Math.Round(action.Period * CpuHz);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action.Verb, "not a K-line verb");
        }
        PlanNext();
    }

    /// <summary>A byte reached the tester: its own echo, which paces the request, or one the ECU sent, which may be the reply.</summary>
    public void OnLineByte(byte value, bool own, ulong cycle)
    {
        if (own)
        {
            if (flight is { } f)
                Echoed(f);
        }
        else if (flight is not { } f || !Heard(f, value, cycle))
            notes.Add(new KLineNote(origin + cycle, Stray, value, FromEcu));   // nothing asked, or no part of the reply
    }

    void Quiet()
    {
        polling = false;
        told.Clear();
        everys.Clear();
        startToken++;                                            // a start waiting for its moment has nothing left to start
        startAt = null;
    }

    /// <summary>Makes sure a start event is waiting for the earliest moment the next request can leave.</summary>
    void PlanNext()
    {
        if (port is null || flight is not null || NextRequestAt() is not { } at)
            return;
        if (startAt <= at)
            return;                                              // a start no later than this is already waiting
        var token = ++startToken;
        startAt = at;
        scheduler.Schedule(at, "K-line tester request", () => Start(token));
    }

    ulong? NextRequestAt()
    {
        var free = Math.Max(scheduler.Now, notBefore);
        if (told.Count > 0 || polling)
            return free;
        ulong? due = null;
        foreach (var every in everys)
            due = due is { } soonest ? Math.Min(soonest, every.NextDue) : every.NextDue;
        return due is { } at ? Math.Max(free, at) : null;
    }

    void Start(long token)
    {
        if (token != startToken)
            return;
        startAt = null;
        if (port is null || flight is not null)
            return;
        if (Next() is { } request)
            Begin(request);
    }

    Request? Next()
    {
        if (told.Count > 0)
            return told.Dequeue();
        foreach (var every in everys)
        {
            if (every.NextDue <= scheduler.Now)
            {
                every.Sent(scheduler.Now);
                return every.Request;
            }
        }
        return polling ? PollRequest : null;
    }

    void Begin(Request request)
    {
        if (request.Baud is { } baud)
            Baud = baud;
        var sending = flight = new Flight(request, scheduler.Now, Baud);
        Put(sending);
    }

    void Put(Flight sending) => port!.Send(sending.Request.Bytes[sending.Sent++], CpuHz / sending.Baud);

    /// <summary>The echo of the byte sent: the next one goes, or, after the last, the listening starts.</summary>
    void Echoed(Flight f)
    {
        if (f.Sent < f.Request.Bytes.Length)
        {
            Put(f);
            return;
        }
        var deadline = scheduler.Now + f.Request.Listen;
        f.Deadline = deadline;
        scheduler.Schedule(deadline, "K-line tester listen time", () => TimeOut(f));
    }

    /// <summary>An ECU byte while <paramref name="f"/> is out: false when it is no part of the reply.</summary>
    bool Heard(Flight f, byte value, ulong cycle)
    {
        if (f.Deadline is not { } deadline || cycle >= deadline)
            return false;                                        // the request is not out yet, or the time is up
        f.Reply.Add(value);
        if (f.Reply.Count < 2 || f.Reply.Count < f.Reply[1])
            return true;                                         // the length byte says when the frame is whole
        var frame = f.Reply.ToArray();
        Complete(f, Ds2.IsFrame(frame) && frame[0] == Ds2.EcuAddress ? KLineOutcome.Replied : KLineOutcome.Malformed, cycle, frame);
        return true;
    }

    void TimeOut(Flight f)
    {
        if (flight == f)
            Complete(f, KLineOutcome.TimedOut, null, null);
    }

    void Complete(Flight f, KLineOutcome outcome, ulong? at, byte[]? reply)
    {
        flight = null;
        Log(f, outcome, at, reply);
        if (outcome == KLineOutcome.Replied && f.Request.Kind != RequestKind.Plain && reply is { Length: > 3 } && reply[2] == Acknowledged)
            Baud = f.Request.Kind == RequestKind.Connect ? HighBaud : LowBaud;
        notBefore = scheduler.Now + (outcome == KLineOutcome.Replied ? pauseCycles : GapCycles);
        PlanNext();
    }

    void Log(Flight f, KLineOutcome outcome, ulong? at, byte[]? reply) =>
        exchanges.Add(new KLineExchange(origin + f.SentAt, f.Request.Bytes, f.Baud, at + origin, reply, outcome, origin + scheduler.Now)
        {
            Partial = outcome == KLineOutcome.TimedOut ? f.Reply.ToArray() : Array.Empty<byte>(),
        });

    void OnNote(KLineNote note) => notes.Add(note with { Cycle = note.Cycle + origin });

    /// <summary>
    /// A period in CPU cycles, of at least one: a period under a cycle (the drive grammar allows any above 0) would otherwise
    /// make an every due again in the cycle it was sent.
    /// </summary>
    static ulong PeriodCycles(double seconds)
    {
        if (!(seconds > 0 && seconds <= LongestPeriodSeconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "an every period is above 0 and at most 60 seconds");
        return Math.Max(1UL, (ulong)Math.Round(seconds * CpuHz, MidpointRounding.AwayFromZero));
    }

    enum RequestKind { Plain, Connect, Disconnect }

    /// <summary>The bytes of a request, what it is for, and the rate it must leave at (null: the tester's current one).</summary>
    sealed class Request(byte[] bytes, RequestKind kind = RequestKind.Plain, int? baud = null)
    {
        public byte[] Bytes { get; } = bytes;
        public RequestKind Kind { get; } = kind;
        public int? Baud { get; } = baud;

        /// <summary>How long the app listens for the answer, from the end of the request: longer for a 0x91 command.</summary>
        public ulong Listen { get; } = bytes.Length > 2 && bytes[2] == SpeedSwitch ? SpeedSwitchListenCycles : NormalListenCycles;
    }

    /// <summary>A request on the line: how many of its bytes have gone out, and, once the last has come back, the listening.</summary>
    sealed class Flight(Request request, ulong sentAt, int baud)
    {
        public Request Request { get; } = request;
        public ulong SentAt { get; } = sentAt;
        public int Baud { get; } = baud;
        public int Sent { get; set; }
        public ulong? Deadline { get; set; }
        public List<byte> Reply { get; } = new();
    }

    /// <summary>A periodic request: due at <see cref="NextDue"/>, and then at each multiple of its period from the first.</summary>
    sealed class Every(Request request, ulong period, ulong nextDue)
    {
        public Request Request { get; } = request;
        public ulong NextDue { get; set; } = nextDue;

        /// <summary>It left at <paramref name="now"/>: the next due moment is the first of the rhythm after it, however late it left.</summary>
        public void Sent(ulong now) => NextDue += (now - NextDue) / period * period + period;
    }
}
