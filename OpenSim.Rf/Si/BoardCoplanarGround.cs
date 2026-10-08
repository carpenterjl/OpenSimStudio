using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;

namespace OpenSim.Rf.Si;

/// <summary>Ground copper found beside one side of a coupled stretch: the median edge-to-edge
/// gap from the outermost trace, the range it varies over, and the strip width it is solved
/// with.</summary>
internal sealed record CoplanarGroundSide(double Gap, double Width, double GapMin, double GapMax);

/// <summary>
/// Finds the ground copper on the trace layer itself beside a coupled stretch, so the
/// cross-section can carry it as coplanar ground strips.
/// <para>
/// Copper counts as ground when it belongs to the net of the reference plane under the traces
/// (stitched to it, so the board import made it one net) or to a net of the same name. Other
/// copper on the layer — another signal, an unconnected island — is not a return conductor and
/// is left out, as before.
/// </para>
/// <para>
/// From the outermost trace's outer edge, at stations along the stretch, a ray runs outward
/// across the layer. A side has a coplanar ground when ground copper is the first copper the
/// ray meets within <see cref="ReachHeights"/> substrate heights at
/// <see cref="BoardReferencePlanes.CoverageFraction"/> of the stations. It is then one strip at
/// the median gap, as wide as the narrowest copper met (capped where a wider strip no longer
/// matters). Ground further away changes the line by under 1 % and is not modelled.
/// </para>
/// </summary>
internal static class BoardCoplanarGround
{
    /// <summary>Ground further than this many substrate heights (trace to plane) from the
    /// outermost trace is not modelled: at ten heights a coplanar ground moves a microstrip's
    /// Z0 by under 1 % (ImpedanceCalculatorTests, "stop mattering when far away").</summary>
    public const double ReachHeights = 10;

    /// <summary>The nets the line returns through: those whose copper on a plane layer lies
    /// under the traces' sample points.</summary>
    public static (HashSet<CopperNet> Nets, HashSet<string> Names) ReferenceNets(PcbBoard board,
        IReadOnlyList<int> planeLayers, IReadOnlyList<Point2> samples)
    {
        var nets = new HashSet<CopperNet>(ReferenceEqualityComparer.Instance);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var net in board.Nets)
            foreach (var island in net.Islands)
            {
                if (!planeLayers.Contains(island.LayerOrder)) continue;
                var index = new PolygonSetIndex(new[] { island.Shape });
                if (!samples.Any(index.Contains)) continue;
                nets.Add(net);
                if (!string.IsNullOrWhiteSpace(net.Name)) names.Add(net.Name);
                break;
            }
        return (nets, names);
    }

    /// <summary>
    /// The ground on each side of a stretch whose traces run from <paramref name="start"/>
    /// along the unit <paramref name="axis"/> for <paramref name="length"/>, with lateral
    /// positions measured along the left normal of the axis from <paramref name="start"/>.
    /// <paramref name="lowEdge"/> and <paramref name="highEdge"/> are the outer edges of the
    /// outermost traces on that scale.
    /// </summary>
    public static (CoplanarGroundSide? Low, CoplanarGroundSide? High, List<string> Notes) Find(
        PcbBoard board, int layer, IReadOnlyCollection<CopperIsland> ownIslands,
        (HashSet<CopperNet> Nets, HashSet<string> Names) reference,
        Point2 start, Point2 axis, double length, double lowEdge, double highEdge,
        double height, bool stripline, double tallestGap)
    {
        var notes = new List<string>();
        var own = new HashSet<CopperIsland>(ownIslands, ReferenceEqualityComparer.Instance);
        var netOf = new Dictionary<CopperIsland, CopperNet>(ReferenceEqualityComparer.Instance);
        foreach (var net in board.Nets)
            foreach (var island in net.Islands) netOf.TryAdd(island, net);
        bool IsGround(CopperIsland island) => netOf.TryGetValue(island, out var net)
            && (reference.Nets.Contains(net) || (!string.IsNullOrWhiteSpace(net.Name) && reference.Names.Contains(net.Name)));

        var candidates = board.Islands.Where(i => i.LayerOrder == layer && !own.Contains(i)).ToList();
        if (candidates.Count == 0 || reference.Nets.Count == 0) return (null, null, notes);

        double reach = ReachHeights * height;
        var normal = new Point2(-axis.Y, axis.X);
        int stations = (int)Math.Clamp(Math.Ceiling(length / (2 * height)), 4, 32);

        CoplanarGroundSide? Side(double edge, double sign, string name)
        {
            var gaps = new List<double>();
            var widths = new List<double>();
            int blocked = 0;
            for (int k = 0; k < stations; k++)
            {
                var origin = start + axis * ((k + 0.5) / stations * length) + normal * edge;
                var direction = normal * sign;
                // The first copper the ray enters, and how far it runs in it.
                double nearest = double.MaxValue, exit = double.MaxValue;
                CopperIsland? hit = null;
                foreach (var island in candidates)
                {
                    var crossings = Crossings(island.Shape, origin, direction);
                    if (crossings.Count < 2 || crossings[0] >= nearest) continue;
                    nearest = crossings[0];
                    exit = crossings[1];
                    hit = island;
                }
                if (hit is null || nearest > reach) continue;
                if (!IsGround(hit)) { blocked++; continue; }
                gaps.Add(nearest);
                widths.Add(exit - nearest);
            }
            double found = (double)gaps.Count / stations;
            if (found < BoardReferencePlanes.CoverageFraction)
            {
                if (gaps.Count > 0 || blocked > 0)
                    notes.Add($"Coplanar copper on the {name} side: ground beside {found:P0} of the stretch"
                        + (blocked > 0 ? $", other copper first at {(double)blocked / stations:P0} of it" : "")
                        + $"; under {BoardReferencePlanes.CoverageFraction:P0}, so it is NOT modelled (a uniform "
                        + "section cannot carry ground that comes and goes).");
                return null;
            }
            gaps.Sort();
            double gap = gaps[gaps.Count / 2];
            double span = highEdge - lowEdge;
            // The calculator's width beyond which a wider ground strip no longer matters.
            double cap = Math.Max(6 * tallestGap, 4 * (span + 2 * gap));
            double width = Math.Min(widths.Min(), cap);
            return new CoplanarGroundSide(gap, width, gaps[0], gaps[^1]);
        }

        var low = Side(lowEdge, -1, "low");
        var high = Side(highEdge, +1, "high");
        foreach (var (side, name) in new[] { (low, "one"), (high, "the other") })
            if (side is not null)
                notes.Add($"Coplanar ground on {name} side: {side.Width * 1e3:g3} mm wide at "
                    + $"{side.Gap * 1e6:g4} µm from the outermost trace"
                    + (side.GapMax - side.GapMin > 0.05 * side.Gap
                        ? $" (median; it varies from {side.GapMin * 1e6:g4} to {side.GapMax * 1e6:g4} µm along the stretch)"
                        : "")
                    + ", solved as a conductor and tied to the reference"
                    + (stripline ? " planes" : " plane") + " along its length (it is stitched to it).");
        return (low, high, notes);
    }

    /// <summary>Distances along the ray at which it crosses the polygon's boundary, ascending.
    /// A ray that starts outside enters at the first and leaves at the second.</summary>
    private static List<double> Crossings(Polygon2 polygon, Point2 origin, Point2 direction)
    {
        var hits = new List<double>();
        foreach (var ring in polygon.Holes.Prepend(polygon.Outer))
            for (int i = 0; i < ring.Count; i++)
            {
                Point2 a = ring[i], b = ring[(i + 1) % ring.Count];
                var e = b - a;
                double denominator = direction.X * e.Y - direction.Y * e.X;
                if (Math.Abs(denominator) < 1e-300) continue;
                var w = a - origin;
                double t = (w.X * e.Y - w.Y * e.X) / denominator;
                double u = (w.X * direction.Y - w.Y * direction.X) / denominator;
                if (t > 0 && u >= 0 && u < 1) hits.Add(t);
            }
        hits.Sort();
        return hits;
    }
}
