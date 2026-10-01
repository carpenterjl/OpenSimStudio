using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Meshing;

/// <summary>
/// Gates for the mapped box mesher. Everything the mesher promises is an EXACTNESS claim —
/// nodes on the geometric planes, lines and corners — so almost every assertion here is an
/// identity at 1e-12 or bitwise, not a tolerance. A band would not be evidence of anything:
/// the Delaunay mesher already lands within a jitter amplitude of all of these.
/// </summary>
public class StructuredLatticeMeshGeneratorTests
{
    private const double Lx = 0.200, Ly = 0.060, Lz = 0.020;

    private static FeMesh Mesh(int nx, int ny, int nz,
        ElementOrder order = ElementOrder.Linear, TriangleMesh? geometry = null) =>
        new StructuredLatticeMeshGenerator().Generate(
            geometry ?? PrimitiveFactory.CreateBox(Lx, Ly, Lz),
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Divisions = new LatticeDivisions(nx, ny, nz),
                ElementOrder = order
            });

    [Fact]
    public void Lattice_HasExactlyTheNodesAndElementsItsDivisionsImply()
    {
        var mesh = Mesh(10, 3, 1);

        Assert.Equal(11 * 4 * 2, mesh.NodeCount);
        Assert.Equal(6 * 10 * 3 * 1, mesh.ElementCount);
        for (int e = 0; e < mesh.ElementCount; e++)
            Assert.True(mesh.ElementVolume(e) > 0, $"Element {e} has non-positive volume.");
    }

    /// <summary>The volume is exact, not approached: the six tetrahedra of a cell partition
    /// it, and the cells partition the box.</summary>
    [Fact]
    public void LatticeVolume_IsExactlyTheBoxVolume()
    {
        Assert.Equal(Lx * Ly * Lz, Mesh(7, 3, 2).TotalVolume(), 12);
    }

    /// <summary>
    /// THE claim the mesher exists for. Extreme coordinates are assigned verbatim, so a node
    /// on a face plane carries that plane's coordinate BIT FOR BIT — asserted with exact
    /// equality, which no jittered mesher could ever pass.
    /// </summary>
    [Fact]
    public void NodesOnAFacePlane_CarryThatPlaneCoordinateBitwise()
    {
        var mesh = Mesh(9, 4, 3);

        var onXMin = mesh.GetFaceNodes(new[] { 0 });
        var onXMax = mesh.GetFaceNodes(new[] { 1 });
        var onYMax = mesh.GetFaceNodes(new[] { 3 });

        Assert.NotEmpty(onXMin);
        Assert.All(onXMin, n => Assert.Equal(0.0, mesh.Nodes[n].X));
        Assert.All(onXMax, n => Assert.Equal(Lx, mesh.Nodes[n].X));
        Assert.All(onYMax, n => Assert.Equal(Ly, mesh.Nodes[n].Y));
    }

    /// <summary>
    /// The support model behind the beam reproduction: every node of the edge where the
    /// bottom face meets an end face is exactly on that mathematical line.
    /// </summary>
    [Fact]
    public void EveryNodeOfASupportEdge_LiesExactlyOnTheGeometricLine()
    {
        var mesh = Mesh(20, 6, 2);

        var atXMin = mesh.Edges.EdgesBetween(new[] { 2, 0 });
        Assert.Single(atXMin);
        var edge = mesh.Edges.EdgeById(atXMin[0])!;

        // The line x = 0, y = 0, running across the thickness.
        Assert.All(edge.NodeIds, n =>
        {
            Assert.Equal(0.0, mesh.Nodes[n].X);
            Assert.Equal(0.0, mesh.Nodes[n].Y);
        });
        Assert.Equal(3, edge.NodeIds.Count);      // nz + 1
        Assert.Equal(Lz, edge.Length, 12);
    }

    [Fact]
    public void Lattice_HasTheBoxTwelveEdgesAndEightVertices_NoneNonManifold()
    {
        var set = Mesh(6, 3, 2).Edges;

        Assert.Equal(12, set.Edges.Count);
        Assert.Equal(8, set.Vertices.Count);
        Assert.Equal(0, set.NonManifoldEdgeCount);
    }

    /// <summary>
    /// Conforming with no hanging nodes: an interior element face is shared by exactly two
    /// elements and a skin face by exactly one, which is what makes the Freudenthal split
    /// the right one.
    /// </summary>
    [Fact]
    public void EveryElementFace_IsUsedOnceOrTwice_AndTheSkinIsExactlyTheOnceUsedOnes()
    {
        var mesh = Mesh(5, 3, 2);
        var counts = new Dictionary<(int, int, int), int>();
        foreach (var e in mesh.Elements)
            foreach (var f in new[]
                     {
                         (e.N1, e.N2, e.N3), (e.N0, e.N2, e.N3),
                         (e.N0, e.N1, e.N3), (e.N0, e.N1, e.N2)
                     })
            {
                var key = Sorted(f.Item1, f.Item2, f.Item3);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }

        Assert.All(counts.Values, c => Assert.InRange(c, 1, 2));
        Assert.Equal(counts.Count(kv => kv.Value == 1), mesh.BoundaryTriangles.Count);
        // Six planar faces of a 5x3x2 lattice, two triangles per cell face.
        Assert.Equal(2 * 2 * (5 * 3 + 3 * 2 + 5 * 2), mesh.BoundaryTriangles.Count);
    }

    [Fact]
    public void SkinTriangles_AreTaggedWithTheGeometryOwnFaceIds()
    {
        var mesh = Mesh(4, 3, 2);
        var bounds = PrimitiveFactory.CreateBox(Lx, Ly, Lz).Bounds;

        foreach (var t in mesh.BoundaryTriangles)
        {
            var centroid = (mesh.Nodes[t.A] + mesh.Nodes[t.B] + mesh.Nodes[t.C]) / 3.0;
            int expected = t.FaceId switch
            {
                0 or 1 => Math.Abs(centroid.X - (t.FaceId == 0 ? bounds.Min.X : bounds.Max.X)) < 1e-12 ? t.FaceId : -1,
                2 or 3 => Math.Abs(centroid.Y - (t.FaceId == 2 ? bounds.Min.Y : bounds.Max.Y)) < 1e-12 ? t.FaceId : -1,
                _ => Math.Abs(centroid.Z - (t.FaceId == 4 ? bounds.Min.Z : bounds.Max.Z)) < 1e-12 ? t.FaceId : -1
            };
            Assert.Equal(t.FaceId, expected);
        }
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 },
            mesh.BoundaryTriangles.Select(t => t.FaceId).Distinct().OrderBy(f => f).ToArray());
    }

    /// <summary>
    /// An imported solid carries the face ids ITS file gave it. Tagging the lattice skin
    /// from the detected box's own map means those ids survive — a boundary condition
    /// naming face 7 still means the same surface after a structured remesh.
    /// </summary>
    [Fact]
    public void ImportedFaceIds_SurviveStructuredMeshing_RatherThanBeingRenumbered()
    {
        var box = PrimitiveFactory.CreateBox(Lx, Ly, Lz);
        // Permute the ids the way a STEP file's native faces would: 0..5 -> 10,11,...
        var permuted = new TriangleMesh(box.Vertices, box.Triangles,
            box.TriangleFaceIds.Select(f => f switch
            {
                0 => 13, 1 => 11, 2 => 17, 3 => 12, 4 => 19, _ => 10
            }).ToArray());

        var mesh = Mesh(4, 2, 2, geometry: permuted);

        Assert.Equal(new[] { 10, 11, 12, 13, 17, 19 },
            mesh.BoundaryTriangles.Select(t => t.FaceId).Distinct().OrderBy(f => f).ToArray());
        // Face 13 was x-min: its nodes still sit exactly on x = 0.
        Assert.All(mesh.GetFaceNodes(new[] { 13 }), n => Assert.Equal(0.0, mesh.Nodes[n].X));
        Assert.All(mesh.GetFaceNodes(new[] { 17 }), n => Assert.Equal(0.0, mesh.Nodes[n].Y));
    }

    [Fact]
    public void QuadraticUpgrade_AppendsMidNodesAndKeepsThemOnTheFacePlanes()
    {
        var linear = Mesh(4, 2, 2);
        var mesh = Mesh(4, 2, 2, ElementOrder.Quadratic);

        Assert.True(mesh.IsQuadratic);
        Assert.Equal(linear.ElementCount, mesh.ElementCount);
        Assert.True(mesh.NodeCount > linear.NodeCount);
        // Corner nodes keep their indices and positions, so the skin stays valid.
        for (int n = 0; n < linear.NodeCount; n++)
            Assert.Equal(linear.Nodes[n], mesh.Nodes[n]);
        // A mid-edge node of two x = 0 corners is the exact midpoint, so still on the plane.
        Assert.All(mesh.GetScopeNodes(new FixedSupport { Name = "s", FaceIds = new[] { 0 } }),
            n => Assert.Equal(0.0, mesh.Nodes[n].X, 12));
    }

    [Fact]
    public void DivisionsDefaultToTheTargetEdgeLength_WhenNotGivenExplicitly()
    {
        var mesh = new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(Lx, Ly, Lz),
            new MeshSettings { Method = MeshMethod.StructuredLattice, TargetEdgeLength = 0.010 });

        // 20 x 6 x 2 cells at 10 mm.
        Assert.Equal(21 * 7 * 3, mesh.NodeCount);
        Assert.Equal(6 * 20 * 6 * 2, mesh.ElementCount);
    }

    [Fact]
    public void AutoEdgeLength_StillProducesAUsableLattice()
    {
        var mesh = new StructuredLatticeMeshGenerator().Generate(
            PrimitiveFactory.CreateBox(Lx, Ly, Lz),
            new MeshSettings { Method = MeshMethod.StructuredLattice });

        Assert.True(mesh.ElementCount > 0);
        Assert.Equal(Lx * Ly * Lz, mesh.TotalVolume(), 12);
    }

    // ---- typed refusals: never a silent fallback to another mesher ----

    [Fact]
    public void ACylinder_IsRefusedByName_NotMeshedAnyway()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Mesh(4, 4, 4, geometry: PrimitiveFactory.CreateCylinder(0.01, 0.05)));

        Assert.Contains("axis-aligned box bodies only", ex.Message);
        Assert.Contains("Delaunay", ex.Message);
    }

    /// <summary>
    /// Six planar faces on the six bounding planes are NOT enough to be a box — the volume
    /// check is what refuses a solid that merely touches them.
    /// </summary>
    [Fact]
    public void ASolidThatDoesNotFillItsBoundingBox_IsRefused()
    {
        // An octahedron: every face is planar, none is axis-aligned.
        var v = new List<Vector3D>
        {
            new(-1, 0, 0), new(1, 0, 0), new(0, -1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1)
        };
        var tris = new List<Triangle>
        {
            new(0, 2, 5), new(2, 1, 5), new(1, 3, 5), new(3, 0, 5),
            new(2, 0, 4), new(1, 2, 4), new(3, 1, 4), new(0, 3, 4)
        };
        var geometry = new TriangleMesh(v, tris, Enumerable.Range(0, 8).Select(i => i % 6).ToArray());

        var ex = Assert.Throws<InvalidOperationException>(() => Mesh(2, 2, 2, geometry: geometry));
        Assert.Contains("axis-aligned box bodies only", ex.Message);
    }

    [Fact]
    public void ZeroDivisions_AreATypedFailure()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Mesh(0, 1, 1));
        Assert.Contains("at least 1", ex.Message);
    }

    [Fact]
    public void ARunawayDivisionCount_IsRefusedWithItsNodeCount()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new StructuredLatticeMeshGenerator { MaxNodes = 1000 }.Generate(
                PrimitiveFactory.CreateBox(Lx, Ly, Lz),
                new MeshSettings
                {
                    Method = MeshMethod.StructuredLattice,
                    Divisions = new LatticeDivisions(50, 50, 50)
                }));

        Assert.Contains("132,651", ex.Message);   // 51^3 nodes
        Assert.Contains("fewer divisions", ex.Message);
    }

    // ---- the selector ----

    [Fact]
    public void Selector_RoutesByMethod_AndTreatsNullAsDelaunay()
    {
        var selector = new MeshGeneratorSelector(
            new DelaunayMeshGenerator(), new StructuredLatticeMeshGenerator());

        Assert.IsType<DelaunayMeshGenerator>(selector.For(new MeshSettings()));
        Assert.IsType<DelaunayMeshGenerator>(
            selector.For(new MeshSettings { Method = MeshMethod.Delaunay }));
        Assert.IsType<StructuredLatticeMeshGenerator>(
            selector.For(new MeshSettings { Method = MeshMethod.StructuredLattice }));
    }

    /// <summary>The back-compatibility pin: routing through the selector with the settings
    /// every existing caller builds produces the Delaunay mesher's own mesh, node for node.</summary>
    [Fact]
    public void SelectorWithDefaultSettings_ProducesTheDelaunayMeshUnchanged()
    {
        var geometry = PrimitiveFactory.CreateBox(0.05, 0.02, 0.01);
        var settings = new MeshSettings { TargetEdgeLength = 0.005 };

        var direct = new DelaunayMeshGenerator().Generate(geometry, settings);
        var routed = new MeshGeneratorSelector(
            new DelaunayMeshGenerator(), new StructuredLatticeMeshGenerator())
            .Generate(geometry, settings);

        Assert.Equal(direct.NodeCount, routed.NodeCount);
        Assert.Equal(direct.ElementCount, routed.ElementCount);
        for (int n = 0; n < direct.NodeCount; n++)
            Assert.Equal(direct.Nodes[n], routed.Nodes[n]);
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
        return (lo, a + b + c - lo - hi, hi);
    }
}
