using System.Globalization;

namespace Ms43Sim.Core;

/// <summary>
/// Pass: the check ran and found nothing (a condition that never occurred is "not exercised", a report
/// word); Fail: at least one finding; NotEvaluated: it could not run in some power cycle (plan 4 R16, C2).
/// </summary>
public enum CheckStatus { Pass, Fail, NotEvaluated }

/// <summary>What a check found: the drive cycles of its first and last occurrence, and how many it merged.</summary>
public sealed record Finding(string Check, string Message, ulong FirstCycle, ulong LastCycle, int Count = 1);

/// <summary>A check that observes a drive run (plan 4 R16).</summary>
public interface ICheck
{
    string Name { get; }

    CheckStatus Status { get; }

    IReadOnlyList<Finding> Findings { get; }
}

/// <summary>
/// A streaming check over a whole drive (plan 4 R16): it observes the machine's events, never the
/// output capture. <see cref="Attach"/> subscribes it to the drive's power cycles; at each one's start
/// (before its power-on) <see cref="Start"/> subscribes to that machine. Findings are in the drive's
/// time and accumulate across every power cycle; a repeat of the same cause merges into its first
/// finding (its last cycle and its count grow).
/// </summary>
public abstract class CheckBase : ICheck
{
    readonly List<Finding> findings = new();
    readonly Dictionary<string, int> byCause = new();

    protected CheckBase(string name) => Name = name;

    public string Name { get; }

    public IReadOnlyList<Finding> Findings => findings;

    /// <summary>The check's condition occurred at least once (a pass that never was is "not exercised").</summary>
    public bool Exercised { get; private set; }

    /// <summary>Why the check could not run, in the first power cycle where it could not; null when it always ran.</summary>
    public string? NotEvaluatedReason { get; private set; }

    public CheckStatus Status =>
        findings.Count > 0 ? CheckStatus.Fail : NotEvaluatedReason != null ? CheckStatus.NotEvaluated : CheckStatus.Pass;

    protected DriveContext Context { get; private set; } = null!;

    /// <summary>The current power cycle's machine.</summary>
    protected Machine Machine => Context.Machine!;

    /// <summary>The drive's cycle now.</summary>
    protected ulong Now => Context.PowerCycleStart + Machine.Cycles;

    public void Attach(DriveContext context)
    {
        Context = context;
        context.PowerCycleStarted += Start;
        context.PowerCycleEnded += End;
        Attached();
    }

    /// <summary>Once, when the check is attached to its drive: subscribe here to what spans power cycles.</summary>
    protected virtual void Attached()
    {
    }

    /// <summary>Subscribes to <paramref name="machine"/>, the power cycle <paramref name="index"/>'s, before its power-on.</summary>
    protected abstract void Start(Machine machine, int index);

    /// <summary>The power cycle's run stopped (the relay dropped or the drive ended).</summary>
    protected virtual void End(Machine machine, int index)
    {
    }

    /// <summary>A finding now.</summary>
    protected void Find(string cause, string message) => Find(cause, message, Now, Now);

    /// <summary>A finding over drive cycles <paramref name="first"/> to <paramref name="last"/>; the same cause merges.</summary>
    protected void Find(string cause, string message, ulong first, ulong last)
    {
        Exercised = true;
        if (byCause.TryGetValue(cause, out var i))
        {
            var found = findings[i];
            findings[i] = found with { LastCycle = Math.Max(found.LastCycle, last), Count = found.Count + 1 };
            return;
        }
        byCause[cause] = findings.Count;
        findings.Add(new Finding(Name, message, first, last));
    }

    /// <summary>The check's condition occurred.</summary>
    protected void Exercise() => Exercised = true;

    /// <summary>The check cannot run in this power cycle.</summary>
    protected void NotEvaluated(string reason) => NotEvaluatedReason ??= reason;

    /// <summary>A CPU address as SS:OOOO.</summary>
    protected static string Address(uint cpu) => string.Create(CultureInfo.InvariantCulture, $"{cpu >> 16:X2}:{cpu & 0xFFFF:X4}");
}
