using OpenSim.Core.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;

namespace OpenSim.Pcb.Inductance;

/// <summary>Either an ordered, consistently oriented open chain, or a specific reason
/// none could be built. Exactly one of the two is non-null.</summary>
public sealed record TraceChainResult(IReadOnlyList<TraceCenterline>? Chain, string? FailureReason)
{
    public static TraceChainResult Success(IReadOnlyList<TraceCenterline> chain) => new(chain, null);
    public static TraceChainResult Failure(string reason) => new(null, reason);
}

/// <summary>A chain terminal: a pad center on a copper layer. When a source/sink pair is
/// supplied, the builder extracts the unique current path between them and prunes dead
/// side branches — which carry zero current under pad-to-pad drive, so the pruning is
/// exact physics, not an approximation.</summary>
public sealed record ChainTerminal(Point2 Center, int Layer)
{
    /// <summary>How far the pad's own copper reaches from its centre [m] (half its size);
    /// 0 when unknown. A terminal attaches to the nearest chain junction, and a junction
    /// farther away than this is reached through copper that is not in the model — the
    /// DC network says so on the pair instead of reporting a bare number.</summary>
    public double Reach { get; init; }
}

/// <summary>The 3D counterpart: planar trace segments at their layer mid-plane z plus
/// vertical via-barrel tubes, ordered and oriented, or a typed failure.</summary>
public sealed record TraceChain3DResult(IReadOnlyList<TraceSegment3D>? Chain, string? FailureReason)
{
    /// <summary>How many gaps were closed by composing a straight bar through connecting
    /// copper (routing runs trace → pad/fill → trace; only draws carry centerlines).
    /// Surfaced so the estimate can state the approximation.</summary>
    public int CopperBridges { get; init; }

    /// <summary>Segments excluded because they hang off the terminal-to-terminal path
    /// (dead branches / disconnected fragments — zero current under pad-to-pad drive).
    /// Zero when no terminals were supplied.</summary>
    public int PrunedSegments { get; init; }

    /// <summary>Total length [m] of the pruned segments, for the assumptions line.</summary>
    public double PrunedLengthMeters { get; init; }

    /// <summary>The stackup z frame the chain was built in: copper layer order →
    /// (zLo, zHi). Chains (and plane surfaces) can only be composed together when they
    /// share ONE frame — this is that frame, for consumers that need more z's in it
    /// (e.g. a return plane's copper surface).</summary>
    public IReadOnlyDictionary<int, (double zLo, double zHi)>? LayerZ { get; init; }

    public static TraceChain3DResult Success(IReadOnlyList<TraceSegment3D> chain) => new(chain, null);
    public static TraceChain3DResult Failure(string reason) => new(null, reason);
}

/// <summary>
/// Orders a net's conductor segments into ONE open chain by endpoint matching, flipping
/// segment directions so current flows consistently start → end along the chain. The
/// orientation step is load-bearing: <see cref="LoopComposer"/>'s mutual terms are signed
/// by the segment endpoints, and raw Gerber draw directions are arbitrary — an unoriented
/// chain would randomly add or subtract mutual inductance. Anything that is not a
/// non-branching open chain returns a typed failure — never a garbage chain that would
/// silently produce a wrong number. One topology engine serves both entry points: the
/// planar single-layer form, and the 3D form that lifts traces to their stackup z and
/// joins layers through plated via barrels.
/// </summary>
public static class TraceChainBuilder
{
    public static TraceChainResult Build(IReadOnlyList<TraceCenterline> centerlines)
    {
        if (centerlines.Count == 0)
            return TraceChainResult.Failure(
                "no trace centerlines (the net is a pour/region or was imported without draw records)");

        var layers = centerlines.Select(c => c.LayerOrder).Distinct().ToList();
        if (layers.Count > 1)
            return TraceChainResult.Failure(
                $"the net's traces span {layers.Count} layers (L{string.Join(", L", layers.OrderBy(l => l))}); " +
                "use the multi-layer chain (via barrels) form");

        // Endpoint matching tolerance: half the narrowest trace — endpoints that meet
        // within a trace width are the same junction; anything further apart is a gap.
        double tolerance = centerlines.Min(c => c.Width) / 2;

        // Real routing carries tiny jog segments shorter than the junction tolerance;
        // both their endpoints would cluster to one junction and fake a degenerate
        // segment. Their inductance is negligible by construction (length < width/2),
        // so drop them and let the surviving topology speak.
        var traces = SplitAtJunctions(
            Deduplicate(centerlines.Where(c => c.Length > tolerance).ToList(), tolerance),
            tolerance, Array.Empty<(Point2, int, double)>());
        if (traces.Count == 0)
            return TraceChainResult.Failure(
                "all trace segments are shorter than the junction tolerance (sub-width stubs)");

        var items = traces
            .Select(c => (new Vector3D(c.Start.X, c.Start.Y, 0), new Vector3D(c.End.X, c.End.Y, 0), tolerance))
            .ToList();
        DropSubJunctionSegments(items, traces);
        if (traces.Count == 0)
            return TraceChainResult.Failure(
                "all trace segments are shorter than the junction tolerance (sub-width stubs)");
        var (order, failure) = OrderChain(items);
        if (failure is not null)
            return TraceChainResult.Failure(failure);

        var chain = new List<TraceCenterline>(order!.Count);
        foreach (var (index, flipped) in order)
        {
            var segment = traces[index];
            chain.Add(flipped ? segment with { Start = segment.End, End = segment.Start } : segment);
        }
        return TraceChainResult.Success(chain);
    }

    /// <summary>
    /// The multi-layer form: traces are lifted to their copper layer's mid-plane z (the
    /// same <see cref="NetMesher.BuildStackupZ"/> model the mesher and preview use, so
    /// all three agree on where copper sits) and plated via barrels join layers as
    /// vertical thin-tube segments spanning mid-plane to mid-plane. A via junction
    /// tolerates endpoint mismatch up to the drill radius — draws end at the via centre,
    /// anywhere on the pad/annulus. A barrel is composed as one tube per pair of
    /// neighbouring layers it has a node on, so a via with pads on every layer still
    /// joins an L1 trace to an L2 trace (it used to be dropped unless traces ended on its
    /// two OUTERMOST layers). In the plain chain form the nodes are the layers where a
    /// drawn trace ends on the via — a stitch into a pad, pour, or nothing is a dead-end
    /// stub that carries no current, so it is left out instead of faking a branch. With
    /// terminals, and in the graph form, every layer the via connects is a node, so a pad
    /// on a layer with no drawn trace (a through-hole pin) can be a terminal; the unused
    /// lengths are dead ends and are pruned or carry no current.
    /// A trace that ends on the INTERIOR of another (a T), crosses it, or passes through a
    /// via is split there first, so the junction is a node rather than a free end.
    /// Real routing also runs trace → pad/fill → trace, and only draws carry
    /// centerlines: when the net's <paramref name="islands"/> are supplied, two free
    /// chain endpoints sitting in the SAME island (the union already proved that copper
    /// continuous) within a pad-scale span are closed with a straight trace-width bar,
    /// and the bridge count is reported on the result. Longer same-island gaps stay a
    /// loud disconnection — a straight bar across a large pour would misstate its
    /// inductance. Genuine branch/closed-loop topologies still fail loudly.
    /// </summary>
    /// <param name="includeLayers">Extra copper layer orders to include in the stackup z
    /// frame (widening the span, not adding segments) — pass the OTHER chain's layers, or
    /// a return plane's, so two composed geometries share one z origin.</param>
    /// <param name="terminals">Optional source/sink pads. When present, the requirement
    /// relaxes from "the whole net is one open chain" to "there is exactly one path
    /// between the terminals": the unique path through the (tree-shaped) segment graph is
    /// extracted and dead side branches are pruned — they carry no current under
    /// pad-to-pad drive, so dropping them is exact. Parallel paths (a cycle in the
    /// component) remain a typed failure: a single path's inductance would be wrong.</param>
    public static TraceChain3DResult Build(IReadOnlyList<TraceCenterline> centerlines,
        IReadOnlyList<ViaBridge> vias, NetMeshOptions options,
        IReadOnlyList<CopperIsland>? islands = null,
        IReadOnlyCollection<int>? includeLayers = null,
        (ChainTerminal Source, ChainTerminal Sink)? terminals = null)
    {
        var (segments, itemTolerances, layerZ, firstBridgeIndex, copperBridges, prepFailure) =
            PrepareSegments(centerlines, vias, options, islands, includeLayers,
                wholeBarrels: terminals is not null);
        if (prepFailure is not null)
            return TraceChain3DResult.Failure(prepFailure);

        var items = segments
            .Select((s, i) => (s.Start, s.End, itemTolerances[i]))
            .ToList();

        if (terminals is { } pads)
            return ExtractTerminalPath(segments, items, pads, layerZ, firstBridgeIndex);

        var (order, failure) = OrderChain(items);
        if (failure is not null)
            return TraceChain3DResult.Failure(failure);

        var chain = new List<TraceSegment3D>(order!.Count);
        foreach (var (index, flipped) in order)
        {
            var segment = segments[index];
            chain.Add(flipped ? segment with { Start = segment.End, End = segment.Start } : segment);
        }
        return TraceChain3DResult.Success(chain) with { CopperBridges = copperBridges, LayerZ = layerZ };
    }

    /// <summary>
    /// The net's conductor topology as a GRAPH instead of one ordered path: the SAME
    /// segment recipe as the 3D <c>Build</c> (dedup, stackup z, via barrels with the
    /// dead-end gate, stub drops, copper bridges), then junction clustering. Exposed
    /// for the DC resistance network — branches and parallel paths (the chain's typed
    /// "a network solve is required" failure) are exactly what a nodal solve handles.
    /// A bridge bar that clusters degenerate after appending (a bridge endpoint can
    /// enlarge a junction's tolerance enough to collapse a short bar) is dropped like
    /// any sub-junction stub — it carries no information for a network — never fatal.
    /// </summary>
    public static TraceGraphResult BuildGraph(IReadOnlyList<TraceCenterline> centerlines,
        IReadOnlyList<ViaBridge> vias, NetMeshOptions options,
        IReadOnlyList<CopperIsland>? islands = null,
        IReadOnlyCollection<int>? includeLayers = null)
    {
        var (segments, itemTolerances, layerZ, firstBridgeIndex, _, prepFailure) =
            PrepareSegments(centerlines, vias, options, islands, includeLayers, wholeBarrels: true);
        if (prepFailure is not null)
            return TraceGraphResult.Failure(prepFailure);

        // The chain path treats a post-bridge degenerate as an unreachable safety net;
        // a network wants a clean edge list, so run the same drop once more here. The
        // bridge flag rides the parallel-list mechanism so FirstBridgeIndex stays exact
        // (bridges are appended, so survivors remain contiguous at the tail).
        var isBridge = new List<bool>(segments.Count);
        for (int i = 0; i < segments.Count; i++) isBridge.Add(i >= firstBridgeIndex);
        var items = segments
            .Select((s, i) => (s.Start, s.End, itemTolerances[i]))
            .ToList();
        DropSubJunctionSegments(items, segments, itemTolerances, isBridge);
        if (segments.Count == 0)
            return TraceGraphResult.Failure(
                "all trace segments are shorter than the junction tolerance (sub-width stubs)");
        int firstSurvivingBridge = isBridge.IndexOf(true);
        firstBridgeIndex = firstSurvivingBridge >= 0 ? firstSurvivingBridge : segments.Count;

        var (junctions, ends, _, degenerateIndex) = Cluster(items);
        if (degenerateIndex >= 0)                        // unreachable after the drop; safety net
            return TraceGraphResult.Failure(DegenerateMessage(items, degenerateIndex));

        return new TraceGraphResult(segments, ends, junctions, layerZ,
            segments.Count - firstBridgeIndex, firstBridgeIndex, null);
    }

    /// <summary>
    /// The shared 3D segment recipe: dedup coincident draws → stackup z frame → traces
    /// lifted to their layer mid-plane → plated via barrels (dead-end stitches dropped)
    /// → sub-junction stub drops → copper bridges through shared islands. Extracted
    /// verbatim from the 3D <c>Build</c> so the chain/path extraction and
    /// <see cref="BuildGraph"/> consume ONE set of composition rules — two copies of
    /// these hard-won real-board rules would drift.
    /// </summary>
    private static (List<TraceSegment3D> Segments, List<double> Tolerances,
        Dictionary<int, (double zLo, double zHi)> LayerZ, int FirstBridgeIndex,
        int CopperBridges, string? Failure) PrepareSegments(
        IReadOnlyList<TraceCenterline> centerlines,
        IReadOnlyList<ViaBridge> vias, NetMeshOptions options,
        IReadOnlyList<CopperIsland>? islands,
        IReadOnlyCollection<int>? includeLayers,
        bool wholeBarrels)
    {
        static (List<TraceSegment3D>, List<double>, Dictionary<int, (double zLo, double zHi)>,
            int, int, string?) Fail(string reason) =>
            (new List<TraceSegment3D>(), new List<double>(),
                new Dictionary<int, (double zLo, double zHi)>(), 0, 0, reason);

        if (centerlines.Count == 0)
            return Fail("no trace centerlines (the net is a pour/region or was imported without draw records)");

        double tolerance = centerlines.Min(c => c.Width) / 2;
        var viaAnchors = new List<(Point2 Point, int Layer, double Reach)>();
        foreach (var bridge in vias)
            foreach (int layer in bridge.Layers.Distinct())
                viaAnchors.Add((bridge.Via.Position, layer, Math.Max(tolerance, bridge.Via.Diameter / 2)));
        var traces = SplitAtJunctions(
            Deduplicate(centerlines.Where(c => c.Length > tolerance).ToList(), tolerance),
            tolerance, viaAnchors);
        if (traces.Count == 0)
            return Fail("all trace segments are shorter than the junction tolerance (sub-width stubs)");

        int minL = traces.Min(c => c.LayerOrder);
        int maxL = traces.Max(c => c.LayerOrder);
        foreach (var bridge in vias)
        {
            minL = Math.Min(minL, bridge.Layers.Min());
            maxL = Math.Max(maxL, bridge.Layers.Max());
        }
        foreach (int layer in includeLayers ?? Array.Empty<int>())
        {
            minL = Math.Min(minL, layer);
            maxL = Math.Max(maxL, layer);
        }
        var (layerZ, _) = NetMesher.BuildStackupZ(minL, maxL, options);
        double MidZ(int layer) => 0.5 * (layerZ[layer].zLo + layerZ[layer].zHi);

        var segments = new List<TraceSegment3D>();
        var itemTolerances = new List<double>();
        foreach (var c in traces)
        {
            double z = MidZ(c.LayerOrder);
            double thickness = layerZ[c.LayerOrder].zHi - layerZ[c.LayerOrder].zLo;
            segments.Add(new TraceSegment3D(
                new Vector3D(c.Start.X, c.Start.Y, z),
                new Vector3D(c.End.X, c.End.Y, z),
                c.Width, thickness));
            itemTolerances.Add(tolerance);
        }
        foreach (var bridge in vias)
        {
            var connected = bridge.Layers.Distinct().OrderBy(l => l).ToList();
            if (connected.Count < 2) continue;
            var p = bridge.Via.Position;
            double viaTolerance = Math.Max(tolerance, bridge.Via.Diameter / 2);

            // The layers on which a drawn trace ends at the via. (A via with pads on all
            // layers and routing L1 → L2 has traces on neither of its outermost layers'
            // pair — testing only top and bottom dropped the barrel and split the net.)
            bool EndsOn(int layer) => traces.Any(c =>
                c.LayerOrder == layer
                && ((c.Start - p).Length <= viaTolerance || (c.End - p).Length <= viaTolerance));
            var attached = connected.Where(EndsOn).ToList();

            // Barrel nodes: every connected layer when the whole barrel is wanted and at
            // least one trace reaches it; else only the trace-attached layers, which
            // leaves out a dead-end stitch entirely.
            var nodes = wholeBarrels
                ? (attached.Count >= 1 ? connected : new List<int>())
                : attached;
            for (int k = 1; k < nodes.Count; k++)
            {
                // The tube's mean shell diameter: the bore (finished hole) plus one
                // plating wall — the same annulus geometry the net mesher extrudes.
                segments.Add(new TraceSegment3D(
                    new Vector3D(p.X, p.Y, MidZ(nodes[k - 1])),
                    new Vector3D(p.X, p.Y, MidZ(nodes[k])),
                    bridge.Via.Diameter + options.ViaPlatingThickness,
                    options.ViaPlatingThickness,
                    SegmentProfile.RoundTube));
                itemTolerances.Add(viaTolerance);
            }
        }

        // Sub-junction-scale drop happens BEFORE bridging: a segment collapsed inside a
        // junction tolerance would otherwise abort both the bridge scan and the ordering.
        var items = segments
            .Select((s, i) => (s.Start, s.End, itemTolerances[i]))
            .ToList();
        DropSubJunctionSegments(items, segments, itemTolerances);
        if (segments.Count == 0)
            return Fail("all trace segments are shorter than the junction tolerance (sub-width stubs)");

        int firstBridgeIndex = segments.Count;
        int copperBridges = 0;
        if (islands is not null && islands.Count > 0)
            copperBridges = BridgeThroughCopper(segments, itemTolerances, islands, layerZ);

        return (segments, itemTolerances, layerZ, firstBridgeIndex, copperBridges, null);
    }

    /// <summary>Longest same-island gap a straight bar may close [m] — pad/fill scale.
    /// Beyond this, the real current path through a pour is too far from straight for
    /// the bar's inductance to be honest, so the disconnection is reported instead.</summary>
    private const double MaxBridgeSpan = 5e-3;

    /// <summary>
    /// Closes trace → pad/fill → trace gaps: pairs of free (degree-1) chain endpoints
    /// from DIFFERENT connected pieces that sit inside the same copper island on the same
    /// layer are joined by a straight bar sized like the traces it connects, provided the
    /// bar itself lies in that island. Being in one island proves only that the two ends
    /// are connected SOMEHOW (every free end of a net on a layer is); the straight line
    /// staying in copper is what makes a straight bar a fair model of it. Pairs are taken closest
    /// first (deterministic); a terminal source/sink endpoint simply never finds an
    /// unclaimed partner in its pad. Appends to <paramref name="segments"/> /
    /// <paramref name="itemTolerances"/> and returns the number of bridges added.
    /// </summary>
    private static int BridgeThroughCopper(List<TraceSegment3D> segments, List<double> itemTolerances,
        IReadOnlyList<CopperIsland> islands,
        Dictionary<int, (double zLo, double zHi)> layerZ)
    {
        // Islands on layers outside the chain's stackup span cannot host a bridge.
        var islandZ = islands
            .Where(i => layerZ.ContainsKey(i.LayerOrder))
            .Select(i => (Island: i, Z: 0.5 * (layerZ[i.LayerOrder].zLo + layerZ[i.LayerOrder].zHi)))
            .ToList();
        var items = segments
            .Select((s, i) => (s.Start, s.End, itemTolerances[i]))
            .ToList();
        var (junctions, ends, degree, degenerateIndex) = Cluster(items);
        if (degenerateIndex >= 0)
            return 0;                                   // let OrderChain surface the failure

        var free = degree.Where(kv => kv.Value.Count == 1).ToDictionary(kv => kv.Key, kv => kv.Value[0]);
        if (free.Count <= 2)
            return 0;                                   // already one open chain (or empty)

        // Connected piece of every junction (union-find over the segment graph), so a
        // bridge never closes a loop within one piece.
        var piece = new int[junctions.Count];
        for (int i = 0; i < piece.Length; i++) piece[i] = i;
        int Find(int j) => piece[j] == j ? j : piece[j] = Find(piece[j]);
        foreach (var (a, b) in ends) piece[Find(a)] = Find(b);

        // Candidate pairs: different pieces, same layer (equal z), pad-scale gap, both
        // inside one island — ordered nearest-first for determinism.
        var candidates = new List<(double Gap, int A, int B)>();
        var freeKeys = free.Keys.OrderBy(j => j).ToList();
        for (int x = 0; x < freeKeys.Count; x++)
            for (int y = x + 1; y < freeKeys.Count; y++)
            {
                int a = freeKeys[x], b = freeKeys[y];
                if (Find(a) == Find(b)) continue;
                var pa = junctions[a].Position;
                var pb = junctions[b].Position;
                if (Math.Abs(pa.Z - pb.Z) > 1e-12) continue;
                double gap = (pa - pb).Length;
                if (gap > MaxBridgeSpan) continue;
                bool sameIsland = islandZ.Any(entry =>
                    Math.Abs(entry.Z - pa.Z) <= 1e-12
                    && BarLiesIn(entry.Island.Shape, new Point2(pa.X, pa.Y), new Point2(pb.X, pb.Y)));
                if (sameIsland)
                    candidates.Add((gap, a, b));
            }
        candidates.Sort((l, r) => l.Gap != r.Gap ? l.Gap.CompareTo(r.Gap) : (l.A, l.B).CompareTo((r.A, r.B)));

        int bridges = 0;
        var claimed = new HashSet<int>();
        foreach (var (_, a, b) in candidates)
        {
            if (claimed.Contains(a) || claimed.Contains(b) || Find(a) == Find(b)) continue;
            claimed.Add(a);
            claimed.Add(b);
            piece[Find(a)] = Find(b);
            var incidentA = segments[free[a]];
            var incidentB = segments[free[b]];
            double width = 0.5 * (incidentA.Width + incidentB.Width);
            segments.Add(new TraceSegment3D(
                junctions[a].Position, junctions[b].Position,
                width, 0.5 * (incidentA.Thickness + incidentB.Thickness)));
            itemTolerances.Add(width / 2);
            bridges++;
        }
        return bridges;
    }

    /// <summary>Both ends and the straight line between them (sampled) lie in the island.</summary>
    private static bool BarLiesIn(Polygon2 island, Point2 a, Point2 b)
    {
        const int samples = 16;
        var shape = new[] { island };
        for (int k = 0; k <= samples; k++)
            if (!Meshing2D.PlanarMesher.ContainsPoint(shape, a + (b - a) * ((double)k / samples)))
                return false;
        return true;
    }

    /// <summary>
    /// Makes every in-layer meeting of two traces an endpoint of both. Junctions are
    /// found by clustering ENDPOINTS, so a trace that ends on the middle of another (a T),
    /// two traces that cross, or a trace running through a via left a free end where
    /// there is a three- or four-way node: the DC network then reported the stub as
    /// disconnected, or bridged it to the nearest free end and priced a path that does
    /// not exist. Each segment is cut at the foot of every other trace's endpoint that
    /// lies on its interior within the junction tolerance, at every proper crossing, and
    /// at every <paramref name="anchors"/> point (via centres) on its layer.
    /// </summary>
    private static List<TraceCenterline> SplitAtJunctions(List<TraceCenterline> traces, double tolerance,
        IReadOnlyList<(Point2 Point, int Layer, double Reach)> anchors)
    {
        var result = new List<TraceCenterline>(traces.Count);
        var cuts = new List<double>();
        for (int i = 0; i < traces.Count; i++)
        {
            var s = traces[i];
            var d = s.End - s.Start;
            double length = d.Length;
            var unit = d * (1.0 / length);
            cuts.Clear();

            // A point on the segment's interior: farther than `reach` from both ends
            // (nearer is an ordinary endpoint match) and within `reach` of the line.
            void Consider(Point2 p, double reach)
            {
                double along = Point2.Dot(p - s.Start, unit);
                if (along <= reach || along >= length - reach) return;
                if (Math.Abs(Point2.Cross(unit, p - s.Start)) > reach) return;
                cuts.Add(along);
            }

            for (int j = 0; j < traces.Count; j++)
            {
                if (j == i || traces[j].LayerOrder != s.LayerOrder) continue;
                var o = traces[j];
                Consider(o.Start, tolerance);
                Consider(o.End, tolerance);

                // Proper crossing, away from all four ends.
                var e = o.End - o.Start;
                double denominator = Point2.Cross(d, e);
                if (Math.Abs(denominator) <= 1e-12 * length * e.Length) continue;   // parallel
                double u = Point2.Cross(o.Start - s.Start, e) / denominator;
                double v = Point2.Cross(o.Start - s.Start, d) / denominator;
                double alongS = u * length, alongO = v * e.Length;
                if (alongS > tolerance && alongS < length - tolerance
                    && alongO > tolerance && alongO < e.Length - tolerance)
                    cuts.Add(alongS);
            }
            foreach (var (point, layer, reach) in anchors)
                if (layer == s.LayerOrder)
                    Consider(point, reach);

            if (cuts.Count == 0)
            {
                result.Add(s);
                continue;
            }
            cuts.Sort();
            var from = s.Start;
            double last = 0;
            foreach (double cut in cuts)
            {
                if (cut - last <= tolerance) continue;                   // same junction
                var at = s.Start + unit * cut;
                result.Add(s with { Start = from, End = at });
                from = at;
                last = cut;
            }
            result.Add(s with { Start = from, End = s.End });
        }
        // Collinear overlaps come out of the split as coincident pieces.
        return Deduplicate(result.Where(c => c.Length > tolerance).ToList(), tolerance);
    }

    /// <summary>
    /// Collapses coincident draws: real CAD exports redraw the same centerline (pad
    /// entries, per-netclass replays), and each copy adds a segment-end to the shared
    /// junctions — a chain through a single via then reads as a 3-way branch. Two
    /// segments are duplicates when both endpoint pairs match within the junction
    /// tolerance on the same layer (either orientation); the wider survives (the copper
    /// is the union, and width only enters L through the GMD). Keep-first plus a
    /// width-compare is deterministic. Public because the per-trace capacitance
    /// extractor needs the SAME rule — copper drawn twice must never count twice there
    /// either — and two implementations of one contract would drift.
    /// </summary>
    public static List<TraceCenterline> Deduplicate(List<TraceCenterline> traces, double tolerance)
    {
        var kept = new List<TraceCenterline>();
        foreach (var c in traces)
        {
            int existing = kept.FindIndex(k => k.LayerOrder == c.LayerOrder
                && ((Within(k.Start, c.Start, tolerance) && Within(k.End, c.End, tolerance))
                 || (Within(k.Start, c.End, tolerance) && Within(k.End, c.Start, tolerance))));
            if (existing < 0) kept.Add(c);
            else if (c.Width > kept[existing].Width) kept[existing] = c;
        }
        return kept;
    }

    private static bool Within(Point2 a, Point2 b, double tolerance)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy <= tolerance * tolerance;
    }

    /// <summary>
    /// The terminal-aware path: maps each pad to its nearest junction, requires the
    /// connected component holding both to be a tree (edge count = node count − 1 ⇒ the
    /// path is unique), walks it, and prunes everything else. Branch topologies that are
    /// fatal without terminals succeed here because the excluded copper carries zero
    /// current under pad-to-pad drive. A cycle stays a typed failure — the current would
    /// split between parallel paths and one path's L is simply the wrong number.
    /// </summary>
    private static TraceChain3DResult ExtractTerminalPath(List<TraceSegment3D> segments,
        IReadOnlyList<(Vector3D Start, Vector3D End, double Tolerance)> items,
        (ChainTerminal Source, ChainTerminal Sink) terminals,
        Dictionary<int, (double zLo, double zHi)> layerZ,
        int firstBridgeIndex)
    {
        var (junctions, ends, degree, degenerateIndex) = Cluster(items);
        if (degenerateIndex >= 0)                        // unreachable after the pre-drop; safety net
            return TraceChain3DResult.Failure(DegenerateMessage(items, degenerateIndex));

        int sourceJunction = MapTerminal(terminals.Source, "source", junctions, layerZ, out var sourceFailure, out _);
        if (sourceFailure is not null) return TraceChain3DResult.Failure(sourceFailure);
        int sinkJunction = MapTerminal(terminals.Sink, "sink", junctions, layerZ, out var sinkFailure, out _);
        if (sinkFailure is not null) return TraceChain3DResult.Failure(sinkFailure);
        if (sourceJunction == sinkJunction)
            return TraceChain3DResult.Failure("the source and sink pads land on the same chain junction");

        // Component + tree check via union-find over the segment graph.
        var piece = new int[junctions.Count];
        for (int i = 0; i < piece.Length; i++) piece[i] = i;
        int Find(int j) => piece[j] == j ? j : piece[j] = Find(piece[j]);
        foreach (var (a, b) in ends) piece[Find(a)] = Find(b);

        int component = Find(sourceJunction);
        if (Find(sinkJunction) != component)
            return TraceChain3DResult.Failure(
                "the source and sink pads sit on disconnected trace pieces (no drawn path joins them)");
        int componentNodes = Enumerable.Range(0, junctions.Count).Count(j => Find(j) == component);
        int componentEdges = ends.Count(e => Find(e.Start) == component);
        if (componentEdges != componentNodes - 1)
            return TraceChain3DResult.Failure(
                "parallel paths exist between the source and sink pads — a network solve is required");

        // Unique path in the tree: BFS parents from the source, read back from the sink.
        var parentSegment = new int[junctions.Count];
        Array.Fill(parentSegment, -1);
        var visited = new bool[junctions.Count];
        visited[sourceJunction] = true;
        var queue = new Queue<int>();
        queue.Enqueue(sourceJunction);
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            foreach (int segment in degree[current])
            {
                int next = ends[segment].Start == current ? ends[segment].End : ends[segment].Start;
                if (visited[next]) continue;
                visited[next] = true;
                parentSegment[next] = segment;
                queue.Enqueue(next);
            }
        }

        var pathSegments = new List<int>();
        for (int j = sinkJunction; j != sourceJunction;)
        {
            int segment = parentSegment[j];
            pathSegments.Add(segment);
            j = ends[segment].Start == j ? ends[segment].End : ends[segment].Start;
        }
        pathSegments.Reverse();

        var chain = new List<TraceSegment3D>(pathSegments.Count);
        int at = sourceJunction;
        foreach (int index in pathSegments)
        {
            bool flipped = ends[index].Start != at;
            var segment = segments[index];
            chain.Add(flipped ? segment with { Start = segment.End, End = segment.Start } : segment);
            at = ends[index].Start == at ? ends[index].End : ends[index].Start;
        }

        var onPath = new HashSet<int>(pathSegments);
        int pruned = segments.Count - pathSegments.Count;
        double prunedLength = Enumerable.Range(0, segments.Count)
            .Where(i => !onPath.Contains(i))
            .Sum(i => (segments[i].End - segments[i].Start).Length);
        int bridgesOnPath = pathSegments.Count(i => i >= firstBridgeIndex);

        return TraceChain3DResult.Success(chain) with
        {
            CopperBridges = bridgesOnPath,
            LayerZ = layerZ,
            PrunedSegments = pruned,
            PrunedLengthMeters = prunedLength
        };
    }

    /// <summary>
    /// Maps a pad terminal to the nearest chain junction on its layer's mid-plane within
    /// the pad-scale span. Junction z's are exact layer mid-planes (traces, barrels, and
    /// bridges are all placed there), so an exact-scale z gate keeps a pad from anchoring
    /// to copper on another layer directly beneath it. Internal because the DC resistance
    /// network attaches pads by the SAME rule — two implementations of one attachment
    /// contract would drift (the <see cref="Deduplicate"/> precedent). Returns the
    /// junction index, or −1 with <paramref name="failure"/> set;
    /// <paramref name="attachDistance"/> is how far that junction is from the pad centre, so a
    /// caller can say when the attachment runs through copper that is not in the model.
    /// </summary>
    internal static int MapTerminal(ChainTerminal terminal, string label,
        IReadOnlyList<(Vector3D Position, double Tolerance)> junctions,
        IReadOnlyDictionary<int, (double zLo, double zHi)> layerZ,
        out string? failure, out double attachDistance)
    {
        failure = null;
        attachDistance = 0;
        if (!layerZ.TryGetValue(terminal.Layer, out var span))
        {
            failure = $"the {label} pad's layer L{terminal.Layer} is outside the chain's stackup span";
            return -1;
        }
        double z = 0.5 * (span.zLo + span.zHi);
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int j = 0; j < junctions.Count; j++)
        {
            if (Math.Abs(junctions[j].Position.Z - z) > 1e-12) continue;
            double dx = junctions[j].Position.X - terminal.Center.X;
            double dy = junctions[j].Position.Y - terminal.Center.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= MaxBridgeSpan && distance < bestDistance)
            {
                best = j;
                bestDistance = distance;
            }
        }
        if (best >= 0)
            attachDistance = bestDistance;
        if (best < 0)
            failure = $"no chain junction within {MaxBridgeSpan * 1e3:g3} mm of the {label} pad at " +
                      $"({terminal.Center.X * 1e3:g4}, {terminal.Center.Y * 1e3:g4}) mm on L{terminal.Layer}";
        return best;
    }

    // ------------------------------------------------------------------
    // The topology engine: cluster endpoints into junctions (each endpoint carries its
    // own matching tolerance; a junction absorbs the largest tolerance seen; an endpoint
    // joins the NEAREST junction within tolerance), classify by degree, then walk from
    // the lexicographically smallest free endpoint flipping segments head-to-tail.
    // Returns the visit order + flip flags, or the typed failure.
    // ------------------------------------------------------------------
    private static (List<(Vector3D Position, double Tolerance)> Junctions, (int Start, int End)[] Ends,
        Dictionary<int, List<int>> Degree, int DegenerateIndex) Cluster(
        IReadOnlyList<(Vector3D Start, Vector3D End, double Tolerance)> items)
    {
        var junctions = new List<(Vector3D Position, double Tolerance)>();
        // NEAREST junction within tolerance, never first-match: a via barrel carries a
        // drill-radius tolerance much larger than the trace tolerance, and first-match
        // let it absorb into an EARLIER trace junction up to a drill radius away — that
        // junction then hosted three segment-ends and a chain that merely passes through
        // one via reported "3 traces meet there". Ties break to the lowest index.
        int JunctionOf(Vector3D p, double tolerance)
        {
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < junctions.Count; i++)
            {
                double distance = (junctions[i].Position - p).Length;
                if (distance <= Math.Max(junctions[i].Tolerance, tolerance) && distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }
            if (best >= 0)
            {
                if (tolerance > junctions[best].Tolerance)
                    junctions[best] = (junctions[best].Position, tolerance);
                return best;
            }
            junctions.Add((p, tolerance));
            return junctions.Count - 1;
        }

        int n = items.Count;
        var ends = new (int Start, int End)[n];
        var degree = new Dictionary<int, List<int>>();   // junction → incident segment indices
        for (int i = 0; i < n; i++)
        {
            int a = JunctionOf(items[i].Start, items[i].Tolerance);
            int b = JunctionOf(items[i].End, items[i].Tolerance);
            if (a == b)
                return (junctions, ends, degree, i);     // sub-junction-scale — caller drops it
            ends[i] = (a, b);
            (degree.TryGetValue(a, out var la) ? la : degree[a] = new List<int>()).Add(i);
            (degree.TryGetValue(b, out var lb) ? lb : degree[b] = new List<int>()).Add(i);
        }
        return (junctions, ends, degree, -1);
    }

    /// <summary>
    /// Drops segments whose two endpoints cluster to the SAME junction: such a segment is
    /// shorter than that junction's matching tolerance (a jog under a via pad, whose
    /// drill-radius tolerance exceeds the global width/2 stub cut), so like a sub-width
    /// stub its inductance is negligible at the junction's own scale. Iterative because a
    /// removal reshapes the junctions. Removes in lockstep from every parallel list.
    /// </summary>
    private static void DropSubJunctionSegments(
        List<(Vector3D Start, Vector3D End, double Tolerance)> items, params System.Collections.IList[] parallel)
    {
        while (items.Count > 0)
        {
            var (_, _, _, degenerateIndex) = Cluster(items);
            if (degenerateIndex < 0) return;
            items.RemoveAt(degenerateIndex);
            foreach (var list in parallel) list.RemoveAt(degenerateIndex);
        }
    }

    private static string DegenerateMessage(
        IReadOnlyList<(Vector3D Start, Vector3D End, double Tolerance)> items, int index) =>
        $"segment {index} is degenerate (both endpoints coincide at " +
        $"({items[index].Start.X:g4}, {items[index].Start.Y:g4}))";

    private static (List<(int Index, bool Flipped)>? Order, string? Failure) OrderChain(
        IReadOnlyList<(Vector3D Start, Vector3D End, double Tolerance)> items)
    {
        var (junctions, ends, degree, degenerateIndex) = Cluster(items);
        if (degenerateIndex >= 0)                        // unreachable after the pre-drop; safety net
            return (null, DegenerateMessage(items, degenerateIndex));
        int n = items.Count;

        foreach (var (junction, segments) in degree)
            if (segments.Count > 2)
            {
                var p = junctions[junction].Position;
                return (null,
                    $"the net branches at ({p.X * 1e3:g4}, {p.Y * 1e3:g4}) mm — {segments.Count} traces meet there");
            }

        var endpoints = degree.Where(kv => kv.Value.Count == 1).Select(kv => kv.Key).ToList();
        if (endpoints.Count == 0)
            return (null, "the traces form a closed loop (no free endpoints)");
        if (endpoints.Count > 2)
            return (null, $"the traces form {endpoints.Count / 2} disconnected pieces");

        // Walk from one free endpoint, flipping segments so each starts where the
        // previous ended. Deterministic start: the lexicographically smallest endpoint.
        int start = endpoints
            .OrderBy(j => junctions[j].Position.X)
            .ThenBy(j => junctions[j].Position.Y)
            .ThenBy(j => junctions[j].Position.Z)
            .First();
        var used = new bool[n];
        var order = new List<(int Index, bool Flipped)>(n);
        int current = start;
        for (int step = 0; step < n; step++)
        {
            int next = degree[current].FirstOrDefault(s => !used[s], -1);
            if (next < 0)
                return (null, "the traces form disconnected pieces");
            used[next] = true;
            order.Add((next, ends[next].Start != current));
            current = ends[next].Start == current ? ends[next].End : ends[next].Start;
        }
        return (order, null);
    }
}
