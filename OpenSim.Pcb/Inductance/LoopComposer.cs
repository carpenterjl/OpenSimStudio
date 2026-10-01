using OpenSim.Core.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;

namespace OpenSim.Pcb.Inductance;

/// <summary>Per-segment and total inductance results for a trace chain.</summary>
public sealed record InductanceReport(
    IReadOnlyList<double> SelfInductances,
    double LoopInductance,
    IReadOnlyList<string> Assumptions)
{
    public double TotalSelf => SelfInductances.Sum();
}

/// <summary>
/// Composes the inductance of a current-carrying chain of conductor segments as
/// L = Σ Lᵢᵢ + 2·Σ_{i&lt;j} Mᵢⱼ, signed by the segments' own start→end directions.
/// PARALLEL rectangular bars — side-by-side, stacked, collinear, staggered — take the
/// same finite-section kernel as the bar self terms (<see cref="PartialInductance.BarBarMutual"/>),
/// so a straight run composes to one value however it is subdivided. Every other pair
/// (oblique bars, round wires, via barrels) is the exact straight-filament Neumann
/// solution (<see cref="FilamentMutual"/>). For an OPEN chain the result is the chain's
/// PARTIAL inductance (no return conductor); a physical loop value needs the return
/// composed in explicitly.
/// </summary>
public sealed class LoopComposer
{
    /// <summary>Statement of the bar-pair model, shared by every composer that uses
    /// <see cref="MutualTerm"/>.</summary>
    internal const string MutualModelAssumption =
        "Parallel rectangular bars (self, side-by-side, stacked, collinear) use the exact " +
        "uniform-current finite-section kernel (Hoer–Love); non-parallel pairs and round " +
        "profiles use the exact straight-filament Neumann solution (Grover).";

    /// <summary>The remaining approximation of the bar model.</summary>
    internal const string BendAssumption =
        "Finite-section effects at bends are not modelled: non-parallel neighbours couple at " +
        "the filament level, and perpendicular ones not at all.";

    private static readonly string[] StandardAssumptions =
    {
        "DC / uniform current distribution (no skin or proximity effect).",
        "Non-magnetic media (µ = µ₀); no ground-plane image return path.",
        "Straight-segment approximation (arcs represented by their chords).",
        MutualModelAssumption,
        BendAssumption
    };

    /// <summary>Planar chains: lifted to z = 0 and composed by the 3D kernel — one
    /// physics path for 2D and 3D callers.</summary>
    public InductanceReport Compose(IReadOnlyList<TraceSegment> chain) =>
        Compose(chain.Select(Lift).ToList());

    private static TraceSegment3D Lift(TraceSegment s) => new(
        new Vector3D(s.Start.X, s.Start.Y, 0),
        new Vector3D(s.End.X, s.End.Y, 0),
        s.Width, s.Thickness);

    public InductanceReport Compose(IReadOnlyList<TraceSegment3D> chain)
    {
        if (chain.Count == 0)
            throw new InvalidOperationException("The trace chain is empty.");

        var self = chain.Select(SelfInductance).ToList();

        double loop = self.Sum();
        // Each unordered pair contributes ±2M; visiting i < j once keeps the sum
        // deterministic (M(i,j) and M(j,i) agree only to rounding).
        for (int i = 0; i < chain.Count; i++)
            for (int j = i + 1; j < chain.Count; j++)
                loop += 2 * MutualTerm(chain[i], chain[j]);

        // The energy of any current distribution is positive; a composed value that is
        // not is a modelling failure, and is refused rather than shown.
        if (!double.IsFinite(loop) || loop <= 0)
            throw new InvalidOperationException(
                $"The composed inductance came out {loop * 1e9:g4} nH, which is not a physical " +
                "value (it must be finite and positive) — the chain geometry is outside what " +
                "the partial-inductance composition can represent.");

        return new InductanceReport(self, loop, StandardAssumptions);
    }

    /// <summary>The parallel branch's tolerance on 1 − |cosε|, matching
    /// <see cref="FilamentMutual"/>: a sub-threshold tilt is FP noise and is flattened.</summary>
    private const double ParallelTolerance = 1e-9;

    /// <summary>Signed mutual inductance between two chain segments: the finite-section
    /// bar kernel for a parallel pair of rectangular bars, the filament kernel for
    /// everything else (round sections need no section correction — a circle's GMD is
    /// its centre distance).</summary>
    internal static double MutualTerm(TraceSegment3D i, TraceSegment3D j)
    {
        double cos = Vector3D.Dot(i.Direction, j.Direction);
        if (1 - Math.Abs(cos) < ParallelTolerance
            && i.Profile == SegmentProfile.Bar && j.Profile == SegmentProfile.Bar)
            return Math.Sign(cos) * ParallelBarMutual(i, j);
        return FilamentMutual.Between(i.Start, i.End, j.Start, j.End);
    }

    /// <summary>
    /// Unsigned mutual of two parallel bars. A bar's section is laid out as copper is:
    /// thickness along the board normal (z), width across the run in the board plane —
    /// so the frame is (ŵ = ẑ × û, τ̂ = û × ŵ, û) with û the run direction of
    /// <paramref name="i"/>, and the pair's centre offset in that frame becomes
    /// Hoer–Love's corner offset.
    /// </summary>
    private static double ParallelBarMutual(TraceSegment3D i, TraceSegment3D j)
    {
        var along = i.Direction;
        var across = Vector3D.Cross(Vector3D.UnitZ, along);
        double horizontal = across.Length;
        if (horizontal < 1e-9)
            throw new InvalidOperationException(
                "A rectangular bar running along the board normal has no defined section " +
                "orientation (width is in-plane, thickness along z) — model a vertical " +
                "conductor as a round wire or a via barrel.");
        across /= horizontal;
        var normal = Vector3D.Cross(along, across);

        double l1 = i.Length, l2 = j.Length;
        var centreOffset = 0.5 * (j.Start + j.End) - 0.5 * (i.Start + i.End);
        double offsetWidth = Vector3D.Dot(centreOffset, across) + 0.5 * (i.Width - j.Width);
        double offsetThickness = Vector3D.Dot(centreOffset, normal) + 0.5 * (i.Thickness - j.Thickness);
        double offsetLength = Vector3D.Dot(centreOffset, along) + 0.5 * (l1 - l2);

        // The same copper counted twice — collinear bars overlapping along their run —
        // is a degenerate chain, as it is for filaments: fail loudly rather than return
        // the (finite) inductance of two bars occupying one volume.
        double scale = Math.Max(l1, l2);
        double axisDistance = (centreOffset - Vector3D.Dot(centreOffset, along) * along).Length;
        double overlap = Math.Min(l1, offsetLength + l2) - Math.Max(0, offsetLength);
        if (axisDistance <= 1e-9 * scale && overlap > 1e-9 * scale)
            throw new InvalidOperationException(
                "Collinear overlapping bars have no meaningful mutual inductance " +
                "(the same copper counted twice) — the chain geometry is degenerate.");

        return PartialInductance.BarBarMutual(i.Width, i.Thickness, l1, j.Width, j.Thickness, l2,
            offsetWidth, offsetThickness, offsetLength);
    }

    private static double SelfInductance(TraceSegment3D s) => s.Profile switch
    {
        SegmentProfile.Bar => PartialInductance.SelfInductance(s.Length, s.Width, s.Thickness),
        SegmentProfile.RoundWire => PartialInductance.RoundWireSelfInductance(s.Length, s.Width / 2),
        SegmentProfile.RoundTube => PartialInductance.RoundTubeSelfInductance(s.Length, s.Width / 2),
        _ => throw new InvalidOperationException($"Unknown segment profile '{s.Profile}'.")
    };
}
