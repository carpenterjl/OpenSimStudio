namespace OpenSim.Optics.Sequential.Composite;

/// <summary>
/// One composite add-on of a surface (OP-32, docs/Optics-Conventions.md §58): a surface shape
/// whose sag is added to the base surface's, placed in the base surface's frame by
/// <see cref="Placement"/> (OpticStudio 2025 R2 help, "Composite Surface": "Decenter X: The
/// X-decenter of the surface with respect to the Composite Base", and so for Decenter Y and Tilt
/// X, Y, Z). Lengths are meters, curvature 1/m, tilts degrees.
/// <para>
/// The shape is given like a surface's: <see cref="Shape"/> null is the Standard conic of
/// <see cref="Curvature"/> and <see cref="Conic"/>; a typed shape reads them as its base conic
/// where its type uses them. <see cref="IsIgnored"/> is the help's "Ignore This Surface" on an
/// add-on: "The sag of ignored Composite Add-On surfaces are no longer added to the sag of their
/// respective Base" (help, "Ignore Composite Add-ons").
/// </para>
/// </summary>
public sealed record CompositeAddOn
{
    /// <summary>Vertex curvature of the add-on's own conic, 1/m.</summary>
    public double Curvature { get; init; }

    /// <summary>Conic constant of the add-on's own conic.</summary>
    public double Conic { get; init; }

    /// <summary>The add-on's shape, or null for the Standard conic.</summary>
    public SurfaceShape? Shape { get; init; }

    /// <summary>Decenter then tilt of the add-on's frame in the base surface's frame
    /// (<see cref="TiltDecenter"/>, order decenter then tilt only).</summary>
    public TiltDecenter Placement { get; init; }

    /// <summary>True when the add-on's sag is not added (OpticStudio's "Ignore This Surface").</summary>
    public bool IsIgnored { get; init; }

    /// <summary>The add-on's own clear semi-diameter, meters, which the types that normalise to
    /// it read (Irregular's r_max); null takes the base surface's.</summary>
    public double? SemiDiameter { get; init; }

    /// <summary>The add-on's comment.</summary>
    public string Comment { get; init; } = "";

    /// <summary>The add-on's type name as the editor lists it.</summary>
    public string TypeName => Shape?.TypeName ?? SavedSurfaces.StandardType;

    /// <summary>True when the placement holds a tilt (any of the three).</summary>
    public bool IsTilted => Placement.TiltXDegrees != 0 || Placement.TiltYDegrees != 0 || Placement.TiltZDegrees != 0;

    /// <summary>True when the placement holds a decenter or a tilt.</summary>
    public bool IsPlaced => IsTilted || Placement.DecenterX != 0 || Placement.DecenterY != 0;

    /// <summary>True for a Standard plane (no shape, zero curvature): its sag is zero
    /// everywhere, and tilted it lies on its own reference plane, so it adds nothing in any
    /// placement (§58).</summary>
    public bool IsZeroSag => Shape is null && Curvature == 0;

    /// <summary>The add-on as a surface its shape can be evaluated on: its curvature, conic and
    /// shape, and the semi-diameter it reads (its own, else <paramref name="baseSemiDiameter"/>).</summary>
    public SequentialSurface AsSurface(double? baseSemiDiameter) => new()
    {
        Curvature = Curvature,
        Conic = Conic,
        Shape = Shape,
        SemiDiameter = SemiDiameter ?? baseSemiDiameter,
        Comment = Comment,
    };
}
