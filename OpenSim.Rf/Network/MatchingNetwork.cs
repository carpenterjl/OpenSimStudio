using System.Numerics;

namespace OpenSim.Rf.Network;

/// <summary>Where the shunt part of an L-section sits.</summary>
public enum LSectionTopology
{
    /// <summary>Shunt part across the load, series part toward the source.</summary>
    ShuntAtLoad,
    /// <summary>Series part next to the load, shunt part across the source side.</summary>
    ShuntAtSource
}

/// <summary>One L-section: the two parts and where the shunt one sits.</summary>
public sealed record LSection(LSectionTopology Topology, LumpedPart Series, LumpedPart Shunt,
    double DesignFrequencyHz, double SeriesReactance, double ShuntSusceptance)
{
    /// <summary>The impedance the source sees when the section drives <paramref name="load"/>
    /// at <paramref name="frequencyHz"/> (the parts' Q included).</summary>
    public Complex InputImpedance(Complex load, double frequencyHz)
    {
        Complex zs = Series.Impedance(frequencyHz);
        Complex ysh = 1 / Shunt.Impedance(frequencyHz);
        return Topology == LSectionTopology.ShuntAtLoad
            ? zs + 1 / (ysh + 1 / load)
            : 1 / (ysh + 1 / (zs + load));
    }

    public string Describe() =>
        Topology == LSectionTopology.ShuntAtLoad
            ? $"shunt {Shunt.Describe()} across the load, then series {Series.Describe()}"
            : $"series {Series.Describe()} at the load, then shunt {Shunt.Describe()} across the source side";
}

/// <summary>
/// Two-part lumped matching (the L-section): every real solution that transforms a load to the
/// reference resistance at one frequency, from the closed-form design equations, each checked by
/// computing the input impedance back. Then the matched response over a band, with the parts'
/// Q if given — the result a designer compares against the bare antenna.
/// </summary>
public static class MatchingNetwork
{
    /// <summary>All L-section solutions for <paramref name="load"/> to <paramref name="referenceOhms"/>
    /// at <paramref name="frequencyHz"/>; empty when the load is already matched to within
    /// 1e-9 or no real solution exists. <paramref name="q"/> is applied to the inductors and
    /// capacitors chosen (null = ideal parts).</summary>
    public static IReadOnlyList<LSection> DesignLSection(Complex load, double referenceOhms,
        double frequencyHz, double? inductorQ = null, double? capacitorQ = null)
    {
        if (!(referenceOhms > 0)) throw new ArgumentOutOfRangeException(nameof(referenceOhms));
        if (!(frequencyHz > 0)) throw new ArgumentOutOfRangeException(nameof(frequencyHz));
        if (!(load.Real > 0))
            throw new ArgumentException("An L-section cannot match a load with no resistance (Re Z ≤ 0).", nameof(load));
        double r = load.Real, x = load.Imaginary, z0 = referenceOhms;
        double w = 2 * Math.PI * frequencyHz;
        var solutions = new List<LSection>();

        // Shunt across the load: B = (X ± √(R/Z0)·√(R² + X² − Z0·R)) / (R² + X²),
        // X_s = 1/B + X·Z0/R − Z0/(B·R).
        double disc = r * r + x * x - z0 * r;
        if (disc >= 0)
            foreach (int sign in new[] { 1, -1 })
            {
                double b = (x + sign * Math.Sqrt(r / z0) * Math.Sqrt(disc)) / (r * r + x * x);
                if (b == 0) continue;
                double xs = 1 / b + x * z0 / r - z0 / (b * r);
                Add(LSectionTopology.ShuntAtLoad, xs, b);
            }

        // Series at the load: X_s = ±√(R·(Z0 − R)) − X, B = ±√((Z0 − R)/R)/Z0.
        if (r <= z0)
            foreach (int sign in new[] { 1, -1 })
            {
                double xs = sign * Math.Sqrt(r * (z0 - r)) - x;
                double b = sign * Math.Sqrt((z0 - r) / r) / z0;
                if (b == 0) continue;
                Add(LSectionTopology.ShuntAtSource, xs, b);
            }
        return solutions;

        void Add(LSectionTopology topology, double seriesX, double shuntB)
        {
            if (seriesX == 0) return;
            var series = seriesX > 0
                ? LumpedPart.Inductor(seriesX / w, inductorQ)
                : LumpedPart.Capacitor(-1 / (w * seriesX), capacitorQ);
            var shunt = shuntB > 0
                ? LumpedPart.Capacitor(shuntB / w, capacitorQ)
                : LumpedPart.Inductor(-1 / (w * shuntB), inductorQ);
            var section = new LSection(topology, series, shunt, frequencyHz, seriesX, shuntB);
            // With ideal parts the design must reproduce Z0 exactly; anything else is a wrong branch.
            var ideal = section with
            {
                Series = section.Series with { Q = null },
                Shunt = section.Shunt with { Q = null }
            };
            if ((ideal.InputImpedance(load, frequencyHz) - z0).Magnitude <= 1e-6 * z0)
                solutions.Add(section);
        }
    }

    /// <summary>The matched input impedance against frequency for a load known at those
    /// frequencies (an antenna's Zin sweep).</summary>
    public static IReadOnlyList<(double FrequencyHz, Complex InputImpedance)> Apply(LSection section,
        IReadOnlyList<(double FrequencyHz, Complex Load)> loads) =>
        loads.Select(p => (p.FrequencyHz, section.InputImpedance(p.Load, p.FrequencyHz))).ToList();

    /// <summary>The span of frequencies (between sweep points, linear) where the return loss is
    /// at least <paramref name="returnLossDb"/>; null when no point reaches it.</summary>
    public static (double Low, double High)? Band(IReadOnlyList<(double FrequencyHz, Complex InputImpedance)> sweep,
        double referenceOhms, double returnLossDb = 10)
    {
        double? low = null, high = null;
        for (int i = 0; i < sweep.Count; i++)
        {
            double rl = PortTermination.ReturnLossDb(sweep[i].InputImpedance, referenceOhms);
            if (rl < returnLossDb) continue;
            if (low is null)
            {
                low = sweep[i].FrequencyHz;
                if (i > 0) low = Crossing(i - 1, i);
            }
            high = sweep[i].FrequencyHz;
            if (i + 1 < sweep.Count &&
                PortTermination.ReturnLossDb(sweep[i + 1].InputImpedance, referenceOhms) < returnLossDb)
            {
                high = Crossing(i, i + 1);
                break;
            }
        }
        return low is null ? null : (low.Value, high!.Value);

        double Crossing(int a, int b)
        {
            double ra = PortTermination.ReturnLossDb(sweep[a].InputImpedance, referenceOhms);
            double rb = PortTermination.ReturnLossDb(sweep[b].InputImpedance, referenceOhms);
            double t = (returnLossDb - ra) / (rb - ra);
            return sweep[a].FrequencyHz + t * (sweep[b].FrequencyHz - sweep[a].FrequencyHz);
        }
    }
}
