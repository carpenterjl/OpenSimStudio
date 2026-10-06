using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Rf.Surface;

/// <summary>A printed antenna and its coplanar ground ready for a free-space solve.</summary>
public sealed record PrintedAntennaModel(SurfaceStructure Structure, SurfacePort Port,
    Point2 AntennaFeedPoint, Point2 GroundFeedPoint, IReadOnlyList<string> Notes);

/// <summary>
/// A printed antenna with a FINITE ground: the antenna copper and the ground pour on the same
/// layer (a printed inverted-F, monopole or meander beside its ground), joined at the feed by a
/// short bridge across the gap, the port a delta gap across that bridge — the coaxial or
/// microstrip feed's terminals, centre to the antenna and shield to the pour. The ground is meshed
/// metal of its real size, so its currents and its share of the radiation are in the solve.
///
/// <para>Solved in AIR (no board dielectric): the dielectric's effect on a printed antenna is a
/// downward shift of the resonance, which this model does not show. The ground can be clipped to a
/// rectangle to bound the unknown count; what lies outside the clip is not in the model.</para>
/// </summary>
public static class PrintedAntennaBuilder
{
    public static IReadOnlyList<string> Assumptions { get; } = new[]
    {
        "Free space: the board dielectric is not in the model (a printed antenna on FR-4 resonates lower than this).",
        "Perfect zero-thickness copper; antenna and ground pour coplanar on one layer.",
        "The feed is a delta gap across a bridge between the antenna copper and the pour at the feed point.",
        "Only the chosen antenna and ground copper are present; other nets, components and cables are not."
    };

    /// <summary>Builds the model from two polygons (meters). The bridge runs from the antenna
    /// copper's point nearest <paramref name="feedNear"/> straight to the nearest ground copper,
    /// <paramref name="bridgeWidth"/> wide.</summary>
    public static PrintedAntennaModel Build(Polygon2 antenna, IReadOnlyList<Polygon2> ground, Point2 feedNear,
        double bridgeWidth, double maxEdgeLength, Polygon2? groundClip = null, int maxUnknowns = 2500)
    {
        if (!(bridgeWidth > 0)) throw new ArgumentOutOfRangeException(nameof(bridgeWidth));
        if (ground.Count == 0) throw new ArgumentException("No ground copper.", nameof(ground));
        var ops = new ClipperPolygonOps();
        var notes = new List<string>();

        IReadOnlyList<Polygon2> groundCopper = ground;
        if (groundClip is not null)
        {
            groundCopper = ops.Intersect(ground, new[] { groundClip.Outer });
            if (groundCopper.Count == 0)
                throw new ArgumentException("The ground clip leaves no ground copper.", nameof(groundClip));
            notes.Add($"Ground clipped to the given rectangle: {groundCopper.Sum(p => p.Area()) * 1e6:g4} mm² kept.");
        }

        var a = NearestOnBoundary(antenna, feedNear);
        var g = groundCopper.Select(p => NearestOnBoundary(p, a)).OrderBy(p => (p - a).Length).First();
        double gap = (g - a).Length;
        if (gap <= 0)
            throw new ArgumentException("The antenna copper touches the ground at the feed; leave a gap for the port.", nameof(feedNear));
        var axis = (g - a) * (1 / gap);
        var normal = new Point2(-axis.Y, axis.X);
        // Run the bridge half a width into each piece of copper so the union is solid.
        var start = a - axis * (bridgeWidth / 2);
        var end = g + axis * (bridgeWidth / 2);
        var half = normal * (bridgeWidth / 2);
        var bridge = new List<Point2> { start - half, end - half, end + half, start + half };

        var rings = new List<IReadOnlyList<Point2>>();
        foreach (var poly in new[] { antenna }.Concat(groundCopper))
            rings.AddRange(Polygon2.OrientedRings(poly));
        rings.Add(Polygon2.RingArea(bridge) >= 0 ? bridge : bridge.AsEnumerable().Reverse().ToList());
        var union = ops.Union(rings);
        if (union.Count != 1)
            throw new InvalidOperationException(
                $"The antenna, bridge and ground did not join into one piece of copper ({union.Count} pieces).");

        var mid = (a + g) * 0.5;
        var mesh = SurfaceMeshBuilder.BuildFromPolygon(union[0], maxEdgeLength, z: 0, feedHint: mid,
            maxUnknowns: maxUnknowns);
        if (mesh.Structure is null || mesh.Port is null)
            throw new InvalidOperationException($"Meshing failed: {mesh.FailureReason}");
        // The port must cross the bridge, not some other narrow neck.
        foreach (int e in mesh.Port.EdgeBases)
        {
            var edge = mesh.Structure.Edges[e];
            var m = (mesh.Structure.Vertices[edge.V1] + mesh.Structure.Vertices[edge.V2]) / 2;
            var d = new Point2(m.X, m.Y) - mid;
            if (Math.Abs(d.X * axis.X + d.Y * axis.Y) > gap / 2 + maxEdgeLength
                || Math.Abs(d.X * normal.X + d.Y * normal.Y) > bridgeWidth / 2 + 1e-9)
                throw new InvalidOperationException("The port cut did not land across the feed bridge; use a finer mesh.");
        }
        notes.Add($"Feed bridge {gap * 1e3:g3} mm long, {bridgeWidth * 1e3:g3} mm wide; {mesh.Structure.BasisCount} unknowns.");
        notes.AddRange(mesh.Warnings);
        return new PrintedAntennaModel(mesh.Structure, mesh.Port, a, g, notes);
    }

    /// <summary>From a board: the antenna net's copper on <paramref name="layerOrder"/> nearest the
    /// feed, and the ground net's copper on the same layer.</summary>
    public static PrintedAntennaModel FromBoard(PcbBoard board, int layerOrder, string antennaNet, string groundNet,
        Point2 feedNear, double bridgeWidth, double maxEdgeLength, Polygon2? groundClip = null, int maxUnknowns = 2500)
    {
        CopperNet Net(string name) => board.Nets.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException($"No net named '{name}' on the board.");
        var antennaIslands = Net(antennaNet).Islands.Where(i => i.LayerOrder == layerOrder).ToList();
        var groundIslands = Net(groundNet).Islands.Where(i => i.LayerOrder == layerOrder).Select(i => i.Shape).ToList();
        if (antennaIslands.Count == 0) throw new ArgumentException($"Net '{antennaNet}' has no copper on layer {layerOrder}.");
        if (groundIslands.Count == 0) throw new ArgumentException($"Net '{groundNet}' has no copper on layer {layerOrder}.");
        var antenna = antennaIslands.OrderBy(i => (NearestOnBoundary(i.Shape, feedNear) - feedNear).Length).First().Shape;
        return Build(antenna, groundIslands, feedNear, bridgeWidth, maxEdgeLength, groundClip, maxUnknowns);
    }

    /// <summary>The point of <paramref name="polygon"/>'s outer boundary nearest <paramref name="p"/>.</summary>
    public static Point2 NearestOnBoundary(Polygon2 polygon, Point2 p)
    {
        Point2 best = polygon.Outer[0];
        double bestDistance = double.MaxValue;
        foreach (var ring in Polygon2.OrientedRings(polygon))
            for (int i = 0; i < ring.Count; i++)
            {
                var s = ring[i];
                var e = ring[(i + 1) % ring.Count];
                var d = e - s;
                double len2 = d.X * d.X + d.Y * d.Y;
                double t = len2 == 0 ? 0 : Math.Clamp(((p.X - s.X) * d.X + (p.Y - s.Y) * d.Y) / len2, 0, 1);
                var q = s + d * t;
                double dist = (q - p).Length;
                if (dist < bestDistance) { bestDistance = dist; best = q; }
            }
        return best;
    }
}
