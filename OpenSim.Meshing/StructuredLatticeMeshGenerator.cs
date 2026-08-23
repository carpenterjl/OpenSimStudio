using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Meshing;

/// <summary>
/// A mapped (structured) tetrahedral mesher for axis-aligned box bodies: a regular grid of
/// cells, each split into six tetrahedra, exactly the mesh a commercial code lays on a
/// block when you give it edge divisions.
/// <para>
/// It exists for one reason the Delaunay mesher cannot serve. That mesher jitters every
/// surface point — deliberately, to break the exact cospherical degeneracies a regular grid
/// feeds floating-point predicates — so its nodes sit NEAR a geometric edge rather than on
/// it, and a support scoped to an edge is a line of nearly-fixed nodes. Here node
/// coordinates are laid on the lattice with the extremes assigned verbatim, so every face
/// is an exact plane, every edge an exact line and every corner exact. The reference beam's
/// fixed edges become genuinely fixed, which is worth several percent of its deflection.
/// </para>
/// <para>
/// No Delaunay predicate is ever evaluated here, so the degeneracy the jitter defends
/// against cannot arise: the connectivity is the Freudenthal subdivision, chosen up front.
/// </para>
/// </summary>
public sealed class StructuredLatticeMeshGenerator : IMeshGenerator
{
    public string Name => "Structured lattice mesher (box)";

    /// <summary>Fraction of the bounding-box diagonal used when TargetEdgeLength is 0
    /// (auto) — the same rule the Delaunay mesher uses, so "auto" means one thing.</summary>
    public double AutoEdgeFraction { get; init; } = 1.0 / 15.0;

    /// <summary>
    /// Refuse rather than allocate past this many nodes. A mapped mesh has no refinement to
    /// bail it out, so a mistyped division count would otherwise die as an out-of-memory
    /// error naming nothing the user can act on.
    /// </summary>
    public int MaxNodes { get; init; } = 4_000_000;

    /// <summary>
    /// The six tetrahedra of a unit cell as local corner indices (bit 0 = x, 1 = y, 2 = z):
    /// the Freudenthal subdivision along the 0–7 diagonal. It tiles space CONSISTENTLY, so
    /// neighbouring cells share whole faces and the mesh is conforming with no hanging
    /// nodes — the property a five-tetrahedron split only has if alternated cell by cell.
    /// </summary>
    private static readonly int[][] CellTets =
    {
        new[] { 0, 1, 3, 7 },
        new[] { 0, 1, 7, 5 },
        new[] { 0, 5, 7, 4 },
        new[] { 0, 3, 2, 7 },
        new[] { 0, 6, 4, 7 },
        new[] { 0, 2, 6, 7 }
    };

    public FeMesh Generate(TriangleMesh geometry, MeshSettings settings,
        CancellationToken cancellationToken = default)
    {
        var box = BoxFit.Detect(geometry);
        var bounds = box.Bounds;
        var size = bounds.Size;

        var divisions = ResolveDivisions(settings, bounds);
        long nodeCount = (long)(divisions.Nx + 1) * (divisions.Ny + 1) * (divisions.Nz + 1);
        if (nodeCount > MaxNodes)
            throw new InvalidOperationException(
                $"A {divisions.Nx}×{divisions.Ny}×{divisions.Nz} lattice needs {nodeCount:N0} nodes, " +
                $"over the {MaxNodes:N0} limit. Use fewer divisions or a larger element size.");

        int nx = divisions.Nx, ny = divisions.Ny, nz = divisions.Nz;

        // Lattice nodes. The extremes are assigned VERBATIM rather than computed, so a node
        // on a face plane carries that plane's coordinate bit for bit — the exactness the
        // whole mesher is for. Interior coordinates are the exact linear interpolant.
        var nodes = new List<Vector3D>((int)nodeCount);
        int Index(int i, int j, int k) => (k * (ny + 1) + j) * (nx + 1) + i;
        for (int k = 0; k <= nz; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double z = Coordinate(bounds.Min.Z, bounds.Max.Z, k, nz);
            for (int j = 0; j <= ny; j++)
            {
                double y = Coordinate(bounds.Min.Y, bounds.Max.Y, j, ny);
                for (int i = 0; i <= nx; i++)
                    nodes.Add(new Vector3D(Coordinate(bounds.Min.X, bounds.Max.X, i, nx), y, z));
            }
        }

        var elements = new List<Tet4>(6 * nx * ny * nz);
        var corner = new int[8];
        for (int k = 0; k < nz; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    for (int c = 0; c < 8; c++)
                        corner[c] = Index(i + (c & 1), j + ((c >> 1) & 1), k + ((c >> 2) & 1));
                    foreach (var tet in CellTets)
                    {
                        int a = corner[tet[0]], b = corner[tet[1]], cc = corner[tet[2]], d = corner[tet[3]];
                        // Orient positively: the assemblers require positive volumes.
                        if (Volume(nodes, a, b, cc, d) < 0) (cc, d) = (d, cc);
                        elements.Add(new Tet4(a, b, cc, d));
                    }
                }
        }

        var mesh = new FeMesh(nodes, elements, Skin(nodes, elements, box));

        // Quadratic last, on the final linear geometry — the Delaunay mesher's order.
        return settings.ElementOrder == ElementOrder.Quadratic
            ? QuadraticMeshBuilder.Upgrade(mesh)
            : mesh;
    }

    /// <summary>
    /// Explicit divisions when given, otherwise one cell per target edge length along each
    /// axis. <see cref="MeshSettings.TargetMinQuality"/> and
    /// <see cref="MeshSettings.MaxRefinementPoints"/> are deliberately unused: a mapped
    /// mesh's element quality is fixed by its cell aspect ratio, so there is nothing for
    /// refinement to improve — the knob that matters here is the division counts.
    /// </summary>
    private LatticeDivisions ResolveDivisions(MeshSettings settings, Aabb bounds)
    {
        if (settings.Divisions is { } given)
        {
            if (given.Nx < 1 || given.Ny < 1 || given.Nz < 1)
                throw new InvalidOperationException(
                    $"Lattice divisions must be at least 1 on every axis; got " +
                    $"{given.Nx}×{given.Ny}×{given.Nz}.");
            return given;
        }

        double h = settings.TargetEdgeLength > 0
            ? settings.TargetEdgeLength
            : bounds.Diagonal * AutoEdgeFraction;
        var size = bounds.Size;
        return new LatticeDivisions(DivisionsFor(size.X, h), DivisionsFor(size.Y, h), DivisionsFor(size.Z, h));
    }

    /// <summary>Cells along one axis for a target edge length — at least one.</summary>
    public static int DivisionsFor(double extent, double targetEdgeLength) =>
        targetEdgeLength > 0 ? Math.Max(1, (int)Math.Round(extent / targetEdgeLength)) : 1;

    private static double Coordinate(double min, double max, int i, int n) =>
        i == 0 ? min : i == n ? max : min + (max - min) * i / n;

    /// <summary>
    /// The boundary skin: element faces appearing exactly once, wound outward and tagged
    /// with the geometry's OWN face id for the plane they lie on. Derived from the elements
    /// rather than from the input geometry, so the skin can never disagree with the volume
    /// mesh; the plane→face-id map comes from the detected box, so an imported solid keeps
    /// the face ids its boundary conditions already name.
    /// </summary>
    private static List<BoundaryTriangle> Skin(List<Vector3D> nodes, List<Tet4> elements, BoxFit box)
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
            // Exact on a lattice: every skin triangle's normal is along one axis.
            int plane = Math.Abs(n.X) > Math.Abs(n.Y) && Math.Abs(n.X) > Math.Abs(n.Z)
                ? (n.X < 0 ? BoxFit.PlaneXMin : BoxFit.PlaneXMax)
                : Math.Abs(n.Y) > Math.Abs(n.Z)
                    ? (n.Y < 0 ? BoxFit.PlaneYMin : BoxFit.PlaneYMax)
                    : (n.Z < 0 ? BoxFit.PlaneZMin : BoxFit.PlaneZMax);
            skin.Add(new BoundaryTriangle(face.A, face.B, face.C, box.FaceIdOfPlane[plane]));
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
}
