using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// Uniformly subdivides each geometry triangle so no edge exceeds the target length,
/// preserving face ids. The refined surface supplies the boundary sample points for
/// Delaunay meshing and the face-id lookup for the final boundary skin.
/// </summary>
public static class SurfaceRefiner
{
    public static TriangleMesh Refine(TriangleMesh mesh, double targetEdgeLength)
    {
        if (targetEdgeLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetEdgeLength));

        var soup = new List<Vector3D>();
        var soupFaceIds = new List<int>();

        for (int t = 0; t < mesh.Triangles.Count; t++)
        {
            var tri = mesh.Triangles[t];
            var a = mesh.Vertices[tri.A];
            var b = mesh.Vertices[tri.B];
            var c = mesh.Vertices[tri.C];
            double longest = Math.Max(Vector3D.Distance(a, b),
                             Math.Max(Vector3D.Distance(b, c), Vector3D.Distance(c, a)));
            int k = Math.Max(1, (int)Math.Ceiling(longest / targetEdgeLength));

            // Split into k² congruent sub-triangles via barycentric grid.
            for (int i = 0; i < k; i++)
            {
                for (int j = 0; j < k - i; j++)
                {
                    Vector3D P(int ii, int jj) => a + (b - a) * ((double)ii / k) + (c - a) * ((double)jj / k);

                    soup.Add(P(i, j)); soup.Add(P(i + 1, j)); soup.Add(P(i, j + 1));
                    soupFaceIds.Add(mesh.TriangleFaceIds[t]);
                    if (j < k - i - 1)
                    {
                        soup.Add(P(i + 1, j)); soup.Add(P(i + 1, j + 1)); soup.Add(P(i, j + 1));
                        soupFaceIds.Add(mesh.TriangleFaceIds[t]);
                    }
                }
            }
        }

        var (vertices, triangles) = VertexWelderWithFaces(soup, soupFaceIds, targetEdgeLength * 1e-6,
            out var faceIds);
        return new TriangleMesh(vertices, triangles, faceIds);
    }

    /// <summary>Welds the subdivided soup while keeping the per-triangle face ids aligned.</summary>
    private static (List<Vector3D>, List<Triangle>) VertexWelderWithFaces(
        List<Vector3D> soup, List<int> soupFaceIds, double tolerance, out List<int> faceIds)
    {
        var vertices = new List<Vector3D>();
        var triangles = new List<Triangle>();
        faceIds = new List<int>();
        var grid = new Dictionary<(long, long, long), int>();
        double tol = Math.Max(tolerance, 1e-12);

        int MapVertex(Vector3D p)
        {
            long cx = (long)Math.Round(p.X / tol);
            long cy = (long)Math.Round(p.Y / tol);
            long cz = (long)Math.Round(p.Z / tol);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out int existing) &&
                            Vector3D.Distance(vertices[existing], p) <= tol)
                            return existing;
            int index = vertices.Count;
            vertices.Add(p);
            grid[(cx, cy, cz)] = index;
            return index;
        }

        for (int t = 0; t < soup.Count; t += 3)
        {
            int a = MapVertex(soup[t]);
            int b = MapVertex(soup[t + 1]);
            int c = MapVertex(soup[t + 2]);
            if (a == b || b == c || c == a) continue;
            triangles.Add(new Triangle(a, b, c));
            faceIds.Add(soupFaceIds[t / 3]);
        }
        return (vertices, triangles);
    }
}
