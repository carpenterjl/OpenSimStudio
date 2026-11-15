namespace OpenSim.Core.Model;

/// <summary>
/// One sequential surface as project and lens files store it (plan item OP-31; OpticStudio's
/// Lens Data Editor row): surface 0 is the object, the last the image. Lengths are in the
/// file's lens unit (<see cref="OpticsSystemSettings.LensUnit"/>), as the editor shows them, so
/// a typed value reads back digit for digit. OpenSim.Optics' <c>SavedSurfaces</c> converts a
/// list of these to and from its <c>SequentialLens</c> in meters.
/// <para>
/// The record is generic on purpose, so surface types built later (aspheres OP-23, special
/// surfaces OP-27, coordinate breaks OP-21, freeforms) fit without a format change: the type is
/// a name, its numeric data is a numbered parameter list (OpticStudio's "Par 1" … columns) and
/// an extra-data list, and the per-cell variable, solve and pickup flags are stored with their
/// parameters as given (OpenSim.Optics' <c>ProjectLens</c> evaluates the solves and pickups,
/// TC-6). A name or flag this build does not know is kept and written back unchanged.
/// </para>
/// </summary>
public sealed record OpticsSurfaceSettings
{
    /// <summary>The surface type's name ("Standard", "Even Asphere", "Coordinate Break", …);
    /// null is "Standard".</summary>
    public string? Type { get; init; }

    /// <summary>The comment column.</summary>
    public string? Comment { get; init; }

    /// <summary>The vertex radius in lens units; null, zero or an infinite value is a plane
    /// (docs/Optics-Conventions.md section 3). JSON has no infinity, so a plane is written
    /// without the field.</summary>
    public double? Radius { get; init; }

    /// <summary>The distance to the next vertex in lens units (unused on the image surface).</summary>
    public double Thickness { get; init; }

    /// <summary>True for an infinite thickness (an object at infinity); <see cref="Thickness"/>
    /// is then ignored.</summary>
    public bool ThicknessIsInfinite { get; init; }

    /// <summary>The medium after the surface by name: a glass of the active catalogs (optionally
    /// "catalog:glass"), or "MIRROR". Null or empty with no <see cref="Index"/> and no
    /// <see cref="ModelGlass"/> is the system's air.</summary>
    public string? Material { get; init; }

    /// <summary>A constant index relative to the system's air instead of a named material.</summary>
    public double? Index { get; init; }

    /// <summary>A model glass (nd, Vd, ΔPgF) instead of a named material.</summary>
    public OpticsModelGlassSettings? ModelGlass { get; init; }

    /// <summary>The coating's name in the lens's coating catalog; null is uncoated.</summary>
    public string? Coating { get; init; }

    /// <summary>The clear semi-diameter in lens units; null is automatic.</summary>
    public double? SemiDiameter { get; init; }

    /// <summary>The chip zone in lens units.</summary>
    public double ChipZone { get; init; }

    /// <summary>The mechanical semi-diameter in lens units; null is automatic (clear plus chip zone).</summary>
    public double? MechanicalSemiDiameter { get; init; }

    /// <summary>The conic constant.</summary>
    public double Conic { get; init; }

    /// <summary>The thermal coefficient of expansion column, 1e-6 per °C: the mount's or a
    /// non-catalog material's expansion, read by the multi-configuration thermal pickups (OP-91);
    /// not read by the trace.</summary>
    public double Tce { get; init; }

    /// <summary>The aperture stop.</summary>
    public bool IsStop { get; init; }

    /// <summary>"Ignore this surface".</summary>
    public bool IsIgnored { get; init; }

    /// <summary>"Skip rays to this surface" (drawing only).</summary>
    public bool SkipRaysTo { get; init; }

    /// <summary>"Hide rays to this surface" (drawing only; layouts do not read it yet).</summary>
    public bool HideRaysTo { get; init; }

    /// <summary>"Do not draw this surface" (drawing only; layouts do not read it yet).</summary>
    public bool DoNotDraw { get; init; }

    /// <summary>The editor row's colour (an index into the editor's palette), or null.</summary>
    public int? RowColor { get; init; }

    /// <summary>The surface aperture, or null for none.</summary>
    public OpticsSurfaceApertureSettings? Aperture { get; init; }

    /// <summary>The surface type's numbered parameters (OpticStudio's PARM columns, 1-based); a
    /// number not listed is zero.</summary>
    public List<OpticsSurfaceParameterSettings>? Parameters { get; init; }

    /// <summary>The surface type's extra data, for types whose data runs past the parameter
    /// columns (1-based); a number not listed is zero.</summary>
    public List<OpticsSurfaceParameterSettings>? ExtraData { get; init; }

    /// <summary>The cells that are not fixed: variable, solved or picked up, one entry each.</summary>
    public List<OpticsCellFlagSettings>? Flags { get; init; }

    /// <summary>The surface's own tilt and decenter before it (OpticStudio's Surface Properties ›
    /// Tilt/Decenter, "before surface"), or null for none (TC-6). Not on a coordinate break.</summary>
    public OpticsTiltDecenterSettings? TiltBefore { get; init; }

    /// <summary>The surface's tilt and decenter after it ("after surface"), with its mode
    /// (<see cref="OpticsTiltDecenterSettings.Mode"/>), or null for none (TC-6).</summary>
    public OpticsTiltDecenterSettings? TiltAfter { get; init; }

    /// <summary>A coordinate break's Coordinate Return, or null for none (TC-6); read only on a
    /// surface of type "Coordinate Break".</summary>
    public OpticsCoordinateReturnSettings? CoordinateReturn { get; init; }

    // OP-25
    /// <summary>A Grid Sag surface's imported grid, or null; read only on a surface of type
    /// "Grid Sag".</summary>
    public OpticsGridSagSettings? GridSag { get; init; }

    // OP-32
    /// <summary>The surface's composite add-ons, in order, or null for none: surfaces whose sag
    /// is added to this one's (OpticStudio's Composite Add-on surfaces, which there are rows
    /// above this one with "Add sag to next surface" checked).</summary>
    public List<OpticsCompositeAddOnSettings>? AddOns { get; init; }
}

/// <summary>
/// A surface tilt and decenter as OpticStudio's Tilt/Decenter page gives it: decenters in lens
/// units, tilts in degrees (right-handed about the positive axes), and the order. For the
/// "after" values <see cref="Mode"/> says where they come from and <see cref="Surface"/> names
/// the prior surface of the two pick-up modes. Names are OpenSim.Optics' <c>SavedSurfaces</c>
/// constants; null fields are the defaults.
/// </summary>
public sealed record OpticsTiltDecenterSettings
{
    public double DecenterX { get; init; }
    public double DecenterY { get; init; }
    public double TiltX { get; init; }
    public double TiltY { get; init; }
    public double TiltZ { get; init; }

    /// <summary>"DecenterThenTilt" (null) or "TiltThenDecenter".</summary>
    public string? Order { get; init; }

    /// <summary>After only: "Explicit" (null), "PickUpThisSurface", "ReverseThisSurface",
    /// "PickUpSurface" or "ReverseSurface".</summary>
    public string? Mode { get; init; }

    /// <summary>After only: the prior surface (0-based) of "PickUpSurface" and "ReverseSurface".</summary>
    public int Surface { get; init; }
}

/// <summary>A coordinate break's Coordinate Return: "None", "OrientationOnly", "OrientationXY"
/// or "OrientationXYZ", to the prior surface <see cref="Surface"/> (0-based).</summary>
public sealed record OpticsCoordinateReturnSettings
{
    public string? Mode { get; init; }
    public int Surface { get; init; }
}

/// <summary>A model glass: d-line index, Abbe number and partial-dispersion deviation.</summary>
public sealed record OpticsModelGlassSettings
{
    public double Nd { get; init; } = 1.5;
    public double Vd { get; init; } = 50;
    public double DeltaPgF { get; init; }
}

/// <summary>
/// A surface aperture. <see cref="Type"/> is "Circular", "Rectangular", "Elliptical",
/// "Spider", "UserDefined" or "Floating" (by the saved names OpenSim.Optics' <c>SavedSurfaces</c>
/// defines); only the fields of that type are read. Lengths in lens units.
/// </summary>
public sealed record OpticsSurfaceApertureSettings
{
    public string? Type { get; init; }
    public bool IsObscuration { get; init; }
    public double DecenterX { get; init; }
    public double DecenterY { get; init; }

    // Circular.
    public double MinRadius { get; init; }
    public double MaxRadius { get; init; }

    // Rectangular (half-widths) and elliptical (semi-axes).
    public double HalfWidthX { get; init; }
    public double HalfWidthY { get; init; }

    // Spider.
    public double ArmWidth { get; init; }
    public int ArmCount { get; init; }

    // User-defined polygon.
    public List<OpticsPointSettings>? Vertices { get; init; }
}

/// <summary>A point of a user-defined aperture, lens units.</summary>
public sealed record OpticsPointSettings
{
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>One numbered parameter (or extra-data value) of a surface.</summary>
public sealed record OpticsSurfaceParameterSettings
{
    public int Number { get; init; }
    public double Value { get; init; }
}

/// <summary>
/// One cell's flag: <see cref="Kind"/> "Variable", "Solve" or "Pickup" for the cell named by
/// <see cref="Column"/> ("Radius", "Thickness", "Conic", "SemiDiameter", "Par3", …). A solve
/// keeps its code and parameters as the editor set them (OP-29 evaluates them); a pickup names
/// its source surface (0-based, as surfaces are numbered), the source column, a scale and an
/// offset.
/// </summary>
public sealed record OpticsCellFlagSettings
{
    public string? Column { get; init; }
    public string? Kind { get; init; }
    public string? Solve { get; init; }
    public List<double>? SolveParameters { get; init; }
    public int PickupSurface { get; init; }
    public string? PickupColumn { get; init; }
    public double PickupScale { get; init; } = 1;
    public double PickupOffset { get; init; }

    // OP-99
    /// <summary>The macro file of a "ZPL Macro" solve (its name in the macro folder), or null.</summary>
    public string? Macro { get; init; }
}
