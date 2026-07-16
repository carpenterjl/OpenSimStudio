using OpenSim.Core.Model;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
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
    /// LayerCopper holds (conductor index, that layer's unioned polygons), non-empty only.</summary>
    private sealed record NetResult(
        List<(int LayerIndex, IReadOnlyList<Polygon2> Polygons)> LayerCopper,
        List<CopperPad> Pads,
        List<TraceCenterline> Centerlines,
        long UnionMs);

    public PcbBoard Build(Ipc2581Board source)
    {
        // Aggregate stage timings surface in the log panel so a slow import names its
        // own bottleneck (per-net lines would be spam on a 100-net board).
        var totalTimer = System.Diagnostics.Stopwatch.StartNew();

        var warnings = new List<string>(source.Warnings);
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
                            netCenterlines, sw.ElapsedMilliseconds);
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
            foreach (var hole in net.Holes)
            {
                var via = new Via(hole.Position, hole.Diameter, hole.Plated);
                vias.Add(via);
                if (isNoNet) { noNetVias.Add(via); continue; }
                var bridge = BuildBridge(hole, via, orderByName, conductors.Count);
                if (bridge is not null) bridges.Add(bridge);
            }

            if (!isNoNet && ownIslands.Count > 0)
                netParts.Add((net.Name, ownIslands, bridges));
            else if (!isNoNet && (net.Traces.Count > 0 || net.Fills.Count > 0 || net.Pads.Count > 0))
                warnings.Add($"IPC-2581: net '{net.Name}' produced no copper islands " +
                             "(all its features were skipped or degenerate).");
        }

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

        warnings.Add($"IPC-2581: {nets.Count} nets ({netParts.Count} named) from {islands.Count} islands " +
                     $"on {conductors.Count} copper layers ({vias.Count(v => v.Plated)} plated holes, " +
                     $"{pads.Count} pads).");
        warnings.Add($"Board-build timing: copper stroke+union {netLoopTimer.ElapsedMilliseconds} ms wall " +
                     $"({unionSumMs} ms summed across {netList.Count} parallel nets × {conductors.Count} layers), " +
                     $"total {totalTimer.ElapsedMilliseconds} ms (excludes XML parse).");

        return new PcbBoard
        {
            Outline = source.Profile,
            Islands = islands,
            Pads = pads,
            Vias = vias,
            Nets = nets,
            Layers = boardLayers,
            Warnings = warnings,
            Stackup = BuildStackup(source, warnings),
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
    /// layers, restricted to the drill span. This is exact (file-declared) — no geometric
    /// annular-ring inference needed.
    /// </summary>
    private static ViaBridge? BuildBridge(Ipc2581Hole hole, Via via,
        Dictionary<string, int> orderByName, int conductorCount)
    {
        if (!via.Plated) return null;

        int spanFrom = orderByName.TryGetValue(hole.SpanFrom, out int f) ? f : 1;
        int spanTo = orderByName.TryGetValue(hole.SpanTo, out int t) ? t : conductorCount;
        if (spanFrom > spanTo) (spanFrom, spanTo) = (spanTo, spanFrom);

        var layers = hole.PadLayers
            .Where(orderByName.ContainsKey)
            .Select(name => orderByName[name])
            .Where(o => o >= spanFrom && o <= spanTo)
            .Distinct()
            .OrderBy(o => o)
            .ToList();
        return layers.Count >= 2 ? new ViaBridge(via, layers) : null;
    }

    /// <summary>
    /// The physical stackup from the file: per-copper-layer thickness and the dielectric
    /// sum between each consecutive copper pair. Zero thicknesses (some exporters omit
    /// them) fall back to the engine defaults with a warning.
    /// </summary>
    private static PcbStackupSettings? BuildStackup(Ipc2581Board source, List<string> warnings)
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
                warnings.Add($"IPC-2581: conductor '{layer.Name}' has no stackup thickness; " +
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
                warnings.Add($"IPC-2581: no dielectric thickness between '{conductors[c].Name}' and " +
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
}
