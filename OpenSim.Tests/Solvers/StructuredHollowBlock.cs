using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// A test-local structured block with a rectangular through-channel bored along x — the
/// smallest honest stand-in for a heat exchanger.
/// <para>
/// It exists because the whole conjugate track rests on a claim nothing else exercises:
/// that ray-crossing classification and the wall-face map work on a body whose skin has an
/// INNER surface. A point in the bore crosses an even number of triangles and must come out
/// FLUID; the wall faces around it must map to inner-surface triangles, not outer ones.
/// Because the mesh is exactly aligned to a cell lattice, the number of fluid cells the
/// voxelizer must find is an integer that can be written down in advance, so the gate is a
/// count, not a tolerance.
/// </para>
/// </summary>
internal static class StructuredHollowBlock
{
    /// <summary>Face id of the bore's inner surface (outer faces keep 0..5).</summary>
    public const int FaceBore = 6;

    private static readonly int[][] CellTets =
    {
        new[] { 0, 1, 3, 7 },
        new[] { 0, 1, 7, 5 },
        new[] { 0, 5, 7, 4 },
        new[] { 0, 3, 2, 7 },
        new[] { 0, 6, 4, 7 },
        new[] { 0, 2, 6, 7 }
    };

    /// <summary>
    /// Builds the block [0,nx·h]×[0,ny·h]×[0,nz·h] minus the cells whose (j, k) lattice
    /// index lies in [j0, j1) × [k0, k1) — a straight rectangular bore through every x.
    /// </summary>
    public static FeMesh Build(int nx, int ny, int nz, double h, int j0, int j1, int k0, int k1)
    {
        var nodes = new List<Vector3D>((nx + 1) * (ny + 1) * (nz + 1));
        int Index(int i, int j, int k) => (k * (ny + 1) + j) * (nx + 1) + i;
        for (int k = 0; k <= nz; k++)
            for (int j = 0; j <= ny; j++)
                for (int i = 0; i <= nx; i++)
                    nodes.Add(new Vector3D(i * h, j * h, k * h));

        bool Solid(int i, int j, int k) =>
            i >= 0 && i < nx && j >= 0 && j < ny && k >= 0 && k < nz
            && !(j >= j0 && j < j1 && k >= k0 && k < k1);

        var elements = new List<Tet4>();
        var corner = new int[8];
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    if (!Solid(i, j, k)) continue;
                    for (int c = 0; c < 8; c++)
                        corner[c] = Index(i + (c & 1), j + ((c >> 1) & 1), k + ((c >> 2) & 1));
                    foreach (var tet in CellTets)
                    {
                        int a = corner[tet[0]], b = corner[tet[1]], cc = corner[tet[2]], d = corner[tet[3]];
                        if (Volume(nodes, a, b, cc, d) < 0) (cc, d) = (d, cc);
                        elements.Add(new Tet4(a, b, cc, d));
                    }
                }

        // The skin: element faces appearing once. A face is tagged as the bore when it
        // faces INTO the removed region — that is exactly "the solid cell on the other
        // side was carved away but is inside the block".
        var counts = new Dictionary<(int, int, int), (int Count, int A, int B, int C)>();
        foreach (var e in elements)
            foreach (var (a, b, c) in new[]
                     {
                         (e.N0, e.N2, e.N1), (e.N0, e.N1, e.N3),
                         (e.N1, e.N2, e.N3), (e.N0, e.N3, e.N2)
                     })
            {
                var key = Sorted(a, b, c);
                counts[key] = counts.TryGetValue(key, out var prior)
                    ? (prior.Count + 1, prior.A, prior.B, prior.C)
                    : (1, a, b, c);
            }

        double xMax = nx * h, yMax = ny * h, zMax = nz * h;
        var skin = new List<BoundaryTriangle>();
        foreach (var (_, face) in counts.OrderBy(kv => kv.Key))
        {
            if (face.Count != 1) continue;
            var centroid = (nodes[face.A] + nodes[face.B] + nodes[face.C]) * (1.0 / 3.0);
            int faceId =
                Near(centroid.X, 0) ? StructuredBoxMesh.FaceXMin
                : Near(centroid.X, xMax) ? StructuredBoxMesh.FaceXMax
                : Near(centroid.Y, 0) ? StructuredBoxMesh.FaceYMin
                : Near(centroid.Y, yMax) ? StructuredBoxMesh.FaceYMax
                : Near(centroid.Z, 0) ? StructuredBoxMesh.FaceZMin
                : Near(centroid.Z, zMax) ? StructuredBoxMesh.FaceZMax
                : FaceBore;
            skin.Add(new BoundaryTriangle(face.A, face.B, face.C, faceId));
        }

        // Lattice nodes strictly inside the carved-out bore belong to no element. Left in,
        // they are zero rows in the conduction matrix and the CG says so, loudly, in a
        // place that has nothing to do with the physics under test — so compact them away
        // and renumber, exactly as a real mesher would never emit them in the first place.
        var used = new int[nodes.Count];
        Array.Fill(used, -1);
        foreach (var e in elements)
            foreach (int n in new[] { e.N0, e.N1, e.N2, e.N3 })
                used[n] = 0;
        var compact = new List<Vector3D>();
        for (int n = 0; n < used.Length; n++)
            if (used[n] == 0) { used[n] = compact.Count; compact.Add(nodes[n]); }
        var renumbered = elements
            .Select(e => new Tet4(used[e.N0], used[e.N1], used[e.N2], used[e.N3])).ToList();
        var renumberedSkin = skin
            .Select(t => new BoundaryTriangle(used[t.A], used[t.B], used[t.C], t.FaceId)).ToList();
        return new FeMesh(compact, renumbered, renumberedSkin);

        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-12;
    }

    /// <summary>A body carrying the hollow block and the given material.</summary>
    public static Body Body(string name, Material material, int nx, int ny, int nz, double h,
        int j0, int j1, int k0, int k1) => new()
    {
        Name = name,
        Material = material,
        Mesh = Build(nx, ny, nz, h, j0, j1, k0, k1)
    };

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
        return (lo, a + b + c - lo - hi, hi);
    }

    private static double Volume(List<Vector3D> nodes, int a, int b, int c, int d) =>
        Vector3D.Dot(nodes[b] - nodes[a], Vector3D.Cross(nodes[c] - nodes[a], nodes[d] - nodes[a])) / 6.0;
}
