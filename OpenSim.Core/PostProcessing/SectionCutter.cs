using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Core.PostProcessing;

/// <summary>A triangle of the section cut, with the interpolated scalar at each vertex.</summary>
public sealed record CutTriangle(
    Vector3D P0, Vector3D P1, Vector3D P2, double S0, double S1, double S2);

/// <summary>
/// Section-plane cutting by marching tetrahedra over the full element list (the mesh's
/// interior — this is why it operates on <see cref="FeMesh"/> in Core rather than on
/// the rendered skin). Produces scalar-interpolated triangles of the cut cross-section
/// and the visibility filter for the clipped boundary skin.
/// </summary>
public static class SectionCutter
{
    /// <summary>
    /// Cuts every element crossing the plane. Corner signed distances exactly on the
    /// plane are ε-nudged to the positive side so only two case families exist:
    /// a 3–1 vertex split (one triangle) and a 2–2 split (a quad emitted as two
    /// triangles in the ac→ad→bd→bc order whose consecutive points share a tet
    /// vertex — any other order can produce a bow-tie).
    /// </summary>
    public static List<CutTriangle> Cut(FeMesh mesh, SectionPlane plane,
        IReadOnlyList<double> nodalScalars, IReadOnlyList<Vector3D>? displacement, double deformScale)
    {
        var cut = new List<CutTriangle>();

        // ε from the mesh extent along the plane axis.
        double minC = double.MaxValue, maxC = double.MinValue;
        foreach (var n in mesh.Nodes)
        {
            double d = plane.SignedDistance(n);
            minC = Math.Min(minC, d);
            maxC = Math.Max(maxC, d);
        }
        double epsilon = 1e-12 * Math.Max(maxC - minC, 1e-30);

        Vector3D Deformed(int n) => displacement is null
            ? mesh.Nodes[n]
            : mesh.Nodes[n] + displacement[n] * deformScale;

        var corners = new int[4];
        var delta = new double[4];
        var pos = new Vector3D[4];
        var below = new int[4];
        var above = new int[4];

        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var el = mesh.Elements[e];
            corners[0] = el.N0; corners[1] = el.N1; corners[2] = el.N2; corners[3] = el.N3;

            int belowCount = 0, aboveCount = 0;
            for (int i = 0; i < 4; i++)
            {
                pos[i] = Deformed(corners[i]);
                double d = plane.SignedDistance(pos[i]);
                delta[i] = d == 0 ? epsilon : d;
                if (delta[i] < 0) below[belowCount++] = i;
                else above[aboveCount++] = i;
            }
            if (belowCount == 0 || belowCount == 4)
                continue;

            (Vector3D P, double S) Intersect(int i, int j)
            {
                double f = delta[i] / (delta[i] - delta[j]);
                var point = pos[i] + (pos[j] - pos[i]) * f;
                double si = nodalScalars[corners[i]];
                double sj = nodalScalars[corners[j]];
                return (point, si + (sj - si) * f);
            }

            if (belowCount == 1 || belowCount == 3)
            {
                // 3–1 split: the lone vertex against the other three → one triangle.
                int lone = belowCount == 1 ? below[0] : above[0];
                var rest = new int[3];
                int r = 0;
                for (int i = 0; i < 4; i++)
                    if (i != lone)
                        rest[r++] = i;
                var (q0, s0) = Intersect(lone, rest[0]);
                var (q1, s1) = Intersect(lone, rest[1]);
                var (q2, s2) = Intersect(lone, rest[2]);
                cut.Add(new CutTriangle(q0, q1, q2, s0, s1, s2));
            }
            else
            {
                // 2–2 split: {a,b} below, {c,d} above → quad ac, ad, bd, bc.
                int a = below[0], b = below[1], c = above[0], d = above[1];
                var (ac, sac) = Intersect(a, c);
                var (ad, sad) = Intersect(a, d);
                var (bd, sbd) = Intersect(b, d);
                var (bc, sbc) = Intersect(b, c);
                cut.Add(new CutTriangle(ac, ad, bd, sac, sad, sbd));
                cut.Add(new CutTriangle(ac, bd, bc, sac, sbd, sbc));
            }
        }
        return cut;
    }

    /// <summary>
    /// Whole-triangle skin filter for the clipped surface: a boundary triangle stays
    /// visible when any vertex is on the kept (negative) side. The resulting ragged
    /// edge is covered by the colored cut face.
    /// </summary>
    public static bool IsTriangleVisible(FeMesh mesh, BoundaryTriangle t, SectionPlane plane,
        IReadOnlyList<Vector3D>? displacement, double deformScale)
    {
        Vector3D Deformed(int n) => displacement is null
            ? mesh.Nodes[n]
            : mesh.Nodes[n] + displacement[n] * deformScale;
        return plane.SignedDistance(Deformed(t.A)) <= 0
            || plane.SignedDistance(Deformed(t.B)) <= 0
            || plane.SignedDistance(Deformed(t.C)) <= 0;
    }
}
