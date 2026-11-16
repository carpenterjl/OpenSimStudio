using OpenSim.App.Controls.Spreadsheet;
using OpenSim.Optics.NonSequential.Editing;

namespace OpenSim.App.ViewModels;

/// <summary>
/// The Non-Sequential Component Editor's sheet (plan item OP-71) on OP-10's spreadsheet control.
/// The fixed columns are those the OpticStudio help names for every object (help, "Object
/// Placement" and "Placing Sources Inside Objects"): comment, reference object, Inside Of, the x,
/// y and z position, the tilts about x, y and z, and the material; the type's parameters follow
/// in its own order, named from its descriptor (<see cref="NscObjectTypeDescriptor"/>). A type the
/// catalogue does not know (read from a file) gets a generic row type with "Par n" columns, so its
/// raw parameters are shown and written back unchanged.
/// <para>
/// The properties flyout holds the rest of OpticStudio's Object Properties this build has: "Use
/// Global XYZ Rotation Order" (help, "General Settings"), OP-70's detecting stand-in, and "Face
/// Is" and the coating of each face the type has (help, "Coat/Scatter").
/// </para>
/// </summary>
public static class OpticsNonSequentialEditorSchema
{
    /// <summary>The number of parameter slots after the fixed columns.</summary>
    public const int ParameterSlots = 32;

    /// <summary>The slot of each fixed column.</summary>
    public const int CommentSlot = 0, ReferenceSlot = 1, InsideOfSlot = 2, XSlot = 3, YSlot = 4, ZSlot = 5,
        TiltXSlot = 6, TiltYSlot = 7, TiltZSlot = 8, MaterialSlot = 9, FirstParameterSlot = 10;

    public const string GlobalOrderProperty = "GlobalXyzRotationOrder";
    public const string StandInProperty = "DetectingStandIn";
    public const string Yes = "Yes", No = "No";

    /// <summary>"Face Is" choices (help, "Coat/Scatter").</summary>
    public static IReadOnlyList<string> FaceIsChoices { get; } = new[] { "Object Default", "Reflective", "Absorbing" };

    /// <summary>The name a saved row with no type is shown under.</summary>
    public const string NoType = "(no type)";

    /// <summary>The flyout key of face <paramref name="face"/>'s "Face Is".</summary>
    public static string FaceIsProperty(int face) => $"Face{face}.FaceIs";

    /// <summary>The flyout key of face <paramref name="face"/>'s coating.</summary>
    public static string CoatingProperty(int face) => $"Face{face}.Coating";

    /// <summary>The key of parameter column <paramref name="number"/> (1-based).</summary>
    public static string ParameterKey(int number) => $"Par {number}";

    private static readonly IReadOnlyList<string> YesNo = new[] { No, Yes };

    private static SheetColumn?[] FixedColumns() => new SheetColumn?[]
    {
        new("Comment", "Comment") { Kind = CellKind.Text },
        new("Ref", "Ref Object")
        {
            Kind = CellKind.Integer,
            ToolTip = "The object whose frame the position and tilts are in: 0 the scene; positive an absolute object number; negative relative (added to this object's number). It must come earlier (help, \"Object Placement\").",
        },
        new("Inside", "Inside Of")
        {
            Kind = CellKind.Integer,
            ToolTip = "The volume this object is placed inside: 0 none; positive absolute; negative relative. It must come earlier (help, \"Placing Sources Inside Objects\").",
        },
        new("X", "X Position") { ToolTip = "Lens units, in the reference object's frame." },
        new("Y", "Y Position") { ToolTip = "Lens units, in the reference object's frame." },
        new("Z", "Z Position") { ToolTip = "Lens units, in the reference object's frame." },
        new("TiltX", "Tilt About X") { ToolTip = "Degrees; about x, then the new y, then the new z, unless the object uses the global XYZ rotation order." },
        new("TiltY", "Tilt About Y") { ToolTip = "Degrees." },
        new("TiltZ", "Tilt About Z") { ToolTip = "Degrees." },
        new("Material", "Material") { Kind = CellKind.Text, ToolTip = "Blank (air), MIRROR, ABSORB, a glass name or an index." },
    };

    private static IEnumerable<SheetColumn> CommonProperties() => new[]
    {
        new SheetColumn(GlobalOrderProperty, "Use global XYZ rotation order")
        {
            Kind = CellKind.Choice, Choices = YesNo,
            ToolTip = "Yes: the tilts turn about the fixed x, then y, then z axes. No: about x, then the new y, then the new z, as a coordinate break with order flag 0 (help, \"General Settings\").",
        },
        new SheetColumn(StandInProperty, "Detecting stand-in (OP-70)")
        {
            Kind = CellKind.Choice, Choices = YesNo,
            ToolTip = "Absorbs and counts every ray that meets this surface object; detectors proper are plan item OP-79.",
        },
    };

    private static IEnumerable<SheetColumn> FaceProperties(IReadOnlyDictionary<int, string> faces) =>
        faces.OrderBy(f => f.Key).SelectMany(f => new[]
        {
            new SheetColumn(FaceIsProperty(f.Key), $"Face {f.Key} ({f.Value}): Face Is")
            {
                Kind = CellKind.Choice, Choices = FaceIsChoices,
                ToolTip = "Object Default follows the material; Reflective is a thick aluminium layer; Absorbing absorbs (help, \"Coat/Scatter\").",
            },
            new SheetColumn(CoatingProperty(f.Key), $"Face {f.Key} ({f.Value}): coating")
            {
                Kind = CellKind.Text,
                ToolTip = "A coating name from the coating catalog; blank for uncoated.",
            },
        });

    /// <summary>A catalogue type's row: the fixed columns, then its parameters.</summary>
    public static SheetRowType Known(NscObjectTypeDescriptor d)
    {
        var columns = FixedColumns().Concat(d.Parameters.Take(ParameterSlots).Select((p, i) => (SheetColumn?)new SheetColumn(ParameterKey(i + 1), Header(p))
        {
            Kind = p.Kind == NscParameterKind.Integer ? CellKind.Integer : CellKind.Number,
            Default = CellValue.Of(p.Default),
            ResetOnRetype = true,
            ToolTip = p.Description,
        })).ToArray();
        return new SheetRowType(d.TypeName, columns)
        {
            Description = $"{d.TypeName} ({d.Category}, {(d.IsVolume switch { true => "a volume", false => "a surface", null => "a volume or a surface by its parameters" })}); parameters from {d.Source ?? "its help page"}.",
            Properties = CommonProperties().Concat(FaceProperties(d.Faces)).ToArray(),
        };

        static string Header(NscParameterDescriptor p) => p.Kind switch
        {
            NscParameterKind.Length => $"{p.Name} [lens units]",
            NscParameterKind.Angle => $"{p.Name} [deg]",
            _ => p.Name,
        };
    }

    /// <summary>A row type for a saved type the catalogue does not know: its raw parameters by number.</summary>
    public static SheetRowType Generic(string name) =>
        new(name, FixedColumns().Concat(Enumerable.Range(1, ParameterSlots).Select(n => (SheetColumn?)new SheetColumn(ParameterKey(n), $"Par {n}") { ResetOnRetype = true })).ToArray())
        {
            Description = $"'{name}' is saved with the system; this build shows its parameters by number, writes them back unchanged and does not trace it.",
            Properties = CommonProperties().ToArray(),
        };

    /// <summary>The schema: the catalogue's types in order (the first is the default for new
    /// rows), then a generic row type for each other saved type name.</summary>
    public static SheetSchema Build(NscObjectCatalogue catalogue, IEnumerable<string> otherTypes)
    {
        var types = catalogue.Types.Select(Known).ToList();
        foreach (var name in otherTypes.Where(n => catalogue.Find(n) is null).Distinct(StringComparer.OrdinalIgnoreCase))
            types.Add(Generic(name));
        var headers = new[] { "Comment", "Ref Object", "Inside Of", "X Position", "Y Position", "Z Position", "Tilt About X", "Tilt About Y", "Tilt About Z", "Material" }
            .Concat(Enumerable.Range(1, ParameterSlots).Select(n => $"Par {n}")).ToArray();
        return new SheetSchema("Object Type", headers, types)
        {
            FirstRowNumber = 1,
            UnknownTypeMessage = catalogue.NotBuiltMessage,
        };
    }
}
