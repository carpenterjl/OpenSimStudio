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
    IReadOnlyList<double>? LeadLengthsMeters)
{
    /// <summary>The nets the coupled section carries, ascending; null is every net. The rest
    /// pass through it unchanged.</summary>
    public IReadOnlyList<int>? Group { get; init; }

    /// <summary>For each net of the group (ascending), which signal conductor of
    /// <see cref="Coupled"/> it is — the cross-section numbers its traces left to right.
    /// Null is the identity.</summary>
    public IReadOnlyList<int>? ConductorOrder { get; init; }

    /// <summary>For a lead section, the single-trace cross-section each net runs this piece
    /// at (its width here); null takes <see cref="BoardCoupledResult.LeadCrossSections"/>.</summary>
    public IReadOnlyList<CoupledLineCrossSection>? LeadSections { get; init; }
}

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

        int nets = LeadCrossSections.Count;
        var sections = new List<MtlSectionBase>();
        foreach (var s in Sections)
        {
            if (s.Coupled is not null)
            {
                var rlgc = s.ConductorOrder is { } order ? RlgcReduction.Permute(Of(s.Coupled), order) : Of(s.Coupled);
                var coupled = new MtlSection(rlgc, s.CoupledLengthMeters);
                sections.Add(s.Group is { } group && group.Count < nets
                    ? new MtlGroupSection(coupled, group, nets)
                    : coupled);
            }
            else
                sections.Add(new MtlLeadSection(s.LeadLengthsMeters!
                    .Select((l, i) => (Of(s.LeadSections?[i] ?? LeadCrossSections[i]), l))
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
/// net's WHOLE ordered route (pin to pin), across layers through its vias. Every stretch where
/// two or more of the selected nets run parallel on one layer within a spacing limit becomes a
/// coupled section of those nets (the others pass through it) — widths from the draws,
/// lateral centres from the perpendicular offsets, the substrate from the reference planes
/// the board actually has above and below that layer (<see cref="BoardReferencePlanes"/>:
/// microstrip, embedded microstrip or stripline — or a refusal when no layer has copper
/// under them). Everything else on each route — pad escapes, bends, the legs of an L or Z —
/// is an uncoupled single-line lead of that net's own length, piece by piece at its own
/// width and on its own layer, cascaded in route order. No routed length is left out. Every
/// section uses the same <see cref="RlgcExtractor"/> / <see cref="MtlNetwork"/> the wizard
/// uses — so a synthetic two-trace board round-trips to the wizard geometry's RLGC exactly.
/// The network's conductors are the nets in the order they were selected.
///
/// <para>The network's near end is the first net's driven end
/// (<see cref="BoardCoupledOptions.DriverPoints"/>, else where its chain starts); the
/// other nets' near ends are the ends on the same side of the coupled stretches.</para>
///
/// <para>Non-conforming topologies are typed failures, never a garbage matrix: a pour/region
/// net (no centerlines), a net that branches (surfaced verbatim from
/// <see cref="TraceChainBuilder"/>), nets with no parallel run in common, or conductors
/// that overlap laterally (a broadside pair or one net drawn twice). The
/// coplanar-at-one-interface contract is the whole layered track's; broadside coupling
/// across layers cannot be expressed by construction.</para>
/// </summary>
public static class BoardCoupledExtractor
{
    /// <summary>A straight run of a route: consecutive near-collinear chain segments merged.
    /// A → B in route order; the path coordinate of A and the summed segment length.</summary>
    private sealed record Run(Point2 A, Point2 B, double Width, double PathStart, double PathLength, int Layer)
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
        /// <summary>Where the route changes layer through a via.</summary>
        public int LayerChanges;
    }

    /// <summary>A stretch where the member nets run parallel: per member the route's path
    /// coordinates at its two ends (Lo at the axis' low end), the lateral offset from the
    /// anchor's run, the run's width, and how path and axis map onto each other.</summary>
    private sealed class Stretch
    {
        public double Length;
        public required bool[] Member;
        public required double[] PathLo, PathHi, Offset, Width;
        public required TraceCenterline[] Pieces;
        public required (double SA, double SB, double PathStart, double PathLength)[] Map;
        public required Point2 Origin, Axis, Perp;
        public double AxisLo, AxisHi;
        public int Layer;

        public IEnumerable<int> Members => Enumerable.Range(0, Member.Length).Where(i => Member[i]);
        public int Count => Member.Count(m => m);

        public double PathAt(int net, double s)
        {
            var m = Map[net];
            return m.PathStart + (s - m.SA) / (m.SB - m.SA) * m.PathLength;
        }

        public double AxisAt(int net, double path)
        {
            var m = Map[net];
            return m.SA + (path - m.PathStart) / m.PathLength * (m.SB - m.SA);
        }

        /// <summary>The same stretch over [lo, hi] of its axis.</summary>
        public Stretch Trim(double lo, double hi)
        {
            int n = Member.Length;
            var trimmed = new Stretch
            {
                Length = hi - lo, Member = Member, Offset = Offset, Width = Width, Map = Map,
                Origin = Origin, Axis = Axis, Perp = Perp, AxisLo = lo, AxisHi = hi, Layer = Layer,
                PathLo = new double[n], PathHi = new double[n], Pieces = new TraceCenterline[n],
            };
            foreach (int i in Members)
            {
                trimmed.PathLo[i] = PathAt(i, lo);
                trimmed.PathHi[i] = PathAt(i, hi);
                var at = Origin + Perp * Offset[i];
                trimmed.Pieces[i] = new TraceCenterline(Layer, at + Axis * lo, at + Axis * hi, Width[i]);
            }
            return trimmed;
        }
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

        // Every net's ordered route: on one layer the planar chain, across layers the chain
        // through its via barrels, flattened to the trace segments with their layers.
        var routes = new Route[n];
        var drawnLength = new double[n];
        var stack = StackupOf(board, options);
        for (int i = 0; i < n; i++)
        {
            var centerlines = NetTraceExtractor.ForNet(board, nets[i]);
            if (centerlines.Count == 0)
                return BoardCoupledResult.Failure(
                    $"net '{nets[i].Label}' has no trace centerlines (a pour/region net cannot "
                    + "be a coupled line — select routed signal nets).");

            List<TraceCenterline> ordered;
            if (centerlines.Select(c => c.LayerOrder).Distinct().Count() == 1)
            {
                var chain = TraceChainBuilder.Build(centerlines);
                if (chain.Chain is null)
                    return BoardCoupledResult.Failure($"net '{nets[i].Label}': {chain.FailureReason}");
                ordered = chain.Chain.ToList();
            }
            else
            {
                var meshOptions = new NetMeshOptions
                {
                    CopperThickness = stack.DefaultCopperThickness, LayerThickness = stack.LayerThickness,
                    DefaultDielectricThickness = stack.DefaultGapThickness, DielectricGapThickness = stack.GapThickness
                };
                var chain = TraceChainBuilder.Build(centerlines, nets[i].StitchingVias, meshOptions, nets[i].Islands);
                if (chain.Chain is null)
                    return BoardCoupledResult.Failure($"net '{nets[i].Label}': {chain.FailureReason}");
                ordered = Flatten(chain);
            }
            if (i == 0 && StartsAtFarEnd(ordered, options, 0)) ordered = Reversed(ordered);
            routes[i] = RouteOf(ordered, cosTol);
            drawnLength[i] = centerlines.Sum(c => c.Length);
        }

        const double slack = 1e-9;
        // Stretches where some of the nets run parallel: every group of two or more, largest
        // groups and longest stretches first, each trimmed to where no stretch already taken
        // claims any of its nets.
        var candidates = new List<Stretch>();
        for (int mask = 1; mask < 1 << n; mask++)
        {
            var members = Enumerable.Range(0, n).Where(i => (mask & (1 << i)) != 0).ToArray();
            if (members.Length < 2) continue;
            candidates.AddRange(FindStretches(routes, members, cosTol, options.CouplingSpacingLimitMeters));
        }
        var taken = new List<Stretch>();
        foreach (var c in candidates.OrderByDescending(s => s.Count).ThenByDescending(s => s.Length))
        {
            var cuts = new List<(double Lo, double Hi)>();
            foreach (var a in taken)
                foreach (int i in c.Members)
                {
                    if (!a.Member[i]) continue;
                    double s0 = c.AxisAt(i, a.PathLo[i]), s1 = c.AxisAt(i, a.PathHi[i]);
                    cuts.Add((Math.Min(s0, s1), Math.Max(s0, s1)));
                }
            double narrowest = c.Members.Min(i => c.Width[i]);
            foreach (var (lo, hi) in Remaining(c.AxisLo, c.AxisHi, cuts))
                if (hi - lo > narrowest) taken.Add(c.Trim(lo, hi));
        }
        if (taken.Count == 0)
            return BoardCoupledResult.Failure(NoCommonRunReason(nets, routes, cosTol, options));

        // Orient every net the way the nets it runs with are walked, starting from the first
        // net and the longest stretches: a net whose path coordinate falls along a stretch is
        // walked from its other end.
        var oriented = new bool[n];
        oriented[0] = true;
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var s in taken.OrderByDescending(s => s.Length))
            {
                int known = s.Members.FirstOrDefault(i => oriented[i], -1);
                if (known < 0) continue;
                bool forward = s.PathHi[known] > s.PathLo[known];
                foreach (int i in s.Members.Where(i => !oriented[i]).ToList())
                {
                    if ((s.PathHi[i] > s.PathLo[i]) != forward)
                    {
                        double total = routes[i].Length;
                        foreach (var t in taken.Where(t => t.Member[i]))
                        {
                            t.PathLo[i] = total - t.PathLo[i];
                            t.PathHi[i] = total - t.PathHi[i];
                        }
                        routes[i] = RouteOf(Reversed(routes[i].Chain), cosTol);
                    }
                    oriented[i] = true;
                    changed = true;
                }
            }
        }

        // A stretch some net walks backwards (a fold-back) cannot sit in one cascade, nor can
        // stretches two nets meet in a different order: the shortest of those goes back to
        // the leads until every net meets its stretches in route order.
        double droppedCoupling = 0;
        var kept = new List<Stretch>();
        foreach (var s in taken)
            if (s.Members.All(i => s.PathHi[i] > s.PathLo[i])) kept.Add(s); else droppedCoupling += s.Length;
        List<Stretch>? inOrder;
        while ((inOrder = InRouteOrder(kept, n)) is null)
        {
            var shortest = kept.MinBy(s => s.Length)!;
            kept.Remove(shortest);
            droppedCoupling += shortest.Length;
        }
        kept = inOrder;
        if (kept.Count == 0)
            return BoardCoupledResult.Failure(NoCommonRunReason(nets, routes, cosTol, options));

        // Lateral overlap (edge gap ≤ 0) in any stretch is not a coplanar coupled line.
        foreach (var s in kept)
        {
            var order = s.Members.OrderBy(i => s.Offset[i]).ToArray();
            for (int k = 1; k < order.Length; k++)
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

        // The substrate of every layer a route runs on: the dielectric between its traces and
        // the reference plane(s) the board has above and below them, found under that layer's
        // coupled stretches, or under the routes' own segments there when it has none.
        var ownIslands = nets.SelectMany(net => net.Islands).ToList();
        var routeLayers = routes.SelectMany(r => r.Chain.Select(c => c.LayerOrder)).Distinct().OrderBy(l => l).ToList();
        var substrates = new Dictionary<int, BoardSubstrate>();
        foreach (int routeLayer in routeLayers)
        {
            var samples = kept.Where(s => s.Layer == routeLayer).SelectMany(s => s.Members.Select(i => s.Pieces[i])).ToList();
            if (samples.Count == 0) samples = routes.SelectMany(r => r.Chain.Where(c => c.LayerOrder == routeLayer)).ToList();
            var resolved = BoardReferencePlanes.Resolve(board, routeLayer, samples, ownIslands, options, out string planeFailure);
            if (resolved is null) return BoardCoupledResult.Failure(planeFailure);
            substrates[routeLayer] = resolved;
        }

        // Ground copper on the trace layer beside each coupled stretch: the reference net(s)
        // under the traces say which copper is ground.
        var references = new Dictionary<int, (HashSet<CopperNet> Nets, HashSet<string> Names)>();
        (HashSet<CopperNet> Nets, HashSet<string> Names) ReferenceOf(int on)
        {
            if (references.TryGetValue(on, out var known)) return known;
            return references[on] = options.CoplanarGround
                ? BoardCoplanarGround.ReferenceNets(board, substrates[on].PlaneLayers,
                    BoardReferencePlanes.SamplePoints(kept.Where(s => s.Layer == on).SelectMany(s => s.Members.Select(i => s.Pieces[i])).ToList()))
                : (new HashSet<CopperNet>(), new HashSet<string>());
        }
        var coplanarNotes = new List<string>();
        int stretchesWithGround = 0;

        // One cross-section object per distinct coupled geometry, so a pair that bends and
        // runs on at the same spacing is solved once. Its traces are numbered left to right;
        // ConductorOrder takes them back to the nets.
        var sectionCache = new Dictionary<string, CoupledLineCrossSection>();
        var coupledSections = new CoupledLineCrossSection[kept.Count];
        var conductorOrders = new int[kept.Count][];
        try
        {
            for (int k = 0; k < kept.Count; k++)
            {
                var s = kept[k];
                var substrate = substrates[s.Layer];
                double copperThickness = stack.CopperThicknessOf(s.Layer);
                var members = s.Members.ToArray();
                var traces = new List<TraceCrossSection>();
                foreach (int i in members)
                    traces.Add(new TraceCrossSection(s.Offset[i], s.Width[i],
                        copperThickness, options.ConductivitySiemensPerMeter));
                var reference = ReferenceOf(s.Layer);
                if (options.CoplanarGround && reference.Nets.Count > 0)
                {
                    var layerThickness = substrate.Stackup.Layers.Select(l => l.ThicknessMeters).ToArray();
                    double toPlaneBelow = layerThickness.Take(substrate.MetalInterface + 1).Sum();
                    double toPlaneAbove = substrate.TopGround ? layerThickness.Skip(substrate.MetalInterface + 1).Sum() : 0;
                    double toPlane = substrate.TopGround ? Math.Min(toPlaneBelow, toPlaneAbove) : toPlaneBelow;
                    double low = members.Min(i => s.Offset[i] - s.Width[i] / 2);
                    double high = members.Max(i => s.Offset[i] + s.Width[i] / 2);
                    var start = s.Origin + s.Axis * s.AxisLo;
                    var (lowSide, highSide, notes) = BoardCoplanarGround.Find(board, s.Layer, ownIslands, reference,
                        start, s.Axis, s.Length, low, high, toPlane, substrate.TopGround,
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
                string key = $"L{s.Layer}:" + string.Join(",", members) + "/" + string.Join("|", traces.Select(t =>
                    $"{Math.Round(t.CenterMeters * 1e9)}:{Math.Round(t.WidthMeters * 1e9)}:{t.IsGround}"));
                if (!sectionCache.TryGetValue(key, out var section))
                {
                    section = new CoupledLineCrossSection(substrate.Stackup, substrate.MetalInterface,
                        traces, substrate.TopGround);
                    sectionCache[key] = section;
                }
                coupledSections[k] = section;
                // The section sorts its traces by centre (stably): the k-th signal is the member
                // with the k-th smallest offset.
                var leftToRight = members.OrderBy(i => s.Offset[i]).ToArray();
                conductorOrders[k] = members.Select(i => Array.IndexOf(leftToRight, i)).ToArray();
            }
        }
        catch (ArgumentException ex) { return BoardCoupledResult.Failure(ex.Message); }

        // The uncoupled stretches of each net are priced by the SAME RlgcExtractor over a
        // single-trace cross-section on its layer's substrate, piece by piece at the width each
        // piece is drawn at (the net's dominant width on its first layer is its listed lead
        // cross-section).
        var leadSections = new CoupledLineCrossSection[n];
        var leadByWidth = new Dictionary<(int, long), CoupledLineCrossSection>[n];
        CoupledLineCrossSection LeadSection(int i, double width, int on)
        {
            var key = (on, (long)Math.Round(width * 1e9));
            if (leadByWidth[i].TryGetValue(key, out var existing)) return existing;
            var substrate = substrates[on];
            return leadByWidth[i][key] = new CoupledLineCrossSection(substrate.Stackup, substrate.MetalInterface,
                new[] { new TraceCrossSection(0, width, stack.CopperThicknessOf(on), options.ConductivitySiemensPerMeter) },
                substrate.TopGround);
        }
        for (int i = 0; i < n; i++)
        {
            leadByWidth[i] = new Dictionary<(int, long), CoupledLineCrossSection>();
            leadSections[i] = LeadSection(i, routes[i].DominantWidth, routes[i].Chain[0].LayerOrder);
        }

        // The cascade, near → far. Before each stretch its own nets run on alone to where it
        // starts; the others wait for a lead of their own (uncoupled pieces of different nets
        // do not interact, so where they sit in the cascade does not matter).
        var cascade = new List<BoardRouteSection>();
        var leadTotals = new double[n];
        var position = new double[n];
        void AddLead(Func<int, double> until, Func<int, bool> moves)
        {
            var pieces = new List<(double Width, int Layer, double Length)>[n];
            for (int i = 0; i < n; i++)
            {
                pieces[i] = moves(i) ? Pieces(routes[i], position[i], until(i), slack) : new();
                leadTotals[i] += pieces[i].Sum(p => p.Length);
                if (moves(i)) position[i] = Math.Max(position[i], until(i));
            }
            int count = pieces.Max(p => p.Count);
            for (int j = 0; j < count; j++)
            {
                var lengths = new double[n];
                var sections = new CoupledLineCrossSection[n];
                for (int i = 0; i < n; i++)
                {
                    lengths[i] = j < pieces[i].Count ? pieces[i][j].Length : 0;
                    sections[i] = j < pieces[i].Count ? LeadSection(i, pieces[i][j].Width, pieces[i][j].Layer) : leadSections[i];
                }
                if (lengths.Any(l => l > 0))
                    cascade.Add(new BoardRouteSection(null, 0, lengths)
                    {
                        LeadSections = sections
                    });
            }
        }
        for (int k = 0; k < kept.Count; k++)
        {
            var s = kept[k];
            AddLead(i => s.PathLo[i], i => s.Member[i]);
            var members = s.Members.ToArray();
            bool identity = conductorOrders[k].Select((c, idx) => c == idx).All(x => x);
            cascade.Add(new BoardRouteSection(coupledSections[k], s.Length, null)
            {
                Group = members.Length < n ? members : null,
                ConductorOrder = identity ? null : conductorOrders[k],
            });
            foreach (int i in members) position[i] = s.PathHi[i];
        }
        AddLead(i => routes[i].Length, _ => true);

        // The principal stretch: the longest that carries every net, else the longest.
        int principal = 0;
        for (int k = 1; k < kept.Count; k++)
        {
            bool full = kept[k].Count == n, principalFull = kept[principal].Count == n;
            if ((full && !principalFull) || (full == principalFull && kept[k].Length > kept[principal].Length))
                principal = k;
        }
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
        var rlgc = RlgcReduction.Permute(
            BoardCoupledResult.Reduce(coupledSections[principal], Rlgc(coupledSections[principal])),
            conductorOrders[principal]);

        var assumptions = new List<string>(rlgc.Assumptions) { substrates[kept[principal].Layer].Note };
        assumptions.AddRange(substrates.Where(kv => kv.Key != kept[principal].Layer).Select(kv => kv.Value.Note));
        int layerChanges = routes.Sum(r => r.LayerChanges);
        if (layerChanges > 0)
            assumptions.Add($"The routes change layer {layerChanges} time(s) through vias; each via is carried as a joint, "
                + "with no barrel inductance or capacitance, and each layer's traces over that layer's own reference plane(s).");
        assumptions.AddRange(coplanarNotes.Distinct());
        if (stretchesWithGround > 0 && leadTotals.Any(l => l > 0))
            assumptions.Add("Coplanar ground is modelled on the coupled stretches only; the uncoupled "
                + "leads are single traces over the plane.");
        else if (options.CoplanarGround && kept.Any(s => ReferenceOf(s.Layer).Nets.Count == 0
                 && board.Islands.Any(i => i.LayerOrder == s.Layer && !ownIslands.Contains(i))))
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
                + "plane at the width each piece is drawn at; a bend is carried as its path length, with no "
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
                    + "stretches are priced piece by piece at the width each is drawn at, and a coupled "
                    + "stretch at the widest segment of its run.");
        }
        if (droppedCoupling > 0)
            assumptions.Add($"A further {droppedCoupling * 1e3:g3} mm where the nets run parallel is "
                + "modelled as UNCOUPLED: the nets fold back or meet those stretches in a different "
                + "order, which one cascade cannot express.");
        if (kept.Any(s => s.Count < n))
            assumptions.Add("Where only some of the selected nets run parallel, those are coupled among "
                + "themselves and the others run alone beside them: "
                + string.Join("; ", kept.Where(s => s.Count < n).Select(s =>
                    $"{string.Join(" + ", s.Members.Select(i => nets[i].Label))} over {s.Length * 1e3:g4} mm"))
                + ".");
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
        assumptions.Add("The network's conductors are the selected nets, in the order they were selected.");

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
                if (Point2.Dot(d, runDir) < cosTol || chain[j].LayerOrder != chain[start].LayerOrder) break;
                length += chain[j].Length;
                width = Math.Max(width, chain[j].Width);
                end = j;
            }
            runs.Add(new Run(chain[start].Start, chain[end].End, width, path, length, chain[start].LayerOrder));
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
            LayerChanges = Enumerable.Range(1, Math.Max(0, chain.Count - 1)).Count(j => chain[j].LayerOrder != chain[j - 1].LayerOrder),
        };
    }

    /// <summary>A chain through via barrels as its trace segments, each with the copper layer
    /// it lies on (by its height in the chain's stackup frame); the barrels themselves drop out,
    /// leaving the layer change between two segments that meet at the via.</summary>
    private static List<TraceCenterline> Flatten(TraceChain3DResult chain)
    {
        var layerZ = chain.LayerZ ?? throw new InvalidOperationException("A multi-layer chain carries its stackup frame.");
        int LayerAt(double z) => layerZ.MinBy(kv => Math.Abs(0.5 * (kv.Value.zLo + kv.Value.zHi) - z)).Key;
        var flat = new List<TraceCenterline>();
        foreach (var segment in chain.Chain!)
        {
            if (Math.Abs(segment.End.Z - segment.Start.Z) > 1e-9) continue;          // a via barrel
            flat.Add(new TraceCenterline(LayerAt(segment.Start.Z), new Point2(segment.Start.X, segment.Start.Y),
                new Point2(segment.End.X, segment.End.Y), segment.Width));
        }
        return flat;
    }

    /// <summary>For every run of the group's first net, the intervals along it over which a
    /// run of EVERY other member lies parallel within the spacing limit.</summary>
    private static List<Stretch> FindStretches(Route[] routes, int[] members, double cosTol, double spacingLimit)
    {
        int n = routes.Length;
        int anchor = members[0];
        var found = new List<Stretch>();
        foreach (var r0 in routes[anchor].Runs)
        {
            double l0 = r0.Extent;
            if (l0 <= 0) continue;
            var axis = (r0.B - r0.A) * (1.0 / l0);
            var perp = new Point2(-axis.Y, axis.X);

            // Candidates: an interval [lo, hi] along the axis and the run picked per member.
            var candidates = new List<(double Lo, double Hi, Run[] Picks)> { (0, l0, new[] { r0 }) };
            foreach (int j in members.Skip(1))
            {
                var next = new List<(double, double, Run[])>();
                foreach (var (lo, hi, picks) in candidates)
                    foreach (var r in routes[j].Runs)
                    {
                        if (r.Extent <= 0 || r.Layer != r0.Layer) continue;
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
                    Member = new bool[n],
                    PathLo = new double[n], PathHi = new double[n],
                    Offset = new double[n], Width = new double[n],
                    Pieces = new TraceCenterline[n],
                    Map = new (double, double, double, double)[n],
                    Origin = r0.A, Axis = axis, Perp = perp, AxisLo = lo, AxisHi = hi,
                    Layer = r0.Layer,
                };
                for (int k = 0; k < members.Length; k++)
                {
                    int i = members[k];
                    var r = picks[k];
                    double sA = Point2.Dot(r.A - r0.A, axis), sB = Point2.Dot(r.B - r0.A, axis);
                    s.Member[i] = true;
                    s.Map[i] = (sA, sB, r.PathStart, r.PathLength);
                    s.Offset[i] = Point2.Dot((r.A + r.B) * 0.5 - r0.A, perp);
                    s.Width[i] = r.Width;
                }
                found.Add(s.Trim(lo, hi));
            }
        }
        return found;
    }

    /// <summary>[lo, hi] less the given intervals, as the pieces that remain.</summary>
    private static List<(double Lo, double Hi)> Remaining(double lo, double hi, List<(double Lo, double Hi)> cuts)
    {
        var pieces = new List<(double, double)>();
        double at = lo;
        foreach (var (a, b) in cuts.OrderBy(c => c.Lo))
        {
            if (b <= at) continue;
            if (a >= hi) break;
            if (a > at) pieces.Add((at, Math.Min(a, hi)));
            at = Math.Max(at, b);
        }
        if (at < hi) pieces.Add((at, hi));
        return pieces;
    }

    /// <summary>The stretches in an order every net meets its own in, or null when two nets
    /// meet theirs in different orders.</summary>
    private static List<Stretch>? InRouteOrder(List<Stretch> stretches, int n)
    {
        var after = stretches.ToDictionary(s => s, _ => new List<Stretch>(), ReferenceEqualityComparer.Instance);
        var before = stretches.ToDictionary(s => s, _ => 0, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < n; i++)
        {
            var own = stretches.Where(s => s.Member[i]).OrderBy(s => s.PathLo[i]).ToList();
            for (int k = 1; k < own.Count; k++)
            {
                after[own[k - 1]].Add(own[k]);
                before[own[k]]++;
            }
        }
        var ordered = new List<Stretch>();
        var ready = stretches.Where(s => before[s] == 0).ToList();
        while (ready.Count > 0)
        {
            // Of the ones free to go, the one nearest the start of its first net's route.
            var next = ready.MinBy(s => s.PathLo[s.Members.First()])!;
            ready.Remove(next);
            ordered.Add(next);
            foreach (var s in after[next])
                if (--before[s] == 0) ready.Add(s);
        }
        return ordered.Count == stretches.Count ? ordered : null;
    }

    /// <summary>The route between path coordinates a and b as pieces of one width each, in route order.</summary>
    private static List<(double Width, int Layer, double Length)> Pieces(Route route, double a, double b, double slack)
    {
        var pieces = new List<(double Width, int Layer, double Length)>();
        if (b - a <= slack) return pieces;
        double path = 0;
        foreach (var segment in route.Chain)
        {
            double from = Math.Max(a, path), to = Math.Min(b, path + segment.Length);
            path += segment.Length;
            if (to - from <= 0) continue;
            if (pieces.Count > 0 && Math.Abs(pieces[^1].Width - segment.Width) < 1e-9 && pieces[^1].Layer == segment.LayerOrder)
                pieces[^1] = (segment.Width, segment.LayerOrder, pieces[^1].Length + to - from);
            else
                pieces.Add((segment.Width, segment.LayerOrder, to - from));
        }
        // What the chain does not cover (rounding at its end) goes to the last piece.
        double covered = pieces.Sum(p => p.Length);
        if (pieces.Count > 0 && b - a - covered > 0)
            pieces[^1] = (pieces[^1].Width, pieces[^1].Layer, pieces[^1].Length + b - a - covered);
        return pieces.Where(p => p.Length > slack).ToList();
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
