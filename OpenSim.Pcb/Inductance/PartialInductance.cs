namespace OpenSim.Pcb.Inductance;

/// <summary>
/// Partial inductances of straight conductors (PEEC building blocks). Rectangular bars
/// use one finite-section kernel for every parallel pair, the bar with itself included
/// (<see cref="BarBarMutual"/>: Hoer &amp; Love's uniform-current solution); round wires
/// and tubes use the exact filament integral at their self-GMD. All lengths in meters,
/// results in henries.
///
/// Assumptions (stated so results are not mistaken for a full-wave solve): DC / uniform
/// current distribution, no skin or proximity effect, non-magnetic media (µ = µ₀), and
/// straight segments (arcs are approximated by their chords upstream).
/// </summary>
public static class PartialInductance
{
    private const double Mu0Over2Pi = 2e-7;                 // µ₀/2π [H/m]

    /// <summary>
    /// Partial self-inductance of a rectangular bar of length <paramref name="length"/>,
    /// width <paramref name="width"/> and thickness <paramref name="thickness"/>: the
    /// bar's mutual inductance with itself, exact for uniform current at any aspect
    /// ratio. (Ruehli's ln(2l/(w+t)) + ½ + (w+t)/(3l), shipped before, is the slender
    /// approximation of this — 0.08 % high for a 10 × 1 × 0.035 mm trace — and, paired
    /// with filament-level collinear mutuals, made a bar's inductance depend on how it
    /// was subdivided.)
    /// </summary>
    public static double SelfInductance(double length, double width, double thickness)
    {
        if (length <= 0 || width <= 0 || thickness <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Bar dimensions must be positive.");
        return BarBarMutual(width, thickness, length, width, thickness, length, 0, 0, 0);
    }

    /// <summary>
    /// Partial mutual inductance of two PARALLEL rectangular bars with co-directed
    /// uniform currents, in Hoer &amp; Love's geometry: bar 1 occupies
    /// [0, <paramref name="width1"/>] × [0, <paramref name="thickness1"/>] ×
    /// [0, <paramref name="length1"/>] and bar 2 occupies
    /// [<paramref name="offsetWidth"/>, … + <paramref name="width2"/>] ×
    /// [<paramref name="offsetThickness"/>, … + <paramref name="thickness2"/>] ×
    /// [<paramref name="offsetLength"/>, … + <paramref name="length2"/>] — the offsets
    /// are CORNER to corner, and the third axis is the current direction. Covers self
    /// (identical boxes, zero offsets), side-by-side, stacked, collinear, staggered and
    /// unequal pairs with one kernel; see <see cref="RectangularBarKernel"/> for how it
    /// is evaluated without losing digits.
    /// </summary>
    public static double BarBarMutual(
        double width1, double thickness1, double length1,
        double width2, double thickness2, double length2,
        double offsetWidth, double offsetThickness, double offsetLength) =>
        RectangularBarKernel.Mutual(width1, thickness1, length1, width2, thickness2, length2,
            offsetWidth, offsetThickness, offsetLength);

    /// <summary>
    /// Partial self-inductance of a straight round wire with uniform (DC) current:
    /// the exact self-GMD evaluation L = (µ₀/2π)·l·[asinh(l/g) − √(1+(g/l)²) + g/l] with
    /// g = r·e^(−¼). Asymptotically (µ₀/2π)·l·[ln(2l/r) − ¾] for l ≫ r, but — unlike the
    /// log form — stays positive for ANY aspect ratio, so stubby segments cannot poison
    /// a chain sum with a negative self-term.
    /// </summary>
    public static double RoundWireSelfInductance(double length, double radius)
    {
        if (length <= 0 || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Wire dimensions must be positive.");
        return SelfFromGmd(length, radius * Math.Exp(-0.25));
    }

    /// <summary>
    /// Partial self-inductance of a straight thin-walled round tube — a plated via
    /// barrel: the bore is empty and the current flows in the shell, whose self-GMD is
    /// exactly the radius. L = (µ₀/2π)·l·[asinh(l/r) − √(1+(r/l)²) + r/l], asymptotically
    /// (µ₀/2π)·l·[ln(2l/r) − 1]; positive for any aspect ratio (adjacent-layer vias are
    /// genuinely stubbier than the log asymptote tolerates).
    /// </summary>
    public static double RoundTubeSelfInductance(double length, double radius)
    {
        if (length <= 0 || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Tube dimensions must be positive.");
        return SelfFromGmd(length, radius);
    }

    /// <summary>Self-inductance as the filament pair integral at the self-GMD ρ — the
    /// full-overlap parallel-filament form, exact for the given GMD.</summary>
    private static double SelfFromGmd(double length, double gmd)
    {
        double l = length, g = gmd;
        return Mu0Over2Pi * l * (Math.Asinh(l / g) - Math.Sqrt(1 + (g / l) * (g / l)) + g / l);
    }
}
