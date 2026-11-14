using System.Text;

namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// Tab-separated text as spreadsheets put it on the clipboard: one record per line, fields split
/// by tabs, and a field holding a tab, a line break or a double quote enclosed in double
/// quotes with each quote inside doubled. These are the quoting rules of RFC 4180 section 2
/// (rules 5 to 7) with the tab in place of the comma.
/// <para>
/// Reading is lenient where clipboard text varies: CRLF, LF and a lone CR all end a record, a
/// final line break does not add an empty record, short records are kept short (the block pads
/// them), and a quote that does not open a field is an ordinary character.
/// </para>
/// </summary>
public static class TabularText
{
    /// <summary>The records and fields of <paramref name="text"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = new List<IReadOnlyList<string>>();
        if (text.Length == 0) return records;

        var record = new List<string>();
        var field = new StringBuilder();
        int i = 0;
        bool fieldStart = true;
        while (i < text.Length)
        {
            char ch = text[i];
            if (fieldStart && ch == '"')
            {
                // A quoted field: up to the quote not followed by another quote.
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    field.Append(text[i++]);
                }
                fieldStart = false;
                continue;
            }
            fieldStart = false;
            if (ch == '\t')
            {
                record.Add(field.ToString());
                field.Clear();
                fieldStart = true;
                i++;
            }
            else if (ch == '\r' || ch == '\n')
            {
                record.Add(field.ToString());
                field.Clear();
                records.Add(record);
                record = new List<string>();
                fieldStart = true;
                i += ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            }
            else
            {
                field.Append(ch);
                i++;
            }
        }
        // The last record, unless the text ended with its line break.
        if (!fieldStart || record.Count > 0) { record.Add(field.ToString()); records.Add(record); }
        return records;
    }

    /// <summary>The text of <paramref name="records"/>: CRLF between records, no final line break.</summary>
    public static string Format(IEnumerable<IReadOnlyList<string>> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var text = new StringBuilder();
        bool firstRecord = true;
        foreach (var record in records)
        {
            if (!firstRecord) text.Append("\r\n");
            firstRecord = false;
            for (int c = 0; c < record.Count; c++)
            {
                if (c > 0) text.Append('\t');
                text.Append(Quote(record[c]));
            }
        }
        return text.ToString();
    }

    private static string Quote(string field)
    {
        bool needs = field.AsSpan().IndexOfAny("\t\r\n\"") >= 0;
        return needs ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
