using OpenSim.Core.Numerics;

namespace OpenSim.Core.Model;

/// <summary>A triangle with vertex indices into the owning mesh.</summary>
public readonly record struct Triangle(int A, int B, int C);

/// <summary>
/// A closed surface triangle mesh describing body geometry (imported STL or generated
/// primitive). Each triangle carries a face id so boundary conditions can target
/// logical faces ("the left end", "the top surface") rather than raw triangles.
/// </summary>
public sealed class TriangleMesh
{
    public IReadOnlyList<Vector3D> Vertices { get; }
    public IReadOnlyList<Triangle> Triangles { get; }

    /// <summary>Face id per triangle, parallel to <see cref="Triangles"/>.</summary>
    public IReadOnlyList<int> TriangleFaceIds { get; }

    public TriangleMesh(IReadOnlyList<Vector3D> vertices, IReadOnlyList<Triangle> triangles, IReadOnlyList<int> triangleFaceIds)
    {
        if (triangleFaceIds.Count != triangles.Count)
            throw new ArgumentException("One face id per triangle is required.", nameof(triangleFaceIds));
        Vertices = vertices;
        Triangles = triangles;
        TriangleFaceIds = triangleFaceIds;
    }

    public int FaceCount => TriangleFaceIds.Count == 0 ? 0 : TriangleFaceIds.Max() + 1;

    public Aabb Bounds => Aabb.FromPoints(Vertices);

    /// <summary>Outward normal of a triangle assuming counter-clockwise winding.</summary>
    public Vector3D TriangleNormal(int triangleIndex)
    {
        var t = Triangles[triangleIndex];
        var n = Vector3D.Cross(Vertices[t.B] - Vertices[t.A], Vertices[t.C] - Vertices[t.A]);
        return n.Normalized();
    }

    public double TriangleArea(int triangleIndex)
    {
        var t = Triangles[triangleIndex];
        return 0.5 * Vector3D.Cross(Vertices[t.B] - Vertices[t.A], Vertices[t.C] - Vertices[t.A]).Length;
    }

    /// <summary>
    /// Signed volume via the divergence theorem. Positive for a closed, consistently
    /// outward-wound surface; near the true volume magnitude regardless of origin.
    /// </summary>
    public double ComputeSignedVolume()
    {
        double vol = 0;
        foreach (var t in Triangles)
        {
            var a = Vertices[t.A];
            var b = Vertices[t.B];
            var c = Vertices[t.C];
            vol += Vector3D.Dot(a, Vector3D.Cross(b, c)) / 6.0;
        }
        return vol;
    }

    /// <summary>
    /// A mesh is watertight when every edge is shared by exactly two triangles
    /// with opposite orientation.
    /// </summary>
    public bool IsWatertight()
    {
        // Count directed edges; watertight ⇔ every directed edge has exactly one
        // matching reverse edge and no duplicates.
        var directed = new HashSet<(int, int)>();
        foreach (var t in Triangles)
        {
            if (!directed.Add((t.A, t.B)) || !directed.Add((t.B, t.C)) || !directed.Add((t.C, t.A)))
                return false; // duplicate directed edge ⇒ non-manifold or inconsistent winding
        }
        foreach (var (u, v) in directed)
        {
            if (!directed.Contains((v, u)))
                return false; // open boundary edge
        }
        return true;
    }
}
