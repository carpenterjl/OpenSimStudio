using System.Globalization;
using System.Text;

namespace OpenSim.Optics.Materials;

/// <summary>
/// Reads an OpticStudio AGF glass catalog.
/// <para>
/// <b>Layout.</b> One record per line, named by its first word. The field order is from the
/// ZemaxGlass user manual (zemaxglass_manual.txt, the AGF layout block and the nomenclature),
/// cross-checked against AGFFileReader.jl (<c>src/GlassDownload.jl</c>, the table above
/// <c>agffile_to_catalog</c>) and ZemaxGlass.py (<c>parse_glass_input</c>):
/// <code>
/// CC comment
/// NM name formula glass-code nd Vd exclude-substitution status melt-frequency
/// GC comment
/// ED TCE(−30…70) TCE(100…300) density ΔPgF ignore-thermal-expansion
/// CD C1 … C10
/// TD D0 D1 D2 E0 E1 λtk Tref
/// OD relative-cost CR FR SR AR PR
/// LD λmin λmax
/// IT λ transmittance thickness   (repeated)
/// </code>
/// </para>
/// <para>
/// <b>What real files do, and what this reader does about it</b> (each seen in the vendor
/// catalogs read for OP-11):
/// <list type="bullet">
/// <item>Encoding: UTF-16 LE with a byte-order mark (HOYA), UTF-8 with or without one, or plain
/// ASCII. A UTF-16 BOM, a UTF-8 BOM, or UTF-16 LE without a BOM (every odd byte zero in the
/// first characters) is detected; anything else is read as UTF-8, where a stray byte can only
/// spoil comment text (a number it spoils fails to parse and is named).</item>
/// <item>CRLF or LF line ends, and blank lines (SCHOTT).</item>
/// <item>A line that does not start with a keyword continues the previous record (NIKON wraps its
/// CD and TD lines). It is appended directly, as ZemaxGlass.py's <c>parse_glass_input</c> does;
/// every wrapped line in the vendor files ends in a space.</item>
/// <item>Text before the first keyword (some files open with "Reproduced here by permission…")
/// and lines starting with "!" (OHARA's legend; ZemaxGlass skips them) are catalog comments.</item>
/// <item>An empty TD line (SCHOTT) means no thermal data.</item>
/// <item>"-" or "_" in place of a number means absent (both readers treat "-" so;
/// AGFFileReader's <c>glassinfo_to_argstring</c> notes SCHOTT's "_" for a missing cost).</item>
/// <item>MD and BD lines, which ZemaxGlass.py lists as keywords without reading them, are
/// skipped with a note: their layout is in neither source.</item>
/// </list>
/// A malformed number in a field that is read throws <see cref="InvalidDataException"/> naming
/// the catalog, line, glass and field: a catalog is never half-read silently.
/// </para>
/// </summary>
public static class AgfReader
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "CC", "NM", "GC", "ED", "CD", "TD", "OD", "LD", "IT", "MD", "BD"
    };

    /// <summary>Reads the file at <paramref name="path"/>; the catalog is named after the file.</summary>
    public static OpticalCatalog Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return Parse(bytes, Path.GetFileNameWithoutExtension(path), path);
    }

    /// <summary>Reads catalog bytes in any of the encodings the class remarks list.</summary>
    public static OpticalCatalog Parse(byte[] bytes, string catalogName, string? sourcePath = null) =>
        ParseText(Decode(bytes), catalogName, sourcePath);

    /// <summary>The text of an AGF file's bytes.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (LooksLikeUtf16LittleEndian(bytes)) return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>ASCII text in UTF-16 LE has a zero in every odd byte; text in UTF-8 has none.</summary>
    private static bool LooksLikeUtf16LittleEndian(byte[] bytes)
    {
        int pairs = Math.Min(bytes.Length / 2, 64);
        if (pairs < 2) return false;
        for (int i = 0; i < pairs; i++)
            if (bytes[2 * i] == 0 || bytes[2 * i + 1] != 0) return false;
        return true;
    }

    /// <summary>Reads catalog text already decoded.</summary>
    public static OpticalCatalog ParseText(string text, string catalogName, string? sourcePath = null)
    {
        var records = SplitRecords(text);
        var comments = new List<string>();
        var notes = new List<string>();
        var materials = new List<OpticalMaterial>();
        var context = new Context(catalogName);
        GlassState? glass = null;

        void Finish()
        {
            if (glass is null) return;
            materials.Add(glass.Build());
            glass = null;
        }

        foreach (var record in records)
        {
            context.Line = record.Line;
            context.Keyword = record.Keyword;
            string[] fields = record.Rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (record.Keyword)
            {
                case "CC":
                    comments.Add(record.Rest.Trim());
                    continue;
                case "MD":
                case "BD":
                    notes.Add($"Line {record.Line}: a {record.Keyword} line was skipped; its layout is not in the sources this reader follows.");
                    continue;
                case "NM":
                    Finish();
                    glass = ReadName(fields, context, notes);
                    context.Glass = glass.Material.Name;
                    continue;
            }

            if (glass is null)
                throw context.Error($"a {record.Keyword} line comes before the first NM line, so it belongs to no glass.");

            switch (record.Keyword)
            {
                case "GC":
                    string comment = record.Rest.Trim();
                    if (comment.Length > 0)
                        glass.Material = glass.Material with
                        {
                            Comment = glass.Material.Comment.Length == 0 ? comment : glass.Material.Comment + " " + comment
                        };
                    break;
                case "ED":
                    glass.Material = glass.Material with
                    {
                        ThermalExpansion = context.Number(fields, 0, "TCE (−30…70 °C)"),
                        ThermalExpansionHigh = context.Number(fields, 1, "TCE (100…300 °C)"),
                        Density = context.Number(fields, 2, "density"),
                        DeltaPgF = context.Number(fields, 3, "ΔPgF"),
                        IgnoreThermalExpansion = (context.Integer(fields, 4, "ignore thermal expansion") ?? 0) != 0
                    };
                    break;
                case "CD":
                    var coefficients = new double[fields.Length];
                    for (int i = 0; i < fields.Length; i++)
                        coefficients[i] = context.Number(fields, i, $"C{i + 1}")
                            ?? throw context.Error($"coefficient C{i + 1} is printed as '{fields[i]}'; a dispersion coefficient cannot be absent.");
                    glass.Material = glass.Material with { Coefficients = coefficients };
                    break;
                case "TD":
                    glass.Material = glass.Material with { Thermal = ReadThermal(fields, context, notes) };
                    break;
                case "OD":
                    glass.Material = glass.Material with
                    {
                        RelativeCost = context.Number(fields, 0, "relative cost"),
                        ClimaticResistance = context.Number(fields, 1, "CR"),
                        StainResistance = context.Number(fields, 2, "FR"),
                        AcidResistance = context.Number(fields, 3, "SR"),
                        AlkaliResistance = context.Number(fields, 4, "AR"),
                        PhosphateResistance = context.Number(fields, 5, "PR")
                    };
                    break;
                case "LD":
                    double? lo = context.Number(fields, 0, "λmin"), hi = context.Number(fields, 1, "λmax");
                    if (lo is null || hi is null)
                        throw context.Error("the LD line needs the shortest and the longest wavelength.");
                    if (!(lo > 0 && hi >= lo))
                        throw context.Error($"the LD range {lo}–{hi} µm is not a range of positive wavelengths.");
                    glass.Material = glass.Material with { MinMicrometers = lo, MaxMicrometers = hi };
                    break;
                case "IT":
                    double? w = context.Number(fields, 0, "IT wavelength"), t = context.Number(fields, 1, "IT transmittance");
                    if (w is null || t is null)
                        throw context.Error("an IT line needs a wavelength and a transmittance.");
                    glass.Transmittance.Add(new TransmittancePoint(w.Value, t.Value, context.Number(fields, 2, "IT thickness")));
                    break;
            }
        }
        Finish();

        foreach (var group in materials.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            notes.Add($"Glass '{group.Key}' appears {group.Count()} times; looking it up by name finds the first.");
        foreach (var m in materials.Where(m => m.Formula is null && m.Tabulated is null))
            notes.Add($"Glass '{m.Name}' uses dispersion formula {DispersionFormulas.NameOf(m.FormulaNumber ?? 0)}; its index cannot be computed.");
        foreach (var m in materials.Where(m => m.ValidRange is null))
            notes.Add($"Glass '{m.Name}' has no LD line, so it has no valid wavelength range and its index cannot be computed.");

        return new OpticalCatalog
        {
            Name = catalogName,
            SourcePath = sourcePath,
            Comments = comments,
            Materials = materials,
            Notes = notes
        };
    }

    private static GlassState ReadName(string[] fields, Context context, List<string> notes)
    {
        if (fields.Length < 2)
            throw context.Error("an NM line needs at least a name and a dispersion formula number.");
        string name = fields[0];
        context.Glass = name;
        int formula = context.Integer(fields, 1, "dispersion formula")
                      ?? throw context.Error("the dispersion formula number is absent.");
        if (formula == (int)DispersionFormula.Sellmeier2)
            notes.Add($"Glass '{name}' uses formula 4 (Sellmeier 2). The two readers this program follows disagree on that " +
                      "formula's second term and neither form is confirmed against OpticStudio; its indices use AGFFileReader's " +
                      "form and are unverified.");
        int? status = context.Integer(fields, 6, "status");
        if (status is int s && !Enum.IsDefined(typeof(GlassStatus), s))
            notes.Add($"Glass '{name}' has status {s}, which is not one of 0–4 (standard, preferred, obsolete, special, melt).");
        string? code = fields.Length > 2 && !IsAbsent(fields[2]) ? fields[2] : null;
        return new GlassState(new OpticalMaterial
        {
            Name = name,
            Catalog = context.Catalog,
            FormulaNumber = formula,
            GlassCode = code,
            Nd = context.Printed(fields, 3, "nd"),
            Vd = context.Printed(fields, 4, "Vd"),
            ExcludeSubstitution = (context.Integer(fields, 5, "exclude substitution") ?? 0) != 0,
            StatusNumber = status,
            MeltFrequency = context.Integer(fields, 7, "melt frequency")
        });
    }

    private static ThermalData? ReadThermal(string[] fields, Context context, List<string> notes)
    {
        if (fields.Length == 0) return null;                    // SCHOTT's empty TD line
        var values = new double?[7];
        string[] names = { "D0", "D1", "D2", "E0", "E1", "λtk", "reference temperature" };
        for (int i = 0; i < 7; i++) values[i] = context.Number(fields, i, names[i]);
        if (values.Any(v => v is null))
        {
            notes.Add($"Line {context.Line}: glass '{context.Glass}' has a TD line without all seven numbers; its thermal data are left out.");
            return null;
        }
        return new ThermalData(values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value, values[4]!.Value,
            values[5]!.Value, values[6]!.Value);
    }

    private static bool IsAbsent(string field) => field is "-" or "_";

    private sealed record Record(string Keyword, string Rest, int Line);

    /// <summary>Splits the text into keyword records, joining continuation lines. Leading text
    /// and "!" lines become CC records, so the catalog's comments keep the file's order.</summary>
    private static List<Record> SplitRecords(string text)
    {
        var records = new List<(string Keyword, StringBuilder Text, int Line)>();
        string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith('!'))
            {
                records.Add(("CC", new StringBuilder(line[1..]), i + 1));
                continue;
            }
            string trimmed = line.TrimStart();
            int end = 0;
            while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end])) end++;
            string first = trimmed[..end];
            if (Keywords.Contains(first))
                records.Add((first, new StringBuilder(trimmed[end..]), i + 1));
            else if (records.Count > 0)
                records[^1].Text.Append(line);
            else
                records.Add(("CC", new StringBuilder(line), i + 1));
        }
        return records.Select(r => new Record(r.Keyword, r.Text.ToString(), r.Line)).ToList();
    }

    /// <summary>A glass being read: the material so far and its transmittance rows.</summary>
    private sealed class GlassState
    {
        public GlassState(OpticalMaterial material) => Material = material;
        public OpticalMaterial Material { get; set; }
        public List<TransmittancePoint> Transmittance { get; } = new();
        public OpticalMaterial Build() => Material with { Transmittance = Transmittance.ToArray() };
    }

    /// <summary>Where the reader is, for messages, and the field readers that use it.</summary>
    private sealed class Context
    {
        public Context(string catalog) => Catalog = catalog;
        public string Catalog { get; }
        public int Line { get; set; }
        public string Keyword { get; set; } = "";
        public string? Glass { get; set; }

        public InvalidDataException Error(string what) =>
            new($"Catalog '{Catalog}', line {Line}{(Glass is null ? "" : $" (glass '{Glass}')")}: {what}");

        /// <summary>Field <paramref name="index"/> as a number; null when the line stops short
        /// of it or prints an absent mark.</summary>
        public double? Number(string[] fields, int index, string what) => Printed(fields, index, what)?.Value;

        public PrintedNumber? Printed(string[] fields, int index, string what)
        {
            if (index >= fields.Length || IsAbsent(fields[index])) return null;
            if (PrintedNumber.TryParse(fields[index], out var number)) return number;
            throw Error($"{what} in the {Keyword} line is '{fields[index]}', which is not a number.");
        }

        /// <summary>An integer field; a whole number printed with a decimal point ("2.0") is
        /// accepted, as ZemaxGlass.py's <c>int_cast</c> accepts it.</summary>
        public int? Integer(string[] fields, int index, string what)
        {
            double? value = Number(fields, index, what);
            if (value is null) return null;
            if (value != Math.Round(value.Value) || Math.Abs(value.Value) > int.MaxValue)
                throw Error($"{what} in the {Keyword} line is '{fields[index]}', which is not a whole number.");
            return (int)value.Value;
        }
    }
}
