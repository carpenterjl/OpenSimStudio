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
/// L = Σ Lᵢᵢ + 2·Σ_{i&lt;j} Mᵢⱼ, with every mutual term the exact straight-filament
/// Neumann solution (<see cref="FilamentMutual"/>) signed by the segments' own start→end
/// directions — collinear, staggered, oblique, and vertical pairs are all exact at the
/// filament level. For an OPEN chain the result is the chain's PARTIAL inductance (no
/// return conductor); a physical loop value needs the return composed in explicitly.
/// </summary>
public sealed class LoopComposer
{
    private static readonly string[] StandardAssumptions =
    {
        "DC / uniform current distribution (no skin or proximity effect).",
        "Non-magnetic media (µ = µ₀); no ground-plane image return path.",
        "Straight-segment approximation (arcs represented by their chords).",
        "Mutual terms are the exact straight-filament Neumann solution (Grover), with a " +
        "geometric-mean-distance correction for parallel finite cross-sections."
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

        return new InductanceReport(self, loop, StandardAssumptions);
    }

    /// <summary>Signed mutual inductance between two chain segments — the filament kernel
    /// with a GMD substitution when a parallel side-by-side pair has finite rectangular
    /// sections (round sections need none: a circle's GMD is its centre distance).</summary>
    internal static double MutualTerm(TraceSegment3D i, TraceSegment3D j)
    {
        double gmd = 0;
        double cos = Vector3D.Dot(i.Direction, j.Direction);
        if (1 - Math.Abs(cos) < 1e-9 && i.Profile == SegmentProfile.Bar && j.Profile == SegmentProfile.Bar)
        {
            double d = FilamentMutual.PerpendicularSeparation(i.Start, i.End, j.Start);
            if (d > 1e-9 * Math.Max(i.Length, j.Length))       // side-by-side, not collinear
                gmd = PartialInductance.GeometricMeanDistance(d,
                    0.5 * (i.Width + j.Width), 0.5 * (i.Thickness + j.Thickness));
        }
        return FilamentMutual.Between(i.Start, i.End, j.Start, j.End, gmd);
    }

    private static double SelfInductance(TraceSegment3D s) => s.Profile switch
    {
        SegmentProfile.Bar => PartialInductance.SelfInductance(s.Length, s.Width, s.Thickness),
        SegmentProfile.RoundWire => PartialInductance.RoundWireSelfInductance(s.Length, s.Width / 2),
        SegmentProfile.RoundTube => PartialInductance.RoundTubeSelfInductance(s.Length, s.Width / 2),
        _ => throw new InvalidOperationException($"Unknown segment profile '{s.Profile}'.")
    };
}
