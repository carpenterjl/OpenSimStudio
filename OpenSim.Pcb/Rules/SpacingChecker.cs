using System.Globalization;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;

namespace OpenSim.Pcb.Rules;

public enum SpacingKind
{
    /// <summary>The straight-line distance between the copper edges of two nets on one layer.</summary>
    Clearance,

    /// <summary>The shortest path along the board surface between two nets on an outer
    /// layer where a board cutout lies between them, so the surface path is longer than the
    /// straight line. Only reported where it differs from the clearance.</summary>
    Creepage,

    /// <summary>Copper of two nets facing each other across a dielectric gap: the spacing
    /// is the gap thickness.</summary>
    LayerToLayer,

    /// <summary>The shortest path along the surface from copper on one outer layer, over the
    /// board edge (or down a cutout's wall), to copper on the other outer layer: the run to
    /// the edge on one side, the board's thickness, and the run from the edge on the other.</summary>
    EdgeCreepage
}

/// <summary>One measured spacing against its requirement.</summary>
/// <param name="Layer">The layer; for <see cref="SpacingKind.LayerToLayer"/> the upper of the two;
/// for <see cref="SpacingKind.EdgeCreepage"/> the first net's layer.</param>
/// <param name="Volts">The working voltage between the two nets the requirement was read at.</param>
/// <param name="At">Where the spacing is smallest (on the first net's copper; for
/// <see cref="SpacingKind.EdgeCreepage"/> the point on the board edge the path crosses).</param>
/// <param name="Column">The table column the requirement came from.</param>
public sealed record SpacingFinding(SpacingKind Kind, string NetA, string NetB, int Layer, double Volts,
    double MeasuredMeters, double RequiredMeters, Point2 At, string Column)
{
    /// <summary>For <see cref="SpacingKind.EdgeCreepage"/>, the second net's layer; 0 otherwise.</summary>
    public int OtherLayer { get; init; }

    public bool Passes => MeasuredMeters >= RequiredMeters * (1 - 1e-9);
    public double MarginMeters => MeasuredMeters - RequiredMeters;

    public string Message
    {
        get
        {
            var ci = CultureInfo.InvariantCulture;
            string what = Kind switch
            {
                SpacingKind.Clearance => $"clearance on L{Layer}",
                SpacingKind.Creepage => $"creepage on L{Layer} around a cutout",
                SpacingKind.EdgeCreepage => $"creepage over the board edge from L{Layer} to L{OtherLayer}",
                _ => $"dielectric between L{Layer} and L{Layer + 1}"
            };
            return $"{(Passes ? "ok  " : "FAIL")} {NetA} ↔ {NetB} at {Volts.ToString("g4", ci)} V: {what} " +
                   $"{(MeasuredMeters * 1e3).ToString("0.###", ci)} mm, required {(RequiredMeters * 1e3).ToString("0.###", ci)} mm " +
                   $"({Column}) at ({(At.X * 1e3).ToString("0.##", ci)}, {(At.Y * 1e3).ToString("0.##", ci)}) mm";
        }
    }
}

/// <summary>Settings of a spacing check. The table and its columns are the user's choice:
/// which column applies (coated or not, internal or external) is a property of the
/// finished board, not of the layout file.</summary>
public sealed record SpacingCheckOptions
{
    public SpacingTable Table { get; init; } = Ipc2221Spacing.Table6_1;

    /// <summary>Column for copper on the outer layers (IPC-2221: B2 uncoated, B4 coated).</summary>
    public string OuterColumn { get; init; } = "B2";

    /// <summary>Column for copper on inner layers and across dielectric gaps (IPC-2221: B1).</summary>
    public string InnerColumn { get; init; } = "B1";

    /// <summary>Column a creepage path around a cutout or over the board edge is held to;
    /// null uses the outer column.</summary>
    public string? CreepageColumn { get; init; }

    /// <summary>
    /// Column for an outer-layer pair whose closest copper edges the solder mask covers on both
    /// sides (IPC-2221: B4, permanent polymer coating), read from the board's mask openings:
    /// a copper edge inside an opening is exposed and the pair stays on <see cref="OuterColumn"/>.
    /// Null, or a board without mask layers, applies <see cref="OuterColumn"/> to every outer pair.
    /// </summary>
    public string? CoatedOuterColumn { get; init; }

    /// <summary>Layer and gap thicknesses. Null is the board file's stackup completed with defaults.</summary>
    public BoardStackup? Stackup { get; init; }

    /// <summary>Pairs farther apart than this many times their requirement are not listed.</summary>
    public double ReportWithinFactor { get; init; } = 2.0;

    public bool CheckLayerToLayer { get; init; } = true;

    /// <summary>Follow the surface path around board cutouts on the outer layers.</summary>
    public bool CheckCutouts { get; init; } = true;

    /// <summary>Follow the surface over the board edge (and down cutout walls) from one outer
    /// layer to the other.</summary>
    public bool CheckBoardEdge { get; init; } = true;
}

public sealed record SpacingReport(IReadOnlyList<SpacingFinding> Findings, IReadOnlyList<string> Notes, SpacingTable Table)
{
    public IReadOnlyList<SpacingFinding> Violations => Findings.Where(f => !f.Passes).ToList();

    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>
        {
            $"Spacing check against {Table.Name}: {Findings.Count} pair(s) listed, {Violations.Count} below the requirement."
        };
        lines.AddRange(Findings.OrderBy(f => f.Passes).ThenBy(f => f.MarginMeters).Select(f => "  " + f.Message));
        lines.AddRange(Notes);
        return lines;
    }
}

/// <summary>
/// Clearance and creepage of a routed board against a spacing table. No field is solved:
/// the copper of every net with a working voltage is measured against every other.
/// <list type="bullet">
/// <item><b>Clearance</b>: on each layer, the shortest distance between the copper edges of
/// two nets, against the outer or inner column at the voltage between them. On an outer
/// layer of a board with solder mask, a pair whose closest edges the mask covers takes the
/// coated column when one is given.</item>
/// <item><b>Creepage</b>: on the outer layers, where the straight line between the two
/// closest points crosses a board cutout, the shortest path around the cutout's true outline
/// — the path a surface current would have to take. Reported only where it is longer than
/// the clearance; slots are what a designer adds to gain creepage.</item>
/// <item><b>Layer to layer</b>: copper of two nets overlapping across a dielectric gap,
/// against the inner column with the gap thickness as the spacing.</item>
/// <item><b>Over the edge</b>: copper on one outer layer to copper on the other by way of
/// the board edge or a cutout's wall — the run to the edge, the board's thickness, the run
/// from the edge — minimised over the crossing point.</item>
/// </list>
/// Conformal coating is not in the layout: choose the coated column for a coated board. The
/// voltage is whatever the table is defined in (IPC-2221: peak).
/// </summary>
public static class SpacingChecker
{
    private sealed class Island
    {
        public required CopperIsland Copper;
        public required string Net;
        public required double Volts;
        public double MinX, MinY, MaxX, MaxY;
        public EdgeIndex? Edges;
        public PolygonSetIndex? Inside;
    }

    /// <summary>Spacing of the points sampled along a board edge before the crossing point is refined.</summary>
    private const double EdgeSampleSpacing = 0.05e-3;

    public static SpacingReport Check(PcbBoard board, IReadOnlyDictionary<string, double> netVolts,
        SpacingCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(netVolts);
        options ??= new SpacingCheckOptions();
        var table = options.Table;
        table.Validate();
        int outer = table.ColumnIndex(options.OuterColumn);
        int inner = table.ColumnIndex(options.InnerColumn);
        int creepage = table.ColumnIndex(options.CreepageColumn ?? options.OuterColumn);
        int coated = options.CoatedOuterColumn is null ? -1 : table.ColumnIndex(options.CoatedOuterColumn);
        var stackup = options.Stackup ?? BoardStackup.FromBoard(board);

        var findings = new List<SpacingFinding>();
        var notes = new List<string>();

        // ---- Which copper carries a voltage.
        var netOf = new Dictionary<CopperIsland, CopperNet>(ReferenceEqualityComparer.Instance);
        foreach (var net in board.Nets)
            foreach (var island in net.Islands) netOf[island] = net;
        var unlisted = new SortedSet<string>();
        int ownerless = 0;
        var islands = new List<Island>();
        foreach (var copper in board.Islands)
        {
            if (!netOf.TryGetValue(copper, out var net)) { ownerless++; continue; }
            string name = NameOf(net);
            if (!netVolts.TryGetValue(name, out double volts)) { unlisted.Add(name); continue; }
            var (minX, minY, maxX, maxY) = copper.Bounds();
            islands.Add(new Island { Copper = copper, Net = name, Volts = volts, MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY });
        }
        if (ownerless > 0) notes.Add($"{ownerless} copper island(s) belong to no net and were not checked.");
        if (unlisted.Count > 0)
            notes.Add($"{unlisted.Count} net(s) have no working voltage and were not checked: " +
                      string.Join(", ", unlisted.Take(12)) + (unlisted.Count > 12 ? ", …" : "") + ".");

        int layerCount = Math.Max(board.Islands.Count > 0 ? board.Islands.Max(i => i.LayerOrder) : 1,
            board.Stackup is { DielectricGapThicknesses.Count: > 0 } s ? s.DielectricGapThicknesses.Count + 1 : 1);
        bool IsOuter(int layer) => layer == 1 || layer == layerCount;

        var cutouts = options.CheckCutouts ? Cutouts(board) : new List<IReadOnlyList<Point2>>();

        // Solder mask: the openings of each side, when the board carries them.
        var topMask = board.TopMaskOpenings.Count > 0 ? new PolygonSetIndex(board.TopMaskOpenings) : null;
        var bottomMask = board.BottomMaskOpenings.Count > 0 ? new PolygonSetIndex(board.BottomMaskOpenings) : null;
        PolygonSetIndex? MaskOf(int layer) => layer == 1 ? topMask : layer == layerCount ? bottomMask : null;
        bool maskUsed = coated >= 0 && (topMask is not null || bottomMask is not null);
        int coatedPairs = 0;

        // ---- Clearance (and creepage) on each layer.
        foreach (var group in islands.GroupBy(i => i.Copper.LayerOrder).OrderBy(g => g.Key))
        {
            int layer = group.Key;
            int column = IsOuter(layer) ? outer : inner;
            var list = group.ToList();
            var mask = coated >= 0 && IsOuter(layer) ? MaskOf(layer) : null;

            // The largest cutoff on the layer sets the grid cell, so every query radius fits.
            double cell = 0;
            var required = new Dictionary<(string, string), double>();
            foreach (var a in list)
                foreach (var b in list)
                {
                    if (string.CompareOrdinal(a.Net, b.Net) >= 0) continue;
                    var key = (a.Net, b.Net);
                    if (required.ContainsKey(key)) continue;
                    double dv = Math.Abs(a.Volts - b.Volts);
                    if (dv <= 0) continue;
                    double r = table.Required(dv, column);
                    if (mask is not null) r = Math.Max(r, table.Required(dv, coated));
                    required[key] = r;
                    cell = Math.Max(cell, r * options.ReportWithinFactor);
                }
            if (required.Count == 0) continue;

            // Best (closest) pair per net pair on this layer.
            var best = new Dictionary<(string, string), (double Dist, Point2 A, Point2 B)>();
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i]; var b = list[j];
                    if (a.Net == b.Net) continue;
                    if (string.CompareOrdinal(a.Net, b.Net) > 0) (a, b) = (b, a);
                    if (!required.TryGetValue((a.Net, b.Net), out double req)) continue;
                    double cutoff = req * options.ReportWithinFactor;
                    if (BoxGap(a, b) >= cutoff) continue;
                    a.Edges ??= new EdgeIndex(a.Copper.Shape, cell);
                    b.Edges ??= new EdgeIndex(b.Copper.Shape, cell);
                    var pair = Distance(a, b, cutoff);
                    if (pair.Dist >= cutoff) continue;
                    if (!best.TryGetValue((a.Net, b.Net), out var current) || pair.Dist < current.Dist)
                        best[(a.Net, b.Net)] = pair;
                }

            foreach (var ((netA, netB), (dist, pa, pb)) in best)
            {
                double dv = Math.Abs(list.First(x => x.Net == netA).Volts - list.First(x => x.Net == netB).Volts);
                // The mask covers the pair when neither closest edge point lies in an opening.
                int pairColumn = column;
                if (mask is not null && !mask.Contains(pa) && !mask.Contains(pb)) { pairColumn = coated; coatedPairs++; }
                double pairRequired = table.Required(dv, pairColumn);
                if (dist >= pairRequired * options.ReportWithinFactor) continue;
                findings.Add(new SpacingFinding(SpacingKind.Clearance, netA, netB, layer, dv, dist,
                    pairRequired, pa, table.Columns[pairColumn]));
                if (!IsOuter(layer) || cutouts.Count == 0) continue;
                if (!cutouts.Any(c => Blocked(pa, pb, c))) continue;
                double path = Creepage(list.Where(x => x.Net == netA).ToList(), list.Where(x => x.Net == netB).ToList(),
                    cutouts, cell);
                if (path > dist * (1 + 1e-9) && !double.IsPositiveInfinity(path))
                    findings.Add(new SpacingFinding(SpacingKind.Creepage, netA, netB, layer, dv, path,
                        table.Required(dv, creepage), pa, table.Columns[creepage]));
                else if (double.IsPositiveInfinity(path))
                    notes.Add($"L{layer}: the surface path between {netA} and {netB} around the cutout runs farther than " +
                              $"{cell * 1e3:g3} mm from the copper and was not followed.");
            }
        }

        // ---- Copper facing copper across each dielectric gap.
        if (options.CheckLayerToLayer)
        {
            var byLayer = islands.ToLookup(i => i.Copper.LayerOrder);
            for (int upper = 1; upper < layerCount; upper++)
            {
                var above = byLayer[upper].ToList();
                var below = byLayer[upper + 1].ToList();
                if (above.Count == 0 || below.Count == 0) continue;
                double gap = stackup.GapThicknessOf(upper);
                var seen = new Dictionary<(string, string), Point2>();
                foreach (var a in above)
                    foreach (var b in below)
                    {
                        if (a.Net == b.Net) continue;
                        var key = string.CompareOrdinal(a.Net, b.Net) < 0 ? (a.Net, b.Net) : (b.Net, a.Net);
                        if (seen.ContainsKey(key)) continue;
                        if (Math.Abs(a.Volts - b.Volts) <= 0) continue;
                        if (a.MaxX < b.MinX || b.MaxX < a.MinX || a.MaxY < b.MinY || b.MaxY < a.MinY) continue;
                        if (Overlap(a, b) is { } at) seen[key] = at;
                    }
                foreach (var ((netA, netB), at) in seen)
                {
                    double dv = Math.Abs(islands.First(x => x.Net == netA).Volts - islands.First(x => x.Net == netB).Volts);
                    double req = table.Required(dv, inner);
                    if (gap >= req * options.ReportWithinFactor) continue;
                    findings.Add(new SpacingFinding(SpacingKind.LayerToLayer, netA, netB, upper, dv, gap, req, at,
                        table.Columns[inner]));
                }
            }
        }

        // ---- Over the board edge: top copper to bottom copper by way of an edge or a cutout wall.
        int edgePairs = 0;
        if (options.CheckBoardEdge && layerCount >= 2 && board.Outline.Count > 0)
        {
            double thickness = stackup.TotalThickness(layerCount);
            var edges = board.Outline.SelectMany(Polygon2.OrientedRings).Where(r => r.Count >= 2).ToList();
            var top = islands.Where(i => i.Copper.LayerOrder == 1).ToList();
            var bottom = islands.Where(i => i.Copper.LayerOrder == layerCount).ToList();
            var done = new HashSet<(string, string)>();
            foreach (var a in top)
                foreach (var b in bottom)
                {
                    if (a.Net == b.Net) continue;
                    var key = (a.Net, b.Net);
                    if (!done.Add(key)) continue;
                    double dv = Math.Abs(a.Volts - b.Volts);
                    if (dv <= 0) continue;
                    double req = table.Required(dv, creepage);
                    double cutoff = req * options.ReportWithinFactor;
                    if (thickness >= cutoff) continue;
                    var result = EdgePath(top.Where(x => x.Net == a.Net).ToList(), bottom.Where(x => x.Net == b.Net).ToList(),
                        edges, cutouts, cutoff - thickness);
                    if (result is not { } crossing) continue;
                    double path = crossing.RunA + thickness + crossing.RunB;
                    if (path >= cutoff) continue;
                    edgePairs++;
                    findings.Add(new SpacingFinding(SpacingKind.EdgeCreepage, a.Net, b.Net, 1, dv, path, req, crossing.At,
                        table.Columns[creepage]) { OtherLayer = layerCount });
                }
        }

        notes.Add($"Table: {table.Name} ({table.Source}). Columns: {table.Columns[outer]} on the outer layers " +
                  $"(L1 and L{layerCount}){(maskUsed ? $" where the copper edge is exposed, {table.Columns[coated]} where the solder mask covers it ({coatedPairs} pair(s))" : "")}, " +
                  $"{table.Columns[inner]} on inner layers and across dielectric gaps" +
                  (cutouts.Count > 0 || options.CheckBoardEdge ? $", {table.Columns[creepage]} for a surface path around a cutout or over the board edge." : "."));
        notes.Add("Voltages are the working voltages between nets as the table defines them (IPC-2221: peak). " +
                  "Clearance is the straight distance between copper edges; the surface path around a board cutout " +
                  "(its true outline) is the creepage." +
                  (maskUsed
                      ? " Solder mask is read from the board's mask openings: a pair is coated when neither of its closest " +
                        "copper edges lies in an opening. Conformal coating is not in the layout."
                      : coated is >= 0
                          ? " The board carries no solder-mask layers, so the coated column was not applied."
                          : " Solder mask and coatings are not modelled — pick the coated column for a coated board.") +
                  " Component bodies and leads are not in the layout and are not checked.");
        if (options.CheckLayerToLayer)
            notes.Add($"Dielectric gaps from the stackup ({stackup.Source}); copper of two nets counts as facing when " +
                      "a vertex or edge midpoint of one lies inside the other.");
        if (options.CheckBoardEdge && layerCount >= 2)
            notes.Add(board.Outline.Count > 0
                ? $"Over the edge: top copper to bottom copper by way of the board edge or a cutout wall — the straight run " +
                  $"to the edge, the board thickness ({stackup.TotalThickness(layerCount) * 1e3:0.###} mm from the stackup), " +
                  $"the run from it — minimised over the crossing point ({edgePairs} pair(s) listed); a run that would cross " +
                  "a cutout is not followed."
                : "Over the edge: the board has no outline, so no edge path was measured.");
        return new SpacingReport(findings, notes, table);
    }

    private static string NameOf(CopperNet net) => net.Name ?? $"Net {net.Id}";

    private static double BoxGap(Island a, Island b)
    {
        double dx = Math.Max(0, Math.Max(a.MinX - b.MaxX, b.MinX - a.MaxX));
        double dy = Math.Max(0, Math.Max(a.MinY - b.MaxY, b.MinY - a.MaxY));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The closest approach of two non-overlapping polygons within a cutoff: a
    /// vertex of one against the edges of the other, both ways.</summary>
    private static (double Dist, Point2 A, Point2 B) Distance(Island a, Island b, double cutoff)
    {
        double best = double.PositiveInfinity;
        Point2 pa = default, pb = default;
        foreach (var p in Vertices(a.Copper.Shape))
        {
            var (d, foot) = b.Edges!.Nearest(p, cutoff);
            if (d < best) { best = d; pa = p; pb = foot; }
        }
        foreach (var p in Vertices(b.Copper.Shape))
        {
            var (d, foot) = a.Edges!.Nearest(p, cutoff);
            if (d < best) { best = d; pa = foot; pb = p; }
        }
        return (best, pa, pb);
    }

    private static IEnumerable<Point2> Vertices(Polygon2 polygon)
    {
        foreach (var p in polygon.Outer) yield return p;
        foreach (var hole in polygon.Holes)
            foreach (var p in hole) yield return p;
    }

    /// <summary>A point of one island inside the other, or null when they do not overlap.</summary>
    private static Point2? Overlap(Island a, Island b)
    {
        a.Inside ??= new PolygonSetIndex(new[] { a.Copper.Shape });
        b.Inside ??= new PolygonSetIndex(new[] { b.Copper.Shape });
        foreach (var p in Probes(a.Copper.Shape)) if (b.Inside.Contains(p)) return p;
        foreach (var p in Probes(b.Copper.Shape)) if (a.Inside.Contains(p)) return p;
        return null;
    }

    private static IEnumerable<Point2> Probes(Polygon2 polygon)
    {
        foreach (var ring in Polygon2.OrientedRings(polygon))
            for (int i = 0; i < ring.Count; i++)
            {
                yield return ring[i];
                yield return (ring[i] + ring[(i + 1) % ring.Count]) * 0.5;
            }
    }

    // ---------------- Cutouts and the surface path around them ----------------

    /// <summary>Board cutouts as their true outlines: the holes of the board outline, and any
    /// further outline polygon lying inside the largest one.</summary>
    private static List<IReadOnlyList<Point2>> Cutouts(PcbBoard board)
    {
        var result = new List<IReadOnlyList<Point2>>();
        if (board.Outline.Count == 0) return result;
        var largest = board.Outline.MaxBy(p => Math.Abs(Polygon2.RingArea(p.Outer)))!;
        var inside = new PolygonSetIndex(new[] { new Polygon2(largest.Outer) });
        foreach (var polygon in board.Outline)
        {
            if (ReferenceEquals(polygon, largest))
            {
                foreach (var hole in polygon.Holes)
                    if (hole.Count >= 3) result.Add(hole);
                continue;
            }
            if (polygon.Outer.Count >= 3 && inside.Contains(Centroid(polygon.Outer)))
                result.Add(polygon.Outer);
        }
        return result;
    }

    private static Point2 Centroid(IReadOnlyList<Point2> ring)
    {
        double x = 0, y = 0;
        foreach (var p in ring) { x += p.X; y += p.Y; }
        return new Point2(x / ring.Count, y / ring.Count);
    }

    /// <summary>
    /// The shortest surface path between the copper of two nets on a layer around the
    /// cutouts: Dijkstra over the cutouts' vertices, where a vertex joins a net at the
    /// nearest point of its copper, and the nets join directly by the shortest straight
    /// segment from a vertex of one to the copper of the other that crosses no cutout.
    /// Infinity when the path leaves the reach of the proximity grid.
    /// </summary>
    private static double Creepage(IReadOnlyList<Island> netA, IReadOnlyList<Island> netB,
        IReadOnlyList<IReadOnlyList<Point2>> cutouts, double reach)
    {
        foreach (var island in netA) island.Edges ??= new EdgeIndex(island.Copper.Shape, reach);
        foreach (var island in netB) island.Edges ??= new EdgeIndex(island.Copper.Shape, reach);

        // Nodes: 0 = net A, 1 = net B, then the vertices of the cutouts near either net.
        double minX = Math.Min(netA.Min(i => i.MinX), netB.Min(i => i.MinX)) - reach;
        double maxX = Math.Max(netA.Max(i => i.MaxX), netB.Max(i => i.MaxX)) + reach;
        double minY = Math.Min(netA.Min(i => i.MinY), netB.Min(i => i.MinY)) - reach;
        double maxY = Math.Max(netA.Max(i => i.MaxY), netB.Max(i => i.MaxY)) + reach;
        var near = cutouts.Where(c => c.Any(p => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY)).ToList();
        var vertices = near.SelectMany(h => h).ToList();
        int n = vertices.Count + 2;

        (double Dist, Point2 Foot) NearestOn(IReadOnlyList<Island> net, Point2 p)
        {
            double best = double.PositiveInfinity; Point2 foot = default;
            foreach (var island in net)
            {
                var (d, f) = island.Edges!.Nearest(p, reach);
                if (d < best) { best = d; foot = f; }
            }
            return (best, foot);
        }
        bool Clear(Point2 p, Point2 q) => !near.Any(c => Blocked(p, q, c));

        // Edge weights into a dense matrix (the graphs are small).
        var w = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) w[i, j] = double.PositiveInfinity;
        for (int k = 0; k < vertices.Count; k++)
        {
            var v = vertices[k];
            var (da, fa) = NearestOn(netA, v);
            if (da < double.PositiveInfinity && Clear(fa, v)) w[0, k + 2] = da;
            var (db, fb) = NearestOn(netB, v);
            if (db < double.PositiveInfinity && Clear(v, fb)) w[k + 2, 1] = db;
            for (int m = k + 1; m < vertices.Count; m++)
                if (Clear(v, vertices[m]))
                    w[k + 2, m + 2] = w[m + 2, k + 2] = (vertices[m] - v).Length;
        }
        // Direct: a vertex of one net to the nearest copper of the other, crossing nothing.
        double direct = double.PositiveInfinity;
        foreach (var (from, to) in new[] { (netA, netB), (netB, netA) })
            foreach (var island in from)
                foreach (var p in Vertices(island.Copper.Shape))
                {
                    var (d, f) = NearestOn(to, p);
                    if (d < direct && Clear(p, f)) direct = d;
                }
        w[0, 1] = direct;

        var dist = new double[n];
        Array.Fill(dist, double.PositiveInfinity);
        dist[0] = 0;
        var done = new bool[n];
        for (int step = 0; step < n; step++)
        {
            int u = -1;
            for (int i = 0; i < n; i++)
                if (!done[i] && (u < 0 || dist[i] < dist[u])) u = i;
            if (u < 0 || double.IsPositiveInfinity(dist[u]) || u == 1) break;
            done[u] = true;
            for (int v = 0; v < n; v++)
                if (!done[v] && dist[u] + w[u, v] < dist[v]) dist[v] = dist[u] + w[u, v];
        }
        return dist[1];
    }

    /// <summary>
    /// The shortest path from a to b that does not cross any cutout: the straight line when
    /// nothing is in the way, else Dijkstra over the visibility graph of the two points and
    /// the vertices of the cutouts near them. Cutouts are true polygons, convex or not.
    /// </summary>
    public static double SurfacePath(Point2 a, Point2 b, IReadOnlyList<IReadOnlyList<Point2>> cutouts)
    {
        double straight = (b - a).Length;
        if (cutouts.Count == 0 || !cutouts.Any(c => Blocked(a, b, c))) return straight;

        // Candidate obstacles: those within the a–b box grown by the straight distance.
        double minX = Math.Min(a.X, b.X) - straight, maxX = Math.Max(a.X, b.X) + straight;
        double minY = Math.Min(a.Y, b.Y) - straight, maxY = Math.Max(a.Y, b.Y) + straight;
        var near = cutouts.Where(c => c.Any(p => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY)).ToList();

        var nodes = new List<Point2> { a, b };
        foreach (var ring in near) nodes.AddRange(ring);
        int n = nodes.Count;
        var dist = new double[n];
        Array.Fill(dist, double.PositiveInfinity);
        dist[0] = 0;
        var done = new bool[n];
        for (int step = 0; step < n; step++)
        {
            int u = -1;
            for (int i = 0; i < n; i++)
                if (!done[i] && (u < 0 || dist[i] < dist[u])) u = i;
            if (u < 0 || double.IsPositiveInfinity(dist[u])) break;
            if (u == 1) break;
            done[u] = true;
            for (int v = 0; v < n; v++)
            {
                if (done[v] || v == u) continue;
                if (near.Any(c => Blocked(nodes[u], nodes[v], c))) continue;
                double d = dist[u] + (nodes[v] - nodes[u]).Length;
                if (d < dist[v]) dist[v] = d;
            }
        }
        // Unreachable (a cutout surrounds one point): the straight line is all there is to report.
        return double.IsPositiveInfinity(dist[1]) ? straight : dist[1];
    }

    // ---------------- Over the board edge ----------------

    /// <summary>
    /// The shortest run from net A's copper on one outer layer to a point on a board edge
    /// plus the run from that point to net B's copper on the other outer layer, minimised
    /// over the crossing point: every edge segment within reach is sampled every
    /// <see cref="EdgeSampleSpacing"/> and the best sample refined by golden section. A run
    /// whose straight segment crosses a cutout is not taken. Null when no edge is within reach.
    /// </summary>
    private static (double RunA, double RunB, Point2 At)? EdgePath(IReadOnlyList<Island> netA, IReadOnlyList<Island> netB,
        IReadOnlyList<IReadOnlyList<Point2>> edges, IReadOnlyList<IReadOnlyList<Point2>> cutouts, double reach)
    {
        if (netA.Count == 0 || netB.Count == 0 || reach <= 0) return null;
        var indexA = netA.Select(i => new EdgeIndex(i.Copper.Shape, reach)).ToList();
        var indexB = netB.Select(i => new EdgeIndex(i.Copper.Shape, reach)).ToList();
        double minX = Math.Max(netA.Min(i => i.MinX), netB.Min(i => i.MinX)) - reach;
        double maxX = Math.Min(netA.Max(i => i.MaxX), netB.Max(i => i.MaxX)) + reach;
        double minY = Math.Max(netA.Min(i => i.MinY), netB.Min(i => i.MinY)) - reach;
        double maxY = Math.Min(netA.Max(i => i.MaxY), netB.Max(i => i.MaxY)) + reach;
        if (minX > maxX || minY > maxY) return null;

        double RunTo(List<EdgeIndex> index, Point2 x)
        {
            double best = double.PositiveInfinity; Point2 foot = default;
            foreach (var edge in index)
            {
                var (d, f) = edge.Nearest(x, reach);
                if (d < best) { best = d; foot = f; }
            }
            if (double.IsPositiveInfinity(best)) return best;
            return cutouts.Any(c => Blocked(x, foot, c)) ? double.PositiveInfinity : best;
        }
        (double A, double B) Runs(Point2 x) => (RunTo(indexA, x), RunTo(indexB, x));

        double bestTotal = double.PositiveInfinity;
        (double, double, Point2)? best = null;
        foreach (var ring in edges)
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i]; var q = ring[(i + 1) % ring.Count];
                if (Math.Max(p.X, q.X) < minX || Math.Min(p.X, q.X) > maxX ||
                    Math.Max(p.Y, q.Y) < minY || Math.Min(p.Y, q.Y) > maxY) continue;
                double length = (q - p).Length;
                if (length <= 0) continue;
                int samples = Math.Max(1, (int)Math.Ceiling(length / EdgeSampleSpacing));
                double tBest = -1, fBest = double.PositiveInfinity;
                for (int k = 0; k <= samples; k++)
                {
                    double t = (double)k / samples;
                    var (ra, rb) = Runs(p + (q - p) * t);
                    if (ra + rb < fBest) { fBest = ra + rb; tBest = t; }
                }
                if (tBest < 0) continue;
                // Golden section around the best sample.
                double lo = Math.Max(0, tBest - 1.0 / samples), hi = Math.Min(1, tBest + 1.0 / samples);
                const double phi = 0.6180339887498949;
                double c = hi - phi * (hi - lo), d = lo + phi * (hi - lo);
                double fc = Sum(Runs(p + (q - p) * c)), fd = Sum(Runs(p + (q - p) * d));
                for (int it = 0; it < 40; it++)
                {
                    if (fc < fd) { hi = d; d = c; fd = fc; c = hi - phi * (hi - lo); fc = Sum(Runs(p + (q - p) * c)); }
                    else { lo = c; c = d; fc = fd; d = lo + phi * (hi - lo); fd = Sum(Runs(p + (q - p) * d)); }
                }
                double tFinal = fc < fd ? c : d;
                var x = p + (q - p) * tFinal;
                var runs = Runs(x);
                double total = Sum(runs);
                if (total < bestTotal) { bestTotal = total; best = (runs.A, runs.B, x); }
            }
        return best;

        static double Sum((double A, double B) runs) => runs.A + runs.B;
    }

    // ---------------- Segment against a polygon ----------------

    /// <summary>Whether the open segment p–q passes through the inside of a polygon ring,
    /// convex or not: a proper crossing of one of its edges, or a point of the segment
    /// strictly inside between the places where it touches the ring's vertices.</summary>
    private static bool Blocked(Point2 p, Point2 q, IReadOnlyList<Point2> ring)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            var c = ring[i]; var d = ring[(i + 1) % ring.Count];
            if (ProperCrossing(p, q, c, d)) return true;
        }
        var pq = q - p;
        double len2 = Point2.Dot(pq, pq);
        if (len2 <= 0) return StrictlyInside(p, ring);
        double tol = 1e-9 * Math.Max(Math.Sqrt(len2), Scale(ring));
        var ts = new List<double> { 0, 1 };
        foreach (var v in ring)
        {
            double t = Point2.Dot(v - p, pq) / len2;
            if (t < -1e-12 || t > 1 + 1e-12) continue;
            var foot = p + pq * Math.Clamp(t, 0, 1);
            if ((v - foot).Length <= tol) ts.Add(Math.Clamp(t, 0, 1));
        }
        ts.Sort();
        for (int i = 0; i + 1 < ts.Count; i++)
        {
            if (ts[i + 1] - ts[i] <= 1e-12) continue;
            if (StrictlyInside(p + pq * (0.5 * (ts[i] + ts[i + 1])), ring)) return true;
        }
        return false;
    }

    private static bool ProperCrossing(Point2 p, Point2 q, Point2 c, Point2 d)
    {
        double d1 = Point2.Cross(q - p, c - p), d2 = Point2.Cross(q - p, d - p);
        double d3 = Point2.Cross(d - c, p - c), d4 = Point2.Cross(d - c, q - c);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    private static double Scale(IReadOnlyList<Point2> ring)
    {
        double scale = 1e-9;
        foreach (var v in ring) scale = Math.Max(scale, Math.Abs(v.X) + Math.Abs(v.Y));
        return scale;
    }

    /// <summary>Strictly inside a ring (convex or not): not within tolerance of an edge, and
    /// an odd number of edge crossings on a ray.</summary>
    private static bool StrictlyInside(Point2 p, IReadOnlyList<Point2> ring)
    {
        if (ring.Count < 3) return false;
        double tol = 1e-9 * Scale(ring);
        bool inside = false;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            var ab = b - a;
            double len2 = Point2.Dot(ab, ab);
            double t = len2 > 0 ? Math.Clamp(Point2.Dot(p - a, ab) / len2, 0, 1) : 0;
            if ((p - (a + ab * t)).Length <= tol) return false;
            if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    // ---------------- Edge proximity index ----------------

    /// <summary>A uniform grid over a polygon's edges answering "the nearest edge within r
    /// of p" exactly for r ≤ the cell size.</summary>
    private sealed class EdgeIndex
    {
        private readonly double _cell;
        private readonly Dictionary<(long, long), List<(Point2 A, Point2 B)>> _cells = new();

        public EdgeIndex(Polygon2 polygon, double cell)
        {
            _cell = Math.Max(cell, 1e-9);
            foreach (var ring in Polygon2.OrientedRings(polygon))
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                    long x0 = Cell(Math.Min(a.X, b.X)), x1 = Cell(Math.Max(a.X, b.X));
                    long y0 = Cell(Math.Min(a.Y, b.Y)), y1 = Cell(Math.Max(a.Y, b.Y));
                    for (long cx = x0; cx <= x1; cx++)
                        for (long cy = y0; cy <= y1; cy++)
                        {
                            if (!_cells.TryGetValue((cx, cy), out var list))
                                _cells[(cx, cy)] = list = new List<(Point2, Point2)>();
                            list.Add((a, b));
                        }
                }
        }

        private long Cell(double v) => (long)Math.Floor(v / _cell);

        public (double Dist, Point2 Foot) Nearest(Point2 p, double r)
        {
            if (r > _cell * (1 + 1e-9)) throw new ArgumentOutOfRangeException(nameof(r), "Query radius exceeds the grid cell size.");
            long cx = Cell(p.X), cy = Cell(p.Y);
            double best = double.PositiveInfinity;
            Point2 foot = default;
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (_cells.TryGetValue((cx + dx, cy + dy), out var list))
                        foreach (var (a, b) in list)
                        {
                            var f = Foot(p, a, b);
                            double d = (p - f).Length;
                            if (d < best) { best = d; foot = f; }
                        }
            return best < r ? (best, foot) : (double.PositiveInfinity, default);
        }

        private static Point2 Foot(Point2 p, Point2 a, Point2 b)
        {
            var ab = b - a;
            double t = Math.Clamp(Point2.Dot(p - a, ab) / Math.Max(Point2.Dot(ab, ab), 1e-300), 0, 1);
            return a + ab * t;
        }
    }
}
