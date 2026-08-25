using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Meshing;

/// <summary>
/// The structured lattice at <see cref="ElementShape.Hexahedral"/>: one HEX20 per cell.
///
/// The counts here are written down in advance rather than measured, because a mapped mesh
/// on a box has exactly one right answer for every one of them. The exactness assertions are
/// bitwise for the same reason the tetrahedral lattice's are: extreme coordinates are
/// assigned verbatim, so a node on a face plane carries that plane's coordinate to the last
/// bit — which is what makes a support scoped to an edge a genuinely fixed LINE.
/// </summary>
public class HexLatticeMeshTests
{
    private const double Lx = 0.200, Ly = 0.060, Lz = 0.020;

    private static FeMesh Mesh(int nx, int ny, int nz, TriangleMesh? geometry = null) =>
        new StructuredLatticeMeshGenerator().Generate(
            geometry ?? PrimitiveFactory.CreateBox(Lx, Ly, Lz),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Shape = ElementShape.Hexahedral,
                ElementOrder = ElementOrder.Quadratic,
                Divisions = new LatticeDivisions(nx, ny, nz)
            });

    [Fact]
    public void OneHexahedronPerCell_WithTheEdgeMidNodesItsDivisionsImply()
    {
        const int nx = 10, ny = 4, nz = 2;
        var mesh = Mesh(nx, ny, nz);

        Assert.True(mesh.IsHex);
        Assert.True(mesh.IsQuadratic);
        Assert.Empty(mesh.Elements);
        Assert.Equal(nx * ny * nz, mesh.ElementCount);

        // Corner lattice, plus one node per lattice EDGE: the edges parallel to each axis
        // are counted by that axis's cells times the other two axes' node planes.
        int corners = (nx + 1) * (ny + 1) * (nz + 1);
        int edgeMids = nx * (ny + 1) * (nz + 1)
                     + ny * (nx + 1) * (nz + 1)
                     + nz * (nx + 1) * (ny + 1);
        Assert.Equal(corners + edgeMids, mesh.NodeCount);
    }

    [Fact]
    public void LatticeVolume_IsExactlyTheBoxVolume()
    {
        var mesh = Mesh(10, 4, 2);
        Assert.Equal(Lx * Ly * Lz, mesh.TotalVolume(), 12);
        for (int e = 0; e < mesh.ElementCount; e++)
            Assert.True(mesh.ElementVolume(e) > 0, $"element {e} has non-positive volume");
    }

    [Fact]
    public void TheQuadSkinIsTheCellFacesUsedOnce_AndTheTriangleSkinIsItsTriangulation()
    {
        const int nx = 5, ny = 3, nz = 2;
        var mesh = Mesh(nx, ny, nz);

        int expectedQuads = 2 * (nx * ny + ny * nz + nz * nx);
        Assert.Equal(expectedQuads, mesh.BoundaryQuads!.Count);
        Assert.Equal(expectedQuads * 2, mesh.BoundaryTriangles.Count);

        // Every internal cell face is shared by exactly two elements, every skin face by one.
        var used = new Dictionary<(int, int, int, int), int>();
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var n = mesh.GetElementNodes(e);
            int[][] faces =
            {
                new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
                new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 }
            };
            foreach (var f in faces)
            {
                var key = f.Select(i => n[i]).OrderBy(v => v).ToArray();
                var k = (key[0], key[1], key[2], key[3]);
                used[k] = used.GetValueOrDefault(k) + 1;
            }
        }
        Assert.All(used.Values, c => Assert.InRange(c, 1, 2));
        Assert.Equal(expectedQuads, used.Values.Count(c => c == 1));
    }

    /// <summary>
    /// Every skin quad must be wound OUTWARD. A flipped face would send a pressure load into
    /// the solid instead of onto it, silently — the load path has no other check on this.
    /// </summary>
    [Fact]
    public void EverySkinQuad_IsWoundOutward()
    {
        var mesh = Mesh(4, 3, 2);
        var centre = new Vector3D(Lx / 2, Ly / 2, Lz / 2);

        foreach (var q in mesh.BoundaryQuads!)
        {
            var a = mesh.Nodes[q.A];
            var normal = Vector3D.Cross(mesh.Nodes[q.B] - a, mesh.Nodes[q.C] - a);
            var outward = a - centre;
            Assert.True(Vector3D.Dot(normal, outward) > 0,
                $"quad on face {q.FaceId} is wound inward");
        }
    }

    [Fact]
    public void NodesOnAFacePlane_CarryThatPlaneCoordinateBitwise()
    {
        var mesh = Mesh(6, 3, 2);
        // Face ids follow PrimitiveFactory.CreateBox: 0 = x-min, 1 = x-max, 4 = z-min.
        foreach (int node in mesh.GetFaceNodes(new[] { 0 }))
            Assert.Equal(0.0, mesh.Nodes[node].X);
        foreach (int node in mesh.GetFaceNodes(new[] { 1 }))
            Assert.Equal(Lx, mesh.Nodes[node].X);
        foreach (int node in mesh.GetFaceNodes(new[] { 4 }))
            Assert.Equal(0.0, mesh.Nodes[node].Z);
    }

    /// <summary>
    /// The support-line exactness the whole mapped mesher exists for, now including the
    /// MID-EDGE nodes: a fixed support scoped to a geometric edge must pin nodes that lie on
    /// that mathematical line exactly, not merely near it.
    /// </summary>
    [Fact]
    public void EveryNodeOfASupportEdge_IncludingMidEdgeNodes_LiesExactlyOnTheGeometricLine()
    {
        const int nx = 8, ny = 4, nz = 3;
        var mesh = Mesh(nx, ny, nz);

        // The bottom edge where z-min (4) meets y-min (2): the line y = 0, z = 0.
        var edgeIds = mesh.Edges.EdgesBetween(new[] { 4, 2 });
        int edgeId = Assert.Single(edgeIds);
        var edge = mesh.Edges.EdgeById(edgeId)!;

        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);
        var nodes = new HashSet<int>(edge.NodeIds);
        foreach (var segment in edge.Segments)
            nodes.Add(edgeMid[segment.A < segment.B ? (segment.A, segment.B) : (segment.B, segment.A)]);

        // Corners plus one mid per segment: 2*nx + 1 nodes along the line.
        Assert.Equal(2 * nx + 1, nodes.Count);
        foreach (int n in nodes)
        {
            Assert.Equal(0.0, mesh.Nodes[n].Y);
            Assert.Equal(0.0, mesh.Nodes[n].Z);
        }
    }

    [Fact]
    public void TheBoxKeepsItsTwelveEdgesAndEightVertices()
    {
        var mesh = Mesh(5, 3, 2);
        Assert.Equal(12, mesh.Edges.Edges.Count);
        Assert.Equal(8, mesh.Edges.Vertices.Count);
    }

    /// <summary>
    /// Face ids come from the geometry, not from a hardcoded 0..5, so an imported solid keeps
    /// the ids its boundary conditions already name — the same contract the tetrahedral
    /// lattice honours.
    /// </summary>
    [Fact]
    public void ImportedFaceIdsSurviveHexahedralMeshing()
    {
        var box = PrimitiveFactory.CreateBox(Lx, Ly, Lz);
        // Relabel the faces the way an imported STEP solid might: shift every id by 40.
        var relabelled = new TriangleMesh(box.Vertices, box.Triangles,
            box.TriangleFaceIds.Select(id => id + 40).ToList());

        var mesh = Mesh(4, 3, 2, relabelled);

        Assert.All(mesh.BoundaryQuads!, q => Assert.InRange(q.FaceId, 40, 45));
        foreach (int node in mesh.GetFaceNodes(new[] { 40 }))
            Assert.Equal(0.0, mesh.Nodes[node].X);
        foreach (int node in mesh.GetFaceNodes(new[] { 41 }))
            Assert.Equal(Lx, mesh.Nodes[node].X);
    }

    [Fact]
    public void EveryQuadEdge_ResolvesInTheEdgeMidMap_AndNoDiagonalDoes()
    {
        var mesh = Mesh(4, 3, 2);
        var edgeMid = QuadraticMeshBuilder.BuildEdgeMidMap(mesh);

        foreach (var q in mesh.BoundaryQuads!)
        {
            foreach (var (a, b) in new[] { (q.A, q.B), (q.B, q.C), (q.C, q.D), (q.D, q.A) })
                Assert.True(edgeMid.ContainsKey(a < b ? (a, b) : (b, a)));

            int lo = Math.Min(q.A, q.C), hi = Math.Max(q.A, q.C);
            Assert.False(edgeMid.ContainsKey((lo, hi)),
                "the triangulation diagonal is not an element edge, and nothing may look it up");
        }
    }

    /// <summary>
    /// Linear hexahedra are refused rather than quietly produced: without incompatible-mode
    /// machinery a HEX8 shear-locks in bending, which would make it a worse element than the
    /// tetrahedra already on offer.
    /// </summary>
    [Fact]
    public void LinearHexahedra_AreRefusedByName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new StructuredLatticeMeshGenerator().Generate(
                PrimitiveFactory.CreateBox(Lx, Ly, Lz),
                new MeshSettings
                {
                    Method = MeshMethod.StructuredLattice,
                    Shape = ElementShape.Hexahedral,
                    ElementOrder = ElementOrder.Linear,
                    Divisions = new LatticeDivisions(2, 2, 2)
                }));
        Assert.Contains("HEX20", ex.Message);
        Assert.Contains("quadratic", ex.Message);
    }

    [Fact]
    public void NonBoxGeometry_IsStillRefusedByName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new StructuredLatticeMeshGenerator().Generate(
                PrimitiveFactory.CreateCylinder(0.02, 0.05, 24),
                new MeshSettings
                {
                    Method = MeshMethod.StructuredLattice,
                    Shape = ElementShape.Hexahedral,
                    ElementOrder = ElementOrder.Quadratic,
                    Divisions = new LatticeDivisions(4, 4, 4)
                }));
        Assert.Contains("box", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A null shape is what every project written before hexes existed carries, and
    /// it must still mesh tetrahedra.</summary>
    [Fact]
    public void ANullShape_StillMeshesTetrahedra()
    {
        var mesh = new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(Lx, Ly, Lz),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                ElementOrder = ElementOrder.Quadratic,
                Divisions = new LatticeDivisions(5, 3, 2)
            });

        Assert.False(mesh.IsHex);
        Assert.True(mesh.IsQuadratic);
        Assert.Equal(6 * 5 * 3 * 2, mesh.ElementCount);
        Assert.Null(mesh.BoundaryQuads);
    }
}
