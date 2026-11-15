namespace OpenSim.Optics.CadExport;

// OP-96: CAD export of the sequential lens (docs/Optics-Conventions.md §70). The settings follow
// OpticStudio's Export CAD Files tool (2025 R2 help page "CAD Files", saved under
// plans/sources/optics/op-96-help/CAD_Files.html) where it has a counterpart.

/// <summary>The file written. SAT (OP-76), point clouds and 3-D DXF are not built.</summary>
public enum CadExportFormat
{
    /// <summary>ISO 10303-21, AP214: solids (help page: "STEP").</summary>
    Step = 0,

    /// <summary>IGES 5.3: surfaces and linework.</summary>
    Iges = 1,

    /// <summary>STL: triangulated solids.</summary>
    Stl = 2,

    /// <summary>DXF: the 2-D cross-section linework (help page "Export 2D DXF").</summary>
    Dxf = 3,
}

/// <summary>
/// How a lens element's edge joins its two faces, the three options of OpticStudio's surface
/// property "Draw Edges As" (help page "Draw (surface properties)", saved under
/// plans/sources/optics/ansyshelp-op31/): applied here to every element of the export.
/// </summary>
public enum CadEdgeStyle
{
    /// <summary>"Squared To Next Surface": a flat face from the smaller face out to the larger,
    /// then a cylinder joining the edges (the shaded model's form, LayoutSolids).</summary>
    Squared = 0,

    /// <summary>"Tapered To Next Surface": each face to its own outer semi-diameter, joined by a
    /// single cone (a bevelled edge).</summary>
    Tapered = 1,

    /// <summary>"Flat To Next Surface": a cylinder at the front face's outer semi-diameter
    /// extending to where it meets the back face (the back face drawn out to that radius).</summary>
    FlatToNext = 2,
}

/// <summary>The rays exported, the patterns of OpticStudio's 3D Viewer that the help page
/// "CAD Files" points to ("This control is very similar to that defined for the 3D layout plot";
/// help page "3D Viewer"). List and Random are not built; Solid beams are not built.</summary>
public enum CadRayPattern
{
    None = 0,

    /// <summary>Rays evenly spaced along the pupil's y diameter.</summary>
    YFan = 1,

    /// <summary>Rays evenly spaced along the pupil's x diameter.</summary>
    XFan = 2,

    /// <summary>The x and the y fan.</summary>
    XYFan = 3,

    /// <summary>Rays evenly spaced around the pupil's rim.</summary>
    Ring = 4,

    /// <summary>A square grid of n × n pupil points, those inside the unit circle (this program's
    /// reading: the help page does not define the grid).</summary>
    Grid = 5,
}

/// <summary>The settings of one CAD export. Lengths are in lens units.</summary>
public sealed record CadExportSettings
{
    public CadExportFormat Format { get; init; } = CadExportFormat.Step;

    /// <summary>The first surface to export (help page: "First/Last Surface").</summary>
    public int FirstSurface { get; init; } = 1;

    /// <summary>The last surface to export; 0 for the last surface before the image.</summary>
    public int LastSurface { get; init; }

    public CadEdgeStyle EdgeStyle { get; init; } = CadEdgeStyle.Squared;

    /// <summary>
    /// The thickness of the flat-backed substrate drawn behind a mirror that is not part of a lens
    /// body, lens units, measured from the mirror's farthest point toward its back (as the layouts'
    /// mirror substrate is, LayoutGeometry.MirrorBack). Zero exports the bare mirror surface (IGES,
    /// DXF, STL) and is refused for STEP, which writes solids only.
    /// </summary>
    public double MirrorSubstrateThickness { get; init; }

    /// <summary>The export tolerance, lens units (help page: "Tolerance: The approximate accuracy
    /// of the exported objects in lens units"); OpticStudio offers 1E-4, 1E-5, 1E-6 and 1E-7
    /// (help page "EXPORTCAD (keywords)", VEC1(19)).</summary>
    public double Tolerance { get; init; } = 1e-4;

    public CadRayPattern RayPattern { get; init; } = CadRayPattern.YFan;

    /// <summary>The number of rays per field and wavelength (the grid's side for Grid).</summary>
    public int NumberOfRays { get; init; } = 7;

    /// <summary>The field number, 1-based; 0 for all (help page: "Field").</summary>
    public int Field { get; init; }

    /// <summary>The wavelength number, 1-based; 0 for all (help page: "Wavelength").</summary>
    public int Wavelength { get; init; }

    /// <summary>Help page: "Delete Vignetted: If checked, rays are not included … if they will be
    /// vignetted by any surface."</summary>
    public bool DeleteVignetted { get; init; }

    /// <summary>Binary (true) or ASCII STL.</summary>
    public bool StlBinary { get; init; } = true;

    /// <summary>The tolerance choices OpticStudio offers (help page "EXPORTCAD (keywords)").</summary>
    public static IReadOnlyList<double> ToleranceChoices { get; } = new[] { 1e-4, 1e-5, 1e-6, 1e-7 };

    /// <summary>Every problem with the settings themselves, one line each.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (!(Tolerance > 0) || !double.IsFinite(Tolerance)) problems.Add($"CAD export: the tolerance must be positive (it is {Tolerance}).");
        if (!(MirrorSubstrateThickness >= 0) || !double.IsFinite(MirrorSubstrateThickness))
            problems.Add($"CAD export: the mirror substrate thickness must be zero or positive (it is {MirrorSubstrateThickness}).");
        if (FirstSurface < 1) problems.Add($"CAD export: the first surface must be 1 or more (it is {FirstSurface}).");
        if (LastSurface < 0) problems.Add($"CAD export: the last surface must be 0 (the last before the image) or a surface number (it is {LastSurface}).");
        if (NumberOfRays < 0) problems.Add($"CAD export: the number of rays must not be negative (it is {NumberOfRays}).");
        if (Field < 0) problems.Add($"CAD export: the field must be 0 (all) or a field number (it is {Field}).");
        if (Wavelength < 0) problems.Add($"CAD export: the wavelength must be 0 (all) or a wavelength number (it is {Wavelength}).");
        return problems;
    }
}
