using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenSim.Pcb.Thermal;
using OpenSim.Rf.Si;

namespace OpenSim.Rf.Pdn;

/// <summary>What a bill of materials or a placement file says about one part.</summary>
public sealed record BomPart(string RefDes)
{
    /// <summary>The value as the file writes it ("100n", "0.1uF 16V X7R").</summary>
    public string? Value { get; init; }
    public double? Farads { get; init; }
    public double? EsrOhms { get; init; }
    public double? EslHenries { get; init; }

    /// <summary>The manufacturer's part number, for finding the vendor's S-parameter file.</summary>
    public string? PartNumber { get; init; }

    public string? Footprint { get; init; }
}

/// <summary>
/// Capacitor values from a bill of materials or a placement file: C from the value column, ESR
/// and ESL from columns of their own when the file has them, and the vendor's measured model
/// where a folder holds an S-parameter file named after the part number.
/// </summary>
public static class CapacitorBom
{
    private static readonly string[] DesignatorKeys = { "designator", "designators", "reference", "references", "refdes", "ref", "refs", "partreference", "parts" };
    private static readonly string[] ValueKeys = { "value", "val", "comment", "capacitance" };
    private static readonly string[] PartNumberKeys = { "mpn", "manufacturerpartnumber", "mfrpartnumber", "mfgpartnumber", "manufacturerpn", "mfrpn", "mfgpn", "partnumber", "pn" };
    private static readonly string[] FootprintKeys = { "footprint", "package", "case" };

    /// <summary>
    /// Reads a BOM: the header row names the columns (a designator column is required; value,
    /// ESR, ESL, part number and footprint are read when present), commas, semicolons or tabs
    /// separate them, and a cell may hold several designators ("C1, C2", "C1 C2", "C1-C4").
    /// A unit in a header ("ESR (mΩ)", "ESL [nH]") applies to the bare numbers below it.
    /// </summary>
    public static IReadOnlyList<BomPart> Read(string text, out IReadOnlyList<string> notes)
    {
        var log = new List<string>();
        notes = log;
        var parts = new List<BomPart>();
        int designator = -1, value = -1, esr = -1, esl = -1, partNumber = -1, footprint = -1;
        double esrScale = 1, eslScale = 1;
        char separator = ',';
        bool header = false;
        int unread = 0, ungrouped = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;
            if (!header)
            {
                string candidate = line.TrimStart('#', ' ');
                separator = candidate.Contains('\t') ? '\t' : candidate.Contains(';') && !candidate.Contains(',') ? ';' : ',';
                var cells = Split(candidate, separator);
                var names = cells.Select(Key).ToList();
                int Find(string[] keys) => names.FindIndex(n => keys.Contains(n));
                designator = Find(DesignatorKeys);
                if (designator < 0) continue;                       // a title or preamble line
                value = Find(ValueKeys);
                partNumber = Find(PartNumberKeys);
                footprint = Find(FootprintKeys);
                esr = names.FindIndex(n => n.StartsWith("esr", StringComparison.Ordinal));
                esl = names.FindIndex(n => n.StartsWith("esl", StringComparison.Ordinal));
                if (esr >= 0) esrScale = HeaderScale(cells[esr]);
                if (esl >= 0) eslScale = HeaderScale(cells[esl]);
                header = true;
                continue;
            }
            var row = Split(line, separator);
            if (designator >= row.Count) { unread++; continue; }
            string? Cell(int i) => i >= 0 && i < row.Count && row[i].Length > 0 ? row[i] : null;
            string? v = Cell(value);
            var refs = Designators(row[designator]).ToList();
            if (refs.Count == 0) { unread++; continue; }
            if (refs.Count > 1) ungrouped++;
            double? esrOhms = Cell(esr) is { } e ? ParseQuantity(e, "ohm", esrScale) : null;
            double? eslHenries = Cell(esl) is { } l ? ParseQuantity(l, "h", eslScale) : null;
            foreach (string r in refs)
                parts.Add(new BomPart(r)
                {
                    Value = v, Farads = v is null ? null : ParseCapacitance(v),
                    EsrOhms = esrOhms, EslHenries = eslHenries,
                    PartNumber = Cell(partNumber), Footprint = Cell(footprint)
                });
        }
        if (!header)
            throw new InvalidOperationException(
                "No header row was found: the bill of materials needs a column named Designator, Reference or RefDes.");
        if (unread > 0) log.Add($"{unread} row(s) without a designator were skipped.");
        if (ungrouped > 0) log.Add($"{ungrouped} row(s) list several designators; each part takes the row's values.");
        int capacitors = parts.Count(p => p.Farads is not null);
        log.Add($"{parts.Count} part(s) read, {capacitors} with a capacitance"
            + (esr >= 0 ? ", ESR from its column" : "") + (esl >= 0 ? ", ESL from its column" : "") + ".");
        return parts;
    }

    /// <summary>The parts of a placement file, with C read from its value column (KiCad .pos
    /// "Val", or the column <see cref="ComponentPlacement.ReadPlacementFile"/> takes as the value).</summary>
    public static IReadOnlyList<BomPart> FromPlacement(IEnumerable<PlacedPart> parts) =>
        parts.Select(p => new BomPart(p.RefDes) { Value = p.Part, Farads = p.Part is null ? null : ParseCapacitance(p.Part) }).ToList();

    /// <summary>
    /// The capacitance a value string gives: a number with an SI prefix and optional F
    /// ("100n", "100nF", "0.1 µF", "1000pF"), or the prefix as decimal point ("4u7", "2n2"). Anything
    /// after the first such token (voltage, dielectric, tolerance) is ignored. A bare number with no
    /// prefix and no unit says nothing about its scale and gives null.
    /// </summary>
    public static double? ParseCapacitance(string value)
    {
        string s = value.Trim().Replace('µ', 'u').Replace('μ', 'u');
        var infix = Regex.Match(s, @"^(\d+)([pPnNuUm])(\d+)(?![0-9.])");
        if (infix.Success)
            return double.Parse($"{infix.Groups[1].Value}.{infix.Groups[3].Value}", CultureInfo.InvariantCulture)
                   * Prefix(infix.Groups[2].Value[0]);
        var plain = Regex.Match(s, @"^([0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*([pPnNuUm]?)\s*([fF])?(?![a-zA-Z0-9])");
        if (!plain.Success) return null;
        double number = double.Parse(plain.Groups[1].Value, CultureInfo.InvariantCulture);
        bool hasPrefix = plain.Groups[2].Value.Length > 0, hasUnit = plain.Groups[3].Success;
        if (!hasPrefix && !hasUnit) return null;
        return number * (hasPrefix ? Prefix(plain.Groups[2].Value[0]) : 1);
    }

    /// <summary>A quantity in the given unit ("ohm" or "h"): a number, an optional SI prefix and an
    /// optional unit ("30m", "30 mΩ", "30mohm", "0.4nH", "400p"); a bare number is in the column's
    /// unit, <paramref name="scale"/> times the base unit.</summary>
    public static double? ParseQuantity(string text, string unit, double scale = 1)
    {
        string s = text.Trim().Replace('µ', 'u').Replace('μ', 'u').Replace("Ω", "ohm").Replace("Ohm", "ohm").Replace("OHM", "ohm");
        var m = Regex.Match(s, @"^([0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*([pnumkKM]?)\s*(ohm|r|R|h|H)?\s*$");
        if (!m.Success) return null;
        double number = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        string prefix = m.Groups[2].Value;
        if (prefix.Length == 0 && !m.Groups[3].Success) return number * scale;
        return number * (prefix.Length > 0 ? Prefix(prefix[0]) : 1);
    }

    /// <summary>
    /// The vendor's measured model for a part number: a .s1p (one port across the part) or .s2p
    /// (two-port shunt, the usual vendor fixture) in <paramref name="folder"/> whose file name,
    /// letters and digits only, is the part number's. Null when there is none.
    /// </summary>
    public static CapacitorModel? VendorModel(string folder, string partNumber, out string? path)
    {
        path = null;
        if (!Directory.Exists(folder) || string.IsNullOrWhiteSpace(partNumber)) return null;
        string wanted = Normalized(partNumber);
        foreach (string file in Directory.EnumerateFiles(folder))
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".s1p" or ".s2p")) continue;
            if (Normalized(Path.GetFileNameWithoutExtension(file)) != wanted) continue;
            path = file;
            var data = TouchstoneReader.ReadFile(file);
            return CapacitorModel.FromTouchstone(partNumber, data,
                data.Ports == 1 ? CapacitorFixture.OnePort : CapacitorFixture.TwoPortShunt);
        }
        return null;
    }

    private static string Normalized(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static double Prefix(char p) => p switch
    {
        'p' or 'P' => 1e-12,
        'n' or 'N' => 1e-9,
        'u' or 'U' => 1e-6,
        'm' => 1e-3,
        'k' or 'K' => 1e3,
        'M' => 1e6,
        _ => 1
    };

    /// <summary>The scale a header's unit gives bare numbers: "ESR (mΩ)" 1e-3, "ESL [nH]" 1e-9.</summary>
    private static double HeaderScale(string header)
    {
        var m = Regex.Match(header.Replace('µ', 'u').Replace('μ', 'u'), @"[\(\[]\s*([pnum]?)\s*(ohm|Ω|r|h)\s*[\)\]]", RegexOptions.IgnoreCase);
        if (!m.Success || m.Groups[1].Value.Length == 0) return 1;
        return Prefix(m.Groups[1].Value[0]);
    }

    private static string Key(string header)
    {
        int bracket = header.IndexOfAny(new[] { '(', '[' });
        if (bracket >= 0) header = header[..bracket];
        return new string(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>The designators of a cell: separated by commas, semicolons or spaces; "C1-C4"
    /// is C1, C2, C3, C4.</summary>
    private static IEnumerable<string> Designators(string cell)
    {
        foreach (string token in cell.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var range = Regex.Match(token, @"^([A-Za-z]+)(\d+)-([A-Za-z]*)(\d+)$");
            if (range.Success && (range.Groups[3].Value.Length == 0 || range.Groups[3].Value == range.Groups[1].Value))
            {
                int from = int.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
                int to = int.Parse(range.Groups[4].Value, CultureInfo.InvariantCulture);
                if (to >= from && to - from < 10000)
                {
                    for (int k = from; k <= to; k++) yield return range.Groups[1].Value + k.ToString(CultureInfo.InvariantCulture);
                    continue;
                }
            }
            yield return token;
        }
    }

    private static List<string> Split(string line, char separator)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == separator && !quoted) { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }
}
