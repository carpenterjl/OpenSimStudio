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
        // ⇒ 2·(b-a)·x = |b|²-|a|² etc. — a 3x3 linear system.
        var ba = b - a; var ca = c - a; var da = d - a;
        double[,] m =
        {
            { ba.X, ba.Y, ba.Z },
            { ca.X, ca.Y, ca.Z },
            { da.X, da.Y, da.Z }
        };
        double[] rhs =
        {
            0.5 * (b.LengthSquared - a.LengthSquared),
            0.5 * (c.LengthSquared - a.LengthSquared),
            0.5 * (d.LengthSquared - a.LengthSquared)
        };
        double det = Det3(m);
        if (Math.Abs(det) < 1e-300) return null;

        return new Vector3D(
            Det3Replaced(m, rhs, 0) / det,
            Det3Replaced(m, rhs, 1) / det,
            Det3Replaced(m, rhs, 2) / det);
    }

    private static double Circumradius(Vector3D a, Vector3D b, Vector3D c, Vector3D d)
    {
        var center = Circumcenter(a, b, c, d);
        return center is null ? 0 : Vector3D.Distance(center.Value, a);
    }

    private static double Det3(double[,] m) =>
        m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
      - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
      + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

    private static double Det3Replaced(double[,] m, double[] rhs, int column)
    {
        var copy = (double[,])m.Clone();
        for (int r = 0; r < 3; r++) copy[r, column] = rhs[r];
        return Det3(copy);
    }

    public static MeshStatistics Compute(FeMesh mesh)
    {
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
}
