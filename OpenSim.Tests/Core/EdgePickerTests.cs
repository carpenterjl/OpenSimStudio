using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Geometry;
using Xunit;

namespace OpenSim.Tests.Core;

/// <summary>
/// Picking a feature edge or vertex with a click ray. Pure geometry, so it is gated here
/// rather than through the viewport: the test project cannot reference WPF, and the decision
/// this makes — which edge is under the cursor — has nothing to do with rendering anyway.
/// </summary>
public class EdgePickerTests
{
    private const double Lx = 0.20, Ly = 0.06, Lz = 0.02;

    private static readonly TriangleMesh Box = PrimitiveFactory.CreateBox(Lx, Ly, Lz);
    private static BoundaryEdgeSet Edges => Box.FeatureEdges;
    private static IReadOnlyList<Vector3D> Points => Box.Vertices;

    /// <summary>The id of the edge where two named faces meet.</summary>
    private static int EdgeBetween(int faceA, int faceB) =>
        Assert.Single(Edges.EdgesBetween(new[] { faceA, faceB }));

    // ---------------------------------------------------------------- edges

    /// <summary>A ray aimed straight down at the top-front edge picks it.</summary>
    [Fact]
    public void ARayThroughAnEdge_PicksThatEdge()
    {
        // Face 2 is y-min, face 5 is z-max: the edge along y = 0, z = Lz.
        int expected = EdgeBetween(2, 5);

        var origin = new Vector3D(Lx / 2, 0, 10 * Lz);
        var hit = EdgePicker.PickEdge(Edges, Points, origin, new Vector3D(0, 0, -1), 1e-4);

        Assert.NotNull(hit);
        Assert.Equal(expected, hit!.Value.EdgeId);
        Assert.Equal(0.0, hit.Value.Distance, 9);
        Assert.Equal(10 * Lz - Lz, hit.Value.RayT, 9);
    }

    [Fact]
    public void ARayPassingWideOfEveryEdge_PicksNothing()
    {
        // Down through the middle of the top face: the nearest edge is half the width away.
        var origin = new Vector3D(Lx / 2, Ly / 2, 10 * Lz);
        Assert.Null(EdgePicker.PickEdge(Edges, Points, origin, new Vector3D(0, 0, -1), 1e-3));
    }

    /// <summary>
    /// A click a little to one side still picks, and the reported distance is exactly how far
    /// the ray passed — the number the tolerance is compared against.
    /// </summary>
    [Fact]
    public void TheReportedDistance_IsHowFarTheRayPassedFromTheEdge()
    {
        const double offset = 3e-4;
        int expected = EdgeBetween(2, 5);

        var origin = new Vector3D(Lx / 2, offset, 10 * Lz);
        var hit = EdgePicker.PickEdge(Edges, Points, origin, new Vector3D(0, 0, -1), 1e-3);

        Assert.NotNull(hit);
        Assert.Equal(expected, hit!.Value.EdgeId);
        Assert.Equal(offset, hit.Value.Distance, 9);
    }

    /// <summary>
    /// Two edges in line with the cursor: the near one wins. A depth-blind pick would select
    /// whichever the enumeration reached first, which on a box is the far side about half the
    /// time.
    /// </summary>
    [Fact]
    public void WhenTwoEdgesLineUp_TheNearerOneIsPicked()
    {
        int top = EdgeBetween(2, 5);       // y = 0, z = Lz
        int bottom = EdgeBetween(2, 4);    // y = 0, z = 0
        Assert.NotEqual(top, bottom);

        var origin = new Vector3D(Lx / 2, 0, 10 * Lz);
        var down = EdgePicker.PickEdge(Edges, Points, origin, new Vector3D(0, 0, -1), 1e-4);
        Assert.Equal(top, down!.Value.EdgeId);

        // From below, the same two edges line up the other way round.
        var fromBelow = new Vector3D(Lx / 2, 0, -10 * Lz);
        var up = EdgePicker.PickEdge(Edges, Points, fromBelow, new Vector3D(0, 0, 1), 1e-4);
        Assert.Equal(bottom, up!.Value.EdgeId);
    }

    /// <summary>
    /// The occlusion gate: with the ray stopped at the surface it actually hit, the edge on
    /// the far side of the solid is not picked through it.
    /// </summary>
    [Fact]
    public void AnEdgeBeyondTheVisibleSurface_IsNotPickedThroughTheSolid()
    {
        int top = EdgeBetween(2, 5);
        int bottom = EdgeBetween(2, 4);

        var origin = new Vector3D(Lx / 2, 0, 10 * Lz);
        var direction = new Vector3D(0, 0, -1);

        // Aim so only the far (bottom) edge is within tolerance...
        var offsetOrigin = new Vector3D(Lx / 2, 0, 10 * Lz);
        double toTop = 10 * Lz - Lz;
        double toBottom = 10 * Lz;

        // ...unbounded, the nearer edge still wins.
        Assert.Equal(top, EdgePicker.PickEdge(Edges, Points, offsetOrigin, direction, 1e-4)!.Value.EdgeId);

        // Stopping the ray just past the top surface leaves only the top edge reachable.
        var gated = EdgePicker.PickEdge(Edges, Points, origin, direction, 1e-4, toTop + 1e-6);
        Assert.Equal(top, gated!.Value.EdgeId);

        // And stopping it BEFORE anything is reachable picks nothing, rather than the far edge.
        Assert.Null(EdgePicker.PickEdge(Edges, Points, origin, direction, 1e-4, toTop - 1e-3));
        Assert.True(toBottom > toTop);
        Assert.NotEqual(top, bottom);
    }

    [Fact]
    public void GeometryBehindTheCamera_IsNeverPicked()
    {
        // Aimed away from the box entirely.
        var origin = new Vector3D(Lx / 2, 0, 10 * Lz);
        Assert.Null(EdgePicker.PickEdge(Edges, Points, origin, new Vector3D(0, 0, 1), 1e-3));
    }

    /// <summary>
    /// A ray running exactly ALONG an edge is the degenerate case: the closest-point equations
    /// have no unique solution, and a naive division produces a NaN distance that no comparison
    /// against the tolerance can ever be true for — so the click would silently do nothing.
    /// <para>
    /// What is asserted is the honest contract, not a particular edge. Such a ray grazes the
    /// edge it runs along AND the four edges meeting it at the two corners, all at distance
    /// zero, so which one "should" win is not a question the geometry answers. It must return
    /// a hit at zero distance, and it must return the same one every time.
    /// </para>
    /// </summary>
    [Fact]
    public void ARayParallelToAnEdge_PicksSomethingOnItAtZeroDistance()
    {
        var origin = new Vector3D(-Lx, 0, Lz);
        var direction = new Vector3D(1, 0, 0);

        var hit = EdgePicker.PickEdge(Edges, Points, origin, direction, 1e-6);

        Assert.NotNull(hit);
        Assert.Equal(0.0, hit!.Value.Distance, 9);

        // Whatever it picked lies on the line the ray runs along.
        var edge = Edges.EdgeById(hit.Value.EdgeId)!;
        Assert.Contains(edge.NodeIds, n => Math.Abs(Points[n].Y) < 1e-12
                                        && Math.Abs(Points[n].Z - Lz) < 1e-12);

        for (int i = 0; i < 5; i++)
            Assert.Equal(hit, EdgePicker.PickEdge(Edges, Points, origin, direction, 1e-6));
    }

    /// <summary>
    /// An edge is picked near ITS OWN extent, not near the infinite line through it. A ray
    /// crossing that line well beyond the box must miss.
    /// </summary>
    [Fact]
    public void TheInfiniteLineThroughAnEdge_IsNotTheEdge()
    {
        var origin = new Vector3D(5 * Lx, 0, 10 * Lz);     // past the far end of the box
        Assert.Null(EdgePicker.PickEdge(Edges, Points, origin, new Vector3D(0, 0, -1), 1e-3));
    }

    [Fact]
    public void ADegenerateDirectionOrTolerance_PicksNothingRatherThanThrowing()
    {
        var origin = new Vector3D(Lx / 2, 0, 10 * Lz);
        Assert.Null(EdgePicker.PickEdge(Edges, Points, origin, new Vector3D(0, 0, 0), 1e-3));
        Assert.Null(EdgePicker.PickEdge(Edges, Points, origin, new Vector3D(0, 0, -1), 0));
        Assert.Null(EdgePicker.PickVertex(Edges, Points, origin, new Vector3D(0, 0, 0), 1e-3));
    }

    /// <summary>The same click resolves the same way every time, whatever the enumeration
    /// order happens to be — a tie is broken by the lowest edge id.</summary>
    [Fact]
    public void ThePickIsDeterministic()
    {
        var origin = new Vector3D(Lx / 2, 1e-9, 10 * Lz);
        var first = EdgePicker.PickEdge(Edges, Points, origin, new Vector3D(0, 0, -1), 1e-3);
        for (int i = 0; i < 5; i++)
            Assert.Equal(first, EdgePicker.PickEdge(Edges, Points, origin, new Vector3D(0, 0, -1), 1e-3));
    }

    // ---------------------------------------------------------------- vertices

    [Fact]
    public void ARayThroughACorner_PicksThatVertex()
    {
        var origin = new Vector3D(0, 0, 10 * Lz);
        var hit = EdgePicker.PickVertex(Edges, Points, origin, new Vector3D(0, 0, -1), 1e-4);

        Assert.NotNull(hit);
        var vertex = Edges.VertexById(hit!.Value.VertexId)!;
        var position = Points[vertex.NodeId];
        Assert.Equal(0.0, position.X, 12);
        Assert.Equal(0.0, position.Y, 12);
        Assert.Equal(Lz, position.Z, 12);       // the NEAR corner, not the one below it
    }

    /// <summary>
    /// At a corner both a vertex and three edges are within reach. The caller resolves
    /// vertices first with a wider tolerance, and this is what makes that work: at the corner
    /// the vertex is exactly as close as the edges, so without that precedence a corner could
    /// never be selected.
    /// </summary>
    [Fact]
    public void AtACorner_BothAVertexAndItsEdgesAreInReach()
    {
        var origin = new Vector3D(0, 0, 10 * Lz);
        var direction = new Vector3D(0, 0, -1);

        var vertex = EdgePicker.PickVertex(Edges, Points, origin, direction, 1.5e-4);
        var edge = EdgePicker.PickEdge(Edges, Points, origin, direction, 1e-4);

        Assert.NotNull(vertex);
        Assert.NotNull(edge);
        Assert.Equal(vertex!.Value.RayT, edge!.Value.RayT, 9);
    }

    [Fact]
    public void AVertexBeyondTheRayLimit_IsNotPicked()
    {
        var origin = new Vector3D(0, 0, 10 * Lz);
        var direction = new Vector3D(0, 0, -1);
        double toNear = 10 * Lz - Lz;

        Assert.NotNull(EdgePicker.PickVertex(Edges, Points, origin, direction, 1e-4, toNear + 1e-6));
        Assert.Null(EdgePicker.PickVertex(Edges, Points, origin, direction, 1e-4, toNear - 1e-3));
    }

    /// <summary>An edge set from a MESH works the same way — the picker consumes positions and
    /// an edge set, not a particular source of them.</summary>
    [Fact]
    public void AMeshEdgeSetPicksTheSameWay()
    {
        var mesh = new OpenSim.Meshing.StructuredLatticeMeshGenerator().Generate(
            Box,
            new MeshSettings
            {
                Method = MeshMethod.StructuredLattice,
                Divisions = new LatticeDivisions(4, 2, 1)
            });

        int expected = Assert.Single(mesh.Edges.EdgesBetween(new[] { 2, 5 }));
        var origin = new Vector3D(Lx / 2, 0, 10 * Lz);
        var hit = EdgePicker.PickEdge(mesh.Edges, mesh.Nodes, origin, new Vector3D(0, 0, -1), 1e-4);

        Assert.NotNull(hit);
        Assert.Equal(expected, hit!.Value.EdgeId);
    }
}
