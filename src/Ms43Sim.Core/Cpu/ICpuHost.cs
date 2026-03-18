namespace Ms43Sim.Core;

/// <summary>What the CPU asks of the rest of the chip when it executes a protected instruction.</summary>
public interface ICpuHost
{
    void Srvwdt();

    /// <summary>Returns false when DISWDT is no longer allowed (after EINIT or the first SRVWDT).</summary>
    bool Diswdt();

    void Einit();

    void SoftwareReset();

    void PowerDown();
}
