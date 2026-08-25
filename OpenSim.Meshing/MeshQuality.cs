using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>Aggregate quality statistics for a tetrahedral mesh.</summary>
public sealed record MeshStatistics(
    int NodeCount,
    int ElementCount,
    double TotalVolume,
    double MinQuality,
    double AverageQuality,
    double MinEdgeLength,
    double MaxEdgeLength);

/// <summary>Tetrahedron quality metrics.</summary>
public static class MeshQuality
{
    /// <summary>
    /// Radius ratio quality: 3·(inradius/circumradius), scaled so a regular tetrahedron
    /// scores 1 and degenerate slivers approach 0.
    /// </summary>
    public static double RadiusRatio(Vector3D a, Vector3D b, Vector3D c, Vector3D d)
    {
        double volume = Math.Abs(GeometricPredicates.Orient3D(a, b, c, d)) / 6.0;
        if (volume <= 0) return 0;

        double areaSum =
            TriangleArea(b, c, d) + TriangleArea(a, c, d) +
            TriangleArea(a, b, d) + TriangleArea(a, b, c);
        double inradius = 3.0 * volume / areaSum;
        double circumradius = Circumradius(a, b, c, d);
        if (circumradius <= 0) return 0;
        return 3.0 * inradius / circumradius;
    }

    private static double TriangleArea(Vector3D p, Vector3D q, Vector3D r) =>
        0.5 * Vector3D.Cross(q - p, r - p).Length;

    /// <summary>
    /// Circumcenter of a tetrahedron (the point equidistant from all four vertices),
    /// or null when the vertices are too degenerate to define one. Used by the
    /// quality-driven refiner as the Steiner-point location for bad tets.
    /// </summary>
    public static Vector3D? Circumcenter(Vector3D a, Vector3D b, Vector3D c, Vector3D d)
    {
        // Solve for the circumcenter x: |x-a|² = |x-b|² = |x-c|² = |x-d|²
        // ⇒ 2·(b-a)·x = |b|²-|a|² etc. — a 3x3 linear system, by Cramer's rule.
        //
        // Held in locals rather than arrays: this is the mesher's hottest inner loop (the
        // smoother evaluates it twice per incident element per node per pass), and the array
        // form allocated a matrix, a right-hand side and three clones of the matrix on every
        // call. The determinant expansion below is the same expression on the same operands
        // in the same order, so the numbers are unchanged to the last bit.
        var ba = b - a; var ca = c - a; var da = d - a;
        double r0 = 0.5 * (b.LengthSquared - a.LengthSquared);
        double r1 = 0.5 * (c.LengthSquared - a.LengthSquared);
        double r2 = 0.5 * (d.LengthSquared - a.LengthSquared);

        double det = Det3(ba.X, ba.Y, ba.Z, ca.X, ca.Y, ca.Z, da.X, da.Y, da.Z);
        if (Math.Abs(det) < 1e-300) return null;

        return new Vector3D(
            Det3(r0, ba.Y, ba.Z, r1, ca.Y, ca.Z, r2, da.Y, da.Z) / det,
            Det3(ba.X, r0, ba.Z, ca.X, r1, ca.Z, da.X, r2, da.Z) / det,
            Det3(ba.X, ba.Y, r0, ca.X, ca.Y, r1, da.X, da.Y, r2) / det);
    }

    private static double Circumradius(Vector3D a, Vector3D b, Vector3D c, Vector3D d)
    {
        var center = Circumcenter(a, b, c, d);
        return center is null ? 0 : Vector3D.Distance(center.Value, a);
    }

    /// <summary>Determinant of a 3x3 matrix given row-major, expanded along the first row.</summary>
    private static double Det3(
        double m00, double m01, double m02,
        double m10, double m11, double m12,
        double m20, double m21, double m22) =>
        m00 * (m11 * m22 - m12 * m21)
      - m01 * (m10 * m22 - m12 * m20)
      + m02 * (m10 * m21 - m11 * m20);

    public static MeshStatistics Compute(FeMesh mesh)
    {
        if (mesh.HexElements is not null) return ComputeHex(mesh);

        double minQ = double.PositiveInfinity, sumQ = 0;
        double minEdge = double.PositiveInfinity, maxEdge = 0;
        Span<double> edges = stackalloc double[6];
        foreach (var e in mesh.Elements)
        {
            var a = mesh.Nodes[e.N0]; var b = mesh.Nodes[e.N1];
            var c = mesh.Nodes[e.N2]; var d = mesh.Nodes[e.N3];
            double q = RadiusRatio(a, b, c, d);
            minQ = Math.Min(minQ, q);
            sumQ += q;

            edges[0] = Vector3D.Distance(a, b); edges[1] = Vector3D.Distance(a, c);
            edges[2] = Vector3D.Distance(a, d); edges[3] = Vector3D.Distance(b, c);
            edges[4] = Vector3D.Distance(b, d); edges[5] = Vector3D.Distance(c, d);
            foreach (double len in edges)
            {
                minEdge = Math.Min(minEdge, len);
                maxEdge = Math.Max(maxEdge, len);
            }
        }
        int n = mesh.ElementCount;
        return new MeshStatistics(mesh.NodeCount, n, mesh.TotalVolume(),
            n > 0 ? minQ : 0, n > 0 ? sumQ / n : 0,
            n > 0 ? minEdge : 0, n > 0 ? maxEdge : 0);
    }

    /// <summary>
    /// Quality of a hexahedral mesh as the cell EDGE RATIO — the shortest edge over the
    /// longest, so a cube reads 1 and a sliver reads near 0.
    /// <para>
    /// Deliberately not the tetrahedral radius ratio. That metric measures how close a
    /// tetrahedron is to regular, and applying it to a brick would report a number with no
    /// meaning on the same scale as the tetrahedral one the UI shows beside it. What actually
    /// degrades a mapped hexahedron is its aspect ratio, which is what this reports.
    /// </para>
    /// </summary>
    private static MeshStatistics ComputeHex(FeMesh mesh)
    {
        // The twelve edges of a hexahedron, as indices into the canonical corner order.
        ReadOnlySpan<int> edgeA = stackalloc int[] { 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3 };
        ReadOnlySpan<int> edgeB = stackalloc int[] { 1, 2, 3, 0, 5, 6, 7, 4, 4, 5, 6, 7 };

        double minQ = double.PositiveInfinity, sumQ = 0;
        double minEdge = double.PositiveInfinity, maxEdge = 0;
        Span<int> corner = stackalloc int[8];

        foreach (var h in mesh.HexElements!)
        {
            corner[0] = h.N0; corner[1] = h.N1; corner[2] = h.N2; corner[3] = h.N3;
            corner[4] = h.N4; corner[5] = h.N5; corner[6] = h.N6; corner[7] = h.N7;

            double shortest = double.PositiveInfinity, longest = 0;
            for (int e = 0; e < 12; e++)
            {
                double len = Vector3D.Distance(mesh.Nodes[corner[edgeA[e]]], mesh.Nodes[corner[edgeB[e]]]);
                shortest = Math.Min(shortest, len);
                longest = Math.Max(longest, len);
                minEdge = Math.Min(minEdge, len);
                maxEdge = Math.Max(maxEdge, len);
            }

            double q = longest > 0 ? shortest / longest : 0;
            minQ = Math.Min(minQ, q);
            sumQ += q;
        }

        int n = mesh.ElementCount;
        return new MeshStatistics(mesh.NodeCount, n, mesh.TotalVolume(),
            n > 0 ? minQ : 0, n > 0 ? sumQ / n : 0,
            n > 0 ? minEdge : 0, n > 0 ? maxEdge : 0);
    }
}
