using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Optics.Sequential;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The Lens Data Editor's sheet (OP-31) on OP-10's spreadsheet control: the columns of
/// OpticStudio's editor (comment, radius, thickness, material, coating, clear semi-diameter,
/// chip zone, mechanical semi-diameter, conic, TCE, then the parameter columns) and the
/// properties flyout (stop, ignore, aperture, tilt/decenter placeholder, drawing flags).
/// Column keys are <see cref="SavedSurfaces"/>' saved column names, so a flag in the sheet and
/// a flag in the file name the same cell.
/// <para>
/// The solve codes offered are OpticStudio's names from its "Summary of Solves" table
/// (OpticStudio 2025 R2 help, Lens Data Editor › Solve Types › Summary of Solves,
/// plans/sources/optics/solves/Summary_of_Solves.html), with their parameter names and code
/// letters as badges; ZPL macro solves are left out. The editor stores them and does not
/// evaluate them (OP-29 builds the solve engine). The clear and mechanical semi-diameters start
/// as the "Automatic" solve, as in OpticStudio; its badge "A" is this program's (OpticStudio
/// shows none, and "U" on a fixed value).
/// </para>
/// <para>
/// Only the Standard surface can be chosen. A surface of another type read from a file gets a
/// row type of its own name with generic "Par n" columns (<see cref="Build"/>), so it is shown,
/// edited and saved without being understood; OP-23, OP-27 and OP-21 add their types' own
/// columns.
/// </para>
/// </summary>
public static class OpticsLensEditorSchema
{
    /// <summary>The number of parameter columns ("Par 1" …).</summary>
    public const int ParameterCount = 16;

    /// <summary>The slot of each fixed column.</summary>
    public const int CommentSlot = 0, RadiusSlot = 1, ThicknessSlot = 2, MaterialSlot = 3, CoatingSlot = 4,
        SemiDiameterSlot = 5, ChipZoneSlot = 6, MechanicalSlot = 7, ConicSlot = 8, TceSlot = 9, FirstParameterSlot = 10;

    // Properties of the flyout.
    public const string StopProperty = "Stop";
    public const string IgnoreProperty = "Ignore";
    public const string ApertureProperty = "ApertureType";
    public const string ObscurationProperty = "Obscuration";
    public const string MinRadiusProperty = "MinRadius";
    public const string MaxRadiusProperty = "MaxRadius";
    public const string HalfWidthXProperty = "HalfWidthX";
    public const string HalfWidthYProperty = "HalfWidthY";
    public const string ArmWidthProperty = "ArmWidth";
    public const string ArmCountProperty = "ArmCount";
    public const string ApertureDecenterXProperty = "ApertureDecenterX";
    public const string ApertureDecenterYProperty = "ApertureDecenterY";
    public const string VerticesProperty = "Vertices";
    public const string TiltProperty = "TiltDecenter";
    public const string SkipRaysProperty = "SkipRaysTo";
    public const string HideRaysProperty = "HideRaysTo";
    public const string DoNotDrawProperty = "DoNotDraw";

    public const string Yes = "Yes", No = "No", NoAperture = "None";

    /// <summary>The aperture choices of the flyout, "None" first, then the saved names.</summary>
    public static IReadOnlyList<string> ApertureChoices { get; } = new[] { NoAperture }.Concat(SavedSurfaces.ApertureTypes).ToArray();

    public const string AutomaticSolve = "Automatic";

    /// <summary>The flag a clear or mechanical semi-diameter starts with.</summary>
    public static CellFlag Automatic { get; } = CellFlag.Solve(AutomaticSolve);

    private static readonly IReadOnlyList<string> YesNo = new[] { No, Yes };

    private static SheetSolveCode Solve(string name, string badge, params string[] parameters) =>
        new(name, name, badge) { ParameterNames = parameters };

    // Summary of Solves (OpticStudio 2025 R2): curvature solves.
    private static readonly SheetSolveCode[] RadiusSolves =
    {
        Solve("Marginal ray angle", "M", "Angle"),
        Solve("Chief ray angle", "C", "Angle"),
        Solve("Marginal ray normal", "N"),
        Solve("Chief ray normal", "N"),
        Solve("Aplanatic", "A"),
        Solve("Element power", "X", "Power"),
        Solve("Concentric with surface", "S", "Surface to be concentric to"),
        Solve("Concentric with radius", "R", "Surface to be concentric with"),
        Solve("F/#", "F", "Paraxial F/#"),
    };

    // Summary of Solves: thickness solves.
    private static readonly SheetSolveCode[] ThicknessSolves =
    {
        Solve("Marginal ray height", "M", "Height", "Pupil zone"),
        Solve("Chief ray height", "C", "Height"),
        Solve("Edge thickness", "E", "Thickness", "Radial height"),
        Solve("Optical path difference", "O", "OPD", "Pupil zone"),
        Solve("Position", "T", "Surface", "Length from surface"),
        Solve("Compensator", "S", "Surface", "Sum of surface thicknesses"),
        Solve("Center of curvature", "X", "Surface to be at the COC of"),
        Solve("Pupil position", "U"),
    };

    // Summary of Solves: clear semi-diameter solves (Automatic and Maximum; Fixed is the flag).
    private static readonly SheetSolveCode[] SemiDiameterSolves =
    {
        Solve(AutomaticSolve, "A"),
        Solve(OpenSim.Optics.Sequential.LensEditing.ApertureTools.MaximumSolve, "M"),
    };

    private static readonly SheetSolveCode[] MechanicalSolves = { Solve(AutomaticSolve, "A") };

    // Summary of Solves: parameter solves.
    private static readonly SheetSolveCode[] ParameterSolves = { Solve("Chief ray", "C", "Field", "Wavelength") };

    private static SheetColumn Length(string key, string header) => new(key, header) { NumberFormat = "G8" };

    /// <summary>The columns every surface type shares, slots 0 to 9.</summary>
    private static SheetColumn?[] CommonColumns() => new SheetColumn?[]
    {
        new(SavedSurfaces.CommentColumn, "Comment") { Kind = CellKind.Text },
        Length(SavedSurfaces.RadiusColumn, "Radius") with
        {
            AllowsInfinity = true, Default = CellValue.Of(double.PositiveInfinity), Flags = CellFlagSupport.All, SolveCodes = RadiusSolves,
            ToolTip = "Vertex radius in lens units; positive when the centre of curvature is toward +z. Infinity is a plane."
        },
        Length(SavedSurfaces.ThicknessColumn, "Thickness") with
        {
            AllowsInfinity = true, Flags = CellFlagSupport.All, SolveCodes = ThicknessSolves,
            ToolTip = "Distance to the next vertex in lens units; negative after an odd number of mirrors. Infinity only on the object."
        },
        new(SavedSurfaces.MaterialColumn, "Material")
        {
            Kind = CellKind.Text, Flags = CellFlagSupport.Pickup,
            ToolTip = "A glass of the active catalogs (catalog:glass picks one), MIRROR, an index (1.5), or a model glass 'nd, Vd' or 'nd, Vd, ΔPgF'. Empty is air."
        },
        new(SavedSurfaces.CoatingColumn, "Coating") { Kind = CellKind.Text, ToolTip = "A coating of the lens's coating catalog, by name; empty is uncoated." },
        Length(SavedSurfaces.SemiDiameterColumn, "Clear Semi-Dia") with
        {
            Minimum = 0, Flags = CellFlagSupport.Solve | CellFlagSupport.Pickup, SolveCodes = SemiDiameterSolves, DefaultFlag = Automatic,
            ToolTip = "Clear semi-diameter in lens units. A: automatic (sized from the rays); typing a value fixes it."
        },
        Length(SavedSurfaces.ChipZoneColumn, "Chip Zone") with { Minimum = 0, Flags = CellFlagSupport.Pickup },
        Length(SavedSurfaces.MechanicalSemiDiameterColumn, "Mech Semi-Dia") with
        {
            Minimum = 0, Flags = CellFlagSupport.Solve | CellFlagSupport.Pickup, SolveCodes = MechanicalSolves, DefaultFlag = Automatic,
            ToolTip = "Mechanical semi-diameter in lens units. A: automatic (the clear one plus the chip zone)."
        },
        new(SavedSurfaces.ConicColumn, "Conic") { Flags = CellFlagSupport.Variable | CellFlagSupport.Pickup },
        new(SavedSurfaces.TceColumn, "TCE x 1E-6") { Flags = CellFlagSupport.Variable | CellFlagSupport.Pickup, ToolTip = "Thermal coefficient of expansion; kept for thermal analysis, which is not built." },
    };

    private static SheetColumn ParameterColumn(int number) =>
        new(SavedSurfaces.ParameterColumn(number), $"Par {number}")
        {
            Flags = CellFlagSupport.Variable | CellFlagSupport.Solve | CellFlagSupport.Pickup, SolveCodes = ParameterSolves
        };

    private static SheetColumn YesNoProperty(string key, string header, string? tip = null) =>
        new(key, header) { Kind = CellKind.Choice, Choices = YesNo, ToolTip = tip };

    private static SheetColumn LengthProperty(string key, string header) => new(key, header) { NumberFormat = "G8" };

    /// <summary>The properties flyout, the same for every type.</summary>
    private static IReadOnlyList<SheetColumn> Properties() => new[]
    {
        YesNoProperty(StopProperty, "Make surface stop", "The aperture stop; exactly one surface between the object and the image is."),
        YesNoProperty(IgnoreProperty, "Ignore this surface", "The ray crosses the vertex plane unchanged; the thickness still counts."),
        new SheetColumn(ApertureProperty, "Aperture type") { Kind = CellKind.Choice, Choices = ApertureChoices },
        YesNoProperty(ObscurationProperty, "Obscuration", "Block the shape and pass the rest."),
        LengthProperty(MinRadiusProperty, "Min radius") with { Minimum = 0 },
        LengthProperty(MaxRadiusProperty, "Max radius") with { Minimum = 0 },
        LengthProperty(HalfWidthXProperty, "X half width / semi-axis") with { Minimum = 0 },
        LengthProperty(HalfWidthYProperty, "Y half width / semi-axis") with { Minimum = 0 },
        LengthProperty(ArmWidthProperty, "Spider arm width") with { Minimum = 0 },
        new SheetColumn(ArmCountProperty, "Spider arms") { Kind = CellKind.Integer, Minimum = 0 },
        LengthProperty(ApertureDecenterXProperty, "Aperture x-decenter"),
        LengthProperty(ApertureDecenterYProperty, "Aperture y-decenter"),
        new SheetColumn(VerticesProperty, "User-defined vertices") { Kind = CellKind.Text, ToolTip = "x y pairs in lens units separated by semicolons: 0 0; 1 0; 0 1" },
        new SheetColumn(TiltProperty, "Tilt/decenter") { Kind = CellKind.Choice, Choices = new[] { "None (not built, OP-21)" } },
        YesNoProperty(SkipRaysProperty, "Skip rays to this surface", "Layouts draw rays past this surface; the trace is unchanged."),
        YesNoProperty(HideRaysProperty, "Hide rays to this surface", "Kept for the layouts; not drawn differently yet."),
        YesNoProperty(DoNotDrawProperty, "Do not draw this surface", "Kept for the layouts; not drawn differently yet."),
    };

    /// <summary>The Standard surface's row type.</summary>
    public static SheetRowType Standard(IReadOnlyList<SheetColumn> properties) =>
        new(SavedSurfaces.StandardType, CommonColumns())
        {
            Description = "A plane, sphere or conic (OpticStudio's Standard surface).",
            Properties = properties,
        };

    /// <summary>A row type for a surface type this build does not trace: the common columns and
    /// every parameter column, shown by number.</summary>
    public static SheetRowType Generic(string name, IReadOnlyList<SheetColumn> properties) =>
        new(name, CommonColumns().Concat(Enumerable.Range(1, ParameterCount).Select(ParameterColumn)).ToArray())
        {
            Description = $"'{name}' is saved with the lens; this build shows its parameters by number and does not trace it.",
            Properties = properties,
        };

    /// <summary>The schema: Standard first (the type new rows get), then a generic row type for
    /// each other type name.</summary>
    public static SheetSchema Build(IEnumerable<string> otherTypes)
    {
        var properties = Properties();
        var types = new List<SheetRowType> { Standard(properties) };
        foreach (var name in otherTypes.Where(n => !string.Equals(n, SavedSurfaces.StandardType, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            types.Add(Generic(name, properties));
        var slotHeaders = new[] { "Comment", "Radius", "Thickness", "Material", "Coating", "Clear Semi-Dia", "Chip Zone", "Mech Semi-Dia", "Conic", "TCE x 1E-6" }
            .Concat(Enumerable.Range(1, ParameterCount).Select(n => $"Par {n}")).ToArray();
        return new SheetSchema("Surface type", slotHeaders, types) { FirstRowNumber = 0 };
    }
}
