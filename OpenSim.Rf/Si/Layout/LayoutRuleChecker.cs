using System.Text.RegularExpressions;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;

namespace OpenSim.Rf.Si.Layout;

public enum LayoutFindingKind
{
    /// <summary>A trace runs over a gap in its reference plane: a split between two plane
    /// areas, a slot or void in one, or past the plane's edge.</summary>
    PlaneGapCrossing,

    /// <summary>A signal changes layer and with it the plane its return current runs in,
    /// with no stitching via or capacitor near the via to carry the return across.</summary>
    ReferenceChange,

    /// <summary>The unused length of a via barrel beyond the layers the signal uses.</summary>
    ViaStub,

    /// <summary>A trace close to the board edge.</summary>
    BoardEdge
}

public enum LayoutSeverity { Note, Warning }

/// <summary>One thing a layout check found.</summary>
/// <param name="Layer">The trace layer (for a via finding, the upper of the two).</param>
/// <param name="MeasureMeters">The length that makes it a finding: the uncovered length, the
/// distance to the nearest return path, the stub length, or the clearance to the edge.</param>
public sealed record LayoutFinding(LayoutFindingKind Kind, LayoutSeverity Severity, string Net,
    int Layer, Point2 At, double MeasureMeters, string Message);

/// <summary>Limits of the layout checks. They are settings of this program, not values
/// from a standard: set them to the design's own rules.</summary>
public sealed record LayoutCheckOptions
{
    /// <summary>Layer and gap thicknesses for the stub length. Null is the board file's
    /// stackup completed with defaults.</summary>
    public BoardStackup? Stackup { get; init; }

    /// <summary>Copper of another net counts as a reference plane when its island is at least
    /// this large [m²].</summary>
    public double MinPlaneAreaSquareMeters { get; init; } = 25e-6;

    /// <summary>A layer is a net's reference when plane copper lies under at least this
    /// fraction of the net's traces on the layer next to it.</summary>
    public double ReferenceCoverage { get; init; } = 0.5;

    /// <summary>Uncovered stretches shorter than this are not reported.</summary>
    public double MinGapMeters { get; init; } = 0.1e-3;

    /// <summary>An uncovered stretch that ends at one of the net's own vias is the via's
    /// clearance hole in the plane, and is not reported up to this length.</summary>
    public double AntipadAllowanceMeters { get; init; } = 1e-3;

    /// <summary>A stitching via between the two reference planes must lie within this of the
    /// signal via.</summary>
    public double StitchingRadiusMeters { get; init; } = 2.5e-3;

    /// <summary>A capacitor between two reference planes of different nets must have a pad
    /// within this of the signal via.</summary>
    public double CapacitorRadiusMeters { get; init; } = 5e-3;

    /// <summary>Via stubs longer than this are reported.</summary>
    public double MaxStubMeters { get; init; } = 0.5e-3;

    /// <summary>Traces whose copper comes closer to the board outline than this are reported.</summary>
    public double EdgeClearanceMeters { get; init; } = 0.5e-3;
}

public sealed record LayoutCheckReport(IReadOnlyList<LayoutFinding> Findings, IReadOnlyList<string> Notes)
{
    public int Count(LayoutFindingKind kind) => Findings.Count(f => f.Kind == kind);
}

/// <summary>
/// Geometric checks of a routed board for the commonest signal-integrity and EMI layout
/// mistakes. No field is solved: each check reads the copper, the vias and the outline.
/// <list type="bullet">
/// <item><b>Plane gaps.</b> For every net's traces on a layer, the reference is the nearest
/// layer on each side with plane copper under most of them. Wherever a trace then has no
/// plane copper under it, the return current has to detour: a split between two plane
/// areas, a slot or void, or the plane's edge.</item>
/// <item><b>Reference changes.</b> Where a signal changes layer through a via and the two
/// layers do not share a reference plane, the return current needs a path between the
/// planes near the via: a stitching via when both planes are one net, a capacitor when they
/// are two.</item>
/// <item><b>Via stubs.</b> The barrel beyond the layers the signal uses, with its
/// quarter-wave frequency.</item>
/// <item><b>Board edge.</b> Trace copper closer to the outline than a limit.</item>
/// </list>
/// </summary>
public static class LayoutRuleChecker
{
    private const double C0 = 299_792_458.0;

    private sealed class Piece
    {
        public required TraceCenterline Trace;
        public required Point2[] Samples;
        public double Step;
    }

    /// <summary>An uncovered stretch: its two ends, and at each the plane island the trace
    /// was over just before (null where the trace itself ends).</summary>
    private sealed class Run
    {
        public Point2 A, B;
        public CopperIsland? IslandA, IslandB;
        public double Length, Width;
    }

    public static LayoutCheckReport Check(PcbBoard board, LayoutCheckOptions? options = null) =>
        Check(new BoardLayoutIndex(board), options);

    public static LayoutCheckReport Check(BoardLayoutIndex index, LayoutCheckOptions? options = null)
    {
        options ??= new LayoutCheckOptions();
        var board = index.Board;
        var stackup = options.Stackup ?? BoardStackup.FromBoard(board);
        var findings = new List<LayoutFinding>();
        var notes = new List<string>();

        CopperIsland? PlaneAt(int layer, Point2 p, CopperNet net)
        {
            var island = index.LargeIslandAt(layer, p, options.MinPlaneAreaSquareMeters);
            return island is null || ReferenceEquals(index.NetOf(island), net) ? null : island;
        }

        // ---- The traces of each net on each layer, sampled.
        double step = options.MinGapMeters / 2;
        var groups = new Dictionary<(int Net, int Layer), (CopperNet Net, List<Piece> Pieces)>();
        foreach (var (trace, net) in index.Traces)
        {
            int n = (int)Math.Clamp(Math.Ceiling(trace.Length / step), 1, 4000);
            var samples = new Point2[n + 1];
            for (int k = 0; k <= n; k++)
                samples[k] = trace.Start + (trace.End - trace.Start) * ((double)k / n);
            var key = (net.Id, trace.LayerOrder);
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = (net, new List<Piece>());
            group.Pieces.Add(new Piece { Trace = trace, Samples = samples, Step = trace.Length / n });
        }

        // ---- References, and the gaps in them.
        var references = new Dictionary<(int Net, int Layer), List<int>>();
        int unreferenced = 0;
        foreach (var (key, group) in groups.OrderBy(g => g.Key.Net).ThenBy(g => g.Key.Layer))
        {
            var net = group.Net;
            int layer = key.Layer;
            var found = new List<int>();
            foreach (int direction in new[] { 1, -1 })
                for (int c = layer + direction; c >= 1 && c <= index.LayerCount; c += direction)
                {
                    long covered = 0, total = 0;
                    foreach (var piece in group.Pieces)
                        foreach (var p in piece.Samples)
                        {
                            total++;
                            if (PlaneAt(c, p, net) is not null) covered++;
                        }
                    if (total > 0 && (double)covered / total >= options.ReferenceCoverage)
                    {
                        found.Add(c);
                        break;
                    }
                }
            references[key] = found;
            if (found.Count == 0) { unreferenced++; continue; }

            foreach (int reference in found)
            {
                var runs = new List<Run>();
                foreach (var piece in group.Pieces)
                {
                    var under = new CopperIsland?[piece.Samples.Length];
                    for (int k = 0; k < under.Length; k++) under[k] = PlaneAt(reference, piece.Samples[k], net);
                    int i = 0;
                    while (i < under.Length)
                    {
                        if (under[i] is not null) { i++; continue; }
                        int j = i;
                        while (j + 1 < under.Length && under[j + 1] is null) j++;
                        bool openStart = i == 0, openEnd = j == under.Length - 1;
                        // Between covered samples the gap is one step longer than the uncovered
                        // samples span; on average half a step is uncovered at each closed end.
                        double length = (j - i) * piece.Step
                            + (openStart ? 0 : piece.Step / 2) + (openEnd ? 0 : piece.Step / 2);
                        runs.Add(new Run
                        {
                            A = piece.Samples[i], IslandA = openStart ? null : under[i - 1],
                            B = piece.Samples[j], IslandB = openEnd ? null : under[j + 1],
                            Length = length, Width = piece.Trace.Width
                        });
                        i = j + 1;
                    }
                }
                Join(runs);

                foreach (var run in runs)
                {
                    if (run.Length < options.MinGapMeters) continue;
                    bool openA = run.IslandA is null, openB = run.IslandB is null;
                    if (openA != openB)
                    {
                        // One end is where the trace ends. At one of the net's own vias that is
                        // the clearance hole the via needs.
                        var end = openA ? run.A : run.B;
                        bool atVia = net.StitchingVias.Any(v =>
                            (v.Via.Position - end).Length <= v.Via.Diameter + run.Width);
                        if (atVia && run.Length <= options.AntipadAllowanceMeters) continue;
                    }
                    var middle = (run.A + run.B) * 0.5;
                    int? other = found.Where(r => r != reference && PlaneAt(r, middle, net) is not null)
                        .Select(r => (int?)r).FirstOrDefault();
                    string what;
                    if (openA && openB)
                        what = $"runs {run.Length * 1e3:g3} mm with no plane copper on L{reference} under it";
                    else if (openA || openB)
                        what = $"runs {run.Length * 1e3:g3} mm past the edge of {index.NameOf((run.IslandA ?? run.IslandB)!)} on L{reference}";
                    else if (ReferenceEquals(run.IslandA, run.IslandB))
                        what = $"crosses a {run.Length * 1e3:g3} mm slot or void in {index.NameOf(run.IslandA!)} on L{reference}";
                    else if (index.NameOf(run.IslandA!) == index.NameOf(run.IslandB!))
                        what = $"crosses a {run.Length * 1e3:g3} mm split on L{reference} between two separate "
                            + $"areas of {index.NameOf(run.IslandA!)}";
                    else
                        what = $"crosses a {run.Length * 1e3:g3} mm split on L{reference} between "
                            + $"{index.NameOf(run.IslandA!)} and {index.NameOf(run.IslandB!)}";
                    string tail = other is { } o
                        ? $" The other reference, L{o}, is continuous there."
                        : " The return current has no plane to follow across it.";
                    findings.Add(new LayoutFinding(LayoutFindingKind.PlaneGapCrossing,
                        other is null ? LayoutSeverity.Warning : LayoutSeverity.Note,
                        BoardLayoutIndex.NameOf(net), layer, middle, run.Length,
                        $"{BoardLayoutIndex.NameOf(net)} on L{layer} {what} at "
                        + $"({middle.X * 1e3:f2}, {middle.Y * 1e3:f2}) mm.{tail}"));
                }
            }
        }
        if (unreferenced > 0)
            notes.Add($"{unreferenced} net/layer group(s) of traces have no plane copper under at least "
                + $"{options.ReferenceCoverage:P0} of them on any layer; they have no reference to check.");

        // ---- Vias: reference changes and stubs.
        var capacitors = board.Pads
            .Where(p => p.ComponentRef is { } r && Regex.IsMatch(r, @"^C\d"))
            .GroupBy(p => p.ComponentRef!)
            .Select(g => g.Select(p => (p.Center,
                Net: index.IslandAt(p.LayerOrder, p.Center) is { } island ? index.NetOf(island) : null)).ToList())
            .ToList();
        bool componentData = board.Pads.Any(p => p.ComponentRef is not null);

        double Span(int upper, int lower)
        {
            double d = 0;
            for (int k = upper; k < lower; k++) d += stackup.GapThicknessOf(k);
            for (int k = upper + 1; k < lower; k++) d += stackup.CopperThicknessOf(k);
            return d;
        }
        double MeanPermittivity(int upper, int lower)
        {
            double weighted = 0, total = 0;
            for (int k = upper; k < lower; k++)
            {
                weighted += stackup.PermittivityOf(k) * stackup.GapThicknessOf(k);
                total += stackup.GapThicknessOf(k);
            }
            return total > 0 ? weighted / total : stackup.DefaultPermittivity;
        }

        foreach (var net in board.Nets)
        {
            if (net.StitchingVias.Count == 0) continue;
            var netGroups = groups.Where(g => g.Key.Net == net.Id).ToList();
            if (netGroups.Count < 2) continue;
            string name = BoardLayoutIndex.NameOf(net);

            foreach (var bridge in net.StitchingVias)
            {
                var via = bridge.Via;
                // The trace that ends at this via on each layer, walked away from the via.
                var entering = new SortedDictionary<int, (Piece Piece, bool FromStart)>();
                foreach (var (key, group) in netGroups)
                {
                    if (!bridge.Layers.Contains(key.Layer)) continue;
                    foreach (var piece in group.Pieces)
                    {
                        double reach = via.Diameter + piece.Trace.Width;
                        bool atStart = (piece.Trace.Start - via.Position).Length <= reach;
                        bool atEnd = (piece.Trace.End - via.Position).Length <= reach;
                        if (!atStart && !atEnd) continue;
                        entering[key.Layer] = (piece, atStart);
                        break;
                    }
                }
                if (entering.Count < 2) continue;
                int first = entering.Keys.First(), last = entering.Keys.Last();

                // Stubs.
                int viaTop = via.FromLayer > 0 && via.ToLayer > 0 ? Math.Min(via.FromLayer, via.ToLayer) : 1;
                int viaBottom = via.FromLayer > 0 && via.ToLayer > 0
                    ? Math.Max(via.FromLayer, via.ToLayer) : index.LayerCount;
                foreach (var (upper, lower, side) in new[]
                         { (viaTop, first, "above"), (last, viaBottom, "below") })
                {
                    if (upper >= lower) continue;
                    double stub = Span(upper, lower);
                    if (stub <= options.MaxStubMeters) continue;
                    double resonance = C0 / (4 * stub * Math.Sqrt(MeanPermittivity(upper, lower)));
                    findings.Add(new LayoutFinding(LayoutFindingKind.ViaStub, LayoutSeverity.Warning, name,
                        first, via.Position, stub,
                        $"{name}: the via at ({via.Position.X * 1e3:f2}, {via.Position.Y * 1e3:f2}) mm carries the "
                        + $"signal between L{first} and L{last} and leaves a {stub * 1e3:g3} mm stub {side} "
                        + $"(L{upper} to L{lower}). Quarter-wave frequency of the bare barrel "
                        + $"{resonance / 1e9:g3} GHz; pad capacitance lowers it."));
                }

                // Reference change between the outermost two layers the signal uses here.
                if (!references.TryGetValue((net.Id, first), out var refsFirst) || refsFirst.Count == 0) continue;
                if (!references.TryGetValue((net.Id, last), out var refsLast) || refsLast.Count == 0) continue;
                if (refsFirst.Intersect(refsLast).Any()) continue;

                CopperIsland? PlaneAlong((Piece Piece, bool FromStart) entry, int reference)
                {
                    var samples = entry.Piece.Samples;
                    for (int k = 0; k < samples.Length; k++)
                    {
                        var p = samples[entry.FromStart ? k : samples.Length - 1 - k];
                        if (PlaneAt(reference, p, net) is { } island) return island;
                    }
                    return null;
                }

                bool carried = false, unknown = false;
                string detail = "";
                foreach (int ra in refsFirst)
                    foreach (int rb in refsLast)
                    {
                        if (carried) break;
                        var a = PlaneAlong(entering[first], ra);
                        var b = PlaneAlong(entering[last], rb);
                        if (a is null || b is null) continue;
                        var netA = index.NetOf(a);
                        var netB = index.NetOf(b);
                        if (netA is null || netB is null)
                        {
                            unknown = true;
                            detail = $"the planes on L{ra} and L{rb} are not in any net, so a path between them cannot be looked for";
                            continue;
                        }
                        if (ReferenceEquals(netA, netB))
                        {
                            double nearest = netA.StitchingVias
                                .Where(v => v.Layers.Contains(ra) && v.Layers.Contains(rb))
                                .Select(v => (v.Via.Position - via.Position).Length)
                                .DefaultIfEmpty(double.PositiveInfinity).Min();
                            if (nearest <= options.StitchingRadiusMeters) { carried = true; break; }
                            detail = double.IsInfinity(nearest)
                                ? $"no via joins {BoardLayoutIndex.NameOf(netA)} on L{ra} and L{rb}"
                                : $"the nearest via joining {BoardLayoutIndex.NameOf(netA)} on L{ra} and L{rb} is "
                                  + $"{nearest * 1e3:g3} mm away (limit {options.StitchingRadiusMeters * 1e3:g3} mm)";
                        }
                        else
                        {
                            bool capacitor = capacitors.Any(pads =>
                                pads.Any(p => ReferenceEquals(p.Net, netA)) && pads.Any(p => ReferenceEquals(p.Net, netB))
                                && pads.Any(p => (p.Center - via.Position).Length <= options.CapacitorRadiusMeters));
                            if (capacitor) { carried = true; break; }
                            detail = componentData
                                ? $"no capacitor between {BoardLayoutIndex.NameOf(netA)} (L{ra}) and "
                                  + $"{BoardLayoutIndex.NameOf(netB)} (L{rb}) has a pad within "
                                  + $"{options.CapacitorRadiusMeters * 1e3:g3} mm"
                                : $"the planes are different nets ({BoardLayoutIndex.NameOf(netA)} on L{ra}, "
                                  + $"{BoardLayoutIndex.NameOf(netB)} on L{rb}) and the board file names no "
                                  + "components, so a capacitor between them cannot be looked for";
                            unknown |= !componentData;
                        }
                    }
                if (carried || detail.Length == 0) continue;
                findings.Add(new LayoutFinding(LayoutFindingKind.ReferenceChange,
                    unknown ? LayoutSeverity.Note : LayoutSeverity.Warning, name, first, via.Position,
                    options.StitchingRadiusMeters,
                    $"{name} changes from L{first} (reference L{string.Join("/L", refsFirst)}) to L{last} "
                    + $"(reference L{string.Join("/L", refsLast)}) at ({via.Position.X * 1e3:f2}, "
                    + $"{via.Position.Y * 1e3:f2}) mm, and {detail}."));
            }
        }

        // ---- Board edge.
        var outline = new List<(Point2 A, Point2 B)>();
        foreach (var polygon in board.Outline)
            foreach (var ring in new[] { polygon.Outer }.Concat(polygon.Holes))
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                    outline.Add((ring[j], ring[i]));
        if (outline.Count == 0)
        {
            notes.Add("The board has no outline, so the clearance of traces to the board edge was not checked.");
        }
        else
        {
            foreach (var (key, group) in groups.OrderBy(g => g.Key.Net).ThenBy(g => g.Key.Layer))
            {
                double worst = double.PositiveInfinity, lengthInside = 0;
                Point2 at = default;
                foreach (var piece in group.Pieces)
                {
                    var t = piece.Trace;
                    double nearest = double.PositiveInfinity;
                    Point2 where = default;
                    foreach (var (a, b) in outline)
                    {
                        double d = SegmentDistance(t.Start, t.End, a, b, out var p);
                        if (d < nearest) { nearest = d; where = p; }
                    }
                    double clearance = nearest - t.Width / 2;
                    if (clearance >= options.EdgeClearanceMeters) continue;
                    lengthInside += t.Length;
                    if (clearance < worst) { worst = clearance; at = where; }
                }
                if (double.IsInfinity(worst)) continue;
                string name = BoardLayoutIndex.NameOf(group.Net);
                findings.Add(new LayoutFinding(LayoutFindingKind.BoardEdge, LayoutSeverity.Warning, name,
                    key.Layer, at, worst,
                    $"{name} on L{key.Layer} comes within {worst * 1e3:g3} mm of the board edge at "
                    + $"({at.X * 1e3:f2}, {at.Y * 1e3:f2}) mm (limit {options.EdgeClearanceMeters * 1e3:g3} mm); "
                    + $"{lengthInside * 1e3:g3} mm of trace segments are inside the limit."));
            }
        }

        notes.Add("A reference plane is copper of another net in one island of at least "
            + $"{options.MinPlaneAreaSquareMeters * 1e6:g3} mm²; the limits used are settings, not values from a standard.");
        notes.Add("Not checked: back-drilled vias (a back-drilled stub is reported at its full length), vias that "
            + "connect a trace on one layer only (test points and through-hole pins look the same), and whether a "
            + "pair of nets is a differential pair.");
        return new LayoutCheckReport(
            findings.OrderBy(f => f.Severity == LayoutSeverity.Warning ? 0 : 1)
                .ThenBy(f => f.Kind).ThenByDescending(f => f.Kind == LayoutFindingKind.BoardEdge ? -f.MeasureMeters : f.MeasureMeters)
                .ToList(),
            notes);
    }

    /// <summary>Join uncovered stretches that meet where two centerlines of the trace meet, so
    /// a gap that a joint happens to fall in is one finding with both its sides.</summary>
    private static void Join(List<Run> runs)
    {
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < runs.Count && !merged; i++)
                for (int j = i + 1; j < runs.Count && !merged; j++)
                {
                    var a = runs[i];
                    var b = runs[j];
                    double tolerance = Math.Max(a.Width, b.Width);
                    foreach (bool aAtA in new[] { true, false })
                    {
                        if (merged) break;
                        if ((aAtA ? a.IslandA : a.IslandB) is not null) continue;
                        foreach (bool bAtA in new[] { true, false })
                        {
                            if ((bAtA ? b.IslandA : b.IslandB) is not null) continue;
                            var pa = aAtA ? a.A : a.B;
                            var pb = bAtA ? b.A : b.B;
                            if ((pa - pb).Length > tolerance) continue;
                            runs[i] = new Run
                            {
                                A = aAtA ? a.B : a.A, IslandA = aAtA ? a.IslandB : a.IslandA,
                                B = bAtA ? b.B : b.A, IslandB = bAtA ? b.IslandB : b.IslandA,
                                Length = a.Length + b.Length, Width = tolerance
                            };
                            runs.RemoveAt(j);
                            merged = true;
                            break;
                        }
                    }
                }
        }
    }

    /// <summary>Shortest distance between segments p0p1 and q0q1, and the point on the first
    /// where it is reached.</summary>
    internal static double SegmentDistance(Point2 p0, Point2 p1, Point2 q0, Point2 q1, out Point2 onFirst)
    {
        if (Intersect(p0, p1, q0, q1, out var crossing)) { onFirst = crossing; return 0; }
        double best = double.PositiveInfinity;
        var closest = p0;
        void Try(Point2 point, Point2 a, Point2 b, bool pointIsOnFirst)
        {
            var ab = b - a;
            double l2 = Point2.Dot(ab, ab);
            double t = l2 > 0 ? Math.Clamp(Point2.Dot(point - a, ab) / l2, 0, 1) : 0;
            var foot = a + ab * t;
            double d = (point - foot).Length;
            if (d >= best) return;
            best = d;
            // Of the pair, the point that lies on the first segment.
            closest = pointIsOnFirst ? point : foot;
        }
        Try(p0, q0, q1, true);
        Try(p1, q0, q1, true);
        Try(q0, p0, p1, false);
        Try(q1, p0, p1, false);
        onFirst = closest;
        return best;
    }

    private static bool Intersect(Point2 p0, Point2 p1, Point2 q0, Point2 q1, out Point2 at)
    {
        at = default;
        var r = p1 - p0;
        var s = q1 - q0;
        double denominator = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(denominator) < 1e-300) return false;
        var d = q0 - p0;
        double t = (d.X * s.Y - d.Y * s.X) / denominator;
        double u = (d.X * r.Y - d.Y * r.X) / denominator;
        if (t < 0 || t > 1 || u < 0 || u > 1) return false;
        at = p0 + r * t;
        return true;
    }
}
