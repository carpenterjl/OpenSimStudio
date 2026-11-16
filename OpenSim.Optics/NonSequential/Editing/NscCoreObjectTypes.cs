namespace OpenSim.Optics.NonSequential.Editing;

/// <summary>
/// The descriptors of the three object types OP-70 builds (plan item OP-71). Parameter order,
/// meaning and face numbers are copied from OpticStudio 2025 R2's help pages, saved in
/// <c>plans/sources/optics/op-70-help/</c>: "Sphere (non-sequential geometry objects)",
/// "Rectangle" and "Standard Lens". The pages describe each parameter in a sentence and do not
/// print the editor's column headers; the short column names here ("Radius 1", "X Half Width")
/// are this program's, made from those sentences, except "Is Volume?", which the Sphere page
/// quotes. The defaults are this program's choice, so that a new row is a valid object (a unit
/// sphere, a unit square, a flat plate of unit thickness); the help gives none.
/// </summary>
public static class NscCoreObjectTypes
{
    /// <summary>Sphere: "Spheres are defined by two parameters" — the radius and "Is Volume?"
    /// (zero is a hollow shell, any other value a solid). "All faces Face 0."</summary>
    public static NscObjectTypeDescriptor Sphere { get; } = new()
    {
        TypeName = "Sphere",
        ZplCode = "NSC_SPHE",   // help, "SETNSCPROPERTY (keywords)", object type codes
        Parameters = new NscParameterDescriptor[]
        {
            new("Radius", 1) { Description = "The radius of the sphere." },
            new("Is Volume?", 1, NscParameterKind.Flag) { Description = "Zero for a hollow shell, which must reflect or absorb; any other value for a closed volume." },
        },
        Faces = new Dictionary<int, string> { [0] = "All Faces" },
        VolumeFlagParameter = 1,
        Source = "OpticStudio 2025 R2 help, \"Sphere (non-sequential geometry objects)\"",
        PlanItem = "OP-70",
        Geometry = p => new NscSphere(p[0], p[1] != 0),
    };

    /// <summary>Rectangle: "a flat surface defined by 2 parameters", the X and Y half widths.
    /// "All faces Face 0."</summary>
    public static NscObjectTypeDescriptor Rectangle { get; } = new()
    {
        TypeName = "Rectangle",
        ZplCode = "NSC_RECT",   // help, "SETNSCPROPERTY (keywords)", object type codes
        Parameters = new NscParameterDescriptor[]
        {
            new("X Half Width", 1) { Description = "The X half width." },
            new("Y Half Width", 1) { Description = "The Y half width." },
        },
        Faces = new Dictionary<int, string> { [0] = "All Faces" },
        IsVolume = false,
        Source = "OpticStudio 2025 R2 help, \"Rectangle\"",
        PlanItem = "OP-70",
        Geometry = p => new NscRectangle(p[0], p[1]),
    };

    /// <summary>Standard Lens: "9 parameters are used to define a standard lens"; "Front face
    /// Face 1, back face Face 2, all other faces Face 0" (the page names face 0 "Side").</summary>
    public static NscObjectTypeDescriptor StandardLens { get; } = new()
    {
        TypeName = "Standard Lens",
        ZplCode = "NSC_SLEN",   // help, "SETNSCPROPERTY (keywords)", object type codes
        Parameters = new NscParameterDescriptor[]
        {
            new("Radius 1", 0) { Description = "The radius of curvature of the front face; zero for flat." },
            new("Conic 1", 0, NscParameterKind.Number) { Description = "The conic constant of the front face." },
            new("Clear 1", 1) { Description = "The clear semi-diameter of the front face (a negative value, the hyperhemispheric sag point, is not built: OP-72)." },
            new("Edge 1", 1) { Description = "The radial aperture to the edge of the front side of the lens." },
            new("Thickness", 1) { Description = "The center-to-center thickness of the lens." },
            new("Radius 2", 0) { Description = "The radius of curvature of the rear face; zero for flat." },
            new("Conic 2", 0, NscParameterKind.Number) { Description = "The conic constant of the rear face." },
            new("Clear 2", 1) { Description = "The clear semi-diameter of the rear face." },
            new("Edge 2", 1) { Description = "The radial aperture to the edge of the rear side of the lens." },
        },
        Faces = new Dictionary<int, string> { [0] = "Side", [1] = "Front", [2] = "Back" },
        IsVolume = true,
        Source = "OpticStudio 2025 R2 help, \"Standard Lens\"",
        PlanItem = "OP-70",
        Geometry = p => new NscStandardLens(p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7], p[8]),
    };

    /// <summary>Sphere, Rectangle, Standard Lens.</summary>
    public static IReadOnlyList<NscObjectTypeDescriptor> All { get; } = new[] { Sphere, Rectangle, StandardLens };
}
