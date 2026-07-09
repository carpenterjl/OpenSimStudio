using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// Point-in-solid classification against a closed triangle mesh by ray-crossing parity.
/// Three rays with irrational-ish directions vote, which makes hits on edges/vertices
/// (where a single parity count can miscount) statistically harmless.
/// </summary>
public sealed class SolidClassifier
{
    private static readonly Vector3D[] RayDirections =
    {
        new Vector3D(0.5773502691896258, 0.5773502691896257, 0.5773502691896259).Normalized(),
        new Vector3D(-0.2672612419124244, 0.5345224838248488, 0.8017837257372732).Normalized(),
        new Vector3D(0.8451542547285166, -0.1690308509457033, -0.5070925528371099).Normalized()
    };

    private readonly TriangleMesh _mesh;
    private readonly TriangleAabbTree _tree;
    private readonly List<int> _candidates = new();

    public SolidClassifier(TriangleMesh mesh)
    {
        _mesh = mesh;
        // The tree only prunes which triangles reach the exact ray test below —
        // crossing counts are identical to a brute-force scan by construction.
        _tree = new TriangleAabbTree(mesh.Vertices,
            mesh.Triangles.Select(t => (t.A, t.B, t.C)).ToList());
    }

    public bool IsInside(Vector3D point)
    {
        int votes = 0;
        foreach (var dir in RayDirections)
        {
            if (CountCrossings(point, dir) % 2 == 1)
                votes++;
        }
        return votes >= 2;
    }

    private int CountCrossings(Vector3D origin, Vector3D dir)
    {
        _candidates.Clear();
        _tree.CollectRayCandidates(origin, dir, _candidates);
        int crossings = 0;
        foreach (int index in _candidates)
        {
            var tri = _mesh.Triangles[index];
            if (RayIntersectsTriangle(origin, dir,
                    _mesh.Vertices[tri.A], _mesh.Vertices[tri.B], _mesh.Vertices[tri.C]))
                crossings++;
        }
        return crossings;
    }

    /// <summary>Möller–Trumbore ray/triangle test for t &gt; 0.</summary>
    private static bool RayIntersectsTriangle(Vector3D origin, Vector3D dir,
        Vector3D a, Vector3D b, Vector3D c)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3D.Cross(dir, e2);
        double det = Vector3D.Dot(e1, p);
        if (Math.Abs(det) < 1e-14) return false; // ray parallel to triangle plane

        double invDet = 1.0 / det;
        var s = origin - a;
        double u = Vector3D.Dot(s, p) * invDet;
        if (u < 0 || u > 1) return false;

        var q = Vector3D.Cross(s, e1);
        double v = Vector3D.Dot(dir, q) * invDet;
        if (v < 0 || u + v > 1) return false;

        double t = Vector3D.Dot(e2, q) * invDet;
        return t > 1e-12;
    }
}
