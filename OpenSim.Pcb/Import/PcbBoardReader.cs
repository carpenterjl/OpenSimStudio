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
        IReadOnlyList<CopperPad> Pads, List<TraceCenterline> Centerlines, bool Negative,
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
        var (copperLayers, copperOrders) = OrderCopperLayers(layers, warnings);
        // The board's layer list reports the order each copper layer was GIVEN, so the
        // stackup panel, the plane picker and the islands all name a layer the same way.
        for (int k = 0; k < layers.Count; k++)
        {
            int at = copperLayers.IndexOf(layers[k]);
            if (at >= 0) layers[k] = layers[k] with { CopperOrder = copperOrders[at] };
        }
        copperLayers = copperLayers.Select((l, k) => l with { CopperOrder = copperOrders[k] }).ToList();
        var results = new LayerResult[copperLayers.Count];
        stageTimer.Restart();
        try
        {
            Parallel.For(0, copperLayers.Count,
                new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism ?? -1 },
                i =>
                {
                    var layer = copperLayers[i];
                    int layerOrder = layer.CopperOrder;
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var doc = new GerberParser().Parse(byName[layer.FileName]);
                        long parseMs = sw.ElapsedMilliseconds;
                        sw.Restart();
                        var image = new LayerImageBuilder(_ops).Build(doc);
                        if (doc.IsNegative)
                            image = InvertWithinOutline(image, outlineTask.Result);
                        long imageMs = sw.ElapsedMilliseconds;
                        sw.Restart();
                        var layerPads = PadExtractor.Extract(doc, layerOrder);
                        // Centerlines must be captured HERE, while the parsed document
                        // exists — the polygon union destroys them, and the document is
                        // not retained.
                        var lines = Inductance.TraceSegmenter.Centerlines(doc, layerOrder).ToList();
                        long extractMs = sw.ElapsedMilliseconds;
                        results[i] = new LayerResult(layerOrder, image, layerPads, lines, doc.IsNegative,
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
            if (r.Negative)
                warnings.Add($"Layer {layer.FileName}: negative-polarity plane — copper is the board outline " +
                             "minus the drawn clearances; a via joins it where its centre lies in that copper.");
        }

        // Vias from drill layers. Plated or not, and the layer span of a blind or buried
        // drill, come from the file's FileFunction attribute; only a file without one is
        // judged by its name.
        // Routed slots are NOT vias: a via is a circular plated bore, and plated-slot
        // layer stitching is not modeled — say so rather than misrepresent connectivity.
        stageTimer.Restart();
        var vias = new List<Via>();
        foreach (var drill in layers.Where(l => l.Type == GerberLayerType.Drill))
        {
            var (plated, fromLayer, toLayer, fromAttribute) =
                GerberLayerClassifier.DrillFunction(drill.FileName, byName[drill.FileName]);
            if (!fromAttribute)
                warnings.Add($"Layer {drill.FileName}: no FileFunction attribute — holes taken as " +
                             $"{(plated ? "plated" : "non-plated")} from the file name, through the whole board.");
            else if (fromLayer > 0)
                warnings.Add($"Layer {drill.FileName}: {(plated ? "plated" : "non-plated")} holes " +
                             $"between copper layers {fromLayer} and {toLayer} only.");
            var features = DrillExtractor.Extract(byName[drill.FileName]);
            foreach (var h in features.Holes)
                vias.Add(new Via(h.Center, h.Diameter, plated, fromLayer, toLayer));
            if (features.Warnings is not null)
                foreach (string w in features.Warnings)
                    warnings.Add($"Layer {drill.FileName}: {w}");
            if (features.Slots.Count > 0)
                warnings.Add($"Layer {drill.FileName}: {features.Slots.Count} slot(s) parsed; slots are " +
                             "subtracted from the board domain but do not stitch copper layers.");
        }

        long drillsMs = stageTimer.ElapsedMilliseconds;

        stageTimer.Restart();
        var nets = NetExtractor.Extract(islands, vias, pads,
            results.Where(r => r.Negative).Select(r => r.LayerOrder).Distinct().ToList());
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

    /// <summary>
    /// Copper layers top to bottom with the order each is given. A set whose every copper
    /// file declares its position (X2 <c>Copper,L&lt;n&gt;</c>) keeps the declared numbers.
    /// A set that needed the filename fallback has no numbers to keep — the fallback only
    /// knows "top", "bottom" (<see cref="GerberLayerClassifier.BottomByName"/>) and
    /// "somewhere inside" — so it is numbered 1..N in stack order. Without this a
    /// top/bottom pair came out as layers 1 and 99, and the stackup z model filled the
    /// range between them with 97 layers that do not exist.
    /// </summary>
    private static (List<BoardLayer> Layers, List<int> Orders) OrderCopperLayers(
        IReadOnlyList<BoardLayer> layers, List<string> warnings)
    {
        var copper = layers
            .Where(l => l.Type is GerberLayerType.CopperSignal or GerberLayerType.CopperPlane)
            .ToList();
        bool byName = copper.Any(l => l.CopperOrder is 0 or GerberLayerClassifier.BottomByName);
        if (!byName)
        {
            var declared = copper.OrderBy(l => l.CopperOrder).ToList();
            return (declared, declared.Select(l => l.CopperOrder).ToList());
        }

        // Stack order: declared/top first, layers of unknown position next (file order),
        // a bottom layer last.
        static int Rank(BoardLayer l) =>
            l.CopperOrder == GerberLayerClassifier.BottomByName ? 2 : l.CopperOrder == 0 ? 1 : 0;
        var ordered = copper
            .Select((l, index) => (Layer: l, Index: index))
            .OrderBy(x => Rank(x.Layer))
            .ThenBy(x => Rank(x.Layer) == 0 ? x.Layer.CopperOrder : 0)
            .ThenBy(x => x.Index)
            .Select(x => x.Layer)
            .ToList();
        var orders = Enumerable.Range(1, ordered.Count).ToList();
        warnings.Add("Copper layer order taken from file names (no %TF.FileFunction on every copper " +
                     "file): " + string.Join(", ", ordered.Select((l, k) => $"L{orders[k]} = {l.FileName}")) + ".");
        var unplaced = ordered.Where(l => l.CopperOrder == 0).Select(l => l.FileName).ToList();
        if (unplaced.Count > 0)
            warnings.Add("The position of " + string.Join(", ", unplaced) + " in the stack is not stated " +
                         "by the file name; placed between the top and bottom layers in file order — " +
                         "check the layer order before trusting any multi-layer result.");
        return (ordered, orders);
    }

    /// <summary>
    /// The copper of a negative-polarity file: the filled board outline minus what the
    /// file drew. There is no other statement of where the plane ends, so a negative
    /// layer without an outline is refused rather than imaged as its clearances.
    /// </summary>
    private LayerImage InvertWithinOutline(LayerImage drawn, IReadOnlyList<Polygon2> outline)
    {
        if (outline.Count == 0)
            throw new InvalidOperationException(
                "the file is a negative-polarity plane (%TF.FilePolarity,Negative), whose copper is the " +
                "board outline minus the drawing, and the set has no usable board outline (Profile) layer.");
        var copper = _ops.Difference(outline, drawn.Polygons.SelectMany(Polygon2.OrientedRings)).ToList();
        return new LayerImage
        {
            Polygons = LayerImageBuilder.CanonicalOrder(copper),
            Warnings = drawn.Warnings
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
