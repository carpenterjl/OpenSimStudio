using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Pcb.Inductance;

/// <summary>One unordered pad pair's DC resistance, or the reason it has none. Pad
/// indices refer to the terminal list handed to the solve. <see cref="ResistanceOhms"/>
/// is null exactly when <see cref="Note"/> explains why (disconnected, unmapped pad);
/// a same-junction pair reports 0 WITH a note — real, but below the model's scale.</summary>
public sealed record PadPairResistance(int PadA, int PadB, double? ResistanceOhms, string? Note);

/// <summary>The per-net DC resistance solve's outcome: every unordered pad pair once,
/// plus the stated model assumptions, or a typed failure. Never a garbage number.</summary>
public sealed record NetResistanceResult(
    IReadOnlyList<PadPairResistance> Pairs,
    IReadOnlyList<string> Assumptions,
    string? FailureReason)
{
    public static NetResistanceResult Failure(string reason) =>
        new(Array.Empty<PadPairResistance>(), Array.Empty<string>(), reason);
}

/// <summary>
/// DC resistance between pad pairs by NODAL ANALYSIS on the trace graph — the "network
/// solve" the chain builder's typed parallel-path failure asks for. Nodes are the chain
/// junctions, each segment an edge of conductance G = σ·A/ℓ, and the pad-pair resistance
/// is the two-point resistance of the resulting conductance Laplacian — exact for the
/// lumped model on ANY topology (branches, parallel paths, cycles), where the single-path
/// chain extraction is either restrictive or wrong. Model assumptions (printed, the PEEC
/// precedent): pads are equipotential attachment points (no pad spreading resistance),
/// segments carry uniform current over the centerline cross-section (corners and necks
/// unmodeled — the arcs-as-chords class), copper bridges are straight bars, DC only.
/// The FE field solve remains the precision tool for a single net; this is the
/// board-sweep screen.
/// </summary>
public static class TraceResistanceNetwork
{
    /// <summary>
    /// Solves every unordered pad pair's DC resistance over the graph. Pads that map to
    /// no junction (no drawn copper within the pad-scale span on their layer) and pairs
    /// on disconnected pieces come back as typed notes, never numbers.
    /// </summary>
    /// <param name="graph">A successful <see cref="TraceChainBuilder.BuildGraph"/> result.</param>
    /// <param name="pads">The net's pad terminals; pair indices refer to this list.</param>
    /// <param name="conductivity">Conductor conductivity σ [S/m].</param>
    public static NetResistanceResult Solve(TraceGraphResult graph,
        IReadOnlyList<ChainTerminal> pads, double conductivity)
    {
        if (graph.FailureReason is not null)
            return NetResistanceResult.Failure(graph.FailureReason);
        if (pads.Count < 2)
            return NetResistanceResult.Failure("at least two pads are required for a pad-pair resistance");
        if (!(conductivity > 0))
            return NetResistanceResult.Failure("the conductivity must be positive");

        var segments = graph.Segments!;
        var ends = graph.Ends!;
        var junctions = graph.Junctions!;

        // Pad → junction by the chain builder's own attachment rule (one contract).
        // The label reads as "the #3 pad at (…)" inside MapTerminal's message.
        var padNode = new int[pads.Count];
        var padNote = new string?[pads.Count];
        for (int i = 0; i < pads.Count; i++)
        {
            padNode[i] = TraceChainBuilder.MapTerminal(pads[i], $"#{i}",
                junctions, graph.LayerZ!, out var failure);
            padNote[i] = failure;
        }

        // Connected components over the segment graph (union-find, path compression).
        var piece = new int[junctions.Count];
        for (int i = 0; i < piece.Length; i++) piece[i] = i;
        int Find(int j) => piece[j] == j ? j : piece[j] = Find(piece[j]);
        foreach (var (a, b) in ends) piece[Find(a)] = Find(b);

        // One grounded reduced Laplacian solve per component holding ≥2 mapped pads.
        // Grounding is per component — a single global ground would leave every other
        // component's block exactly singular. Potentials are solved once per PAD (unit
        // current in at the pad, out at the ground node), and the pair resistance is the
        // energy form R(p,q) = (e_p − e_q)ᵀ L⁺ (e_p − e_q) = x_p[p] − x_p[q] − x_q[p] + x_q[q],
        // symmetric under p↔q by construction.
        var potentials = new Dictionary<int, double[]>();   // pad index → nodal potentials
        var pairResistance = new Dictionary<(int, int), double>();
        foreach (var component in padNode.Where(n => n >= 0).Select(Find).Distinct().OrderBy(c => c))
        {
            var members = Enumerable.Range(0, junctions.Count).Where(j => Find(j) == component).ToList();
            var padIndices = Enumerable.Range(0, pads.Count)
                .Where(i => padNode[i] >= 0 && Find(padNode[i]) == component).ToList();
            if (padIndices.Count < 2)
                continue;                                    // nothing to pair inside this piece

            int ground = members[0];                         // lowest junction index — deterministic
            var local = new Dictionary<int, int>();          // junction → reduced row
            foreach (int j in members)
                if (j != ground)
                    local[j] = local.Count;

            int n = local.Count;
            var matrix = new ComplexDenseMatrix(n, n);
            // Stamp in segment-index order — a fixed FP accumulation order keeps the
            // assembly bitwise-deterministic (never iterate a Dictionary here).
            for (int e = 0; e < segments.Count; e++)
            {
                var (a, b) = ends[e];
                if (Find(a) != component) continue;
                double g = conductivity * CrossSectionArea(segments[e]) / segments[e].Length;
                bool ra = local.TryGetValue(a, out int ia);
                bool rb = local.TryGetValue(b, out int ib);
                if (ra) matrix[ia, ia] += g;
                if (rb) matrix[ib, ib] += g;
                if (ra && rb)
                {
                    matrix[ia, ib] -= g;
                    matrix[ib, ia] -= g;
                }
            }

            var lu = ComplexLu.Factor(matrix);
            foreach (int p in padIndices)
            {
                if (padNode[p] == ground)
                {
                    potentials[p] = new double[n];           // injecting at the ground: all zeros
                    continue;
                }
                var rhs = new Complex[n];
                rhs[local[padNode[p]]] = Complex.One;
                var x = lu.Solve(rhs);
                var real = new double[n];
                for (int k = 0; k < n; k++) real[k] = x[k].Real;
                potentials[p] = real;
            }

            // Local closure to read a junction's potential (ground reads 0 by definition).
            double At(int p, int junction) =>
                junction == ground ? 0 : potentials[p][local[junction]];
            foreach (int p in padIndices)
                foreach (int q in padIndices)
                    if (p < q && padNode[p] != padNode[q])
                        pairResistance[(p, q)] = At(p, padNode[p]) - At(p, padNode[q])
                                               - At(q, padNode[p]) + At(q, padNode[q]);
        }

        // Compose every unordered pair once, in i<j order, with typed notes.
        var pairs = new List<PadPairResistance>();
        for (int i = 0; i < pads.Count; i++)
            for (int j = i + 1; j < pads.Count; j++)
            {
                if (padNote[i] is not null)
                    pairs.Add(new PadPairResistance(i, j, null, padNote[i]));
                else if (padNote[j] is not null)
                    pairs.Add(new PadPairResistance(i, j, null, padNote[j]));
                else if (padNode[i] == padNode[j])
                    pairs.Add(new PadPairResistance(i, j, 0,
                        "pads coincide at one chain junction (below the model's junction scale)"));
                else if (Find(padNode[i]) != Find(padNode[j]))
                    pairs.Add(new PadPairResistance(i, j, null,
                        "not connected by drawn traces/vias"));
                else
                    pairs.Add(new PadPairResistance(i, j, pairResistance[(i, j)], null));
            }

        var assumptions = new List<string>
        {
            "DC nodal network on trace centerlines: R = ρℓ/A per segment " +
            "(corners/necks unmodeled; pads are equipotential attachment points)",
            "no skin/proximity effect (DC only)",
        };
        if (graph.CopperBridges > 0)
            assumptions.Add($"{graph.CopperBridges} gap(s) bridged with straight bars through connecting copper");

        return new NetResistanceResult(pairs, assumptions, null);
    }

    /// <summary>Conducting cross-section per profile. A RoundTube's Width is the MEAN
    /// SHELL diameter (bore + one plating wall — the chain builder's convention), so the
    /// mean-circumference form π·t·W IS the exact annulus π((r+t)² − r²), r = bore/2.</summary>
    private static double CrossSectionArea(TraceSegment3D segment) => segment.Profile switch
    {
        SegmentProfile.RoundTube => Math.PI * segment.Thickness * segment.Width,
        SegmentProfile.RoundWire => Math.PI * segment.Width * segment.Width / 4,
        _ => segment.Width * segment.Thickness,
    };
}
