namespace OpenSim.Optics.Sequential;

/// <summary>
/// OpticStudio's Coordinate Return on a coordinate break (help page "Tilt/Decenter", OpticStudio
/// 2025 R2): which of the break's values are solved so that the coordinate system returns to that
/// of a prior surface (docs/Optics-Conventions.md §15).
/// </summary>
public enum CoordinateReturnMode
{
    /// <summary>The break's values are used as entered.</summary>
    None = 0,

    /// <summary>The three tilts are solved so that the break's axes are those of the target
    /// surface; the decenters and the thickness are used as entered.</summary>
    OrientationOnly = 1,

    /// <summary>The tilts and the x and y decenters are solved: the break's axes are the
    /// target's and its vertex lies on the target's z axis. The thickness is used as entered.</summary>
    OrientationXY = 2,

    /// <summary>The tilts, the decenters and the break's thickness are solved, so the surface
    /// after the break has the target surface's orientation and vertex.</summary>
    OrientationXYZ = 3,
}

/// <summary>
/// A coordinate-break surface (OpticStudio's Coordinate Break, help page "Coordinate Break",
/// OpticStudio 2025 R2): a dummy plane that places a new coordinate system relative to the one
/// of the surface before it, by <see cref="Values"/> (decenters, tilts and the order flag,
/// docs/Optics-Conventions.md §4), its thickness then applied last along the new z. It cannot
/// be the object, the image or a mirror; its medium is the one before it; it has no curvature,
/// shape, aperture or surface tilt of its own.
/// <para>
/// With a <see cref="Return"/> other than <see cref="CoordinateReturnMode.None"/> some of the
/// values are solved instead (<see cref="SurfacePlacement.ResolvedBreak"/>).
/// </para>
/// </summary>
public sealed record CoordinateBreak
{
    /// <summary>The decenters (meters), tilts (degrees) and order.</summary>
    public TiltDecenter Values { get; init; }

    /// <summary>The coordinate return, if any.</summary>
    public CoordinateReturnMode Return { get; init; }

    /// <summary>The prior surface the return goes back to (1 or later, before this one).
    /// Ignored when <see cref="Return"/> is <see cref="CoordinateReturnMode.None"/>.</summary>
    public int ReturnToSurface { get; init; }

    public CoordinateBreak() { }

    public CoordinateBreak(TiltDecenter values) => Values = values;

    /// <summary>A break whose values are all solved to return to surface
    /// <paramref name="surface"/>, in the given order.</summary>
    public static CoordinateBreak ReturnTo(int surface, CoordinateReturnMode mode = CoordinateReturnMode.OrientationXYZ,
        TiltDecenterOrder order = TiltDecenterOrder.DecenterThenTilt) => new()
    {
        Values = TiltDecenter.None with { Order = order },
        Return = mode,
        ReturnToSurface = surface,
    };
}

/// <summary>Where a surface's "after" tilt and decenter comes from (help page "Tilt/Decenter",
/// OpticStudio 2025 R2, the after-surface options a to e).</summary>
public enum TiltAfterKind
{
    /// <summary>a) The values given in <see cref="SurfaceTiltAfter.Values"/>.</summary>
    Explicit = 0,

    /// <summary>b) The same values and order as this surface's "before" (a fold mirror).</summary>
    PickUpThisSurface = 1,

    /// <summary>c) This surface's "before" reversed: the other order, every value negated
    /// (<see cref="TiltDecenter.Reversed"/>).</summary>
    ReverseThisSurface = 2,

    /// <summary>d) The "before" values and order of the prior surface <see cref="SurfaceTiltAfter.Surface"/>.</summary>
    PickUpSurface = 3,

    /// <summary>e) The "before" of the prior surface <see cref="SurfaceTiltAfter.Surface"/>
    /// reversed (the end of a decentered group).</summary>
    ReverseSurface = 4,
}

/// <summary>A surface's tilt and decenter after the surface (docs/Optics-Conventions.md §15).</summary>
public sealed record SurfaceTiltAfter
{
    public TiltAfterKind Kind { get; init; }

    /// <summary>The values, for <see cref="TiltAfterKind.Explicit"/> only.</summary>
    public TiltDecenter Values { get; init; }

    /// <summary>The prior surface, for <see cref="TiltAfterKind.PickUpSurface"/> and
    /// <see cref="TiltAfterKind.ReverseSurface"/> only.</summary>
    public int Surface { get; init; }

    public static SurfaceTiltAfter Explicit(TiltDecenter values) => new() { Kind = TiltAfterKind.Explicit, Values = values };
    public static SurfaceTiltAfter PickUpThis { get; } = new() { Kind = TiltAfterKind.PickUpThisSurface };
    public static SurfaceTiltAfter ReverseThis { get; } = new() { Kind = TiltAfterKind.ReverseThisSurface };
    public static SurfaceTiltAfter PickUp(int surface) => new() { Kind = TiltAfterKind.PickUpSurface, Surface = surface };
    public static SurfaceTiltAfter Reverse(int surface) => new() { Kind = TiltAfterKind.ReverseSurface, Surface = surface };
}
