using OpenSim.Core.Geometry2D;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// FU-47: pins wherever the antenna panel puts them — several snapped vertices on the wizard plate,
/// exact vertices in a board island's polygon mesh. The reference is the two-post loop inductance
/// the grid-aligned pins already meet (BoardStructuresTests): a driven pin and a shorting pin a
/// distance s apart between a plate and the ground see (µ0·h/π)·ln(s/a) at low frequency, whatever
/// the dielectric. The bands are that test's own (4 % of the closed form, 0.5 % between air and
/// εr 4.4). First measurements, s = 8.881 mm (closed form 2.4278 nH): off-grid pins snapped on the
/// 3 mm plate 2.3812 nH in air (−1.92 %), 2.3860 nH at εr 4.4 (+0.20 %); the same pins in a 60 mm
/// square island's polygon mesh (1631 unknowns) 2.3780 nH (−2.05 %), 2.3819 nH (+0.16 %).
/// </summary>
public class PinMeshTests
{
    private readonly ITestOutputHelper _out;
    public PinMeshTests(ITestOutputHelper output) => _out = output;

    private const double Mu0 = 4e-7 * Math.PI;
    private const double H = 1.6e-3, A = 0.2e-3, F = 30e6;

    private static double TwoPost(double s) => Mu0 * H / Math.PI * Math.Log(s / A);

    private static double LoopInductance(SurfaceStructure surface, (double X, double Y) feed, (double X, double Y) shorting,
        double epsR = 1.0)
    {
        var table = new LayeredKernelTable(new SubstrateStackup(epsR, 0, H), F, 0.1);
        var solution = new SurfaceMomSolver().SolvePins(surface, table, new[]
        {
            new VerticalPin("feed", new ProbeFeed(feed.X, feed.Y, A, 3)),
            new VerticalPin("short", new ProbeFeed(shorting.X, shorting.Y, A, 3), IsPort: false)
        });
        return solution.Impedance()[0, 0].Imaginary / (2 * Math.PI * F);
    }

    private static Polygon2 Square(double half) => new(new[]
    {
        new Point2(-half, -half), new Point2(half, -half), new Point2(half, half), new Point2(-half, half)
    });

    [Fact]
    public void SnappingOntoGridNodes_LeavesThePlateAndItsSolveAsBuilt()
    {
        // The grid-aligned pins of the two-post test: each point is a node already, so the snap
        // moves nothing but the last bits of the node's coordinates (a few 1e-18 m). The solve
        // feels that only through round-off, which the EFIE's conditioning at 30 MHz on a 60 mm
        // plate amplifies: measured 4.6e-9 relative, gated at 1e-7.
        var bare = SurfaceMeshBuilder.BuildRectangularPlate(0.06, 0.06, 3e-3, z: H).Structure!;
        var snapped = SurfaceMeshBuilder.BuildRectangularPlate(0.06, 0.06, 3e-3, z: H,
            snapVertices: new[] { (-3e-3, 0.0), (6e-3, 0.0) }).Structure!;
        double moved = bare.Vertices.Zip(snapped.Vertices, (p, q) => (p - q).Length).Max();
        Assert.True(moved <= 1e-15, $"a vertex moved {moved:e3} m");
        Assert.Equal(bare.Triangles, snapped.Triangles);
        double l0 = LoopInductance(bare, (-3e-3, 0), (6e-3, 0));
        double l1 = LoopInductance(snapped, (-3e-3, 0), (6e-3, 0));
        _out.WriteLine($"unsnapped {l0 * 1e9:F9} nH, snapped {l1 * 1e9:F9} nH, rel {Math.Abs(l1 - l0) / l0:e3}");
        Assert.True(Math.Abs(l1 - l0) <= 1e-7 * l0, $"{l1} vs {l0}, rel {Math.Abs(l1 - l0) / l0:e3}");
    }

    [Fact]
    public void OffGridPins_SnapExactly_AndFollowTheTwoPostInductance()
    {
        // Pins between grid nodes: each moves its nearest interior vertex onto itself, so the tubes
        // stand where they were asked for, and the loop inductance follows ln(s/a) for the pins' own
        // spacing — in air and on FR-4 alike.
        (double X, double Y) feed = (-2.2e-3, 0.7e-3), shorting = (6.6e-3, -0.5e-3);
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(0.06, 0.06, 3e-3, z: H, snapVertices: new[] { feed, shorting });
        Assert.NotNull(grid.Structure);
        var surface = grid.Structure!;
        Assert.Contains(surface.Vertices, v => v.X == feed.X && v.Y == feed.Y);
        Assert.Contains(surface.Vertices, v => v.X == shorting.X && v.Y == shorting.Y);
        double s = Math.Sqrt((shorting.X - feed.X) * (shorting.X - feed.X) + (shorting.Y - feed.Y) * (shorting.Y - feed.Y));
        double expected = TwoPost(s);
        double air = LoopInductance(surface, feed, shorting), fr4 = LoopInductance(surface, feed, shorting, 4.4);
        _out.WriteLine($"s {s * 1e3:F3} mm: air {air * 1e9:F4} nH ({air / expected - 1:+0.00%;-0.00%}), εr 4.4 {fr4 * 1e9:F4} nH, "
            + $"two-post {expected * 1e9:F4} nH; {string.Join(" ", grid.Warnings)}");
        Assert.True(Math.Abs(fr4 - air) / air <= 0.005, $"εr changes L: {air * 1e9:F4} vs {fr4 * 1e9:F4} nH");
        Assert.True(Math.Abs(air - expected) / expected <= 0.04, $"air {air * 1e9:F4} nH vs {expected * 1e9:F4} nH");
    }

    [Fact]
    public void SnapOrder_DoesNotMatter_AndPinsOnOneVertexAreRefused()
    {
        (double, double) p = (-2.2e-3, 0.7e-3), q = (6.6e-3, -0.5e-3);
        var a = SurfaceMeshBuilder.BuildRectangularPlate(0.06, 0.06, 3e-3, z: H, snapVertices: new[] { p, q }).Structure!;
        var b = SurfaceMeshBuilder.BuildRectangularPlate(0.06, 0.06, 3e-3, z: H, snapVertices: new[] { q, p }).Structure!;
        Assert.Equal(a.Vertices, b.Vertices);

        var clash = SurfaceMeshBuilder.BuildRectangularPlate(0.06, 0.06, 3e-3, z: H,
            snapVertices: new[] { (0.1e-3, 0.1e-3), (-0.2e-3, 0.3e-3) });
        Assert.Null(clash.Structure);
        Assert.Contains("same mesh vertex", clash.FailureReason);

        // The single probe keeps its own parameter and result.
        var single = SurfaceMeshBuilder.BuildRectangularPlate(0.06, 0.06, 3e-3, z: H, snapVertex: p).Structure!;
        var listed = SurfaceMeshBuilder.BuildRectangularPlate(0.06, 0.06, 3e-3, z: H, snapVertices: new[] { p }).Structure!;
        Assert.Equal(single.Vertices, listed.Vertices);
    }

    [Fact]
    public void IslandPins_AreExactVertices_AndFollowTheTwoPostInductance()
    {
        // The same two posts under a 60 mm square meshed as a board island: the polygon mesher puts a
        // vertex exactly on each pin (the triangulation's jitter is undone for them).
        var feed = new Point2(-2.2e-3, 0.7e-3);
        var shorting = new Point2(6.6e-3, -0.5e-3);
        var grid = SurfaceMeshBuilder.BuildFromPolygon(Square(0.03), 3e-3, H, maxUnknowns: 5000,
            fixedPoints: new[] { feed, shorting });
        Assert.True(grid.Structure is not null, grid.FailureReason);
        var surface = grid.Structure!;
        Assert.Contains(surface.Vertices, v => v.X == feed.X && v.Y == feed.Y);
        Assert.Contains(surface.Vertices, v => v.X == shorting.X && v.Y == shorting.Y);
        double expected = TwoPost((shorting - feed).Length);
        double air = LoopInductance(surface, (feed.X, feed.Y), (shorting.X, shorting.Y));
        double fr4 = LoopInductance(surface, (feed.X, feed.Y), (shorting.X, shorting.Y), 4.4);
        _out.WriteLine($"{surface.BasisCount} unknowns: air {air * 1e9:F4} nH ({air / expected - 1:+0.00%;-0.00%}), "
            + $"εr 4.4 {fr4 * 1e9:F4} nH, two-post {expected * 1e9:F4} nH");
        Assert.True(Math.Abs(fr4 - air) / air <= 0.005, $"εr changes L: {air * 1e9:F4} vs {fr4 * 1e9:F4} nH");
        Assert.True(Math.Abs(air - expected) / expected <= 0.04, $"air {air * 1e9:F4} nH vs {expected * 1e9:F4} nH");
    }

    [Fact]
    public void IslandPins_OffTheCopperOrOnItsEdge_AreRefused_AndNoPinsChangeNothing()
    {
        var outside = SurfaceMeshBuilder.BuildFromPolygon(Square(0.01), 2e-3, H, fixedPoints: new[] { new Point2(0.02, 0) });
        Assert.Null(outside.Structure);
        Assert.Contains("not on the copper", outside.FailureReason);
        var edge = SurfaceMeshBuilder.BuildFromPolygon(Square(0.01), 2e-3, H, fixedPoints: new[] { new Point2(0.0099, 0) });
        Assert.Null(edge.Structure);
        Assert.Contains("edge", edge.FailureReason);

        var plain = SurfaceMeshBuilder.BuildFromPolygon(Square(0.01), 2e-3, H).Structure!;
        var none = SurfaceMeshBuilder.BuildFromPolygon(Square(0.01), 2e-3, H, fixedPoints: Array.Empty<Point2>()).Structure!;
        Assert.Equal(plain.Vertices, none.Vertices);
        Assert.Equal(plain.Triangles, none.Triangles);
    }
}
