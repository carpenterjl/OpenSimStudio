using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;

namespace OpenSim.Optics.Coatings;

/// <summary>
/// Reads and writes coating catalogs: OpticStudio's coating file (COATING.DAT and others like it)
/// and this program's own JSON catalog file.
/// <para>
/// <b>OpticStudio's layout</b> was read from the OpticStudio 2025 R2 help pages "Coating File Data
/// Syntax", "The MATE / TAPR / COAT Data Section", "Defining Replicated Groups of Coating Layers",
/// "Defining Simple Ideal Coatings", "The IDEAL / IDEAL2 / TABLE Data Section", "Adding Comments to
/// the Coating File" and "Defining Coatings" (ansyshelp.ansys.com, saved under
/// plans/sources/optics/coatingfile/ on 2026-11-14). Text after "!" is a comment. A MATE row is
/// wavelength (µm), n and the extinction coefficient written negative for an absorbing material
/// (OpticStudio's n − ik); the reader stores k = −(written value) and refuses a positive written
/// value (gain) naming the material and line; the writer writes −k. An ENCRYPTED coating cannot be
/// read: it is left out and named in the notes. Names are not truncated to 32 characters.
/// </para>
/// <para>
/// No coating file written by OpticStudio itself was available to test against; the reader is
/// checked against files written from the help's syntax and examples only
/// (docs/Optics-Conventions.md section 10).
/// </para>
/// </summary>
public static class CoatingFile
{
    /// <summary>The extension of this program's own catalog file.</summary>
    public const string JsonExtension = ".oscoat.json";
    private const string JsonFormat = "OpenSim coating catalog";
    private const int JsonVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
    };

    private sealed record JsonCatalogFile
    {
        public string? Format { get; init; }
        public int FormatVersion { get; init; }
        public OpticsCoatingsSettings? Catalog { get; init; }
    }

    // ---------------------------------------------------------------- JSON

    public static string ToJson(CoatingCatalog catalog) => JsonSerializer.Serialize(new JsonCatalogFile
    {
        Format = JsonFormat, FormatVersion = JsonVersion,
        Catalog = CoatingCatalogSettings.ToSettings(catalog) ?? new OpticsCoatingsSettings()
    }, JsonOptions);

    /// <summary>Reads this program's catalog file. Throws <see cref="InvalidDataException"/>,
    /// naming <paramref name="source"/>, when it is not one.</summary>
    public static CoatingCatalog FromJson(string json, ICollection<string> notes, string source = "the coating catalog")
    {
        JsonCatalogFile? file;
        try { file = JsonSerializer.Deserialize<JsonCatalogFile>(json, JsonOptions); }
        catch (JsonException e) { throw new InvalidDataException($"{source} is not a coating catalog file: {e.Message}", e); }
        if (file is null || file.Format != JsonFormat)
            throw new InvalidDataException($"{source} is not a coating catalog file (its format is not '{JsonFormat}').");
        if (file.FormatVersion > JsonVersion)
            notes.Add($"{source} was written by a later version (format {file.FormatVersion}); fields this version does not know were ignored.");
        return CoatingCatalogSettings.FromSettings(file.Catalog, notes);
    }

    // ---------------------------------------------------------------- OpticStudio coating file

    /// <summary>Reads an OpticStudio coating file's text. Throws <see cref="InvalidDataException"/>
    /// naming <paramref name="source"/> and the line when a line cannot be read.</summary>
    public static CoatingCatalog ReadDat(string text, ICollection<string> notes, string source = "the coating file")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(notes);
        var materials = new List<CoatingMaterial>();
        var tapers = new List<CoatingTaper>();
        var coatings = new List<Coating>();

        // The section being read: its kind, name and accumulated data.
        string? kind = null, name = null;
        int startLine = 0;
        var rows = new List<TabulatedIndexPoint>();
        var layers = new List<CoatingLayer>();
        var angles = new List<CoatingTableAngle>();
        List<CoatingTableRow>? angleRows = null;
        double angle = 0;
        double dx = 0, dy = 0, rotation = 0;
        Dictionary<int, double> radial = new(), cosine = new(), poly = new();
        bool encrypted = false;

        void Close()
        {
            switch (kind)
            {
                case "MATE":
                    if (rows.Count == 0) throw Error(startLine, $"material '{name}' has no rows.");
                    materials.Add(new CoatingMaterial { Name = name!, Rows = rows.ToArray() });
                    break;
                case "TAPR":
                    tapers.Add(new CoatingTaper
                    {
                        Name = name!, DecenterX = dx, DecenterY = dy, RotationDegrees = rotation,
                        RadialTerms = radial, CosineTerms = cosine, PolynomialTerms = poly
                    });
                    break;
                case "COAT":
                    if (encrypted) notes.Add($"{source}, line {startLine}: coating '{name}' is encrypted (ENCRYPTED); encrypted coatings cannot be read and it was left out.");
                    else if (IdealCoating.TryParseSimpleName(name!, out double t) && layers.Count == 0)
                        coatings.Add(new IdealCoating { Name = name!, Transmission = t, Reflection = 1 - t, IsSimple = true });
                    else if (layers.Count == 0) throw Error(startLine, $"coating '{name}' has no layers.");
                    else coatings.Add(new LayerCoating { Name = name!, Layers = layers.ToArray() });
                    break;
                case "TABLE":
                    if (angleRows is not null) angles.Add(new CoatingTableAngle(angle, angleRows.ToArray()));
                    if (angles.Count == 0) throw Error(startLine, $"table coating '{name}' has no ANGL block.");
                    coatings.Add(new TableCoating { Name = name!, Angles = angles.ToArray() });
                    break;
            }
            kind = null;
            name = null;
            rows = new();
            layers = new();
            angles = new();
            angleRows = null;
            dx = dy = rotation = 0;
            radial = new();
            cosine = new();
            poly = new();
            encrypted = false;
        }

        InvalidDataException Error(int line, string message) => new($"{source}, line {line}: {message}");

        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int lineNumber = i + 1;
            string line = lines[i];
            int bang = line.IndexOf('!');
            if (bang >= 0) line = line[..bang];
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            string keyword = tokens[0].ToUpperInvariant();

            double Number(int index, string what)
            {
                if (index >= tokens.Length) throw Error(lineNumber, $"{what} is missing.");
                if (!double.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                    throw Error(lineNumber, $"{what} '{tokens[index]}' is not a number.");
                return v;
            }
            double Optional(int index, string what, double fallback) => index < tokens.Length ? Number(index, what) : fallback;
            int Term(int index, int max, string what)
            {
                double v = Number(index, what);
                if (v != Math.Floor(v) || v < 0 || v > max) throw Error(lineNumber, $"{what} must be a whole number in 0–{max} (it is {tokens[index]}).");
                return (int)v;
            }
            string Name()
            {
                if (tokens.Length < 2) throw Error(lineNumber, $"{keyword} needs a name.");
                return tokens[1];
            }

            switch (keyword)
            {
                case "MATE":
                case "TAPR":
                case "COAT":
                case "TABLE":
                    Close();
                    kind = keyword;
                    name = Name();
                    startLine = lineNumber;
                    continue;
                case "IDEAL":
                    Close();
                    coatings.Add(new IdealCoating
                    {
                        Name = Name(), Transmission = Number(2, "The transmitted intensity"), Reflection = Number(3, "The reflected intensity"),
                        TotalInternalReflection = Optional(4, "The total-internal-reflection intensity", 1)
                    });
                    continue;
                case "IDEAL2":
                    Close();
                    coatings.Add(new Ideal2Coating
                    {
                        Name = Name(),
                        SReflection = new Complex(Number(2, "s_rr"), Number(3, "s_ri")),
                        STransmission = new Complex(Number(4, "s_tr"), Number(5, "s_ti")),
                        PReflection = new Complex(Number(6, "p_rr"), Number(7, "p_ri")),
                        PTransmission = new Complex(Number(8, "p_tr"), Number(9, "p_ti")),
                        NoPiFlag = Optional(10, "no_pi_flag", 0) != 0,
                        TotalInternalReflection = Optional(11, "The total-internal-reflection intensity", 1)
                    });
                    continue;
            }

            switch (kind)
            {
                case "MATE":
                {
                    double wl = Number(0, "The wavelength"), n = Number(1, "The real index"), written = Optional(2, "The extinction coefficient", 0);
                    if (written > 0)
                        throw Error(lineNumber,
                            $"material '{name}' has a positive extinction coefficient ({tokens[2]}). OpticStudio writes the index as n − ik with k negative for an absorbing material, so a positive value is gain, which is not modelled. Write it negative.");
                    rows.Add(new TabulatedIndexPoint(wl, n, written == 0 ? 0 : -written));
                    break;
                }
                case "TAPR":
                    switch (keyword)
                    {
                        case "DX": dx = Number(1, "DX"); break;
                        case "DY": dy = Number(1, "DY"); break;
                        case "AN": rotation = Number(1, "AN"); break;
                        case "RT": radial[Term(1, CoatingTaper.MaxRadialTerm, "The RT term number")] = Number(2, "The RT coefficient"); break;
                        case "CT": cosine[Term(1, CoatingTaper.MaxCosineTerm, "The CT term number")] = Number(2, "The CT coefficient"); break;
                        case "PT": poly[Term(1, CoatingTaper.MaxPolynomialTerm, "The PT term number")] = Number(2, "The PT coefficient"); break;
                        default: throw Error(lineNumber, $"'{tokens[0]}' is not a taper keyword (DX, DY, AN, RT, CT, PT).");
                    }
                    break;
                case "COAT":
                    if (keyword == "ENCRYPTED") { encrypted = true; break; }
                    double loop = Optional(3, "The loop index", 0);
                    if (loop != Math.Floor(loop) || loop < 0) throw Error(lineNumber, $"the loop index must be a whole number ≥ 0 (it is {tokens[3]}).");
                    layers.Add(new CoatingLayer
                    {
                        Material = tokens[0], Thickness = Number(1, "The thickness"),
                        IsAbsolute = Optional(2, "is_absolute", 0) != 0, LoopIndex = (int)loop,
                        Taper = tokens.Length > 4 ? tokens[4] : null
                    });
                    break;
                case "TABLE":
                    if (keyword == "ANGL")
                    {
                        if (angleRows is not null) angles.Add(new CoatingTableAngle(angle, angleRows.ToArray()));
                        angle = Number(1, "The angle");
                        angleRows = new();
                    }
                    else if (keyword == "WAVE")
                    {
                        if (angleRows is null) throw Error(lineNumber, "a WAVE line comes before any ANGL line.");
                        angleRows.Add(new CoatingTableRow(Number(1, "The wavelength"), Number(2, "Rs"), Number(3, "Rp"), Number(4, "Ts"), Number(5, "Tp"),
                            Optional(6, "Ars", 0), Optional(7, "Arp", 0), Optional(8, "Ats", 0), Optional(9, "Atp", 0)));
                    }
                    else throw Error(lineNumber, $"'{tokens[0]}' is not a table keyword (ANGL, WAVE).");
                    break;
                default:
                    throw Error(lineNumber, $"'{tokens[0]}' is outside any MATE, TAPR, COAT or TABLE section and is not a keyword.");
            }
        }
        Close();
        return new CoatingCatalog { Materials = materials, Tapers = tapers, Coatings = coatings };
    }

    /// <summary>Writes a catalog in OpticStudio's coating file layout: materials, then tapers,
    /// then coatings, as the help requires. Numbers are written so they read back exactly.</summary>
    public static string WriteDat(CoatingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var sb = new StringBuilder();
        sb.Append("! Coating catalog written by OpenSim Studio. Extinction coefficients are negative for absorbing materials (OpticStudio's n - ik).\n");
        foreach (var m in catalog.Materials)
        {
            sb.Append("MATE ").Append(m.Name).Append('\n');
            foreach (var r in m.Rows)
                sb.Append(N(r.Micrometers)).Append(' ').Append(N(r.N)).Append(' ').Append(N(r.K == 0 ? 0 : -r.K)).Append('\n');
        }
        foreach (var t in catalog.Tapers)
        {
            sb.Append("TAPR ").Append(t.Name).Append('\n');
            if (t.DecenterX != 0) sb.Append("DX ").Append(N(t.DecenterX)).Append('\n');
            if (t.DecenterY != 0) sb.Append("DY ").Append(N(t.DecenterY)).Append('\n');
            if (t.RotationDegrees != 0) sb.Append("AN ").Append(N(t.RotationDegrees)).Append('\n');
            foreach (var (k, v) in t.RadialTerms.OrderBy(x => x.Key)) sb.Append($"RT {k} ").Append(N(v)).Append('\n');
            foreach (var (k, v) in t.CosineTerms.OrderBy(x => x.Key)) sb.Append($"CT {k} ").Append(N(v)).Append('\n');
            foreach (var (k, v) in t.PolynomialTerms.OrderBy(x => x.Key)) sb.Append($"PT {k} ").Append(N(v)).Append('\n');
        }
        foreach (var c in catalog.Coatings)
        {
            switch (c)
            {
                case LayerCoating l:
                    sb.Append("COAT ").Append(l.Name).Append('\n');
                    foreach (var x in l.Layers)
                    {
                        sb.Append(x.Material).Append(' ').Append(N(x.Thickness)).Append(' ').Append(x.IsAbsolute ? 1 : 0)
                          .Append(' ').Append(x.LoopIndex.ToString(CultureInfo.InvariantCulture));
                        if (x.Taper is { Length: > 0 } taper) sb.Append(' ').Append(taper);
                        sb.Append('\n');
                    }
                    break;
                case IdealCoating { IsSimple: true } s:
                    sb.Append("COAT ").Append(s.Name).Append('\n');
                    break;
                case IdealCoating i:
                    sb.Append("IDEAL ").Append(i.Name).Append(' ').Append(N(i.Transmission)).Append(' ').Append(N(i.Reflection))
                      .Append(' ').Append(N(i.TotalInternalReflection)).Append('\n');
                    break;
                case Ideal2Coating i:
                    sb.Append("IDEAL2 ").Append(i.Name);
                    foreach (var z in new[] { i.SReflection, i.STransmission, i.PReflection, i.PTransmission })
                        sb.Append(' ').Append(N(z.Real)).Append(' ').Append(N(z.Imaginary));
                    sb.Append(' ').Append(i.NoPiFlag ? 1 : 0).Append(' ').Append(N(i.TotalInternalReflection)).Append('\n');
                    break;
                case TableCoating t:
                    sb.Append("TABLE ").Append(t.Name).Append('\n');
                    foreach (var a in t.Angles)
                    {
                        sb.Append("ANGL ").Append(N(a.AngleDegrees)).Append('\n');
                        foreach (var r in a.Rows)
                            sb.Append("WAVE ").Append(string.Join(' ', new[] { r.Micrometers, r.Rs, r.Rp, r.Ts, r.Tp, r.PhaseRs, r.PhaseRp, r.PhaseTs, r.PhaseTp }.Select(N))).Append('\n');
                    }
                    break;
                default:
                    throw new NotSupportedException($"Coating '{c.Name}' is of a kind ({c.GetType().Name}) the coating file cannot hold.");
            }
        }
        return sb.ToString();
    }

    /// <summary>A number in the shortest form that reads back to the same double.</summary>
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
