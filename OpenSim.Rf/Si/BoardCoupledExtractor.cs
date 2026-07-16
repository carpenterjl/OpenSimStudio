using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Si;

/// <summary>Knobs for <see cref="BoardCoupledExtractor"/>. Conductor material and the
/// dielectric fallbacks used when the board carries no per-gap stackup data.</summary>
public sealed record BoardCoupledOptions
{
    /// <summary>Copper thickness of the traces [m] (sets R only — the C/L solve is
    /// zero-thickness). Default 35 µm (1 oz).</summary>
    public double CopperThicknessMeters { get; init; } = 35e-6;

    /// <summary>Trace conductivity [S/m]; default annealed copper.</summary>
    public double ConductivitySiemensPerMeter { get; init; } = 5.8e7;

    /// <summary>Two principal runs count as parallel when their directions agree within
    /// this angle. Real coupled traces are drawn exactly parallel, so a few degrees is a
    /// generous-but-safe cut; beyond it the "uniform coupled section" model is dishonest.</summary>
    public double AngleToleranceDegrees { get; init; } = 5;

    /// <summary>Substrate εr when the board has no per-gap permittivity (Gerber sets).</summary>
    public double DefaultEpsR { get; init; } = 4.4;

    /// <summary>Substrate tanδ when the board has no per-gap loss tangent.</summary>
    public double DefaultTanD { get; init; } = 0.02;

    /// <summary>Total board thickness [m], split across gaps, when the board has no
    /// per-gap thickness list. Default 1.6 mm.</summary>
    public double DefaultBoardThicknessMeters { get; init; } = 1.6e-3;
}

/// <summary>
/// The coupled cross-section extracted from a set of real board nets, plus the network it
/// composes into — or a typed failure naming the non-conforming topology. Exactly one of
/// <see cref="CrossSection"/> / <see cref="FailureReason"/> is non-null.
/// </summary>
public sealed record BoardCoupledResult(
    CoupledLineCrossSection? CrossSection,
    RlgcResult? Rlgc,
    MtlNetwork? Network,
    double CoupledLengthMeters,
    IReadOnlyList<double> LeadLengthsMeters,
    IReadOnlyList<string> Assumptions,
    string? FailureReason)
{
    public static BoardCoupledResult Failure(string reason) =>
        new(null, null, null, 0, Array.Empty<double>(), Array.Empty<string>(), reason);
}

/// <summary>
/// SI Stage S6 — the board multi-trace bridge. Takes 2+ selected copper nets, reduces each
/// to its dominant straight run (the routed trace = pad jogs → a long parallel run → pad
/// jogs; the long run is the coupled line), requires the runs to be mutually parallel and
/// laterally separated, and reads off the uniform coupled cross-section: trace widths from
/// the draws, lateral centers from the perpendicular offsets, the substrate from the
/// dielectric gap adjacent to the trace layer. The coupled SECTION length is the runs'
/// longitudinal overlap; the non-overlapping tails are reported as leads. The cross-section
/// feeds the same <see cref="RlgcExtractor"/> / <see cref="MtlNetwork"/> the wizard uses —
/// so a synthetic two-trace board round-trips to the wizard geometry's RLGC exactly.
///
/// <para>Non-conforming topologies are typed failures, never a garbage matrix: a pour/region
/// net (no centerlines), a net that branches or changes layer (surfaced verbatim from
/// <see cref="TraceChainBuilder"/>), runs that are not parallel, runs that do not overlap
/// longitudinally, or conductors that overlap laterally (a broadside pair or one net drawn
/// twice). The coplanar-at-one-interface contract is the whole layered track's; broadside
/// coupling across layers cannot be expressed by construction.</para>
/// </summary>
public static class BoardCoupledExtractor
{
    /// <summary>A conductor's dominant straight run on the board: endpoints A → B at the
    /// trace layer, the representative width, and the total routed path length (leads are
    /// path minus the coupled overlap).</summary>
    private readonly record struct PrincipalRun(Point2 A, Point2 B, double Width, double PathLength)
    {
        public Point2 Delta => B - A;
        public double Length => Delta.Length;
        public Point2 Midpoint => (A + B) * 0.5;
    }

    public static BoardCoupledResult Extract(PcbBoard board, IReadOnlyList<CopperNet> nets,
        BoardCoupledOptions? options = null)
    {
        options ??= new BoardCoupledOptions();
        if (nets is null || nets.Count < 2)
            return BoardCoupledResult.Failure(
                "a coupled-line extraction needs at least two selected nets.");

        // Reduce every net to its principal run and pin the common trace layer.
        var runs = new PrincipalRun[nets.Count];
        int layer = -1;
        for (int i = 0; i < nets.Count; i++)
        {
            var centerlines = NetTraceExtractor.ForNet(board, nets[i]);
            if (centerlines.Count == 0)
                return BoardCoupledResult.Failure(
                    $"net '{nets[i].Label}' has no trace centerlines (a pour/region net cannot "
                    + "be a coupled line — select routed signal nets).");

            var chain = TraceChainBuilder.Build(centerlines);
            if (chain.Chain is null)
                return BoardCoupledResult.Failure($"net '{nets[i].Label}': {chain.FailureReason}");

            int netLayer = chain.Chain[0].LayerOrder;
            if (layer < 0) layer = netLayer;
            else if (netLayer != layer)
                return BoardCoupledResult.Failure(
                    $"net '{nets[i].Label}' routes on layer L{netLayer} but the others are on "
                    + $"L{layer} — a coplanar coupled line needs every conductor on one layer.");

            runs[i] = PrincipalRunOf(chain.Chain, options.AngleToleranceDegrees);
        }

        // Common axis = the longest run's direction (ties → input order, deterministic).
        int reference = 0;
        for (int i = 1; i < runs.Length; i++)
            if (runs[i].Length > runs[reference].Length) reference = i;
        var refRun = runs[reference];
        if (refRun.Length <= 0)
            return BoardCoupledResult.Failure("the selected nets have no straight run to couple.");
        var axis = refRun.Delta * (1.0 / refRun.Length);
        var perp = new Point2(-axis.Y, axis.X);
        var origin = refRun.A;
        double cosTol = Math.Cos(options.AngleToleranceDegrees * Math.PI / 180.0);

        // Parallelism + longitudinal projection of every run onto the axis.
        var loS = new double[runs.Length];
        var hiS = new double[runs.Length];
        var offset = new double[runs.Length];
        for (int i = 0; i < runs.Length; i++)
        {
            var dir = runs[i].Delta * (1.0 / runs[i].Length);
            if (Math.Abs(Point2.Dot(dir, axis)) < cosTol)
            {
                double deg = Math.Acos(Math.Clamp(Math.Abs(Point2.Dot(dir, axis)), -1, 1)) * 180 / Math.PI;
                return BoardCoupledResult.Failure(
                    $"net '{nets[i].Label}' runs {deg:g3}° off the others — a coupled-line model "
                    + "requires parallel conductors (this is a non-parallel tangle).");
            }
            double sA = Point2.Dot(runs[i].A - origin, axis);
            double sB = Point2.Dot(runs[i].B - origin, axis);
            loS[i] = Math.Min(sA, sB);
            hiS[i] = Math.Max(sA, sB);
            offset[i] = Point2.Dot(runs[i].Midpoint - origin, perp);
        }

        double coupledLo = loS.Max();
        double coupledHi = hiS.Min();
        double coupledLength = coupledHi - coupledLo;
        double narrowest = runs.Min(r => r.Width);
        if (coupledLength <= narrowest)      // sub-width overlap is not a coupled run
            return BoardCoupledResult.Failure(
                "the selected nets do not share a common parallel run (no longitudinal "
                + "overlap) — they may connect end-to-end rather than run side by side.");

        // Lateral ordering: sort by perpendicular offset, forbid overlap (edge gap > 0).
        var order = Enumerable.Range(0, runs.Length).OrderBy(i => offset[i]).ToArray();
        for (int k = 1; k < order.Length; k++)
        {
            int a = order[k - 1], b = order[k];
            double edgeGap = (offset[b] - runs[b].Width / 2) - (offset[a] + runs[a].Width / 2);
            if (edgeGap <= 0)
                return BoardCoupledResult.Failure(
                    $"nets '{nets[a].Label}' and '{nets[b].Label}' overlap laterally (edge gap "
                    + $"{edgeGap * 1e6:g3} µm) — a broadside pair or one net drawn twice, not a "
                    + "coplanar coupled line.");
        }

        // The substrate: the dielectric gap adjacent to the trace layer (below preferred).
        var substrate = ResolveSubstrate(board, layer, options, out string dielectricNote);
        if (substrate is null)
            return BoardCoupledResult.Failure(
                $"no dielectric gap is adjacent to the trace layer L{layer} — a microstrip "
                + "cross-section needs a reference plane above or below the traces.");

        var traces = new TraceCrossSection[runs.Length];
        for (int i = 0; i < runs.Length; i++)
            traces[i] = new TraceCrossSection(offset[i], runs[i].Width,
                options.CopperThicknessMeters, options.ConductivitySiemensPerMeter);

        CoupledLineCrossSection section;
        try { section = new CoupledLineCrossSection(substrate, 0, traces); }
        catch (ArgumentException ex) { return BoardCoupledResult.Failure(ex.Message); }

        var rlgc = RlgcExtractor.Extract(section);
        var network = new MtlNetwork(new[] { new MtlSection(rlgc, coupledLength) });

        var leads = new double[runs.Length];
        for (int i = 0; i < runs.Length; i++)
            leads[i] = Math.Max(0, runs[i].PathLength - coupledLength);

        var assumptions = new List<string>(rlgc.Assumptions)
        {
            dielectricNote,
            $"Coupled section = the {coupledLength * 1e3:g4} mm longitudinal overlap of the nets' "
                + "parallel runs; the non-overlapping tails ("
                + string.Join(", ", leads.Select(l => $"{l * 1e3:g3} mm")) + ") are reported as "
                + "leads but not cascaded — their series R+L and the bends are a named refinement.",
        };

        return new BoardCoupledResult(section, rlgc, network, coupledLength, leads,
            assumptions, null);
    }

    /// <summary>The longest straight run of an ordered (head-to-tail) chain: consecutive
    /// near-collinear segments are merged, and the run with the greatest straight A→B extent
    /// wins. Its path length (the summed segment lengths) is kept so the leads can be sized.</summary>
    private static PrincipalRun PrincipalRunOf(IReadOnlyList<TraceCenterline> chain, double angleToleranceDegrees)
    {
        double cosTol = Math.Cos(angleToleranceDegrees * Math.PI / 180.0);

        PrincipalRun best = default;
        double bestExtent = -1;

        int start = 0;
        while (start < chain.Count)
        {
            var runDir = Unit(chain[start].End - chain[start].Start);
            double path = chain[start].Length;
            int end = start;
            for (int j = start + 1; j < chain.Count; j++)
            {
                var d = Unit(chain[j].End - chain[j].Start);
                if (Math.Abs(Point2.Dot(d, runDir)) < cosTol) break;
                path += chain[j].Length;
                end = j;
            }

            var a = chain[start].Start;
            var b = chain[end].End;
            double extent = (b - a).Length;
            if (extent > bestExtent)
            {
                // Representative width: the widest segment in the run (the coupled line's
                // dominant conductor; jog widths rarely differ, but favour the main trace).
                double width = 0;
                for (int j = start; j <= end; j++) width = Math.Max(width, chain[j].Width);
                best = new PrincipalRun(a, b, width, path);
                bestExtent = extent;
            }
            start = end + 1;
        }
        return best;
    }

    private static Point2 Unit(Point2 v)
    {
        double l = v.Length;
        return l > 0 ? v * (1.0 / l) : new Point2(1, 0);
    }

    /// <summary>The single-slab substrate beneath (or above) the trace layer, from the board's
    /// per-gap stackup data when present, else the option defaults. Returns null when the trace
    /// layer has no adjacent dielectric gap (a one-layer board has no reference plane).
    /// Internal — <see cref="TraceCapacitanceExtractor"/> shares this exact gap-below-then-above
    /// rule; a second implementation would drift.</summary>
    internal static LayeredStackup? ResolveSubstrate(PcbBoard board, int layer,
        BoardCoupledOptions options, out string note)
    {
        var stackup = board.Stackup;
        int numGaps = stackup is not null && stackup.DielectricGapThicknesses.Count > 0
            ? stackup.DielectricGapThicknesses.Count
            : Math.Max(0, MaxCopperOrder(board) - 1);

        // Gap index i sits between copper orders i+1 and i+2. Prefer the gap BELOW the
        // trace layer (index layer-1); fall back to the gap above (index layer-2).
        int gapIndex = -1;
        bool below = false;
        if (layer - 1 >= 0 && layer - 1 < numGaps) { gapIndex = layer - 1; below = true; }
        else if (layer - 2 >= 0 && layer - 2 < numGaps) { gapIndex = layer - 2; }
        if (gapIndex < 0) { note = ""; return null; }

        double h = options.DefaultBoardThicknessMeters / Math.Max(1, numGaps);
        double epsR = options.DefaultEpsR, tanD = options.DefaultTanD;
        bool fromFile = false;
        if (stackup is not null)
        {
            if (gapIndex < stackup.DielectricGapThicknesses.Count)
            { h = stackup.DielectricGapThicknesses[gapIndex]; fromFile = true; }
            if (gapIndex < stackup.DielectricGapPermittivities.Count)
                epsR = stackup.DielectricGapPermittivities[gapIndex];
            if (gapIndex < stackup.DielectricGapLossTangents.Count)
                tanD = stackup.DielectricGapLossTangents[gapIndex];
        }

        note = $"Substrate = the dielectric gap {(below ? "below" : "above")} trace layer L{layer} "
            + $"({(fromFile ? "from the board stackup" : "default")}: εr {epsR:g3}, tanδ {tanD:g3}, "
            + $"h {h * 1e3:g3} mm); the reference plane is the adjacent copper layer, and coupling "
            + "to any other layer is out of scope by construction.";
        return new LayeredStackup(new[] { new LayeredStackup.Layer(epsR, tanD, h) });
    }

    private static int MaxCopperOrder(PcbBoard board) =>
        board.Islands.Count > 0 ? board.Islands.Max(i => i.LayerOrder) : 1;
}
