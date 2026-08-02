namespace Ms43Sim.Core;

/// <summary>
/// What the compare lines up of one drive run (plan 5 Task 7): its sparks and injections and its output events in the
/// drive's cycles, its trace every 100 ms, the fault memory stored at the end of each power cycle (in order), how each power
/// cycle ended, and the EEPROM at the end.
/// </summary>
public sealed record RunTrack(IReadOnlyList<Spark> Sparks, IReadOnlyList<Injection> Injections, IReadOnlyList<TraceRow> Trace,
    IReadOnlyList<OutputEvent> Outputs, IReadOnlyList<FaultEntry> Faults, IReadOnlyList<CycleEnd> Ends, byte[] Eeprom)
{
    public static RunTrack From(DriveResult result) => new(result.Sparks, result.Injections, result.Trace, result.OutputEvents,
        result.PowerCycles.SelectMany(c => c.Faults.Stored).ToArray(), result.PowerCycles.Select(c => c.End).ToArray(),
        result.Eeprom.FinalImage);
}
