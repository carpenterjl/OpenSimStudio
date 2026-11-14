using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// The OP-40 gate (docs/Optics-Gates.md): the Gaussian pupil quadrature is exact to its order
/// and its RMS equals a dense rectangular-grid RMS on an unvignetted lens; the minimum of RMS
/// against focus sits at the analytic best focus of defocus plus third-order spherical
/// aberration. Every expected value is derived in the comments; the lenses are arbitrary inputs.
/// </summary>
public sealed class RmsGateTests
{
    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false, bool mirror = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror,
    };

    private static SequentialLens Lens(params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = double.PositiveInfinity } }.Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double epd, params double[] angleFields) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet
        {
            Type = FieldType.Angle,
            Points = angleFields.Length == 0 ? new[] { new FieldPoint() } : angleFields.Select(a => new FieldPoint { Y = a }).ToArray(),
        },
    };

    private static OpticsAnalysisContext Context(OpticalSystemData system) =>
        new("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });

    private static double[] Curve(OpticsAnalysisResult result, int index = 0) =>
        ((LinePlot)result.Plots[0]).Curves[index].Y.ToArray();

    // ---- The quadrature's order ----

    /// <summary>(2p − 1)!!, 1 for p = 0.</summary>
    private static double OddFactorial(int p)
    {
        double f = 1;
        for (int k = 2 * p - 1; k > 1; k -= 2) f *= k;
        return f;
    }

    [Fact]
    public void TheGaussianRule_AveragesEveryMonomialUpToItsDegreeExactly()
    {
        // Derived: the mean of x^a y^b over the unit disk is (1/π)∫₀¹ρ^(a+b+1)dρ ∫₀^{2π}cos^aθ sin^bθ dθ.
        // The angular integral is 0 unless a and b are both even; for a = 2p, b = 2q it is
        // 2π(2p − 1)!!(2q − 1)!!/(2p + 2q)!!, so the mean is 2(2p − 1)!!(2q − 1)!!/((a + b + 2)(2p + 2q)!!)
        // (a = b = 0 gives 1; a = b = 2 gives 1/24). The rule is exact to degree min(4n − 1, N − 1)
        // (PupilQuadrature's remarks), and N = 4n by default.
        for (int n = 1; n <= 5; n++)
        {
            var rule = PupilQuadrature.Gaussian(n);
            Assert.Equal(4 * n - 1, PupilQuadrature.ExactDegree(n, rule.Arms));
            Assert.Equal(n * 4 * n, rule.Points.Count);
            Assert.Equal(1.0, rule.Weights.Sum(), 14);
            for (int a = 0; a <= 4 * n - 1; a++)
                for (int b = 0; a + b <= 4 * n - 1; b++)
                {
                    double expected = 0;
                    if (a % 2 == 0 && b % 2 == 0)
                    {
                        int p = a / 2, q = b / 2;
                        double evenFactorial = Math.Pow(2, p + q) * Enumerable.Range(1, p + q).Aggregate(1.0, (f, k) => f * k);   // (2p+2q)!! = 2^(p+q)(p+q)!
                        expected = 2 * OddFactorial(p) * OddFactorial(q) / ((a + b + 2) * evenFactorial);
                    }
                    int aa = a, bb = b;
                    double actual = rule.Mean((x, y) => Math.Pow(x, aa) * Math.Pow(y, bb));
                    Assert.True(Math.Abs(actual - expected) <= 1e-14, $"n = {n}, x^{a} y^{b}: expected {expected:R}, got {actual:R}.");
                }
        }
    }

    [Fact]
    public void TheRadialRule_IsExactForRhoToThe4nMinus2_AndNotFor4n()
    {
        // Derived: the disk mean of ρ^(2m) is 1/(m + 1). In u = ρ² it is ∫₀¹ u^m du, which n
        // Gauss–Legendre nodes integrate exactly for m ≤ 2n − 1 and not for m = 2n (the rule's
        // error for u^(2n) is nonzero: its error constant times (2n)!; a direct evaluation puts it
        // above 5·10⁻⁹ for n ≤ 7, so 10⁻⁹ separates it from rounding).
        for (int n = 1; n <= 6; n++)
        {
            var rule = PupilQuadrature.Gaussian(n);
            for (int m = 0; m <= 2 * n - 1; m++)
            {
                int mm = m;
                Assert.Equal(1.0 / (m + 1), rule.Mean((x, y) => Math.Pow(x * x + y * y, mm)), 14);
            }
            int top = 2 * n;
            double error = Math.Abs(rule.Mean((x, y) => Math.Pow(x * x + y * y, top)) - 1.0 / (top + 1));
            Assert.True(error > 1e-9, $"n = {n}: ρ^{2 * top} came out exact ({error:R}), which the rule cannot do.");
        }
    }

    [Fact]
    public void TheObscuredRule_AveragesOverTheAnnulus()
    {
        // Derived: over ε ≤ ρ ≤ 1 the mean of ρ² is ∫ρ³dρ/∫ρ dρ = ((1 − ε⁴)/4)/((1 − ε²)/2) = (1 + ε²)/2,
        // of ρ⁴ it is ((1 − ε⁶)/6)/((1 − ε²)/2) = (1 + ε² + ε⁴)/3.
        double e = 0.35;
        var rule = PupilQuadrature.Gaussian(3, obscuration: e);
        Assert.Equal((1 + e * e) / 2, rule.Mean((x, y) => x * x + y * y), 14);
        Assert.Equal((1 + e * e + e * e * e * e) / 3, rule.Mean((x, y) => Math.Pow(x * x + y * y, 2)), 14);
        Assert.All(rule.Points, p => Assert.InRange(p.Px * p.Px + p.Py * p.Py, e * e, 1));
    }

    // ---- Quadrature against a dense rectangular grid on a real lens ----

    private static SequentialLens Singlet() => Lens(S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064));   // near its paraxial focus (BFL ≈ 64.2 mm)

    [Theory]
    [InlineData(RmsData.WavefrontError, RmsReference.ChiefRay)]
    [InlineData(RmsData.WavefrontError, RmsReference.Centroid)]
    [InlineData(RmsData.SpotRadius, RmsReference.ChiefRay)]
    [InlineData(RmsData.SpotRadius, RmsReference.Centroid)]
    public void TheQuadratureRms_EqualsADenseGridRms_OnAnUnvignettedLens(RmsData data, RmsReference reference)
    {
        // An F/3.2 singlet with no apertures, on axis and at 10°: every ray of either rule passes.
        var lens = Singlet();
        var context = Context(Sys(0.02, 0, 10));
        var analysis = new RmsVsFieldAnalysis(_ => lens);
        double[] Run(PupilIntegration method, int density) => Curve(analysis.Compute(context, new RmsVsFieldSettings
        {
            Method = method, RayDensity = density, FieldDensity = 2, Data = data, Reference = reference,
        }, default));
        var gq20 = Run(PupilIntegration.GaussianQuadrature, 20);
        var gq10 = Run(PupilIntegration.GaussianQuadrature, 10);
        var grid = Run(PupilIntegration.RectangularArray, 301);
        for (int i = 0; i < 2; i++)
        {
            Assert.True(gq20[i] > 0 && double.IsFinite(gq20[i]), $"field {i}: {gq20[i]}");
            // The OPD and ray errors are analytic in the pupil, so the rule converges faster than
            // any power of n: 10 and 20 rings agree to rounding of the sums.
            Assert.True(Math.Abs(gq10[i] / gq20[i] - 1) <= 1e-9, $"field {i}: 10 rings {gq10[i]:R}, 20 rings {gq20[i]:R}.");
            // The dense grid's own error: a cell-centred grid of spacing h = 2/301 approximates the
            // disk's edge cell by cell. Evaluating both rules on smooth model wavefronts and ray
            // errors (ρ⁴ − ρ², plus coma, astigmatism and an exponential term) put the grid's
            // relative error of the RMS at 2·10⁻⁴ to 4·10⁻⁴ at 301 across and up to 3·10⁻³ at 101
            // to 201, erratically (an edge effect, not a power law); 2·10⁻³ allows five times the
            // 301 figure.
            Assert.True(Math.Abs(grid[i] / gq20[i] - 1) <= 2e-3, $"field {i}: grid {grid[i]:R}, quadrature {gq20[i]:R}.");
        }
    }

    // ---- The minimum of RMS against focus ----

    [Theory]
    [InlineData(RmsData.WavefrontError)]
    [InlineData(RmsData.SpotRadius)]
    public void TheRmsAgainstFocus_IsLeastAtTheAnalyticBestFocus_OfDefocusPlusSpherical(RmsData data)
    {
        // A concave spherical mirror, radius |R| = 200 mm, the stop on it, collimated light of
        // EPD 10 mm (marginal height h = 5 mm, F/10), the image at the paraxial focus |R|/2.
        //
        // Derivation of the best focus. A ray at height h reflects at angle α (sin α = h/|R|) and
        // crosses the axis |R|/(2 cos α) from the centre of curvature, so h²/(4|R|) closer to the
        // mirror than the paraxial focus to third order (|R|/(2cos α) ≈ |R|/2 + |R|α²/4): the
        // longitudinal spherical aberration LSA = h²/(4|R|). To third order the wavefront is
        // W(ρ) = aρ⁴ + bρ², b linear in the image position, b = 0 at the paraxial focus. The marginal
        // ray crosses the axis where its transverse error ∝ W′(1) = 4a + 2b vanishes, b = −2a.
        // The RMS wavefront with piston removed, ⟨W²⟩ − ⟨W⟩² over the disk (⟨ρ^{2m}⟩ = 1/(m + 1)),
        // is a²(1/5 − 1/9) + 2ab(1/4 − 1/6) + b²(1/3 − 1/4), least at b = −a: halfway, so the
        // best wavefront focus is LSA/2 = h²/(8|R|) toward the mirror. The RMS spot ∝ ⟨W′²⟩ =
        // ⟨(4aρ³ + 2bρ)²⟩ = 16a²/4 + 16ab/3 + 4b²/2, least at b = −4a/3: two thirds of the way,
        // h²/(6|R|). Both minima are on axis about the chief ray (the centroid by symmetry).
        //
        // The focus shift is added to the mirror's thickness (−100 mm), so a positive shift moves
        // the image toward the mirror (help, "RMS vs. Focus": with an odd number of mirrors
        // "negative focus values move the image surface farther away from the last component").
        //
        // Tolerance. Fifth-order spherical aberration and the ρ⁴ part of a real defocus are of
        // relative size (h/|R|)² and NA² (6·10⁻⁴, 2.5·10⁻³ here) and shift the minimum slightly;
        // an independent exact trace of this mirror (ray geometry and the reference sphere written
        // out, minimised numerically) put the true minima 1.7·10⁻⁴ (wavefront) and 5.1·10⁻⁴ (spot)
        // relative away from the third-order values. 2·10⁻³ allows four times the larger.
        const double radius = 0.2, h = 0.005;
        var lens = Lens(S(-radius, -radius / 2, stop: true, mirror: true));
        var system = Sys(2 * h);
        var unit = system.Units.LensUnit;
        double best = data == RmsData.WavefrontError ? h * h / (8 * radius) : h * h / (6 * radius);
        double top = 2 * h * h / (6 * radius);
        var result = new RmsVsFocusAnalysis(_ => lens).Compute(Context(system), new RmsVsFocusSettings
        {
            Method = PupilIntegration.GaussianQuadrature, RayDensity = 10, FocusDensity = 100, Data = data,
            MinFocus = 0, MaxFocus = OpticsConventions.FromMeters(top, unit),
        }, default);
        var curve = ((LinePlot)result.Plots[0]).Curves[0];
        var z = curve.X.Select(v => OpticsConventions.ToMeters(v, unit)).ToArray();
        var rms = curve.Y.ToArray();
        Assert.All(rms, v => Assert.True(double.IsFinite(v)));
        int k = Array.IndexOf(rms, rms.Min());
        Assert.InRange(k, 1, rms.Length - 2);
        // RMS² is quadratic in b and b is linear in the focus, so the parabola through the three
        // squared values about the smallest locates the minimum between samples.
        double y0 = rms[k - 1] * rms[k - 1], y1 = rms[k] * rms[k], y2 = rms[k + 1] * rms[k + 1];
        double step = z[k + 1] - z[k];
        double vertex = z[k] + 0.5 * step * (y0 - y2) / (y0 - 2 * y1 + y2);
        Assert.True(Math.Abs(vertex / best - 1) <= 2e-3, $"{data}: least RMS at a focus shift of {vertex:R} m, the analytic best focus is {best:R} m.");
    }
}
