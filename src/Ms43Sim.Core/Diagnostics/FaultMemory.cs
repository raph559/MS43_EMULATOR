using System.Buffers.Binary;
using System.Numerics;

namespace Ms43Sim.Core;

/// <summary>
/// One fault path's state, decoded. Type: 0 above maximum, 1 below minimum, 2 no signal,
/// 3 implausible. The P-code is P0000 when the path has no OBD code.
/// </summary>
public sealed record FaultEntry(int Path, byte Ds2Code, string? Name, byte Status, int Type, string PCode,
    bool Stored, bool Present, bool Sporadic, bool ObdConfirmed, bool MilRequest, bool EmlRequest,
    int Frequency, int Heal, byte[] Environment)
{
    public string Label => Name ?? $"path {Path}";
}

/// <summary>Stored: list A (what a tester reads as stored); Detected: list B (every path detected). Both oldest first.</summary>
public sealed record FaultMemoryState(bool Recognised, IReadOnlyList<FaultEntry> Stored, IReadOnlyList<FaultEntry> Detected);

/// <summary>
/// The firmware's fault memory (plan 3, Reference facts) [obs; ver]. Each path has a 12-byte state
/// record at 91DAh + 12·p in the external RAM and a 22-byte descriptor at file 0x6D08E + 22·p:
/// its DS2 code, flags, and four pointers to the P-code word of each type at calibration file
/// 0x70000 + pointer. List A (9974h, count at 81BBh) holds the stored paths and list B (9A16h,
/// count at 81BCh) every path detected. The after-run saves the first 10 entries of list A in
/// EEPROM bytes 0x56–0xD5 under a CRC-16/ARC from 000Fh. A layout is recognised by the 162 DS2
/// codes at the descriptors; an unrecognised image decodes nothing.
/// </summary>
public static class FaultMemory
{
    const int Descriptors = 0x6D08E, DescriptorSize = 22, CalibrationBase = 0x70000;
    const int Records = 0x91DA, RecordSize = 12;
    const uint ListA = 0x9974, CountA = 0x81BB, ListB = 0x9A16, CountB = 0x81BC, ExternalRamBase = 0x8000;
    const int EepromRecord = 0x56, EepromCrcLength = 0x7E, EepromPaths = 0x04, EepromStates = 0x0E,
        EepromStateSize = 11, EepromCount = 0x7C, EepromMaxEntries = 10;
    const ushort EepromCrcSeed = 0x000F;

    /// <summary>The 162 DS2 codes are at the descriptors: this firmware's fault memory layout.</summary>
    public static bool Recognises(FirmwareImage image)
    {
        var bytes = image.Bytes;
        for (var p = 0; p < FaultPaths.Count; p++)
        {
            if (bytes[Descriptors + DescriptorSize * p] != FaultPaths.Ds2Code(p))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Path <paramref name="path"/>'s 12 state bytes: status (bits 0–3 the type bits, 5 stored,
    /// 6 present, 7 sporadic), debounce, frequency, heal, environment (4–7), time stamp, OBD byte
    /// (bit 2 confirmed), test counter. The type is the lowest set type bit, or 3 when none is set.
    /// MIL and EML are bits 15 and 14 of the type-0 P-code word [inf].
    /// </summary>
    public static FaultEntry Decode(FirmwareImage image, int path, ReadOnlySpan<byte> record)
    {
        var status = record[0];
        var typeBits = status & 0x0F;
        var type = typeBits == 0 ? 3 : BitOperations.TrailingZeroCount(typeBits);
        var descriptor = Descriptors + DescriptorSize * path;
        ushort PcodeWord(int t) => image.Read16(CalibrationBase + image.Read16(descriptor + 2 + 2 * t));
        var word = PcodeWord(type);
        var type0 = PcodeWord(0);
        return new FaultEntry(path, image.Bytes[descriptor], FaultPaths.Name(path), status, type, $"P{word & 0x3FFF:X4}",
            Stored: (status & 0x20) != 0, Present: (status & 0x40) != 0, Sporadic: (status & 0x80) != 0,
            ObdConfirmed: (record[10] & 0x04) != 0, MilRequest: (type0 & 0x8000) != 0, EmlRequest: (type0 & 0x4000) != 0,
            Frequency: record[2], Heal: record[3], Environment: record[4..8].ToArray());
    }

    /// <summary>List A and list B as the machine holds them now, each path once, oldest first.</summary>
    public static FaultMemoryState Read(Machine machine)
    {
        var image = machine.Image.Image;
        if (!Recognises(image))
            return new FaultMemoryState(false, Array.Empty<FaultEntry>(), Array.Empty<FaultEntry>());
        var ram = machine.Bus.ExternalRam;
        byte At(uint address) => ram[address - ExternalRamBase];
        FaultEntry Entry(int path) =>
            Decode(image, path, ram.AsSpan(Records - (int)ExternalRamBase + RecordSize * path, RecordSize));
        IReadOnlyList<FaultEntry> List(uint start, uint count) =>
            Enumerable.Range(0, Math.Min((int)At(count), FaultPaths.Count))
                .Select(i => (int)At(start + (uint)i))
                .Where(path => path < FaultPaths.Count)
                .Distinct()
                .Select(Entry)
                .ToList();
        return new FaultMemoryState(true, List(ListA, CountA), List(ListB, CountB));
    }

    /// <summary>
    /// The after-run record in EEPROM bytes 0x56–0xD5: stamp, 883Ch, ten path indexes, 11 state
    /// bytes per entry, the count (0xD2) and the CRC (0xD4). Only <c>min(count, 10)</c> entries are
    /// decoded: unused slots hold stale bytes. Empty when the CRC fails or the layout is not recognised.
    /// </summary>
    public static IReadOnlyList<FaultEntry> SavedInEeprom(FirmwareImage image, byte[] eeprom)
    {
        if (!Recognises(image))
            return Array.Empty<FaultEntry>();
        var record = eeprom.AsSpan(EepromRecord, EepromCrcLength + 2);
        var crc = Checksums.Crc16(record[..EepromCrcLength], EepromCrcSeed);
        if (crc != BinaryPrimitives.ReadUInt16LittleEndian(record[EepromCrcLength..]))
            return Array.Empty<FaultEntry>();
        var entries = new List<FaultEntry>();
        var count = Math.Min((int)record[EepromCount], EepromMaxEntries);
        for (var k = 0; k < count; k++)
        {
            var path = record[EepromPaths + k];
            if (path >= FaultPaths.Count)
                continue;
            var state = new byte[RecordSize];
            record.Slice(EepromStates + EepromStateSize * k, EepromStateSize).CopyTo(state);
            entries.Add(Decode(image, path, state));
        }
        return entries;
    }

    /// <summary>Why a detected path is expected on this car, or null: never for a stored entry.</summary>
    public static string? Explanation(FaultEntry entry) => entry.Stored ? null : entry.Path switch
    {
        80 => "1F3h is DSC-only and this car has ASC [owner]; debounce increment 0, never stored",
        156 => "reported until the cluster's first 615h is parsed (a start-up transient a real car shows too); debounce increment 0, never stored",
        _ => null,
    };
}
