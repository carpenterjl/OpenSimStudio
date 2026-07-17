using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// An IPC-2581 <c>Xform</c> placement: rotation (degrees, counter-clockwise) applied
/// FIRST, then mirror (about the y-axis through the anchor, i.e. x → −x), then the
/// translation to <see cref="Offset"/>. The rotate-before-mirror order is pinned
/// EMPIRICALLY by the component-instantiation transform oracle in the tests: on a real
/// Cadence board it matches 1689/1689 instantiated pads to placed copper, while
/// mirror-before-rotate matches only 523/1417 on the mirrored (bottom) side and a
/// y-axis-flip variant only 56/1417 — the measurement is unambiguous.
/// </summary>
public readonly record struct Ipc2581Transform(Point2 Offset, double RotationDeg, bool Mirror)
{
    public Point2 Apply(Point2 local)
    {
        if (Math.Abs(RotationDeg % 360) >= 1e-9)
        {
            double a = RotationDeg * Math.PI / 180;
            double cos = Math.Cos(a), sin = Math.Sin(a);
            local = new Point2(local.X * cos - local.Y * sin, local.X * sin + local.Y * cos);
        }
        if (Mirror) local = new Point2(-local.X, local.Y);
        return local + Offset;
    }

    /// <summary>Transforms a ring, restoring the original winding when the mirror flips
    /// it — downstream consumers keep the CCW-outer convention either way.</summary>
    public IReadOnlyList<Point2> Apply(IReadOnlyList<Point2> ring)
    {
        var placed = new Point2[ring.Count];
        for (int i = 0; i < ring.Count; i++) placed[i] = Apply(ring[i]);
        if (Mirror) Array.Reverse(placed);
        return placed;
    }

    /// <summary>Transforms an open path (a stroked centerline) — no winding to preserve.</summary>
    public IReadOnlyList<Point2> ApplyPath(IReadOnlyList<Point2> path)
    {
        var placed = new Point2[path.Count];
        for (int i = 0; i < path.Count; i++) placed[i] = Apply(path[i]);
        return placed;
    }

    public Polygon2 Apply(Polygon2 polygon)
    {
        var outer = Apply(polygon.Outer);
        if (polygon.Holes.Count == 0) return new Polygon2(outer);
        return new Polygon2(outer, polygon.Holes.Select(Apply).ToList());
    }
}
