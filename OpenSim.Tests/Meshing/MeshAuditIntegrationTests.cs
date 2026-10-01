using System.Globalization;
using System.Text;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Geometry.Step;
using OpenSim.Meshing;
using OpenSim.Tests.Geometry.Step;

namespace OpenSim.Tests.Meshing;

/// <summary>
/// The audit where it is wired in: at the end of both meshers, and in front of the solvers
/// for a mesh that was not generated in this session. The fixtures the plan lists as
/// "correctly meshed, must pass" are meshed here by the real Delaunay mesher.
/// </summary>
public class MeshAuditIntegrationTests
{
    private static FeMesh Delaunay(TriangleMesh geometry, double h, ElementOrder order = ElementOrder.Linear) =>
        new DelaunayMeshGenerator().Generate(geometry, new MeshSettings { TargetEdgeLength = h, ElementOrder = order });

    private static void AssertAudited(FeMesh mesh, int regions = 1)
    {
        Assert.NotNull(mesh.Audit);
        Assert.True(mesh.Audit!.Passed, mesh.Audit.Describe());
        Assert.Equal(11, mesh.Audit.Checks.Count);
        Assert.Equal(MeshAuditOutcome.Passed, mesh.Audit["A0"]!.Outcome);
        Assert.Equal(MeshAuditOutcome.Passed, mesh.Audit["A10"]!.Outcome);
        Assert.Equal(regions, (int)mesh.Audit["A6"]!.Limit!.Value);
    }

    // ------------------------------------------------------------------ the threshold fixtures

    /// <summary>
    /// T_D, T_V and T_A are twice the worst the clean cube and cylinder fixtures show, not a
    /// guess: this asserts the "twice", so a mesher change that eats the margin fails here
    /// rather than at a user's desk.
    /// </summary>
    [Theory]
    [InlineData("unit cube", 0, 0.25)]
    [InlineData("48-facet cylinder", 48, 0.15)]
    [InlineData("small 32-facet cylinder", 32, 0.004)]
    [InlineData("13-facet cylinder", 13, 0.10)]
    public void Thresholds_AreTwiceWhatTheFixturesShow(string fixture, int facets, double h)
    {
        var geometry = facets == 0 ? PrimitiveFactory.CreateBox(1, 1, 1)
            : facets == 32 ? PrimitiveFactory.CreateCylinder(0.01, 0.04, 32)
            : PrimitiveFactory.CreateCylinder(0.5, 1.0, facets);
        var audit = Delaunay(geometry, h).Audit!;
        Assert.True(audit.Passed, fixture + ": " + audit.Describe());
        // No rebuild: these are what the mesher produces first time.
        Assert.Empty(audit.Warnings);

        Assert.True(audit["A7"]!.Observed <= 0.5 * MeshAudit.DistanceFactor * h, audit["A7"]!.Detail);
        Assert.True(audit["A8"]!.Observed <= 0.5 * MeshAudit.DistanceFactor * h, audit["A8"]!.Detail);
        Assert.True(audit["A5"]!.Observed <= 0.5 * MeshAudit.AreaTolerance, audit["A5"]!.Detail);
        Assert.True(audit["A4"]!.Observed <= 0.5 * audit["A4"]!.Limit, audit["A4"]!.Detail);

        // And the 13-facet cylinder is what sets T_D: its edges are cut by nearly half of it.
        if (facets == 13)
            Assert.True(audit["A8"]!.Observed >= 0.4 * MeshAudit.DistanceFactor * h, audit["A8"]!.Detail);
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(0.07)]
    public void CutReentrantEdge_IsRepairedByARebuild_NotRefusedAndNotAccepted(double h)
    {
        // An L-shaped bar whose faces are tessellated so that surface points sit half an
        // element from the reentrant edge on both sides: the first triangulation chamfers
        // that edge by 0.26 to 0.30 of an element, which is over T_D.
        var ell = new GridSolid(new[] { 0, 0.5, 1 }, new[] { 0, 0.5, 1 }, new double[] { 0, 1 },
            p => !GridSolid.In(p, 0.5, 2, 0.5, 2, -1, 2)).ToGeometry();
        var mesh = Delaunay(ell, h);

        AssertAudited(mesh);
        Assert.Contains(mesh.Audit!.Warnings, w => w.Contains("feature edges seeded twice as densely"));
        Assert.True(mesh.Audit["A8"]!.Observed <= 0.01 * h, mesh.Audit["A8"]!.Detail);
        Assert.InRange(mesh.TotalVolume(), 0.75 * 0.999, 0.75 * 1.001);
    }

    [Fact]
    public void PitLeftByTheSliverCull_IsRepairedByARebuild()
    {
        // The reference beam at two elements through its thickness: the first triangulation
        // has a pit half an element deep (interior points on the skin).
        var mesh = Delaunay(PrimitiveFactory.CreateBox(0.2, 0.06, 0.02), 0.010);
        AssertAudited(mesh);
        Assert.Contains(mesh.Audit!.Warnings, w => w.Contains("interior point(s) on the skin (pits)"));
        Assert.True(mesh.Audit["A7"]!.Observed <= 0.002 * 0.010, mesh.Audit["A7"]!.Detail);
    }

    [Fact]
    public void GeneratedMesh_CarriesItsAuditReport_ThroughTheQuadraticUpgrade()
    {
        var box = PrimitiveFactory.CreateBox(1, 1, 1);
        AssertAudited(Delaunay(box, 0.25));
        var quadratic = Delaunay(box, 0.25, ElementOrder.Quadratic);
        Assert.True(quadratic.IsQuadratic);
        AssertAudited(quadratic);
    }

    [Fact]
    public void LatticeMesh_IsAudited_WithoutTheResolutionGuard()
    {
        // One cell through a plate: coarse, but every plane is exact - not a lost feature.
        var plate = PrimitiveFactory.CreatePlate(1, 1, 0.05);
        var settings = new MeshSettings { Method = MeshMethod.StructuredLattice, Divisions = new LatticeDivisions(4, 4, 1) };

        var tets = new StructuredLatticeMeshGenerator().Generate(plate, settings);
        Assert.True(tets.Audit!.Passed, tets.Audit.Describe());
        Assert.Equal(MeshAuditOutcome.Skipped, tets.Audit["A0"]!.Outcome);

        var hexes = new StructuredLatticeMeshGenerator().Generate(plate,
            settings with { Shape = ElementShape.Hexahedral, ElementOrder = ElementOrder.Quadratic });
        Assert.True(hexes.IsHex);
        Assert.True(hexes.Audit!.Passed, hexes.Audit.Describe());
        Assert.Equal(MeshAuditOutcome.Passed, hexes.Audit["A3"]!.Outcome);
    }

    // ------------------------------------------------------------------ refusals out of the mesher

    [Fact]
    public void Delaunay_RefusesAPlateThinnerThanTwoElements_BeforeMeshing()
    {
        var plate = PrimitiveFactory.CreatePlate(1, 1, 0.06);
        var ex = Assert.Throws<MeshAuditException>(() => Delaunay(plate, 0.2));
        Assert.Equal("A0", Assert.Single(ex.Report.Checks).Id);
        Assert.Contains("thickness = 0.06", ex.Message);
        Assert.Contains("reduce h to at most 0.03", ex.Message);
    }

    [Theory]
    [InlineData(0.005)]
    [InlineData(0.02)]
    [InlineData(0.1)]
    public void TwoCubesAHairApart_AreRefused_NotWeldedIntoOneBody(double gap)
    {
        // F01: at h = 0.2 these meshed as one connected domain with 300+ spanning elements
        // and a volume of 1.9997 of 2.0.
        var cubes = new GridSolid(new[] { 0, 1, 1 + gap, 2 + gap }, new double[] { 0, 1 }, new double[] { 0, 1 },
            p => p.X < 1 || p.X > 1 + gap).ToGeometry();
        var ex = Assert.Throws<MeshAuditException>(() => Delaunay(cubes, 0.2));
        Assert.Contains("clearance", ex.Message);
        Assert.Contains("below mesh resolution", ex.Message);
    }

    // ------------------------------------------------------------------ the automatic edge length

    [Fact]
    public void AutomaticEdgeLength_IsMadeFineEnoughToResolveTheThinnestWall()
    {
        // The reference beam: 20 mm thick, automatic size 14 mm (a fifteenth of the
        // diagonal). Left there, "auto" could not mesh the project's own benchmark part.
        var beam = PrimitiveFactory.CreateBox(0.2, 0.06, 0.02);
        var mesh = new DelaunayMeshGenerator().Generate(beam, new MeshSettings());

        Assert.Equal(0.01, mesh.Audit!.EdgeLength, 12);
        Assert.True(mesh.Audit.Passed, mesh.Audit.Describe());
        Assert.Contains(mesh.Audit.Warnings, w => w.Contains("Automatic edge length reduced from 0.014 m to 0.01 m"));
    }

    [Fact]
    public void AutomaticEdgeLength_IsLeftAlone_WhenItAlreadyResolvesThePart()
    {
        var cube = PrimitiveFactory.CreateBox(1, 1, 1);
        var mesh = new DelaunayMeshGenerator().Generate(cube, new MeshSettings());
        Assert.Equal(Math.Sqrt(3) / 15, mesh.Audit!.EdgeLength, 12);
        Assert.Empty(mesh.Audit.Warnings);
    }

    [Fact]
    public void AutomaticEdgeLength_DoesNotChaseAVeryThinFeature_AndSaysWhatSizeWould()
    {
        // 1 x 1 x 0.01: resolving it means an edge length 19 times finer than automatic.
        var sheet = PrimitiveFactory.CreatePlate(1, 1, 0.01);
        var ex = Assert.Throws<MeshAuditException>(() => new DelaunayMeshGenerator().Generate(sheet, new MeshSettings()));
        Assert.Contains("thickness = 0.01", ex.Message);
        Assert.Contains("reduce h to at most 0.005", ex.Message);
    }

    [Fact]
    public void ExplicitEdgeLength_IsNeverAdjusted()
    {
        var beam = PrimitiveFactory.CreateBox(0.2, 0.06, 0.02);
        Assert.Throws<MeshAuditException>(() => Delaunay(beam, 0.014));
    }

    // ------------------------------------------------------------------ correct meshes of awkward solids

    [Fact]
    public void TwoCubesThreeElementsApart_MeshAsTwoBodies()
    {
        var cubes = new GridSolid(new[] { 0, 1, 1.6, 2.6 }, new double[] { 0, 1 }, new double[] { 0, 1 },
            p => p.X < 1 || p.X > 1.6).ToGeometry();
        AssertAudited(Delaunay(cubes, 0.2), regions: 2);
    }

    private static readonly double[] Thirds = { 0, 0.3, 0.7, 1 };

    private static TriangleMesh HollowCubeFromStl()
    {
        var hollow = new GridSolid(Thirds, Thirds, Thirds, p => !GridSolid.In(p, 0.3, 0.7, 0.3, 0.7, 0.3, 0.7)).ToGeometry();
        var stl = new StringBuilder("solid hollow\n");
        foreach (var t in hollow.Triangles)
        {
            stl.Append("facet normal 0 0 0\nouter loop\n");
            foreach (var v in new[] { hollow.Vertices[t.A], hollow.Vertices[t.B], hollow.Vertices[t.C] })
                stl.Append(CultureInfo.InvariantCulture, $"vertex {v.X:R} {v.Y:R} {v.Z:R}\n");
            stl.Append("endloop\nendfacet\n");
        }
        stl.Append("endsolid hollow\n");
        string path = Path.Combine(Path.GetTempPath(), $"opensim-hollow-{Guid.NewGuid():N}.stl");
        File.WriteAllText(path, stl.ToString());
        try { return new StlImporter().Import(path); }
        finally { File.Delete(path); }
    }

    private static TriangleMesh HollowCubeFromStep()
    {
        // BREP_WITH_VOIDS: an outer box and one void shell, oriented into the cavity.
        var b = new StepFixtures.Builder();
        b.AddUnits(StepFixtures.Unit.Metre);
        int outerSolid = StepFixtures.EmitBox(b, (0, 0, 0), 1, 1, 1);
        int innerSolid = StepFixtures.EmitBox(b, (0.3, 0.3, 0.3), 0.4, 0.4, 0.4);
        int voidShell = b.Add($"ORIENTED_CLOSED_SHELL('',*,#{innerSolid - 1},.F.)");
        b.Add($"BREP_WITH_VOIDS('',#{outerSolid - 1},(#{voidShell}))");
        // EmitBox wraps each shell in a solid of its own; only the voided solid is wanted.
        var lines = b.Render().Split('\n')
            .Where(l => !l.StartsWith($"#{outerSolid}=") && !l.StartsWith($"#{innerSolid}="));
        return new StepImporter().ImportText(string.Join('\n', lines)).Mesh;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HollowSolid_FromStlAndFromStep_MeshesAsOneRegionWithTwoShells(bool step)
    {
        var geometry = step ? HollowCubeFromStep() : HollowCubeFromStl();
        var audit = MeshAudit.Begin(geometry, 0.1);
        Assert.Equal(new[] { 0, 1 }, audit.Geometry.Shells.Select(s => s.Depth).OrderBy(d => d));
        Assert.Equal(1 - 0.064, audit.Geometry.RegionVolumes.Single(), 9);

        var mesh = Delaunay(geometry, 0.1);
        AssertAudited(mesh);
        Assert.Contains("2 shell(s)", mesh.Audit!["A9"]!.Detail);
        Assert.InRange(mesh.TotalVolume(), 0.936 * 0.99, 0.936 * 1.01);
    }

    [Fact]
    public void SolidIslandInsideACavity_MeshesAsTwoRegions()
    {
        double[] planes = { 0, 0.3, 0.6, 0.9, 1.2, 1.5 };
        var geometry = new GridSolid(planes, planes, planes,
            p => !GridSolid.In(p, 0.3, 1.2, 0.3, 1.2, 0.3, 1.2) || GridSolid.In(p, 0.6, 0.9, 0.6, 0.9, 0.6, 0.9)).ToGeometry();
        var mesh = Delaunay(geometry, 0.1);
        AssertAudited(mesh, regions: 2);
        Assert.Contains("3 shell(s)", mesh.Audit!["A9"]!.Detail);
    }

    [Fact]
    public void BoreThreeElementsWide_Passes()
    {
        var bored = new GridSolid(new[] { 0, 0.5, 0.8, 1.3 }, new[] { 0, 0.5, 0.8, 1.3 }, new[] { 0, 0.5 },
            p => !GridSolid.In(p, 0.5, 0.8, 0.5, 0.8, -1, 2)).ToGeometry();
        AssertAudited(Delaunay(bored, 0.1));
    }

    [Fact]
    public void SlotThreeElementsWide_Passes()
    {
        var slotted = new GridSolid(new[] { 0, 0.5, 0.8, 1.3 }, new[] { 0, 0.4, 1 }, new[] { 0, 0.5 },
            p => !GridSolid.In(p, 0.5, 0.8, 0.4, 2, -1, 1)).ToGeometry();
        AssertAudited(Delaunay(slotted, 0.1));
    }

    [Fact]
    public void PlateThreeElementsThick_Passes()
    {
        AssertAudited(Delaunay(PrimitiveFactory.CreatePlate(2, 1.5, 0.3), 0.1));
    }

    [Fact]
    public void FilletOfRadiusTwoElements_Passes()
    {
        // A unit bar with one edge rounded at r = 2·h, the arc cut into 3.75° facets.
        const double h = 0.1, r = 2 * h;
        var polygon = new List<(double, double)> { (0, 0), (1, 0) };
        for (int i = 0; i <= 24; i++)
        {
            // Counter-clockwise: up the side to (1, 1 − r), round the corner to (1 − r, 1).
            double angle = Math.PI / 2 * i / 24;
            polygon.Add((1 - r + r * Math.Cos(angle), 1 - r + r * Math.Sin(angle)));
        }
        polygon.Add((0, 1));
        var bar = new SurfaceBuilder().AddPrism(polygon, (0.5, 0.5), 0, 1).ToGeometry();
        AssertAudited(Delaunay(bar, h));
    }

    // ------------------------------------------------------------------ meshes that did not come from a mesher

    private static Body BodyWithUnauditedMesh(TriangleMesh geometry, FeMesh mesh, MeshSettings settings) => new()
    {
        Name = "part",
        Geometry = geometry,
        MeshSettings = settings,
        // What a project file gives back: the same mesh, without its report.
        Mesh = new FeMesh(mesh.Nodes, mesh.Elements, mesh.BoundaryTriangles, mesh.ElementRegionIds,
            mesh.MidEdgeNodes, mesh.HexElements, mesh.HexMidEdgeNodes, mesh.BoundaryQuads)
    };

    [Fact]
    public void Gate_AuditsAMeshLoadedWithoutAReport_AndLeavesAnAuditedOneAlone()
    {
        var box = PrimitiveFactory.CreateBox(1, 1, 1);
        var settings = new MeshSettings { TargetEdgeLength = 0.25 };
        var generated = new DelaunayMeshGenerator().Generate(box, settings);
        var body = BodyWithUnauditedMesh(box, generated, settings);
        Assert.Null(body.Mesh!.Audit);

        var report = MeshAuditGate.AuditIfNeeded(body);
        Assert.NotNull(report);
        Assert.True(report!.Passed);

        body.Mesh = body.Mesh.WithAudit(report);
        Assert.Null(MeshAuditGate.AuditIfNeeded(body));
    }

    [Fact]
    public void Gate_RefusesAStoredMeshThatIsNotItsGeometry()
    {
        // A project whose stored mesh welds two bodies (what F01 produced) must not solve.
        var cubes = new GridSolid(new[] { 0, 1, 1.6, 2.6 }, new double[] { 0, 1 }, new double[] { 0, 1 },
            p => p.X < 1 || p.X > 1.6, maxCell: 0.2);
        var welded = new GridSolid(new[] { 0, 1, 1.6, 2.6 }, new double[] { 0, 1 }, new double[] { 0, 1 }, _ => true, maxCell: 0.2);
        var geometry = cubes.ToGeometry();
        var body = BodyWithUnauditedMesh(geometry, welded.ToMesh(geometry), new MeshSettings { TargetEdgeLength = 0.2 });

        var ex = Assert.Throws<MeshAuditException>(() => MeshAuditGate.AuditIfNeeded(body));
        Assert.Contains("separate bodies were meshed as one", ex.Message);
    }

    [Fact]
    public void Gate_AuditsALatticeMeshTheWayItsMesherDoes()
    {
        var plate = PrimitiveFactory.CreatePlate(1, 1, 0.05);
        var settings = new MeshSettings { Method = MeshMethod.StructuredLattice, Divisions = new LatticeDivisions(4, 4, 1) };
        var body = BodyWithUnauditedMesh(plate, new StructuredLatticeMeshGenerator().Generate(plate, settings), settings);

        var report = MeshAuditGate.AuditIfNeeded(body);
        Assert.True(report!.Passed, report.Describe());
        Assert.Equal(MeshAuditOutcome.Skipped, report["A0"]!.Outcome);
    }

    [Fact]
    public void Gate_LeavesPcbBodiesAndBodiesWithoutGeometryAlone()
    {
        var box = PrimitiveFactory.CreateBox(1, 1, 1);
        var settings = new MeshSettings { TargetEdgeLength = 0.25 };
        var mesh = new DelaunayMeshGenerator().Generate(box, settings);

        var pcb = BodyWithUnauditedMesh(box, mesh, settings);
        pcb.RegionMaterialNames = new Dictionary<int, string> { [0] = "Copper" };
        Assert.Null(MeshAuditGate.AuditIfNeeded(pcb));

        var bare = BodyWithUnauditedMesh(box, mesh, settings);
        bare.Geometry = null;
        Assert.Null(MeshAuditGate.AuditIfNeeded(bare));
    }
}
