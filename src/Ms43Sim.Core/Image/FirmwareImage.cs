using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ms43Sim.Core;

/// <summary>An immutable 512 KiB MS43 flash image (AM29F400, file offset = CPU address &amp; 0x7FFFF).</summary>
public sealed class FirmwareImage
{
    public const int Size = 0x80000;

    readonly byte[] bytes;
    string? sha256;

    FirmwareImage(byte[] bytes) => this.bytes = bytes;

    public static FirmwareImage Load(string path)
    {
        var data = File.ReadAllBytes(path);
        CheckSize(data.Length, path);
        return new FirmwareImage(data);
    }

    public static FirmwareImage FromBytes(ReadOnlySpan<byte> bytes)
    {
        CheckSize(bytes.Length, "Image");
        return new FirmwareImage(bytes.ToArray());
    }

    public ReadOnlySpan<byte> Bytes => bytes;

    public string Sha256 => sha256 ??= Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public ushort Read16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));

    public uint Read32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    public byte[] ToArray() => (byte[])bytes.Clone();

    static void CheckSize(int length, string what)
    {
        if (length != Size)
            throw new InvalidDataException(
                $"{what} must be {Size} bytes (512 KiB), got {length}.");
    }
}
