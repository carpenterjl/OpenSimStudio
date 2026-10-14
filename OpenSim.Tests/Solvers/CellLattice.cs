using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// A test-local mesh of cubic cells on a lattice, each cell kept or not and given a region
/// by a function — bodies with cavities, bodies inside cavities, plates facing each other —
/// all with exactly planar faces, so view factors and radiative balances can be checked
/// against what the geometry says exactly.
/// </summary>
internal static class CellLattice
{
    /// <summary>Faces on the lattice's outer boundary keep the box ids 0…5; every other
    /// skin face (facing a removed cell) gets <see cref="InnerFace"/> + the region of its cell.</summary>
    public const int InnerFace = 10;

    private static readonly int[][] CellTets =
    {
        new[] { 0, 1, 3, 7 },
        new[] { 0, 1, 7, 5 },
        new[] { 0, 5, 7, 4 },
        new[] { 0, 3, 2, 7 },
        new[] { 0, 6, 4, 7 },
        new[] { 0, 2, 6, 7 }
    };

    /// <param name="region">Region of cell (i, j, k), or −1 where there is no cell.</param>
    public static FeMesh Build(int nx, int ny, int nz, double h, Func<int, int, int, int> region)
    {
        var nodes = new List<Vector3D>((nx + 1) * (ny + 1) * (nz + 1));
        int Index(int i, int j, int k) => (k * (ny + 1) + j) * (nx + 1) + i;
        for (int k = 0; k <= nz; k++)
            for (int j = 0; j <= ny; j++)
                for (int i = 0; i <= nx; i++)
                    nodes.Add(new Vector3D(i * h, j * h, k * h));

        var elements = new List<Tet4>();
        var regions = new List<int>();
        var corner = new int[8];
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int r = region(i, j, k);
                    if (r < 0) continue;
                    for (int c = 0; c < 8; c++)
                        corner[c] = Index(i + (c & 1), j + ((c >> 1) & 1), k + ((c >> 2) & 1));
                    foreach (var tet in CellTets)
                    {
                        int a = corner[tet[0]], b = corner[tet[1]], cc = corner[tet[2]], d = corner[tet[3]];
                        if (Volume(nodes, a, b, cc, d) < 0) (cc, d) = (d, cc);
                        elements.Add(new Tet4(a, b, cc, d));
                        regions.Add(r);
                    }
                }

        var counts = new Dictionary<(int, int, int), (int Count, int A, int B, int C, int Region)>();
        for (int e = 0; e < elements.Count; e++)
        {
            var el = elements[e];
            foreach (var (a, b, c) in new[]
                     {
                         (el.N0, el.N2, el.N1), (el.N0, el.N1, el.N3),
                         (el.N1, el.N2, el.N3), (el.N0, el.N3, el.N2)
                     })
            {
                var key = Sorted(a, b, c);
                counts[key] = counts.TryGetValue(key, out var prior)
                    ? (prior.Count + 1, prior.A, prior.B, prior.C, prior.Region)
                    : (1, a, b, c, regions[e]);
            }
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
                : InnerFace + face.Region;
            skin.Add(new BoundaryTriangle(face.A, face.B, face.C, faceId));
        }

        var used = new int[nodes.Count];
        Array.Fill(used, -1);
        foreach (var e in elements)
            foreach (int n in new[] { e.N0, e.N1, e.N2, e.N3 })
                used[n] = 0;
        var compact = new List<Vector3D>();
        for (int n = 0; n < used.Length; n++)
            if (used[n] == 0) { used[n] = compact.Count; compact.Add(nodes[n]); }
        return new FeMesh(compact,
            elements.Select(e => new Tet4(used[e.N0], used[e.N1], used[e.N2], used[e.N3])).ToList(),
            skin.Select(t => new BoundaryTriangle(used[t.A], used[t.B], used[t.C], t.FaceId)).ToList(),
            regions);

        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-12;
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
        return (lo, a + b + c - lo - hi, hi);
    }

    private static double Volume(List<Vector3D> nodes, int a, int b, int c, int d) =>
        Vector3D.Dot(nodes[b] - nodes[a], Vector3D.Cross(nodes[c] - nodes[a], nodes[d] - nodes[a])) / 6.0;
}
