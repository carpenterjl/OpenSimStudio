using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;
using OpenSim.Rf.Layered;

namespace OpenSim.Rf.Si;

/// <summary>One (layer, width) group of a net's routed copper: the summed centerline
/// length carried at this cross-section, and the 2D BEM's per-unit-length capacitances
/// over that layer's adjacent dielectric gap.</summary>
public sealed record TraceCapacitanceGroup(
    int LayerOrder,
    double WidthMeters,
    double LengthMeters,
    double CapacitanceFaradsPerMeter,
    double AirCapacitanceFaradsPerMeter)
{
    /// <summary>ε_eff = C′/C′_air — the usual microstrip effective permittivity.</summary>
    public double EffectivePermittivity =>
        AirCapacitanceFaradsPerMeter > 0
            ? CapacitanceFaradsPerMeter / AirCapacitanceFaradsPerMeter : 0;

    /// <summary>This group's contribution to the net total: C′ × length.</summary>
    public double TotalFarads => CapacitanceFaradsPerMeter * LengthMeters;
}

/// <summary>
/// A net's capacitance to the reference plane, or a typed failure naming the
/// non-conforming topology. Exactly one of <see cref="Groups"/> (non-empty) /
/// <see cref="FailureReason"/> is meaningful.
/// </summary>
public sealed record TraceCapacitanceResult(
    double TotalFarads,
    double TraceFarads,
    double PadFarads,
    int PadCount,
    IReadOnlyList<TraceCapacitanceGroup> Groups,
    IReadOnlyList<string> Assumptions,
    string? FailureReason)
{
    public static TraceCapacitanceResult Failure(string reason) =>
        new(0, 0, 0, 0, Array.Empty<TraceCapacitanceGroup>(), Array.Empty<string>(), reason);
}

/// <summary>
/// Per-trace capacitance to the ground/reference plane from a net's FULL routed copper —
/// the electrostatic complement of the S6 coupled-section extraction. Capacitance is
/// electrostatics: ALL of the net's copper holds charge, so unlike the inductance chain
/// (a current path — branches pruned because they carry no current) this consumes every
/// centerline of the net, bends and branches included, and totals
/// C = Σ over (layer, width) groups of C′(width, gap) × summed length. C′ comes from the
/// SAME 1-conductor <see cref="RlgcExtractor"/> BEM the SI wizard uses, over the
/// cross-section the board really has on that layer: <see cref="BoardReferencePlanes"/>
/// finds the copper planes above and below the traces, so an inner layer between two
/// planes is priced as a stripline, not as a microstrip with air above (the S6 coupled
/// extraction shares the same resolver). Translational invariance of the uniform cross-section makes bends exact
/// for same-width runs; corner effects are ignored and SAID so (the PEEC arcs-as-chords
/// class of assumption).
///
/// <para>Pads add a parallel-plate term ε₀εr·A/h (polygon area over the same gap) — a
/// stated no-fringing lower bound; ignoring pads silently would misread short pad-heavy
/// nets. Other nets are absent from the model (trace + infinite reference plane only),
/// via barrels carry no capacitance, and a pour/region net (no centerlines) is a typed
/// failure — an area sheet needs an area model, and a garbage number is worse than none.</para>
/// </summary>
public static class TraceCapacitanceExtractor
{
    private const double Epsilon0 = 8.8541878128e-12;

    public static TraceCapacitanceResult Extract(PcbBoard board, CopperNet net,
        BoardCoupledOptions? options = null)
    {
        options ??= new BoardCoupledOptions();

        var centerlines = NetTraceExtractor.ForNet(board, net);
        if (centerlines.Count == 0)
            return TraceCapacitanceResult.Failure(
                $"net '{net.Label}' has no trace centerlines — a pour/region net needs an "
                + "area model; per-trace capacitance covers drawn traces.");

        // The chain builder's coincident-draw rule, reused verbatim: copper drawn twice
        // (pad entries, per-netclass replays) must not hold charge twice. Same tolerance
        // convention too (half the narrowest width = the junction scale).
        double tolerance = centerlines.Min(c => c.Width) / 2;
        var traces = TraceChainBuilder.Deduplicate(centerlines.ToList(), tolerance);
        int duplicatesDropped = centerlines.Count - traces.Count;

        // One cross-section per layer that carries traces: the reference planes the board
        // has under and over this net's traces on that layer (shared with S6).
        var substrates = new Dictionary<int, BoardSubstrate>();
        foreach (int layer in traces.Select(t => t.LayerOrder).Distinct().OrderBy(l => l))
        {
            var substrate = BoardReferencePlanes.Resolve(board, layer,
                traces.Where(t => t.LayerOrder == layer).ToList(), net.Islands, options,
                out string planeFailure);
            if (substrate is null)
                return TraceCapacitanceResult.Failure($"net '{net.Label}': {planeFailure}");
            substrates[layer] = substrate;
        }

        // Group the routed length by (layer, width): C′ is a pure cross-section property,
        // so within a group total C = C′ × summed length exactly (translation invariance).
        var groupKeys = new List<(int Layer, double Width)>();
        var groupLength = new Dictionary<(int, double), double>();
        foreach (var t in traces)
        {
            var key = (t.LayerOrder, t.Width);
            if (!groupLength.ContainsKey(key)) { groupKeys.Add(key); groupLength[key] = 0; }
            groupLength[key] += t.Length;
        }
        groupKeys.Sort();   // deterministic composition order, independent of draw order

        // One 1-conductor BEM solve per distinct cross-section — independent, so the
        // Stage G recipe applies: parallel solves into ordered slots, sequential compose.
        var boardStackup = BoardCoupledExtractor.StackupOf(board, options);
        var groups = new TraceCapacitanceGroup[groupKeys.Count];
        try
        {
            Parallel.For(0, groupKeys.Count, i =>
            {
                var (layer, width) = groupKeys[i];
                var substrate = substrates[layer];
                var section = new CoupledLineCrossSection(substrate.Stackup, substrate.MetalInterface,
                    new[] { new TraceCrossSection(0, width, boardStackup.CopperThicknessOf(layer),
                        options.ConductivitySiemensPerMeter) }, substrate.TopGround);
                var rlgc = RlgcExtractor.Extract(section);
                groups[i] = new TraceCapacitanceGroup(layer, width, groupLength[(layer, width)],
                    rlgc.CapacitanceFaradsPerMeter[0, 0], rlgc.AirCapacitanceFaradsPerMeter[0, 0]);
            });
        }
        catch (AggregateException ex) when (ex.InnerException is ArgumentException arg)
        {
            return TraceCapacitanceResult.Failure(arg.Message);
        }

        double traceFarads = 0;
        foreach (var g in groups) traceFarads += g.TotalFarads;

        // Pads: parallel-plate ε₀εr·A/h to each reference plane of the pad's layer (in
        // series through the gaps in between). An ESTIMATE, not a bound: no fringing (which
        // would add), and the trace term already counts the centerline length that runs to
        // each pad's centre (which the pad's own area counts again).
        // Pads on a layer with no plane under them are counted and NAMED, never
        // silently dropped (the Gerber warn-not-silent rule).
        double padFarads = 0;
        int padCount = 0, padsSkipped = 0;
        var netPads = NetTraceExtractor.PadsForNet(board, net);
        foreach (var pad in netPads)
        {
            if (!substrates.TryGetValue(pad.LayerOrder, out var sub))
            {
                // A layer the net has pads on but no traces: the planes under those pads.
                var resolved = BoardReferencePlanes.Resolve(board, pad.LayerOrder,
                    netPads.Where(p => p.LayerOrder == pad.LayerOrder).Select(p => p.Center).ToList(),
                    net.Islands, options, out _);
                if (resolved is null) { padsSkipped++; continue; }
                sub = resolved;
                substrates[pad.LayerOrder] = sub;
            }
            padFarads += sub.PlateCapacitancePerSquareMeter * pad.Shape.Area();
            padCount++;
        }

        var assumptions = new List<string>
        {
            "Electrostatic per-trace model: C = Σ C′(width, gap) × routed centerline length "
                + "over every (layer, width) group — bends are exact by translational "
                + "invariance of the uniform cross-section; corner/junction effects and via "
                + "barrels are ignored (stated, like PEEC's arcs-as-chords). Branches DO "
                + "count: all of the net's copper holds charge.",
            "The net is modeled alone against the reference plane(s) found above and below "
                + "each trace layer, taken as infinite and solid — other nets are absent, so "
                + "shielding by neighbours is not modeled and the number is the isolated-net "
                + "capacitance to those planes.",
            $"Pads add parallel-plate ε₀εr·A/h terms to each plane ({padCount} pads) — no "
                + "fringing, and overlapping the trace length that already runs to each pad's "
                + "centre: an estimate of the pad contribution, not a bound.",
        };
        if (duplicatesDropped > 0)
            assumptions.Add($"{duplicatesDropped} coincident duplicate draw(s) collapsed "
                + "(wider wins) so redrawn copper is not double-counted.");
        if (padsSkipped > 0)
            assumptions.Add($"{padsSkipped} pad(s) sit on a layer with no reference plane "
                + "under or over them and are OMITTED from the pad term.");
        foreach (var layer in substrates.Keys.OrderBy(l => l))
            assumptions.Add(substrates[layer].Note);

        return new TraceCapacitanceResult(traceFarads + padFarads, traceFarads, padFarads,
            padCount, groups, assumptions, null);
    }
}
