using System.Globalization;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;

namespace OpenSim.Rf.Si.Layout;

/// <summary>Settings of the board crosstalk scan.</summary>
public sealed record CrosstalkScanOptions
{
    /// <summary>The stackup; null is the board file's completed with defaults.</summary>
    public BoardStackup? Stackup { get; init; }

    /// <summary>Two traces are scanned as a pair when their edges are no further apart than this.</summary>
    public double MaxEdgeGapMeters { get; init; } = 1e-3;

    /// <summary>Pairs of nets that run together for less than this in total are left out.</summary>
    public double MinParallelLengthMeters { get; init; } = 2e-3;

    /// <summary>Two runs count as parallel when their directions agree within this angle.</summary>
    public double AngleToleranceDegrees { get; init; } = 3;

    /// <summary>10–90 % edge of the aggressor [s]: what turns the per-unit-length coupling and
    /// the run length into a fraction of the aggressor's swing.</summary>
    public double RiseTimeSeconds { get; init; } = 0.5e-9;

    /// <summary>The net pairs that get a field solve, most strongly coupled (by geometry)
    /// first; the rest are counted in the report and not priced.</summary>
    public int MaxSolvedPairs { get; init; } = 300;

    /// <summary>Trace conductivity [S/m] (it does not enter the coupling).</summary>
    public double ConductivitySiemensPerMeter { get; init; } = 5.8e7;

    /// <summary>Also price the coupling between vias that cross the same plane pair (FU-29,
    /// <see cref="ViaCouplingScan"/>).</summary>
    public bool ViaCoupling { get; init; } = true;

    /// <summary>The lines' impedance the via coupling is a fraction of: |Z₂₁|/(2Z₀) [Ω].</summary>
    public double ViaReferenceOhms { get; init; } = 50;
}

/// <summary>One stretch geometry two nets share: same layer, widths and gap.</summary>
public sealed record CrosstalkRun(int Layer, double WidthAMeters, double WidthBMeters, double EdgeGapMeters,
    double LengthMeters, double NearEndCoupling, double FarEndCouplingSecondsPerMeter,
    double DelaySecondsPerMeter, double MutualCapacitanceFaradsPerMeter = 0)
{
    /// <summary>Width of the copper running between the two [m]; 0 when there is none.</summary>
    public double GuardWidthMeters { get; init; }

    /// <summary>The copper between them belongs to the reference plane's net (a stitched
    /// ground guard), rather than being another signal.</summary>
    public bool GuardIsGround { get; init; }

    /// <summary>The second trace's layer when the two run on different layers, one over the other
    /// (broadside, FU-29); 0 when both are on <see cref="CrosstalkRun.Layer"/>. The edge gap is then
    /// measured in plan, negative where they overlap.</summary>
    public int LayerB { get; init; }

    public bool Broadside => LayerB > 0 && LayerB != Layer;
}

/// <summary>Two nets that run side by side, with what couples between them.</summary>
public sealed record CrosstalkPair
{
    public required string NetA { get; init; }
    public required string NetB { get; init; }
    public required IReadOnlyList<CrosstalkRun> Runs { get; init; }

    /// <summary>Total length over which the two run parallel within the gap limit.</summary>
    public required double ParallelLengthMeters { get; init; }
    public required double MinEdgeGapMeters { get; init; }

    /// <summary>Near-end (backward) crosstalk as a fraction of the aggressor's swing at the
    /// scan's rise time: the largest of the runs, each K_b·min(1, 2·T_d/t_r).</summary>
    public required double NearEnd { get; init; }

    /// <summary>Far-end (forward) crosstalk as a fraction of the swing at the scan's rise
    /// time: |Σ K_f·length|/t_r. Zero between two planes in one dielectric.</summary>
    public required double FarEnd { get; init; }

    /// <summary>Where the longest shared stretch is.</summary>
    public required Point2 At { get; init; }

    /// <summary>The two names differ only in P/N or +/−: the coupling is the pair's own.</summary>
    public bool LikelyDifferentialPair { get; init; }

    /// <summary>Set instead of the numbers when the pair could not be priced.</summary>
    public string? NotPriced { get; init; }

    public double Worst => Math.Max(NearEnd, FarEnd);

    public string Describe()
    {
        static string F(double v) => v.ToString("g3", CultureInfo.InvariantCulture);
        string head = $"{NetA} / {NetB}: {F(ParallelLengthMeters * 1e3)} mm parallel on L"
            + string.Join("+L", Runs.SelectMany(r => r.Broadside ? new[] { r.Layer, r.LayerB } : new[] { r.Layer })
                .Distinct().OrderBy(l => l))
            + (Runs.Any(r => r.Broadside) ? " (broadside where on two layers)" : "")
            + $", closest gap {F(MinEdgeGapMeters * 1e3)} mm";
        if (NotPriced is not null) return head + " — not priced: " + NotPriced;
        return head + $" — near-end {F(NearEnd * 100)} %, far-end {F(FarEnd * 100)} % of the swing"
            + (LikelyDifferentialPair ? " (looks like a differential pair: this coupling is its own)" : "");
    }
}

public sealed record CrosstalkScanReport(IReadOnlyList<CrosstalkPair> Pairs, int PairsNotSolved,
    IReadOnlyList<string> Notes)
{
    /// <summary>Nets coupled through vias that cross the same plane pair, worst first (FU-29).</summary>
    public IReadOnlyList<ViaCouplingPair> ViaPairs { get; init; } = Array.Empty<ViaCouplingPair>();
}

/// <summary>
/// The whole-board crosstalk scan: every two nets with traces running side by side on a
/// layer, ranked by the crosstalk a fast edge on one puts on the other.
/// <para>
/// The geometry gives the run lengths and gaps. The coupling per unit length comes from the
/// same 2D field solve as the impedance calculator — the two traces over the reference
/// planes the board has under and over them — as the backward coefficient
/// K_b = ¼(C_m/C + L_m/L) and the forward coefficient K_f = ½(C_m·Z0 − L_m/Z0). With the
/// scan's rise time t_r a run of length ℓ and delay T_d = ℓ·√(LC) gives near-end
/// K_b·min(1, 2T_d/t_r) and far-end |K_f|·ℓ/t_r of the aggressor's swing, both for matched
/// ends. It is a ranking, not a waveform: the eye analysis on the worst pairs is the
/// next step.
/// </para>
/// </summary>
public static class CrosstalkScan
{
    private static readonly RlgcModel Model = new()
    {
        ThicknessCorrection = true, SurfaceImpedance = false, WidebandDielectric = false
    };

    private sealed class Group
    {
        public int Layer;
        /// <summary>The second trace's layer for a broadside group, and the centre-to-centre offset
        /// in plan; 0 for a group on one layer.</summary>
        public int LayerB;
        public double Offset;
        public double WidthA, WidthB, Gap, Length;
        /// <summary>Copper of a third net between the two over most of the run: its width,
        /// its centre's distance from A's centre toward B, and its net.</summary>
        public double GuardWidth, GuardFromA;
        public CopperNet? GuardNet;
        public double LongestPiece;
        public Point2 At;
        public List<Point2> Samples = new();
    }

    private sealed class PairData
    {
        public required CopperNet A, B;
        public Dictionary<(int, long, long, long), Group> Groups = new();
        /// <summary>Runs on two layers, one over the other: (layer A, layer B, widths, offset).</summary>
        public Dictionary<(int, int, long, long, long), Group> Broadside = new();
        public IEnumerable<Group> AllGroups => Groups.Values.Concat(Broadside.Values);
        public double Length => AllGroups.Sum(g => g.Length);
    }

    public static CrosstalkScanReport Run(PcbBoard board, CrosstalkScanOptions? options = null) =>
        Run(new BoardLayoutIndex(board), options);

    public static CrosstalkScanReport Run(BoardLayoutIndex index, CrosstalkScanOptions? options = null)
    {
        options ??= new CrosstalkScanOptions();
        if (!(options.RiseTimeSeconds > 0)) throw new ArgumentException("The rise time must be positive.");
        var board = index.Board;
        double cosTolerance = Math.Cos(options.AngleToleranceDegrees * Math.PI / 180);
        var traces = index.Traces;

        // ---- Parallel stretches, through a grid so only neighbours are compared.
        double maxWidth = traces.Count > 0 ? traces.Max(t => t.Trace.Width) : 0;
        double reach = options.MaxEdgeGapMeters + maxWidth;
        double cell = Math.Max(2e-3, 2 * reach);
        var grid = new Dictionary<(int Layer, long X, long Y), List<int>>();
        for (int i = 0; i < traces.Count; i++)
        {
            var t = traces[i].Trace;
            long x0 = (long)Math.Floor((Math.Min(t.Start.X, t.End.X) - reach) / cell);
            long x1 = (long)Math.Floor((Math.Max(t.Start.X, t.End.X) + reach) / cell);
            long y0 = (long)Math.Floor((Math.Min(t.Start.Y, t.End.Y) - reach) / cell);
            long y1 = (long)Math.Floor((Math.Max(t.Start.Y, t.End.Y) + reach) / cell);
            for (long x = x0; x <= x1; x++)
                for (long y = y0; y <= y1; y++)
                {
                    if (!grid.TryGetValue((t.LayerOrder, x, y), out var list))
                        grid[(t.LayerOrder, x, y)] = list = new List<int>();
                    list.Add(i);
                }
        }

        var pairs = new Dictionary<(int, int), PairData>();
        var seen = new HashSet<(int, int)>();
        foreach (var list in grid.OrderBy(g => g.Key.Layer).ThenBy(g => g.Key.X).ThenBy(g => g.Key.Y).Select(g => g.Value))
            for (int a = 0; a < list.Count; a++)
                for (int b = a + 1; b < list.Count; b++)
                {
                    int i = Math.Min(list[a], list[b]), j = Math.Max(list[a], list[b]);
                    var (ti, ni) = traces[i];
                    var (tj, nj) = traces[j];
                    if (ReferenceEquals(ni, nj) || !seen.Add((i, j))) continue;

                    var axis = (ti.End - ti.Start) * (1 / ti.Length);
                    var other = (tj.End - tj.Start) * (1 / tj.Length);
                    if (Math.Abs(Point2.Dot(axis, other)) < cosTolerance) continue;
                    var normal = new Point2(-axis.Y, axis.X);
                    double offset = Point2.Dot(tj.Midpoint - ti.Start, normal);
                    double gap = Math.Abs(offset) - (ti.Width + tj.Width) / 2;
                    if (gap <= 0 || gap > options.MaxEdgeGapMeters) continue;
                    double sA = Point2.Dot(tj.Start - ti.Start, axis), sB = Point2.Dot(tj.End - ti.Start, axis);
                    double lo = Math.Max(0, Math.Min(sA, sB)), hi = Math.Min(ti.Length, Math.Max(sA, sB));
                    if (hi - lo <= 0) continue;

                    // Lower net id first, so a pair has one orientation.
                    bool swap = ni.Id > nj.Id;
                    var first = swap ? nj : ni;
                    var second = swap ? ni : nj;
                    if (!pairs.TryGetValue((first.Id, second.Id), out var pair))
                        pairs[(first.Id, second.Id)] = pair = new PairData { A = first, B = second };
                    double wA = swap ? tj.Width : ti.Width, wB = swap ? ti.Width : tj.Width;

                    // Copper of a third net between the two, along most of this run: a guard.
                    double guardWidth = 0, guardFromA = 0;
                    CopperNet? guardNet = null;
                    double bestOverlap = 0;
                    foreach (int k in list)
                    {
                        if (k == i || k == j) continue;
                        var (tk, nk) = traces[k];
                        if (ReferenceEquals(nk, ni) || ReferenceEquals(nk, nj) || tk.LayerOrder != ti.LayerOrder) continue;
                        if (Math.Abs(Point2.Dot((tk.End - tk.Start) * (1 / tk.Length), axis)) < cosTolerance) continue;
                        double at = Point2.Dot(tk.Midpoint - ti.Start, normal);
                        // Inside the gap, edges and all, on the same side as tj.
                        if (Math.Sign(at) != Math.Sign(offset)) continue;
                        double near = Math.Abs(at) - tk.Width / 2, far = Math.Abs(at) + tk.Width / 2;
                        if (near <= ti.Width / 2 || far >= Math.Abs(offset) - tj.Width / 2) continue;
                        double kA = Point2.Dot(tk.Start - ti.Start, axis), kB = Point2.Dot(tk.End - ti.Start, axis);
                        double overlap = Math.Min(hi, Math.Max(kA, kB)) - Math.Max(lo, Math.Min(kA, kB));
                        if (overlap < 0.5 * (hi - lo) || overlap <= bestOverlap) continue;
                        bestOverlap = overlap;
                        guardWidth = tk.Width;
                        double fromTi = Math.Abs(at), pitchHere = Math.Abs(offset);
                        guardFromA = swap ? pitchHere - fromTi : fromTi;
                        guardNet = nk;
                    }

                    var key = (ti.LayerOrder, (long)Math.Round(wA * 1e6), (long)Math.Round(wB * 1e6),
                        (long)Math.Round(gap * 1e6 / 5) * 100000 + (long)Math.Round(guardWidth * 1e6) * 1000
                        + (long)Math.Round(guardFromA * 1e6 / 5));
                    if (!pair.Groups.TryGetValue(key, out var group))
                        pair.Groups[key] = group = new Group
                        {
                            Layer = ti.LayerOrder, WidthA = wA, WidthB = wB, Gap = gap,
                            GuardWidth = guardWidth, GuardFromA = guardFromA, GuardNet = guardNet
                        };
                    double length = hi - lo;
                    group.Length += length;
                    group.Gap = Math.Min(group.Gap, gap);
                    var middle = ti.Start + axis * (0.5 * (lo + hi));
                    if (length > group.LongestPiece) { group.LongestPiece = length; group.At = middle; }
                    if (group.Samples.Count < 64)
                    {
                        group.Samples.Add(ti.Start + axis * lo);
                        group.Samples.Add(middle);
                        group.Samples.Add(ti.Start + axis * hi);
                        group.Samples.Add(middle + normal * offset);
                    }
                }

        // ---- Broadside (FU-29): traces on two layers, one over the other or close beside it in
        // plan, through a grid without layers. Whether a plane lies between them is found with the
        // cross-section, below.
        var plan = new Dictionary<(long X, long Y), List<int>>();
        for (int i = 0; i < traces.Count; i++)
        {
            var t = traces[i].Trace;
            long x0 = (long)Math.Floor((Math.Min(t.Start.X, t.End.X) - reach) / cell);
            long x1 = (long)Math.Floor((Math.Max(t.Start.X, t.End.X) + reach) / cell);
            long y0 = (long)Math.Floor((Math.Min(t.Start.Y, t.End.Y) - reach) / cell);
            long y1 = (long)Math.Floor((Math.Max(t.Start.Y, t.End.Y) + reach) / cell);
            for (long x = x0; x <= x1; x++)
                for (long y = y0; y <= y1; y++)
                {
                    if (!plan.TryGetValue((x, y), out var list)) plan[(x, y)] = list = new List<int>();
                    list.Add(i);
                }
        }
        var seenAcross = new HashSet<(int, int)>();
        foreach (var list in plan.OrderBy(g => g.Key.X).ThenBy(g => g.Key.Y).Select(g => g.Value))
            for (int a = 0; a < list.Count; a++)
                for (int b = a + 1; b < list.Count; b++)
                {
                    int i = Math.Min(list[a], list[b]), j = Math.Max(list[a], list[b]);
                    var (ti, ni) = traces[i];
                    var (tj, nj) = traces[j];
                    if (ti.LayerOrder == tj.LayerOrder || ReferenceEquals(ni, nj) || !seenAcross.Add((i, j))) continue;
                    var axis = (ti.End - ti.Start) * (1 / ti.Length);
                    var other = (tj.End - tj.Start) * (1 / tj.Length);
                    if (Math.Abs(Point2.Dot(axis, other)) < cosTolerance) continue;
                    var normal = new Point2(-axis.Y, axis.X);
                    double offset = Point2.Dot(tj.Midpoint - ti.Start, normal);
                    double gap = Math.Abs(offset) - (ti.Width + tj.Width) / 2;    // in plan; < 0 overlaps
                    if (gap > options.MaxEdgeGapMeters) continue;
                    double sA = Point2.Dot(tj.Start - ti.Start, axis), sB = Point2.Dot(tj.End - ti.Start, axis);
                    double lo = Math.Max(0, Math.Min(sA, sB)), hi = Math.Min(ti.Length, Math.Max(sA, sB));
                    if (hi - lo <= 0) continue;

                    bool swap = ni.Id > nj.Id;
                    var first = swap ? nj : ni;
                    var second = swap ? ni : nj;
                    if (!pairs.TryGetValue((first.Id, second.Id), out var pair))
                        pairs[(first.Id, second.Id)] = pair = new PairData { A = first, B = second };
                    var (ta, tb) = swap ? (tj, ti) : (ti, tj);
                    // Two strips' cross-section depends on their offset only through its size.
                    var key = (ta.LayerOrder, tb.LayerOrder, (long)Math.Round(ta.Width * 1e6), (long)Math.Round(tb.Width * 1e6),
                        (long)Math.Round(Math.Abs(offset) * 1e6 / 5));
                    if (!pair.Broadside.TryGetValue(key, out var group))
                        pair.Broadside[key] = group = new Group
                        {
                            Layer = ta.LayerOrder, LayerB = tb.LayerOrder, WidthA = ta.Width, WidthB = tb.Width,
                            Gap = gap, Offset = Math.Abs(offset)
                        };
                    double length = hi - lo;
                    group.Length += length;
                    group.Gap = Math.Min(group.Gap, gap);
                    var middle = ti.Start + axis * (0.5 * (lo + hi));
                    if (length > group.LongestPiece) { group.LongestPiece = length; group.At = middle; }
                    if (group.Samples.Count < 64)
                    {
                        group.Samples.Add(ti.Start + axis * lo);
                        group.Samples.Add(middle);
                        group.Samples.Add(ti.Start + axis * hi);
                        group.Samples.Add(middle + normal * offset);
                    }
                }

        var candidates = pairs.Values
            .Where(p => p.Length >= options.MinParallelLengthMeters)
            // The geometric stand-in for coupling: length over the square of the gap in widths.
            .OrderByDescending(p => p.AllGroups.Sum(g =>
                g.Length / (1 + Math.Pow(Math.Max(g.Gap, 0) / Math.Max(g.WidthA, g.WidthB), 2))))
            .ThenBy(p => p.A.Id).ThenBy(p => p.B.Id)
            .ToList();
        int notSolved = Math.Max(0, candidates.Count - options.MaxSolvedPairs);

        // ---- Coupling per unit length, one field solve per distinct cross-section.
        var coupled = new BoardCoupledOptions
        {
            Stackup = options.Stackup, ConductivitySiemensPerMeter = options.ConductivitySiemensPerMeter
        };
        var stackup = BoardCoupledExtractor.StackupOf(board, coupled);
        var solved = new Dictionary<string, (double Near, double Far, double Delay)>();
        var mutualCapacitance = new Dictionary<string, double>();
        var result = new List<CrosstalkPair>();
        foreach (var pair in candidates.Take(options.MaxSolvedPairs))
        {
            var own = pair.A.Islands.Concat(pair.B.Islands).ToList();
            var runs = new List<CrosstalkRun>();
            string? failure = null;
            foreach (var group in pair.Groups.Values.OrderByDescending(g => g.Length))
            {
                var substrate = BoardReferencePlanes.Resolve(board, group.Layer, group.Samples, own, coupled,
                    out string planeFailure);
                if (substrate is null) { failure ??= planeFailure; continue; }
                bool guardIsGround = false;
                if (group.GuardNet is { } guardNet)
                {
                    var reference = BoardCoplanarGround.ReferenceNets(board, substrate.PlaneLayers, group.Samples);
                    guardIsGround = reference.Nets.Contains(guardNet)
                        || (!string.IsNullOrWhiteSpace(guardNet.Name) && reference.Names.Contains(guardNet.Name));
                }
                string key = string.Join("|", substrate.Stackup.Layers.Select(l =>
                        FormattableString.Invariant($"{l.RelativePermittivity:r},{l.ThicknessMeters:r}")))
                    + FormattableString.Invariant(
                        $"|{substrate.MetalInterface}|{substrate.TopGround}|{group.WidthA:r}|{group.WidthB:r}|{Math.Round(group.Gap * 1e6 / 5)}|{stackup.CopperThicknessOf(group.Layer):r}")
                    + FormattableString.Invariant($"|{group.GuardWidth:r}|{Math.Round(group.GuardFromA * 1e6 / 5)}");
                if (!solved.TryGetValue(key, out var k))
                {
                    double thickness = stackup.CopperThicknessOf(group.Layer);
                    double pitch = group.Gap + (group.WidthA + group.WidthB) / 2;
                    var traceList = new List<TraceCrossSection>
                    {
                        new TraceCrossSection(-pitch / 2, group.WidthA, thickness, options.ConductivitySiemensPerMeter),
                        new TraceCrossSection(pitch / 2, group.WidthB, thickness, options.ConductivitySiemensPerMeter),
                    };
                    // A guard is solved as a conductor and then held quiet: tied to the reference
                    // along its length, which a stitched ground guard is, and which a terminated
                    // neighbouring signal nearly is for the coupling between the other two.
                    if (group.GuardWidth > 0)
                        traceList.Add(new TraceCrossSection(-pitch / 2 + group.GuardFromA, group.GuardWidth, thickness,
                            options.ConductivitySiemensPerMeter) { IsGround = true });
                    var section = new CoupledLineCrossSection(substrate.Stackup, substrate.MetalInterface, traceList,
                        substrate.TopGround);
                    var rlgc = BoardCoupledResult.Reduce(section, RlgcExtractor.Extract(section, Model));
                    solved[key] = k = Coefficients(rlgc);
                    mutualCapacitance[key] = -rlgc.CapacitanceFaradsPerMeter[0, 1];
                }
                runs.Add(new CrosstalkRun(group.Layer, group.WidthA, group.WidthB, group.Gap, group.Length,
                    k.Near, k.Far, k.Delay, mutualCapacitance[key])
                {
                    GuardWidthMeters = group.GuardWidth, GuardIsGround = guardIsGround
                });
            }

            // Broadside runs: the two traces on their own interfaces of the cross-section the upper
            // one sits in. A reference plane between their layers shields them, and the run is not
            // a coupling at all.
            var shielded = new List<Group>();
            foreach (var group in pair.Broadside.Values.OrderByDescending(g => g.Length))
            {
                int upper = Math.Min(group.Layer, group.LayerB), lower = Math.Max(group.Layer, group.LayerB);
                var substrate = BoardReferencePlanes.Resolve(board, upper, group.Samples, own, coupled,
                    out string planeFailure);
                if (substrate is null) { failure ??= planeFailure; continue; }
                int interfaceA = IndexOf(substrate.InterfaceLayers, group.Layer);
                int interfaceB = IndexOf(substrate.InterfaceLayers, group.LayerB);
                if (substrate.PlaneLayers.Any(p => p > upper && p < lower) || interfaceA < 0 || interfaceB < 0)
                {
                    shielded.Add(group);
                    continue;
                }
                string key = "B|" + string.Join("|", substrate.Stackup.Layers.Select(l =>
                        FormattableString.Invariant($"{l.RelativePermittivity:r},{l.ThicknessMeters:r}")))
                    + FormattableString.Invariant(
                        $"|{interfaceA}|{interfaceB}|{substrate.TopGround}|{group.WidthA:r}|{group.WidthB:r}|{Math.Round(group.Offset * 1e6 / 5)}");
                if (!solved.TryGetValue(key, out var k))
                {
                    var rlgc = BroadsideExtractor.Extract(substrate.Stackup, substrate.TopGround, new[]
                    {
                        new LayeredTrace(new TraceCrossSection(0, group.WidthA, stackup.CopperThicknessOf(group.Layer),
                            options.ConductivitySiemensPerMeter), interfaceA),
                        new LayeredTrace(new TraceCrossSection(group.Offset, group.WidthB, stackup.CopperThicknessOf(group.LayerB),
                            options.ConductivitySiemensPerMeter), interfaceB),
                    });
                    solved[key] = k = Coefficients(rlgc);
                    mutualCapacitance[key] = -rlgc.CapacitanceFaradsPerMeter[0, 1];
                }
                runs.Add(new CrosstalkRun(group.Layer, group.WidthA, group.WidthB, group.Gap, group.Length,
                    k.Near, k.Far, k.Delay, mutualCapacitance[key]) { LayerB = group.LayerB });
            }
            // Met only through a plane: the scan has nothing to report for this pair.
            var reported = pair.AllGroups.Where(g => !shielded.Contains(g)).ToList();
            if (reported.Count == 0 || reported.Sum(g => g.Length) < options.MinParallelLengthMeters) continue;

            var longest = reported.MaxBy(g => g.LongestPiece)!;
            string nameA = BoardLayoutIndex.NameOf(pair.A), nameB = BoardLayoutIndex.NameOf(pair.B);
            double tr = options.RiseTimeSeconds;
            if (runs.Count == 0)
            {
                // Nothing priced: list the geometry with the reason.
                runs.AddRange(reported.Select(g => new CrosstalkRun(g.Layer, g.WidthA, g.WidthB, g.Gap,
                    g.Length, 0, 0, 0) { LayerB = g.LayerB }));
            }
            result.Add(new CrosstalkPair
            {
                NetA = nameA,
                NetB = nameB,
                Runs = runs,
                ParallelLengthMeters = reported.Sum(g => g.Length),
                MinEdgeGapMeters = reported.Min(g => g.Gap),
                NearEnd = runs.Max(r => r.NearEndCoupling
                    * Math.Min(1, 2 * r.LengthMeters * r.DelaySecondsPerMeter / tr)),
                FarEnd = Math.Abs(runs.Sum(r => r.FarEndCouplingSecondsPerMeter * r.LengthMeters)) / tr,
                At = longest.At,
                LikelyDifferentialPair = LooksLikeAPair(pair.A.Name, pair.B.Name),
                NotPriced = runs.All(r => r.DelaySecondsPerMeter == 0) ? failure : null
            });
        }

        var notes = new List<string>
        {
            $"Crosstalk as a fraction of the aggressor's swing for a {options.RiseTimeSeconds * 1e9:g3} ns edge, "
                + "both lines matched at both ends; a mismatched end reflects and can double it.",
            "Each pair is solved as its two traces over their reference plane(s), with the copper of a third "
                + "net that runs between them along most of a run (a guard) solved too and held quiet: exact for a "
                + "ground guard stitched to the plane, close for a terminated signal trace. Other neighbours are "
                + "not in the solve.",
            $"Traces within {options.AngleToleranceDegrees:g2}° of parallel are scanned: on one layer within "
                + $"{options.MaxEdgeGapMeters * 1e3:g3} mm edge to edge, and on two layers with no reference plane "
                + $"between them where they overlap or come within {options.MaxEdgeGapMeters * 1e3:g3} mm in plan "
                + "(broadside, solved with each trace on its own interface of the stack)"
                + (options.ViaCoupling ? "." : "; coupling between vias is not."),
        };
        if (notSolved > 0)
            notes.Add($"{notSolved} more pair(s) run together but were not priced: the scan solves the "
                + $"{options.MaxSolvedPairs} most closely coupled by geometry.");
        var viaPairs = Array.Empty<ViaCouplingPair>() as IReadOnlyList<ViaCouplingPair>;
        if (options.ViaCoupling)
        {
            var vias = ViaCouplingScan.Run(index, options.Stackup ?? BoardStackup.FromBoard(board),
                options.RiseTimeSeconds, options.ViaReferenceOhms);
            viaPairs = vias.Pairs;
            notes.AddRange(vias.Notes);
        }
        return new CrosstalkScanReport(
            result.OrderBy(p => p.NotPriced is null ? 0 : 1).ThenByDescending(p => p.Worst)
                .ThenBy(p => p.NetA, StringComparer.Ordinal).ThenBy(p => p.NetB, StringComparer.Ordinal).ToList(),
            notSolved, notes) { ViaPairs = viaPairs };
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == value) return i;
        return -1;
    }

    /// <summary>K_b, K_f and the delay of a two-line extraction, with the geometric means of
    /// the two lines' own L and C where they differ in width.</summary>
    internal static (double Near, double Far, double Delay) Coefficients(RlgcResult rlgc)
    {
        var l = rlgc.InductanceHenriesPerMeter;
        var c = rlgc.CapacitanceFaradsPerMeter;
        double lOwn = Math.Sqrt(l[0, 0] * l[1, 1]), cOwn = Math.Sqrt(c[0, 0] * c[1, 1]);
        double lMutual = l[0, 1], cMutual = -c[0, 1];
        double z0 = Math.Sqrt(lOwn / cOwn);
        return (0.25 * (cMutual / cOwn + lMutual / lOwn), 0.5 * (cMutual * z0 - lMutual / z0),
            Math.Sqrt(lOwn * cOwn));
    }

    /// <summary>Names that differ in exactly one place, by P/N or +/−.</summary>
    internal static bool LooksLikeAPair(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length || a.Length < 2) return false;
        int at = -1;
        for (int i = 0; i < a.Length; i++)
        {
            if (char.ToUpperInvariant(a[i]) == char.ToUpperInvariant(b[i])) continue;
            if (at >= 0) return false;
            at = i;
        }
        if (at < 0) return false;
        char x = char.ToUpperInvariant(a[at]), y = char.ToUpperInvariant(b[at]);
        return (x, y) is ('P', 'N') or ('N', 'P') or ('+', '-') or ('-', '+');
    }
}
