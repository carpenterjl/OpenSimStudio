using OpenSim.Core.Numerics;

namespace OpenSim.Pcb.Inductance;

/// <summary>Mutual coupling between two conductor chains, and the loop they form when
/// connected in series.</summary>
public sealed record MutualReport(
    double MutualHenries,
    double CouplingK,
    double LoopInductanceHenries,
    bool ReturnTraversedForward,
    IReadOnlyList<string> Assumptions);

/// <summary>
/// Mutual inductance and coupling between two oriented chains, composed pairwise from the
/// exact filament kernel: M = Σᵢ Σⱼ M(aᵢ, bⱼ) signed by each chain's own start → end
/// orientation, and k = |M| / √(L_A·L_B) with L_A, L_B the chains' partial inductances.
/// The series LOOP closure pairs endpoints by proximity — the jumper connects whichever
/// end of B is nearer to A's end — and reports which way B is traversed instead of
/// silently taking the sign that flatters: L_loop = L_A + L_B + 2·s·M.
/// </summary>
public sealed class MutualCouplingAnalyzer
{
    private readonly LoopComposer _composer = new();

    /// <summary>Signed mutual inductance [H] between the chains as oriented (positive
    /// when currents traversing both start → end aid each other).</summary>
    public double MutualBetween(IReadOnlyList<TraceSegment3D> chainA, IReadOnlyList<TraceSegment3D> chainB)
    {
        if (chainA.Count == 0 || chainB.Count == 0)
            throw new InvalidOperationException("Both chains must be non-empty.");
        double m = 0;
        foreach (var a in chainA)
            foreach (var b in chainB)
                m += LoopComposer.MutualTerm(a, b);
        return m;
    }

    /// <summary>Mutual, coupling coefficient, and the series-loop inductance of the pair.</summary>
    public MutualReport Analyze(IReadOnlyList<TraceSegment3D> chainA, IReadOnlyList<TraceSegment3D> chainB)
    {
        double la = _composer.Compose(chainA).LoopInductance;
        double lb = _composer.Compose(chainB).LoopInductance;
        double m = MutualBetween(chainA, chainB);
        double k = Math.Abs(m) / Math.Sqrt(la * lb);

        // Series loop: A start → end, jumper to B's NEARER end, B back to its other end,
        // jumper to A.Start. Traversing B from its own start means forward (s = +1);
        // entering at B's end means backward (s = −1) — the sign is geometric fact, not
        // a choice.
        var aEnd = chainA[^1].End;
        double toBStart = (chainB[0].Start - aEnd).Length;
        double toBEnd = (chainB[^1].End - aEnd).Length;
        bool forward = toBStart <= toBEnd;
        double s = forward ? 1 : -1;
        double loop = la + lb + 2 * s * m;

        var assumptions = new List<string>
        {
            "DC / uniform current distribution (no skin or proximity effect).",
            "Series loop: the return chain is traversed " + (forward ? "start → end" : "end → start") +
            " (endpoint-proximity pairing); the two jumpers closing the loop are not modeled.",
            "Mutual terms are the exact straight-filament Neumann solution (Grover), with a " +
            "geometric-mean-distance correction for parallel finite cross-sections.",
            "k = |M|/√(L_A·L_B) uses the chains' PARTIAL inductances."
        };
        return new MutualReport(m, k, loop, forward, assumptions);
    }
}
