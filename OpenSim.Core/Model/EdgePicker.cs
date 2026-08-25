using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>One picked geometric edge: its id, how far along the click ray it sits, and how
/// far the ray passed from it.</summary>
public readonly record struct EdgePickHit(int EdgeId, double RayT, double Distance);

/// <summary>One picked geometric vertex.</summary>
public readonly record struct VertexPickHit(int VertexId, double RayT, double Distance);

/// <summary>
/// Picking a feature edge or vertex with a click ray, kept UI-free so it is testable — the
/// <c>NetPicker</c> precedent.
///
/// Edges are picked by RAY DISTANCE rather than by hit-testing geometry. The alternative
/// would be building a pickable tube around every edge and feeding it to the viewport's hit
/// tester, which means new 3D geometry, a model per edge for the reverse lookup, and a
/// thickness that has to be re-derived whenever the camera moves. The distance test needs
/// none of that: the caller converts a few pixels into a world radius (trivial under the
/// orthographic camera this app uses, where the scale is depth-independent) and everything
/// else is arithmetic over the edge segments already in the model.
///
/// Edges must win over the face behind them. A face always occupies more of the screen than
/// the line along its border, so a pick that resolved faces first could never select an edge
/// at all; the caller therefore tries this first and falls back to the face pick. What stops
/// that from selecting an edge on the far side of the body is <c>maxRayT</c> — the distance to
/// the nearest face the click actually hit.
/// </summary>
public static class EdgePicker
{
    /// <summary>
    /// The edge nearest along the ray whose distance from it is within
    /// <paramref name="tolerance"/>, or null when the ray passes near none.
    /// <para>
    /// Ties are broken by lowest edge id, then by lowest segment index, so a click that lands
    /// exactly between two edges resolves the same way every time rather than by enumeration
    /// order.
    /// </para>
    /// </summary>
    /// <param name="maxRayT">How far along the ray a hit may be — pass the distance to the
    /// nearest visible surface (plus the tolerance) so edges behind the solid are not picked
    /// through it, or <see cref="double.PositiveInfinity"/> to accept any depth.</param>
    public static EdgePickHit? PickEdge(BoundaryEdgeSet edges, IReadOnlyList<Vector3D> positions,
        Vector3D rayOrigin, Vector3D rayDirection, double tolerance,
        double maxRayT = double.PositiveInfinity)
    {
        double length = rayDirection.Length;
        if (length <= 0 || tolerance <= 0) return null;
        var direction = rayDirection / length;

        EdgePickHit? best = null;
        foreach (var edge in edges.Edges)
        {
            for (int i = 0; i < edge.Segments.Count; i++)
            {
                var segment = edge.Segments[i];
                if (segment.A >= positions.Count || segment.B >= positions.Count) continue;

                var (t, distance) = RayToSegment(rayOrigin, direction,
                    positions[segment.A], positions[segment.B]);
                if (distance > tolerance || t > maxRayT) continue;

                if (best is not { } current || t < current.RayT ||
                    (t == current.RayT && edge.Id < current.EdgeId))
                    best = new EdgePickHit(edge.Id, t, distance);
            }
        }
        return best;
    }

    /// <summary>
    /// The vertex nearest along the ray within <paramref name="tolerance"/>. Give this a
    /// slightly larger tolerance than the edge pick: a vertex is a point where several edges
    /// meet, so at an equal radius the edges would always win and corners would be unpickable.
    /// </summary>
    public static VertexPickHit? PickVertex(BoundaryEdgeSet edges, IReadOnlyList<Vector3D> positions,
        Vector3D rayOrigin, Vector3D rayDirection, double tolerance,
        double maxRayT = double.PositiveInfinity)
    {
        double length = rayDirection.Length;
        if (length <= 0 || tolerance <= 0) return null;
        var direction = rayDirection / length;

        VertexPickHit? best = null;
        foreach (var vertex in edges.Vertices)
        {
            if (vertex.NodeId >= positions.Count) continue;

            var offset = positions[vertex.NodeId] - rayOrigin;
            double t = Vector3D.Dot(offset, direction);
            if (t < 0 || t > maxRayT) continue;

            double distance = (offset - direction * t).Length;
            if (distance > tolerance) continue;

            if (best is not { } current || t < current.RayT ||
                (t == current.RayT && vertex.Id < current.VertexId))
                best = new VertexPickHit(vertex.Id, t, distance);
        }
        return best;
    }

    /// <summary>
    /// Closest approach between a ray and a segment: the ray parameter at that point, and the
    /// distance there. The ray parameter is CLAMPED to be non-negative — geometry behind the
    /// camera is not clicked — and the segment parameter to [0, 1], so an edge is picked near
    /// its own extent rather than near the infinite line through it.
    /// </summary>
    private static (double T, double Distance) RayToSegment(
        Vector3D origin, Vector3D direction, Vector3D a, Vector3D b)
    {
        var edge = b - a;
        var offset = a - origin;
        double edgeLengthSquared = Vector3D.Dot(edge, edge);

        if (edgeLengthSquared <= 0)
        {
            // A degenerate segment is a point: fall back to the point-to-ray distance rather
            // than dividing by its zero length.
            double tPoint = Math.Max(0, Vector3D.Dot(-offset, direction));
            return (tPoint, ((origin + direction * tPoint) - a).Length);
        }

        double edgeDotDirection = Vector3D.Dot(edge, direction);
        double denominator = edgeLengthSquared - edgeDotDirection * edgeDotDirection;
        double offsetDotDirection = Vector3D.Dot(offset, direction);
        double offsetDotEdge = Vector3D.Dot(offset, edge);

        double s;
        if (denominator <= 1e-12 * edgeLengthSquared)
        {
            // Parallel (or nearly): every point of the segment is the same distance from the
            // ray, so depth is the only thing left to choose by — take the end that comes
            // first along it, which is the one the viewer sees nearest.
            double atStart = Vector3D.Dot(a - origin, direction);
            double atEnd = Vector3D.Dot(b - origin, direction);
            s = atEnd < atStart ? 1 : 0;
        }
        else
        {
            s = (edgeDotDirection * offsetDotDirection - offsetDotEdge) / denominator;
            s = Math.Clamp(s, 0, 1);
        }

        var point = a + edge * s;
        double t = Math.Max(0, Vector3D.Dot(point - origin, direction));
        return (t, (point - (origin + direction * t)).Length);
    }
}
