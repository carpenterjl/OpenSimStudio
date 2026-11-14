using OpenSim.Core.Geometry2D;

namespace OpenSim.Optics.Layout;

/// <summary>What a line of the 2-D cross-section is.</summary>
public enum LayoutLineKind
{
    /// <summary>The closed outline of a lens body: front face, edge, back face, edge.</summary>
    LensOutline = 0,

    /// <summary>A mirror: its profile, closed through the substrate's back when it has one.</summary>
    Mirror = 1,

    /// <summary>The profile of a surface that bounds no lens body (a dummy, the image plane).</summary>
    Surface = 2,

    /// <summary>One blade of the aperture drawn at the stop, from the clear edge outward.</summary>
    Aperture = 3,

    Ray = 4,

    /// <summary>A direction arrow on a ray.</summary>
    Fletch = 5,
}

/// <summary>
/// One line of the cross-section. Points are (z, y) of the global frame in meters: X is the
/// horizontal axis of the drawing (the direction light starts out in), Y the vertical.
/// <see cref="ColorKey"/> is the field or wavelength index for rays and fletches, and the first
/// surface index otherwise; <see cref="Surface"/> is the surface the line belongs to (−1 for
/// rays).
/// </summary>
public sealed record LayoutPolyline(IReadOnlyList<Point2> Points, LayoutLineKind Kind, int ColorKey,
    bool Closed, int Surface);
