using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Gerber;

namespace OpenSim.Pcb.Import;

/// <summary>A net name stated by the source file at one point of one copper layer.</summary>
public readonly record struct NetLabel(int LayerOrder, Point2 At, string Name);

/// <summary>
/// Names the extracted nets from Gerber X2 <c>%TO.N</c> attributes. Connectivity is still
/// decided by the copper alone (<see cref="NetExtractor"/>); a label only says what the
/// design calls the copper it lies on. Each net takes the name most of its labelled objects
/// carry, and a net whose objects disagree is reported — the copper joins what the netlist
/// keeps apart, or the attributes are wrong.
/// </summary>
public static class NetNamer
{
    /// <summary>One label per attributed object of a positive copper layer: a flash at
    /// its centre, a draw at the middle of its first segment, a region just inside its
    /// first contour edge.</summary>
    public static List<NetLabel> Labels(GerberDocument document, int layerOrder)
    {
        var labels = new List<NetLabel>();
        // A negative file's objects are clearances: the name on one is the net of the
        // hole, not of the plane around it.
        if (document.IsNegative) return labels;
        foreach (var op in document.Ops)
        {
            if (op.Net is not { } name || op.Polarity != GerberPolarity.Dark) continue;
            switch (op)
            {
                case FlashOp flash:
                    labels.Add(new NetLabel(layerOrder, flash.Position, name));
                    break;
                case DrawOp { Path.Count: >= 2 } draw:
                    labels.Add(new NetLabel(layerOrder, (draw.Path[0] + draw.Path[1]) * 0.5, name));
                    break;
                case RegionOp { Contours.Count: > 0 } region when InteriorPoint(region.Contours[0]) is { } inside:
                    labels.Add(new NetLabel(layerOrder, inside, name));
                    break;
            }
        }
        return labels;
    }

    /// <summary>A point 1 µm inside a counter-clockwise contour, off the middle of its
    /// longest edge (the parser hands every contour over counter-clockwise).</summary>
    private static Point2? InteriorPoint(IReadOnlyList<Point2> contour)
    {
        int best = -1;
        double bestLength = 0;
        for (int i = 0; i < contour.Count; i++)
        {
            double length = (contour[(i + 1) % contour.Count] - contour[i]).Length;
            if (length > bestLength) { bestLength = length; best = i; }
        }
        if (best < 0 || bestLength < 4e-6) return null;
        var a = contour[best];
        var b = contour[(best + 1) % contour.Count];
        var along = (b - a) * (1.0 / bestLength);
        var left = new Point2(-along.Y, along.X);                 // interior of a CCW ring
        return (a + b) * 0.5 + left * 1e-6;
    }

    /// <summary>Returns the nets with <see cref="CopperNet.Name"/> set where the labels
    /// name them (a net that already has a name keeps it), and adds what was found to
    /// <paramref name="notes"/>.</summary>
    public static IReadOnlyList<CopperNet> Apply(IReadOnlyList<CopperNet> nets,
        IReadOnlyList<NetLabel> labels, List<string> notes)
    {
        if (labels.Count == 0) return nets;

        var locator = new IslandLocator(nets);
        var votes = new Dictionary<int, Dictionary<string, int>>();
        int placed = 0;
        foreach (var label in labels)
        {
            int net = locator.NetAt(label.LayerOrder, label.At);
            if (net < 0) continue;
            placed++;
            if (!votes.TryGetValue(net, out var tally)) votes[net] = tally = new Dictionary<string, int>();
            tally[label.Name] = tally.GetValueOrDefault(label.Name) + 1;
        }

        var named = new List<CopperNet>(nets.Count);
        var conflicts = new List<string>();
        var pieces = new Dictionary<string, int>();
        int namedCount = 0;
        for (int i = 0; i < nets.Count; i++)
        {
            var net = nets[i];
            if (net.Name is not null || !votes.TryGetValue(i, out var tally))
            {
                named.Add(net);
                continue;
            }
            // Most objects win; ties go to the name that sorts first, so the result does
            // not depend on dictionary order.
            var ranked = tally.OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            string name = ranked[0].Key;
            named.Add(net with { Name = name });
            namedCount++;
            pieces[name] = pieces.GetValueOrDefault(name) + 1;
            if (ranked.Count > 1)
                conflicts.Add($"Net {net.Id} is one piece of copper but its objects name " +
                              string.Join(", ", ranked.Select(kv => $"'{kv.Key}' ({kv.Value})")) +
                              $"; it is called '{name}'.");
        }

        notes.Add($"Net names from X2 attributes: {namedCount} of {nets.Count} nets named " +
                  $"({placed} of {labels.Count} labelled objects lie on extracted copper).");
        foreach (string conflict in conflicts.Take(5)) notes.Add(conflict);
        if (conflicts.Count > 5)
            notes.Add($"…and {conflicts.Count - 5} more nets whose objects carry more than one net name.");
        if (conflicts.Count > 0)
            notes.Add("Copper carrying two net names is a short, a net tie, or wrong attributes — " +
                      "check those nets before trusting a result on them.");
        var split = pieces.Where(kv => kv.Value > 1).OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (split.Count > 0)
            notes.Add($"{split.Count} net name(s) label more than one separate piece of copper (" +
                      string.Join(", ", split.Take(5).Select(kv => $"'{kv.Key}' ×{kv.Value}")) +
                      (split.Count > 5 ? ", …" : "") + "): the pieces are joined through a component, " +
                      "a negative plane or a hole the import did not stitch, or the net is not fully routed. " +
                      "Each piece is analysed on its own.");
        return named;
    }

    /// <summary>Finds the net whose copper contains a point of a layer: island bounding
    /// boxes bucketed on a uniform grid per layer, then an exact containment test.</summary>
    private sealed class IslandLocator
    {
        private const int Cells = 48;

        private sealed class Layer
        {
            public double MinX = double.MaxValue, MinY = double.MaxValue;
            public double MaxX = double.MinValue, MaxY = double.MinValue;
            public readonly List<Entry> Entries = new();
            public List<int>[]? Grid;
        }

        private sealed class Entry
        {
            public required int Net;
            public required CopperIsland Island;
            public required (double MinX, double MinY, double MaxX, double MaxY) Bounds;
            public PolygonSetIndex? Index;
        }

        private readonly Dictionary<int, Layer> _layers = new();

        public IslandLocator(IReadOnlyList<CopperNet> nets)
        {
            for (int n = 0; n < nets.Count; n++)
                foreach (var island in nets[n].Islands)
                {
                    if (!_layers.TryGetValue(island.LayerOrder, out var layer))
                        _layers[island.LayerOrder] = layer = new Layer();
                    var b = island.Bounds();
                    layer.Entries.Add(new Entry { Net = n, Island = island, Bounds = b });
                    layer.MinX = Math.Min(layer.MinX, b.MinX); layer.MaxX = Math.Max(layer.MaxX, b.MaxX);
                    layer.MinY = Math.Min(layer.MinY, b.MinY); layer.MaxY = Math.Max(layer.MaxY, b.MaxY);
                }
            foreach (var layer in _layers.Values)
            {
                layer.Grid = new List<int>[Cells * Cells];
                for (int e = 0; e < layer.Entries.Count; e++)
                {
                    var b = layer.Entries[e].Bounds;
                    int x0 = Cell(b.MinX, layer.MinX, layer.MaxX), x1 = Cell(b.MaxX, layer.MinX, layer.MaxX);
                    int y0 = Cell(b.MinY, layer.MinY, layer.MaxY), y1 = Cell(b.MaxY, layer.MinY, layer.MaxY);
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                            (layer.Grid[y * Cells + x] ??= new List<int>()).Add(e);
                }
            }
        }

        private static int Cell(double v, double min, double max) =>
            max > min ? Math.Clamp((int)((v - min) / (max - min) * Cells), 0, Cells - 1) : 0;

        public int NetAt(int layerOrder, Point2 p)
        {
            if (!_layers.TryGetValue(layerOrder, out var layer)) return -1;
            if (p.X < layer.MinX || p.X > layer.MaxX || p.Y < layer.MinY || p.Y > layer.MaxY) return -1;
            var bucket = layer.Grid![Cell(p.Y, layer.MinY, layer.MaxY) * Cells + Cell(p.X, layer.MinX, layer.MaxX)];
            if (bucket is null) return -1;
            foreach (int e in bucket)
            {
                var entry = layer.Entries[e];
                var b = entry.Bounds;
                if (p.X < b.MinX || p.X > b.MaxX || p.Y < b.MinY || p.Y > b.MaxY) continue;
                entry.Index ??= new PolygonSetIndex(new[] { entry.Island.Shape });
                if (entry.Index.Contains(p)) return entry.Net;
            }
            return -1;
        }
    }
}
