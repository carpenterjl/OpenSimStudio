using OpenSim.Core.Model;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Pcb.Ipc2581;

/// <summary>
/// Maps a parsed <see cref="Ipc2581Board"/> into the engine's <see cref="PcbBoard"/>.
/// Because IPC-2581 declares net membership explicitly, copper is unioned per (net, layer)
/// and nets are built directly from the file's net names — no union-find inference. Only
/// the "No Net" bucket (features without a net) goes through <see cref="NetExtractor"/>
/// so its unconnected blobs stay individually selectable.
/// </summary>
public sealed class Ipc2581BoardBuilder
{
    /// <summary>Flashes above this size are pours/heatsinks, not pads (mirrors PadExtractor).</summary>
    private const double MaxPadSize = 5e-3;

    private readonly IPolygonOps _ops = new ClipperPolygonOps();

    /// <summary>Caps the net-level parallelism; null lets the scheduler decide.
    /// Output is bitwise-identical for any value — the tests pin 1 vs unbounded.</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>The boolean work one net contributes, computed independently per net and
    /// stitched back in file order so island ids and warnings stay deterministic.
    /// LayerCopper holds (conductor index, that layer's unioned polygons), non-empty only.
    /// ResolvedPadOrders refines fallback-provenance holes by coincident same-net copper
    /// (parallel to the net's hole list; null = keep the declared list).</summary>
    private sealed record NetResult(
        List<(int LayerIndex, IReadOnlyList<Polygon2> Polygons)> LayerCopper,
        List<CopperPad> Pads,
        List<TraceCenterline> Centerlines,
        IReadOnlyList<int>?[] ResolvedPadOrders,
        long UnionMs);

    public PcbBoard Build(Ipc2581Board source)
    {
        // Aggregate stage timings surface in the log panel so a slow import names its
        // own bottleneck (per-net lines would be spam on a 100-net board).
        var totalTimer = System.Diagnostics.Stopwatch.StartNew();

        var warnings = new List<string>(source.Warnings);
        var notes = new List<string>(source.Notes);
        var conductors = source.ConductorLayers;
        var orderByName = conductors.ToDictionary(l => l.Name, l => l.CopperOrder!.Value);
        var nameByOrder = conductors.ToDictionary(l => l.CopperOrder!.Value, l => l.Name);

        // refdes → part name (footprint package as the fallback), for pad identity.
        var partByRefDes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var component in source.Components)
            partByRefDes[component.RefDes] = component.Part ?? component.PackageRef;

        // Nets are mutually independent boolean workloads: stroke + union in parallel,
        // then assemble strictly in file order (ids, warnings, list order bitwise-stable).
        var netList = source.Nets.Values.ToList();
        var results = new NetResult[netList.Count];
        var netLoopTimer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Parallel.For(0, netList.Count,
                new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism ?? -1 },
                i =>
                {
                    var net = netList[i];
                    try
                    {
                        var netCenterlines = new List<TraceCenterline>();
                        CollectCenterlines(net, orderByName, netCenterlines);
                        var layerCopper = new List<(int, IReadOnlyList<Polygon2>)>();
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        for (int c = 0; c < conductors.Count; c++)
                        {
                            var polygons = UnionLayerCopper(net, conductors[c].Name);
                            if (polygons.Count > 0) layerCopper.Add((c, polygons));
                        }
                        sw.Stop();
                        results[i] = new NetResult(layerCopper,
                            BuildPads(net, orderByName, partByRefDes),
                            netCenterlines,
                            ResolvePadOrdersByCoincidence(net, orderByName, nameByOrder, conductors.Count),
                            sw.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"IPC-2581 net '{net.Name}': {ex.Message}", ex);
                    }
                });
        }
        catch (AggregateException ae)
        {
            // Fail loudly with the (net-named) cause, not a scheduler wrapper.
            throw ae.InnerExceptions[0];
        }
        netLoopTimer.Stop();

        // Backdrills sever coincident vias over their span (minus the spec's protected
        // layers); prepared once, applied in the sequential loop below.
        var backdrills = PrepareBackdrills(source, orderByName, conductors.Count, warnings);
        int severedVias = 0;

        var islands = new List<CopperIsland>();
        var pads = new List<CopperPad>();
        var vias = new List<Via>();
        var centerlines = new List<TraceCenterline>();
        // Per source net: the islands and via bridges that will form its CopperNet.
        var netParts = new List<(string Name, List<CopperIsland> Islands, List<ViaBridge> Bridges)>();
        var noNetIslands = new List<CopperIsland>();
        var noNetVias = new List<Via>();
        var noNetPads = new List<CopperPad>();
        long unionSumMs = 0;

        for (int i = 0; i < netList.Count; i++)
        {
            var net = netList[i];
            var result = results[i];
            bool isNoNet = net.Name == Ipc2581Net.NoNet;
            centerlines.AddRange(result.Centerlines);
            unionSumMs += result.UnionMs;

            var ownIslands = new List<CopperIsland>();
            foreach (var (layerIndex, polygons) in result.LayerCopper)
            {
                var layer = conductors[layerIndex];
                foreach (var polygon in polygons)
                {
                    var island = new CopperIsland(islands.Count, layer.CopperOrder!.Value, layer.Name, polygon);
                    islands.Add(island);
                    ownIslands.Add(island);
                    if (isNoNet) noNetIslands.Add(island);
                }
            }

            pads.AddRange(result.Pads);
            if (isNoNet) noNetPads.AddRange(result.Pads);

            var bridges = new List<ViaBridge>();
            for (int h = 0; h < net.Holes.Count; h++)
            {
                var hole = net.Holes[h];
                var via = new Via(hole.Position, hole.Diameter, hole.Plated);
                vias.Add(via);
                var severed = SeveredOrders(backdrills, hole, ref severedVias);
                if (isNoNet)
                {
                    // No-Net vias stitch geometrically (NetExtractor) — a fully severed
                    // barrel must not stitch anything, so it degrades to unplated.
                    noNetVias.Add(severed is null ? via : new Via(hole.Position, hole.Diameter, false));
                    continue;
                }
                var bridge = BuildBridge(hole, via, orderByName, conductors.Count,
                    result.ResolvedPadOrders[h], severed);
                if (bridge is not null) bridges.Add(bridge);
            }

            if (!isNoNet && ownIslands.Count > 0)
                netParts.Add((net.Name, ownIslands, bridges));
            else if (!isNoNet && (net.Traces.Count > 0 || net.Fills.Count > 0 || net.Pads.Count > 0))
                warnings.Add($"IPC-2581: net '{net.Name}' produced no copper islands " +
                             "(all its features were skipped or degenerate).");
        }

        // Routed slots: every slot's outline is a board cutout; a PLATED slot bridges
        // the copper it touches through a synthesized chain of plated barrels.
        var outline = ApplySlotCutouts(source, notes);
        SynthesizeSlotBarrels(source, orderByName, islands, netParts, vias, noNetVias,
            warnings, notes);

        // "No Net" copper has no declared connectivity — group it geometrically so each
        // disconnected blob is its own selectable net, exactly like the Gerber path.
        var noNetGroups = NetExtractor.Extract(noNetIslands, noNetVias, noNetPads);

        var nets = netParts
            .Select(p => (p.Islands, p.Bridges, Name: (string?)p.Name))
            .Concat(noNetGroups.Select(g =>
                ((List<CopperIsland>)g.Islands.ToList(), g.StitchingVias.ToList(), (string?)null)))
            .OrderByDescending(n => n.Item1.Sum(i => i.Area))
            .Select((n, id) => new CopperNet(id + 1, n.Item1)
            {
                StitchingVias = n.Item2,
                Name = n.Item3
            })
            .ToList();

        var boardLayers = conductors.Select(l => new BoardLayer(
            l.Name,
            l.Function.Equals("PLANE", StringComparison.OrdinalIgnoreCase)
                ? GerberLayerType.CopperPlane : GerberLayerType.CopperSignal,
            l.CopperOrder!.Value,
            l.Side.Equals("TOP", StringComparison.OrdinalIgnoreCase))).ToList();

        if (severedVias > 0)
            notes.Add($"IPC-2581: {severedVias} via(s) severed by {backdrills.Count} backdrill " +
                      "hole(s) — stub removal disconnects the drilled-out layers.");

        // Summary and timing are informational, not skipped-content warnings — they must
        // never stop a conforming file from importing with zero warnings.
        notes.Add($"IPC-2581: {nets.Count} nets ({netParts.Count} named) from {islands.Count} islands " +
                  $"on {conductors.Count} copper layers ({vias.Count(v => v.Plated)} plated holes, " +
                  $"{pads.Count} pads).");
        notes.Add($"Board-build timing: copper stroke+union {netLoopTimer.ElapsedMilliseconds} ms wall " +
                  $"({unionSumMs} ms summed across {netList.Count} parallel nets × {conductors.Count} layers), " +
                  $"total {totalTimer.ElapsedMilliseconds} ms (excludes XML parse).");

        return new PcbBoard
        {
            Outline = outline,
            Islands = islands,
            Pads = pads,
            Vias = vias,
            Nets = nets,
            Layers = boardLayers,
            Warnings = warnings,
            Notes = notes,
            Stackup = BuildStackup(source, notes),
            TraceCenterlines = centerlines
        };
    }

    /// <summary>
    /// Straight centerline segments from the net's Line/Arc draws — the geometry the PEEC
    /// impedance estimator needs, captured before <see cref="UnionLayerCopper"/> strokes it
    /// into polygons and destroys it. Arc tessellation produces chords shorter than half
    /// the trace width, which the chain builder would drop as stubs; chords are accumulated
    /// until the running length clears that tolerance, and a sub-tolerance tail is folded
    /// into the previous chord so the path's endpoints (where it meets pads) are preserved.
    /// </summary>
    private static void CollectCenterlines(Ipc2581Net net, Dictionary<string, int> orderByName,
        List<TraceCenterline> centerlines)
    {
        foreach (var trace in net.Traces)
        {
            if (!orderByName.TryGetValue(trace.LayerRef, out int order)) continue;
            var path = trace.Path;
            if (path.Count < 2) continue;

            double tolerance = trace.Width / 2;
            var anchor = path[0];
            int firstEmitted = centerlines.Count;
            for (int i = 1; i < path.Count; i++)
            {
                if ((path[i] - anchor).Length <= tolerance) continue;
                centerlines.Add(new TraceCenterline(order, anchor, path[i], trace.Width));
                anchor = path[i];
            }

            var last = path[^1];
            if ((last - anchor).Length <= 0) continue;
            if (centerlines.Count > firstEmitted)
                centerlines[^1] = centerlines[^1] with { End = last };
            else
                // The whole draw is shorter than the stub tolerance — keep the single
                // segment; the chain builder decides its fate exactly as on the Gerber path.
                centerlines.Add(new TraceCenterline(order, anchor, last, trace.Width));
        }
    }

    /// <summary>Unions one net's traces, fills, and pad flashes on one layer into islands.</summary>
    private IReadOnlyList<Polygon2> UnionLayerCopper(Ipc2581Net net, string layerName)
    {
        // Traces are stroked ONE PER TRACE on purpose: grouping same-width paths into
        // one InflatePaths call was tried and reverted — the offset engine pre-unions
        // overlapping capsules with different boundary vertices than the boolean union
        // below produces (same region, different vertex lists), breaking bitwise
        // reproducibility against the established pipeline.
        var rings = new List<IReadOnlyList<Point2>>();
        foreach (var trace in net.Traces)
        {
            if (trace.LayerRef != layerName) continue;
            rings.AddRange(_ops.StrokeOpenPath(trace.Path, trace.Width / 2));
        }
        foreach (var fill in net.Fills)
            if (fill.LayerRef == layerName)
                rings.AddRange(Polygon2.OrientedRings(fill.Shape));
        foreach (var pad in net.Pads)
            if (pad.LayerRef == layerName)
                rings.AddRange(Polygon2.OrientedRings(pad.Shape));
        return rings.Count == 0 ? Array.Empty<Polygon2>() : _ops.Union(rings);
    }

    /// <summary>Pad-sized flashes on conductor layers → selectable electrode pads,
    /// carrying the PinRef identity (refdes.pin + resolved part name) when the file
    /// declared one.</summary>
    private static List<CopperPad> BuildPads(Ipc2581Net net, Dictionary<string, int> orderByName,
        Dictionary<string, string?> partByRefDes)
    {
        var result = new List<CopperPad>();
        foreach (var flash in net.Pads)
        {
            if (!orderByName.TryGetValue(flash.LayerRef, out int order)) continue;
            double minX = double.MaxValue, minY = double.MaxValue,
                   maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in flash.Shape.Outer)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            double size = Math.Max(maxX - minX, maxY - minY);
            if (size <= MaxPadSize)
                result.Add(new CopperPad(order, flash.Center, flash.Shape, size)
                {
                    ComponentRef = flash.ComponentRef,
                    Pin = flash.Pin,
                    PartName = flash.ComponentRef is { } refDes
                        && partByRefDes.TryGetValue(refDes, out var part) ? part : null,
                });
        }
        return result;
    }

    /// <summary>
    /// The copper layers a plated hole electrically joins: the padstack's declared pad
    /// layers (or the coincident-copper refinement of a fallback list), restricted to
    /// the drill span, minus any layers a backdrill severed.
    /// </summary>
    private static ViaBridge? BuildBridge(Ipc2581Hole hole, Via via,
        Dictionary<string, int> orderByName, int conductorCount,
        IReadOnlyList<int>? resolvedOrders, HashSet<int>? severed)
    {
        if (!via.Plated) return null;

        int spanFrom = orderByName.TryGetValue(hole.SpanFrom, out int f) ? f : 1;
        int spanTo = orderByName.TryGetValue(hole.SpanTo, out int t) ? t : conductorCount;
        if (spanFrom > spanTo) (spanFrom, spanTo) = (spanTo, spanFrom);

        var declared = resolvedOrders ?? hole.PadLayers
            .Where(orderByName.ContainsKey)
            .Select(name => orderByName[name])
            .ToList();
        var layers = declared
            .Where(o => o >= spanFrom && o <= spanTo)
            .Where(o => severed is null || !severed.Contains(o))
            .Distinct()
            .OrderBy(o => o)
            .ToList();
        return layers.Count >= 2 ? new ViaBridge(via, layers) : null;
    }

    /// <summary>One prepared backdrill: position, radius, and the conductor orders it
    /// severs (its span minus the spec's MUST_NOT_CUT layers).</summary>
    private sealed record PreparedBackdrill(Point2 Position, double Radius, HashSet<int> Severed);

    private static List<PreparedBackdrill> PrepareBackdrills(Ipc2581Board source,
        Dictionary<string, int> orderByName, int conductorCount, List<string> warnings)
    {
        var prepared = new List<PreparedBackdrill>();
        foreach (var bd in source.Backdrills)
        {
            int lo = orderByName.TryGetValue(bd.SpanFrom, out int f) ? f : 1;
            int hi = orderByName.TryGetValue(bd.SpanTo, out int t) ? t : conductorCount;
            if (lo > hi) (lo, hi) = (hi, lo);
            var severed = new HashSet<int>(Enumerable.Range(lo, hi - lo + 1));
            foreach (var name in bd.Spec?.MustNotCutLayers ?? Array.Empty<string>())
                if (orderByName.TryGetValue(name, out int keep) && severed.Remove(keep))
                    // A protected layer INSIDE the drill span is a self-contradictory
                    // declaration — honor the protection and say so.
                    warnings.Add($"IPC-2581: backdrill spec '{bd.Spec!.Name}' protects layer " +
                                 $"'{name}' inside its own drill span; the layer is kept connected.");
            if (severed.Count > 0)
                prepared.Add(new PreparedBackdrill(bd.Position, bd.Diameter / 2, severed));
        }
        return prepared;
    }

    /// <summary>The conductor orders every backdrill coincident with this hole severs
    /// (null when untouched). Coincidence = the barrel center inside the backdrill bore.</summary>
    private static HashSet<int>? SeveredOrders(List<PreparedBackdrill> backdrills,
        Ipc2581Hole hole, ref int severedVias)
    {
        HashSet<int>? severed = null;
        foreach (var bd in backdrills)
        {
            if ((bd.Position - hole.Position).Length > bd.Radius) continue;
            severed ??= new HashSet<int>();
            severed.UnionWith(bd.Severed);
        }
        if (severed is not null && hole.Plated) severedVias++;
        return severed;
    }

    /// <summary>
    /// Refines a fallback pad-layer list (span endpoints / all conductors — geometric
    /// guesses, not declarations) by coincident same-net copper: the barrel joins
    /// exactly the span layers where a pad or fill of ITS OWN net contains the hole
    /// center. Measured on a real 12-layer Cadence board: a through via with pads on
    /// TOP/S05/BOTTOM previously bridged only the span endpoints {TOP, BOTTOM} —
    /// silently missing the inner connection. Runs inside the per-net parallel body
    /// (net-local data only, so the result stays bitwise at any DOP); a refinement
    /// finding fewer than two layers keeps the declared fallback (a synthetic fixture
    /// with no drawn copper must keep bridging its declared span).
    /// </summary>
    private static IReadOnlyList<int>?[] ResolvePadOrdersByCoincidence(Ipc2581Net net,
        Dictionary<string, int> orderByName, Dictionary<int, string> nameByOrder, int conductorCount)
    {
        var resolved = new IReadOnlyList<int>?[net.Holes.Count];
        if (net.Holes.Count == 0) return resolved;
        bool anyFallback = net.Holes.Any(h => h.Plated
            && h.Source is Ipc2581PadLayersSource.SpanEndpoints or Ipc2581PadLayersSource.AllConductors);
        if (!anyFallback) return resolved;

        // Per-layer candidate shapes with bounding boxes (pads AND fills — a via lands
        // on a plane through its pour; an anti-pad hole in the pour correctly excludes).
        var shapesByLayer = new Dictionary<string, List<(Polygon2 Shape, double MinX, double MinY, double MaxX, double MaxY)>>();
        void AddShape(string layerRef, Polygon2 shape)
        {
            if (!shapesByLayer.TryGetValue(layerRef, out var list))
                shapesByLayer[layerRef] = list = new();
            double minX = double.MaxValue, minY = double.MaxValue,
                   maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in shape.Outer)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            list.Add((shape, minX, minY, maxX, maxY));
        }
        foreach (var pad in net.Pads) AddShape(pad.LayerRef, pad.Shape);
        foreach (var fill in net.Fills) AddShape(fill.LayerRef, fill.Shape);

        for (int h = 0; h < net.Holes.Count; h++)
        {
            var hole = net.Holes[h];
            if (!hole.Plated) continue;
            if (hole.Source is not (Ipc2581PadLayersSource.SpanEndpoints
                or Ipc2581PadLayersSource.AllConductors)) continue;

            int lo = orderByName.TryGetValue(hole.SpanFrom, out int f) ? f : 1;
            int hi = orderByName.TryGetValue(hole.SpanTo, out int t) ? t : conductorCount;
            if (lo > hi) (lo, hi) = (hi, lo);

            var found = new List<int>();
            for (int order = lo; order <= hi; order++)
            {
                if (!nameByOrder.TryGetValue(order, out var layerName)) continue;
                if (!shapesByLayer.TryGetValue(layerName, out var candidates)) continue;
                var c = hole.Position;
                foreach (var (shape, minX, minY, maxX, maxY) in candidates)
                {
                    if (c.X < minX || c.X > maxX || c.Y < minY || c.Y > maxY) continue;
                    if (!PlanarMesher.ContainsPoint(new[] { shape }, c)) continue;
                    found.Add(order);
                    break;
                }
            }
            if (found.Count >= 2) resolved[h] = found;
        }
        return resolved;
    }

    /// <summary>
    /// The physical stackup from the file: per-copper-layer thickness and the dielectric
    /// sum between each consecutive copper pair. Zero thicknesses (some exporters omit
    /// them) fall back to the engine defaults with a NOTE — the data is genuinely absent
    /// from the file, so this is defaulting, not skipping declared content.
    /// </summary>
    private static PcbStackupSettings? BuildStackup(Ipc2581Board source, List<string> notes)
    {
        var conductors = source.ConductorLayers;
        if (conductors.Count == 0) return null;

        var defaults = new PcbStackupSettings();
        int gapCount = Math.Max(1, conductors.Count - 1);
        double defaultGap = defaults.BoardThickness / gapCount;

        var copper = new List<double>();
        foreach (var layer in conductors)
        {
            if (layer.Thickness > 0) copper.Add(layer.Thickness);
            else
            {
                notes.Add($"IPC-2581: conductor '{layer.Name}' has no stackup thickness; " +
                          $"defaulting to {defaults.CopperThickness * 1e6:g3} µm.");
                copper.Add(defaults.CopperThickness);
            }
        }

        // Dielectric between copper i and i+1 = sum of dielectric-layer thicknesses
        // sitting between them in the physical stack (a gap can hold core + prepreg).
        var gaps = new List<double>();
        var ordered = source.Layers;
        for (int c = 0; c < conductors.Count - 1; c++)
        {
            int i0 = IndexOf(ordered, conductors[c].Name);
            int i1 = IndexOf(ordered, conductors[c + 1].Name);
            double gap = 0;
            for (int i = Math.Min(i0, i1) + 1; i < Math.Max(i0, i1); i++)
                if (Ipc2581Layer.IsDielectricFunction(ordered[i].Function))
                    gap += ordered[i].Thickness;
            if (gap <= 0)
            {
                notes.Add($"IPC-2581: no dielectric thickness between '{conductors[c].Name}' and " +
                          $"'{conductors[c + 1].Name}'; defaulting to {defaultGap * 1e3:g3} mm.");
                gap = defaultGap;
            }
            gaps.Add(gap);
        }

        return new PcbStackupSettings
        {
            CopperThickness = copper[0],
            BoardThickness = gaps.Count > 0 ? gaps.Sum() : defaults.BoardThickness,
            CopperLayerThicknesses = copper,
            DielectricGapThicknesses = gaps
        };
    }

    private static int IndexOf(IReadOnlyList<Ipc2581Layer> layers, string name)
    {
        for (int i = 0; i < layers.Count; i++)
            if (layers[i].Name == name) return i;
        return -1;
    }

    /// <summary>Every slot's outline becomes a HOLE in the board profile polygon that
    /// contains it (the routed bore is gone from the meshed domain, plated or not).</summary>
    private static IReadOnlyList<Polygon2> ApplySlotCutouts(Ipc2581Board source, List<string> notes)
    {
        if (source.Slots.Count == 0) return source.Profile;

        var regions = source.Profile
            .Select(p => (Outer: p.Outer, Holes: p.Holes.ToList()))
            .ToList();
        foreach (var slot in source.Slots)
        {
            var probe = Centroid(slot.Outline);
            int region = regions.FindIndex(r =>
                PlanarMesher.ContainsPoint(new[] { new Polygon2(r.Outer) }, probe));
            if (region >= 0) regions[region].Holes.Add(slot.Outline);
        }
        notes.Add($"IPC-2581: {source.Slots.Count} routed slot(s) subtracted from the board outline.");
        return regions.Select(r => new Polygon2(r.Outer, r.Holes)).ToList();
    }

    /// <summary>
    /// A PLATED slot bridges the copper layers it touches. The plating is modeled as a
    /// chain of plated barrels along the slot outline's principal axis (diameter = the
    /// perpendicular extent, spacing ≤ half a diameter) so the existing via machinery
    /// carries it — electrically exact for connectivity, and near-exact for resistance
    /// on the oblong outlines slots actually are (a note names the approximation
    /// otherwise). Each barrel joins the ONE net whose islands contain it (a plated slot
    /// touching several nets would be a short — warned, never silently bridged); barrels
    /// on unnamed copper stitch geometrically like any No-Net via.
    /// </summary>
    private static void SynthesizeSlotBarrels(Ipc2581Board source,
        Dictionary<string, int> orderByName, List<CopperIsland> islands,
        List<(string Name, List<CopperIsland> Islands, List<ViaBridge> Bridges)> netParts,
        List<Via> vias, List<Via> noNetVias, List<string> warnings, List<string> notes)
    {
        if (!source.Slots.Any(s => s.Plated)) return;

        // island index → owning named-net part (absent = No-Net copper).
        var partByIsland = new Dictionary<int, int>();
        for (int p = 0; p < netParts.Count; p++)
            foreach (var island in netParts[p].Islands)
                partByIsland[island.Index] = p;

        foreach (var slot in source.Slots)
        {
            if (!slot.Plated) continue;

            // Principal axis of the outline (2×2 covariance eigenvector), extents.
            var mean = Centroid(slot.Outline);
            double sxx = 0, sxy = 0, syy = 0;
            foreach (var p in slot.Outline)
            {
                var d = p - mean;
                sxx += d.X * d.X; sxy += d.X * d.Y; syy += d.Y * d.Y;
            }
            double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            var axis = new Point2(Math.Cos(angle), Math.Sin(angle));
            var perp = new Point2(-axis.Y, axis.X);
            double tMin = double.MaxValue, tMax = double.MinValue;
            double wMin = double.MaxValue, wMax = double.MinValue;
            foreach (var p in slot.Outline)
            {
                var d = p - mean;
                double t = d.X * axis.X + d.Y * axis.Y;
                double w = d.X * perp.X + d.Y * perp.Y;
                tMin = Math.Min(tMin, t); tMax = Math.Max(tMax, t);
                wMin = Math.Min(wMin, w); wMax = Math.Max(wMax, w);
            }
            double width = wMax - wMin;
            double length = tMax - tMin;
            if (width <= 0) continue;

            // A non-oblong outline (area far from the capsule of these extents) is
            // still bridged, but the barrel model is stated as an approximation.
            double capsule = (length - width) * width + Math.PI * width * width / 4;
            double area = new Polygon2(slot.Outline).Area();
            if (Math.Abs(area - capsule) > 0.15 * area)
                notes.Add($"IPC-2581: plated slot '{slot.Name}' has a non-oblong outline; " +
                          "its plating is approximated by a barrel chain along the principal axis.");

            var centers = new List<Point2>();
            if (length <= width)
                centers.Add(mean);
            else
            {
                int steps = (int)Math.Ceiling((length - width) / (width / 2));
                for (int s = 0; s <= steps; s++)
                {
                    double t = tMin + width / 2 + (length - width) * s / steps;
                    centers.Add(mean + new Point2(axis.X * t, axis.Y * t));
                }
            }

            // The one named net whose copper the barrels land in (per touched layer).
            var touchedParts = new HashSet<int>();
            bool touchesNoNet = false;
            var barrelLayers = new List<(Point2 Center, List<int> Orders)>();
            foreach (var center in centers)
            {
                var orders = new List<int>();
                foreach (var island in islands)
                {
                    var (minX, minY, maxX, maxY) = island.Bounds();
                    if (center.X < minX || center.X > maxX || center.Y < minY || center.Y > maxY)
                        continue;
                    if (!PlanarMesher.ContainsPoint(new[] { island.Shape }, center)) continue;
                    if (!orders.Contains(island.LayerOrder)) orders.Add(island.LayerOrder);
                    if (partByIsland.TryGetValue(island.Index, out int part)) touchedParts.Add(part);
                    else touchesNoNet = true;
                }
                orders.Sort();
                barrelLayers.Add((center, orders));
            }

            if (touchedParts.Count > 1)
            {
                warnings.Add($"IPC-2581: plated slot '{slot.Name}' touches copper of " +
                             $"{touchedParts.Count} different nets; not bridged (a plated " +
                             "short between nets is a design conflict).");
                continue;
            }

            foreach (var (center, orders) in barrelLayers)
            {
                var via = new Via(center, width, Plated: true);
                vias.Add(via);
                if (touchedParts.Count == 1 && !touchesNoNet)
                {
                    if (orders.Count >= 2)
                        netParts[touchedParts.First()].Bridges.Add(new ViaBridge(via, orders));
                }
                else
                    noNetVias.Add(via);                          // geometric stitching
            }
        }
    }

    private static Point2 Centroid(IReadOnlyList<Point2> ring)
    {
        double x = 0, y = 0;
        foreach (var p in ring) { x += p.X; y += p.Y; }
        return new Point2(x / ring.Count, y / ring.Count);
    }
}
