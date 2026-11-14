using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Optics.Sequential;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The Lens Data Editor's sheet (OP-31) on OP-10's spreadsheet control: the columns of
/// OpticStudio's editor (comment, radius, thickness, material, coating, clear semi-diameter,
/// chip zone, mechanical semi-diameter, conic, TCE, then the parameter columns) and the
/// properties flyout (stop, ignore, aperture, tilt/decenter before and after, drawing flags).
/// Column keys are <see cref="SavedSurfaces"/>' saved column names, so a flag in the sheet and
/// a flag in the file name the same cell.
/// <para>
/// The solve codes offered are OpticStudio's names from its "Summary of Solves" table
/// (OpticStudio 2025 R2 help, Lens Data Editor › Solve Types › Summary of Solves,
/// plans/sources/optics/solves/Summary_of_Solves.html), with their parameter names and code
/// letters as badges; ZPL macro solves are left out. <see cref="SavedSolves"/> evaluates them
/// (TC-6). The clear and mechanical semi-diameters start as the "Automatic" solve, as in
/// OpticStudio; its badge "A" is this program's (OpticStudio shows none, and "U" on a fixed
/// value).
/// </para>
/// <para>
/// Row types (TC-6): Standard; one for each registered surface type of
/// <see cref="SurfaceTypes"/>, whose parameter columns follow the shared columns in the type's
/// own order, named from its help page, with the unit in the tooltip; and a generic row type
/// with "Par n" columns for a type read from a file that is not registered, so it is shown,
/// edited and saved without being understood. A type's parameters past the last slot
/// (<see cref="ParameterSlots"/>) are kept with the row and written back, not shown. Parameter
/// columns reset when the row's type changes (<see cref="SheetColumn.ResetOnRetype"/>), the
/// shared columns carry over: this program's reading, since OpticStudio's "Type (surface
/// properties)" page does not say what a type change keeps.
/// </para>
/// </summary>
public static class OpticsLensEditorSchema
{
    /// <summary>The number of parameter slots after the shared columns.</summary>
    public const int ParameterSlots = 32;

    /// <summary>The number of "Par n" columns of the generic row type.</summary>
    public const int ParameterCount = ParameterSlots;

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
    public const string SkipRaysProperty = "SkipRaysTo";
    public const string HideRaysProperty = "HideRaysTo";
    public const string DoNotDrawProperty = "DoNotDraw";

    // Tilt/decenter before and after (OpticStudio's Surface Properties › Tilt/Decenter).
    public const string BeforeDecenterXProperty = "BeforeDecenterX";
    public const string BeforeDecenterYProperty = "BeforeDecenterY";
    public const string BeforeTiltXProperty = "BeforeTiltX";
    public const string BeforeTiltYProperty = "BeforeTiltY";
    public const string BeforeTiltZProperty = "BeforeTiltZ";
    public const string BeforeOrderProperty = "BeforeOrder";
    public const string AfterModeProperty = "AfterMode";
    public const string AfterSurfaceProperty = "AfterSurface";
    public const string AfterDecenterXProperty = "AfterDecenterX";
    public const string AfterDecenterYProperty = "AfterDecenterY";
    public const string AfterTiltXProperty = "AfterTiltX";
    public const string AfterTiltYProperty = "AfterTiltY";
    public const string AfterTiltZProperty = "AfterTiltZ";
    public const string AfterOrderProperty = "AfterOrder";

    // A coordinate break's Coordinate Return.
    public const string ReturnModeProperty = "CoordinateReturn";
    public const string ReturnSurfaceProperty = "ReturnToSurface";

    public const string Yes = "Yes", No = "No", NoAperture = "None";

    /// <summary>The order choices, as the Tilt/Decenter page words them.</summary>
    public const string DecenterThenTilt = "Decenter then tilt", TiltThenDecenter = "Tilt then decenter";

    /// <summary>The after-surface choices, the page's options a) to e).</summary>
    public const string AfterExplicit = "Explicit", AfterPickUpThis = "Pick up this surface", AfterReverseThis = "Reverse this surface",
        AfterPickUp = "Pick up surface", AfterReverse = "Reverse surface";

    /// <summary>The coordinate return choices, the Tilt/Decenter page's names.</summary>
    public const string ReturnNone = "None", ReturnOrientation = "Orientation Only", ReturnXY = "Orientation XY", ReturnXYZ = "Orientation XYZ";

    public static IReadOnlyList<string> OrderChoices { get; } = new[] { DecenterThenTilt, TiltThenDecenter };
    public static IReadOnlyList<string> AfterChoices { get; } = new[] { AfterExplicit, AfterPickUpThis, AfterReverseThis, AfterPickUp, AfterReverse };
    public static IReadOnlyList<string> ReturnChoices { get; } = new[] { ReturnNone, ReturnOrientation, ReturnXY, ReturnXYZ };

    /// <summary>The aperture choices of the flyout, "None" first, then the saved names.</summary>
    public static IReadOnlyList<string> ApertureChoices { get; } = new[] { NoAperture }.Concat(SavedSurfaces.ApertureTypes).ToArray();

    public const string AutomaticSolve = SavedSolves.Automatic;

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
        Solve(SavedSolves.Maximum, "M"),
    };

    private static readonly SheetSolveCode[] MechanicalSolves = { Solve(AutomaticSolve, "A") };

    // Summary of Solves: parameter solves. "The Chief Ray solve works only on coordinate break
    // surfaces, and only on the first four parameters" ("Parameter Solves").
    private static readonly SheetSolveCode[] ChiefRaySolves = { Solve("Chief ray", "C", "Field", "Wavelength") };

    private static SheetColumn Length(string key, string header) => new(key, header) { NumberFormat = "G8" };

    private static SheetColumn ThicknessColumn() => Length(SavedSurfaces.ThicknessColumn, "Thickness") with
    {
        AllowsInfinity = true, Flags = CellFlagSupport.All, SolveCodes = ThicknessSolves,
        ToolTip = "Distance to the next vertex in lens units; negative after an odd number of mirrors. Infinity only on the object."
    };

    /// <summary>The columns every surface type shares, slots 0 to 9; a type that ignores the
    /// radius and conic has no cell for them.</summary>
    private static SheetColumn?[] CommonColumns(bool radiusAndConic = true) => new SheetColumn?[]
    {
        new(SavedSurfaces.CommentColumn, "Comment") { Kind = CellKind.Text },
        radiusAndConic ? Length(SavedSurfaces.RadiusColumn, "Radius") with
        {
            AllowsInfinity = true, Default = CellValue.Of(double.PositiveInfinity), Flags = CellFlagSupport.All, SolveCodes = RadiusSolves,
            ToolTip = "Vertex radius in lens units; positive when the centre of curvature is toward +z. Infinity is a plane."
        } : null,
        ThicknessColumn(),
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
        Length(SavedSurfaces.ChipZoneColumn, "Chip Zone") with { Minimum = 0, Flags = CellFlagSupport.Pickup, ToolTip = "Chip zone in lens units. A pickup here is stored but not evaluated (OpticStudio's Summary of Solves has no solve for this column)." },
        Length(SavedSurfaces.MechanicalSemiDiameterColumn, "Mech Semi-Dia") with
        {
            Minimum = 0, Flags = CellFlagSupport.Solve | CellFlagSupport.Pickup, SolveCodes = MechanicalSolves, DefaultFlag = Automatic,
            ToolTip = "Mechanical semi-diameter in lens units. A: automatic (the clear one plus the chip zone). A pickup here is stored but not evaluated."
        },
        radiusAndConic ? new(SavedSurfaces.ConicColumn, "Conic") { Flags = CellFlagSupport.Variable | CellFlagSupport.Pickup } : null,
        new(SavedSurfaces.TceColumn, "TCE x 1E-6") { Flags = CellFlagSupport.Variable | CellFlagSupport.Pickup, ToolTip = "Thermal coefficient of expansion: the expansion of this surface's mount or of a material that is not a catalog glass, read by thermal pickups (Multi-Configuration Editor, Make Thermal)." },
    };

    /// <summary>A generic "Par n" column of a type that is not registered.</summary>
    private static SheetColumn GenericParameterColumn(int number) =>
        new(SavedSurfaces.ParameterColumn(number), $"Par {number}")
        {
            Flags = number < SurfaceTypes.FirstExtraData ? CellFlagSupport.Variable | CellFlagSupport.Pickup : CellFlagSupport.Variable,
            ResetOnRetype = true,
        };

    /// <summary>A registered type's parameter column: named and explained from its help page,
    /// in lens units scaled by the column's power (<see cref="SurfaceParameterInfo.UnitText"/>).
    /// Parameters 1–12 take variables and pickups; extra data (13 on) variables only, since
    /// OpticStudio's pickup columns reach parameter 12; integer columns take no flag.</summary>
    private static SheetColumn ParameterColumn(SurfaceTypeLayout layout, SurfaceParameterInfo p)
    {
        bool chiefRay = layout.IsCoordinateBreak && p.Number <= 4;
        return new(SavedSurfaces.ParameterColumn(p.Number), p.Name)
        {
            Kind = p.IsInteger ? CellKind.Integer : CellKind.Number,
            Minimum = p.Range?.Min,
            Maximum = p.Range?.Max,
            Flags = p.IsInteger ? CellFlagSupport.None
                : p.IsExtraData ? CellFlagSupport.Variable
                : CellFlagSupport.Variable | CellFlagSupport.Pickup | (chiefRay ? CellFlagSupport.Solve : CellFlagSupport.None),
            SolveCodes = chiefRay ? ChiefRaySolves : Array.Empty<SheetSolveCode>(),
            ResetOnRetype = true,
            ToolTip = $"{layout.Name}, parameter {p.Number}: {p.Description ?? p.Name} ({p.UnitText}).",
        };
    }

    private static SheetColumn YesNoProperty(string key, string header, string? tip = null) =>
        new(key, header) { Kind = CellKind.Choice, Choices = YesNo, ToolTip = tip };

    private static SheetColumn LengthProperty(string key, string header, string? tip = null) => new(key, header) { NumberFormat = "G8", ToolTip = tip };

    private static SheetColumn DrawProperty(string key) => key switch
    {
        SkipRaysProperty => YesNoProperty(SkipRaysProperty, "Skip rays to this surface", "Layouts draw rays past this surface; the trace is unchanged."),
        HideRaysProperty => YesNoProperty(HideRaysProperty, "Hide rays to this surface", "Kept for the layouts; not drawn differently yet."),
        _ => YesNoProperty(DoNotDrawProperty, "Do not draw this surface", "Kept for the layouts; not drawn differently yet."),
    };

    /// <summary>The properties flyout of every type but the coordinate break.</summary>
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
        // Tilt/Decenter (OpticStudio 2025 R2 help, Surface Properties › Tilt/Decenter).
        LengthProperty(BeforeDecenterXProperty, "Before: decenter X", "Lens units, applied before the surface."),
        LengthProperty(BeforeDecenterYProperty, "Before: decenter Y", "Lens units, applied before the surface."),
        LengthProperty(BeforeTiltXProperty, "Before: tilt X", "Degrees, right-handed about +x."),
        LengthProperty(BeforeTiltYProperty, "Before: tilt Y", "Degrees, right-handed about the new +y."),
        LengthProperty(BeforeTiltZProperty, "Before: tilt Z", "Degrees, right-handed about the new +z."),
        new SheetColumn(BeforeOrderProperty, "Before: order") { Kind = CellKind.Choice, Choices = OrderChoices },
        new SheetColumn(AfterModeProperty, "After: values")
        {
            Kind = CellKind.Choice, Choices = AfterChoices,
            ToolTip = "Explicit values below; this surface's before values picked up or reversed; or a prior surface's before values picked up or reversed."
        },
        new SheetColumn(AfterSurfaceProperty, "After: prior surface") { Kind = CellKind.Integer, Minimum = 0, ToolTip = "The prior surface whose before values are picked up or reversed." },
        LengthProperty(AfterDecenterXProperty, "After: decenter X", "Lens units, applied after the surface (explicit values)."),
        LengthProperty(AfterDecenterYProperty, "After: decenter Y", "Lens units, applied after the surface (explicit values)."),
        LengthProperty(AfterTiltXProperty, "After: tilt X", "Degrees (explicit values)."),
        LengthProperty(AfterTiltYProperty, "After: tilt Y", "Degrees (explicit values)."),
        LengthProperty(AfterTiltZProperty, "After: tilt Z", "Degrees (explicit values)."),
        new SheetColumn(AfterOrderProperty, "After: order") { Kind = CellKind.Choice, Choices = OrderChoices },
        DrawProperty(SkipRaysProperty),
        DrawProperty(HideRaysProperty),
        DrawProperty(DoNotDrawProperty),
    };

    /// <summary>The coordinate break's flyout: its Coordinate Return and the drawing flags.</summary>
    private static IReadOnlyList<SheetColumn> BreakProperties() => new[]
    {
        new SheetColumn(ReturnModeProperty, "Coordinate return")
        {
            Kind = CellKind.Choice, Choices = ReturnChoices,
            ToolTip = "Solve the break's tilts (and decenters, and thickness) so the coordinate system returns to that of the prior surface below."
        },
        new SheetColumn(ReturnSurfaceProperty, "Return to surface") { Kind = CellKind.Integer, Minimum = 0 },
        DrawProperty(SkipRaysProperty),
        DrawProperty(HideRaysProperty),
        DrawProperty(DoNotDrawProperty),
    };

    /// <summary>The Standard surface's row type.</summary>
    public static SheetRowType Standard(IReadOnlyList<SheetColumn> properties) =>
        new(SavedSurfaces.StandardType, CommonColumns())
        {
            Description = "A plane, sphere or conic (OpticStudio's Standard surface).",
            Properties = properties,
        };

    /// <summary>A registered type's row: the shared columns (without radius and conic where
    /// the type ignores them; comment and thickness only for a coordinate break), then its
    /// parameters in number order, the first <see cref="ParameterSlots"/> of them.</summary>
    public static SheetRowType Registered(SurfaceTypeLayout layout, IReadOnlyList<SheetColumn> properties, IReadOnlyList<SheetColumn> breakProperties)
    {
        SheetColumn?[] common;
        if (layout.IsCoordinateBreak)
        {
            common = new SheetColumn?[FirstParameterSlot];
            common[CommentSlot] = new(SavedSurfaces.CommentColumn, "Comment") { Kind = CellKind.Text };
            common[ThicknessSlot] = ThicknessColumn() with { ToolTip = "Distance to the next vertex along the new z, lens units, applied after the break's decenters and tilts." };
        }
        else common = CommonColumns(layout.UsesRadiusAndConic);
        var shown = layout.Parameters.Take(ParameterSlots).ToArray();
        string more = layout.Parameters.Count > ParameterSlots
            ? $" The sheet shows parameters {shown[0].Number} to {shown[^1].Number}; further ones are kept and saved."
            : "";
        return new SheetRowType(layout.Name, common.Concat(shown.Select(p => ParameterColumn(layout, p))).ToArray())
        {
            Description = $"OpticStudio's {layout.Name} surface; parameters from {layout.Source}.{more}",
            Properties = layout.IsCoordinateBreak ? breakProperties : properties,
        };
    }

    /// <summary>A row type for a surface type that is not registered: the shared columns and
    /// every parameter column, shown by number.</summary>
    public static SheetRowType Generic(string name, IReadOnlyList<SheetColumn> properties) =>
        new(name, CommonColumns().Concat(Enumerable.Range(1, ParameterCount).Select(GenericParameterColumn)).ToArray())
        {
            Description = $"'{name}' is saved with the lens; this build shows its parameters by number and does not trace it.",
            Properties = properties,
        };

    /// <summary>The schema: Standard first (the type new rows get), the registered types, then a
    /// generic row type for each other type name.</summary>
    public static SheetSchema Build(IEnumerable<string> otherTypes)
    {
        var properties = Properties();
        var breakProperties = BreakProperties();
        var types = new List<SheetRowType> { Standard(properties) };
        types.AddRange(SurfaceTypes.All.Select(l => Registered(l, properties, breakProperties)));
        foreach (var name in otherTypes.Where(n => !string.Equals(n, SavedSurfaces.StandardType, StringComparison.OrdinalIgnoreCase) && SurfaceTypes.Find(n) is null)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            types.Add(Generic(name, properties));
        var slotHeaders = new[] { "Comment", "Radius", "Thickness", "Material", "Coating", "Clear Semi-Dia", "Chip Zone", "Mech Semi-Dia", "Conic", "TCE x 1E-6" }
            .Concat(Enumerable.Range(1, ParameterSlots).Select(n => $"Par {n}")).ToArray();
        return new SheetSchema("Surface type", slotHeaders, types) { FirstRowNumber = 0 };
    }
}
