using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;

namespace OpenSim.Rf.Si;

/// <summary>
/// One net's trace as a single transmission line, or a typed failure. Exactly one of
/// <see cref="Mode"/> / <see cref="FailureReason"/> is non-null.
/// </summary>
/// <param name="LayerOrder">The copper layer the line runs on.</param>
/// <param name="WidthMeters">The trace width the cross-section was solved at.</param>
/// <param name="LengthMeters">The routed centerline length at that (layer, width).</param>
/// <param name="OtherLengthMeters">The net's routed length at other widths or on other
/// layers, which is not in the cross-section.</param>
public sealed record TraceLineResult(
    LineMode? Mode,
    RlgcResult? Rlgc,
    int LayerOrder,
    double WidthMeters,
    double LengthMeters,
    double OtherLengthMeters,
    IReadOnlyList<string> Assumptions,
    string? FailureReason)
{
    public static TraceLineResult Failure(string reason) =>
        new(null, null, 0, 0, 0, 0, Array.Empty<string>(), reason);
}

/// <summary>
/// The per-unit-length line a net's routed trace forms over the board's own reference
/// planes — the stackup-and-impedance calculator's answer, read from the layout instead of
/// typed in. The net's routed copper is grouped by (layer, width) as the capacitance
/// extractor does; the group carrying the most length is the line, and its cross-section
/// is the one <see cref="BoardReferencePlanes"/> finds for that layer (microstrip,
/// embedded microstrip or stripline, the planes taken as infinite and solid). The same
/// 1-conductor <see cref="RlgcExtractor"/> solve and the same mode arithmetic as
/// <see cref="ImpedanceCalculator"/>, so a single-gap microstrip read from a board and the
/// calculator's microstrip of the same numbers agree exactly. Coupling to other nets, the
/// other (layer, width) groups, bends and vias are not in the line and are stated.
/// </summary>
public static class BoardTraceLine
{
    public static TraceLineResult Extract(PcbBoard board, CopperNet net, BoardCoupledOptions? options = null,
        double frequencyHz = 1e9, int panelsPerTrace = 48)
    {
        options ??= new BoardCoupledOptions();
        if (!(frequencyHz > 0)) throw new ArgumentOutOfRangeException(nameof(frequencyHz), "The report frequency must be positive.");

        var centerlines = NetTraceExtractor.ForNet(board, net);
        if (centerlines.Count == 0)
            return TraceLineResult.Failure(
                $"net '{net.Label}' has no trace centerlines — a pour/region net is not a transmission line.");
        double tolerance = centerlines.Min(c => c.Width) / 2;
        var traces = TraceChainBuilder.Deduplicate(centerlines.ToList(), tolerance);

        // The (layer, width) group with the most routed length is the line; ties go to the
        // lower layer, then the wider trace, so the choice does not depend on draw order.
        var groups = traces.GroupBy(t => (t.LayerOrder, t.Width))
            .Select(g => (g.Key.LayerOrder, g.Key.Width, Length: g.Sum(t => t.Length)))
            .OrderByDescending(g => g.Length).ThenBy(g => g.LayerOrder).ThenByDescending(g => g.Width).ToList();
        var (layer, width, length) = groups[0];
        double other = groups.Skip(1).Sum(g => g.Length);

        var substrate = BoardReferencePlanes.Resolve(board, layer,
            traces.Where(t => t.LayerOrder == layer && t.Width == width).ToList(), net.Islands, options,
            out string planeFailure);
        if (substrate is null) return TraceLineResult.Failure($"net '{net.Label}': {planeFailure}");

        var stackup = BoardCoupledExtractor.StackupOf(board, options);
        var section = new CoupledLineCrossSection(substrate.Stackup, substrate.MetalInterface,
            new[] { new TraceCrossSection(0, width, stackup.CopperThicknessOf(layer), options.ConductivitySiemensPerMeter) },
            substrate.TopGround);
        var rlgc = RlgcExtractor.Extract(section, options.Model, panelsPerTrace);
        var mode = ImpedanceCalculator.SingleMode(rlgc, frequencyHz);

        var assumptions = new List<string>
        {
            $"The line is the net's routed copper on L{layer} at {width * 1e3:g3} mm width ({length * 1e3:g4} mm of it)" +
            (other > 0
                ? $"; {other * 1e3:g4} mm at other widths or on other layers is not in the cross-section."
                : "; all of the net's routed copper is at this width."),
            "The trace is solved alone over its reference plane(s): other nets, bends, pads and via barrels are not in the line.",
            substrate.Note
        };
        assumptions.AddRange(rlgc.Assumptions);
        return new TraceLineResult(mode, rlgc, layer, width, length, other, assumptions, null);
    }
}
