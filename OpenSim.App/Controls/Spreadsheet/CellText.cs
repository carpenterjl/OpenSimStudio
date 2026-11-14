using System.Globalization;

namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// Text to cell value and back, for one column. Numbers are read and written in the invariant
/// culture (a point for the decimal separator), as everywhere else in the application, so a
/// prescription copied on one machine pastes the same on another.
/// </summary>
public static class CellText
{
    /// <summary>The text for an infinite number: what the invariant culture writes and reads.</summary>
    public const string Infinity = "Infinity";

    /// <summary>The text that edits and copies <paramref name="value"/>: the shortest form that
    /// reads back to the same double, so a copy and paste is exact.</summary>
    public static string Format(SheetColumn column, CellValue value)
    {
        if (!value.IsNumber) return value.Text;
        double x = value.Number;
        if (double.IsPositiveInfinity(x)) return Infinity;
        if (double.IsNegativeInfinity(x)) return "-" + Infinity;
        return column.Kind == CellKind.Integer
            ? ((long)x).ToString(CultureInfo.InvariantCulture)
            : x.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>The text a cell shows: numbers in the column's <see cref="SheetColumn.NumberFormat"/>.</summary>
    public static string Display(SheetColumn column, CellValue value)
    {
        if (!value.IsNumber) return value.Text;
        double x = value.Number;
        if (double.IsInfinity(x) || column.Kind == CellKind.Integer) return Format(column, value);
        return x.ToString(column.NumberFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads <paramref name="text"/> as a value of <paramref name="column"/>. Empty text gives the
    /// column's default. On failure <paramref name="error"/> says what was wrong, without the row
    /// and column, which the caller adds.
    /// </summary>
    public static bool TryParse(SheetColumn column, string text, out CellValue value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(column);
        text ??= "";
        string trimmed = text.Trim();
        value = default;
        error = null;
        if (trimmed.Length == 0)
        {
            value = column.DefaultValue;
            return true;
        }

        switch (column.Kind)
        {
            case CellKind.Text:
                value = CellValue.Of(text);
                return true;

            case CellKind.Choice:
                string? match = column.Choices.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    error = $"'{trimmed}' is not one of {string.Join(", ", column.Choices)}.";
                    return false;
                }
                value = CellValue.Of(match);
                return true;

            case CellKind.Integer:
                if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
                {
                    error = $"'{trimmed}' is not a whole number.";
                    return false;
                }
                value = CellValue.Of((double)whole);
                break;

            default:
                if (!TryParseNumber(trimmed, out double x))
                {
                    error = $"'{trimmed}' is not a number.";
                    return false;
                }
                value = CellValue.Of(x);
                break;
        }

        error = Validate(column, value);
        return error is null;
    }

    /// <summary>Why <paramref name="value"/> is not valid in <paramref name="column"/>, or null
    /// when it is: the kind must match, a choice must be listed, a number must be finite (or an
    /// allowed infinity) and inside the limits.</summary>
    public static string? Validate(SheetColumn column, CellValue value)
    {
        if (column.IsNumeric != value.IsNumber)
            return column.IsNumeric ? $"'{value.Text}' is not a number." : "A number was given where text is expected.";
        if (column.Kind == CellKind.Choice && !column.Choices.Contains(value.Text, StringComparer.Ordinal))
            return $"'{value.Text}' is not one of {string.Join(", ", column.Choices)}.";
        if (!value.IsNumber) return null;

        double x = value.Number;
        if (double.IsNaN(x)) return "NaN is not a value.";
        if (double.IsInfinity(x) && !column.AllowsInfinity) return "Infinity is not allowed here.";
        if (column.Kind == CellKind.Integer && (double.IsInfinity(x) || x != Math.Floor(x))) return $"{x.ToString(CultureInfo.InvariantCulture)} is not a whole number.";
        if (column.Minimum is { } min && x < min) return $"{Format(column, value)} is below the minimum {min.ToString(CultureInfo.InvariantCulture)}.";
        if (column.Maximum is { } max && x > max) return $"{Format(column, value)} is above the maximum {max.ToString(CultureInfo.InvariantCulture)}.";
        return null;
    }

    /// <summary>Reads a number in the invariant culture, accepting "inf", "infinity" and "∞" with
    /// an optional sign in any case (a flat radius is commonly typed that way) and rejecting NaN.</summary>
    public static bool TryParseNumber(string text, out double value)
    {
        string t = text.Trim();
        string body = t.TrimStart('+', '-');
        bool negative = t.StartsWith('-');
        if (body.Equals("inf", StringComparison.OrdinalIgnoreCase)
            || body.Equals(Infinity, StringComparison.OrdinalIgnoreCase)
            || body == "∞")
        {
            if (t.Length - body.Length > 1) { value = 0; return false; }   // "+-inf"
            value = negative ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value))
            return true;
        value = 0;
        return false;
    }
}
