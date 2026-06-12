namespace Ms43Sim.Core;

/// <summary>What a segment does with the key and the engine (plan 4 R17).</summary>
public enum DriveMode { On, Off, Crank, Idle, Drive }

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
    IReadOnlyDictionary<string, double> Forces, IReadOnlySet<uint> Silenced, int Line = 0);

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
