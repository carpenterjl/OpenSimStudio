using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage D1b (geometry) — resolving where a wire attaches to a sheet, and refusing the cases the
/// attachment model does not describe. Gated separately from the matrix assembly, because every
/// one of these is a decision about admissibility rather than about numerics: a guessed
/// attachment point is a WRONG answer, not an approximate one.
/// </summary>
public class WireSurfaceJunctionTests
{
    private const double Radius = 5e-4;

    /// <summary>A plate, plus a vertex that is genuinely INTERIOR — every fan wedge at it has a
    /// neighbour to continue into. Found by construction rather than by picking a coordinate: on
    /// a coarse mesh the nearest vertex to an arbitrary point is often on the rim, and a fixture
    /// that quietly attaches to the rim would be testing the wrong refusal.</summary>
    private static SurfaceStructure Plate(out (double X, double Y) snap, double edge = 0.01)
    {
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(0.08, 0.06, edge, z: 0,
            portFraction: 0);
        Assert.NotNull(grid.Structure);
        var surface = grid.Structure!;
        for (int v = 0; v < surface.Vertices.Count; v++)
        {
            try
            {
                _ = new AttachmentFan(surface, v, Radius);
                var p = surface.Vertices[v];
                snap = (p.X, p.Y);
                return surface;
            }
            catch (InvalidOperationException) { /* rim vertex: keep looking */ }
        }
        throw new InvalidOperationException("no interior vertex on the test plate");
    }

    /// <summary>A wire from `from` down to the plate at `snap`, with the attached end carrying a
    /// half hat (grounded flag, null ground plane).</summary>
    private static WireStructure WireTo((double X, double Y) snap, Vector3D from,
        int segments = 4, bool endGrounded = true)
    {
        var target = new Vector3D(snap.X, snap.Y, 0);
        var nodes = new List<Vector3D>();
        for (int i = 0; i <= segments; i++)
            nodes.Add(from + (target - from) * ((double)i / segments));
        return new WireStructure(nodes, Enumerable.Repeat(Radius, segments).ToList(),
            isLoop: false, ground: null, startGrounded: false, endGrounded: endGrounded);
    }

    [Fact]
    public void APerpendicularWire_AttachesAtTheSnappedVertex()
    {
        var surface = Plate(out var snap);
        var wire = WireTo(snap, new Vector3D(snap.X, snap.Y, 0.02));
        var junction = WireSurfaceJunction.Attach(wire, surface, Radius);

        Assert.Equal(90.0, junction.IncidenceDegrees, 6);
        Assert.True(junction.WireEndsAtElementEnd);
        Assert.Equal(wire.BasisCount - 1, junction.WireBasis);
        Assert.Equal(wire.Nodes.Count - 1, wire.BasisNode(junction.WireBasis));
        Assert.Equal(snap.X, surface.Vertices[junction.Vertex].X, 12);
        Assert.Equal(snap.Y, surface.Vertices[junction.Vertex].Y, 12);
        // The fan's angles must close on 2π — the identity that says the junction current is
        // fully accounted for, with no share of it lost.
        Assert.Equal(2 * Math.PI, junction.Fan.TotalAngle, 9);
    }

    [Theory]
    [InlineData(60.0)]
    [InlineData(30.0)]
    [InlineData(15.0)]
    public void AnObliqueWire_AttachesAndReportsItsIncidence(double degrees)
    {
        // The fan stays in the sheet plane at any angle: the disc exists to absorb the wire's
        // endpoint CHARGE, which is a condition on the divergence, not on the arrival direction.
        var surface = Plate(out var snap);
        double rad = degrees * Math.PI / 180;
        var from = new Vector3D(
            snap.X + 0.02 * Math.Cos(rad), snap.Y, 0.02 * Math.Sin(rad));
        var wire = WireTo(snap, from);
        var junction = WireSurfaceJunction.Attach(wire, surface, Radius);

        Assert.Equal(degrees, junction.IncidenceDegrees, 6);
        Assert.Equal(2 * Math.PI, junction.Fan.TotalAngle, 9);
    }

    [Fact]
    public void GrazingIncidence_IsATypedFailure()
    {
        // Below the limit the wire's reduced-kernel tube overlaps the metal it is attaching to,
        // so the thin-wire model stops describing the geometry. Named, not approximated.
        var surface = Plate(out var snap);
        double rad = 5 * Math.PI / 180;
        var from = new Vector3D(snap.X + 0.02 * Math.Cos(rad), snap.Y, 0.02 * Math.Sin(rad));
        var e = Assert.Throws<ArgumentException>(
            () => WireSurfaceJunction.Attach(WireTo(snap, from), surface, Radius));
        Assert.Contains("grazing incidence", e.Message);
        Assert.Contains("5", e.Message);
    }

    [Fact]
    public void AWireThatDoesNotReachTheSheet_IsATypedFailure()
    {
        var surface = Plate(out var snap);
        var target = new Vector3D(snap.X, snap.Y, 0.001);        // stops 1 mm short
        var nodes = new List<Vector3D>();
        for (int i = 0; i <= 4; i++)
            nodes.Add(new Vector3D(snap.X, snap.Y, 0.02 + (0.001 - 0.02) * i / 4.0));
        var wire = new WireStructure(nodes, Enumerable.Repeat(Radius, 4).ToList(),
            isLoop: false, ground: null, endGrounded: true);
        var e = Assert.Throws<ArgumentException>(
            () => WireSurfaceJunction.Attach(wire, surface, Radius));
        Assert.Contains("must terminate ON the metal", e.Message);
    }

    [Fact]
    public void AWireLandingOffAnyVertex_IsATypedFailure()
    {
        // The fan is anchored at a vertex; landing between vertices has no anchor, and picking
        // the nearest one would silently move the attachment.
        var surface = Plate(out var snap);
        var offVertex = (X: snap.X + 0.0037, Y: snap.Y + 0.0021);
        var wire = WireTo(offVertex, new Vector3D(offVertex.X, offVertex.Y, 0.02));
        var e = Assert.Throws<ArgumentException>(
            () => WireSurfaceJunction.Attach(wire, surface, Radius));
        Assert.Contains("not a mesh vertex", e.Message);
    }

    [Fact]
    public void AFreeEndedWire_IsATypedFailure()
    {
        // Without the half hat no basis carries current at the contact, so the junction would
        // transport nothing — a silently dead attachment, which is worse than a refusal.
        var surface = Plate(out var snap);
        var wire = WireTo(snap, new Vector3D(snap.X, snap.Y, 0.02), endGrounded: false);
        var e = Assert.Throws<ArgumentException>(
            () => WireSurfaceJunction.Attach(wire, surface, Radius));
        Assert.Contains("transport nothing", e.Message);
    }

    [Fact]
    public void ARimAttachment_IsATypedFailure()
    {
        // At a boundary vertex one fan wedge has no neighbour to continue into, so a share of the
        // junction current would have nowhere to go.
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(0.08, 0.06, 0.01, z: 0,
            portFraction: 0);
        Assert.NotNull(grid.Structure);
        var surface = grid.Structure!;
        // A corner vertex of the plate is necessarily on the rim.
        int corner = 0;
        double best = double.MaxValue;
        for (int v = 0; v < surface.Vertices.Count; v++)
        {
            double d = (surface.Vertices[v] - new Vector3D(-0.04, -0.03, 0)).Length;
            if (d < best) { best = d; corner = v; }
        }
        var c = surface.Vertices[corner];
        var wire = WireTo((c.X, c.Y), new Vector3D(c.X, c.Y, 0.02));
        var e = Assert.Throws<ArgumentException>(
            () => WireSurfaceJunction.Attach(wire, surface, Radius));
        Assert.Contains("not usable", e.Message);
    }

    [Fact]
    public void ALoop_IsATypedFailure()
    {
        var surface = Plate(out var snap);
        var nodes = new List<Vector3D>
        {
            new(0, 0, 0.01), new(0.01, 0, 0.01), new(0.01, 0.01, 0.01), new(0, 0.01, 0.01),
        };
        var loop = new WireStructure(nodes, Enumerable.Repeat(Radius, 4).ToList(),
            isLoop: true, ground: null);
        var e = Assert.Throws<ArgumentException>(
            () => WireSurfaceJunction.Attach(loop, surface, Radius));
        Assert.Contains("no free end", e.Message);
    }
}
