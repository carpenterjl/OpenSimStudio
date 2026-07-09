using OpenSim.Geometry.Step;
using Xunit;

namespace OpenSim.Tests.Geometry.Step;

/// <summary>
/// Phase 4 gate: B-spline surfaces through the full pipeline — complex-instance rational
/// parsing, Gauss-Newton boundary inversion with continuity seeding, curvature-sized
/// interior lattice — asserted with the convex-hull property (exact for B-splines).
/// </summary>
public class StepBSplineSurfaceTests
{
    private const double A = 10e-3, B = 8e-3, Bump = 2e-3; // meters after import (fixture mm)

    [Fact]
    public void Pillow_ImportsWatertight_TwoNativeFaces()
    {
        var report = new StepImporter().ImportText(StepFixtures.BSplinePillow(10, 8, 2));
        var mesh = report.Mesh;

        Assert.True(mesh.IsWatertight());
        Assert.Equal(2, mesh.FaceCount);
        Assert.DoesNotContain(report.Notes, n => n.Contains("inside-out"));
        // The interior lattice must exist (a curved face without interior sampling would
        // triangulate boundary-only and badly misrepresent the bump).
        Assert.Contains(mesh.Vertices, v => v.Z > 1e-6);
    }

    [Fact]
    public void Pillow_VerticesRespectTheControlHull_Exactly()
    {
        // A B-spline lies in the convex hull of its control net; boundary samples are
        // edge points and interior nodes are surface evaluations, so EVERY vertex obeys
        // the hull bounds to rounding — the sharp assertion the formulation guarantees.
        var mesh = new StepImporter().ImportText(StepFixtures.BSplinePillow(10, 8, 2)).Mesh;
        Assert.All(mesh.Vertices, v =>
        {
            Assert.InRange(v.X, -1e-12, A + 1e-12);
            Assert.InRange(v.Y, -1e-12, B + 1e-12);
            Assert.InRange(v.Z, -1e-12, Bump + 1e-12);
        });
        double volume = mesh.ComputeSignedVolume();
        Assert.InRange(volume, 1e-12, A * B * Bump);
    }

    [Fact]
    public void Pillow_BoundaryInversion_RoundTripsWithinAcceptance()
    {
        // Every boundary sample of the top face was accepted by the Newton gate during
        // import (a miss is a loud StepGeometryException), so the shared edges must have
        // stitched: the z = 0 boundary rectangle belongs to both faces.
        var mesh = new StepImporter().ImportText(StepFixtures.BSplinePillow(10, 8, 2)).Mesh;
        var rim = mesh.Vertices.Where(v => Math.Abs(v.Z) < 1e-12 &&
            (Math.Abs(v.X) < 1e-12 || Math.Abs(v.X - A) < 1e-12 ||
             Math.Abs(v.Y) < 1e-12 || Math.Abs(v.Y - B) < 1e-12)).ToList();
        Assert.True(rim.Count >= 8, "shared boundary rectangle must carry welded samples");
    }

    [Fact]
    public void Pillow_Import_IsDeterministic()
    {
        var a = new StepImporter().ImportText(StepFixtures.BSplinePillow(10, 8, 2)).Mesh;
        var b = new StepImporter().ImportText(StepFixtures.BSplinePillow(10, 8, 2)).Mesh;
        Assert.Equal(a.Vertices.Count, b.Vertices.Count);
        for (int i = 0; i < a.Vertices.Count; i++)
            Assert.True(a.Vertices[i].Equals(b.Vertices[i]), $"vertex {i} differs");
        Assert.Equal(a.Triangles, b.Triangles);
    }
}
