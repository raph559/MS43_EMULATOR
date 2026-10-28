namespace Ms43Sim.Core;

/// <summary>One trace sample: the time and one value per <see cref="VariableTrace.Columns"/> entry (null: not available).</summary>
public sealed record TraceRow(double Seconds, double?[] Values);

/// <summary>
/// The firmware's variables a run samples every 100 ms (plan 3, Reference facts; scalings from the
/// XDF and the logger frame), and the engine's own values beside them. RAM is read straight from
/// its arrays, so the values are real even while XPEN is clear. The engine columns (rpm_fed,
/// cam_in_fed, cam_ex_fed) are null without an engine.
/// </summary>
public static class VariableTrace
{
    const double TicksToMs = 128 / 24_000.0;                 // T1 ticks of 5.333 µs

    enum Area { Xram, Iram, External }

    sealed record Column(string Name, Func<Machine, Engine?, double?> Read, bool Hex = false);

    static readonly Column[] Table =
    {
        new("rpm_fed", (_, e) => e?.Rpm),
        new("rpm_ecu", (m, _) => Word(m, Area.Xram, 0xE0E4)),
        new("f900", (m, _) => Byte(m, Area.Iram, 0xF900)),
        new("fd2c", (m, _) => Word(m, Area.Iram, 0xFD2C), Hex: true),
        new("coolant_c", (m, _) => 0.75 * Byte(m, Area.External, 0x8433) - 48),
        new("intake_c", (m, _) => 0.75 * Byte(m, Area.External, 0x8432) - 48),
        new("oil_c", (m, _) => 0.79607843 * Byte(m, Area.External, 0x842F) - 48),
        new("battery_v", (m, _) => 0.10196078 * Byte(m, Area.Iram, 0xFBD5)),
        new("maf_kgh", (m, _) => 0.25 * Word(m, Area.Xram, 0xE32E)),
        new("tps_deg", (m, _) => 0.0072941464 * Word(m, Area.Xram, 0xE14C)),
        new("tps_sp_deg", (m, _) => 0.0072941464 * Word(m, Area.Xram, 0xE14E)),
        new("throttle_drive_pct", (m, _) => (short)Word(m, Area.Xram, 0xE0C8) / 327.68),
        new("pedal_pct", (m, _) => 0.0018310547 * Word(m, Area.Xram, 0xE0FE)),
        new("cam_in_deg", (m, _) => 0.375 * Byte(m, Area.External, 0x8972) + 60),
        new("cam_in_sp_deg", (m, _) => 0.375 * Byte(m, Area.External, 0x8971) + 60),
        new("cam_ex_deg", (m, _) => -0.375 * Byte(m, Area.External, 0x8AE0) - 60),
        new("cam_ex_sp_deg", (m, _) => -0.375 * Byte(m, Area.External, 0x8ADF) - 60),
        new("vanos_in_pct", (m, _) => Byte(m, Area.External, 0x8A79) / 2.56),
        new("vanos_ex_pct", (m, _) => Byte(m, Area.External, 0x8BE7) / 2.56),
        new("idle_target_rpm", (m, _) => Word(m, Area.External, 0x8878)),
        new("idle_valve_pct", (m, _) => Word(m, Area.External, 0x8862) / 655.36),
        new("ti1_ms", (m, _) => TicksToMs * Word(m, Area.Xram, 0xE46A)),
        new("ti2_ms", (m, _) => TicksToMs * Word(m, Area.Xram, 0xE46C)),
        new("deadtime_ms", (m, _) => TicksToMs * Word(m, Area.Xram, 0xE47C)),
        new("iga1_deg", (m, _) => 72 - 0.375 * Byte(m, Area.Iram, 0xFBAB)),
        new("dwell_ms", (m, _) => TicksToMs * Word(m, Area.Xram, 0xE4B8)),
        new("cam_in_raw", (m, _) => Byte(m, Area.External, 0x8973)),
        new("cam_ex_raw", (m, _) => Byte(m, Area.External, 0x8AE1)),
        new("cam_in_fed", (_, e) => e?.Vanos.IntakeAdvance),
        new("cam_ex_fed", (_, e) => e?.Vanos.ExhaustRetard),
        new("idle_i_pct", (m, _) => (short)Word(m, Area.External, 0x8856) / 655.36),
        new("fd9c", (m, _) => Word(m, Area.Iram, 0xFD9C), Hex: true),
        new("lambda_cfg", (m, _) => Byte(m, Area.External, 0x80F7)),
        new("e17a", (m, _) => Byte(m, Area.Xram, 0xE17A)),
        new("fd0c", (m, _) => Word(m, Area.Iram, 0xFD0C), Hex: true),
        new("vs_ecu_kmh", (m, _) => Byte(m, Area.External, 0x847F)),   // the road speed the firmware uses (LC, NLS, pops)
        new("fdf8", (m, _) => Word(m, Area.Iram, 0xFDF8), Hex: true),   // LC, NLS, RAL and (V8) the pops window bits 3 and 4
    };

    /// <summary>The Reference facts' trace table, in order (time_s is the row's own field).</summary>
    public static IReadOnlyList<string> Columns { get; } = Table.Select(c => c.Name).ToArray();

    /// <summary>The columns that hold a bit field: trace.csv writes them in hex.</summary>
    public static IReadOnlySet<string> HexColumns { get; } = Table.Where(c => c.Hex).Select(c => c.Name).ToHashSet();

    public static double?[] Sample(Machine machine, Engine? engine) => Table.Select(c => c.Read(machine, engine)).ToArray();

    static (byte[] Memory, uint Start) Region(Machine m, Area area) => area switch
    {
        Area.Xram => (m.Bus.Xram, 0xE000),
        Area.Iram => (m.Bus.Iram, 0xF600),
        _ => (m.Bus.ExternalRam, 0x8000),
    };

    static byte Byte(Machine m, Area area, uint address)
    {
        var (memory, start) = Region(m, area);
        return memory[address - start];
    }

    static ushort Word(Machine m, Area area, uint address)
    {
        var (memory, start) = Region(m, area);
        return (ushort)(memory[address - start] | memory[address - start + 1] << 8);
    }
}
