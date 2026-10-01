using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using OpenSim.Meshing;

namespace OpenSim.Tests.Meshing;

/// <summary>
/// A0, the sub-resolution guard: a wall or a gap narrower than two elements is refused on
/// the geometry alone, before any mesh exists. These are the features the Delaunay pipeline
/// bridges, fills or drops, including the four the plan's adversarial review used to show
/// that no mesh-against-geometry comparison could be trusted below the mesh resolution.
/// </summary>
public class MeshAuditResolutionTests
{
    private static readonly double[] Unit = { 0, 1 };

    private static MeshAuditCheck A0(TriangleMesh geometry, double h) => MeshAudit.Begin(geometry, h).Resolution;

    private static void AssertRefused(MeshAuditCheck check, string dimension, double size, double h,
        Func<Vector3D, bool> near)
    {
        Assert.Equal(MeshAuditOutcome.Failed, check.Outcome);
        Assert.Contains("feature below mesh resolution near (", check.Detail);
        Assert.Contains($"{dimension} = ", check.Detail);
        Assert.Contains("reduce h", check.Detail);
        Assert.Equal(size, check.Observed!.Value, 9);
        Assert.Equal(2 * h, check.Limit!.Value, 12);
        Assert.True(near(check.Location!.Value), $"reported at {check.Location}");
    }

    [Fact]
    public void TwoCubesAFractionOfAnElementApart_AreRefusedOnTheGap_AndPassOnceItIsResolved()
    {
        var cubes = new GridSolid(new[] { 0, 1, 1.02, 2.02 }, Unit, Unit, p => p.X < 1 || p.X > 1.02).ToGeometry();

        AssertRefused(A0(cubes, 0.2), "clearance", 0.02, 0.2, p => p.X is > 0.99 and < 1.03);
        Assert.Equal(MeshAuditOutcome.Passed, A0(cubes, 0.01).Outcome);
    }

    [Fact]
    public void PlateThinnerThanTwoElements_IsRefusedOnThickness_AndPassesOnceItIsResolved()
    {
        var plate = PrimitiveFactory.CreatePlate(1, 1, 0.06);

        AssertRefused(A0(plate, 0.2), "thickness", 0.06, 0.2, _ => true);
        Assert.Equal(MeshAuditOutcome.Passed, A0(plate, 0.03).Outcome);
        // One step coarser than resolved is refused: the factor is 2, not "about 2".
        Assert.Equal(MeshAuditOutcome.Failed, A0(plate, 0.031).Outcome);
    }

    [Fact]
    public void BoreNarrowerThanAnElement_IsRefused_WithOneFaceIdOnTheWholeGeometry()
    {
        // Round 1: nothing that leans on face ids could see this bore filled.
        var raw = new GridSolid(new[] { 0, 0.5, 0.52, 1 }, new[] { 0, 0.5, 0.52, 1 }, Unit,
            p => !GridSolid.In(p, 0.5, 0.52, 0.5, 0.52, -1, 2)).ToGeometry();
        var bored = new TriangleMesh(raw.Vertices, raw.Triangles, new int[raw.Triangles.Count]);

        AssertRefused(A0(bored, 0.2), "clearance", 0.02, 0.2,
            p => p.X is > 0.49 and < 0.53 && p.Y is > 0.49 and < 0.53);
        Assert.Equal(MeshAuditOutcome.Passed, A0(bored, 0.01).Outcome);
    }

    [Fact]
    public void FourMicronTunnelInAMetreCube_IsRefused()
    {
        // Round 3: relocated rather than deleted, this tunnel passed every check then written.
        double w = 4e-6;
        var tunnel = new GridSolid(new[] { 0, 0.5, 0.5 + w, 1 }, new[] { 0, 0.5, 0.5 + w, 1 }, Unit,
            p => !GridSolid.In(p, 0.5, 0.5 + w, 0.5, 0.5 + w, -1, 2)).ToGeometry();

        AssertRefused(A0(tunnel, 0.2), "clearance", w, 0.2, p => Math.Abs(p.X - 0.5) < 1e-5 && Math.Abs(p.Y - 0.5) < 1e-5);
    }

    [Fact]
    public void FourMicronTabOnAMetreCube_IsRefused()
    {
        // Round 4: thinned to a tenth about its midplane, with a collar, it passed too.
        double t = 4e-6;
        var tab = new GridSolid(new[] { 0, 1, 1.5 }, new[] { 0, 0.4, 0.6, 1 }, new[] { 0, 0.5, 0.5 + t, 1 },
            p => p.X < 1 || GridSolid.In(p, 1, 1.5, 0.4, 0.6, 0.5, 0.5 + t)).ToGeometry();

        AssertRefused(A0(tab, 0.2), "thickness", t, 0.2, p => p.X > 1 - 1e-9);
    }

    [Fact]
    public void HundredByOneByFourMicronTab_IsRefused()
    {
        // Round 6: hollowed to two skins.
        const double um = 1e-6;
        var tab = new GridSolid(new[] { 0, 200 * um, 300 * um }, new[] { 0, 100 * um, 101 * um, 200 * um },
            new[] { 0, 100 * um, 104 * um, 200 * um },
            p => p.X < 200 * um || GridSolid.In(p, 200 * um, 300 * um, 100 * um, 101 * um, 100 * um, 104 * um)).ToGeometry();

        AssertRefused(A0(tab, 20 * um), "thickness", 1 * um, 20 * um, p => p.X > 200 * um - 1e-12);
    }

    [Fact]
    public void HalfMicronGrooveInAMillimetreCube_IsRefusedOnClearance()
    {
        // Round 14: meshed at a tenth of its depth, this groove passed A1-A10 as then
        // written. It never reaches a mesh comparison now.
        var groove = MeshAuditRayTests.GrooveInMillimetreCube(depth: 0.9e-6).ToGeometry();

        AssertRefused(A0(groove, 20e-6), "clearance", 0.5e-6, 20e-6,
            p => p.Z > 1e-3 - 1e-6 && Math.Abs(p.X - 0.5e-3) < 1e-6);
    }

    [Fact]
    public void SlotNarrowerThanTwoElements_IsRefused_AndPassesOnceItIsResolved()
    {
        // An L-bracket's inner concavity: 0.3·h wide.
        var bracket = new GridSolid(new[] { 0, 0.5, 0.53, 1 }, new[] { 0, 0.4, 1 }, new[] { 0, 0.5 },
            p => !GridSolid.In(p, 0.5, 0.53, 0.4, 2, -1, 1)).ToGeometry();

        AssertRefused(A0(bracket, 0.1), "clearance", 0.03, 0.1, p => p.X is > 0.49 and < 0.54 && p.Y > 0.39);
        Assert.Equal(MeshAuditOutcome.Passed, A0(bracket, 0.015).Outcome);
    }

    [Fact]
    public void ThrowIfUnresolved_RefusesBeforeAnyMeshExists()
    {
        var plate = PrimitiveFactory.CreatePlate(1, 1, 0.06);

        var ex = Assert.Throws<MeshAuditException>(() => MeshAudit.Begin(plate, 0.2).ThrowIfUnresolved());
        Assert.Equal("A0", Assert.Single(ex.Report.Checks).Id);
        Assert.Contains("below mesh resolution", ex.Message);

        MeshAudit.Begin(plate, 0.03).ThrowIfUnresolved();
    }

    [Fact]
    public void SkippingTheGuard_IsReportedAsSkipped_NotAsPassed()
    {
        var plate = PrimitiveFactory.CreatePlate(1, 1, 0.06);
        var check = MeshAudit.Begin(plate, 0.2, new MeshAuditOptions { SkipResolutionGuard = true }).Resolution;
        Assert.Equal(MeshAuditOutcome.Skipped, check.Outcome);
    }

    // ------------------------------------------------------------------ sharp edges are not small features

    private static TriangleMesh Prism(params (double X, double Y)[] polygon)
    {
        double cx = polygon.Average(p => p.X), cy = polygon.Average(p => p.Y);
        return new SurfaceBuilder().AddPrism(polygon, (cx, cy), 0, 1).ToGeometry();
    }

    [Fact]
    public void SharpEdgeOfAPrism_IsNotASmallFeature()
    {
        // The two faces of a 60° edge are a hair apart next to the edge whatever h is.
        // That is a wedge, not a wall: the guard measures faces that oppose each other.
        var prism = Prism((0, 0), (1, 0), (0.5, Math.Sqrt(3) / 2));
        var check = A0(prism, 0.2);
        Assert.Equal(MeshAuditOutcome.Passed, check.Outcome);
        Assert.Equal(1.0, check.Observed!.Value, 9);      // the caps, one prism length apart
    }

    [Fact]
    public void BladeSharperThanTheCreaseAngle_IsRefused()
    {
        // A 20° wedge: its faces are within the crease angle of facing each other, so it is
        // a wall that thins to nothing, and the mesher cannot carry it.
        var blade = Prism((0, 0), (1, 0), (1, Math.Tan(20 * Math.PI / 180)));
        var check = A0(blade, 0.1);
        Assert.Equal(MeshAuditOutcome.Failed, check.Outcome);
        Assert.Contains("thickness", check.Detail);
    }

    [Fact]
    public void WideNotch_IsNotASmallFeature_ButTheLipBesideItCanBe()
    {
        // The round-13 notch: a 60° reentrant wedge cut into a 2 x 2 bar. The notch itself
        // has no size; the lip between it and the bar's face is 0.42 thick at its thinnest.
        var notch = MeshAuditRayTests.Notch(chamfered: false).ToGeometry();
        Assert.Equal(MeshAuditOutcome.Passed, A0(notch, 0.1).Outcome);

        var coarse = A0(notch, 0.4);
        Assert.Equal(MeshAuditOutcome.Failed, coarse.Outcome);
        Assert.True(coarse.Location!.Value.X > 0.5, $"the thin lip is at the open end of the notch, not {coarse.Location}");
    }
}
