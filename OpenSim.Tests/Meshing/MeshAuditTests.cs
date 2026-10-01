using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;

namespace OpenSim.Tests.Meshing;

/// <summary>
/// The post-mesh audit against meshes that are wrong in a KNOWN way. Each fixture is a
/// geometry and a mesh of a deliberately different solid (see <see cref="GridSolid"/>), so
/// the assertion names the check that must catch it — and the controls beside it are the
/// same solids meshed correctly, which must pass everything.
/// </summary>
public class MeshAuditTests
{
    private const double H = 0.1;

    private static MeshAuditReport Audit(GridSolid geometry, GridSolid mesh, double h = H,
        MeshAuditOptions? options = null)
    {
        var surface = geometry.ToGeometry();
        return MeshAudit.Begin(surface, h, options).Evaluate(mesh.ToMesh(surface));
    }

    private static void AssertFailed(MeshAuditReport report, string id, string? fragment = null)
    {
        var check = report[id];
        Assert.NotNull(check);
        Assert.True(check!.Outcome == MeshAuditOutcome.Failed, $"{id} should have failed: {report.Describe()}");
        if (fragment is not null) Assert.Contains(fragment, check.Detail);
    }

    private static void AssertPassed(MeshAuditReport report, params string[] ids)
    {
        foreach (string id in ids)
            Assert.True(report[id]!.Outcome == MeshAuditOutcome.Passed, $"{id} should have passed: {report.Describe()}");
    }

    private static readonly double[] Unit = { 0, 1 };

    // ------------------------------------------------------------------ controls

    [Fact]
    public void ExactMeshOfACube_PassesEveryCheck()
    {
        var cube = new GridSolid(Unit, Unit, Unit, _ => true, maxCell: H);
        var report = Audit(cube, cube);
        Assert.True(report.Passed, report.Describe());
        Assert.Equal(new[] { "A0", "A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "A9", "A10" },
            report.Checks.Select(c => c.Id));
        Assert.All(report.Checks, c => Assert.Equal(MeshAuditOutcome.Passed, c.Outcome));
    }

    [Fact]
    public void Check_ThrowsATypedExceptionCarryingTheReport()
    {
        var cubes = TwoCubes(gap: 3 * H);
        var welded = new GridSolid(new[] { 0, 1, 1 + 3 * H, 2 + 3 * H }, Unit, Unit, _ => true, maxCell: H);
        var geometry = cubes.ToGeometry();

        var ex = Assert.Throws<MeshAuditException>(() => MeshAudit.Check(welded.ToMesh(geometry), geometry, H));
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.False(ex.Report.Passed);
        Assert.Contains("A6", ex.Message);
    }

    // ------------------------------------------------------------------ A6: bodies and regions

    private static GridSolid TwoCubes(double gap) =>
        new(new[] { 0, 1, 1 + gap, 2 + gap }, Unit, Unit, p => p.X < 1 || p.X > 1 + gap, maxCell: H);

    [Fact]
    public void TwoCubesThreeElementsApart_MeshedCorrectly_PassEverything()
    {
        var cubes = TwoCubes(gap: 3 * H);
        var report = Audit(cubes, cubes);
        Assert.True(report.Passed, report.Describe());
        Assert.Contains("2 regions", report["A6"]!.Detail);
    }

    [Fact]
    public void TwoCubesMeshedAsOneWeldedBody_FailConnectivity()
    {
        // The F01 reproduction at a gap the resolution guard allows: volume says nothing
        // useful (2.3 of 2.0 here, 1.9997 of 2.0 in the original), connectivity does.
        var welded = new GridSolid(new[] { 0, 1, 1 + 3 * H, 2 + 3 * H }, Unit, Unit, _ => true, maxCell: H);
        var report = Audit(TwoCubes(gap: 3 * H), welded);
        AssertPassed(report, "A0", "A1", "A2", "A3");
        AssertFailed(report, "A6", "separate bodies were meshed as one");
    }

    [Fact]
    public void ElementsSpanningTwoBodies_AreNamed_EvenWhenTheWeldIsThin()
    {
        // Two cubes half an element apart joined by a single layer of cells: the F01 weld
        // itself. A6 in isolation sees it; in the full audit A0 has already refused the gap.
        double gap = 0.5 * H;
        var welded = new GridSolid(new[] { 0, 1, 1 + gap, 2 + gap }, Unit, Unit, _ => true, maxCell: H);
        var report = Audit(TwoCubes(gap), welded);
        AssertFailed(report, "A6", "span two different material regions");
        AssertFailed(report, "A0", "below mesh resolution");
    }

    [Fact]
    public void TwoCubesHalfAnElementApart_MeshedCorrectly_PassConnectivityButNotTheResolutionGuard()
    {
        var cubes = TwoCubes(gap: 0.5 * H);
        var report = Audit(cubes, cubes);
        AssertPassed(report, "A6");
        AssertFailed(report, "A0", "clearance");
    }

    private static GridSolid HollowCube() =>
        new(new[] { 0, 0.3, 0.7, 1 }, new[] { 0, 0.3, 0.7, 1 }, new[] { 0, 0.3, 0.7, 1 },
            p => !GridSolid.In(p, 0.3, 0.7, 0.3, 0.7, 0.3, 0.7), maxCell: H);

    [Fact]
    public void HollowSolid_IsOneRegionWithTwoShells_AndPasses()
    {
        var hollow = HollowCube();
        var surface = hollow.ToGeometry();
        var audit = MeshAudit.Begin(surface, H);

        var shells = audit.Geometry.Shells;
        Assert.Equal(2, shells.Length);
        Assert.Equal(new[] { 0, 1 }, shells.Select(s => s.Depth).OrderBy(d => d));
        Assert.All(shells, s => Assert.Equal(0, s.Genus));
        Assert.Equal(1, audit.Geometry.RegionCount);
        Assert.Equal(1 - 0.4 * 0.4 * 0.4, audit.Geometry.RegionVolumes[0], 12);

        var report = audit.Evaluate(hollow.ToMesh(surface));
        Assert.True(report.Passed, report.Describe());
    }

    [Fact]
    public void HollowSolid_MeshedSolid_FailsShellCorrespondence()
    {
        var report = Audit(HollowCube(), new GridSolid(Unit, Unit, Unit, _ => true, maxCell: H));
        AssertFailed(report, "A9", "the geometry has 2 closed shell(s), the mesh skin has 1");
        AssertFailed(report, "A8");
        AssertFailed(report, "A4");
    }

    private static GridSolid IslandInCavity()
    {
        // Depths 0, 1, 2: a block, a cavity in it, and a solid island floating in the cavity.
        double[] planes = { 0, 0.3, 0.6, 0.9, 1.2, 1.5 };
        return new GridSolid(planes, planes, planes,
            p => !GridSolid.In(p, 0.3, 1.2, 0.3, 1.2, 0.3, 1.2) || GridSolid.In(p, 0.6, 0.9, 0.6, 0.9, 0.6, 0.9),
            maxCell: H);
    }

    [Fact]
    public void SolidIslandInsideACavity_IsTwoRegionsAndThreeShells_AndPasses()
    {
        var solid = IslandInCavity();
        var surface = solid.ToGeometry();
        var audit = MeshAudit.Begin(surface, H);

        Assert.Equal(new[] { 0, 1, 2 }, audit.Geometry.Shells.Select(s => s.Depth).OrderBy(d => d));
        Assert.Equal(2, audit.Geometry.RegionCount);
        Assert.Equal(new[] { 0.3 * 0.3 * 0.3, 1.5 * 1.5 * 1.5 - 0.9 * 0.9 * 0.9 },
            audit.Geometry.RegionVolumes.OrderBy(v => v).Select(v => Math.Round(v, 12)));

        var report = audit.Evaluate(solid.ToMesh(surface));
        Assert.True(report.Passed, report.Describe());
        Assert.Contains("2 regions", report["A6"]!.Detail);
        Assert.Contains("3 shell(s)", report["A9"]!.Detail);
    }

    [Fact]
    public void IslandDroppedFromTheMesh_FailsConnectivityAndShells()
    {
        double[] planes = { 0, 0.3, 0.6, 0.9, 1.2, 1.5 };
        var withoutIsland = new GridSolid(planes, planes, planes,
            p => !GridSolid.In(p, 0.3, 1.2, 0.3, 1.2, 0.3, 1.2), maxCell: H);
        var report = Audit(IslandInCavity(), withoutIsland);
        AssertFailed(report, "A6", "2 separate material region(s) but the mesh has 1");
        AssertFailed(report, "A9");
    }

    // ------------------------------------------------------------------ A8 / A5 / A9: filled features

    private static readonly double[] Wide = { 0, 2 };

    private static GridSolid CubeWithBore(double width, double at = 0.8) =>
        new(new[] { 0, at, at + width, 2 }, new[] { 0, at, at + width, 2 }, Wide,
            p => !GridSolid.In(p, at, at + width, at, at + width, -1, 3), maxCell: H);

    [Fact]
    public void BoreThreeElementsWide_MeshedCorrectly_PassesEverything()
    {
        var bored = CubeWithBore(3 * H);
        var surface = bored.ToGeometry();
        var audit = MeshAudit.Begin(surface, H);
        Assert.Equal(1.0, audit.Geometry.Shells.Single().Genus);
        var report = audit.Evaluate(bored.ToMesh(surface));
        Assert.True(report.Passed, report.Describe());
    }

    [Fact]
    public void FilledBore_FailsCoverageAndGenus_WithNoHelpFromFaceIds()
    {
        // The round-1 counterexample at a resolvable size: one face id on the whole
        // geometry, so nothing that depends on face ids can see the bore go.
        var bored = CubeWithBore(3 * H);
        var raw = bored.ToGeometry();
        var singleFace = new TriangleMesh(raw.Vertices, raw.Triangles, new int[raw.Triangles.Count]);
        var filled = new GridSolid(Wide, Wide, Wide, _ => true, maxCell: H);

        var report = MeshAudit.Begin(singleFace, H).Evaluate(filled.ToMesh());
        Assert.Equal(MeshAuditOutcome.Skipped, report["A5"]!.Outcome);
        AssertPassed(report, "A1", "A2", "A3", "A6");
        AssertFailed(report, "A8", "feature smaller than the target edge length");
        AssertFailed(report, "A9", "1 through-hole(s) in the geometry");
    }

    [Fact]
    public void BoreAndVoidBothFilled_FailOnShellCount_WhereAnAggregateEulerCharacteristicWouldNot()
    {
        // Round 2: a through-bore (χ 0) plus a closed void (χ 2) sum to χ 2 - a plain cube.
        var geometry = new GridSolid(new[] { 0, 0.3, 0.6, 1.4, 1.7, 2 }, new[] { 0, 0.3, 0.6, 1.4, 1.7, 2 },
            new[] { 0, 0.3, 0.6, 2 },
            p => !GridSolid.In(p, 0.3, 0.6, 0.3, 0.6, -1, 3) && !GridSolid.In(p, 1.4, 1.7, 1.4, 1.7, 0.3, 0.6),
            maxCell: H);
        var surface = geometry.ToGeometry();
        var audit = MeshAudit.Begin(surface, H);
        Assert.Equal(new[] { 0.0, 1.0 }, audit.Geometry.Shells.Select(s => s.Genus).OrderBy(g => g));

        var report = audit.Evaluate(new GridSolid(Wide, Wide, Wide, _ => true, maxCell: H).ToMesh(surface));
        AssertFailed(report, "A9", "the geometry has 2 closed shell(s), the mesh skin has 1");
    }

    private static GridSolid Bracket(bool slotted) =>
        new(new[] { 0, 0.5, 0.8, 1 }, new[] { 0, 0.4, 1 }, new[] { 0, 0.5 },
            p => !slotted || !GridSolid.In(p, 0.5, 0.8, 0.4, 2, -1, 1), maxCell: H);

    [Fact]
    public void ConcavityThreeElementsWide_MeshedCorrectly_Passes()
    {
        var bracket = Bracket(slotted: true);
        var report = Audit(bracket, bracket);
        Assert.True(report.Passed, report.Describe());
    }

    [Fact]
    public void BridgedConcavity_FailsCoverageAndFaceIds()
    {
        var report = Audit(Bracket(slotted: true), Bracket(slotted: false));
        AssertFailed(report, "A8", "filled or bridged");
        AssertFailed(report, "A5", "a face was bridged over or collapsed");
        AssertFailed(report, "A4");
        // Still one shell of genus 0: A9 cannot see a bridged slot, which is why A8 exists.
        AssertPassed(report, "A9");
    }

    // ------------------------------------------------------------------ A1 / A2 / A3

    [Fact]
    public void InvertedElement_FailsElementValidity()
    {
        var cube = new GridSolid(Unit, Unit, Unit, _ => true, maxCell: 0.5);
        var geometry = cube.ToGeometry();
        var mesh = cube.ToMesh(geometry);
        var elements = mesh.Elements.ToList();
        var e = elements[3];
        elements[3] = new Tet4(e.N0, e.N1, e.N3, e.N2);

        var report = MeshAudit.Begin(geometry, 0.5).Evaluate(new FeMesh(mesh.Nodes, elements, mesh.BoundaryTriangles));
        AssertFailed(report, "A1", "non-positive volume");
    }

    [Fact]
    public void FaceSharedByThreeElements_FailsElementValidity()
    {
        var cube = new GridSolid(Unit, Unit, Unit, _ => true, maxCell: 0.5);
        var geometry = cube.ToGeometry();
        var mesh = cube.ToMesh(geometry);
        var elements = mesh.Elements.ToList();
        var nodes = mesh.Nodes.ToList();
        // A third tetrahedron on an interior face of the first element.
        var e = elements[0];
        nodes.Add((nodes[e.N0] + nodes[e.N1] + nodes[e.N2] + nodes[e.N3]) / 4.0);
        var interior = InteriorFace(mesh, 0);
        int apex = nodes.Count - 1;
        elements.Add(Positive(nodes, interior.A, interior.B, interior.C, apex));

        var report = MeshAudit.Begin(geometry, 0.5).Evaluate(
            new FeMesh(nodes, elements, GridSolid.Skin(nodes, elements, geometry)));
        AssertFailed(report, "A1", "shared by more than two elements");
    }

    private static (int A, int B, int C) InteriorFace(FeMesh mesh, int element)
    {
        var skin = new HashSet<(int, int, int)>(mesh.BoundaryTriangles.Select(t => Sorted(t.A, t.B, t.C)));
        var e = mesh.Elements[element];
        foreach (var f in new[] { (e.N1, e.N2, e.N3), (e.N0, e.N2, e.N3), (e.N0, e.N1, e.N3), (e.N0, e.N1, e.N2) })
            if (!skin.Contains(Sorted(f.Item1, f.Item2, f.Item3))) return f;
        throw new InvalidOperationException("The element has no interior face.");
    }

    private static Tet4 Positive(IReadOnlyList<Vector3D> nodes, int a, int b, int c, int d) =>
        Vector3D.Dot(nodes[b] - nodes[a], Vector3D.Cross(nodes[c] - nodes[a], nodes[d] - nodes[a])) > 0
            ? new Tet4(a, b, c, d)
            : new Tet4(a, b, d, c);

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    [Fact]
    public void PinchedSkin_LeftByThePinchResolver_FailsManifoldness()
    {
        // What BoundaryPinchResolver's "unresolvable - leave as-is" path hands on: two
        // parts of the mesh touching along one edge, so that edge carries four skin faces.
        // Built directly: two cells of a 2 x 2 x 1 block sharing only their vertical edge.
        var pinched = new GridSolid(new[] { 0, 0.5, 1 }, new[] { 0, 0.5, 1 }, new[] { 0, 0.5 },
            p => (p.X < 0.5) == (p.Y < 0.5));
        var whole = new GridSolid(new[] { 0, 0.5, 1 }, new[] { 0, 0.5, 1 }, new[] { 0, 0.5 }, _ => true);
        var geometry = whole.ToGeometry();

        var report = MeshAudit.Begin(geometry, 0.25).Evaluate(pinched.ToMesh(geometry));
        AssertFailed(report, "A2", "not shared by exactly two skin triangles");
        Assert.Equal(MeshAuditOutcome.Skipped, report["A10"]!.Outcome);
    }

    [Fact]
    public void PinchTheResolverGivesUpOn_IsCaughtByTheAudit()
    {
        // BoundaryPinchResolver removes one tetrahedron per pass and stops after 256 passes,
        // leaving whatever is still pinched "as-is". 300 independent pinches - pairs of
        // tetrahedra sharing one edge and nothing else - are more than it gets through.
        var points = new List<Vector3D>();
        var kept = new List<(int, int, int, int)>();
        for (int pair = 0; pair < 300; pair++)
        {
            var origin = new Vector3D(3 * pair, 0, 0);
            int first = points.Count;
            points.AddRange(new[]
            {
                origin, origin + new Vector3D(0, 0, 1),                                   // the shared edge
                origin + new Vector3D(1, 0, 0), origin + new Vector3D(1, 1, 0),
                origin + new Vector3D(-1, 0, 0), origin + new Vector3D(-1, -1, 0)
            });
            kept.Add(Orient(points, first, first + 1, first + 2, first + 3));
            kept.Add(Orient(points, first, first + 1, first + 4, first + 5));
        }

        var resolved = BoundaryPinchResolver.Resolve(kept, points);
        Assert.Equal(600 - 256, resolved.Count);

        var elements = resolved.Select(t => new Tet4(t.A, t.B, t.C, t.D)).ToList();
        var mesh = new FeMesh(points, elements, GridSolid.Skin(points, elements));
        var report = MeshAudit.Begin(PrimitiveFactory.CreateBox(1, 1, 1), 0.5).Evaluate(mesh);
        AssertFailed(report, "A2", "44 skin edge(s) not shared by exactly two skin triangles");
    }

    private static (int, int, int, int) Orient(IReadOnlyList<Vector3D> p, int a, int b, int c, int d) =>
        GeometricPredicates.Orient3D(p[a], p[b], p[c], p[d]) > 0 ? (a, b, c, d) : (a, b, d, c);

    [Fact]
    public void SkinTriangleWoundInward_FailsClosure()
    {
        var cube = new GridSolid(Unit, Unit, Unit, _ => true, maxCell: 0.5);
        var geometry = cube.ToGeometry();
        var mesh = cube.ToMesh(geometry);
        var skin = mesh.BoundaryTriangles.ToList();
        skin[0] = new BoundaryTriangle(skin[0].A, skin[0].C, skin[0].B, skin[0].FaceId);

        var report = MeshAudit.Begin(geometry, 0.5).Evaluate(new FeMesh(mesh.Nodes, mesh.Elements, skin));
        AssertFailed(report, "A3", "wound inward");
    }

    [Fact]
    public void MissingSkinTriangle_FailsManifoldnessAndClosure()
    {
        var cube = new GridSolid(Unit, Unit, Unit, _ => true, maxCell: 0.5);
        var geometry = cube.ToGeometry();
        var mesh = cube.ToMesh(geometry);
        var skin = mesh.BoundaryTriangles.Skip(1).ToList();

        var report = MeshAudit.Begin(geometry, 0.5).Evaluate(new FeMesh(mesh.Nodes, mesh.Elements, skin));
        AssertFailed(report, "A2");
        AssertFailed(report, "A3", "exposed element faces");
    }
}
