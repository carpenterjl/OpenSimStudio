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
/// The "Evaluate DC nets" sweep. Only measurements that START AND END AT A COMPONENT are
/// reported: each reported pad must trace back to a package/pin (an IPC-2581 PinRef), so
/// via landing pads and Gerber flashes — which carry no component identity — never
/// terminate a row. This makes the report an IPC-2581-specific feature: a Gerber board
/// (no PinRef anywhere) yields no rows, every net skipped.
///
/// For every net with at least two COMPONENT PINS (fewer is skipped and counted), it
/// computes the DC resistance between every unordered pin pair by the
/// <see cref="TraceResistanceNetwork"/> nodal solve (over the net's FULL copper graph —
/// via landings and traces still conduct, they just don't terminate a measurement), the
/// net's total capacitance to the reference plane by the S12
/// <see cref="TraceCapacitanceExtractor"/>, and the lumped screen τ = R(pair) × C(net) —
/// the whole net's capacitance charged through that pair's path resistance, an
/// order-of-magnitude RC SCREEN, not a distributed/Elmore delay (stated). Only pairs
/// with BOTH values become rows; incomplete pairs and non-conforming nets (pours,
/// unreachable pins, no reference gap) are counted with their reasons — the sweep never
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
            "scope: only pairs whose BOTH pads are component pins (IPC-2581 PinRef → refdes.pin) "
                + "are reported — via landings and Gerber flashes carry no component identity, so a "
                + "Gerber board yields no rows and every net is skipped",
            "resistance: DC nodal network on trace centerlines — R = ρℓ/A per segment, "
                + "pins as equipotential attachment points, corners/necks unmodeled, no skin effect "
                + "(the FE field solve remains the per-net precision tool)",
            "capacitance: the net alone over an infinite reference plane — traces Σ C′(width, gap)·length "
                + "+ pad plates ε₀εr·A/h (no fringing on pads, a stated lower bound); other nets absent",
            "time constant: lumped RC screen τ = R(pin pair) × C(whole net) — not a distributed/Elmore delay",
            "part name is the file's Component part (footprint package as fallback) when present",
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
            // Only component pins are measured: a reported endpoint must trace back to a
            // package/pin (IPC-2581 PinRef). Via landing pads and Gerber flashes have no
            // component identity, so a net with fewer than two component pins produces no
            // rows — skipped and counted (Gerber boards land entirely here, by design).
            var pins = pads.Where(p => p.ComponentRef is not null).ToList();
            if (pins.Count < 2)
                return (new List<DcNetRow>(), 0, Skipped: true, null);

            // The graph spans EVERY pad's layer (via landings included) so the conduction
            // path is complete; only the reported terminals narrow to the component pins.
            var graph = TraceChainBuilder.BuildGraph(
                NetTraceExtractor.ForNet(board, net), net.StitchingVias, meshOptions,
                net.Islands, pads.Select(p => p.LayerOrder).Distinct().ToList());
            var resistance = TraceResistanceNetwork.Solve(graph,
                pins.Select(p => new ChainTerminal(p.Center, p.LayerOrder)).ToList(),
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
                    PadLabel(pins, pair.PadA), pins[pair.PadA].PartName,
                    PadLabel(pins, pair.PadB), pins[pair.PadB].PartName,
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

    /// <summary>The reported pin's refdes.pin (IPC-2581 PinRef) — every reported pad is a
    /// component pin by construction, so <see cref="CopperPad.ComponentRef"/> is set. The
    /// coordinate fallback survives only for the odd component pad missing a pin number,
    /// where the refdes alone still names the component.</summary>
    private static string PadLabel(IReadOnlyList<CopperPad> pins, int index)
    {
        var pad = pins[index];
        string refDes = pad.ComponentRef!;
        return pad.Pin is { } pin ? $"{refDes}.{pin}" : refDes;
    }
}
