using System.Globalization;
using System.Text;

namespace OpenSim.Pcb.Rules;

/// <summary>One row of a spacing table: the spacing per column for working voltages up
/// to <see cref="MaxVolts"/>.</summary>
public sealed record SpacingRow(double MaxVolts, IReadOnlyList<double> SpacingMeters);

/// <summary>
/// A table of minimum conductor spacing against working voltage, with one column per
/// situation (an internal layer, an uncoated outer layer, a coated one …). The program
/// ships the IPC-2221 conductor-spacing table (<see cref="Ipc2221Spacing"/>) and reads any
/// other — an IEC 60664-1 creepage table by pollution degree and material group, a
/// company rule — from CSV (<see cref="ParseCsv"/>), so the number a board is held to is
/// always one the user can see and replace.
/// </summary>
public sealed record SpacingTable
{
    public required string Name { get; init; }

    /// <summary>Where the numbers come from, printed with every report.</summary>
    public required string Source { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>Rows in ascending <see cref="SpacingRow.MaxVolts"/>; the first row covers
    /// everything up to its limit.</summary>
    public required IReadOnlyList<SpacingRow> Rows { get; init; }

    /// <summary>Spacing added per volt above the last row's limit [m/V], per column. Null:
    /// a voltage beyond the table is refused.</summary>
    public IReadOnlyList<double>? PerVoltAbove { get; init; }

    public double MaxTabulatedVolts => Rows[^1].MaxVolts;

    public int ColumnIndex(string column)
    {
        for (int i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i], column, StringComparison.OrdinalIgnoreCase)) return i;
        throw new ArgumentException($"Spacing table '{Name}' has no column '{column}'; it has {string.Join(", ", Columns)}.");
    }

    /// <summary>The spacing [m] required at a working voltage in a column.</summary>
    public double Required(double volts, string column) => Required(volts, ColumnIndex(column));

    public double Required(double volts, int column)
    {
        if (!(volts >= 0)) throw new ArgumentOutOfRangeException(nameof(volts), "The working voltage must be non-negative.");
        if (column < 0 || column >= Columns.Count) throw new ArgumentOutOfRangeException(nameof(column));
        foreach (var row in Rows)
            if (volts <= row.MaxVolts) return row.SpacingMeters[column];
        if (PerVoltAbove is null)
            throw new InvalidOperationException(
                $"{volts:g4} V is above the {MaxTabulatedVolts:g4} V the table '{Name}' covers, and the table " +
                "gives no rule beyond it.");
        var last = Rows[^1];
        return last.SpacingMeters[column] + PerVoltAbove[column] * (volts - last.MaxVolts);
    }

    public void Validate()
    {
        if (Columns.Count == 0) throw new InvalidOperationException($"Spacing table '{Name}' has no columns.");
        if (Rows.Count == 0) throw new InvalidOperationException($"Spacing table '{Name}' has no rows.");
        double previous = double.NegativeInfinity;
        foreach (var row in Rows)
        {
            if (!(row.MaxVolts > previous))
                throw new InvalidOperationException($"Spacing table '{Name}': rows must have strictly increasing voltage limits.");
            previous = row.MaxVolts;
            if (row.SpacingMeters.Count != Columns.Count)
                throw new InvalidOperationException(
                    $"Spacing table '{Name}': the row up to {row.MaxVolts:g4} V has {row.SpacingMeters.Count} values for {Columns.Count} columns.");
            if (row.SpacingMeters.Any(s => !(s >= 0)))
                throw new InvalidOperationException($"Spacing table '{Name}': spacings must be non-negative.");
        }
        if (PerVoltAbove is not null && PerVoltAbove.Count != Columns.Count)
            throw new InvalidOperationException($"Spacing table '{Name}': the per-volt row has {PerVoltAbove.Count} values for {Columns.Count} columns.");
    }

    /// <summary>
    /// Reads a table from CSV. First line: <c>Max volts,&lt;column&gt;,&lt;column&gt;,…</c>;
    /// then one line per row with the voltage limit and the spacing in each column in
    /// millimetres; an optional last line <c>per volt above,&lt;mm/V&gt;,…</c> extends the
    /// table linearly beyond its last row. Lines starting with '#' are comments.
    /// </summary>
    public static SpacingTable ParseCsv(string text, string name, string source)
    {
        var ci = CultureInfo.InvariantCulture;
        var lines = text.Split('\n').Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (lines.Count < 2) throw new InvalidDataException("A spacing table needs a header line and at least one row.");
        var header = lines[0].Split(',').Select(s => s.Trim()).ToList();
        if (header.Count < 2) throw new InvalidDataException("The header needs the voltage column and at least one spacing column.");
        var columns = header.Skip(1).ToList();
        var rows = new List<SpacingRow>();
        IReadOnlyList<double>? perVolt = null;
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',').Select(s => s.Trim()).ToList();
            if (cells.Count != header.Count)
                throw new InvalidDataException($"Row '{line}' has {cells.Count} cells; the header has {header.Count}.");
            var values = cells.Skip(1).Select(s =>
            {
                if (!double.TryParse(s, NumberStyles.Float, ci, out double v))
                    throw new InvalidDataException($"'{s}' in row '{line}' is not a number.");
                return v * 1e-3;                                   // mm → m
            }).ToList();
            if (cells[0].StartsWith("per volt", StringComparison.OrdinalIgnoreCase) || cells[0].StartsWith('>'))
            {
                perVolt = values;                                  // mm/V → m/V, the same factor
                continue;
            }
            if (!double.TryParse(cells[0], NumberStyles.Float, ci, out double volts))
                throw new InvalidDataException($"'{cells[0]}' is not a voltage limit.");
            rows.Add(new SpacingRow(volts, values));
        }
        var table = new SpacingTable { Name = name, Source = source, Columns = columns, Rows = rows, PerVoltAbove = perVolt };
        table.Validate();
        return table;
    }

    /// <summary>The same CSV form <see cref="ParseCsv"/> reads.</summary>
    public string ToCsv()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# ").Append(Name).Append(" — ").AppendLine(Source);
        sb.Append("Max volts,").AppendLine(string.Join(",", Columns));
        foreach (var row in Rows)
            sb.Append(row.MaxVolts.ToString("g6", ci)).Append(',')
              .AppendLine(string.Join(",", row.SpacingMeters.Select(s => (s * 1e3).ToString("g6", ci))));
        if (PerVoltAbove is not null)
            sb.Append("per volt above,").AppendLine(string.Join(",", PerVoltAbove.Select(s => (s * 1e3).ToString("g6", ci))));
        return sb.ToString();
    }

    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string> { $"{Name} ({Source}); columns: {string.Join(", ", Columns)}; spacing in mm:" };
        foreach (var row in Rows)
            lines.Add($"  ≤ {row.MaxVolts:g4} V: {string.Join(", ", row.SpacingMeters.Select(s => (s * 1e3).ToString("g4", CultureInfo.InvariantCulture)))}");
        if (PerVoltAbove is not null)
            lines.Add($"  above {MaxTabulatedVolts:g4} V add per volt: {string.Join(", ", PerVoltAbove.Select(s => (s * 1e3).ToString("g4", CultureInfo.InvariantCulture)))}");
        return lines;
    }
}

/// <summary>The situations IPC-2221's conductor-spacing table distinguishes.</summary>
public enum Ipc2221Column
{
    /// <summary>B1: internal conductors.</summary>
    B1Internal = 0,
    /// <summary>B2: external conductors, uncoated, sea level to 3050 m.</summary>
    B2ExternalUncoated = 1,
    /// <summary>B3: external conductors, uncoated, over 3050 m.</summary>
    B3ExternalUncoatedHighAltitude = 2,
    /// <summary>B4: external conductors with a permanent polymer coating (any elevation).</summary>
    B4ExternalCoated = 3,
    /// <summary>A5: external conductors with conformal coating over the assembly.</summary>
    A5ConformalCoatedAssembly = 4,
    /// <summary>A6: external component lead or termination, uncoated.</summary>
    A6ComponentLeadUncoated = 5,
    /// <summary>A7: external component lead or termination with conformal coating.</summary>
    A7ComponentLeadCoated = 6
}

/// <summary>
/// The IPC-2221 generic conductor-spacing table (Table 6-1 in IPC-2221B), as it is
/// reproduced in design guides and calculators. The standard's own text was not available
/// when this was written, so the values are the commonly published ones: check them against
/// your copy before a safety decision rests on them, and replace the table with
/// <see cref="SpacingTable.ParseCsv"/> if yours differs. IPC-2221 defines the voltage as the
/// peak voltage between the conductors (DC or AC peak).
/// </summary>
public static class Ipc2221Spacing
{
    public static readonly IReadOnlyList<string> ColumnNames = new[] { "B1", "B2", "B3", "B4", "A5", "A6", "A7" };

    public const string TableSource =
        "IPC-2221B Table 6-1 as commonly reproduced; not checked against the standard's text on this machine — verify before relying on it";

    private static SpacingRow Row(double volts, params double[] mm) =>
        new(volts, mm.Select(v => v * 1e-3).ToList());

    public static SpacingTable Table6_1 { get; } = new()
    {
        Name = "IPC-2221B Table 6-1",
        Source = TableSource,
        Columns = ColumnNames,
        Rows = new[]
        {
            //         B1    B2    B3    B4    A5    A6    A7     [mm]
            Row(15,   0.05, 0.1,  0.1,  0.05, 0.13, 0.13, 0.13),
            Row(30,   0.05, 0.1,  0.1,  0.05, 0.13, 0.25, 0.13),
            Row(50,   0.1,  0.6,  0.6,  0.13, 0.13, 0.4,  0.13),
            Row(100,  0.1,  0.6,  1.5,  0.13, 0.13, 0.5,  0.13),
            Row(150,  0.2,  0.6,  3.2,  0.4,  0.4,  0.8,  0.4),
            Row(170,  0.2,  1.25, 3.2,  0.4,  0.4,  0.8,  0.4),
            Row(250,  0.2,  1.25, 6.4,  0.4,  0.4,  0.8,  0.4),
            Row(300,  0.2,  1.25, 12.5, 0.4,  0.4,  0.8,  0.8),
            Row(500,  0.25, 2.5,  12.5, 0.8,  0.8,  1.5,  0.8)
        },
        // Above 500 V the table adds a spacing per volt [mm/V]; stored in m/V.
        PerVoltAbove = new[] { 0.0025, 0.005, 0.025, 0.00305, 0.00305, 0.00305, 0.00305 }.Select(v => v * 1e-3).ToList()
    };

    public static string ColumnName(Ipc2221Column column) => ColumnNames[(int)column];

    /// <summary>The spacing [m] IPC-2221 asks for at a peak working voltage.</summary>
    public static double Required(double volts, Ipc2221Column column) => Table6_1.Required(volts, (int)column);
}

/// <summary>
/// What an IEC 60664-1 table looks like to the checker. The standard's creepage table is
/// indexed by the RMS working voltage, the pollution degree (1–3) and the material group
/// (I, II, IIIa/IIIb by CTI); its clearance table by the rated impulse withstand voltage,
/// which comes from the overvoltage category and the mains voltage. Neither is shipped —
/// the standard was not available to copy from — so a user types the rows that apply from
/// their copy into a CSV with one column per case and loads it with
/// <see cref="SpacingTable.ParseCsv"/>; <see cref="CreepageHeader"/> is the header such a
/// file would carry.
/// </summary>
public static class Iec60664Spacing
{
    /// <summary>A header for a creepage CSV: one column per pollution degree and material group.</summary>
    public const string CreepageHeader =
        "Max volts,PD1,PD2 group I,PD2 group II,PD2 group III,PD3 group I,PD3 group II,PD3 group III";

    /// <summary>The source text a loaded IEC table should carry.</summary>
    public static string Source(string edition) => $"IEC 60664-1 {edition}, rows copied by the user";
}
