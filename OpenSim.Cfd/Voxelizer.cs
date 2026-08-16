using OpenSim.Core.Model;
using OpenSim.Core.Numerics;

namespace OpenSim.Cfd;

/// <summary>
/// A grid face separating a fluid cell from a solid cell — where the flow meets a body.
/// </summary>
/// <param name="Axis">0/1/2 = x/y/z-normal face.</param>
/// <param name="I">Face index along x in that axis's face grid.</param>
/// <param name="J">Face index along y.</param>
/// <param name="K">Face index along z.</param>
/// <param name="FluidCell">Flat cell index of the fluid side.</param>
/// <param name="SolidCell">Flat cell index of the solid side.</param>
/// <param name="SolidBody">Body index of the solid side.</param>
/// <param name="BoundaryTriangle">Global index into the merged mesh's BoundaryTriangles:
/// the nearest boundary-triangle centroid of the SOLID body — the seam over which wall
/// heat flux is exchanged with the FE thermal solve.</param>
/// <param name="SolidIsHighSide">True when the solid cell sits on the + side of the face
/// (fixes the wall-normal sign without recomputing it downstream).</param>
public readonly record struct WallFace(int Axis, int I, int J, int K,
    int FluidCell, int SolidCell, int SolidBody, int BoundaryTriangle, bool SolidIsHighSide);

/// <summary>The voxelized CFD domain: the classified grid plus the fluid–solid wall faces.</summary>
public sealed record VoxelizedDomain(
    CartesianGrid Grid,
    IReadOnlyList<WallFace> WallFaces,
    int FluidCellCount,
    IReadOnlyList<int> SolidCellCounts,
    IReadOnlyList<string> Notes);

/// <summary>
/// Classifies every grid cell against the bodies' FE boundary skins (staircase voxels —
/// first-order boundary accuracy, stated and measured by the order gate) and extracts
/// the fluid–solid wall faces with their mapping back to FE boundary triangles.
/// <para>
/// Classification is against the FE skin, not the imported CAD geometry, ON PURPOSE:
/// the conjugate exchange happens on <see cref="FeMesh.BoundaryTriangles"/> (that is
/// where <see cref="SurfaceFilmModel"/> lives), so the solid the flow sees must be the
/// solid the thermal solve integrates — and the FE boundary skin is guaranteed closed.
/// </para>
/// Cell-center classification uses the same 3-ray-voting <see cref="PointInSolidClassifier"/>
/// as the tet mesher. A single axis-aligned ray per grid column would be ~100× cheaper but
/// grazes shared face diagonals of structured meshes EXACTLY — the degeneracy class the
/// voting exists to dodge — so the honest per-cell test stays. Each cell's classification
/// is an independent pure function, so the parallel sweep is bitwise identical at any
/// degree of parallelism (workers get their own classifier instances; the shared
/// candidate buffers are the only mutable state).
/// </summary>
public static class Voxelizer
{
    public static VoxelizedDomain Voxelize(FeMesh mesh, IReadOnlyList<int> nodeBases,
        CfdSettings.ResolvedGrid resolved, int maxDegreeOfParallelism = -1,
        CancellationToken cancellationToken = default)
    {
        if (nodeBases.Count == 0 || nodeBases[0] != 0)
            throw new ArgumentException("nodeBases must start at 0 (one entry per body).",
                nameof(nodeBases));
        int bodyCount = nodeBases.Count;
        var notes = new List<string>();

        // ---- Partition the boundary skin by body (triangles never span bodies:
        //      the merge concatenates and never re-welds).
        var bodyTris = new List<(int A, int B, int C)>[bodyCount];
        var bodyTriGlobal = new List<int>[bodyCount];
        for (int b = 0; b < bodyCount; b++)
        {
            bodyTris[b] = new List<(int, int, int)>();
            bodyTriGlobal[b] = new List<int>();
        }
        for (int t = 0; t < mesh.BoundaryTriangles.Count; t++)
        {
            var bt = mesh.BoundaryTriangles[t];
            int b = BodyOfNode(nodeBases, bt.A);
            bodyTris[b].Add((bt.A, bt.B, bt.C));
            bodyTriGlobal[b].Add(t);
        }
        for (int b = 0; b < bodyCount; b++)
            if (bodyTris[b].Count == 0)
                throw new InvalidOperationException(
                    $"Body {b} contributes no boundary triangles — cannot voxelize a body without a skin.");

        // Per-body bounding boxes prefilter the classification: a cell center inside a
        // body necessarily lies inside its skin bounds, so most of the domain never
        // reaches a ray test.
        var bodyBounds = new Aabb[bodyCount];
        for (int b = 0; b < bodyCount; b++)
            bodyBounds[b] = Aabb.FromPoints(EnumerateSkinVertices(mesh, bodyTris[b]));

        var grid = new CartesianGrid(resolved.CellsX, resolved.CellsY, resolved.CellsZ,
            resolved.Domain.Min, resolved.CellSize);
        notes.AddRange(resolved.Notes);

        // ---- Classify cell centers, parallel over z-slabs, disjoint writes.
        //
        // The probe point is the cell center plus a CONSTANT sub-grid-scale offset with
        // mutually irrational-ish components (the mesher's jitter precedent). Exact
        // structured skins — test fixtures, hand-built meshes — put their vertices on a
        // rational lattice, and the classifier's voting rays have rational direction
        // RATIOS, so rays from lattice-aligned cell centers pass exactly through mesh
        // vertices and edges and several votes can go wrong together (found live: a 4³
        // box read 53 of its 64 cells). The offset breaks that whole degeneracy class;
        // a center within 1e-7·h of a surface may classify either way, which is far
        // below the first-order staircase error already stated for the voxel boundary.
        var probeOffset = grid.H * 1e-7 *
            new Vector3D(0.7548776662466927, 0.5698402909980532, 0.43247204223513106);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };
        Parallel.For(0, grid.Nz, options,
            () => new PointInSolidClassifier?[bodyCount],
            (k, _, classifiers) =>
            {
                for (int j = 0; j < grid.Ny; j++)
                {
                    for (int i = 0; i < grid.Nx; i++)
                    {
                        var center = grid.CellCenter(i, j, k) + probeOffset;
                        for (int b = 0; b < bodyCount; b++)
                        {
                            if (!bodyBounds[b].Contains(center)) continue;
                            var classifier = classifiers[b] ??=
                                new PointInSolidClassifier(mesh.Nodes, bodyTris[b]);
                            if (classifier.IsInside(center))
                            {
                                // Bodies should not overlap; if two skins both claim a
                                // center the FIRST body in assembly order wins —
                                // deterministic, and the overlap itself is a modeling
                                // problem the contact detector already surfaces.
                                grid.CellBody[grid.CellIndex(i, j, k)] = b;
                                break;
                            }
                        }
                    }
                }
                return classifiers;
            },
            _ => { });

        // ---- Counts + honesty checks (sequential, canonical order).
        int fluidCells = 0;
        var solidCells = new int[bodyCount];
        foreach (int body in grid.CellBody)
        {
            if (body == CartesianGrid.Fluid) fluidCells++;
            else solidCells[body]++;
        }
        for (int b = 0; b < bodyCount; b++)
            if (solidCells[b] == 0)
                throw new InvalidOperationException(
                    $"Body {b} occupies no grid cell at cell size {grid.H:G4} m — the grid cannot " +
                    "see it. Refine the CFD cell size below the body's smallest feature.");
        if (fluidCells == 0)
            throw new InvalidOperationException(
                "The CFD domain contains no fluid cells — the bodies fill the entire box.");

        // ---- Wall faces, canonical order (axis, then k, j, i) — the deterministic
        //      ordering every flux aggregation loop inherits.
        var wallFaces = new List<WallFace>();
        var centroidTrees = new KdTree?[bodyCount];
        var centroidLists = new List<Vector3D>?[bodyCount];

        void Consider(int axis, int i, int j, int k, int lowCell, int highCell)
        {
            int lowBody = grid.CellBody[lowCell];
            int highBody = grid.CellBody[highCell];
            bool lowFluid = lowBody == CartesianGrid.Fluid;
            bool highFluid = highBody == CartesianGrid.Fluid;
            if (lowFluid == highFluid) return;

            int solidBody = lowFluid ? highBody : lowBody;
            var faceCenter = axis switch
            {
                0 => grid.UFaceCenter(i, j, k),
                1 => grid.VFaceCenter(i, j, k),
                _ => grid.WFaceCenter(i, j, k)
            };
            int tri = NearestTriangle(solidBody, faceCenter);
            wallFaces.Add(new WallFace(axis, i, j, k,
                FluidCell: lowFluid ? lowCell : highCell,
                SolidCell: lowFluid ? highCell : lowCell,
                SolidBody: solidBody,
                BoundaryTriangle: tri,
                SolidIsHighSide: lowFluid));
        }

        int NearestTriangle(int body, Vector3D point)
        {
            if (centroidTrees[body] is null)
            {
                var centroids = new List<Vector3D>(bodyTris[body].Count);
                foreach (var (a, bb, c) in bodyTris[body])
                    centroids.Add((mesh.Nodes[a] + mesh.Nodes[bb] + mesh.Nodes[c]) * (1.0 / 3.0));
                centroidLists[body] = centroids;
                centroidTrees[body] = new KdTree(centroids);
            }
            return bodyTriGlobal[body][centroidTrees[body]!.NearestNeighbor(point)];
        }

        for (int k = 0; k < grid.Nz; k++)
            for (int j = 0; j < grid.Ny; j++)
                for (int i = 1; i < grid.Nx; i++)
                    Consider(0, i, j, k, grid.CellIndex(i - 1, j, k), grid.CellIndex(i, j, k));
        for (int k = 0; k < grid.Nz; k++)
            for (int j = 1; j < grid.Ny; j++)
                for (int i = 0; i < grid.Nx; i++)
                    Consider(1, i, j, k, grid.CellIndex(i, j - 1, k), grid.CellIndex(i, j, k));
        for (int k = 1; k < grid.Nz; k++)
            for (int j = 0; j < grid.Ny; j++)
                for (int i = 0; i < grid.Nx; i++)
                    Consider(2, i, j, k, grid.CellIndex(i, j, k - 1), grid.CellIndex(i, j, k));

        notes.Add($"Voxelized {grid.Nx}×{grid.Ny}×{grid.Nz} cells at {grid.H:G4} m: " +
                  $"{fluidCells:N0} fluid, {solidCells.Sum():N0} solid " +
                  $"({string.Join(", ", solidCells.Select((n, b) => $"body {b}: {n:N0}"))}), " +
                  $"{wallFaces.Count:N0} wall faces.");

        return new VoxelizedDomain(grid, wallFaces, fluidCells, solidCells, notes);
    }

    private static int BodyOfNode(IReadOnlyList<int> nodeBases, int node)
    {
        for (int b = nodeBases.Count - 1; b >= 0; b--)
            if (node >= nodeBases[b]) return b;
        return 0;
    }

    private static IEnumerable<Vector3D> EnumerateSkinVertices(FeMesh mesh,
        List<(int A, int B, int C)> tris)
    {
        foreach (var (a, b, c) in tris)
        {
            yield return mesh.Nodes[a];
            yield return mesh.Nodes[b];
            yield return mesh.Nodes[c];
        }
    }
}
