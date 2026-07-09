using OpenSim.Pcb.Import;

namespace OpenSim.Pcb.Inductance;

/// <summary>One frequency point of a lumped impedance estimate.</summary>
public sealed record ImpedancePoint(double Frequency, double Magnitude, double PhaseDegrees);

/// <summary>A lumped R + jωL impedance sweep with the assumptions that produced it.</summary>
public sealed record NetImpedanceReport(
    double ResistanceOhms,
    double InductanceHenries,
    IReadOnlyList<ImpedancePoint> Points,
    IReadOnlyList<string> Assumptions);

/// <summary>
/// Lumped trace impedance Z(f) = R + jωL: R comes from the DC FIELD solve (it carries
/// the real meshed geometry — necks, pads, corners — that no width×length formula
/// sees), L from the PEEC partial-inductance composition of the ordered centerline
/// chain. An open pad-to-pad chain has no return path, so what is composed is PARTIAL
/// inductance — stated in the assumptions rather than overclaimed as loop inductance.
/// </summary>
public static class NetImpedanceEstimator
{
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
        assumptions = assumptions
            .Append("Open trace chain: PARTIAL inductance only — no return-path loop closure.")
            .Append("R is the DC field-solve value, constant over the sweep (no skin-effect rise).");
        return new NetImpedanceReport(dcResistanceOhms, inductance.LoopInductance, sweep.ToList(),
            assumptions.ToList());
    }
}
