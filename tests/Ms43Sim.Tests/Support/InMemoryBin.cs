using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>Images built in memory from a file's bytes and a few edits — never written to disk (owner rule).</summary>
public static class InMemoryBin
{
    /// <summary><paramref name="baseBytes"/> with each edit's bytes at its offset, on a copy, as the ECU holds it after a full write.</summary>
    public static FlashedImage With(byte[] baseBytes, params (int Offset, byte[] New)[] edits)
    {
        var copy = baseBytes.ToArray();
        foreach (var (offset, bytes) in edits)
            bytes.CopyTo(copy, offset);
        return FlashImageBuilder.AsFlashedFull(FirmwareImage.FromBytes(copy));
    }
}
