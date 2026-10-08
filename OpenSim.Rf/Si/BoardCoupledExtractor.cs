using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Si;

/// <summary>Knobs for <see cref="BoardCoupledExtractor"/>. Conductor material and the
/// dielectric fallbacks used when the board carries no per-gap stackup data.</summary>
public sealed record BoardCoupledOptions
{
    /// <summary>
    /// The stackup to extract over: the trace layer's own copper thickness and the
    /// adjacent gap's thickness, εr and tanδ. The application passes the stackup panel's
    /// (<see cref="NetMeshOptions.Stackup"/>), the same object the mesher and the
    /// inductance chain read. Null ⇒ the board file's stackup, completed with
    /// <see cref="CopperThicknessMeters"/> and the Default* values below.
    /// </summary>
    public BoardStackup? Stackup { get; init; }

    /// <summary>Copper thickness of the traces [m] when no <see cref="Stackup"/> is given
    /// and the board file lists none for the layer (sets R only — the C/L solve is
    /// zero-thickness). Default 35 µm (1 oz).</summary>
    public double CopperThicknessMeters { get; init; } = 35e-6;

    /// <summary>Trace conductivity [S/m]; default annealed copper.</summary>
    public double ConductivitySiemensPerMeter { get; init; } = 5.8e7;

    /// <summary>Two runs count as parallel when their directions agree within this angle.
    /// Real coupled traces are drawn exactly parallel, so a few degrees is a
    /// generous-but-safe cut; beyond it the "uniform coupled section" model is dishonest.</summary>
    public double AngleToleranceDegrees { get; init; } = 5;

    /// <summary>Runs further apart than this (centre to centre) are treated as uncoupled
    /// even when parallel: that stretch of each net is a single-line lead. Default 5 mm —
    /// tens of substrate heights on any ordinary board, where the coupling is far below
    /// the 2D model's own accuracy.</summary>
    public double CouplingSpacingLimitMeters { get; init; } = 5e-3;

    /// <summary>Per selected net, a point at (or nearest) its DRIVER pin; the route end
    /// nearer to it is that net's driven end. The FIRST net's driven end defines the
    /// network's near end. Null (or a null entry) ⇒ the first net's near end is where its
    /// ordered chain starts, and the other nets are assumed driven from the same side.</summary>
    public IReadOnlyList<Point2?>? DriverPoints { get; init; }

    /// <summary>Exchange the near and far ends of the whole network (drive the first net
    /// from its other end). Ignored for the first net when <see cref="DriverPoints"/>
    /// names its driver.</summary>
    public bool SwapEnds { get; init; }

    /// <summary>The per-unit-length model every section is extracted with. The default is the
    /// bare kernel (zero-thickness strips, forward-conductor loss only, constant ε), which is
    /// what the extractor's geometric gates are written against; a caller that wants numbers
    /// for a real board passes <see cref="RlgcModel.Board"/>, as the app does.</summary>
    public RlgcModel Model { get; init; } = RlgcModel.Kernel;

    /// <summary>Carry ground copper on the trace layer beside a coupled stretch as coplanar
    /// ground strips (see <see cref="BoardCoplanarGround"/>). On by default; off solves the
    /// traces over their plane(s) alone, as before.</summary>
    public bool CoplanarGround { get; init; } = true;

    /// <summary>Substrate εr when the board has no per-gap permittivity (Gerber sets).</summary>
    public double DefaultEpsR { get; init; } = 4.4;

    /// <summary>Substrate tanδ when the board has no per-gap loss tangent.</summary>
    public double DefaultTanD { get; init; } = 0.02;

    /// <summary>Total board thickness [m], split across gaps, when the board has no
    /// per-gap thickness list. Default 1.6 mm.</summary>
    public double DefaultBoardThicknessMeters { get; init; } = 1.6e-3;
}

/// <summary>One section of a board route's cascade, in near → far order: a coupled
/// stretch (<see cref="Coupled"/> over <see cref="CoupledLengthMeters"/>), or an uncoupled
/// stretch where every net runs alone over its own length (<see cref="LeadLengthsMeters"/>).</summary>
public sealed record BoardRouteSection(
    CoupledLineCrossSection? Coupled, double CoupledLengthMeters,
    IReadOnlyList<double>? LeadLengthsMeters);

/// <summary>
/// The coupled cross-section extracted from a set of real board nets, plus the network it
/// composes into — or a typed failure naming the non-conforming topology. Exactly one of
/// <see cref="CrossSection"/> / <see cref="FailureReason"/> is non-null.
/// <see cref="CrossSection"/> / <see cref="Rlgc"/> are the LONGEST coupled stretch's;
/// <see cref="CoupledLengthMeters"/> is the summed length of every coupled stretch and
/// <see cref="LeadLengthsMeters"/> each net's routed length outside them.
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

    /// <summary>The cascade, near → far: leads and coupled stretches in the order the
    /// routes traverse them.</summary>
    public IReadOnlyList<BoardRouteSection> Sections { get; init; } = Array.Empty<BoardRouteSection>();

    /// <summary>Per net, the single-trace cross-section its uncoupled stretches are priced with.</summary>
    public IReadOnlyList<CoupledLineCrossSection> LeadCrossSections { get; init; }
        = Array.Empty<CoupledLineCrossSection>();

    /// <summary>Per net, the routed (pin-to-pin chain) length; all of it is in the network.</summary>
    public IReadOnlyList<double> RoutedLengthsMeters { get; init; } = Array.Empty<double>();

    /// <summary>Per net, the board point of its near-end / far-end port.</summary>
    public IReadOnlyList<Point2> NearEnds { get; init; } = Array.Empty<Point2>();
    public IReadOnlyList<Point2> FarEnds { get; init; } = Array.Empty<Point2>();

    /// <summary>Per net, false when <see cref="BoardCoupledOptions.DriverPoints"/> places
    /// its driver at the network's FAR end (it is driven against the first net).</summary>
    public IReadOnlyList<bool> DriverAtNearEnd { get; init; } = Array.Empty<bool>();

    /// <summary>
    /// Compose the same cascade with a caller-supplied per-unit-length extraction — the
    /// way to attach frequency-dependent R(f)/L(f) providers (proximity effect) to EVERY
    /// section, leads included, instead of rebuilding one section and losing the rest.
    /// Called once per distinct cross-section. With <see cref="RlgcExtractor.Extract"/>
    /// it reproduces <see cref="Network"/>.
    /// </summary>
    public MtlNetwork BuildNetwork(Func<CoupledLineCrossSection, RlgcResult> extract)
    {
        if (Sections.Count == 0)
            throw new InvalidOperationException("A failed extraction has no network.");
        var cache = new Dictionary<CoupledLineCrossSection, RlgcResult>(ReferenceEqualityComparer.Instance);
        RlgcResult Of(CoupledLineCrossSection s) =>
            cache.TryGetValue(s, out var r) ? r : cache[s] = Reduce(s, extract(s));

        var sections = new List<MtlSectionBase>();
        foreach (var s in Sections)
        {
            if (s.Coupled is not null)
                sections.Add(new MtlSection(Of(s.Coupled), s.CoupledLengthMeters));
            else
                sections.Add(new MtlLeadSection(s.LeadLengthsMeters!
                    .Select((l, i) => (Of(LeadCrossSections[i]), l))
                    .ToArray()));
        }
        return new MtlNetwork(sections);
    }

    /// <summary>A section's extraction with its coplanar ground strips tied to the reference,
    /// so only the signal traces are ports. A section without ground strips is returned as is.
    /// <see cref="BuildNetwork"/> applies it to what its extraction returns.</summary>
    public static RlgcResult Reduce(CoupledLineCrossSection section, RlgcResult full) =>
        section.GroundIndices.Count == 0 ? full : RlgcReduction.GroundConductors(full, section.GroundIndices);
}

/// <summary>
/// SI Stage S6 — the board multi-trace bridge. Takes 2+ selected copper nets and walks each
/// net's WHOLE ordered route (pin to pin). Every stretch where all the selected nets run
/// parallel within a spacing limit becomes a coupled section — widths from the draws,
/// lateral centres from the perpendicular offsets, the substrate from the reference planes
/// the board actually has above and below (<see cref="BoardReferencePlanes"/>: microstrip,
/// embedded microstrip or stripline — or a refusal when no layer has copper under them).
/// Everything else on each route — pad escapes, bends, the legs of an L or Z, a stretch
/// where only some of the nets run together — is an uncoupled single-line lead of that
/// net's own length, cascaded in route order. No routed length is left out. Every section
/// uses the same <see cref="RlgcExtractor"/> / <see cref="MtlNetwork"/> the wizard uses —
/// so a synthetic two-trace board round-trips to the wizard geometry's RLGC exactly.
///
/// <para>The network's near end is the first net's driven end
/// (<see cref="BoardCoupledOptions.DriverPoints"/>, else where its chain starts); the
/// other nets' near ends are the ends on the same side of the coupled stretches.</para>
///
/// <para>Non-conforming topologies are typed failures, never a garbage matrix: a pour/region
/// net (no centerlines), a net that branches or changes layer (surfaced verbatim from
/// <see cref="TraceChainBuilder"/>), nets with no parallel run in common, or conductors
/// that overlap laterally (a broadside pair or one net drawn twice). The
/// coplanar-at-one-interface contract is the whole layered track's; broadside coupling
/// across layers cannot be expressed by construction.</para>
/// </summary>
public static class BoardCoupledExtractor
{
    /// <summary>A straight run of a route: consecutive near-collinear chain segments merged.
    /// A → B in route order; the path coordinate of A and the summed segment length.</summary>
    private sealed record Run(Point2 A, Point2 B, double Width, double PathStart, double PathLength)
    {
        public double Extent => (B - A).Length;
    }

    private sealed class Route
    {
        public required List<TraceCenterline> Chain;
        public required List<Run> Runs;
        public double Length;
        public double DominantWidth;
        public bool MixedWidths;
    }

    /// <summary>A stretch where every net runs parallel: per net the route's path
    /// coordinates at its two ends (Lo at the first net's upstream end), the lateral
    /// offset from the first net's run, and the run's width.</summary>
    private sealed class Stretch
    {
        public double Length;
        public required double[] PathLo, PathHi, Offset, Width;
        public required TraceCenterline[] Pieces;
    }

    public static BoardCoupledResult Extract(PcbBoard board, IReadOnlyList<CopperNet> nets,
        BoardCoupledOptions? options = null)
    {
        options ??= new BoardCoupledOptions();
        if (nets is null || nets.Count < 2)
            return BoardCoupledResult.Failure(
                "a coupled-line extraction needs at least two selected nets.");
        int n = nets.Count;
        double cosTol = Math.Cos(options.AngleToleranceDegrees * Math.PI / 180.0);

        // Every net's ordered route and the common trace layer.
        var routes = new Route[n];
        var drawnLength = new double[n];
        int layer = -1;
        for (int i = 0; i < n; i++)
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

            var ordered = chain.Chain.ToList();
            if (i == 0 && StartsAtFarEnd(ordered, options, 0)) ordered = Reversed(ordered);
            routes[i] = RouteOf(ordered, cosTol);
            drawnLength[i] = centerlines.Sum(c => c.Length);
        }

        // Stretches where ALL nets run parallel, found along each run of the first net.
        var stretches = FindStretches(routes, cosTol, options.CouplingSpacingLimitMeters);
        if (stretches.Count == 0)
            return BoardCoupledResult.Failure(NoCommonRunReason(nets, routes, cosTol, options));

        // Orient every other net the way the first net traverses the LONGEST stretch: a
        // net whose path coordinate falls along it is walked from its other end.
        var longest = stretches.MaxBy(s => s.Length)!;
        for (int i = 1; i < n; i++)
        {
            if (longest.PathLo[i] <= longest.PathHi[i]) continue;
            double total = routes[i].Length;
            foreach (var s in stretches)
            {
                s.PathLo[i] = total - s.PathLo[i];
                s.PathHi[i] = total - s.PathHi[i];
            }
            routes[i] = RouteOf(Reversed(routes[i].Chain), cosTol);
        }

        // Keep the stretches every net meets in the same order and the same sense (longest
        // first); the rest — a fold-back, or stretches two nets meet in opposite order —
        // cannot sit in one cascade and stay in the leads as uncoupled length.
        const double slack = 1e-9;
        var kept = new List<Stretch>();
        double droppedCoupling = 0;
        foreach (var s in stretches.OrderByDescending(s => s.Length))
        {
            bool ok = Enumerable.Range(0, n).All(i => s.PathHi[i] > s.PathLo[i]);
            foreach (var k in kept)
            {
                if (!ok) break;
                bool before = s.PathHi[0] <= k.PathLo[0] + slack;
                bool after = s.PathLo[0] >= k.PathHi[0] - slack;
                if (!before && !after) { ok = false; break; }
                for (int i = 1; i < n && ok; i++)
                    ok = before ? s.PathHi[i] <= k.PathLo[i] + slack : s.PathLo[i] >= k.PathHi[i] - slack;
            }
            if (ok) kept.Add(s); else droppedCoupling += s.Length;
        }
        if (kept.Count == 0)
            return BoardCoupledResult.Failure(NoCommonRunReason(nets, routes, cosTol, options));
        kept.Sort((a, b) => a.PathLo[0].CompareTo(b.PathLo[0]));

        // Lateral overlap (edge gap ≤ 0) in any stretch is not a coplanar coupled line.
        foreach (var s in kept)
        {
            var order = Enumerable.Range(0, n).OrderBy(i => s.Offset[i]).ToArray();
            for (int k = 1; k < n; k++)
            {
                int a = order[k - 1], b = order[k];
                double edgeGap = (s.Offset[b] - s.Width[b] / 2) - (s.Offset[a] + s.Width[a] / 2);
                if (edgeGap <= 0)
                    return BoardCoupledResult.Failure(
                        $"nets '{nets[a].Label}' and '{nets[b].Label}' overlap laterally (edge gap "
                        + $"{edgeGap * 1e6:g3} µm) — a broadside pair or one net drawn twice, not a "
                        + "coplanar coupled line.");
            }
        }

        // The substrate: the dielectric between the coupled runs and the reference plane(s)
        // the board has above and below them.
        var ownIslands = nets.SelectMany(net => net.Islands).ToList();
        var substrate = BoardReferencePlanes.Resolve(board, layer,
            kept.SelectMany(s => s.Pieces).ToList(), ownIslands, options, out string planeFailure);
        if (substrate is null)
            return BoardCoupledResult.Failure(planeFailure);

        double copperThickness = StackupOf(board, options).CopperThicknessOf(layer);

        // Ground copper on the trace layer beside each coupled stretch: the reference net(s)
        // under the traces say which copper is ground.
        var layerThickness = substrate.Stackup.Layers.Select(l => l.ThicknessMeters).ToArray();
        double toPlaneBelow = layerThickness.Take(substrate.MetalInterface + 1).Sum();
        double toPlaneAbove = substrate.TopGround ? layerThickness.Skip(substrate.MetalInterface + 1).Sum() : 0;
        double toPlane = substrate.TopGround ? Math.Min(toPlaneBelow, toPlaneAbove) : toPlaneBelow;
        var reference = options.CoplanarGround
            ? BoardCoplanarGround.ReferenceNets(board, substrate.PlaneLayers,
                BoardReferencePlanes.SamplePoints(kept.SelectMany(s => s.Pieces).ToList()))
            : (new HashSet<CopperNet>(), new HashSet<string>());
        var coplanarNotes = new List<string>();
        int stretchesWithGround = 0;

        // One cross-section object per distinct coupled geometry, so a pair that bends and
        // runs on at the same spacing is solved once.
        var sectionCache = new Dictionary<string, CoupledLineCrossSection>();
        var coupledSections = new CoupledLineCrossSection[kept.Count];
        try
        {
            for (int k = 0; k < kept.Count; k++)
            {
                var s = kept[k];
                var traces = new List<TraceCrossSection>();
                for (int i = 0; i < n; i++)
                    traces.Add(new TraceCrossSection(s.Offset[i], s.Width[i],
                        copperThickness, options.ConductivitySiemensPerMeter));
                if (options.CoplanarGround && reference.Item1.Count > 0)
                {
                    double low = Enumerable.Range(0, n).Min(i => s.Offset[i] - s.Width[i] / 2);
                    double high = Enumerable.Range(0, n).Max(i => s.Offset[i] + s.Width[i] / 2);
                    var axis = Unit(s.Pieces[0].End - s.Pieces[0].Start);
                    var start = s.Pieces[0].Start - new Point2(-axis.Y, axis.X) * s.Offset[0];
                    var (lowSide, highSide, notes) = BoardCoplanarGround.Find(board, layer, ownIslands, reference,
                        start, axis, s.Length, low, high, toPlane, substrate.TopGround,
                        Math.Max(toPlaneBelow, toPlaneAbove));
                    coplanarNotes.AddRange(notes.Select(note => kept.Count > 1
                        ? $"Stretch {k + 1} ({s.Length * 1e3:g4} mm): {note}" : note));
                    if (lowSide is not null)
                        traces.Add(new TraceCrossSection(low - lowSide.Gap - lowSide.Width / 2, lowSide.Width,
                            copperThickness, options.ConductivitySiemensPerMeter) { IsGround = true });
                    if (highSide is not null)
                        traces.Add(new TraceCrossSection(high + highSide.Gap + highSide.Width / 2, highSide.Width,
                            copperThickness, options.ConductivitySiemensPerMeter) { IsGround = true });
                    if (lowSide is not null || highSide is not null) stretchesWithGround++;
                }
                string key = string.Join("|", traces.Select(t =>
                    $"{Math.Round(t.CenterMeters * 1e9)}:{Math.Round(t.WidthMeters * 1e9)}:{t.IsGround}"));
                if (!sectionCache.TryGetValue(key, out var section))
                {
                    section = new CoupledLineCrossSection(substrate.Stackup, substrate.MetalInterface,
                        traces, substrate.TopGround);
                    sectionCache[key] = section;
                }
                coupledSections[k] = section;
            }
        }
        catch (ArgumentException ex) { return BoardCoupledResult.Failure(ex.Message); }

        // The uncoupled stretches of each net are priced by the SAME RlgcExtractor over a
        // single-trace cross-section on the same substrate, at the net's dominant width.
        var leadSections = new CoupledLineCrossSection[n];
        for (int i = 0; i < n; i++)
            leadSections[i] = new CoupledLineCrossSection(substrate.Stackup, substrate.MetalInterface,
                new[] { new TraceCrossSection(0, routes[i].DominantWidth, copperThickness,
                    options.ConductivitySiemensPerMeter) }, substrate.TopGround);

        // The cascade, near → far: lead, coupled, lead, coupled, …, lead. A lead no net
        // extends into is left out, so a board with no leads keeps its single section.
        var cascade = new List<BoardRouteSection>();
        var leadTotals = new double[n];
        void AddLead(Func<int, double> length)
        {
            var lengths = new double[n];
            for (int i = 0; i < n; i++)
            {
                double l = length(i);
                lengths[i] = l > slack ? l : 0;
                leadTotals[i] += lengths[i];
            }
            if (lengths.Any(l => l > 0)) cascade.Add(new BoardRouteSection(null, 0, lengths));
        }
        for (int k = 0; k < kept.Count; k++)
        {
            int kk = k;
            AddLead(i => kept[kk].PathLo[i] - (kk == 0 ? 0 : kept[kk - 1].PathHi[i]));
            cascade.Add(new BoardRouteSection(coupledSections[k], kept[k].Length, null));
        }
        AddLead(i => routes[i].Length - kept[^1].PathHi[i]);

        int principal = 0;
        for (int k = 1; k < kept.Count; k++)
            if (kept[k].Length > kept[principal].Length) principal = k;
        double coupledLength = kept.Sum(s => s.Length);

        var driverAtNear = new bool[n];
        for (int i = 0; i < n; i++)
            driverAtNear[i] = i == 0 || !StartsAtFarEnd(routes[i].Chain, options with { SwapEnds = false }, i);

        var result = new BoardCoupledResult(coupledSections[principal], null, null, coupledLength,
            leadTotals, Array.Empty<string>(), null)
        {
            Sections = cascade,
            LeadCrossSections = leadSections,
            RoutedLengthsMeters = routes.Select(r => r.Length).ToArray(),
            NearEnds = routes.Select(r => r.Chain[0].Start).ToArray(),
            FarEnds = routes.Select(r => r.Chain[^1].End).ToArray(),
            DriverAtNearEnd = driverAtNear,
        };

        var rlgcCache = new Dictionary<CoupledLineCrossSection, RlgcResult>(ReferenceEqualityComparer.Instance);
        RlgcResult Rlgc(CoupledLineCrossSection s) =>
            rlgcCache.TryGetValue(s, out var r) ? r : rlgcCache[s] = RlgcExtractor.Extract(s, options.Model);
        var network = result.BuildNetwork(Rlgc);
        var rlgc = BoardCoupledResult.Reduce(coupledSections[principal], Rlgc(coupledSections[principal]));

        var assumptions = new List<string>(rlgc.Assumptions) { substrate.Note };
        assumptions.AddRange(coplanarNotes.Distinct());
        if (stretchesWithGround > 0 && leadTotals.Any(l => l > 0))
            assumptions.Add("Coplanar ground is modelled on the coupled stretches only; the uncoupled "
                + "leads are single traces over the plane.");
        else if (options.CoplanarGround && reference.Item1.Count == 0
                 && board.Islands.Any(i => i.LayerOrder == layer && !ownIslands.Contains(i)))
            assumptions.Add("Copper on the trace layer is not modelled: the reference plane's copper "
                + "belongs to no net of the board, so which copper is ground cannot be told.");
        bool anyLead = leadTotals.Any(l => l > 0);
        if (!anyLead && kept.Count == 1)
        {
            assumptions.Add($"Coupled section = the {coupledLength * 1e3:g4} mm parallel run of the "
                + "nets, which spans their whole routed length (no lead tails).");
        }
        else
        {
            string coupledText = kept.Count == 1
                ? $"Coupled section = the {coupledLength * 1e3:g4} mm stretch where the nets run parallel"
                : $"{kept.Count} coupled sections ({string.Join(" + ", kept.Select(s => $"{s.Length * 1e3:g4}"))} mm) "
                  + "= the stretches where the nets run parallel";
            assumptions.Add(coupledText + "; the rest of each route ("
                + string.Join(", ", leadTotals.Select(l => $"{l * 1e3:g3} mm")) + ") is CASCADED in "
                + "route order as uncoupled single-trace lead sections (their own R, L and C to the "
                + "plane at the net's dominant width; a bend is carried as its path length, with no "
                + "corner discontinuity and no coupling between a net's own legs).");
        }
        assumptions.Add("Routed length in the network: "
            + string.Join(", ", Enumerable.Range(0, n).Select(i =>
                $"{nets[i].Label} {routes[i].Length * 1e3:g4} mm")) + " (the whole pin-to-pin chain).");
        for (int i = 0; i < n; i++)
        {
            if (Math.Abs(drawnLength[i] - routes[i].Length) > 0.01 * routes[i].Length)
                assumptions.Add($"Net '{nets[i].Label}': {drawnLength[i] * 1e3:g4} mm of trace is drawn "
                    + $"but the chain is {routes[i].Length * 1e3:g4} mm (duplicate draws and sub-width "
                    + "stubs are merged or dropped); the difference is not modelled.");
            if (routes[i].MixedWidths)
                assumptions.Add($"Net '{nets[i].Label}' changes width along its route; its uncoupled "
                    + $"stretches are priced at {routes[i].DominantWidth * 1e3:g3} mm, the width "
                    + "most of it is drawn at.");
        }
        if (droppedCoupling > 0)
            assumptions.Add($"A further {droppedCoupling * 1e3:g3} mm where the nets run parallel is "
                + "modelled as UNCOUPLED: the nets fold back or meet those stretches in a different "
                + "order, which one cascade cannot express.");
        if (n > 2)
            assumptions.Add("A stretch is coupled only where ALL selected nets run parallel; where "
                + "only some do, each is modelled as running alone.");
        assumptions.Add("Near end: "
            + string.Join(", ", Enumerable.Range(0, n).Select(i =>
                $"{nets[i].Label} at ({result.NearEnds[i].X * 1e3:g4}, {result.NearEnds[i].Y * 1e3:g4}) mm"))
            + (options.DriverPoints is null
                ? " — taken from the first net's chain start; every net is assumed driven from this side."
                : "."));
        for (int i = 1; i < n; i++)
            if (!driverAtNear[i])
                assumptions.Add($"Net '{nets[i].Label}' has its driver at the network's FAR end: "
                    + "drive it at its far port.");

        return result with { Rlgc = rlgc, Network = network, Assumptions = assumptions };
    }

    /// <summary>True when net <paramref name="index"/>'s driver is at the END of the chain as
    /// ordered: its driver point is nearer the last endpoint, or (first net, no driver
    /// point) the ends are swapped by option.</summary>
    private static bool StartsAtFarEnd(IReadOnlyList<TraceCenterline> chain,
        BoardCoupledOptions options, int index)
    {
        Point2? driver = options.DriverPoints is { } points && index < points.Count ? points[index] : null;
        if (driver is { } p)
            return (chain[^1].End - p).Length < (chain[0].Start - p).Length;
        return index == 0 && options.SwapEnds;
    }

    private static List<TraceCenterline> Reversed(IReadOnlyList<TraceCenterline> chain)
    {
        var reversed = new List<TraceCenterline>(chain.Count);
        for (int i = chain.Count - 1; i >= 0; i--)
            reversed.Add(chain[i] with { Start = chain[i].End, End = chain[i].Start });
        return reversed;
    }

    /// <summary>An ordered chain reduced to straight runs (consecutive near-collinear
    /// segments merged; the run's width is its widest segment), with path coordinates.</summary>
    private static Route RouteOf(List<TraceCenterline> chain, double cosTol)
    {
        var runs = new List<Run>();
        double path = 0;
        int start = 0;
        while (start < chain.Count)
        {
            var runDir = Unit(chain[start].End - chain[start].Start);
            double length = chain[start].Length;
            double width = chain[start].Width;
            int end = start;
            for (int j = start + 1; j < chain.Count; j++)
            {
                var d = Unit(chain[j].End - chain[j].Start);
                if (Point2.Dot(d, runDir) < cosTol) break;
                length += chain[j].Length;
                width = Math.Max(width, chain[j].Width);
                end = j;
            }
            runs.Add(new Run(chain[start].Start, chain[end].End, width, path, length));
            path += length;
            start = end + 1;
        }

        // Dominant width: the one the greatest routed length is drawn at.
        var byWidth = chain.GroupBy(c => Math.Round(c.Width * 1e6))
            .Select(g => (Width: g.First().Width, Length: g.Sum(c => c.Length)))
            .OrderByDescending(g => g.Length).ToList();
        return new Route
        {
            Chain = chain,
            Runs = runs,
            Length = path,
            DominantWidth = byWidth[0].Width,
            MixedWidths = byWidth.Count > 1
                && byWidth.Skip(1).Any(g => Math.Abs(g.Width - byWidth[0].Width) > 0.1 * byWidth[0].Width),
        };
    }

    /// <summary>For every run of the first net, the intervals along it over which a run of
    /// EVERY other net lies parallel within the spacing limit.</summary>
    private static List<Stretch> FindStretches(Route[] routes, double cosTol, double spacingLimit)
    {
        int n = routes.Length;
        var found = new List<Stretch>();
        foreach (var r0 in routes[0].Runs)
        {
            double l0 = r0.Extent;
            if (l0 <= 0) continue;
            var axis = (r0.B - r0.A) * (1.0 / l0);
            var perp = new Point2(-axis.Y, axis.X);

            // Candidates: an interval [lo, hi] along the axis and the run picked per net.
            var candidates = new List<(double Lo, double Hi, Run[] Picks)>
                { (0, l0, new[] { r0 }) };
            for (int j = 1; j < n && candidates.Count > 0; j++)
            {
                var next = new List<(double, double, Run[])>();
                foreach (var (lo, hi, picks) in candidates)
                    foreach (var r in routes[j].Runs)
                    {
                        if (r.Extent <= 0) continue;
                        if (Math.Abs(Point2.Dot(Unit(r.B - r.A), axis)) < cosTol) continue;
                        double off = Point2.Dot((r.A + r.B) * 0.5 - r0.A, perp);
                        if (Math.Abs(off) > spacingLimit) continue;
                        double sA = Point2.Dot(r.A - r0.A, axis), sB = Point2.Dot(r.B - r0.A, axis);
                        double newLo = Math.Max(lo, Math.Min(sA, sB));
                        double newHi = Math.Min(hi, Math.Max(sA, sB));
                        double narrowest = Math.Min(r.Width, picks.Min(p => p.Width));
                        if (newHi - newLo <= narrowest) continue;   // sub-width overlap is not a coupled run
                        next.Add((newLo, newHi, picks.Append(r).ToArray()));
                    }
                candidates = next;
            }

            foreach (var (lo, hi, picks) in candidates)
            {
                var s = new Stretch
                {
                    Length = hi - lo,
                    PathLo = new double[n], PathHi = new double[n],
                    Offset = new double[n], Width = new double[n],
                    Pieces = new TraceCenterline[n],
                };
                for (int i = 0; i < n; i++)
                {
                    var r = picks[i];
                    double sA = Point2.Dot(r.A - r0.A, axis), sB = Point2.Dot(r.B - r0.A, axis);
                    double PathAt(double sAxis) => r.PathStart + (sAxis - sA) / (sB - sA) * r.PathLength;
                    s.PathLo[i] = PathAt(lo);
                    s.PathHi[i] = PathAt(hi);
                    s.Offset[i] = Point2.Dot((r.A + r.B) * 0.5 - r0.A, perp);
                    s.Width[i] = r.Width;
                    var at = r0.A + perp * s.Offset[i];
                    s.Pieces[i] = new TraceCenterline(routes[i].Chain[0].LayerOrder,
                        at + axis * lo, at + axis * hi, r.Width);
                }
                found.Add(s);
            }
        }
        return found;
    }

    private static string NoCommonRunReason(IReadOnlyList<CopperNet> nets, Route[] routes,
        double cosTol, BoardCoupledOptions options)
    {
        for (int j = 1; j < routes.Length; j++)
        {
            double best = 0;
            foreach (var a in routes[0].Runs)
                foreach (var b in routes[j].Runs)
                    best = Math.Max(best, Math.Abs(Point2.Dot(Unit(a.B - a.A), Unit(b.B - b.A))));
            if (best < cosTol)
            {
                double deg = Math.Acos(Math.Clamp(best, -1, 1)) * 180 / Math.PI;
                return $"net '{nets[j].Label}' runs at least {deg:g3}° off '{nets[0].Label}' everywhere — "
                    + "a coupled-line model requires parallel conductors (this is a non-parallel tangle).";
            }
        }
        return "the selected nets do not share a common parallel run (no longitudinal overlap "
            + $"within {options.CouplingSpacingLimitMeters * 1e3:g3} mm of each other) — they may "
            + "connect end-to-end rather than run side by side.";
    }

    private static Point2 Unit(Point2 v)
    {
        double l = v.Length;
        return l > 0 ? v * (1.0 / l) : new Point2(1, 0);
    }

    /// <summary>The stackup an extraction runs over: the one it was handed, else the board
    /// file's completed with the option defaults. Internal — the capacitance extractor and
    /// the DC sweep resolve it through this one function.</summary>
    internal static BoardStackup StackupOf(PcbBoard board, BoardCoupledOptions options) =>
        options.Stackup ?? BoardStackup.FromBoard(board, options.DefaultBoardThicknessMeters,
            options.DefaultEpsR, options.DefaultTanD, options.CopperThicknessMeters);

}
