namespace Ms43Sim.Core;

/// <summary>Board facts and the assumptions a run is made under (each one is in the report).</summary>
public sealed record MachineOptions
{
    /// <summary>[inf] 24 MHz: the boot's T1 loop counts 375 ticks of fCPU/64 per ms; ASC0 S0BG=4Dh gives 9,615 baud.</summary>
    public double CpuHz { get; init; } = 24_000_000;

    /// <summary>The car's ECU has 8 KiB of external RAM (REPORT.md §9.5).</summary>
    public int ExternalRamBytes { get; init; } = 8192;

    /// <summary>[assumption] What a read of a chip-select window with no device returns.</summary>
    public ushort UnmodelledReadValue { get; init; } = 0xFFFF;

    public int BranchTraceDepth { get; init; } = 64;
}
