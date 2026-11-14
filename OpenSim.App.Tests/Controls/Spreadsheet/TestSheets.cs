using OpenSim.App.Controls.Spreadsheet;

namespace OpenSim.App.Tests.Controls.Spreadsheet;

/// <summary>
/// A small sheet shaped like a lens data editor, for the spreadsheet tests. The names and solve
/// codes are made up for the tests: they exercise the control, not any program's definitions.
/// </summary>
internal static class TestSheets
{
    public static readonly SheetSolveCode Height = new("height", "Ray height", "H") { ParameterNames = new[] { "Height", "Pupil zone" } };
    public static readonly SheetSolveCode Focus = new("focus", "Focus", "F");

    public static readonly SheetColumn Comment = new("comment", "Comment") { Kind = CellKind.Text, Flags = CellFlagSupport.Pickup };
    public static readonly SheetColumn Radius = new("radius", "Radius") { AllowsInfinity = true, Default = CellValue.Of(double.PositiveInfinity), Flags = CellFlagSupport.All, SolveCodes = new[] { Focus } };
    public static readonly SheetColumn Thickness = new("thickness", "Thickness") { Flags = CellFlagSupport.All, SolveCodes = new[] { Height, Focus } };
    public static readonly SheetColumn Glass = new("glass", "Material") { Kind = CellKind.Text, Flags = CellFlagSupport.Pickup };
    public static readonly SheetColumn SemiDiameter = new("semi", "Semi-diameter") { Minimum = 0, Flags = CellFlagSupport.Variable };
    public static readonly SheetColumn Conic = new("conic", "Conic") { Flags = CellFlagSupport.Variable | CellFlagSupport.Pickup };
    public static readonly SheetColumn A4 = new("a4", "4th order") { Flags = CellFlagSupport.Variable };
    public static readonly SheetColumn DecenterX = new("dx", "Decenter X") { Flags = CellFlagSupport.Variable };
    public static readonly SheetColumn Order = new("order", "Order") { Kind = CellKind.Choice, Choices = new[] { "Decenter, tilt", "Tilt, decenter" } };
    public static readonly SheetColumn Power = new("power", "Power") { IsReadOnly = true };
    public static readonly SheetColumn Count = new("count", "Count") { Kind = CellKind.Integer, Minimum = 1, Default = CellValue.Of(1.0) };

    public static readonly SheetColumn Aperture = new("aperture", "Aperture") { Kind = CellKind.Choice, Choices = new[] { "None", "Circular" } };
    public static readonly SheetColumn ApertureRadius = new("apertureRadius", "Aperture radius") { Minimum = 0 };

    /// <summary>Slots: 0 comment, 1 radius, 2 thickness, 3 material, 4 semi-diameter, 5 conic, 6 parameter 1, 7 parameter 2.</summary>
    public static readonly SheetRowType Standard = new("Standard", new[] { Comment, Radius, Thickness, Glass, SemiDiameter, Conic })
    {
        Description = "A sphere or conic.",
        Properties = new[] { Aperture, ApertureRadius },
    };

    public static readonly SheetRowType Asphere = new("Even asphere", new[] { Comment, Radius, Thickness, Glass, SemiDiameter, Conic, A4, Count })
    {
        Properties = new[] { Aperture },
        DefaultTint = 2,
    };

    /// <summary>Its decenter sits in the radius slot and its order in parameter 1: a type whose
    /// columns are not the standard ones.</summary>
    public static readonly SheetRowType Break = new("Coordinate break", new SheetColumn?[] { Comment, DecenterX, Thickness, null, null, null, Order, Power });

    public static SheetSchema Schema() => new("Surface type",
        new[] { "Comment", "Radius", "Thickness", "Material", "Semi-diameter", "Conic", "Par 1", "Par 2" },
        new[] { Standard, Asphere, Break }) { FirstRowNumber = 0 };

    /// <summary>A sheet of <paramref name="rows"/> Standard rows with no undo history.</summary>
    public static SheetDocument Document(int rows)
    {
        var doc = new SheetDocument(Schema());
        if (rows > 0) doc.InsertRows(0, rows);
        doc.History.Clear();
        return doc;
    }

    public const int CommentColumn = 1;
    public const int RadiusColumn = 2;
    public const int ThicknessColumn = 3;
    public const int GlassColumn = 4;
    public const int SemiColumn = 5;
    public const int ConicColumn = 6;
    public const int Par1Column = 7;
    public const int Par2Column = 8;
}
