using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Geometry;

/// <summary>
/// Segments a triangle mesh into logical faces by region-growing across edges whose
/// dihedral angle is below a crease threshold. This is what lets a user click "the
/// top face" of an imported STL and have boundary conditions target it.
/// </summary>
public static class FaceDetector
{
    /// <summary>
    /// Assigns a face id to every triangle. Triangles connected across an edge whose
    /// normals deviate less than <paramref name="creaseAngleDegrees"/> share a face.
    /// </summary>
    public static int[] DetectFaces(IReadOnlyList<Vector3D> vertices, IReadOnlyList<Triangle> triangles,
        double creaseAngleDegrees = 30)
    {
        int n = triangles.Count;
        var faceIds = new int[n];
        Array.Fill(faceIds, -1);
        if (n == 0) return faceIds;

        // Edge (undirected) → adjacent triangles
        var edgeMap = new Dictionary<(int, int), List<int>>();
        for (int t = 0; t < n; t++)
        {
            var tri = triangles[t];
            AddEdge(edgeMap, tri.A, tri.B, t);
            AddEdge(edgeMap, tri.B, tri.C, t);
            AddEdge(edgeMap, tri.C, tri.A, t);
        }

        var normals = new Vector3D[n];
        for (int t = 0; t < n; t++)
        {
            var tri = triangles[t];
            var cross = Vector3D.Cross(vertices[tri.B] - vertices[tri.A], vertices[tri.C] - vertices[tri.A]);
            normals[t] = cross.LengthSquared > 0 ? cross.Normalized() : Vector3D.UnitZ;
        }

        double cosThreshold = Math.Cos(creaseAngleDegrees * Math.PI / 180.0);
        int currentFace = 0;
        var stack = new Stack<int>();

        for (int seed = 0; seed < n; seed++)
        {
            if (faceIds[seed] != -1) continue;
            faceIds[seed] = currentFace;
            stack.Push(seed);
            while (stack.Count > 0)
            {
                int t = stack.Pop();
                var tri = triangles[t];
                foreach (var edge in new[] { Key(tri.A, tri.B), Key(tri.B, tri.C), Key(tri.C, tri.A) })
                {
                    foreach (int neighbor in edgeMap[edge])
                    {
                        if (neighbor == t || faceIds[neighbor] != -1) continue;
                        if (Vector3D.Dot(normals[t], normals[neighbor]) >= cosThreshold)
                        {
                            faceIds[neighbor] = currentFace;
                            stack.Push(neighbor);
                        }
                    }
                }
            }
            currentFace++;
        }
        return faceIds;
    }

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    private static void AddEdge(Dictionary<(int, int), List<int>> map, int a, int b, int triangle)
    {
        var key = Key(a, b);
        if (!map.TryGetValue(key, out var list))
            map[key] = list = new List<int>(2);
        list.Add(triangle);
    }
}
