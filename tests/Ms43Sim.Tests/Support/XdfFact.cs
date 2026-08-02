using Ms43Sim.Core;

namespace Ms43Sim.Tests.Support;

/// <summary>
/// A fact that needs the MS43X001 XDF, which is local only and never committed (plan 5 Task 8): it is skipped, never
/// passed, when no folder from the test assembly upwards holds <see cref="XdfPath"/>.
/// </summary>
public sealed class XdfFactAttribute : FactAttribute
{
    public const string XdfPath = "defs/Siemens_MS43_MS43X001_512K_v1.0.0.xdf";

    public XdfFactAttribute()
    {
        if (CalibrationCatalogue.FindUpwards(XdfPath, AppContext.BaseDirectory) is null)
            Skip = $"{XdfPath} is not in this checkout or a folder above it";
    }
}
