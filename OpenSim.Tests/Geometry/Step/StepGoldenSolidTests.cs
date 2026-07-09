using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry.Step;
using Xunit;

namespace OpenSim.Tests.Geometry.Step;

/// <summary>
/// Golden gates in the project's benchmark style: sharp assertions against the
/// DISCRETIZATION model (inscribed n-gon volumes, vertices exactly on the surface),
/// never loose tolerances against the continuum value.
/// </summary>
public class StepGoldenSolidTests
{
    private static StepImportReport Import(string text, StepImportOptions? options = null) =>
        new StepImporter(options).ImportText(text);

    private static void AssertNoOrientationRepair(StepImportReport report) =>
        Assert.DoesNotContain(report.Notes, n => n.Contains("inside-out"));

    [Fact]
    public void Box_IsExact_VolumeAreaFacesWatertight()
    {
        var report = Import(StepFixtures.Box(1, 2, 3)); // mm
        var mesh = report.Mesh;

        Assert.True(mesh.IsWatertight());
        Assert.Equal(6, mesh.FaceCount);
        Assert.Equal(8, mesh.Vertices.Count);
        Assert.Equal(12, mesh.Triangles.Count);
        // Planar faces get no interior points, so the box is exact, not approximate.
        Assert.Equal(6e-9, mesh.ComputeSignedVolume(), 1e-21);
        double area = Enumerable.Range(0, mesh.Triangles.Count).Sum(mesh.TriangleArea);
        Assert.Equal(2 * (1e-3 * 2e-3 + 2e-3 * 3e-3 + 1e-3 * 3e-3), area, 1e-15);
        AssertNoOrientationRepair(report);
    }

    [Fact]
    public void Box_WithFlippedTopFace_StillWatertightAndOutward()
    {
        // The fixture states the same outward orientation two ways at once (reversed
        // plane normal AND same_sense=.F.); a winding bug would break IsWatertight
        // (mixed soup) — the global volume flip cannot repair that.
        var report = Import(StepFixtures.Box(1, 1, 1, flipTopFace: true));
        Assert.True(report.Mesh.IsWatertight());
        Assert.Equal(1e-9, report.Mesh.ComputeSignedVolume(), 1e-21);
        AssertNoOrientationRepair(report);
    }

    [Fact]
    public void Cylinder_BoundaryOnly_MatchesInscribedPrismIdentityExactly()
    {
        const double r = 5e-3, h = 20e-3; // meters after import (fixture is mm)
        var report = Import(StepFixtures.Cylinder(5, 20),
            StepImportOptions.Default with { InteriorRefinement = false });
        var mesh = report.Mesh;

        Assert.True(mesh.IsWatertight());
        Assert.Equal(3, mesh.FaceCount);

        // Every vertex lies on a ring: caps are inscribed n-gons, lateral wall quads are
        // planar, and ring samples are uniform in angle from the same seam vertex — so
        // the enclosed volume is EXACTLY the inscribed prism, to rounding.
        int n = mesh.Vertices.Count(v => Math.Abs(v.Z) < 1e-12);
        Assert.Equal(n, mesh.Vertices.Count(v => Math.Abs(v.Z - h) < 1e-12));
        Assert.Equal(mesh.Vertices.Count, 2 * n);
        double identity = 0.5 * n * r * r * h * Math.Sin(2 * Math.PI / n);
        Assert.Equal(1.0, mesh.ComputeSignedVolume() / identity, 12);
        AssertNoOrientationRepair(report);
    }

    [Fact]
    public void Cylinder_WithRefinement_LateralVerticesExactlyOnRadius()
    {
        const double r = 5e-3, h = 20e-3;
        var report = Import(StepFixtures.Cylinder(5, 20));
        var mesh = report.Mesh;

        Assert.True(mesh.IsWatertight());
        Assert.Equal(3, mesh.FaceCount);
        // Interior lattice nodes are surface evaluations — every strictly-interior-z
        // vertex must sit on the cylinder to rounding (the house-style sharp assertion).
        var lateral = mesh.Vertices.Where(v => v.Z > 1e-12 && v.Z < h - 1e-12).ToList();
        Assert.NotEmpty(lateral);
        Assert.All(lateral, v =>
            Assert.Equal(r, Math.Sqrt(v.X * v.X + v.Y * v.Y), 12));
        double exact = Math.PI * r * r * h;
        Assert.InRange(mesh.ComputeSignedVolume(), 0.98 * exact, exact);
        AssertNoOrientationRepair(report);
    }

    [Fact]
    public void Sphere_SeamAndBothPoles_WatertightAllVerticesOnRadius()
    {
        const double r = 5e-3;
        var report = Import(StepFixtures.Sphere(5));
        var mesh = report.Mesh;

        Assert.True(mesh.IsWatertight());
        Assert.Equal(1, mesh.FaceCount);
        Assert.All(mesh.Vertices, v =>
            Assert.Equal(r, Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z), 12));
        double exact = 4.0 / 3.0 * Math.PI * r * r * r;
        Assert.InRange(mesh.ComputeSignedVolume(), 0.9 * exact, exact);
        AssertNoOrientationRepair(report);
    }

    [Fact]
    public void ConeFrustum_VerticesOnTheCone_AndVolumeInBand()
    {
        const double r1 = 6e-3, r2 = 3e-3, h = 10e-3;
        var report = Import(StepFixtures.ConeFrustum(6, 3, 10));
        var mesh = report.Mesh;

        Assert.True(mesh.IsWatertight());
        Assert.Equal(3, mesh.FaceCount);
        Assert.All(mesh.Vertices, v =>
        {
            double expected = r1 + (r2 - r1) * v.Z / h;
            Assert.Equal(expected, Math.Sqrt(v.X * v.X + v.Y * v.Y), 12);
        });
        double exact = Math.PI * h / 3 * (r1 * r1 + r1 * r2 + r2 * r2);
        Assert.InRange(mesh.ComputeSignedVolume(), 0.9 * exact, exact);
        AssertNoOrientationRepair(report);
    }

    [Fact]
    public void FullCone_ApexPole_WeldsToOneVertexAndCloses()
    {
        const double radius = 5e-3, h = 8e-3;
        var report = Import(StepFixtures.FullCone(5, 8));
        var mesh = report.Mesh;

        Assert.True(mesh.IsWatertight());
        Assert.Equal(2, mesh.FaceCount);
        // The apex (a parameterization pole) must weld to exactly one vertex.
        Assert.Equal(1, mesh.Vertices.Count(v => Math.Abs(v.Z - h) < 1e-9));
        Assert.All(mesh.Vertices, v =>
            Assert.Equal(radius * (1 - v.Z / h), Math.Sqrt(v.X * v.X + v.Y * v.Y), 12));
        double exact = Math.PI * radius * radius * h / 3;
        Assert.InRange(mesh.ComputeSignedVolume(), 0.85 * exact, exact);
        AssertNoOrientationRepair(report);
    }

    [Fact]
    public void Torus_DoublyPeriodic_WatertightVerticesOnSurface()
    {
        const double major = 8e-3, minor = 2.5e-3;
        var report = Import(StepFixtures.Torus(8, 2.5));
        var mesh = report.Mesh;

        Assert.True(mesh.IsWatertight());
        Assert.Equal(1, mesh.FaceCount);
        Assert.All(mesh.Vertices, v =>
        {
            double rho = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            double residual = Math.Sqrt((rho - major) * (rho - major) + v.Z * v.Z);
            Assert.Equal(minor, residual, 12);
        });
        double exact = 2 * Math.PI * Math.PI * major * minor * minor;
        Assert.InRange(mesh.ComputeSignedVolume(), 0.85 * exact, exact);
        AssertNoOrientationRepair(report);
    }

    [Fact]
    public void Barrel_SurfaceOfRevolution_VerticesOnTheProfile()
    {
        // Arc profile: centre x = −8 mm, radius 13 mm (from r0 = 4, h = 10, bulge = 1).
        const double r0 = 4e-3, h = 10e-3, bulge = 1e-3;
        const double cx = -8e-3, arcR = 13e-3;
        var report = Import(StepFixtures.Barrel(4, 10, 1));
        var mesh = report.Mesh;

        Assert.True(mesh.IsWatertight());
        Assert.Equal(3, mesh.FaceCount);
        Assert.All(mesh.Vertices, v =>
        {
            double expected = cx + Math.Sqrt(arcR * arcR - (v.Z - h / 2) * (v.Z - h / 2));
            Assert.Equal(expected, Math.Sqrt(v.X * v.X + v.Y * v.Y), 12);
        });
        Assert.InRange(mesh.ComputeSignedVolume(),
            Math.PI * r0 * r0 * h, Math.PI * (r0 + bulge) * (r0 + bulge) * h);
        AssertNoOrientationRepair(report);
    }

    [Fact]
    public void UnitScaling_InchBoxIs25_4TimesMmBox()
    {
        double mm = Import(StepFixtures.Box(1, 1, 1, StepFixtures.Unit.Millimetre)).Mesh.ComputeSignedVolume();
        double inch = Import(StepFixtures.Box(1, 1, 1, StepFixtures.Unit.Inch)).Mesh.ComputeSignedVolume();
        Assert.Equal(Math.Pow(25.4, 3), inch / mm, 9);
    }

    [Fact]
    public void MultiSolid_LargestWins_WithNote()
    {
        var report = Import(StepFixtures.TwoBoxes()); // 1 mm³ and 8 mm³ boxes
        Assert.Equal(8e-9, report.Mesh.ComputeSignedVolume(), 1e-20);
        Assert.Contains(report.Notes, n => n.Contains("2 solids") && n.Contains("Phase 4"));
    }

    [Fact]
    public void Import_IsDeterministic_BitwiseIdenticalMeshes()
    {
        var a = Import(StepFixtures.Cylinder(5, 20)).Mesh;
        var b = Import(StepFixtures.Cylinder(5, 20)).Mesh;
        Assert.Equal(a.Vertices.Count, b.Vertices.Count);
        for (int i = 0; i < a.Vertices.Count; i++)
            Assert.True(a.Vertices[i].Equals(b.Vertices[i]), $"vertex {i} differs");
        Assert.Equal(a.Triangles.Count, b.Triangles.Count);
        for (int i = 0; i < a.Triangles.Count; i++)
            Assert.Equal(a.Triangles[i], b.Triangles[i]);
        Assert.Equal(a.TriangleFaceIds, b.TriangleFaceIds);
    }

    [Fact]
    public void ExampleModel_ImportsWatertight_AllNativeFaces()
    {
        string? path = Part21Tests.FindExampleStepFile();
        if (path is null) return; // example not present in this checkout

        var report = new StepImporter().ImportWithNotes(path);
        var mesh = report.Mesh;

        Assert.True(mesh.IsWatertight(), "real CAD export must import watertight");
        Assert.Equal(65, mesh.FaceCount); // native STEP faces, no crease detection
        Assert.True(mesh.ComputeSignedVolume() > 0);
        AssertNoOrientationRepair(report);
    }
}
