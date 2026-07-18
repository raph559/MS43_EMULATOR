namespace Ms43Sim.Core;

/// <summary>
/// DS2 frames as the MS43 logger app speaks them (plan 5 R1): <c>address, length, payload…, xor</c>. The length
/// counts every byte of the frame, and the xor covers every byte before it, so the xor of a whole frame is 0.
/// </summary>
public static class Ds2
{
    /// <summary>The ECU's address: the first byte of the app's requests and of the ECU's replies.</summary>
    public const byte EcuAddress = 0x12;

    const int Overhead = 3;                                      // the address, the length byte and the xor
    const int MostPayload = byte.MaxValue - Overhead;            // the length byte is one byte

    /// <summary>
    /// The frame round <paramref name="payload"/> (the command byte on): the address, the length, the payload and the
    /// xor of all of them. The payload holds at most 252 bytes, as the length byte counts the other three too.
    /// </summary>
    public static byte[] Frame(IReadOnlyList<byte> payload, byte address = EcuAddress)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Count > MostPayload)
            throw new ArgumentException($"a DS2 payload holds at most {MostPayload} bytes: the length byte counts the address, itself and the xor too", nameof(payload));
        var frame = new byte[payload.Count + Overhead];
        frame[0] = address;
        frame[1] = (byte)frame.Length;
        for (var i = 0; i < payload.Count; i++)
            frame[2 + i] = payload[i];
        frame[^1] = Xor(frame, frame.Length - 1);
        return frame;
    }

    /// <summary>
    /// Whether <paramref name="bytes"/> are exactly one frame: at least 3 of them, the length byte their count, and the
    /// last byte the xor of the rest. The address is not checked: a frame is a frame whoever it is for.
    /// </summary>
    public static bool IsFrame(IReadOnlyList<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes.Count >= Overhead && bytes[1] == bytes.Count && Xor(bytes, bytes.Count) == 0;
    }

    static byte Xor(IReadOnlyList<byte> bytes, int count)
    {
        byte xor = 0;
        for (var i = 0; i < count; i++)
            xor ^= bytes[i];
        return xor;
    }
}
