using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Polygons;

namespace OpenSim.Pcb.Import;

/// <summary>
/// Reads a whole fabrication archive into a <see cref="PcbBoard"/>: board outline, every
/// copper island on every copper layer, drilled vias, and the extracted connected nets.
/// This is the "import the full board" step the net-selection workflow needs before the
/// user chooses which net to simulate.
/// </summary>
public sealed class PcbBoardReader
{
    private readonly IPolygonOps _ops = new ClipperPolygonOps();

    /// <summary>Caps the layer-level parallelism; null lets the scheduler decide.
    /// Output is bitwise-identical for any value — the tests pin 1 vs unbounded.</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>Everything one copper layer contributes, computed independently per
    /// layer and stitched back in file order so ids and warnings stay deterministic.</summary>
    private sealed record LayerResult(int LayerOrder, LayerImage Image,
        IReadOnlyList<CopperPad> Pads, List<TraceCenterline> Centerlines,
        long ParseMs, long ImageMs, long ExtractMs, int OpCount, int PolarityFlips);

    public PcbBoard Read(string archivePath)
    {
        // Per-stage wall times go into the warnings list (the log panel) so a slow
        // import names its own bottleneck instead of being a black box.
        var totalTimer = System.Diagnostics.Stopwatch.StartNew();
        var stageTimer = new System.Diagnostics.Stopwatch();

        var warnings = new List<string>();
        var files = PcbArchive.Read(archivePath);
        if (files.Count == 0)
            throw new InvalidOperationException("No Gerber or drill files were found in the archive.");
        var byName = files.ToDictionary(f => f.Name, f => f.Text);
        var layers = files.Select(f => GerberLayerClassifier.Classify(f.Name, f.Text)).ToList();

        // Board outline (filled from the usually-stroked profile), overlapped with the
        // copper layers below — it shares nothing with them.
        var profile = layers.FirstOrDefault(l => l.Type == GerberLayerType.Profile);
        var outlineWarnings = new List<string>();
        var outlineTimer = new System.Diagnostics.Stopwatch();
        var outlineTask = Task.Run(() =>
        {
            outlineTimer.Start();
            var result = profile is null
                ? new List<Polygon2>()
                : FillOutline(byName[profile.FileName], outlineWarnings);
            outlineTimer.Stop();
            return result;
        });

        // Copper layers are mutually independent: parse + polygonize in parallel, then
        // assemble strictly in layer order so island ids, pads, centerlines and warning
        // lines come out bitwise-identical to a sequential run.
        var copperLayers = layers
            .Where(l => l.Type is GerberLayerType.CopperSignal or GerberLayerType.CopperPlane)
            .OrderBy(l => l.CopperOrder == 0 ? int.MaxValue : l.CopperOrder)
            .ToList();
        var results = new LayerResult[copperLayers.Count];
        stageTimer.Restart();
        try
        {
            Parallel.For(0, copperLayers.Count,
                new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism ?? -1 },
                i =>
                {
                    var layer = copperLayers[i];
                    int layerOrder = layer.CopperOrder > 0 ? layer.CopperOrder : i + 1;
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var doc = new GerberParser().Parse(byName[layer.FileName]);
                        long parseMs = sw.ElapsedMilliseconds;
                        sw.Restart();
                        var image = new LayerImageBuilder(_ops).Build(doc);
                        long imageMs = sw.ElapsedMilliseconds;
                        sw.Restart();
                        var layerPads = PadExtractor.Extract(doc, layerOrder);
                        // Centerlines must be captured HERE, while the parsed document
                        // exists — the polygon union destroys them, and the document is
                        // not retained.
                        var lines = Inductance.TraceSegmenter.Centerlines(doc, layerOrder).ToList();
                        long extractMs = sw.ElapsedMilliseconds;
                        results[i] = new LayerResult(layerOrder, image, layerPads, lines,
                            parseMs, imageMs, extractMs, doc.Ops.Count, CountPolarityFlips(doc));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Layer {layer.FileName}: {ex.Message}", ex);
                    }
                });
        }
        catch (AggregateException ae)
        {
            // Fail loudly with the (layer-named) cause, not a scheduler wrapper.
            throw ae.InnerExceptions[0];
        }
        long layersWallMs = stageTimer.ElapsedMilliseconds;

        var outline = outlineTask.Result;
        warnings.AddRange(outlineWarnings);
        if (profile is null)
            warnings.Add("No board outline (Profile) layer found; nets extracted without a board boundary.");
        long outlineMs = outlineTimer.ElapsedMilliseconds;

        var islands = new List<CopperIsland>();
        var pads = new List<CopperPad>();
        var centerlines = new List<TraceCenterline>();
        long layersSumMs = 0;
        for (int i = 0; i < results.Length; i++)
        {
            var r = results[i];
            var layer = copperLayers[i];
            foreach (var polygon in r.Image.Polygons)
                islands.Add(new CopperIsland(islands.Count, r.LayerOrder, layer.FileName, polygon));
            pads.AddRange(r.Pads);
            centerlines.AddRange(r.Centerlines);
            layersSumMs += r.ParseMs + r.ImageMs + r.ExtractMs;
            warnings.Add($"Layer {layer.FileName}: {r.Image.Polygons.Count} copper islands, {r.Pads.Count} pads " +
                         $"(parse {r.ParseMs} ms, copper image {r.ImageMs} ms over {r.OpCount} ops / " +
                         $"{r.PolarityFlips} polarity flips, pads+centerlines {r.ExtractMs} ms).");
        }

        // Vias from drill layers (plated = PTH, non-plated = NPTH by FileFunction).
        // Routed slots are NOT vias: a via is a circular plated bore, and plated-slot
        // layer stitching is not modeled — say so rather than misrepresent connectivity.
        stageTimer.Restart();
        var vias = new List<Via>();
        foreach (var drill in layers.Where(l => l.Type == GerberLayerType.Drill))
        {
            bool plated = !drill.FileName.Contains("NPTH", StringComparison.OrdinalIgnoreCase)
                          && !drill.FileName.Contains("NonPlated", StringComparison.OrdinalIgnoreCase);
            var features = DrillExtractor.Extract(byName[drill.FileName]);
            foreach (var h in features.Holes)
                vias.Add(new Via(h.Center, h.Diameter, plated));
            if (features.Slots.Count > 0)
                warnings.Add($"Layer {drill.FileName}: {features.Slots.Count} slot(s) parsed; slots are " +
                             "subtracted from the board domain but do not stitch copper layers.");
        }

        long drillsMs = stageTimer.ElapsedMilliseconds;

        stageTimer.Restart();
        var nets = NetExtractor.Extract(islands, vias, pads);
        long netsMs = stageTimer.ElapsedMilliseconds;
        warnings.Add($"Extracted {nets.Count} copper nets from {islands.Count} islands " +
                     $"({vias.Count(v => v.Plated)} plated vias, {pads.Count} pads).");
        warnings.Add($"Import timing: outline {outlineMs} ms, copper layers {layersWallMs} ms wall " +
                     $"({layersSumMs} ms summed over {results.Length} parallel layers), " +
                     $"drills {drillsMs} ms, net extraction {netsMs} ms, " +
                     $"total {totalTimer.ElapsedMilliseconds} ms.");

        return new PcbBoard
        {
            Outline = outline,
            Islands = islands,
            Pads = pads,
            Vias = vias,
            Nets = nets,
            Layers = layers,
            Warnings = warnings,
            TraceCenterlines = centerlines
        };
    }

    /// <summary>Dark↔clear transitions in op order — each one costs a boolean pass.</summary>
    private static int CountPolarityFlips(GerberDocument doc)
    {
        int flips = 0;
        var polarity = GerberPolarity.Dark;
        foreach (var op in doc.Ops)
        {
            if (op.Polarity != polarity)
            {
                flips++;
                polarity = op.Polarity;
            }
        }
        return flips;
    }

    private List<Polygon2> FillOutline(string profileText, List<string> warnings)
    {
        var doc = new GerberParser().Parse(profileText);
        var image = new LayerImageBuilder(_ops).Build(doc);
        if (image.Polygons.Count == 0)
        {
            warnings.Add("Board outline produced no geometry.");
            return new List<Polygon2>();
        }
        var outerRings = image.Polygons.Select(p => (IReadOnlyList<Point2>)p.Outer).ToList();
        return _ops.Union(outerRings).ToList();
    }
}
