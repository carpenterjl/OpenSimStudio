using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Meshing;

namespace OpenSim.Tests.Meshing;

/// <summary>
/// A10 — local thickness and clearance by ray casting — against the counterexamples that
/// shaped it. Two kinds: skins that are a legitimate approximation of the geometry
/// (coarser faceting, a rotated faceting, an offset within tolerance, a chamfered convex
/// corner) and must PASS, and skins whose distances to the geometry are all within
/// tolerance but which are a different solid (thinned, hollowed, relocated, shallowed) and
/// must FAIL. The second kind are all features below the mesh resolution, which A0 now
/// refuses outright; they are audited here with A0 bypassed, to show the two defences
/// are independent.
/// </summary>
public class MeshAuditRayTests
{
    private static MeshAuditReport Skin(SurfaceBuilder geometry, SurfaceBuilder skin, double h, double tolerance,
        out MeshAudit audit, bool bypassResolutionGuard = false)
    {
        audit = MeshAudit.Begin(geometry.ToGeometry(), h,
            new MeshAuditOptions { DistanceTolerance = tolerance, SkipResolutionGuard = bypassResolutionGuard });
        return audit.EvaluateSkin(skin.Vertices, skin.Triangles);
    }

    private static void AssertSkinPasses(MeshAuditReport report)
    {
        Assert.True(report.Passed, report.Describe());
        Assert.Equal(MeshAuditOutcome.Passed, report["A10"]!.Outcome);
    }

    // ------------------------------------------------------------------ faceting is not a defect

    /// <summary>A 128-gon "cylinder" beside a box, and the same box with a coarser polygon.</summary>
    private static (SurfaceBuilder Geometry, SurfaceBuilder Skin) CylinderBesideBox(
        double radius, double centreX, double centreY, double length, int zSteps,
        (double X0, double X1, double Y0, double Y1) box,
        int skinSides, double skinRotation, double geometryRotation = 0, double skinRadius = double.NaN,
        double skinCentreY = double.NaN)
    {
        if (double.IsNaN(skinRadius)) skinRadius = radius;
        if (double.IsNaN(skinCentreY)) skinCentreY = centreY;
        var geometry = new SurfaceBuilder()
            .AddPrism(SurfaceBuilder.Polygon(128, radius, centreX, centreY, geometryRotation), (centreX, centreY), 0, length, zSteps)
            .AddBox(box.X0, box.X1, box.Y0, box.Y1, 0, length);
        var skin = new SurfaceBuilder()
            .AddPrism(SurfaceBuilder.Polygon(skinSides, skinRadius, centreX, skinCentreY, skinRotation), (centreX, skinCentreY), 0, length, zSteps)
            .AddBox(box.X0, box.X1, box.Y0, box.Y1, 0, length);
        return (geometry, skin);
    }

    [Fact]
    public void CoarserFaceting_OfACylinderBesideABox_Passes()
    {
        // Round 5: a single mesh ray missing at a facet silhouette rejected this.
        var (geometry, skin) = CylinderBesideBox(2, 5, 0, 4, 4, (-4, 0, -2, 2), skinSides: 32, skinRotation: 0);
        AssertSkinPasses(Skin(geometry, skin, h: 1, tolerance: 0.05, out _));
    }

    [Fact]
    public void RotatedFaceting_Passes()
    {
        // Round 7: the skin's silhouette sits 0.0048 inside the geometry's all the way round.
        var (geometry, skin) = CylinderBesideBox(2, 5, 0, 4, 4, (-4, 0, -2, 2), skinSides: 32,
            skinRotation: Math.PI / 32);
        AssertSkinPasses(Skin(geometry, skin, h: 1, tolerance: 0.05, out _));
    }

    [Fact]
    public void SilhouetteAlongAWholeNeighbouringFace_IsResolvedSampleBySample_NotBudgeted()
    {
        // Round 7: a cylinder 100 long beside a box whose face edges look straight past it.
        // Every ray from those edges clips one faceting and misses the other. There is
        // nothing to budget: each is settled by how closely the other ray passed.
        var (geometry, skin) = CylinderBesideBox(1, 3.2, 1, 100, 200, (-1, 0, 0, 2), skinSides: 32,
            skinRotation: Math.PI / 32);
        var report = Skin(geometry, skin, h: 0.5, tolerance: 0.025, out var audit);
        AssertSkinPasses(report);
        Assert.True(audit.ResolvedSilhouettes >= 300,
            $"only {audit.ResolvedSilhouettes} silhouettes of {audit.SampleCount} samples");
    }

    [Fact]
    public void ExactSkin_OfTheLongCylinder_Passes()
    {
        var (geometry, _) = CylinderBesideBox(1, 3.2, 1, 30, 60, (-1, 0, 0, 2), skinSides: 32, skinRotation: 0);
        AssertSkinPasses(Skin(geometry, geometry, h: 0.5, tolerance: 0.025, out _));
    }

    [Fact]
    public void SilhouetteMovedBeyondTolerance_Fails()
    {
        // The same cylinder with its radius reduced by three tolerances: the rays that
        // clip the geometry now miss the skin by more than a silhouette can explain.
        var (geometry, skin) = CylinderBesideBox(1, 3.2, 1, 30, 60, (-1, 0, 0, 2), skinSides: 32,
            skinRotation: Math.PI / 32, skinRadius: 1 - 3 * 0.025);
        var report = Skin(geometry, skin, h: 0.5, tolerance: 0.025, out _);
        Assert.Equal(MeshAuditOutcome.Failed, report["A10"]!.Outcome);
        Assert.Equal(MeshAuditOutcome.Failed, report["A8"]!.Outcome);
    }

    [Theory]
    [InlineData(16, Math.PI / 16, -Math.PI / 256, 0.4, 0.02)]     // 16-gon, sagitta 0.0192
    [InlineData(32, Math.PI / 32, 0.0, 0.2, 0.01)]                // 32-gon, sagitta 0.0048
    public void NearTangentRays_PassWhateverTheirIncidence(int sides, double skinRotation, double geometryRotation,
        double h, double tolerance)
    {
        // Round 8: any incidence cutoff only moves the grazing failure. There is none.
        var (geometry, skin) = CylinderBesideBox(1, 2, 0, 10, 50, (-1, 0, -1.5, 1.5), sides, skinRotation, geometryRotation);
        AssertSkinPasses(Skin(geometry, skin, h, tolerance, out _));
    }

    [Fact]
    public void CoarseEqualAreaFaceting_WithDihedralsAboveTheCreaseAngle_Passes()
    {
        // Round 10: an 11-gon's dihedrals are 32.7°, above the crease angle. Sheets cut at
        // MESH dihedrals split it into eleven; sheets taken from the geometry do not.
        double apothem = 0.986038, shift = 0.013962;
        var (geometry, skin) = CylinderBesideBox(1, 5, 0, 20, 40, (-4, 0, -2, 2), skinSides: 11, skinRotation: 0,
            skinRadius: apothem / Math.Cos(Math.PI / 11), skinCentreY: shift);
        // h = 0.99: a 128-gon of unit radius is 1.9994 across its flats, a hair under 2·h at h = 1.
        AssertSkinPasses(Skin(geometry, skin, h: 0.99, tolerance: 0.05, out _));
    }

    // ------------------------------------------------------------------ offsets and chamfers within tolerance

    [Fact]
    public void FinelyTessellatedPlane_OffsetWithinTolerance_Passes()
    {
        // Round 9: a target patch one triangle ring wide made this depend on how finely the
        // wedge's lower face happened to be triangulated. A sheet is the whole plane.
        var lower = Enumerable.Range(0, 101).Select(i => (X: 0.1 * i, Y: 0.01 * i));
        var geometry = new SurfaceBuilder().AddPrism(lower.Concat(new[] { (10.0, 2.0), (0.0, 2.0) }).ToList(), (5, 1.2), 0, 10, 100);
        var skin = new SurfaceBuilder().AddPrism(new[] { (0, -0.01), (10, 0.99), (10.0, 2.0), (0.0, 2.0) }, (5, 1.2), 0, 10);
        AssertSkinPasses(Skin(geometry, skin, h: 0.4, tolerance: 0.02, out _));
    }

    [Theory]
    [InlineData(0.025, 0.025)]     // round 9 and 11: 45° to both faces, Hausdorff 0.0177
    [InlineData(0.015, 0.003)]     // round 12: 78.7° from one face, 11.3° from the other, Hausdorff 0.0029
    public void ConvexCornerChamferedWithinTolerance_Passes(double alongX, double alongY)
    {
        // The chamfer facet stands in for BOTH faces of the crease it replaces: rays from
        // either face strike it, and it belongs to both sheets (positive dot with each).
        var geometry = new SurfaceBuilder().AddBox(0, 1.2, 0, 10, 0, 10);
        var skin = new SurfaceBuilder().AddPrism(
            new[] { (alongX, 0.0), (1.2, 0.0), (1.2, 10.0), (0.0, 10.0), (0.0, alongY) }, (0.6, 5), 0, 10);
        AssertSkinPasses(Skin(geometry, skin, h: 0.4, tolerance: 0.02, out _));
    }

    [Fact]
    public void ConvexCornerChamferedBeyondTolerance_Fails()
    {
        var geometry = new SurfaceBuilder().AddBox(0, 1.2, 0, 10, 0, 10);
        var skin = new SurfaceBuilder().AddPrism(
            new[] { (0.1, 0.0), (1.2, 0.0), (1.2, 10.0), (0.0, 10.0), (0.0, 0.1) }, (0.6, 5), 0, 10);
        var report = Skin(geometry, skin, h: 0.4, tolerance: 0.02, out _);
        Assert.Equal(MeshAuditOutcome.Failed, report["A8"]!.Outcome);
        Assert.Equal(MeshAuditOutcome.Failed, report["A10"]!.Outcome);
    }

    // ------------------------------------------------------------------ the accepted refusal

    /// <summary>[−1, 1]² less the wedge {x ≥ 0, |y| &lt; x/√3}, extruded over z ∈ [0, 10];
    /// optionally with the notch tip filled by the triangle O–A–B.</summary>
    internal static SurfaceBuilder Notch(bool chamfered)
    {
        double s = 1 / Math.Sqrt(3);
        var polygon = new List<(double, double)> { (-1, -1), (1, -1), (1, -s) };
        if (chamfered)
        {
            polygon.Add((0.02, -0.02 * s));
            polygon.Add((0.001, 0.001 * s));
        }
        else
        {
            polygon.Add((0, 0));
        }
        polygon.AddRange(new[] { (1, s), (1.0, 1.0), (-1.0, 1.0) });
        return new SurfaceBuilder().AddPrism(polygon, (-0.5, 0), 0, 10);
    }

    [Fact]
    public void FacetReplacingAReentrantCrease_WithinTolerance_Passes()
    {
        // Round 13. The chamfer is 0.0012 from the geometry - well inside tolerance - but
        // stands at 117 degrees to the notch wall it replaces, so it belongs to the other
        // wall's sheet only. The plan accepted refusing it as the price of having no
        // exemption band around creases. It passes here without one: the ray that crosses
        // the upper wall in the geometry crosses the chamfer in the mesh, and each of those
        // is a silhouette of the other ray - the surface is there within tolerance, facing
        // the right way, and the other ray passes it without crossing.
        var report = Skin(Notch(chamfered: false), Notch(chamfered: true), h: 0.4, tolerance: 0.02, out var audit,
            bypassResolutionGuard: true);
        AssertSkinPasses(report);
        Assert.True(audit.ResolvedSilhouettes > 0);
    }

    [Fact]
    public void NotchTipFilledBeyondTolerance_Fails()
    {
        // The same notch with its tip filled ten times further: no longer a silhouette.
        double s = 1 / Math.Sqrt(3);
        var filled = new SurfaceBuilder().AddPrism(new[]
        {
            (-1.0, -1.0), (1.0, -1.0), (1.0, -s), (0.2, -0.2 * s), (0.2, 0.2 * s), (1.0, s), (1.0, 1.0), (-1.0, 1.0)
        }, (-0.5, 0), 0, 10);
        var report = Skin(Notch(chamfered: false), filled, h: 0.4, tolerance: 0.02, out _, bypassResolutionGuard: true);
        Assert.Equal(MeshAuditOutcome.Failed, report["A8"]!.Outcome);
        Assert.Equal(MeshAuditOutcome.Failed, report["A10"]!.Outcome);
    }

    [Fact]
    public void ExactSkinOfTheNotch_Passes()
    {
        AssertSkinPasses(Skin(Notch(chamfered: false), Notch(chamfered: false), h: 0.1, tolerance: 0.005, out _));
    }

    // ------------------------------------------------------------------ different solids within tolerance

    /// <summary>The full audit with A0 bypassed, on a geometry carrying ONE face id - so
    /// that nothing here leans on face ids (A5), which a real geometry may not have.</summary>
    private static MeshAuditReport Bypassed(GridSolid geometry, GridSolid mesh, double h, double tolerance)
    {
        var raw = geometry.ToGeometry();
        var surface = new TriangleMesh(raw.Vertices, raw.Triangles, new int[raw.Triangles.Count]);
        return MeshAudit.Begin(surface, h, new MeshAuditOptions { DistanceTolerance = tolerance, SkipResolutionGuard = true })
            .Evaluate(mesh.ToMesh());
    }

    private static void AssertOnlyTheRaysFail(MeshAuditReport report)
    {
        foreach (string id in new[] { "A1", "A2", "A3", "A4", "A6", "A7", "A8", "A9" })
            Assert.True(report[id]!.Outcome == MeshAuditOutcome.Passed, $"{id} should pass: {report.Describe()}");
        Assert.Equal(MeshAuditOutcome.Skipped, report["A5"]!.Outcome);
        Assert.True(report["A10"]!.Outcome == MeshAuditOutcome.Failed, report.Describe());
    }

    private static readonly double[] Unit = { 0, 1 };

    [Fact]
    public void RelocatedTunnel_PassesEveryDistanceAndTopologyCheck_AndFailsTheRays()
    {
        // Round 3: a 4 µm tunnel moved sideways by 7 µm in a metre cube meshed at 0.2 m.
        // Volume, area, genus and both distances all match.
        double w = 4e-6, shift = 7e-6;
        GridSolid Tunnel(double at) => new(new[] { 0, 0.5, 0.5 + w, 0.5 + shift, 0.5 + shift + w, 1 },
            new[] { 0, 0.5, 0.5 + w, 1 }, Unit, p => !GridSolid.In(p, at, at + w, 0.5, 0.5 + w, -1, 2));

        var report = Bypassed(Tunnel(0.5), Tunnel(0.5 + shift), h: 0.2, tolerance: 0.01);
        AssertOnlyTheRaysFail(report);
    }

    [Fact]
    public void TabThinnedAboutItsMidplane_PassesEveryDistanceAndTopologyCheck_AndFailsTheRays()
    {
        // Round 4: a 4 µm tab thinned to a tenth, with a full-thickness collar round its edge.
        double t = 4e-6, z0 = 0.5;
        double[] x = { 0, 1, 1.48, 1.5 }, y = { 0, 0.4, 0.42, 0.58, 0.6, 1 };
        double[] z = { 0, z0, z0 + 0.45 * t, z0 + 0.55 * t, z0 + t, 1 };
        var tab = new GridSolid(x, y, z, p => p.X < 1 || GridSolid.In(p, 1, 1.5, 0.4, 0.6, z0, z0 + t));
        var thinned = new GridSolid(x, y, z, p => p.X < 1
            || GridSolid.In(p, 1, 1.5, 0.4, 0.6, z0 + 0.45 * t, z0 + 0.55 * t)
            || (GridSolid.In(p, 1, 1.5, 0.4, 0.6, z0, z0 + t) && !GridSolid.In(p, 1, 1.48, 0.42, 0.58, -1, 2)));

        var report = Bypassed(tab, thinned, h: 0.2, tolerance: 0.01);
        AssertOnlyTheRaysFail(report);
    }

    [Fact]
    public void TabHollowedToASkin_PassesEveryDistanceAndTopologyCheck_AndFailsTheRays()
    {
        // Round 6: a 100 x 1 x 4 µm tab emptied through its free end, leaving walls a fifth
        // to a half of a micron thick. One shell, genus 0, every point within tolerance.
        const double um = 1e-6;
        double[] x = { 0, 200 * um, 300 * um };
        double[] y = { 0, 100 * um, 100.2 * um, 100.8 * um, 101 * um, 200 * um };
        double[] z = { 0, 100 * um, 100.5 * um, 103.5 * um, 104 * um, 200 * um };
        bool Tab(Vector3D p) => GridSolid.In(p, 200 * um, 300 * um, 100 * um, 101 * um, 100 * um, 104 * um);
        var tab = new GridSolid(x, y, z, p => p.X < 200 * um || Tab(p));
        var hollowed = new GridSolid(x, y, z, p => p.X < 200 * um
            || (Tab(p) && !GridSolid.In(p, 200 * um, 400 * um, 100.2 * um, 100.8 * um, 100.5 * um, 103.5 * um)));

        var report = Bypassed(tab, hollowed, h: 20 * um, tolerance: 1 * um);
        AssertOnlyTheRaysFail(report);
    }

    /// <summary>A 1 mm cube with a groove 0.5 µm wide and 100 µm long in its top face.</summary>
    internal static GridSolid GrooveInMillimetreCube(double depth)
    {
        const double mm = 1e-3, um = 1e-6;
        return new GridSolid(new[] { 0, 0.5 * mm, 0.5 * mm + 0.5 * um, mm }, new[] { 0, 0.45 * mm, 0.55 * mm, mm },
            new[] { 0, mm - 0.9 * um, mm - 0.09 * um, mm },
            p => !GridSolid.In(p, 0.5 * mm, 0.5 * mm + 0.5 * um, 0.45 * mm, 0.55 * mm, mm - depth, 2 * mm));
    }

    [Fact]
    public void GrooveMeshedAtATenthOfItsDepth_PassesEveryDistanceAndTopologyCheck_AndFailsTheRays()
    {
        // Round 14, with A0 bypassed: the groove floor faces across the walls (zero normal
        // dot), so with no crease-band exception it cannot stand in for them.
        var report = Bypassed(GrooveInMillimetreCube(depth: 0.9e-6), GrooveInMillimetreCube(depth: 0.09e-6),
            h: 20e-6, tolerance: 1e-6);
        AssertOnlyTheRaysFail(report);
    }

    // ------------------------------------------------------------------ the same defects at a resolvable size

    private const double H = 0.1;

    private static MeshAuditReport Full(GridSolid geometry, GridSolid mesh)
    {
        var surface = geometry.ToGeometry();
        return MeshAudit.Begin(surface, H).Evaluate(mesh.ToMesh(surface));
    }

    private static void AssertRaysFailAtResolution(MeshAuditReport report)
    {
        Assert.Equal(MeshAuditOutcome.Passed, report["A0"]!.Outcome);
        Assert.True(report["A10"]!.Outcome == MeshAuditOutcome.Failed, report.Describe());
        Assert.False(report.Passed);
    }

    private static readonly double[] Wide = { 0, 2 };

    [Fact]
    public void TunnelThreeElementsWide_Relocated_Fails()
    {
        double shift = 3 * MeshAudit.DistanceFactor * H;
        GridSolid Tunnel(double at) => new(new[] { 0, 0.8, 0.8 + shift, 1.1, 1.1 + shift, 2 }, new[] { 0, 0.8, 1.1, 2 }, Wide,
            p => !GridSolid.In(p, at, at + 0.3, 0.8, 1.1, -1, 3), maxCell: H);
        AssertRaysFailAtResolution(Full(Tunnel(0.8), Tunnel(0.8 + shift)));
    }

    private static GridSolid Tab(Func<Vector3D, bool> tabInterior) =>
        new(new[] { 0, 2, 2.95, 3 }, new[] { 0, 0.5, 0.55, 1.45, 1.5, 2 },
            new[] { 0, 0.85, 0.85 + 0.004, 0.9, 1.1, 1.15 - 0.004, 1.15, 2 },
            p => p.X < 2 || (GridSolid.In(p, 2, 3, 0.5, 1.5, 0.85, 1.15) && tabInterior(p)), maxCell: H);

    [Fact]
    public void TabThreeElementsThick_MeshedCorrectly_Passes()
    {
        var tab = Tab(_ => true);
        var report = Full(tab, tab);
        Assert.True(report.Passed, report.Describe());
    }

    [Fact]
    public void TabThreeElementsThick_ThinnedToTwoWithACollar_Fails()
    {
        // Thinned to 2·h about its midplane, full thickness kept in a collar round the edge.
        var thinned = Tab(p => p.Z is > 0.9 and < 1.1 || !GridSolid.In(p, 2, 2.95, 0.55, 1.45, -1, 3));
        AssertRaysFailAtResolution(Full(Tab(_ => true), thinned));
    }

    [Fact]
    public void TabThreeElementsThick_HollowedToItsSkin_Fails()
    {
        // Emptied through its free end, leaving walls thinner than the distance tolerance.
        var hollowed = Tab(p => !GridSolid.In(p, 2, 4, 0.55, 1.45, 0.854, 1.146));
        AssertRaysFailAtResolution(Full(Tab(_ => true), hollowed));
    }

    [Fact]
    public void GrooveThreeElementsWideAndDeep_MeshedAtAThirdOfItsDepth_Fails()
    {
        GridSolid Groove(double depth) => new(new[] { 0, 0.85, 1.15, 2 }, Wide, new[] { 0, 1.7, 1.9, 2 },
            p => !GridSolid.In(p, 0.85, 1.15, -1, 3, 2 - depth, 3), maxCell: H);
        var correct = Groove(0.3);
        Assert.True(Full(correct, correct).Passed);
        AssertRaysFailAtResolution(Full(correct, Groove(0.1)));
    }
}
