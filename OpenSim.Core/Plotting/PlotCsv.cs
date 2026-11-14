using System.Globalization;
using System.Text;
using OpenSim.Core.Geometry2D;

namespace OpenSim.Core.Plotting;

/// <summary>One plotted point as read back from CSV: the series it belongs to and its two values.</summary>
public readonly record struct CsvPoint(string Series, double X, double Y);

/// <summary>
/// The text export of every plot, and its reader. Records follow RFC 4180 ("Common Format and
/// MIME Type for Comma-Separated Values (CSV) Files", IETF, 2005, section 2): CRLF line ends, a
/// header record, and a field holding a comma, a double quote or a line break enclosed in double
/// quotes with inner quotes doubled. Two additions the RFC does not define, both skipped by the
/// reader: lines starting with '#' carry metadata (units, levels, panel titles), and blank lines
/// separate sections. Numbers are written in the invariant culture with the shortest
/// round-tripping form, so reading the export gives back the plotted doubles bit for bit
/// (NaN and the infinities included).
/// </summary>
public static class PlotCsv
{
    private const string Eol = "\r\n";

    /// <summary>A number as written to CSV: shortest round-trip form, invariant culture.</summary>
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A field, quoted when RFC 4180 requires it.</summary>
    public static string Field(string text)
    {
        if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 && !text.StartsWith('#')) return text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>A table: optional '#' comment lines, the header, then the rows.</summary>
    public static string Table(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows,
        IEnumerable<string>? comments = null)
    {
        var text = new StringBuilder();
        if (comments is not null)
            foreach (string comment in comments)
                text.Append("# ").Append(comment.Replace("\r", " ").Replace("\n", " ")).Append(Eol);
        text.Append(string.Join(",", header.Select(Field))).Append(Eol);
        foreach (var row in rows) text.Append(string.Join(",", row.Select(Field))).Append(Eol);
        return text.ToString();
    }

    /// <summary>Points of named series in long form: one row per point, "series,x,y".</summary>
    public static string Points(string xName, string yName, IEnumerable<(string Series, IReadOnlyList<Point2> Points)> series,
        IEnumerable<string>? comments = null) =>
        Table(new[] { "series", xName, yName },
            series.SelectMany(s => s.Points.Select(p => (IReadOnlyList<string>)new[] { s.Series, Number(p.X), Number(p.Y) })),
            comments);

    /// <summary>
    /// A grid as a matrix: the header holds the column x values after a corner cell, each row
    /// starts with its y value; rows run in ascending y, as the field stores them.
    /// </summary>
    public static string Grid(GridField field, string corner = "y\\x", IEnumerable<string>? comments = null)
    {
        var header = new List<string> { corner };
        for (int c = 0; c < field.Columns; c++) header.Add(Number(field.XAt(c)));
        var rows = new List<IReadOnlyList<string>>(field.Rows);
        for (int r = 0; r < field.Rows; r++)
        {
            var row = new string[field.Columns + 1];
            row[0] = Number(field.YAt(r));
            for (int c = 0; c < field.Columns; c++) row[c + 1] = Number(field.Values[r, c]);
            rows.Add(row);
        }
        return Table(header, rows, comments);
    }

    /// <summary>Joins sections, each led by a "# name" comment and separated by a blank line (a
    /// multi-panel figure's one-file export).</summary>
    public static string Sections(IEnumerable<(string Name, string Csv)> sections) =>
        string.Join(Eol, sections.Select(s => "# " + s.Name.Replace("\r", " ").Replace("\n", " ") + Eol + s.Csv));

    /// <summary>Splits <see cref="Sections"/> output back into named sections: a section starts at
    /// a comment line after a blank line (or at the top), and its name is that comment.</summary>
    public static IReadOnlyList<(string Name, string Csv)> SplitSections(string text)
    {
        var result = new List<(string, string)>();
        string? name = null;
        var body = new StringBuilder();
        bool atStart = true;
        foreach (string line in SplitLines(text))
        {
            if (line.Length == 0)
            {
                if (name is not null) result.Add((name, body.ToString()));
                name = null; body.Clear(); atStart = true;
                continue;
            }
            if (atStart && line.StartsWith('#'))
            {
                name = line[1..].Trim();
                atStart = false;
                continue;
            }
            atStart = false;
            name ??= "";
            body.Append(line).Append(Eol);
        }
        if (name is not null) result.Add((name, body.ToString()));
        return result;
    }

    /// <summary>
    /// Reads a table: comment and blank lines skipped, the first record is the header. Quoted
    /// fields may hold commas, doubled quotes and line breaks. Fails naming the line when a quote
    /// is left open.
    /// </summary>
    public static (IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) ReadTable(string text)
    {
        var records = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        int i = 0, line = 1;
        bool lineStart = true;
        while (i < text.Length)
        {
            char ch = text[i];
            if (lineStart && (ch == '#' || ch == '\r' || ch == '\n'))
            {
                // A comment or a blank line: skip to the end of it.
                while (i < text.Length && text[i] != '\n') i++;
                i++; line++;
                continue;
            }
            lineStart = false;
            if (ch == '"' && field.Length == 0)
            {
                int open = line;
                i++;
                while (true)
                {
                    if (i >= text.Length)
                        throw new FormatException($"CSV line {open}: a quoted field is never closed.");
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    if (text[i] == '\n') line++;
                    field.Append(text[i++]);
                }
                continue;
            }
            if (ch == ',') { fields.Add(field.ToString()); field.Clear(); i++; continue; }
            if (ch == '\r' || ch == '\n')
            {
                fields.Add(field.ToString()); field.Clear();
                records.Add(fields.ToArray()); fields.Clear();
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                i++; line++;
                lineStart = true;
                continue;
            }
            field.Append(ch);
            i++;
        }
        if (!lineStart) { fields.Add(field.ToString()); records.Add(fields.ToArray()); }
        if (records.Count == 0) throw new FormatException("CSV text has no header record.");
        return (records[0], records.Skip(1).ToArray());
    }

    /// <summary>Reads <see cref="Points"/> output back.</summary>
    public static IReadOnlyList<CsvPoint> ReadPoints(string text)
    {
        var (header, rows) = ReadTable(text);
        if (header.Count < 3) throw new FormatException($"A point table needs 3 columns (series, x, y), got {header.Count}.");
        return rows.Select((row, k) =>
        {
            if (row.Count < 3) throw new FormatException($"Point row {k + 1} has {row.Count} fields, expected 3.");
            return new CsvPoint(row[0], Parse(row[1], k), Parse(row[2], k));
        }).ToArray();
    }

    /// <summary>Reads <see cref="Grid"/> output back into a field (the extent from the first and
    /// last x and y; the samples exactly as written).</summary>
    public static GridField ReadGrid(string text)
    {
        var (header, rows) = ReadTable(text);
        int columns = header.Count - 1;
        if (columns < 2 || rows.Count < 2)
            throw new FormatException($"A grid table needs at least 2 x 2 samples, got {rows.Count} x {columns}.");
        var values = new double[rows.Count, columns];
        for (int r = 0; r < rows.Count; r++)
        {
            if (rows[r].Count != columns + 1)
                throw new FormatException($"Grid row {r + 1} has {rows[r].Count} fields, expected {columns + 1}.");
            for (int c = 0; c < columns; c++) values[r, c] = Parse(rows[r][c + 1], r);
        }
        return new GridField(values, Parse(header[1], 0), Parse(header[^1], 0), Parse(rows[0][0], 0), Parse(rows[^1][0], rows.Count - 1));
    }

    /// <summary>Parses a number written by <see cref="Number"/>; fails naming the row.</summary>
    public static double Parse(string text, int row)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return value;
        throw new FormatException($"CSV row {row + 1}: '{text}' is not a number.");
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            yield return text[start..end];
            start = i + 1;
        }
        if (start < text.Length) yield return text[start..];
    }
}
