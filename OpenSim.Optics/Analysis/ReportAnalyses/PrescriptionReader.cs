// OP-44
using System.Text.RegularExpressions;
using OpenSim.Core.Model;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>The surfaces a prescription report reads back to, in its lens unit.</summary>
public sealed record PrescriptionReadBack(LensUnit Unit, List<OpticsSurfaceSettings> Surfaces);

/// <summary>
/// Reads the plain-text Prescription Data report (<see cref="PrescriptionDataAnalysis"/>, the
/// analysis window's Text tab, <see cref="OpticsAnalysisResult.Text"/>) back into saved surfaces
/// (docs/Optics-Conventions.md §29): the Surface data table gives each surface's type, radius,
/// thickness, material, semi-diameter, conic, comment and the stop (row STO); Surface detail its
/// parameters, coordinate return, mechanical semi-diameter, chip zone, TCE, coating and the
/// ignore and skip flags; Tilts and decenters and Apertures the rest. Converting the result
/// (<see cref="SavedSurfaces.ToLens"/>) with the same catalogs gives the lens the report was made
/// from. Anything that does not read is a <see cref="FormatException"/> naming the line.
/// <para>
/// Only this program's own report text is read; it is not a reader of OpticStudio's
/// prescription files.
/// </para>
/// </summary>
public static class PrescriptionReader
{
    private const string Separator = " | ";

    public static PrescriptionReadBack Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Replace("\r", "").Split('\n');

        var (dataHeaders, dataRows) = Table(lines, PrescriptionDataAnalysis.SurfaceDataHeading, required: true);
        var unit = UnitOf(dataHeaders[2], dataRows.Count > 0 ? dataRows[0].Line : 0);
        int count = dataRows.Count;
        if (count < 3) throw new FormatException($"Prescription read-back: the {PrescriptionDataAnalysis.SurfaceDataHeading} table has {count} rows; a lens has at least the object, one surface and the image.");
        var surfaces = new OpticsSurfaceSettings[count];
        var labels = new string[count];
        for (int j = 0; j < count; j++)
        {
            var (lineNo, c) = dataRows[j];
            string label = c[0].Trim();
            string expected = j == 0 ? "OBJ" : j == count - 1 ? "IMA" : j.ToString(ReportText.Ci);
            bool stop = label == "STO" && j > 0 && j < count - 1;
            if (label != expected && !stop)
                throw new FormatException($"Prescription read-back, line {lineNo}: row {j} is labelled '{label}'; expected '{expected}'{(j > 0 && j < count - 1 ? " or 'STO'" : "")}.");
            labels[j] = label;
            string type = c[1].Trim();
            string radius = c[2].Trim();
            string thickness = c[3].Trim();
            string semi = c[5].Trim();
            var s = new OpticsSurfaceSettings
            {
                Type = type == SavedSurfaces.StandardType ? null : type,
                Radius = radius == "Infinity" ? null : Number(radius, lineNo, "radius"),
                ThicknessIsInfinite = thickness == "Infinity",
                Thickness = thickness == "Infinity" ? 0 : Number(thickness, lineNo, "thickness"),
                SemiDiameter = semi.StartsWith("auto", StringComparison.Ordinal) ? null : Number(semi, lineNo, "semi-diameter"),
                Conic = Number(c[6], lineNo, "conic"),
                IsStop = stop,
            };
            string comment = ReportText.Unquote(c[7])
                ?? throw new FormatException($"Prescription read-back, line {lineNo}: the comment is not a quoted string.");
            s = s with { Comment = comment.Length == 0 ? null : comment };
            string material = c[4].Trim();
            s = SavedSurfaces.ParseMaterial(s, material == "—" ? "" : material, out string? error)
                ?? throw new FormatException($"Prescription read-back, line {lineNo}: {error}");
            surfaces[j] = s;
        }

        int Row(string label, int lineNo)
        {
            int index = Array.IndexOf(labels, label.Trim());
            if (index < 0) throw new FormatException($"Prescription read-back, line {lineNo}: no surface is labelled '{label.Trim()}'.");
            return index;
        }

        // Surface detail.
        var (_, detailRows) = Table(lines, PrescriptionDataAnalysis.SurfaceDetailHeading, required: false);
        var parameterNumber = new Regex(@"^Parameter (\d+)\b", RegexOptions.CultureInvariant);
        var returnPattern = new Regex(@"^(\w+) to surface (\d+)$", RegexOptions.CultureInvariant);
        foreach (var (lineNo, c) in detailRows)
        {
            int j = Row(c[0], lineNo);
            var s = surfaces[j];
            string item = c[1].Trim(), value = c[2];
            if (parameterNumber.Match(item) is { Success: true } m)
            {
                int n = int.Parse(m.Groups[1].Value, ReportText.Ci);
                var entry = new OpticsSurfaceParameterSettings { Number = n, Value = Number(value, lineNo, item) };
                if (n >= SurfaceTypes.FirstExtraData && SurfaceTypes.Find(s.Type) is not null)
                    s = s with { ExtraData = Append(s.ExtraData, entry) };
                else
                    s = s with { Parameters = Append(s.Parameters, entry) };
            }
            else switch (item)
            {
                case "Coordinate return":
                    var r = returnPattern.Match(value.Trim());
                    if (!r.Success) throw new FormatException($"Prescription read-back, line {lineNo}: the coordinate return '{value.Trim()}' is not 'Mode to surface n'.");
                    s = s with { CoordinateReturn = new OpticsCoordinateReturnSettings { Mode = r.Groups[1].Value, Surface = int.Parse(r.Groups[2].Value, ReportText.Ci) } };
                    break;
                case "Mechanical semi-diameter": s = s with { MechanicalSemiDiameter = Number(value, lineNo, item) }; break;
                case "Chip zone": s = s with { ChipZone = Number(value, lineNo, item) }; break;
                case "TCE": s = s with { Tce = Number(value, lineNo, item) }; break;
                case "Ignored": s = s with { IsIgnored = Yes(value, lineNo, item) }; break;
                case "Skip rays to": s = s with { SkipRaysTo = Yes(value, lineNo, item) }; break;
                case "Coating": s = s with { Coating = value }; break;
                default: throw new FormatException($"Prescription read-back, line {lineNo}: the detail item '{item}' is not one this version writes.");
            }
            surfaces[j] = s;
        }

        // Tilts and decenters.
        var (_, tiltRows) = Table(lines, PrescriptionDataAnalysis.TiltsHeading, required: false);
        foreach (var (lineNo, c) in tiltRows)
        {
            int j = Row(c[0], lineNo);
            string where = c[1].Trim(), mode = c[2].Trim();
            bool explicitValues = mode == nameof(TiltAfterKind.Explicit);
            double V(int i, string what) => explicitValues ? Number(c[i], lineNo, what) : 0;
            string order = c[8].Trim();
            var t = new OpticsTiltDecenterSettings
            {
                Mode = explicitValues ? null : mode,
                DecenterX = V(3, "decenter X"), DecenterY = V(4, "decenter Y"),
                TiltX = V(5, "tilt X"), TiltY = V(6, "tilt Y"), TiltZ = V(7, "tilt Z"),
                Order = !explicitValues || order == nameof(TiltDecenterOrder.DecenterThenTilt) ? null : order,
                Surface = c[9].Trim() == "—" ? 0 : (int)Number(c[9], lineNo, "pick-up surface"),
            };
            surfaces[j] = where switch
            {
                "before" when explicitValues => surfaces[j] with { TiltBefore = t },
                "after" => surfaces[j] with { TiltAfter = t },
                _ => throw new FormatException($"Prescription read-back, line {lineNo}: '{where}' with mode '{mode}' is not a tilt this version writes."),
            };
        }

        // Apertures.
        var (_, apertureRows) = Table(lines, PrescriptionDataAnalysis.AperturesHeading, required: false);
        foreach (var (lineNo, c) in apertureRows)
        {
            int j = Row(c[0], lineNo);
            string vertices = c[11].Trim();
            List<OpticsPointSettings>? points = null;
            if (vertices != "—")
            {
                points = new List<OpticsPointSettings>();
                foreach (var pair in vertices.Split(';', StringSplitOptions.TrimEntries))
                {
                    var xy = pair.Split(',');
                    if (xy.Length != 2) throw new FormatException($"Prescription read-back, line {lineNo}: the vertex '{pair}' is not 'x,y'.");
                    points.Add(new OpticsPointSettings { X = Number(xy[0], lineNo, "vertex x"), Y = Number(xy[1], lineNo, "vertex y") });
                }
            }
            surfaces[j] = surfaces[j] with
            {
                Aperture = new OpticsSurfaceApertureSettings
                {
                    Type = c[1].Trim() == "—" ? null : c[1].Trim(),
                    IsObscuration = Yes(c[2], lineNo, "obscuration"),
                    DecenterX = Number(c[3], lineNo, "decenter X"), DecenterY = Number(c[4], lineNo, "decenter Y"),
                    MinRadius = Number(c[5], lineNo, "min radius"), MaxRadius = Number(c[6], lineNo, "max radius"),
                    HalfWidthX = Number(c[7], lineNo, "half-width X"), HalfWidthY = Number(c[8], lineNo, "half-width Y"),
                    ArmWidth = Number(c[9], lineNo, "arm width"),
                    ArmCount = (int)Number(c[10], lineNo, "arm count"),
                    Vertices = points,
                }
            };
        }
        return new PrescriptionReadBack(unit, surfaces.ToList());
    }

    private static List<OpticsSurfaceParameterSettings> Append(List<OpticsSurfaceParameterSettings>? list, OpticsSurfaceParameterSettings entry)
    {
        var copy = list is null ? new List<OpticsSurfaceParameterSettings>() : new List<OpticsSurfaceParameterSettings>(list);
        copy.Add(entry);
        return copy;
    }

    private static double Number(string text, int lineNo, string what) =>
        ReportText.TryReadExact(text, out double v) ? v
            : throw new FormatException($"Prescription read-back, line {lineNo}: the {what} '{text.Trim()}' is not a number.");

    private static bool Yes(string text, int lineNo, string what) => text.Trim() switch
    {
        "yes" => true,
        "no" => false,
        var other => throw new FormatException($"Prescription read-back, line {lineNo}: the {what} '{other}' is neither 'yes' nor 'no'."),
    };

    /// <summary>The lens unit named in a header such as "Radius (mm)".</summary>
    private static LensUnit UnitOf(string header, int lineNo)
    {
        int open = header.LastIndexOf('('), close = header.LastIndexOf(')');
        string symbol = open >= 0 && close > open ? header[(open + 1)..close] : "";
        foreach (var unit in ConventionNames.LensUnits.All)
            if (ConventionNames.Symbol(unit) == symbol) return unit;
        throw new FormatException($"Prescription read-back, line {lineNo}: the header '{header}' does not name a lens unit (mm, cm, in or m).");
    }

    /// <summary>
    /// The table under the unindented heading line <paramref name="heading"/>: its header cells
    /// and its rows with their 1-based line numbers. A row is split into as many cells as the
    /// header has, the last cell taking the rest of the line (so a comment or coating may hold
    /// the separator). The table ends at the first line that is not indented or has too few cells.
    /// </summary>
    private static (string[] Headers, List<(int Line, string[] Cells)> Rows) Table(string[] lines, string heading, bool required)
    {
        int at = Array.IndexOf(lines, heading);
        if (at < 0)
        {
            if (required) throw new FormatException($"Prescription read-back: there is no '{heading}' section; this is not a prescription report of this program.");
            return (Array.Empty<string>(), new List<(int, string[])>());
        }
        if (at + 1 >= lines.Length || !lines[at + 1].StartsWith("  ", StringComparison.Ordinal))
            throw new FormatException($"Prescription read-back, line {at + 2}: the '{heading}' section has no table header.");
        var headers = lines[at + 1][2..].Split(Separator);
        var rows = new List<(int, string[])>();
        for (int i = at + 2; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!line.StartsWith("  ", StringComparison.Ordinal)) break;
            var cells = line[2..].Split(Separator, headers.Length);
            if (cells.Length < headers.Length) break;
            rows.Add((i + 1, cells));
        }
        return (headers, rows);
    }
}
