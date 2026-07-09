using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// Restores a manifold boundary after the sliver cull. Culling a near-surface sliver
/// can leave the kept set touching itself along an edge shared by four (or more)
/// boundary faces — a pinch that breaks watertightness. The resolution is always to
/// remove more tets (never to re-admit a culled sliver: a near-degenerate element in
/// the stiffness matrix costs far more accuracy through CG conditioning than the
/// removal of a sound tet costs in volume). Removing a tet that owns two boundary
/// faces at the pinched edge lowers that edge's boundary-face count by two.
/// </summary>
public static class BoundaryPinchResolver
{
    public static List<(int A, int B, int C, int D)> Resolve(
        List<(int A, int B, int C, int D)> kept, IReadOnlyList<Vector3D> points)
    {
        var alive = new bool[kept.Count];
        Array.Fill(alive, true);

        // One removal per iteration (the face/edge maps go stale after a removal), so
        // the cap bounds total removals. Pinches are rare — dozens at worst — and each
        // iteration is O(kept), so a generous cap is still cheap.
        for (int iteration = 0; iteration < 256; iteration++)
        {
            // Boundary faces (used once) and their edge use, over the live set.
            var faceUse = new Dictionary<(int, int, int), int>();
            for (int i = 0; i < kept.Count; i++)
            {
                if (!alive[i]) continue;
                foreach (var face in Faces(kept[i]))
                    faceUse[face] = faceUse.GetValueOrDefault(face) + 1;
            }
            var edgeUse = new Dictionary<(int, int), int>();
            foreach (var ((a, b, c), use) in faceUse)
            {
                if (use != 1) continue;
                void Count(int u, int v)
                {
                    var key = u < v ? (u, v) : (v, u);
                    edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
                }
                Count(a, b); Count(b, c); Count(a, c);
            }
            var pinched = new List<(int U, int V)>();
            foreach (var (edge, use) in edgeUse)
                if (use > 2)
                    pinched.Add(edge);
            if (pinched.Count == 0)
                break;

            bool removedAny = false;
            foreach (var (u, v) in pinched)
            {
                // Prefer the worst-quality live tet holding TWO boundary faces at the
                // edge (its removal reduces the edge's count by two); fall back to the
                // worst single-boundary-face tet, which reshapes the neighborhood for
                // the next iteration without changing this edge's count.
                int bestBoth = -1, bestOne = -1;
                double bestBothQ = double.MaxValue, bestOneQ = double.MaxValue;
                for (int i = 0; i < kept.Count; i++)
                {
                    if (!alive[i]) continue;
                    var t = kept[i];
                    if (!ContainsEdge(t, u, v)) continue;
                    int boundaryFacesAtEdge = 0;
                    foreach (var face in Faces(t))
                        if (ContainsBoth(face, u, v) && faceUse[face] == 1)
                            boundaryFacesAtEdge++;
                    if (boundaryFacesAtEdge == 0) continue;
                    double q = MeshQuality.RadiusRatio(
                        points[t.A], points[t.B], points[t.C], points[t.D]);
                    if (boundaryFacesAtEdge == 2 && q < bestBothQ) { bestBothQ = q; bestBoth = i; }
                    if (boundaryFacesAtEdge == 1 && q < bestOneQ) { bestOneQ = q; bestOne = i; }
                }
                int victim = bestBoth >= 0 ? bestBoth : bestOne;
                if (victim < 0) continue;
                alive[victim] = false;
                removedAny = true;
                // Face/edge maps are stale now; the outer loop rebuilds them.
                break;
            }
            if (!removedAny)
                break;   // unresolvable with removals; leave as-is
        }

        var result = new List<(int, int, int, int)>(kept.Count);
        for (int i = 0; i < kept.Count; i++)
            if (alive[i])
                result.Add(kept[i]);
        return result;
    }

    private static (int, int, int)[] Faces((int A, int B, int C, int D) t) => new[]
    {
        Sorted(t.B, t.C, t.D), Sorted(t.A, t.C, t.D), Sorted(t.A, t.B, t.D), Sorted(t.A, t.B, t.C)
    };

    private static bool ContainsEdge((int A, int B, int C, int D) t, int u, int v) =>
        (t.A == u || t.B == u || t.C == u || t.D == u) &&
        (t.A == v || t.B == v || t.C == v || t.D == v);

    private static bool ContainsBoth((int, int, int) face, int u, int v) =>
        (face.Item1 == u || face.Item2 == u || face.Item3 == u) &&
        (face.Item1 == v || face.Item2 == v || face.Item3 == v);

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }
}
