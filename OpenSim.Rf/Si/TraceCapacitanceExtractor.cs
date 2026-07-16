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
/// SAME 1-conductor <see cref="RlgcExtractor"/> BEM the SI wizard uses (the layered
/// electrostatic image series — the dielectric gap's real εr, tanδ and thickness), and
/// the substrate resolution is the S6 rule verbatim (gap below the trace layer preferred,
/// then above). Translational invariance of the uniform cross-section makes bends exact
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

        // One substrate per layer that carries copper (the S6 gap rule, shared code).
        var substrates = new Dictionary<int, (LayeredStackup Stackup, string Note)>();
        foreach (int layer in traces.Select(t => t.LayerOrder).Distinct().OrderBy(l => l))
        {
            var stackup = BoardCoupledExtractor.ResolveSubstrate(board, layer, options, out string note);
            if (stackup is null)
                return TraceCapacitanceResult.Failure(
                    $"no dielectric gap is adjacent to trace layer L{layer} — capacitance "
                    + "to ground needs a reference plane above or below the traces.");
            substrates[layer] = (stackup, note);
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
        var groups = new TraceCapacitanceGroup[groupKeys.Count];
        try
        {
            Parallel.For(0, groupKeys.Count, i =>
            {
                var (layer, width) = groupKeys[i];
                var section = new CoupledLineCrossSection(substrates[layer].Stackup, 0,
                    new[] { new TraceCrossSection(0, width, options.CopperThicknessMeters,
                        options.ConductivitySiemensPerMeter) });
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

        // Pads: parallel-plate ε₀εr·A/h over the pad layer's gap. No fringing — a stated
        // lower bound (fringing only adds). Pads on a layer without an adjacent gap are
        // counted and NAMED, never silently dropped (the Gerber warn-not-silent rule).
        double padFarads = 0;
        int padCount = 0, padsSkipped = 0;
        foreach (var pad in NetTraceExtractor.PadsForNet(board, net))
        {
            if (!substrates.TryGetValue(pad.LayerOrder, out var sub))
            {
                var stackup = BoardCoupledExtractor.ResolveSubstrate(board, pad.LayerOrder,
                    options, out string note);
                if (stackup is null) { padsSkipped++; continue; }
                sub = (stackup, note);
                substrates[pad.LayerOrder] = sub;
            }
            var gap = sub.Stackup.Layers[0];
            padFarads += Epsilon0 * gap.RelativePermittivity * pad.Shape.Area()
                / gap.ThicknessMeters;
            padCount++;
        }

        var assumptions = new List<string>
        {
            "Electrostatic per-trace model: C = Σ C′(width, gap) × routed centerline length "
                + "over every (layer, width) group — bends are exact by translational "
                + "invariance of the uniform cross-section; corner/junction effects and via "
                + "barrels are ignored (stated, like PEEC's arcs-as-chords). Branches DO "
                + "count: all of the net's copper holds charge.",
            "The net is modeled alone over an infinite reference plane at the adjacent "
                + "stackup gap — other nets are absent, so shielding by neighbours is not "
                + "modeled and the number is the isolated-net capacitance to ground.",
            $"Pads add parallel-plate ε₀εr·A/h terms ({padCount} pads) — no fringing, a "
                + "stated lower bound for the pad contribution.",
        };
        if (duplicatesDropped > 0)
            assumptions.Add($"{duplicatesDropped} coincident duplicate draw(s) collapsed "
                + "(wider wins) so redrawn copper is not double-counted.");
        if (padsSkipped > 0)
            assumptions.Add($"{padsSkipped} pad(s) sit on a layer with no adjacent "
                + "dielectric gap and are OMITTED from the pad term.");
        foreach (var layer in substrates.Keys.OrderBy(l => l))
            assumptions.Add(substrates[layer].Note);

        return new TraceCapacitanceResult(traceFarads + padFarads, traceFarads, padFarads,
            padCount, groups, assumptions, null);
    }
}
