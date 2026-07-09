using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Geometry;

/// <summary>
/// Merges coincident vertices (within a tolerance) of a triangle soup into a shared
/// vertex list, and drops triangles that become degenerate. STL files store every
/// triangle with its own three vertices, so welding is what recovers connectivity.
/// </summary>
public static class VertexWelder
{
    /// <summary>
    /// Welds the given triangle soup. <paramref name="soup"/> holds three consecutive
    /// vertices per triangle. Tolerance defaults to 10⁻⁶ of the bounding-box diagonal.
    /// </summary>
    public static (List<Vector3D> Vertices, List<Triangle> Triangles) Weld(
        IReadOnlyList<Vector3D> soup, double? tolerance = null)
    {
        var (vertices, triangles, _) = Weld(soup, triangleFaceIds: null, tolerance);
        return (vertices, triangles);
    }

    /// <summary>
    /// Face-id-carrying weld: <paramref name="triangleFaceIds"/> holds one id per input
    /// triangle and shrinks in lockstep when degenerate/sliver triangles are dropped —
    /// the STEP importer carries native surface-face ids through here.
    /// </summary>
    public static (List<Vector3D> Vertices, List<Triangle> Triangles, List<int> TriangleFaceIds) Weld(
        IReadOnlyList<Vector3D> soup, IReadOnlyList<int>? triangleFaceIds, double? tolerance = null)
    {
        if (soup.Count % 3 != 0)
            throw new ArgumentException("Vertex soup length must be a multiple of 3.", nameof(soup));
        if (triangleFaceIds is not null && triangleFaceIds.Count * 3 != soup.Count)
            throw new ArgumentException("One face id per triangle is required.", nameof(triangleFaceIds));
        if (soup.Count == 0)
            return (new List<Vector3D>(), new List<Triangle>(), new List<int>());

        double tol = tolerance ?? Aabb.FromPoints(soup).Diagonal * 1e-6;
        if (tol <= 0) tol = 1e-12;

        var vertices = new List<Vector3D>();
        var triangles = new List<Triangle>();
        var faceIds = new List<int>();
        // Snap-to-grid welding: quantize coordinates to the tolerance grid and also
        // probe the 26 neighbouring cells so points straddling a cell boundary still weld.
        var grid = new Dictionary<(long, long, long), int>();

        int MapVertex(Vector3D p)
        {
            long cx = (long)Math.Round(p.X / tol);
            long cy = (long)Math.Round(p.Y / tol);
            long cz = (long)Math.Round(p.Z / tol);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        if (grid.TryGetValue((cx + dx, cy + dy, cz + dz), out int existing) &&
                            Vector3D.Distance(vertices[existing], p) <= tol)
                            return existing;
                    }
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
            if (a == b || b == c || c == a)
                continue; // degenerate after welding
            var area = Vector3D.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).Length;
            if (area <= tol * tol)
                continue; // sliver triangle
            triangles.Add(new Triangle(a, b, c));
            if (triangleFaceIds is not null) faceIds.Add(triangleFaceIds[t / 3]);
        }

        return (vertices, triangles, faceIds);
    }
}
