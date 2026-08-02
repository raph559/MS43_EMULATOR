using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Ms43Sim.Core;

/// <summary>A named stretch of the image: a table, a table's axis or a constant, at a file offset.</summary>
public sealed record CatalogueEntry(string Name, uint Address, int Bytes);

/// <summary>
/// The names the compare gives calibration bytes (plan 5 C8). The tables come from <c>calmaps_ms43x.csv</c>, which is
/// tracked: rows × cols × the element's size from its data address. Their axes come from the same rows, where an axis has
/// an address and a name: as many elements as the table has rows (the row axis) or columns (the column axis), each axis
/// once [call]. The single constants come from the MS43X001 XDF's XDFCONSTANT entries when it is given (it is local
/// only): their EMBEDDEDDATA address plus the XDF's BASEOFFSET, as <c>tools/xdf_import.py</c> reads them.
/// </summary>
public sealed class CalibrationCatalogue
{
    CalibrationCatalogue(IReadOnlyList<CatalogueEntry> entries, string source) => (Entries, Source) = (entries, source);

    /// <summary>No names: every changed calibration range is unnamed.</summary>
    public static CalibrationCatalogue Empty { get; } = new(Array.Empty<CatalogueEntry>(), "none: every changed calibration range is unnamed");

    /// <summary>Every entry, by address.</summary>
    public IReadOnlyList<CatalogueEntry> Entries { get; }

    /// <summary>Where the names come from, in words, for a report.</summary>
    public string Source { get; }

    public static CalibrationCatalogue Load(string calmapsCsv, string? xdf = null)
    {
        var entries = new List<CatalogueEntry>();
        var seen = new HashSet<(uint, string)>();
        var source = $"{Path.GetFileName(calmapsCsv)} (tables and their axes)";
        void Add(string name, uint address, int bytes)
        {
            if (name.Length > 0 && bytes > 0 && seen.Add((address, name)))
                entries.Add(new CatalogueEntry(name, address, bytes));
        }

        using (var reader = new StreamReader(calmapsCsv))
        {
            var header = Fields(reader.ReadLine() ?? throw new InvalidDataException($"{calmapsCsv} is empty"));
            int Column(string name) => header.IndexOf(name) is >= 0 and var i ? i : throw new InvalidDataException($"{calmapsCsv} has no column '{name}'");
            var (data, rows, cols, elem, name) = (Column("data"), Column("rows"), Column("cols"), Column("elem"), Column("name"));
            var (rowAxis, rowElem, rowName) = (Column("row_axis"), Column("row_axis_elem"), Column("row_axis_name"));
            var (colAxis, colElem, colName) = (Column("col_axis"), Column("col_axis_elem"), Column("col_axis_name"));
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0)
                    continue;
                var f = Fields(line);
                var (r, c) = (int.Parse(f[rows], CultureInfo.InvariantCulture), int.Parse(f[cols], CultureInfo.InvariantCulture));
                Add(f[name], Number(f[data]), r * c * Size(f[elem]));
                if (f[rowAxis].Length > 0)
                    Add(f[rowName], Number(f[rowAxis]), r * Size(f[rowElem]));
                if (f[colAxis].Length > 0)
                    Add(f[colName], Number(f[colAxis]), c * Size(f[colElem]));
            }
        }

        if (xdf is not null)
        {
            using var stream = File.OpenRead(xdf);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            var root = XDocument.Load(reader).Root ?? throw new InvalidDataException($"{xdf} has no root element");
            var offset = root.Element("XDFHEADER")?.Element("BASEOFFSET");
            var baseOffset = offset?.Attribute("offset") is { } o ? Number(o.Value) : 0;
            var subtract = offset?.Attribute("subtract")?.Value is "1";
            foreach (var constant in root.Descendants("XDFCONSTANT"))
            {
                if (constant.Element("EMBEDDEDDATA") is not { } data || data.Attribute("mmedaddress") is not { } address)
                    continue;
                var bits = data.Attribute("mmedelementsizebits") is { } b ? (int)Number(b.Value) : 8;
                var at = subtract ? baseOffset - Number(address.Value) : baseOffset + Number(address.Value);
                Add(constant.Element("title")?.Value.Trim() ?? "", at, Math.Max(1, bits / 8));
            }
            source += $" and {Path.GetFileName(xdf)} (constants)";
        }

        return new CalibrationCatalogue(entries.OrderBy(e => e.Address).ThenBy(e => e.Name, StringComparer.Ordinal).ToList(), source);
    }

    /// <summary>The full path of <paramref name="fileName"/> (it may name a sub-folder) in <paramref name="from"/> or a folder above it; null when none holds it.</summary>
    public static string? FindUpwards(string fileName, string from)
    {
        for (var folder = Directory.Exists(from) ? new DirectoryInfo(from) : new FileInfo(from).Directory; folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, fileName);
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }
        return null;
    }

    static int Size(string elem) => elem switch
    {
        "u8" or "s8" => 1,
        "u16" or "s16" => 2,
        "u32" or "s32" => 4,
        _ => throw new InvalidDataException($"unknown element type '{elem}'"),
    };

    /// <summary>"0x…" hexadecimal or decimal.</summary>
    static uint Number(string text)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>One CSV line's fields: commas separate them; a quoted field keeps its commas, and "" is a quote.</summary>
    static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    field.Append(line[++i]);
                else if (ch == '"')
                    quoted = false;
                else
                    field.Append(ch);
            }
            else if (ch == '"')
                quoted = true;
            else if (ch == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
                field.Append(ch);
        }
        fields.Add(field.ToString());
        return fields;
    }
}
