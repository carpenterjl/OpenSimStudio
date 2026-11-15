using System.Globalization;
using System.Text;

namespace OpenSim.Optics.Optimization.TestPlates;

// OP-17: fabricator test-plate lists (OpticStudio's .TPD layout).

/// <summary>Which test plates of a radius the fabricator has (the TPD "code").</summary>
public enum TestPlateShape
{
    /// <summary>Code −1: a concave plate only.</summary>
    ConcaveOnly = -1,
    /// <summary>Code 0: both a concave and a convex plate.</summary>
    Both = 0,
    /// <summary>Code 1: a convex plate only.</summary>
    ConvexOnly = 1,
}

/// <summary>One line of a test plate list: the part name, the radius of curvature and the plate
/// diameter (both millimeters, whatever the lens unit) and which shapes exist.</summary>
/// <param name="Name">The fabricator's ID or name (no spaces or tabs).</param>
/// <param name="Radius">The radius of curvature, mm, positive.</param>
/// <param name="Diameter">The plate's diameter, mm; 0 means "not given", which skips the diameter test.</param>
/// <param name="Shape">The TPD code.</param>
/// <param name="Line">The 1-based line of the file it came from; 0 for a plate built in code.</param>
public sealed record TestPlate(string Name, double Radius, double Diameter, TestPlateShape Shape, int Line = 0)
{
    /// <summary>A concave plate exists (codes −1 and 0): the "CC" column of the list report.</summary>
    public bool HasConcave => Shape != TestPlateShape.ConvexOnly;

    /// <summary>A convex plate exists (codes 0 and 1): the "CX" column.</summary>
    public bool HasConvex => Shape != TestPlateShape.ConcaveOnly;
}

/// <summary>
/// A fabricator's test plate list in OpticStudio's .TPD text layout (OpticStudio 2025 R2 help,
/// "Test Plate Fitting", the format at the end of the page; saved under
/// plans/sources/optics/op-17-help/): up to 15 header lines starting with "!", then one plate per
/// line, "partname radius diameter code", the four values separated by spaces or tabs, the
/// part name without spaces or tabs, radius and diameter in millimeters in free numeric format,
/// code −1 (concave only), 0 (both) or 1 (convex only); at most 30,000 plates. The header
/// typically names the vendor; "!" is shown as a space when the list is reported.
/// docs/Optics-Conventions.md §48. No list ships with the program: lists come from fabricators.
/// </summary>
public sealed record TestPlateList
{
    /// <summary>The most header lines the layout supports (help: "up to 15 lines of header info").</summary>
    public const int MaxHeaderLines = 15;

    /// <summary>The most plates per file (help: "The maximum number of test plates allowed per file is 30,000").</summary>
    public const int MaxPlates = 30_000;

    /// <summary>The header lines without their leading "!".</summary>
    public IReadOnlyList<string> Header { get; init; } = Array.Empty<string>();

    public required IReadOnlyList<TestPlate> Plates { get; init; }

    /// <summary>Where the list was read from, for reports; null for a list built in code.</summary>
    public string? Source { get; init; }

    /// <summary>Reads a .TPD file; throws <see cref="FormatException"/> naming the file and line
    /// of every problem.</summary>
    public static TestPlateList Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FormatException($"The test plate list '{path}' cannot be read: {e.Message}", e);
        }
        return Parse(text, path);
    }

    /// <summary>Parses the text of a .TPD file; <paramref name="source"/> names it in messages.
    /// Blank lines are skipped. Throws <see cref="FormatException"/> listing every line that does
    /// not follow the layout.</summary>
    public static TestPlateList Parse(string text, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        string where = source is null ? "The test plate list" : $"The test plate list '{source}'";
        var header = new List<string>();
        var plates = new List<TestPlate>();
        var problems = new List<string>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int number = i + 1;
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line[0] == '!')
            {
                if (plates.Count > 0) { problems.Add($"line {number}: a '!' header line after the first plate; the layout puts the header first."); continue; }
                header.Add(line[1..]);
                if (header.Count == MaxHeaderLines + 1) problems.Add($"line {number}: more than {MaxHeaderLines} header lines (the layout supports up to {MaxHeaderLines}).");
                continue;
            }
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) { problems.Add($"line {number}: {parts.Length} values; a plate line is 'partname radius diameter code'."); continue; }
            bool ok = true;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) || !double.IsFinite(radius) || !(radius > 0))
            {
                problems.Add($"line {number} ({parts[0]}): the radius '{parts[1]}' is not a positive number of millimeters.");
                ok = false;
            }
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double diameter) || !double.IsFinite(diameter) || diameter < 0)
            {
                problems.Add($"line {number} ({parts[0]}): the diameter '{parts[2]}' is not a number of millimeters, zero or more.");
                ok = false;
            }
            if (!int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code) || code is < -1 or > 1)
            {
                problems.Add($"line {number} ({parts[0]}): the code '{parts[3]}' is not -1 (concave only), 0 (both) or 1 (convex only).");
                ok = false;
            }
            if (!ok) continue;
            plates.Add(new TestPlate(parts[0], radius, diameter, (TestPlateShape)code, number));
            if (plates.Count == MaxPlates + 1) problems.Add($"line {number}: more than {MaxPlates} plates (the layout allows up to {MaxPlates} per file).");
        }
        if (problems.Count > 0)
            throw new FormatException($"{where} does not follow the TPD layout:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        if (plates.Count == 0)
            throw new FormatException($"{where} has no plates.");
        return new TestPlateList { Header = header, Plates = plates, Source = source };
    }

    /// <summary>The list in the TPD layout: the header lines with "!", then one plate per line,
    /// space separated, numbers round-trip exact. Throws <see cref="ArgumentException"/> for a
    /// list the layout cannot hold (a part name with a space or tab or empty, a radius or
    /// diameter out of range, too many header lines or plates, a header line with a line break).</summary>
    public string Format()
    {
        var problems = Validate();
        if (problems.Count > 0)
            throw new ArgumentException("The test plate list cannot be written in the TPD layout:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        var sb = new StringBuilder();
        foreach (string h in Header) sb.Append('!').Append(h).Append("\r\n");
        foreach (var p in Plates)
            sb.Append(p.Name).Append(' ')
              .Append(p.Radius.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(p.Diameter.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(((int)p.Shape).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Writes <see cref="Format"/> to <paramref name="path"/>.</summary>
    public void Write(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllText(path, Format(), new UTF8Encoding(false));
    }

    /// <summary>What the TPD layout cannot hold, one line each; empty when it can be written.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Header.Count > MaxHeaderLines) problems.Add($"{Header.Count} header lines; the layout supports up to {MaxHeaderLines}.");
        for (int i = 0; i < Header.Count; i++)
            if (Header[i] is null || Header[i].IndexOfAny(new[] { '\r', '\n' }) >= 0) problems.Add($"Header line {i + 1} is missing or holds a line break.");
        if (Plates.Count == 0) problems.Add("There are no plates.");
        if (Plates.Count > MaxPlates) problems.Add($"{Plates.Count} plates; the layout allows up to {MaxPlates}.");
        for (int i = 0; i < Plates.Count; i++)
        {
            var p = Plates[i];
            string label = $"Plate {i + 1} ({p?.Name})";
            if (p is null) { problems.Add($"Plate {i + 1} is missing."); continue; }
            if (string.IsNullOrEmpty(p.Name) || p.Name.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0 || p.Name[0] == '!')
                problems.Add($"{label}: the part name must be non-empty, without spaces or tabs, and not start with '!'.");
            if (!double.IsFinite(p.Radius) || !(p.Radius > 0)) problems.Add($"{label}: the radius must be a positive number of millimeters.");
            if (!double.IsFinite(p.Diameter) || p.Diameter < 0) problems.Add($"{label}: the diameter must be zero or more millimeters.");
            if (!Enum.IsDefined(p.Shape)) problems.Add($"{label}: the code must be -1, 0 or 1.");
        }
        return problems;
    }

    /// <summary>The list as OpticStudio's Test Plate Lists tool shows it (help, "Test Plate Lists
    /// (optimization tools group)": "All units on the report are in mm. The CC and CX columns
    /// indicate the availability of concave and convex test plates"): the header with "!" shown
    /// as a space, then one line per plate.</summary>
    public IReadOnlyList<string> ReportLines()
    {
        var lines = new List<string>();
        if (Source is not null) lines.Add($"Test plate list: {Source}");
        foreach (string h in Header) lines.Add(" " + h);
        lines.Add($"{Plates.Count} plate{(Plates.Count == 1 ? "" : "s")}; units are millimeters.");
        int width = Math.Max(4, Plates.Max(p => p.Name.Length));
        lines.Add($"{"Part".PadRight(width)}  {"Radius",14}  {"Diameter",10}  CC  CX");
        foreach (var p in Plates)
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{p.Name.PadRight(width)}  {p.Radius,14:G10}  {p.Diameter,10:G8}  {(p.HasConcave ? "X" : "-"),2}  {(p.HasConvex ? "X" : "-"),2}"));
        return lines;
    }

    /// <summary>The header with "!" shown as a space, as the fitting report names the vendor.</summary>
    public string Vendor => string.Join(Environment.NewLine, Header.Select(h => " " + h));
}
