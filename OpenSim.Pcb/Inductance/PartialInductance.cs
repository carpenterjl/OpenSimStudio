namespace OpenSim.Pcb.Inductance;

/// <summary>
/// Closed-form partial inductances of straight rectangular conductors (PEEC building
/// blocks). Self-inductance follows Ruehli's formula for a rectangular bar; mutual
/// inductance uses Grover's parallel-filament result with a geometric-mean-distance
/// correction for finite cross-sections. All lengths in meters, results in henries.
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
    /// width <paramref name="width"/> and thickness <paramref name="thickness"/>
    /// (Ruehli 1972). Accurate to ~1–2% for practical trace aspect ratios.
    /// </summary>
    public static double SelfInductance(double length, double width, double thickness)
    {
        if (length <= 0 || width <= 0 || thickness <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "Bar dimensions must be positive.");

        double l = length, w = width, t = thickness;
        double u = w + t;
        // Ruehli's closed form: L = (µ₀/2π)·l·[ ln(2l/(w+t)) + 0.5 + (w+t)/(3l) ].
        return Mu0Over2Pi * l * (Math.Log(2 * l / u) + 0.5 + u / (3 * l));
    }

    /// <summary>
    /// Partial mutual inductance of two parallel rectangular bars of equal length, offset
    /// by centre-to-centre distance <paramref name="separation"/>. Uses Grover's
    /// filament formula with the separation replaced by the geometric mean distance so
    /// finite width is accounted for at first order.
    /// </summary>
    public static double MutualInductanceParallel(double length, double separation,
        double width, double thickness)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (separation <= 0) throw new ArgumentOutOfRangeException(nameof(separation));

        double gmd = GeometricMeanDistance(separation, width, thickness);
        double l = length, d = gmd;
        // Grover: M = (µ₀/2π)·l·[ ln( l/d + sqrt(1 + (l/d)²) ) − sqrt(1 + (d/l)²) + d/l ].
        double lod = l / d;
        double dol = d / l;
        return Mu0Over2Pi * l * (Math.Log(lod + Math.Sqrt(1 + lod * lod)) - Math.Sqrt(1 + dol * dol) + dol);
    }

    /// <summary>
    /// Geometric mean distance between two identical parallel rectangular cross-sections
    /// whose centres are <paramref name="separation"/> apart. For separations well above
    /// the conductor size this tends to the centre distance; the leading correction
    /// subtracts the self-GMD contribution of the cross-section.
    /// </summary>
    public static double GeometricMeanDistance(double separation, double width, double thickness)
    {
        // ln(GMD) ≈ ln(d) − (w² + t²)/(24 d²) is the standard small-section expansion.
        double d = separation;
        double correction = (width * width + thickness * thickness) / (24 * d * d);
        return d * Math.Exp(-correction);
    }

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
