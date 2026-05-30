using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>One call of a decision task: its inputs at the entry and its results at the read-out, by address.</summary>
public sealed record DecisionCall(ulong Cycle, IReadOnlyDictionary<uint, int> Entry, IReadOnlyDictionary<uint, int> Result);

/// <summary>
/// The car file's decision tasks, snapshotted through <see cref="Machine.OnInstruction"/> (plan 3,
/// Reference facts, "Decisions and the calibration"): ignition 0A:C2AE → 0A:D428, injection
/// 0C:A8CC → 0C:B07A, and the supply task 05h 0D:8504 → 0D:855A. Each call pairs the last entry
/// snapshot before the read-out with the read-out (a hook can fire twice when an interrupt enters
/// at its boundary). Addresses read through the map: 8000h–9FFFh external RAM, E000h–E7FFh XRAM,
/// F600h–FDFFh IRAM; the listed words as words, every other address as a byte.
/// </summary>
public sealed class DecisionLog
{
    static readonly uint[] IgnitionWords = { 0xE0E4, 0xE32C, 0x88DE, 0x8846, 0xE2C6, 0xFD9A, 0xFD2C, 0xFD36, 0xFD48, 0xFD2E, 0xFD14, 0xFDC0, 0xFDB2 };
    static readonly uint[] IgnitionBytes = new uint[] { 0x81DE, 0x8437, 0x81D1, 0x81D2, 0x849C, 0x849D, 0xE383, 0x8849, 0x883F, 0x847F }
        .Concat(Range(0x80E1, 6)).ToArray();
    static readonly uint[] InjectionWords = { 0xE33A, 0x919A, 0x88DE, 0x8C84, 0x8C7E, 0x9174, 0x9172, 0xFD36, 0xFD4E, 0xFD48, 0xFD2C };

    readonly List<DecisionCall> ignition = new(), injection = new(), supply = new();

    public DecisionLog(Machine machine)
    {
        Hook(machine, ignition, 0x0AC2AE, IgnitionWords, IgnitionBytes,
            0x0AD428, new uint[] { 0xE2B4 }, Range(0xFBAB, 6).Concat(Range(0x83A2, 6)).ToArray());
        Hook(machine, injection, 0x0CA8CC, InjectionWords, new uint[] { 0x8D0F },
            0x0CB07A, new uint[] { 0x8DAA, 0x8F18 }, Array.Empty<uint>());
        Hook(machine, supply, 0x0D8504, new uint[] { 0xFD32 }, new uint[] { 0xFBD5, 0x80ED },
            0x0D855A, new uint[] { 0xE47C, 0xE4B8 }, Array.Empty<uint>());
    }

    public IReadOnlyList<DecisionCall> Ignition => ignition;

    public IReadOnlyList<DecisionCall> Injection => injection;

    /// <summary>Task 05h: the dead time and the dwell.</summary>
    public IReadOnlyList<DecisionCall> Supply => supply;

    static void Hook(Machine machine, List<DecisionCall> calls, uint entryAt, uint[] entryWords, uint[] entryBytes,
        uint resultAt, uint[] resultWords, uint[] resultBytes)
    {
        Dictionary<uint, int>? entry = null;
        machine.OnInstruction(entryAt, m => entry = Read(m, entryWords, entryBytes));
        machine.OnInstruction(resultAt, m =>
        {
            if (entry is null)
                return;
            calls.Add(new DecisionCall(m.Cycles, entry, Read(m, resultWords, resultBytes)));
            entry = null;
        });
    }

    static Dictionary<uint, int> Read(Machine m, uint[] words, uint[] bytes)
    {
        var values = new Dictionary<uint, int>();
        foreach (var a in words)
            values[a] = Byte(m, a) | Byte(m, a + 1) << 8;
        foreach (var a in bytes)
            values[a] = Byte(m, a);
        return values;
    }

    static byte Byte(Machine m, uint a) => a switch
    {
        >= 0x8000 and <= 0x9FFF => m.Bus.ExternalRam[a - 0x8000],
        >= 0xE000 and <= 0xE7FF => m.Bus.Xram[a - 0xE000],
        >= 0xF600 and <= 0xFDFF => m.Bus.Iram[a - 0xF600],
        _ => throw new ArgumentOutOfRangeException(nameof(a), $"{a:X4}h is not in the RAM map"),
    };

    static IEnumerable<uint> Range(uint start, int count) => Enumerable.Range(0, count).Select(i => start + (uint)i);
}
