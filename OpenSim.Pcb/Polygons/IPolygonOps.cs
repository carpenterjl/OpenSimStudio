using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Polygons;

/// <summary>
/// Robust 2D polygon boolean/offset operations. The seam that keeps the polygon
/// engine (currently Clipper2) replaceable and its types out of the rest of the code.
/// </summary>
public interface IPolygonOps
{
    /// <summary>Union of a set of (possibly overlapping) rings into polygons with holes.</summary>
    IReadOnlyList<Polygon2> Union(IEnumerable<IReadOnlyList<Point2>> rings);

    /// <summary>Boolean subtraction: <paramref name="subjects"/> minus <paramref name="clips"/>.</summary>
    IReadOnlyList<Polygon2> Difference(IEnumerable<Polygon2> subjects, IEnumerable<IReadOnlyList<Point2>> clips);

    /// <summary>
    /// Boolean subtraction on raw rings, returning raw oriented rings (outers CCW,
    /// holes CW) ready to re-enter a later boolean — the staging primitive of the
    /// layer-image composition, which subtracts each dark batch's clear suffix without
    /// materializing intermediate polygons.
    /// </summary>
    IReadOnlyList<IReadOnlyList<Point2>> DifferenceRings(
        IEnumerable<IReadOnlyList<Point2>> subjects, IEnumerable<IReadOnlyList<Point2>> clips);

    /// <summary>Boolean intersection: the parts of <paramref name="subjects"/> covered by <paramref name="clips"/>.</summary>
    IReadOnlyList<Polygon2> Intersect(IEnumerable<Polygon2> subjects, IEnumerable<IReadOnlyList<Point2>> clips);

    /// <summary>
    /// Strokes an open polyline with a round pen of the given radius (round joins and
    /// end caps) — the shape a circular Gerber aperture draws.
    /// </summary>
    IReadOnlyList<IReadOnlyList<Point2>> StrokeOpenPath(IReadOnlyList<Point2> path, double radius);
}
