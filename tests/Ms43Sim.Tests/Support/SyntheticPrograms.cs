using System.Globalization;
using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>
/// Small C167 programs on an otherwise erased (FFh) image, for the failing sides the car file never shows: the owner's
/// rule allows no pokes into a car-file run (plan 5 final review I2). Each program starts with DISWDT and chip select 3
/// set up for the output latch, as the car's boot does. With a handover, the image also carries the car file's checkpoint
/// bytes and the reset vector jumps through 0E:366A to the program at 0E:2EBA, so a run recognises it and watches its
/// main relay (plan 2's start-up watch); nothing else of the car's program is there.
/// </summary>
public static class SyntheticPrograms
{
    const string Prologue = "A55AA5A5 E60E0008 E68CAE85";        // DISWDT ; MOV ADDRSEL3,#0800h ; MOV BUSCON3,#85AEh

    /// <summary>The main relay released: the output latch := 58h (MOV R0,#0058h ; EXTP #20h,#1 ; MOV 0000h,R0).</summary>
    public const string ReleaseRelay = "E6F05800 D7402000 F6F00000";

    /// <summary>SRST.</summary>
    public const string Reset = "B748B7B7";

    /// <summary>JMPR cc_UC to itself: the program stops here.</summary>
    public const string Hold = "0DFF";

    /// <summary>Injector 1 opened and closed: P2.0 made an output, high, then low and high again (P2 = FFC0h, DP2 = FFC2h).</summary>
    public const string PulseInjector1 = "E6E00100 E6E10100 E6E00000 E6E00100";

    /// <summary>
    /// A counted loop of <paramref name="turns"/> × 65,535 (R2 and R1): about 0.115 s a turn on the emulator's flash timing
    /// (measured: the relay dropped 2.98 s into a run after 26 turns).
    /// </summary>
    public static string Wait(int turns) =>
        string.Create(CultureInfo.InvariantCulture, $"E6F2{turns & 0xFF:X2}{turns >> 8:X2} E6F1FFFF 2811 3DFE 2821 3DFA");

    /// <summary>The image: <paramref name="code"/> after the prologue, at 00:0000 or, with <paramref name="handover"/>, at 0E:2EBA.</summary>
    public static FlashedImage Image(bool handover, params string[] code)
    {
        var image = Enumerable.Repeat((byte)0xFF, FirmwareImage.Size).ToArray();
        void Put(int offset, string hex) => Hex.Bytes(hex).CopyTo(image, offset);
        var program = Prologue + " " + string.Join(' ', code);
        if (!handover)
        {
            Put(0, program);
            return FlashImageBuilder.Raw(FirmwareImage.FromBytes(image));
        }
        foreach (var (offset, hex) in new[]
        {
            (0x02EFC, "A758A7A7"), (0x6366A, "DA0EBA2E"), (0x636BC, "BF88"), (0x63700, "DA0D08EC"),
            (0x63812, "9A42FE90"), (0x60094, "8A420290"), (0x60144, "A758A7A7"),
        })
            Put(offset, hex);                                            // RunState's checkpoints; 0E:366A is CALLS 0Eh,2EBAh
        Put(0, "FA0E6A36");                                              // JMPS 0Eh,366Ah
        Put(0x62EBA, program);
        return FlashImageBuilder.Raw(FirmwareImage.FromBytes(image));
    }
}
