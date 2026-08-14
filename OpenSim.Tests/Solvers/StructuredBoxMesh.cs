using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Tests.Solvers;

/// <summary>
/// A test-local structured box mesh: a regular grid of cells, each split into six
/// tetrahedra, with the outer skin tagged by the six axis-aligned faces.
///
/// The assembly and contact gates use this instead of the production mesher on purpose.
/// The mesher jitters its surface points (deliberately — it breaks exact cocircular
/// degeneracies), so its box faces are not exact planes; a contact interface built on them
/// could only ever be checked to a couple of percent. Here the interface is EXACTLY planar
/// and the analytic two-slab solution lies exactly in the discrete space, which is what
/// lets the series-resistance identity be gated at 1e-9 instead of a band.
/// </summary>
internal static class StructuredBoxMesh
{
    public const int FaceXMin = 0;
    public const int FaceXMax = 1;
    public const int FaceYMin = 2;
    public const int FaceYMax = 3;
    public const int FaceZMin = 4;
    public const int FaceZMax = 5;

    /// <summary>The six tetrahedra of a unit cell, as local corner indices (bit 0 = x, 1 = y,
    /// 2 = z) — the Freudenthal subdivision along the 0–7 diagonal, which tiles space
    /// consistently so neighbouring cells share faces.</summary>
    private static readonly int[][] CellTets =
    {
        new[] { 0, 1, 3, 7 },
        new[] { 0, 1, 7, 5 },
        new[] { 0, 5, 7, 4 },
        new[] { 0, 3, 2, 7 },
        new[] { 0, 6, 4, 7 },
        new[] { 0, 2, 6, 7 }
    };

    /// <summary>Builds the mesh of the axis-aligned box [x0,x1]×[y0,y1]×[z0,z1].</summary>
    public static FeMesh Build(double x0, double x1, double y0, double y1, double z0, double z1,
        int nx, int ny, int nz)
    {
        var nodes = new List<Vector3D>((nx + 1) * (ny + 1) * (nz + 1));
        int Index(int i, int j, int k) => (k * (ny + 1) + j) * (nx + 1) + i;
        for (int k = 0; k <= nz; k++)
            for (int j = 0; j <= ny; j++)
                for (int i = 0; i <= nx; i++)
                    nodes.Add(new Vector3D(
                        x0 + (x1 - x0) * i / nx,
                        y0 + (y1 - y0) * j / ny,
                        z0 + (z1 - z0) * k / nz));

        var elements = new List<Tet4>(6 * nx * ny * nz);
        var corner = new int[8];
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    for (int c = 0; c < 8; c++)
                        corner[c] = Index(i + (c & 1), j + ((c >> 1) & 1), k + ((c >> 2) & 1));
                    foreach (var tet in CellTets)
                    {
                        int a = corner[tet[0]], b = corner[tet[1]], cc = corner[tet[2]], d = corner[tet[3]];
                        // Orient positively: the assembler's volumes must be positive.
                        if (Volume(nodes, a, b, cc, d) < 0) (cc, d) = (d, cc);
                        elements.Add(new Tet4(a, b, cc, d));
                    }
                }

        return new FeMesh(nodes, elements, Skin(nodes, elements));
    }

    /// <summary>
    /// The boundary skin: element faces that appear exactly once, oriented outward and
    /// tagged with the axis-aligned face they lie on. Derived from the elements rather than
    /// emitted alongside them, so the skin can never disagree with the volume mesh.
    /// </summary>
    private static List<BoundaryTriangle> Skin(List<Vector3D> nodes, List<Tet4> elements)
    {
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

        var skin = new List<BoundaryTriangle>();
        foreach (var (_, face) in counts.OrderBy(kv => kv.Key))
        {
            if (face.Count != 1) continue;
            var n = Vector3D.Cross(nodes[face.B] - nodes[face.A], nodes[face.C] - nodes[face.A]);
            int faceId = Math.Abs(n.X) > Math.Abs(n.Y) && Math.Abs(n.X) > Math.Abs(n.Z)
                ? (n.X < 0 ? FaceXMin : FaceXMax)
                : Math.Abs(n.Y) > Math.Abs(n.Z)
                    ? (n.Y < 0 ? FaceYMin : FaceYMax)
                    : (n.Z < 0 ? FaceZMin : FaceZMax);
            skin.Add(new BoundaryTriangle(face.A, face.B, face.C, faceId));
        }
        return skin;
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
        return (lo, a + b + c - lo - hi, hi);
    }

    private static double Volume(List<Vector3D> nodes, int a, int b, int c, int d) =>
        Vector3D.Dot(nodes[b] - nodes[a], Vector3D.Cross(nodes[c] - nodes[a], nodes[d] - nodes[a])) / 6.0;

    /// <summary>A body carrying a structured box mesh and the given material.</summary>
    public static Body Box(string name, double x0, double x1, double y0, double y1,
        double z0, double z1, int nx, int ny, int nz, Material material) =>
        new() { Name = name, Mesh = Build(x0, x1, y0, y1, z0, z1, nx, ny, nz), Material = material };

    /// <summary>A conductor with unit density and specific heat, for pure-conduction gates.</summary>
    public static Material Conductor(string name, double conductivity) => new()
    {
        Name = name,
        Density = 1000,
        YoungsModulus = 1e9,
        PoissonRatio = 0.3,
        ThermalConductivity = conductivity,
        SpecificHeat = 500
    };
}
