namespace Ms43Sim.Core;

public readonly record struct DecodedInstruction(uint Address, int Size, string Mnemonic, bool Undefined);

/// <summary>Instruction listing over <see cref="OpcodeTable"/>, for reports and the c167dis cross-check.</summary>
public static class Decoder
{
    public static DecodedInstruction Decode(ReadOnlySpan<byte> image, int offset, uint address)
    {
        var op = image[offset];
        var b1 = offset + 1 < image.Length ? image[offset + 1] : (byte)0;
        return new DecodedInstruction(address, OpcodeTable.Size(op, b1), OpcodeTable.Mnemonic(op, b1),
            OpcodeTable.IsUndefined(op, b1));
    }

    /// <summary>A linear sweep of [start, end): an undefined instruction advances by 2.</summary>
    public static IEnumerable<DecodedInstruction> Sweep(byte[] image, int start, int end)
    {
        for (var offset = start; offset < end;)
        {
            var d = Decode(image, offset, (uint)offset);
            yield return d;
            offset += d.Size;
        }
    }
}
