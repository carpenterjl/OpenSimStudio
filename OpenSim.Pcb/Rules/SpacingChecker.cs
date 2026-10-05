using System.Globalization;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
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
    LayerToLayer
}

/// <summary>One measured spacing against its requirement.</summary>
/// <param name="Layer">The layer; for <see cref="SpacingKind.LayerToLayer"/> the upper of the two.</param>
/// <param name="Volts">The working voltage between the two nets the requirement was read at.</param>
/// <param name="At">Where the spacing is smallest (on the first net's copper).</param>
/// <param name="Column">The table column the requirement came from.</param>
public sealed record SpacingFinding(SpacingKind Kind, string NetA, string NetB, int Layer, double Volts,
    double MeasuredMeters, double RequiredMeters, Point2 At, string Column)
{
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

    /// <summary>Column a creepage path around a cutout is held to; null uses the outer column.</summary>
    public string? CreepageColumn { get; init; }

    /// <summary>Layer and gap thicknesses. Null is the board file's stackup completed with defaults.</summary>
    public BoardStackup? Stackup { get; init; }

    /// <summary>Pairs farther apart than this many times their requirement are not listed.</summary>
    public double ReportWithinFactor { get; init; } = 2.0;

    public bool CheckLayerToLayer { get; init; } = true;

    /// <summary>Follow the surface path around board cutouts on the outer layers.</summary>
    public bool CheckCutouts { get; init; } = true;
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
/// two nets, against the outer or inner column at the voltage between them.</item>
/// <item><b>Creepage</b>: on the outer layers, where the straight line between the two
/// closest points crosses a board cutout, the shortest path around the cutout (taken as its
/// convex hull) — the path a surface current would have to take. Reported only where it is
/// longer than the clearance; slots are what a designer adds to gain creepage.</item>
/// <item><b>Layer to layer</b>: copper of two nets overlapping across a dielectric gap,
/// against the inner column with the gap thickness as the spacing.</item>
/// </list>
/// Solder mask and conformal coating are not modelled: choose the coated column when the
/// board has them. The voltage is whatever the table is defined in (IPC-2221: peak).
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

        // ---- Clearance (and creepage) on each layer.
        foreach (var group in islands.GroupBy(i => i.Copper.LayerOrder).OrderBy(g => g.Key))
        {
            int layer = group.Key;
            int column = IsOuter(layer) ? outer : inner;
            var list = group.ToList();

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
                findings.Add(new SpacingFinding(SpacingKind.Clearance, netA, netB, layer, dv, dist,
                    required[(netA, netB)], pa, table.Columns[column]));
                if (!IsOuter(layer) || cutouts.Count == 0) continue;
                if (!cutouts.Any(c => Crosses(pa, pb, c))) continue;
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

        notes.Add($"Table: {table.Name} ({table.Source}). Columns: {table.Columns[outer]} on the outer layers " +
                  $"(L1 and L{layerCount}), {table.Columns[inner]} on inner layers and across dielectric gaps" +
                  (cutouts.Count > 0 ? $", {table.Columns[creepage]} for a surface path around a cutout." : "."));
        notes.Add("Voltages are the working voltages between nets as the table defines them (IPC-2221: peak). " +
                  "Clearance is the straight distance between copper edges; the surface path around a board cutout " +
                  "(taken as its convex hull) is the creepage. Solder mask and coatings are not modelled — pick the " +
                  "coated column for a coated board. Component bodies and leads are not in the layout and are not checked.");
        if (options.CheckLayerToLayer)
            notes.Add($"Dielectric gaps from the stackup ({stackup.Source}); copper of two nets counts as facing when " +
                      "a vertex or edge midpoint of one lies inside the other.");
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

    /// <summary>Board cutouts as convex hulls: the holes of the board outline, and any
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
                    if (hole.Count >= 3) result.Add(ConvexHull.Compute(hole.ToList()));
                continue;
            }
            if (polygon.Outer.Count >= 3 && inside.Contains(Centroid(polygon.Outer)))
                result.Add(ConvexHull.Compute(polygon.Outer.ToList()));
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
    /// cutouts: Dijkstra over the hull vertices, where a hull vertex joins a net at the
    /// nearest point of its copper, and the nets join directly by the shortest straight
    /// segment from a vertex of one to the copper of the other that crosses no cutout.
    /// Infinity when the path leaves the reach of the proximity grid.
    /// </summary>
    private static double Creepage(IReadOnlyList<Island> netA, IReadOnlyList<Island> netB,
        IReadOnlyList<IReadOnlyList<Point2>> cutouts, double reach)
    {
        foreach (var island in netA) island.Edges ??= new EdgeIndex(island.Copper.Shape, reach);
        foreach (var island in netB) island.Edges ??= new EdgeIndex(island.Copper.Shape, reach);

        // Nodes: 0 = net A, 1 = net B, then the hull vertices of the cutouts near either net.
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
        bool Clear(Point2 p, Point2 q) => !near.Any(c => Crosses(p, q, c));

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
    /// the hull vertices of the cutouts near them.
    /// </summary>
    public static double SurfacePath(Point2 a, Point2 b, IReadOnlyList<IReadOnlyList<Point2>> cutouts)
    {
        double straight = (b - a).Length;
        if (cutouts.Count == 0 || !cutouts.Any(c => Crosses(a, b, c))) return straight;

        // Candidate obstacles: those within the a–b box grown by the straight distance.
        double minX = Math.Min(a.X, b.X) - straight, maxX = Math.Max(a.X, b.X) + straight;
        double minY = Math.Min(a.Y, b.Y) - straight, maxY = Math.Max(a.Y, b.Y) + straight;
        var near = cutouts.Where(c => c.Any(p => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY)).ToList();

        var nodes = new List<Point2> { a, b };
        foreach (var hull in near) nodes.AddRange(hull);
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
                if (near.Any(c => Crosses(nodes[u], nodes[v], c))) continue;
                double d = dist[u] + (nodes[v] - nodes[u]).Length;
                if (d < dist[v]) dist[v] = d;
            }
        }
        // Unreachable (a cutout surrounds one point): the straight line is all there is to report.
        return double.IsPositiveInfinity(dist[1]) ? straight : dist[1];
    }

    /// <summary>Whether the segment p–q passes through the inside of a convex hull: a proper
    /// crossing of one of its edges, or its midpoint strictly inside.</summary>
    private static bool Crosses(Point2 p, Point2 q, IReadOnlyList<Point2> hull)
    {
        for (int i = 0; i < hull.Count; i++)
        {
            var c = hull[i]; var d = hull[(i + 1) % hull.Count];
            if (ProperCrossing(p, q, c, d)) return true;
        }
        return StrictlyInside((p + q) * 0.5, hull);
    }

    private static bool ProperCrossing(Point2 p, Point2 q, Point2 c, Point2 d)
    {
        double d1 = Point2.Cross(q - p, c - p), d2 = Point2.Cross(q - p, d - p);
        double d3 = Point2.Cross(d - c, p - c), d4 = Point2.Cross(d - c, q - c);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }

    private static bool StrictlyInside(Point2 p, IReadOnlyList<Point2> hull)
    {
        // Convex: p is strictly inside when it lies strictly on one side of every edge.
        double scale = 0;
        foreach (var v in hull) scale = Math.Max(scale, Math.Abs(v.X) + Math.Abs(v.Y));
        double tolerance = 1e-12 * Math.Max(scale, 1e-9);
        int sign = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            var c = hull[i]; var d = hull[(i + 1) % hull.Count];
            double cross = Point2.Cross(d - c, p - c);
            if (Math.Abs(cross) <= tolerance) return false;
            int s = cross > 0 ? 1 : -1;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return hull.Count >= 3;
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
