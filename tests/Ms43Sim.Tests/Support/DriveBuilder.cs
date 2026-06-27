using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>
/// Drives in code for the check tests, before and beside the committed standard drives: each named one
/// is a short equivalent of the standard drive of that name (plan 4 Task 12), with shorter idles, and
/// with its drive segments starting near the idle speed so the engine's hand-offs are gentle.
/// </summary>
public static class DriveBuilder
{
    const string Warm = "set coolant=90 oil=90 intake=33 ambient=20 clock=60";

    static readonly Dictionary<string, string> Texts = new()
    {
        ["warm-start"] = $"""
            drive warm-start
            start blank
            {Warm}
            3 on
            1.5 crank rpm=200
            20 idle
            """,
        ["part-load"] = $"""
            drive part-load
            start seasoned
            {Warm}
            3 on
            1.5 crank rpm=200
            6 idle
            6 drive gear=3 speed=20..60 pedal=15
            6 drive gear=4 speed=60..70 pedal=12
            5 drive gear=4 speed=70..25 pedal=0         # the coast, down to near idle
            3 idle
            """,
        ["full-load"] = $"""
            drive full-load
            start seasoned
            {Warm}
            3 on
            1.5 crank rpm=200
            6 idle
            5 drive gear=3 speed=20..140 pedal=95       # the pull
            2 drive gear=3 rpm=6900                     # just below the limiter
            1 drive gear=3 rpm=7100                     # past the limiter for 1 s: rev-limit judges the cut
            0.4 drive gear=3 speed=150 pedal=95 clutch=1   # the no-lift shift
            3 drive gear=4 speed=150..90 pedal=95 clutch=0
            5 drive gear=4 speed=90..25 pedal=0         # the coast
            3 idle
            """,
        ["overrun"] = $"""
            drive overrun
            start seasoned
            {Warm}
            3 on
            1.5 crank rpm=200
            6 idle
            6 drive gear=4 speed=25..110 pedal=30
            8 drive gear=4 speed=110..30 pedal=0        # the lift: the overrun cut
            2 drive gear=4 speed=30 pedal=0 clutch=1    # the clutch in before the speed falls under idle
            3 idle
            """,
        ["key-cycle"] = $"""
            drive key-cycle
            start blank
            {Warm}
            3 on
            1.5 crank rpm=200
            10 idle
            15 off
            3 on
            1.5 crank rpm=200
            10 idle
            """,
    };

    /// <summary>The code drive called <paramref name="name"/>: warm-start, part-load, full-load, overrun or key-cycle.</summary>
    public static Drive Named(string name) =>
        Texts.TryGetValue(name, out var text)
            ? DriveParser.Parse(text)
            : throw new ArgumentException($"no code drive '{name}': {string.Join(", ", Texts.Keys)}", nameof(name));
}
