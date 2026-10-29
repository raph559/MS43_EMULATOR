using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ms43Sim.Core;

/// <summary>
/// Reads a drive file (plan 4 R17): UTF-8 text where <c>#</c> starts a comment and blank lines are
/// ignored; <c>drive NAME</c> first, then optionally <c>start blank|seasoned</c> (blank when absent) and
/// <c>set KEY=VALUE …</c>, then the segments, <c>DURATION MODE KEY=VALUE …</c>. A value is a number,
/// <c>A..B</c> (a linear ramp across the segment), <c>N</c> for the gear, <c>CHANNEL:VOLTS</c> for
/// <c>force</c> and a partner's identifier for <c>silence</c>. The first mistake is reported with its line.
/// Plan 5 adds the <c>stall</c> mode and the <c>kline</c> lines, which script the K-line tester: each
/// applies at the start of the segment that follows it.
/// </summary>
public static partial class DriveParser
{
    /// <summary>R17's keys: their range, whether a ramp is allowed, and whether a later segment keeps them.</summary>
    sealed record KeyRule(double Min, double Max, bool Ramps, bool Kept, bool Whole = false);

    static readonly Dictionary<string, KeyRule> Rules = new()
    {
        ["coolant"] = new(-30, 140, true, true),
        ["oil"] = new(-30, 140, true, true),
        ["intake"] = new(-30, 140, true, true),
        ["ambient"] = new(-40, 50, true, true),
        ["clock"] = new(0, 1439, true, true),
        ["ac"] = new(0, 1, false, true, Whole: true),
        ["pedal"] = new(0, 95, true, true),
        ["gear"] = new(0, 5, false, true, Whole: true),
        ["clutch"] = new(0, 1, false, true, Whole: true),
        ["brake"] = new(0, 1, false, true, Whole: true),
        ["rpm"] = new(0, 7600, true, false),
        ["speed"] = new(0, 250, true, false),
        ["battery"] = new(6, 16, true, false),
    };

    static readonly uint[] Partners = { 0x153, 0x613, 0x615 };

    const string ModeNames = "on, off, crank, idle, drive or stall";

    const string KLineVerbNames = "connect, disconnect, poll, quiet, send, every, raw or pause";

    /// <summary>
    /// A DS2 frame is the address, the length, the payload and the xor, and the length byte counts them
    /// all, so a payload is 255 - 3 = 252 bytes at most. A raw line goes on the wire as it stands.
    /// </summary>
    const int MaxPayloadBytes = 252, MaxRawBytes = 255;

    /// <summary>The longest period of a repeated request, in seconds.</summary>
    const double MaxEverySeconds = 60;

    public static Drive Parse(string text) =>
        TryParse(text, out var drive, out var error) ? drive : throw new DriveFormatException(error);

    public static bool TryParse(string text, [NotNullWhen(true)] out Drive? drive, [NotNullWhen(false)] out DriveError? error)
    {
        try
        {
            drive = Read(text);
            error = null;
            return true;
        }
        catch (DriveFormatException e)
        {
            drive = null;
            error = e.Error;
            return false;
        }
    }

    static Drive Read(string text)
    {
        string? name = null;
        var start = StartEeprom.Blank;
        var initial = new Dictionary<string, double>();
        var segments = new List<Segment>();
        var kline = new List<KLineAction>();   // the kline lines waiting for the segment they apply to
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = index + 1;
            var content = lines[index];
            var hash = content.IndexOf('#');
            if (hash >= 0)
                content = content[..hash];
            var tokens = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;
            if (name is null)
            {
                if (tokens[0] != "drive" || tokens.Length != 2)
                    throw Fail(line, "the first line must be 'drive NAME'");
                if (!NamePattern().IsMatch(tokens[1]))
                    throw Fail(line, $"a drive's name is lower-case letters, digits and dashes: '{tokens[1]}'");
                name = tokens[1];
                continue;
            }
            switch (tokens[0])
            {
                case "drive":
                    throw Fail(line, "a second 'drive' line");
                case "start":
                    if (segments.Count > 0)
                        throw Fail(line, "'start' comes before the segments");
                    if (tokens.Length != 2 || tokens[1] is not ("blank" or "seasoned"))
                        throw Fail(line, "'start' takes blank or seasoned");
                    start = tokens[1] == "blank" ? StartEeprom.Blank : StartEeprom.Seasoned;
                    break;
                case "set":
                    if (segments.Count > 0)
                        throw Fail(line, "'set' comes before the segments");
                    foreach (var (key, value) in Pairs(tokens.Skip(1), line))
                    {
                        var ramp = KeyValue(key, value, line);
                        if (!Rules[key].Kept)
                            throw Fail(line, $"set takes only the kept keys, not '{key}'");
                        if (ramp.From != ramp.To)
                            throw Fail(line, $"set cannot ramp: {key}={value}");
                        if (!initial.TryAdd(key, ramp.From))
                            throw Fail(line, $"key '{key}' given twice");
                    }
                    break;
                case "kline":
                    kline.Add(ReadKLine(tokens, line));
                    break;
                default:
                    segments.Add(ReadSegment(tokens, line) with { KLine = kline.ToArray() });
                    kline.Clear();
                    break;
            }
        }
        if (name is null)
            throw Fail(1, "the first line must be 'drive NAME'");
        if (segments.Count == 0)
            throw Fail(1, "a drive needs at least one segment");
        var drive = new Drive(name, start, segments, initial);
        Validate(drive);
        // After Validate: a kline line left over follows every segment, so a mistake Validate finds comes first in the file.
        if (kline.Count > 0)
            throw Fail(kline[0].Line, "a kline line needs a segment after it: it applies at the start of the next segment");
        return drive;
    }

    static Segment ReadSegment(string[] tokens, int line)
    {
        if (!TryNumber(tokens[0], out var seconds))
            throw Fail(line, $"a segment starts with its duration in seconds, not '{tokens[0]}'");
        if (seconds <= 0)
            throw Fail(line, $"duration must be above 0, not {tokens[0]}");
        if (tokens.Length < 2)
            throw Fail(line, $"a segment needs a mode: {ModeNames}");
        var mode = tokens[1] switch
        {
            "on" => DriveMode.On,
            "off" => DriveMode.Off,
            "crank" => DriveMode.Crank,
            "idle" => DriveMode.Idle,
            "drive" => DriveMode.Drive,
            "stall" => DriveMode.Stall,
            _ => throw Fail(line, $"unknown mode '{tokens[1]}': {ModeNames}"),
        };
        var keys = new Dictionary<string, Ramp>();
        var forces = new Dictionary<string, double>();
        var silenced = new HashSet<uint>();
        foreach (var (key, value) in Pairs(tokens.Skip(2), line))
        {
            switch (key)
            {
                case "force":
                    var (channel, volts) = Force(value, line);
                    if (!forces.TryAdd(channel, volts))
                        throw Fail(line, $"{channel} forced twice");
                    break;
                case "silence":
                    if (!silenced.Add(Partner(value, line)))
                        throw Fail(line, $"{value} silenced twice");
                    break;
                default:
                    if (!keys.TryAdd(key, KeyValue(key, value, line)))
                        throw Fail(line, $"key '{key}' given twice");
                    break;
            }
        }
        return new Segment(mode, seconds, keys, forces, silenced, line);
    }

    /// <summary>
    /// A <c>kline</c> line (plan 5): <c>connect</c>, <c>disconnect</c>, <c>poll</c> and <c>quiet</c> take
    /// nothing; <c>send HH…</c> is a DS2 request's payload (1–252 bytes, from the command byte on),
    /// <c>every SECONDS HH…</c> the same payload sent again every SECONDS (above 0, at most 60) and
    /// <c>raw HH…</c> bytes put on the line as they stand (1–255). A byte is two hex digits.
    /// </summary>
    static KLineAction ReadKLine(string[] tokens, int line)
    {
        if (tokens.Length < 2)
            throw Fail(line, $"kline needs a verb: {KLineVerbNames}");
        var verb = tokens[1] switch
        {
            "connect" => KLineVerb.Connect,
            "disconnect" => KLineVerb.Disconnect,
            "poll" => KLineVerb.Poll,
            "quiet" => KLineVerb.Quiet,
            "send" => KLineVerb.Send,
            "every" => KLineVerb.Every,
            "raw" => KLineVerb.Raw,
            "pause" => KLineVerb.Pause,
            _ => throw Fail(line, $"unknown kline verb '{tokens[1]}': {KLineVerbNames}"),
        };
        var arguments = tokens[2..];
        switch (verb)
        {
            case KLineVerb.Send or KLineVerb.Raw:
                return new KLineAction(verb, KLineBytes(verb, arguments, line), 0, line);
            case KLineVerb.Every:
                if (arguments.Length == 0)
                    throw Fail(line, "kline every needs its period in seconds, then the payload bytes");
                if (!TryNumber(arguments[0], out var period))
                    throw Fail(line, $"kline every takes its period in seconds first: '{arguments[0]}' is not a number");
                if (period <= 0)
                    throw Fail(line, $"kline every: the period must be above 0 seconds, not {arguments[0]}");
                if (period > MaxEverySeconds)
                    throw Fail(line, $"kline every: the period must be at most {N(MaxEverySeconds)} seconds, not {arguments[0]}");
                return new KLineAction(verb, KLineBytes(verb, arguments[1..], line), period, line);
            case KLineVerb.Pause:
                // The tester's wait after a reply, in milliseconds (the app's LiveLogger pause); kept as seconds, as Every's.
                if (arguments.Length != 1 || !TryNumber(arguments[0], out var ms) || ms < 0 || ms > 1000)
                    throw Fail(line, "kline pause takes the wait after each reply in milliseconds, 0 to 1000");
                return new KLineAction(verb, Array.Empty<byte>(), ms / 1000, line);
            default:
                if (arguments.Length > 0)
                    throw Fail(line, $"kline {tokens[1]} takes no arguments, not '{arguments[0]}'");
                return new KLineAction(verb, Array.Empty<byte>(), 0, line);
        }
    }

    /// <summary>The bytes of a send, every or raw line: as many as a frame carries, each two hex digits.</summary>
    static byte[] KLineBytes(KLineVerb verb, string[] tokens, int line)
    {
        var raw = verb == KLineVerb.Raw;
        var most = raw ? MaxRawBytes : MaxPayloadBytes;
        var name = verb.ToString().ToLowerInvariant();
        if (tokens.Length == 0 || tokens.Length > most)
            throw Fail(line, $"kline {name} takes 1 to {most} {(raw ? "bytes" : "payload bytes")}, not {tokens.Length}");
        var bytes = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Length != 2 || !char.IsAsciiHexDigit(tokens[i][0]) || !char.IsAsciiHexDigit(tokens[i][1]))
                throw Fail(line, $"kline {name}: '{tokens[i]}' is not a hex byte (two hex digits, 00 to FF)");
            bytes[i] = Convert.ToByte(tokens[i], 16);
        }
        return bytes;
    }

    static IEnumerable<(string Key, string Value)> Pairs(IEnumerable<string> tokens, int line)
    {
        foreach (var token in tokens)
        {
            var equals = token.IndexOf('=');
            if (equals <= 0 || equals == token.Length - 1)
                throw Fail(line, $"'{token}' is not KEY=VALUE");
            yield return (token[..equals], token[(equals + 1)..]);
        }
    }

    /// <summary>A key's value, checked against R17's table: known, in range, a ramp only where one is allowed.</summary>
    static Ramp KeyValue(string key, string value, int line)
    {
        if (!Rules.TryGetValue(key, out var rule))
            throw Fail(line, $"unknown key '{key}'");
        var ramp = Number(key, value, line);
        if (ramp.From != ramp.To && !rule.Ramps)
            throw Fail(line, $"{key} cannot ramp: {key}={value}");
        foreach (var v in new[] { ramp.From, ramp.To })
        {
            if (v < rule.Min || v > rule.Max || rule.Whole && v != Math.Floor(v))
            {
                var range = key == "gear" ? "N or 1…5" : rule.Whole ? "0 or 1" : $"{N(rule.Min)}…{N(rule.Max)}";
                throw Fail(line, $"{key} out of range {range}: {value}");
            }
        }
        return ramp;
    }

    static Ramp Number(string key, string value, int line)
    {
        if (key == "gear" && value == "N")
            return new Ramp(0, 0);
        var dots = value.IndexOf("..", StringComparison.Ordinal);
        if (dots < 0)
            return TryNumber(value, out var constant) ? new Ramp(constant, constant) : throw Fail(line, $"{key}: '{value}' is not a number");
        if (TryNumber(value[..dots], out var from) && TryNumber(value[(dots + 2)..], out var to))
            return new Ramp(from, to);
        throw Fail(line, $"{key}: '{value}' is not a number or a ramp A..B");
    }

    static (string Channel, double Volts) Force(string value, int line)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0 || !TryNumber(value[(colon + 1)..], out var volts))
            throw Fail(line, $"force takes CHANNEL:VOLTS, not '{value}'");
        var channel = value[..colon];
        if (!CarControls.CanForce(channel))
            throw Fail(line, $"cannot force '{channel}': a channel is AN0–AN15 (AN4 and AN13 are multiplexed: muxB0–7, muxA0–7)");
        if (volts is < 0 or > 5)
            throw Fail(line, $"force volts out of range 0…5: {value}");
        return (channel, volts);
    }

    static uint Partner(string value, int line) =>
        uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) && Partners.Contains(id)
            ? id
            : throw Fail(line, $"silence takes 153, 613 or 615, not '{value}'");

    static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>
    /// What each mode takes, and the order of the modes (R17): a crank needs its rpm; only a crank and a
    /// drive take an rpm, only a drive a speed; an idle is in neutral with the pedal released, and so is
    /// the pedal in a crank; an idle or a drive needs a crank since the key went on; with the engine
    /// possibly running (after a crank, an idle or a drive) only an idle, a drive, an off or another
    /// crank after a crank follow; an off comes neither first, nor twice, nor while moving. A stall
    /// (plan 5) takes the kept keys alone and follows an idle, or a drive that ends at standstill,
    /// only: a stall stands the car still, so, like an off, it cannot follow a segment that ends
    /// moving. After it the engine stands, so an idle or a drive needs a crank again, as after an
    /// off, while an on, a crank or an off may follow.
    /// </summary>
    static void Validate(Drive drive)
    {
        var cranked = false;
        DriveMode? previous = null;
        foreach (var at in DriveTimeline.Starts(drive))
        {
            var (segment, line) = (at.Segment, at.Segment.Line);
            var mode = segment.Mode;
            var name = mode.ToString().ToLowerInvariant();
            var keys = segment.Keys;
            if (mode == DriveMode.Crank && !keys.ContainsKey("rpm"))
                throw Fail(line, "crank needs rpm=, the starter's speed");
            if (mode == DriveMode.Crank && keys["rpm"].From != keys["rpm"].To)
                throw Fail(line, "crank's rpm cannot ramp: the starter turns at one speed");
            if (mode == DriveMode.Stall && UnkeptKey(segment) is { } unkept)
                throw Fail(line, $"stall takes only the kept keys, not '{unkept}'");
            if (keys.ContainsKey("rpm") && mode is not (DriveMode.Crank or DriveMode.Drive))
                throw Fail(line, "rpm only in crank and drive");
            if (keys.ContainsKey("speed") && mode != DriveMode.Drive)
                throw Fail(line, "speed only in drive");
            if (mode == DriveMode.Idle && keys.TryGetValue("gear", out var gear) && gear.From != 0)
                throw Fail(line, "idle is in neutral: gear=N or no gear");
            if (mode is DriveMode.Crank or DriveMode.Idle
                && (DriveTimeline.Values(at.Kept, segment, 0)["pedal"] > 0 || DriveTimeline.Values(at.Kept, segment, 1)["pedal"] > 0))
                throw Fail(line, $"pedal must be 0 in {name}");

            switch (mode)
            {
                case DriveMode.Off when previous is null:
                    throw Fail(line, "the first segment cannot be off");
                case DriveMode.Off when previous == DriveMode.Off:
                    throw Fail(line, "the key is already off");
                case DriveMode.Off when at.PreviousSpeed > 0:
                    throw Fail(line, $"off while moving: the segment before ends at {N(at.PreviousSpeed)} km/h");
                case DriveMode.Idle or DriveMode.Drive when !cranked:
                    throw Fail(line, $"{name} needs a crank first: the key is on and the engine stands");
                case DriveMode.On or DriveMode.Crank when previous is DriveMode.Idle or DriveMode.Drive
                                                       || mode == DriveMode.On && previous == DriveMode.Crank:
                    throw Fail(line, $"the engine runs: {name} needs the key off first");
                case DriveMode.Stall when previous is not (DriveMode.Idle or DriveMode.Drive):
                    throw Fail(line, "stall needs a running engine: it follows an idle or a drive");
                case DriveMode.Stall when at.PreviousSpeed > 0:
                    throw Fail(line, $"stall while moving: the car is still moving, the segment before ends at {N(at.PreviousSpeed)} km/h");
            }
            cranked = mode switch
            {
                DriveMode.Crank => true,
                DriveMode.Off or DriveMode.Stall => false,
                _ => cranked,
            };
            previous = mode;
        }
    }

    /// <summary>The first key (in name order) that <paramref name="segment"/> gives and a later segment would not keep: rpm, speed or battery.</summary>
    static string? UnkeptKey(Segment segment) =>
        segment.Keys.Keys.Where(key => !Rules[key].Kept).Order(StringComparer.Ordinal).FirstOrDefault();

    static DriveFormatException Fail(int line, string message) => new(new DriveError(line, message));

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NamePattern();
}
