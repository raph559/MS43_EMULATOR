namespace Ms43Sim.Core;

/// <summary>
/// The flash writes a car run makes that a real flash ignores (R4; plan 4 Task 13): lone byte writes,
/// with no AM29F400 unlock cycle around them, each from one place in the program — 0x4286C from
/// 0C:6CEE (R4, 95.8 a second) and, once per after-run, 0x4289A from 0E:257C and 0x42000 from 0E:28DC.
/// They are writes beyond the 8 KiB RAM window that land in the flash's address space (plan 2). Any
/// other flash write stays "not modelled".
/// </summary>
public static class BenignAccesses
{
    static readonly (uint Address, uint Pc)[] FlashByteWrites = { (0x4286C, 0x0C6CEE), (0x4289A, 0x0E257C), (0x42000, 0x0E28DC) };

    public static bool IsBenignFlashWrite(FlashWriteEvent write) =>
        write.IsByte && FlashByteWrites.Contains((write.Address, write.Pc));
}
