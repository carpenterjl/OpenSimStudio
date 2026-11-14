// OP-44
using System.Globalization;
using System.Text;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Optics.Analysis;

/// <summary>Formatting shared by the report analyses of OP-44 (invariant culture throughout, so
/// a report is the same byte for byte on every machine).</summary>
internal static class ReportText
{
    public static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>The shortest text that reads back to the same double ("R"), "Infinity" for +∞.</summary>
    public static string Exact(double v) =>
        double.IsPositiveInfinity(v) ? "Infinity" : double.IsNegativeInfinity(v) ? "-Infinity" : v.ToString("R", Ci);

    /// <summary>Reads <see cref="Exact"/>'s text back.</summary>
    public static bool TryReadExact(string text, out double value)
    {
        string t = text.Trim();
        if (t == "Infinity") { value = double.PositiveInfinity; return true; }
        if (t == "-Infinity") { value = double.NegativeInfinity; return true; }
        return double.TryParse(t, NumberStyles.Float, Ci, out value);
    }

    /// <summary>A comment as a quoted string: backslash, quote and line breaks escaped, so it
    /// reads back whatever it holds.</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
            sb.Append(ch switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => ch.ToString() });
        return sb.Append('"').ToString();
    }

    /// <summary>Reads <see cref="Quote"/>'s text back, or null when it is not a quoted string.</summary>
    public static string? Unquote(string text)
    {
        string t = text.Trim();
        if (t.Length < 2 || t[0] != '"' || t[^1] != '"') return null;
        var sb = new StringBuilder();
        for (int i = 1; i < t.Length - 1; i++)
        {
            char ch = t[i];
            if (ch != '\\') { sb.Append(ch); continue; }
            if (++i >= t.Length - 1) return null;
            sb.Append(t[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var other => other });
        }
        return sb.ToString();
    }

    /// <summary>OpticStudio's row label: OBJ, STO, IMA or the number.</summary>
    public static string Label(SequentialLens lens, int j) =>
        j == 0 ? "OBJ" : j == lens.ImageIndex ? "IMA" : lens.Surfaces[j].IsStop ? "STO" : j.ToString(Ci);

    /// <summary>A length in the lens unit at report precision.</summary>
    public static string Length(double meters, LensUnit unit, string format = "g10") =>
        double.IsPositiveInfinity(meters) ? "Infinity" : StudyReport.Number(OpticsConventions.FromMeters(meters, unit), format);

    /// <summary>The 0-based wavelength row of a 1-based setting (0 = primary), or an
    /// <see cref="ArgumentException"/> naming the analysis.</summary>
    public static int WavelengthRow(string analysis, int setting, OpticalSystemData system)
    {
        int count = system.Wavelengths.Wavelengths.Count;
        if (setting < 0 || setting > count)
            throw new ArgumentException($"{analysis}: the wavelength row must be 0 (primary) or 1 to {count} (it is {setting}).");
        return setting == 0 ? system.Wavelengths.PrimaryIndex : setting - 1;
    }

    /// <summary>The result of a report run on a project without surfaces.</summary>
    public static OpticsAnalysisResult NoSurfaces(string name) => OpticsAnalysisResult.FromSections(name, new[]
    {
        new ReportSection("No surfaces")
        {
            Lines = new[] { "The system has no sequential surfaces: add them in the Lens Data Editor, so there is something to report." }
        }
    }, new[] { "Nothing is reported." });

    /// <summary>
    /// The medium filling the space after each surface (entry j for j = 0 … image − 1): a mirror,
    /// a coordinate break and an ignored surface keep the medium before them, as the trace does
    /// (<see cref="SequentialSurface.Medium"/>). The image surface's entry repeats the last.
    /// </summary>
    public static ParaxialMedium[] SpaceMedia(SequentialLens lens)
    {
        var media = new ParaxialMedium[lens.Surfaces.Count];
        for (int j = 0; j < media.Length; j++)
        {
            var s = lens.Surfaces[j];
            media[j] = j > 0 && (s.IsMirror || s.IsCoordinateBreak || s.IsIgnored || j == lens.ImageIndex) ? media[j - 1] : s.Medium;
        }
        return media;
    }

    /// <summary>True when a medium is the system's air (a constant index of exactly 1).</summary>
    public static bool IsAir(ParaxialMedium medium) => medium.Material is null && medium.Index == 1.0;

    /// <summary>"N-BK7 (SCHOTT)", "n = 1.5" or "air".</summary>
    public static string Describe(ParaxialMedium medium) => IsAir(medium) ? "air" : medium.Describe();
}
