using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Polygons;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;
using OpenSim.Rf.Si.Layout;

namespace OpenSim.Rf.Extraction;

/// <summary>One coupling the capacitance matrix carries beyond the side-by-side runs: what it
/// is, where, and how many farads it adds between the two nets.</summary>
public sealed record CouplingTerm(string NetA, string NetB, string Kind, Point2 At, double Farads);

/// <summary>
/// FU-33 — the capacitance-matrix terms the run scan cannot see: traces of two nets CROSSING
/// on different layers, pads stacked over one another, pads side by side on a layer, and via
/// barrels running parallel. Each is priced by a closed form fed from a 2D field solve of its
/// own cross-section (<see cref="BroadsideExtractor"/> on a synthetic stack of the two
/// conductors, their dielectric, and a far ground), cached per distinct geometry:
/// <list type="bullet">
/// <item>a crossing at angle θ is the pair's mutual capacitance per length, times the mean of
/// the two widths, over sin θ — the overlap plate plus all four edge fringes;</item>
/// <item>stacked pads are the same section at the pads' equivalent square width, times that
/// width; side-by-side pads are the coplanar section times the facing length;</item>
/// <item>two parallel barrels are the two-cylinder form 2πεL/acosh((D² − r₁² − r₂²)/2r₁r₂)
/// over their shared vertical span.</item>
/// </list>
/// Copper of any third net (or a plane) at the spot, on a layer between the two, shields the
/// pair and the term is dropped. A plane's clearance holes around a via pair are not modelled:
/// the barrel term is conservative where planes cross the span.
/// </summary>
public static class NetCouplingTerms
{
    private static readonly IPolygonOps Ops = new ClipperPolygonOps();
    private const double ParallelCos = 0.9986;          // within ~3°: the run scan's territory

    /// <summary>
    /// Mutual capacitance per length [F/m] of two strips of the board: widths
    /// <paramref name="wA"/> (the upper layer's) and <paramref name="wB"/>, on two layers with
    /// their centres <paramref name="offset"/> apart in plan, or side by side on one layer with
    /// <paramref name="offset"/> between their EDGES — a 2D field solve of the two conductors
    /// and the stackup's dielectrics. The REAL reference planes bound the section where the
    /// board has them (<paramref name="planeBelowLayer"/> below,
    /// <paramref name="planeAboveLayer"/> above, 0 = none): a nearby plane takes most of each
    /// trace's field and shrinks the mutual, so leaving it out would badly over-state the
    /// coupling. With no plane below, a ground far below keeps the section solvable without
    /// stealing the mutual.
    /// </summary>
    internal static double MutualPerLength(BoardStackup stackup, int upperLayer, int lowerLayer,
        double wA, double wB, double offset, int planeBelowLayer = 0, int planeAboveLayer = 0)
    {
        // Dielectric span between two copper layers: thickness and the series εr.
        (double Thickness, double Epsilon) Span(int top, int bottom)
        {
            double thickness = 0, weighted = 0;
            for (int g = top; g < bottom; g++)
            {
                double t = stackup.GapThicknessOf(g);
                thickness += t;
                weighted += t / stackup.PermittivityOf(g);
                if (g > top) thickness += stackup.CopperThicknessOf(g);
            }
            return thickness > 0 ? (thickness, thickness / Math.Max(weighted, 1e-30))
                : (0, stackup.PermittivityOf(top));
        }
        var between = Span(upperLayer, lowerLayer);
        var layers = new List<LayeredStackup.Layer>();
        if (planeBelowLayer > lowerLayer)
        {
            var below = Span(lowerLayer, planeBelowLayer);
            layers.Add(new LayeredStackup.Layer(below.Epsilon, 0, below.Thickness));
        }
        else
            // No plane below: a far ground keeps the section solvable without stealing the mutual.
            layers.Add(new LayeredStackup.Layer(between.Epsilon, 0,
                30 * Math.Max(between.Thickness, Math.Max(wA, wB))));
        var traces = new List<LayeredTrace>();
        const double conductivity = 5.8e7;
        if (upperLayer == lowerLayer)
        {
            double pitch = offset + (wA + wB) / 2;
            traces.Add(new LayeredTrace(new TraceCrossSection(-pitch / 2, wA,
                stackup.CopperThicknessOf(upperLayer), conductivity), 0));
            traces.Add(new LayeredTrace(new TraceCrossSection(pitch / 2, wB,
                stackup.CopperThicknessOf(lowerLayer), conductivity), 0));
        }
        else
        {
            layers.Add(new LayeredStackup.Layer(between.Epsilon, 0, between.Thickness));
            traces.Add(new LayeredTrace(new TraceCrossSection(0, wB,
                stackup.CopperThicknessOf(lowerLayer), conductivity), 0));
            traces.Add(new LayeredTrace(new TraceCrossSection(offset, wA,
                stackup.CopperThicknessOf(upperLayer), conductivity), 1));
        }
        bool topGround = planeAboveLayer > 0 && planeAboveLayer < upperLayer;
        if (topGround)
        {
            var above = Span(planeAboveLayer, upperLayer);
            layers.Add(new LayeredStackup.Layer(above.Epsilon, 0, above.Thickness));
        }
        var rlgc = BroadsideExtractor.Extract(new LayeredStackup(layers), topGround, traces);
        return Math.Max(0, -rlgc.CapacitanceFaradsPerMeter[0, 1]);
    }

    /// <summary>
    /// Capacitance [F] of two strips of the board crossing at an angle whose sine is
    /// <paramref name="sinTheta"/>, or of two stacked pads taken as strips of their equivalent
    /// square widths (<paramref name="sinTheta"/> = 1): the broadside section's mutual per
    /// length, INTEGRATED over the lateral offset between the two and divided by sin θ. The
    /// plate part of the section integrates to exactly ε·wA·wB/h, and the integral carries how
    /// far each conductor's fringe reaches along the other's arms — which a single section
    /// times the width badly understates (on the 3D gate, by a factor near three).
    /// </summary>
    internal static double CrossingFarads(BoardStackup stackup, int upperLayer, int lowerLayer,
        double wA, double wB, double sinTheta, int planeBelowLayer = 0, int planeAboveLayer = 0)
    {
        double centre = MutualPerLength(stackup, upperLayer, lowerLayer, wA, wB, 0, planeBelowLayer, planeAboveLayer);
        if (!(centre > 0)) return 0;
        double step = Math.Max(Math.Min(wA, wB), 0.25 * (wA + wB)) / 2;
        double reach = 20 * (wA + wB);
        // Trapezoid over the symmetric profile, out to where the section's mutual has died.
        double integral = centre * step;
        for (double d = step; d <= reach; d += step)
        {
            double value = MutualPerLength(stackup, upperLayer, lowerLayer, wA, wB, d, planeBelowLayer, planeAboveLayer);
            integral += 2 * value * step;
            if (value < 0.004 * centre) break;
        }
        return integral / Math.Max(sinTheta, 1e-3);
    }

    public static List<CouplingTerm> Find(BoardLayoutIndex index, BoardStackup stackup,
        IReadOnlyList<CopperNet> nets, double maxEdgeGapMeters)
    {
        var board = index.Board;
        var wanted = new Dictionary<CopperNet, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < nets.Count; i++) wanted[nets[i]] = i;
        var terms = new List<CouplingTerm>();
        var solved = new Dictionary<(int, int, long, long, long, int, int), double>();   // per-length kinds
        const double minPlaneArea = 25e-6;                       // 5 × 5 mm of copper is a reference

        (int Below, int Above) Planes(int upperLayer, int lowerLayer, Point2 at)
        {
            int below = 0, above = 0;
            for (int k = lowerLayer + 1; k <= index.LayerCount && below == 0; k++)
                if (index.LargeIslandAt(k, at, minPlaneArea) is not null) below = k;
            for (int k = upperLayer - 1; k >= 1 && above == 0; k--)
                if (index.LargeIslandAt(k, at, minPlaneArea) is not null) above = k;
            return (below, above);
        }

        // Mutual capacitance per length with the real planes around the pair, one solve per
        // distinct geometry.
        double PerLength(int upperLayer, int lowerLayer, double wA, double wB, double planGap, Point2 at)
        {
            var (below, above) = Planes(upperLayer, lowerLayer, at);
            var key = (upperLayer, lowerLayer, (long)Math.Round(wA * 1e7), (long)Math.Round(wB * 1e7),
                (long)Math.Round(planGap * 1e7), below, above);
            if (!solved.TryGetValue(key, out double mutual))
                solved[key] = mutual = MutualPerLength(stackup, upperLayer, lowerLayer, wA, wB, planGap, below, above);
            return mutual;
        }

        // The crossing (and stacked-pad) integral with the real planes, cached the same way.
        var crossings = new Dictionary<(int, int, long, long, long, int, int), double>();
        double Crossing(int upperLayer, int lowerLayer, double wA, double wB, double sinTheta, Point2 at)
        {
            var (below, above) = Planes(upperLayer, lowerLayer, at);
            var key = (upperLayer, lowerLayer, (long)Math.Round(wA * 1e7), (long)Math.Round(wB * 1e7),
                (long)Math.Round(sinTheta * 1e4), below, above);
            if (!crossings.TryGetValue(key, out double farads))
                crossings[key] = farads = CrossingFarads(stackup, upperLayer, lowerLayer, wA, wB, sinTheta, below, above);
            return farads;
        }

        // Copper of anything else between the two layers at the point shields the pair.
        bool Shielded(int upperLayer, int lowerLayer, Point2 at)
        {
            for (int k = upperLayer + 1; k < lowerLayer; k++)
                if (index.IslandAt(k, at) is not null) return true;
            return false;
        }

        // ---- Crossings: centerlines of two nets on different layers, NOT nearly parallel
        // (parallel pairs are the run scan's, broadside included).
        var traces = index.Traces;
        var grid = new Dictionary<(long X, long Y), List<int>>();
        double cell = 2e-3;
        for (int i = 0; i < traces.Count; i++)
        {
            var t = traces[i].Trace;
            if (!wanted.ContainsKey(traces[i].Net)) continue;
            long x0 = (long)Math.Floor(Math.Min(t.Start.X, t.End.X) / cell), x1 = (long)Math.Floor(Math.Max(t.Start.X, t.End.X) / cell);
            long y0 = (long)Math.Floor(Math.Min(t.Start.Y, t.End.Y) / cell), y1 = (long)Math.Floor(Math.Max(t.Start.Y, t.End.Y) / cell);
            for (long x = x0; x <= x1; x++)
                for (long y = y0; y <= y1; y++)
                {
                    if (!grid.TryGetValue((x, y), out var list)) grid[(x, y)] = list = new List<int>();
                    list.Add(i);
                }
        }
        var seen = new HashSet<(int, int)>();
        foreach (var list in grid.Values)
            for (int a = 0; a < list.Count; a++)
                for (int b = a + 1; b < list.Count; b++)
                {
                    int i = Math.Min(list[a], list[b]), j = Math.Max(list[a], list[b]);
                    var (ti, ni) = traces[i];
                    var (tj, nj) = traces[j];
                    if (ti.LayerOrder == tj.LayerOrder || ReferenceEquals(ni, nj) || !seen.Add((i, j))) continue;
                    var di = (ti.End - ti.Start) * (1 / ti.Length);
                    var dj = (tj.End - tj.Start) * (1 / tj.Length);
                    double cos = Point2.Dot(di, dj);
                    double sin = Math.Sqrt(Math.Max(0, 1 - cos * cos));
                    if (Math.Abs(cos) >= ParallelCos) continue;
                    // Where the centerlines cross, if inside both spans.
                    var ni2 = new Point2(-di.Y, di.X);
                    double denominator = Point2.Dot(dj, ni2);
                    if (Math.Abs(denominator) < 1e-12) continue;
                    double sj = Point2.Dot(ti.Start - tj.Start, ni2) / denominator;
                    if (sj < 0 || sj > tj.Length) continue;
                    var at = tj.Start + dj * sj;
                    double si = Point2.Dot(at - ti.Start, di);
                    if (si < 0 || si > ti.Length) continue;
                    int upper = Math.Min(ti.LayerOrder, tj.LayerOrder), lower = Math.Max(ti.LayerOrder, tj.LayerOrder);
                    if (Shielded(upper, lower, at)) continue;
                    double wUpper = ti.LayerOrder == upper ? ti.Width : tj.Width;
                    double wLower = ti.LayerOrder == upper ? tj.Width : ti.Width;
                    terms.Add(new CouplingTerm(BoardLayoutIndex.NameOf(ni), BoardLayoutIndex.NameOf(nj),
                        "crossing", at, Crossing(upper, lower, wUpper, wLower, sin, at)));
                }

        // ---- Pads: the pad list with each pad's net read from the copper under it.
        var pads = new List<(CopperPad Pad, CopperNet Net)>();
        foreach (var pad in board.Pads)
        {
            var island = index.IslandAt(pad.LayerOrder, pad.Center);
            if (island is null || index.NetOf(island) is not { } net || !wanted.ContainsKey(net)) continue;
            pads.Add((pad, net));
        }
        for (int a = 0; a < pads.Count; a++)
            for (int b = a + 1; b < pads.Count; b++)
            {
                var (pa, na) = pads[a];
                var (pb, nb) = pads[b];
                if (ReferenceEquals(na, nb)) continue;
                double centre = (pa.Center - pb.Center).Length;
                if (pa.LayerOrder == pb.LayerOrder)
                {
                    double gap = centre - (pa.Size + pb.Size) / 2;
                    if (gap <= 0 || gap > maxEdgeGapMeters) continue;
                    double perLength = PerLength(pa.LayerOrder, pa.LayerOrder, pa.Size, pb.Size, gap, pa.Center);
                    terms.Add(new CouplingTerm(BoardLayoutIndex.NameOf(na), BoardLayoutIndex.NameOf(nb),
                        "pads side by side", pa.Center, perLength * Math.Min(pa.Size, pb.Size)));
                }
                else
                {
                    if (centre > (pa.Size + pb.Size) / 2) continue;      // no overlap in plan
                    var overlap = Ops.Intersect(new[] { pa.Shape }, new[] { (IReadOnlyList<Point2>)pb.Shape.Outer });
                    double area = overlap.Sum(p => p.Area());
                    if (!(area > 0)) continue;
                    int upper = Math.Min(pa.LayerOrder, pb.LayerOrder), lower = Math.Max(pa.LayerOrder, pb.LayerOrder);
                    if (Shielded(upper, lower, pa.Center)) continue;
                    double side = Math.Sqrt(area);
                    terms.Add(new CouplingTerm(BoardLayoutIndex.NameOf(na), BoardLayoutIndex.NameOf(nb),
                        "stacked pads", pa.Center, Crossing(upper, lower, side, side, 1, pa.Center)));
                }
            }

        // ---- Via barrels: two nets' plated barrels running parallel, the two-cylinder form
        // over their shared vertical span.
        var vias = new List<(Via Via, CopperNet Net)>();
        foreach (var net in nets)
            foreach (var bridge in net.StitchingVias)
                if (bridge.Via.Plated) vias.Add((bridge.Via, net));
        int layerCount = index.LayerCount;
        double ZofTop(int layer)
        {
            double z = 0;
            for (int g = 1; g < layer; g++) z += stackup.GapThicknessOf(g) + stackup.CopperThicknessOf(g);
            return z;
        }
        for (int a = 0; a < vias.Count; a++)
            for (int b = a + 1; b < vias.Count; b++)
            {
                var (va, na) = vias[a];
                var (vb, nb) = vias[b];
                if (ReferenceEquals(na, nb)) continue;
                double r1 = va.Diameter / 2, r2 = vb.Diameter / 2;
                double distance = (va.Position - vb.Position).Length;
                if (distance - r1 - r2 > maxEdgeGapMeters || distance <= r1 + r2) continue;
                var spanA = va.PlatedSpan(layerCount);
                var spanB = vb.PlatedSpan(layerCount);
                if (spanA is null || spanB is null) continue;
                int top = Math.Max(spanA.Value.Top, spanB.Value.Top);
                int bottom = Math.Min(spanA.Value.Bottom, spanB.Value.Bottom);
                if (bottom <= top) continue;
                double length = ZofTop(bottom) + stackup.CopperThicknessOf(bottom) - ZofTop(top);
                double weighted = 0, dielectric = 0;
                for (int g = top; g < bottom; g++)
                {
                    double t = stackup.GapThicknessOf(g);
                    dielectric += t;
                    weighted += t * stackup.PermittivityOf(g);
                }
                double epsilon = dielectric > 0 ? weighted / dielectric : stackup.PermittivityOf(top);
                double argument = (distance * distance - r1 * r1 - r2 * r2) / (2 * r1 * r2);
                double farads = 2 * Math.PI * 8.8541878128e-12 * epsilon * length / Math.Acosh(argument);
                terms.Add(new CouplingTerm(BoardLayoutIndex.NameOf(na), BoardLayoutIndex.NameOf(nb),
                    "via barrels", va.Position, farads));
            }
        return terms;
    }
}
