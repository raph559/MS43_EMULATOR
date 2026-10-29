namespace Ms43Sim.Core;

/// <summary>
/// What a segment does with the key and the engine (plan 4 R17). Stall (plan 5): the key stays on and
/// the engine plant is starved, so a running engine runs down; it follows an idle, or a drive that
/// ends at standstill.
/// </summary>
public enum DriveMode { On, Off, Crank, Idle, Drive, Stall }

/// <summary>What a <c>kline</c> line asks of the scripted K-line tester (plan 5).</summary>
public enum KLineVerb { Connect, Disconnect, Poll, Quiet, Send, Every, Raw, Pause }

/// <summary>
/// One <c>kline</c> line: its verb, its bytes (the DS2 payload from the command byte on for Send and
/// Every, the exact bytes for Raw, none for the other verbs), the repeat period in seconds (Every only,
/// else 0) and its 1-based line in the file.
/// </summary>
public sealed record KLineAction(KLineVerb Verb, IReadOnlyList<byte> Bytes, double Period, int Line);

/// <summary>The EEPROM a drive expects at its first key-on.</summary>
public enum StartEeprom { Blank, Seasoned }

/// <summary>A value across a segment: linear from From to To; From == To for a constant.</summary>
public sealed record Ramp(double From, double To)
{
    /// <summary>The value at <paramref name="fraction"/> (0…1) of the segment.</summary>
    public double At(double fraction) => From == To ? From : From + (To - From) * fraction;
}

/// <summary>
/// One line of a drive: its mode, its duration, the numeric keys it gives (ramps included; a gear is
/// 0 for N), the channels it forces and the CAN partners it silences, and its 1-based line in the file.
/// </summary>
public sealed record Segment(DriveMode Mode, double Seconds, IReadOnlyDictionary<string, Ramp> Keys,
    IReadOnlyDictionary<string, double> Forces, IReadOnlySet<uint> Silenced, int Line = 0)
{
    /// <summary>The K-line actions applied at the segment's start, in file order (plan 5).</summary>
    public IReadOnlyList<KLineAction> KLine { get; init; } = Array.Empty<KLineAction>();
}

/// <summary>A drive file (plan 4 R17): its name, the EEPROM it expects, its segments, and the values its set lines give.</summary>
public sealed record Drive(string Name, StartEeprom Start, IReadOnlyList<Segment> Segments, IReadOnlyDictionary<string, double> Initial);

/// <summary>The first mistake in a drive file, on its 1-based line.</summary>
public sealed record DriveError(int Line, string Message)
{
    public override string ToString() => $"line {Line}: {Message}";
}

public sealed class DriveFormatException(DriveError error) : Exception(error.ToString())
{
    public DriveError Error { get; } = error;
}
