using System.Globalization;

namespace OpenSim.App.Controls.Spreadsheet;

/// <summary>
/// The content of one cell: a number or a piece of text. Choice and text cells hold text;
/// number and integer cells hold a double (an integer cell's is whole).
/// </summary>
public readonly record struct CellValue
{
    private readonly string? _text;

    private CellValue(bool isNumber, double number, string? text)
    {
        IsNumber = isNumber;
        Number = number;
        _text = text;
    }

    /// <summary>True when the value is a number.</summary>
    public bool IsNumber { get; }

    /// <summary>The number; NaN when the value is text.</summary>
    public double Number { get; }

    /// <summary>The text; empty when the value is a number.</summary>
    public string Text => _text ?? "";

    /// <summary>The empty text value.</summary>
    public static CellValue Empty => new(false, double.NaN, "");

    /// <summary>A number value.</summary>
    public static CellValue Of(double number) => new(true, number, null);

    /// <summary>A text value.</summary>
    public static CellValue Of(string text) => new(false, double.NaN, text ?? "");

    /// <inheritdoc/>
    public override string ToString() =>
        IsNumber ? Number.ToString(CultureInfo.InvariantCulture) : Text;
}
