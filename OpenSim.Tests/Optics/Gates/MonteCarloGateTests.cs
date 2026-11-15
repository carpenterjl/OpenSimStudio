using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Tolerancing;
using X = OpenSim.Tests.Optics.Gates.ToleranceRunFixtures;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-65's gate (plan): "With one linear tolerance the output distribution is the input
/// distribution scaled; trial k is identical whether run alone, in sequence or in parallel."
/// <para>
/// The linear tolerance: TFRN on surface 1 of the singlet, read by a merit function of one CVVA
/// row (the curvature of surface 1, target 0, weight 1), compensation none. TFRN adds
/// ΔC = 2ΔZ/r² with ΔZ = λN/2 (help, "TFRN: Tolerance on Fringes"; OP-63's rule), so with the
/// test wavelength λ (TWAV) and a fixed semi-diameter r the curvature is c₀ + (λ/r²)·N, exactly
/// linear in the drawn fringes N. The merit value of one row is √(w(c − 0)²/w) = |c| = c (c &gt; 0
/// here), so the criterion is c₀ + sN with s = λ/r² (derived). λ = 0.6328 µm, r = 3 mm and the
/// ±2 fringes are arbitrary test inputs.
/// </para>
/// </summary>
public sealed class MonteCarloGateTests
{
    internal const double Lambda = 0.6328;           // µm, the TWAV row (a test input)
    internal const double SemiDiameter = 3;          // mm, fixed on surface 1 (a test input)
    internal const double Fringes = 2;               // ± fringes (a test input)

    /// <summary>s = λ/r², 1/mm per fringe (λ converted from µm to mm).</summary>
    internal static double Slope => Lambda * 1e-3 / (SemiDiameter * SemiDiameter);

    internal static double C0 => 1 / X.R1;

    internal static OpticsSettings Lens()
    {
        var lens = X.Singlet();
        var surfaces = lens.Surfaces!.ToList();
        surfaces[1] = surfaces[1] with { SemiDiameter = SemiDiameter };
        var merit = new MeritFunction(new[] { MeritOperandRow.Of("CVVA", 1, 0, 0, 1) });
        return lens with { Surfaces = surfaces, MeritFunction = MeritFunctionSettings.ToSettings(merit) };
    }

    internal static ToleranceTable Table(int statistics, int standardDeviations) => X.Table(
        new ToleranceOperandRow { Code = "TWAV", Min = Lambda },
        new ToleranceOperandRow { Code = "STAT", Int1 = statistics, Int2 = standardDeviations },
        X.Row("TFRN", 1, 0, -Fringes, Fringes));

    internal static readonly ToleranceRunOptions Linear = new()
    {
        Criterion = ToleranceCriterion.MeritFunction, Compensation = ToleranceCompensation.None,
    };

    /// <summary>Every trial's criterion is c₀ + s·N of its own draw, to rounding (the curvature passes
    /// through a radius and back, about 1e-16 relative).</summary>
    [Fact]
    public void EachTrial_IsTheLinearImageOfItsDraw()
    {
        var r = MonteCarloAnalysis.Run(Lens(), Table(1, 0), X.NoCatalogs, Linear, new MonteCarloOptions { Trials = 50, Seed = 3 });
        Assert.Equal(0, r.Failed);
        Assert.Equal(C0, r.Nominal, 15);
        foreach (var t in r.Trials)
        {
            var d = Assert.Single(t.Draws);
            Assert.Equal(3, d.Row);
            Assert.InRange(d.Value, -Fringes, Fringes);
            Assert.True(Math.Abs(t.Criterion - (C0 + Slope * d.Value)) <= 1e-12 * C0, $"trial {t.Index}: {t.Criterion:R} against {C0 + Slope * d.Value:R}");
            Assert.Equal(t.Criterion - r.Nominal, t.Change);
        }
    }

    /// <summary>
    /// The output distribution is the input distribution scaled by s about c₀: sample mean, standard
    /// deviation and percentiles of 2000 trials against the scaled closed forms of the uniform,
    /// parabolic and truncated normal distributions.
    /// <para>
    /// Closed forms (derived): uniform on ±Δ: σ² = Δ²/3, quantile Δ(2p − 1), density 1/(2Δ), kurtosis
    /// 9/5. Parabolic p(x) = 3x²/(2Δ³): σ² = 3Δ²/5, E x⁴ = 3Δ⁴/7, quantile Δ∛(2p − 1), density
    /// 3q²/(2Δ³). Normal with σ₀ = Δ/2 truncated at ±Δ: the moments by 64-point Gauss–Legendre
    /// quadrature of x²e^(−x²/2σ₀²) and x⁴e^(−x²/2σ₀²) over [−Δ, Δ], exact to rounding for such a
    /// smooth integrand.
    /// </para>
    /// <para>
    /// Tolerances: five standard errors of each estimator at N = 2000 (asymptotic, derived): the mean
    /// σ/√N; the standard deviation σ√((κ − 1)/(4N)) (delta method on s²); a p-quantile
    /// √(p(1 − p)/N)/f(q_p). The run is seeded, so the test is fixed; the bands say why its numbers
    /// may differ from the population values. Percentiles where the parabolic density is zero
    /// (p = 0.5) are not compared, their standard error being unbounded.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void TheOutputDistribution_IsTheInputDistributionScaled()
    {
        const int n = 2000;
        double delta = Fringes, s = Slope;
        var cases = new (int Stat, int Sd, double Sigma, double Kurtosis, Func<double, double>? Quantile, Func<double, double>? Density)[]
        {
            (1, 0, delta / Math.Sqrt(3), 9.0 / 5, p => delta * (2 * p - 1), _ => 1 / (2 * delta)),
            (2, 0, delta * Math.Sqrt(3.0 / 5), (3.0 / 7) / (9.0 / 25), p => delta * Math.Cbrt(2 * p - 1), q => 3 * q * q / (2 * delta * delta * delta)),
            TruncatedNormal(delta, 2),
        };
        foreach (var c in cases)
        {
            var r = MonteCarloAnalysis.Run(Lens(), Table(c.Stat, c.Sd), X.NoCatalogs, Linear,
                new MonteCarloOptions { Trials = n, Seed = 11, Percentiles = new double[] { 10, 25, 75, 90 } });
            Assert.Equal(0, r.Failed);
            var st = r.Statistics!;
            Assert.Equal(n, st.Count);
            string who = $"STAT {c.Stat}";
            double se = s * c.Sigma / Math.Sqrt(n);
            Assert.True(Math.Abs(st.Mean - C0) <= 5 * se, $"{who}: mean {st.Mean:R} against {C0:R} ± {5 * se:R}");
            double seSd = s * c.Sigma * Math.Sqrt((c.Kurtosis - 1) / (4.0 * n));
            Assert.True(Math.Abs(st.StandardDeviation - s * c.Sigma) <= 5 * seSd, $"{who}: σ {st.StandardDeviation:R} against {s * c.Sigma:R} ± {5 * seSd:R}");
            Assert.InRange(st.Min, C0 - s * delta * (1 + 1e-12), C0 + s * delta * (1 + 1e-12));
            Assert.InRange(st.Max, C0 - s * delta * (1 + 1e-12), C0 + s * delta * (1 + 1e-12));
            if (c.Quantile is null) continue;
            foreach (var p in st.Percentiles)
            {
                double pp = p.Percent / 100, q = c.Quantile(pp);
                double seQ = s * Math.Sqrt(pp * (1 - pp) / n) / c.Density!(q);
                Assert.True(Math.Abs(p.Value - (C0 + s * q)) <= 5 * seQ, $"{who}: P{p.Percent} {p.Value:R} against {C0 + s * q:R} ± {5 * seQ:R}");
            }
        }
    }

    /// <summary>The truncated normal's σ and kurtosis by quadrature (see the test's remarks).</summary>
    private static (int, int, double, double, Func<double, double>?, Func<double, double>?) TruncatedNormal(double delta, int sd)
    {
        double sigma0 = delta / sd;
        var (x, w) = GaussLegendre.Rule(64, -delta, delta);
        double m0 = 0, m2 = 0, m4 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double g = Math.Exp(-x[i] * x[i] / (2 * sigma0 * sigma0)) * w[i];
            m0 += g; m2 += x[i] * x[i] * g; m4 += x[i] * x[i] * x[i] * x[i] * g;
        }
        double variance = m2 / m0;
        return (0, sd, Math.Sqrt(variance), m4 / m0 / (variance * variance), null, null);
    }

    /// <summary>Trial k alone (<see cref="MonteCarloAnalysis.Trial"/>), in a sequential run (one thread) and in a
    /// parallel run (no thread limit) has the same draws and the same criterion and compensator bits, on an optical
    /// criterion (RMS wavefront, paraxial focus) with rows under two seeds and two distributions.</summary>
    [Fact]
    public void TrialK_IsTheSame_Alone_InSequence_AndInParallel()
    {
        var lens = X.Singlet();
        var table = X.Table(
            new ToleranceOperandRow { Code = "SEED", Int1 = 7 },
            X.Row("TRAD", 1, 0, -1, 1),
            X.Row("TTHI", 1, 1, -0.1, 0.1),
            new ToleranceOperandRow { Code = "SEED", Int1 = 0 },
            new ToleranceOperandRow { Code = "STAT", Int1 = 2 },
            X.Row("TCON", 2, 0, -0.5, 0.5),
            X.Row("TRAD", 2, 0, -1, 1));
        var mc = new MonteCarloOptions { Trials = 6, Seed = 42 };
        var sequential = MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, X.Wavefront with { MaxDegreeOfParallelism = 1 }, mc);
        var parallel = MonteCarloAnalysis.Run(lens, table, X.NoCatalogs, X.Wavefront with { MaxDegreeOfParallelism = null }, mc);
        Assert.Equal(0, sequential.Failed);
        for (int k = 1; k <= mc.Trials; k++)
        {
            var alone = MonteCarloAnalysis.Trial(lens, table, X.NoCatalogs, X.Wavefront, mc, k);
            foreach (var other in new[] { sequential.Trials[k - 1], parallel.Trials[k - 1] })
            {
                Assert.Equal(k, other.Index);
                Assert.Equal(alone.Draws, other.Draws);
                Assert.Equal(BitConverter.DoubleToInt64Bits(alone.Criterion), BitConverter.DoubleToInt64Bits(other.Criterion));
                Assert.Equal(alone.Compensators.Select(BitConverter.DoubleToInt64Bits), other.Compensators.Select(BitConverter.DoubleToInt64Bits));
            }
        }
        Assert.Equal(sequential.ReportLines(), parallel.ReportLines());
        // The trials differ from one another: the streams are per trial.
        Assert.NotEqual(sequential.Trials[0].Draws[0].Value, sequential.Trials[1].Draws[0].Value);
    }
}
