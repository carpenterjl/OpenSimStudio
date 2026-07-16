using OpenSim.Pcb.Import;
using OpenSim.Pcb.Inductance;

namespace OpenSim.Rf.Si;

/// <summary>One report row: a pad pair with BOTH its DC resistance and the net's total
/// capacitance computable, plus the lumped RC time constant. Pairs lacking either value
/// never become rows — they are counted (<see cref="DcNetReport.PairsOmitted"/>) and
/// net-level failures carry their reason on <see cref="DcNetReport.FailureNotes"/>, so
/// nothing is dropped silently. <see cref="Note"/> is the rare caveat on a kept row
/// (e.g. two pads inside one junction read R = 0).</summary>
public sealed record DcNetRow(
    string Net,
    string PadA,
    string? PartA,
    string PadB,
    string? PartB,
    double ResistanceOhms,
    double CapacitanceFarads,
    double TimeConstantSeconds,
    string? Note);

/// <summary>The board-wide DC net evaluation: complete (R and C) pad-pair rows, plus the
/// sweep's counts, per-net failure reasons, and stated assumptions.</summary>
public sealed record DcNetReport(
    string BoardName,
    IReadOnlyList<DcNetRow> Rows,
    int NetsEvaluated,
    int NetsSkipped,
    int NetsFailed,
    int PairsOmitted,
    IReadOnlyList<string> FailureNotes,
    IReadOnlyList<string> Assumptions);

/// <summary>
/// The "Evaluate DC nets" sweep: for every net on the board with at least two pads
/// (fewer is an unusable import artifact — skipped and counted), compute the DC
/// resistance between every unordered pad pair by the <see cref="TraceResistanceNetwork"/>
/// nodal solve, the net's total capacitance to the reference plane by the S12
/// <see cref="TraceCapacitanceExtractor"/>, and the lumped screen τ = R(pair) × C(net) —
/// the whole net's capacitance charged through that pair's path resistance, an
/// order-of-magnitude RC SCREEN, not a distributed/Elmore delay (stated). Only pairs
/// with BOTH values become rows; incomplete pairs and non-conforming nets (pours,
/// unreachable pads, no reference gap) are counted with their reasons — the sweep never
/// aborts and never emits a garbage number.
/// </summary>
public static class DcNetEvaluator
{
    /// <summary>
    /// Runs the sweep. Nets are evaluated in parallel into ordered slots and composed
    /// sequentially in board-net order (the Stage G recipe), so the report is bitwise
    /// identical at any degree of parallelism.
    /// </summary>
    public static DcNetReport Evaluate(PcbBoard board, NetMeshOptions meshOptions,
        BoardCoupledOptions? options = null, string boardName = "",
        int? maxDegreeOfParallelism = null)
    {
        options ??= new BoardCoupledOptions();

        var nets = board.Nets;
        var slots = new (List<DcNetRow> Rows, int Omitted, bool Skipped, string? Failure)[nets.Count];
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1
        };
        Parallel.For(0, nets.Count, parallelOptions,
            i => slots[i] = EvaluateNet(board, nets[i], meshOptions, options));

        var rows = new List<DcNetRow>();
        var failureNotes = new List<string>();
        int evaluated = 0, skipped = 0, omitted = 0;
        foreach (var (netRows, netOmitted, wasSkipped, failure) in slots)
        {
            if (wasSkipped) { skipped++; continue; }
            if (failure is not null) { failureNotes.Add(failure); continue; }
            evaluated++;
            omitted += netOmitted;
            rows.AddRange(netRows);
        }

        var assumptions = new List<string>
        {
            "resistance: DC nodal network on trace centerlines — R = ρℓ/A per segment, "
                + "pads as equipotential attachment points, corners/necks unmodeled, no skin effect "
                + "(the FE field solve remains the per-net precision tool)",
            "capacitance: the net alone over an infinite reference plane — traces Σ C′(width, gap)·length "
                + "+ pad plates ε₀εr·A/h (no fringing on pads, a stated lower bound); other nets absent",
            "time constant: lumped RC screen τ = R(pad pair) × C(whole net) — not a distributed/Elmore delay",
            "pads are named refdes.pin (part name from the file's Component data) when the source "
                + "declares them (IPC-2581); otherwise P{index} L{layer} (x;y)mm is synthesized",
            $"conductivity {options.ConductivitySiemensPerMeter:g3} S/m, copper thickness from the stackup",
        };

        return new DcNetReport(boardName, rows, evaluated, skipped, failureNotes.Count,
            omitted, failureNotes, assumptions);
    }

    private static (List<DcNetRow> Rows, int Omitted, bool Skipped, string? Failure) EvaluateNet(
        PcbBoard board, CopperNet net, NetMeshOptions meshOptions, BoardCoupledOptions options)
    {
        string label = net.Name ?? $"Net {net.Id}";
        try
        {
            var pads = NetTraceExtractor.PadsForNet(board, net);
            if (pads.Count < 2)
                return (new List<DcNetRow>(), 0, Skipped: true, null);

            // The pad layers widen the stackup span so a pad on a traceless layer maps
            // to the honest "no junction within span" note, never "outside the stackup".
            var graph = TraceChainBuilder.BuildGraph(
                NetTraceExtractor.ForNet(board, net), net.StitchingVias, meshOptions,
                net.Islands, pads.Select(p => p.LayerOrder).Distinct().ToList());
            var resistance = TraceResistanceNetwork.Solve(graph,
                pads.Select(p => new ChainTerminal(p.Center, p.LayerOrder)).ToList(),
                options.ConductivitySiemensPerMeter);
            if (resistance.FailureReason is not null)
                return (new List<DcNetRow>(), 0, false, $"{label} — {resistance.FailureReason}");

            // Rows require BOTH values: a net whose C has no reference plane yields no
            // rows at all — its reason lands on the failure notes, never lost.
            var capacitance = TraceCapacitanceExtractor.Extract(board, net, options);
            if (capacitance.FailureReason is not null)
                return (new List<DcNetRow>(), 0, false,
                    $"{label} — C not computable: {capacitance.FailureReason}");

            var rows = new List<DcNetRow>();
            int omitted = 0;
            foreach (var pair in resistance.Pairs)
            {
                if (pair.ResistanceOhms is not { } r) { omitted++; continue; }
                rows.Add(new DcNetRow(label,
                    PadLabel(pads, pair.PadA), pads[pair.PadA].PartName,
                    PadLabel(pads, pair.PadB), pads[pair.PadB].PartName,
                    r, capacitance.TotalFarads, r * capacitance.TotalFarads, pair.Note));
            }
            return (rows, omitted, false, null);
        }
        catch (Exception ex)
        {
            // The sweep must survive any single pathological net; unwrap the parallel
            // wrapper so the note names the real reason (the S12 precedent).
            string reason = ex is AggregateException { InnerException: { } inner }
                ? inner.Message : ex.Message;
            return (new List<DcNetRow>(), 0, false, $"{label} — {reason}");
        }
    }

    /// <summary>The pad's refdes.pin when the file declared one (IPC-2581 PinRef);
    /// otherwise a synthesized identity — index in the net's pad list, layer, and
    /// position in mm, with a semicolon coordinate separator so the label never needs
    /// CSV quoting. Gerber never carries pad names (%TO attributes are discarded).</summary>
    private static string PadLabel(IReadOnlyList<CopperPad> pads, int index)
    {
        var pad = pads[index];
        if (pad.ComponentRef is { } refDes)
            return pad.Pin is { } pin ? $"{refDes}.{pin}" : refDes;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"P{index} L{pad.LayerOrder} ({pad.Center.X * 1e3:0.###};{pad.Center.Y * 1e3:0.###})mm");
    }
}
