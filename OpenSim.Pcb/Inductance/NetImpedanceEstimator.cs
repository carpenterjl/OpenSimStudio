using OpenSim.Pcb.Import;

namespace OpenSim.Pcb.Inductance;

/// <summary>One frequency point of a lumped impedance estimate.</summary>
public sealed record ImpedancePoint(double Frequency, double Magnitude, double PhaseDegrees);

/// <summary>A lumped R + jωL impedance sweep with the assumptions that produced it.</summary>
/// <param name="SkinCrossoverHz">The frequency at which the skin depth in copper falls to
/// half the thinnest conductor dimension of the chain. Below it the DC resistance stands;
/// above it the real resistance rises (as √f once well past it) and the points of the
/// sweep carry too little R.</param>
public sealed record NetImpedanceReport(
    double ResistanceOhms,
    double InductanceHenries,
    IReadOnlyList<ImpedancePoint> Points,
    IReadOnlyList<string> Assumptions,
    double SkinCrossoverHz = double.PositiveInfinity);

/// <summary>
/// Lumped trace impedance Z(f) = R + jωL: R comes from the DC FIELD solve (it carries
/// the real meshed geometry — necks, pads, corners — that no width×length formula
/// sees), L from the PEEC partial-inductance composition of the ordered centerline
/// chain. An open pad-to-pad chain has no return path, so what is composed is PARTIAL
/// inductance — stated in the assumptions rather than overclaimed as loop inductance.
/// </summary>
public static class NetImpedanceEstimator
{
    /// <summary>Annealed copper [S/m] — what the skin-depth crossover is stated for.</summary>
    public const double CopperConductivity = 5.8e7;

    /// <summary>
    /// The frequency at which the skin depth δ = 1/√(π·f·μ₀·σ) equals half of
    /// <paramref name="dimension"/>: f = 4/(π·μ₀·σ·d²). 14 MHz for 35 µm copper.
    /// </summary>
    public static double SkinCrossover(double dimension, double conductivity = CopperConductivity) =>
        4 / (Math.PI * 4e-7 * Math.PI * conductivity * dimension * dimension);

    public static NetImpedanceReport Estimate(double dcResistanceOhms,
        IReadOnlyList<TraceCenterline> chain, double copperThickness,
        double fMin, double fMax, int points)
    {
        if (copperThickness <= 0)
            throw new InvalidOperationException("The copper thickness must be positive.");
        if (chain.Count == 0)
            throw new InvalidOperationException("The trace chain is empty.");

        var segments = chain
            .Select(c => new TraceSegment3D(
                new Core.Numerics.Vector3D(c.Start.X, c.Start.Y, 0),
                new Core.Numerics.Vector3D(c.End.X, c.End.Y, 0),
                c.Width, copperThickness))
            .ToList();
        return Estimate(dcResistanceOhms, segments, fMin, fMax, points);
    }

    /// <summary>The 3D form: multi-layer chains with via-barrel tube segments, as built
    /// by <see cref="TraceChainBuilder"/>'s stackup-aware overload.</summary>
    public static NetImpedanceReport Estimate(double dcResistanceOhms,
        IReadOnlyList<TraceSegment3D> chain, double fMin, double fMax, int points)
    {
        if (chain.Count == 0)
            throw new InvalidOperationException("The trace chain is empty.");
        if (dcResistanceOhms <= 0)
            throw new InvalidOperationException("The DC resistance must be positive (run the electrical test first).");
        if (fMin <= 0 || fMax < fMin || points < 1)
            throw new InvalidOperationException("The frequency sweep range is invalid.");

        var inductance = new LoopComposer().Compose(chain);

        var sweep = new List<ImpedancePoint>(points);
        for (int k = 0; k < points; k++)
        {
            double f = points == 1
                ? fMin
                : fMin * Math.Pow(fMax / fMin, (double)k / (points - 1));
            var z = new System.Numerics.Complex(
                dcResistanceOhms, 2 * Math.PI * f * inductance.LoopInductance);
            sweep.Add(new ImpedancePoint(f, z.Magnitude, z.Phase * 180 / Math.PI));
        }

        var assumptions = inductance.Assumptions.AsEnumerable();
        int barrels = chain.Count(s => s.Profile == SegmentProfile.RoundTube);
        if (barrels > 0)
            assumptions = assumptions.Append(
                $"Includes {barrels} plated via barrel(s) modeled as thin tubes " +
                "(mean shell radius; barrel spans copper mid-plane to mid-plane).");
        // Where the constant-R model stops: the skin depth against the thinnest dimension
        // any bar of the chain has (its copper thickness, on a board).
        double thinnest = chain
            .Where(s => s.Profile == SegmentProfile.Bar)
            .Select(s => Math.Min(s.Width, s.Thickness))
            .DefaultIfEmpty(chain.Min(s => Math.Min(s.Width, s.Thickness)))
            .Min();
        double crossover = SkinCrossover(thinnest);
        int past = sweep.Count(p => p.Frequency > crossover);
        string skin = $"R is the DC field-solve value, constant over the sweep (no skin-effect rise). " +
                      $"That holds up to about {crossover / 1e6:g3} MHz, where the skin depth in copper " +
                      $"is half the {thinnest * 1e6:g3} µm conductor";
        skin += past == 0
            ? "; the whole sweep lies below it."
            : $"; {past} of {sweep.Count} points lie ABOVE it (up to " +
              $"{sweep[^1].Frequency / crossover:g3}× past), where the real R is higher — by about " +
              $"√(f/{crossover / 1e6:g3} MHz) well above the crossover — and |Z| there is a lower bound " +
              "wherever R still matters against ωL.";
        assumptions = assumptions
            .Append("Open trace chain: PARTIAL inductance only — no return-path loop closure.")
            .Append(skin);
        return new NetImpedanceReport(dcResistanceOhms, inductance.LoopInductance, sweep.ToList(),
            assumptions.ToList(), crossover);
    }
}
