using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// Fast approximate distance-to-surface queries against a triangle mesh with roughly
/// uniform triangle sizes (the refined surface). Exact point-triangle distance is
/// evaluated for candidate triangles found through a kd-tree over triangle centroids.
/// </summary>
public sealed class SurfaceDistanceField
{
    private readonly TriangleMesh _mesh;
    private readonly KdTree _centroids;
    private readonly double _searchRadius;

    public SurfaceDistanceField(TriangleMesh mesh)
    {
        _mesh = mesh;
        var centroids = new List<Vector3D>(mesh.Triangles.Count);
        double maxEdge = 0;
        foreach (var t in mesh.Triangles)
        {
            var a = mesh.Vertices[t.A];
            var b = mesh.Vertices[t.B];
            var c = mesh.Vertices[t.C];
            centroids.Add((a + b + c) / 3.0);
            maxEdge = Math.Max(maxEdge, Vector3D.Distance(a, b));
            maxEdge = Math.Max(maxEdge, Vector3D.Distance(b, c));
            maxEdge = Math.Max(maxEdge, Vector3D.Distance(c, a));
        }
        _centroids = new KdTree(centroids);
        _searchRadius = maxEdge * 1.5;
    }

    /// <summary>
    /// Distance from <paramref name="p"/> to the surface, exact when below the search
    /// radius; otherwise a value at least the search radius is returned.
    /// </summary>
    public double Distance(Vector3D p)
    {
        var candidates = _centroids.RadiusSearch(p, _searchRadius);
        if (candidates.Count == 0)
            return double.MaxValue;

        double best = double.MaxValue;
        foreach (int i in candidates)
        {
            var t = _mesh.Triangles[i];
            double d = PointTriangleDistance(p,
                _mesh.Vertices[t.A], _mesh.Vertices[t.B], _mesh.Vertices[t.C]);
            best = Math.Min(best, d);
        }
        return best;
    }

    /// <summary>Exact distance from a point to a triangle (interior, edge or vertex region).</summary>
    public static double PointTriangleDistance(Vector3D p, Vector3D a, Vector3D b, Vector3D c)
    {
        // Ericson, "Real-Time Collision Detection", closest point on triangle.
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        double d1 = Vector3D.Dot(ab, ap);
        double d2 = Vector3D.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return Vector3D.Distance(p, a);

        var bp = p - b;
        double d3 = Vector3D.Dot(ab, bp);
        double d4 = Vector3D.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return Vector3D.Distance(p, b);

        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            double t = d1 / (d1 - d3);
            return Vector3D.Distance(p, a + ab * t);
        }

        var cp = p - c;
        double d5 = Vector3D.Dot(ab, cp);
        double d6 = Vector3D.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return Vector3D.Distance(p, c);

        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            double t = d2 / (d2 - d6);
            return Vector3D.Distance(p, a + ac * t);
        }

        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            double t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return Vector3D.Distance(p, b + (c - b) * t);
        }

        double denom = 1.0 / (va + vb + vc);
        double v = vb * denom;
        double w = vc * denom;
        return Vector3D.Distance(p, a + ab * v + ac * w);
    }
}
