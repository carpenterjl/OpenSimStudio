using OpenSim.Core.Numerics;
using OpenSim.Pcb.Inductance;
using Xunit;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The finite-section bar kernel (<see cref="PartialInductance.BarBarMutual"/>) against
/// oracles that share no code with it: the cross-section average of the exact filament
/// kernel by tensor Gauss–Legendre, the exact-GMD slender asymptote, the subdivision
/// identity, and values of Hoer &amp; Love's closed form evaluated to 60 digits outside
/// this code base (mpmath). The closed form and the quadrature are two independent
/// evaluations of one integral, and are also held against each other.
/// </summary>
public class RectangularBarKernelTests
{
    private const double Mu0Over2Pi = 2e-7;

    /// <summary>One bar pair in Hoer–Love's corner-offset geometry.</summary>
    private readonly record struct Pair(double A, double B, double L1, double C, double D, double L2,
        double E, double P, double L3)
    {
        public double Ratio => RectangularBarKernel.ConditioningRatio(A, B, L1, C, D, L2, E, P, L3);
        public bool Certified => RectangularBarKernel.ClosedFormIsCertified(A, B, L1, C, D, L2, E, P, L3);
        public double Mutual() => PartialInductance.BarBarMutual(A, B, L1, C, D, L2, E, P, L3);
        public double ClosedForm() => RectangularBarKernel.ClosedForm(A, B, L1, C, D, L2, E, P, L3);
        public double Quadrature(int orderScale = 1) =>
            RectangularBarKernel.Quadrature(A, B, L1, C, D, L2, E, P, L3, orderScale);
    }

    private static Pair Self(double width, double thickness, double length) =>
        new(width, thickness, length, width, thickness, length, 0, 0, 0);

    private static double RelativeError(double value, double reference) =>
        Math.Abs(value / reference - 1);

    // ---------------- test-local quadrature ----------------

    internal static (double[] Nodes, double[] Weights) GaussLegendre(int n)
    {
        var nodes = new double[n];
        var weights = new double[n];
        for (int i = 0; i < (n + 1) / 2; i++)
        {
            double x = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5));
            double dp = 0;
            for (int iteration = 0; iteration < 100; iteration++)
            {
                double p0 = 1, p1 = x;
                for (int k = 2; k <= n; k++)
                {
                    double pk = ((2 * k - 1) * x * p1 - (k - 1) * p0) / k;
                    p0 = p1;
                    p1 = pk;
                }
                dp = n * (x * p1 - p0) / (x * x - 1);
                double step = p1 / dp;
                x -= step;
                if (Math.Abs(step) < 1e-15) break;
            }
            nodes[i] = -x;
            nodes[n - 1 - i] = x;
            weights[i] = weights[n - 1 - i] = 2 / ((1 - x * x) * dp * dp);
        }
        return (nodes, weights);
    }

    /// <summary>
    /// The definition, evaluated head-on: the average over BOTH cross-sections of the
    /// exact filament mutual, by an n⁴-point tensor Gauss–Legendre rule.
    /// </summary>
    private static double FilamentAverage(Pair g, int n)
    {
        var (nodes, weights) = GaussLegendre(n);
        double sum = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double x1 = 0.5 * g.A * (1 + nodes[i]), y1 = 0.5 * g.B * (1 + nodes[j]);
                var a1 = new Vector3D(x1, y1, 0);
                var a2 = new Vector3D(x1, y1, g.L1);
                double inner = 0;
                for (int k = 0; k < n; k++)
                    for (int m = 0; m < n; m++)
                    {
                        double x2 = g.E + 0.5 * g.C * (1 + nodes[k]), y2 = g.P + 0.5 * g.D * (1 + nodes[m]);
                        inner += weights[k] * weights[m] * FilamentMutual.Between(
                            a1, a2, new Vector3D(x2, y2, g.L3), new Vector3D(x2, y2, g.L3 + g.L2));
                    }
                sum += weights[i] * weights[j] * inner;
            }
        return sum / 16;
    }

    // ------------------------------------------------------------------
    // Oracle (mutual): non-touching pairs, both regimes.
    // ------------------------------------------------------------------

    public static TheoryData<string, double[]> SeparatedPairs() => new()
    {
        // name, { a, b, l1, c, d, l2, E, P, l3 } [m]
        { "side by side, 1 mm traces at 2 mm pitch", new[] { 1e-3, 35e-6, 10e-3, 1e-3, 35e-6, 10e-3, 2e-3, 0, 0 } },
        { "stacked, 0.5 mm trace and its image at 2h + t = 235 µm", new[] { 0.5e-3, 35e-6, 10e-3, 0.5e-3, 35e-6, 10e-3, 0, 235e-6, 0 } },
        { "staggered along the run", new[] { 1e-3, 35e-6, 10e-3, 1e-3, 35e-6, 10e-3, 2e-3, 0, 6e-3 } },
        { "unequal sections and lengths, offset on all axes", new[] { 1e-3, 35e-6, 10e-3, 0.3e-3, 70e-6, 4e-3, 1.7e-3, 0.2e-3, 8e-3 } },
        { "stubby boxes (closed-form regime)", new[] { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.5, 0.3, 0.4 } },
        { "stubby unequal boxes (closed-form regime)", new[] { 1.0, 0.5, 2.0, 0.7, 1.2, 1.0, 1.6, -0.4, 1.5 } }
    };

    [Theory]
    [MemberData(nameof(SeparatedPairs))]
    public void SeparatedPairs_MatchTheCrossSectionAverageOfTheFilamentKernel(string name, double[] g)
    {
        var pair = new Pair(g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7], g[8]);
        double coarse = FilamentAverage(pair, 8);
        double medium = FilamentAverage(pair, 16);
        double fine = FilamentAverage(pair, 32);

        // The oracle has converged (geometrically: the integrand is analytic)...
        Assert.True(RelativeError(medium, fine) < 1e-8, $"{name}: oracle 16 → 32 moved {RelativeError(medium, fine):e2}");
        Assert.True(RelativeError(medium, fine) <= RelativeError(coarse, medium) + 1e-13, name);
        // ...and the kernel is that number.
        Assert.True(RelativeError(pair.Mutual(), fine) < 1e-8,
            $"{name}: kernel {pair.Mutual():r} vs filament average {fine:r}");
    }

    [Fact]
    public void BothRegimes_AreExercisedByTheSeparatedPairs()
    {
        var certified = SeparatedPairs().Select(row => (double[])row[1])
            .Select(g => new Pair(g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7], g[8]).Certified)
            .ToList();
        Assert.Contains(true, certified);
        Assert.Contains(false, certified);
    }

    [Fact]
    public void CollinearAdjoiningBars_MatchTheFilamentAverage()
    {
        // Two bars end to end. The filament kernel is finite for distinct filaments, so
        // the same definition applies — but the junction leaves a cone at zero
        // section-offset, on which the plain tensor rule converges only algebraically
        // (it is still 3.5e-5 out at 32⁴ points). The oracle therefore handles the
        // junction in the quadrature: for each filament of bar 1, bar 2's section is
        // integrated in Duffy coordinates centred on that filament, where the cone is a
        // polynomial.
        var pair = new Pair(1e-3, 0.5e-3, 2e-3, 1e-3, 0.5e-3, 2e-3, 0, 0, 2e-3);
        double coarse = JunctionFilamentAverage(pair, 6);
        double medium = JunctionFilamentAverage(pair, 12);
        double fine = JunctionFilamentAverage(pair, 24);

        Assert.True(RelativeError(medium, fine) < RelativeError(coarse, medium),
            "The oracle must be converging.");
        Assert.True(RelativeError(medium, fine) < 1e-6, $"oracle 12 → 24 moved {RelativeError(medium, fine):e2}");
        Assert.True(RelativeError(pair.Mutual(), fine) < 1e-6,
            $"kernel {pair.Mutual():r} vs filament average {fine:r}");
        // The plain tensor rule agrees as far as it has converged.
        Assert.True(RelativeError(FilamentAverage(pair, 16), fine) < 1e-3);

        // The filament-level value the composer used before (centre lines only) is
        // (µ₀/2π)·l·ln 2 — off by far more than the oracle's resolution.
        double centreLines = Mu0Over2Pi * 2e-3 * Math.Log(2);
        Assert.True(RelativeError(centreLines, fine) > 1e-2);
    }

    /// <summary>
    /// The filament average for two bars of IDENTICAL, aligned section: for each
    /// filament (x₁, y₁) of bar 1, bar 2's section is split into the four rectangles
    /// cornered on it, each into two triangles mapped from the unit square
    /// (δ = (X·u, Y·u·v) and (X·u·v, Y·u), Jacobian X·Y·u).
    /// </summary>
    private static double JunctionFilamentAverage(Pair g, int n)
    {
        var (nodes, weights) = GaussLegendre(n);
        double[] unit = nodes.Select(x => 0.5 * (1 + x)).ToArray();
        double[] half = weights.Select(w => 0.5 * w).ToArray();

        double outer = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double x1 = g.A * unit[i], y1 = g.B * unit[j];
                var a1 = new Vector3D(x1, y1, 0);
                var a2 = new Vector3D(x1, y1, g.L1);
                double inner = 0;
                foreach (int sx in new[] { -1, 1 })
                    foreach (int sy in new[] { -1, 1 })
                    {
                        double xSpan = sx < 0 ? x1 : g.A - x1, ySpan = sy < 0 ? y1 : g.B - y1;
                        for (int p = 0; p < n; p++)
                            for (int q = 0; q < n; q++)
                            {
                                double u = unit[p], v = unit[q];
                                double jacobian = half[p] * half[q] * xSpan * ySpan * u;
                                foreach (var (dx, dy) in new[] { (xSpan * u, ySpan * u * v), (xSpan * u * v, ySpan * u) })
                                {
                                    double x2 = x1 + sx * dx, y2 = y1 + sy * dy;
                                    inner += jacobian * FilamentMutual.Between(a1, a2,
                                        new Vector3D(x2, y2, g.L3), new Vector3D(x2, y2, g.L3 + g.L2));
                                }
                            }
                    }
                outer += half[i] * half[j] * inner;
            }
        return outer / (g.A * g.B);
    }

    // ------------------------------------------------------------------
    // Oracle (self), two independent gates.
    // ------------------------------------------------------------------

    /// <summary>Maxwell's closed form for ln of the self-GMD of a w × t rectangle.</summary>
    private static double LogSelfGmd(double w, double t) =>
        Math.Log(Math.Sqrt(w * w + t * t))
        - w * w / (12 * t * t) * Math.Log(1 + t * t / (w * w))
        - t * t / (12 * w * w) * Math.Log(1 + w * w / (t * t))
        + 2 * w / (3 * t) * Math.Atan(t / w)
        + 2 * t / (3 * w) * Math.Atan(w / t)
        - 25.0 / 12;

    /// <summary>Mean distance between two uniform random points of a w × t rectangle.</summary>
    private static double SelfArithmeticMeanDistance(double w, double t)
    {
        double d = Math.Sqrt(w * w + t * t);
        return (w * w * w / (t * t) + t * t * t / (w * w)
                + d * (3 - w * w / (t * t) - t * t / (w * w))
                + 2.5 * (t * t / w * Math.Log((w + d) / t) + w * w / t * Math.Log((t + d) / w))) / 15;
    }

    /// <summary>
    /// The slender-bar series L = (µ₀/2π)·l·[ln(2l/R) − 1 + R_a/l − (w²+t²)/(24l²)]
    /// with the EXACT self-GMD R and arithmetic mean distance R_a. Next term O((w/l)⁴).
    /// A test oracle only — it is not a production branch.
    /// </summary>
    internal static double SlenderSeries(double l, double w, double t) =>
        Mu0Over2Pi * l * (Math.Log(2 * l) - LogSelfGmd(w, t) - 1
                          + SelfArithmeticMeanDistance(w, t) / l
                          - (w * w + t * t) / (24 * l * l));

    [Fact]
    public void SelfInductance_ApproachesTheExactGmdAsymptote_AtTheRightRate()
    {
        // L/(µ₀l/2π) − [ln(2l/R) − 1] is the end correction R_a/l − …: it must fall by
        // ten per decade of length. (Grover's 0.2235(w+t) is itself an approximation
        // to R, 2e-4 out, so the exact Maxwell form anchors this.)
        const double w = 1e-3, t = 35e-6;
        double Residual(double ratio)
        {
            double l = ratio * (w + t);
            return PartialInductance.SelfInductance(l, w, t) / (Mu0Over2Pi * l)
                   - (Math.Log(2 * l) - LogSelfGmd(w, t) - 1);
        }
        double r3 = Residual(1e3), r4 = Residual(1e4), r5 = Residual(1e5);
        Assert.True(r3 > 0 && r4 > 0 && r5 > 0);
        Assert.InRange(r4 / r3, 0.095, 0.105);
        Assert.InRange(r5 / r4, 0.095, 0.105);
        // The coefficient is the arithmetic mean distance: r ≈ R_a/l.
        Assert.Equal(SelfArithmeticMeanDistance(w, t) / (1e4 * (w + t)), r4, r4 * 1e-4);
    }

    [Theory]
    [InlineData(1e-3, 1e-3)]         // square
    [InlineData(1e-3, 5e-6)]         // wide and thin
    [InlineData(5e-6, 1e-3)]         // tall and thin
    public void SlenderSelf_MatchesTheAsymptoticOracle(double w, double t)
    {
        // l / max(w, t) = 3·10⁴ — far outside the closed form's certified range.
        double l = 3e4 * Math.Max(w, t);
        var bar = Self(w, t, l);
        Assert.False(bar.Certified);
        Assert.True(RelativeError(bar.Mutual(), SlenderSeries(l, w, t)) < 1e-8,
            $"{bar.Mutual():r} vs series {SlenderSeries(l, w, t):r}");
    }

    [Fact]
    public void TheMetreLongFoil_IsRight_WhereTheClosedFormHasLostItsDigits()
    {
        // 1 m × 1 mm × 5 µm: l / max(w, t) = 1000, L/s = 2·10⁵.
        var bar = Self(1e-3, 5e-6, 1);
        const double reference = 1.6192061091306202e-6;       // 60-digit closed form
        Assert.False(bar.Certified);
        Assert.True(RelativeError(bar.Mutual(), reference) < 1e-9);
        Assert.True(RelativeError(bar.Mutual(), SlenderSeries(1, 1e-3, 5e-6)) < 1e-8);
        // The same closed form in double precision has lost every digit that matters.
        Assert.True(RelativeError(bar.ClosedForm(), reference) > 1e-2);
    }

    [Theory]
    [InlineData(1.0, 1.0, 1.0, 1e-10)]                 // stubby: closed form throughout
    [InlineData(1e-3, 35e-6, 10e-3, 1e-10)]            // the reference trace: quadrature
    [InlineData(0.2e-3, 35e-6, 100e-3, 1e-10)]         // slender
    public void SelfInductance_SatisfiesTheSubdivisionIdentity(double w, double t, double l, double tolerance)
    {
        // Cut the bar 2 (along) × 2 × 2 (across). The four section cells are PARALLEL
        // paths carrying I/4 each, so L = Σᵢⱼ fᵢfⱼMᵢⱼ with every f = 1/4: the 8 × 8 sum
        // weighted by 1/16. Only smaller selfs and oracle-verified mutuals enter.
        double sum = 0;
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 8; j++)
            {
                double e = ((j & 1) - (i & 1)) * w / 2;
                double p = (((j >> 1) & 1) - ((i >> 1) & 1)) * t / 2;
                double l3 = (((j >> 2) & 1) - ((i >> 2) & 1)) * l / 2;
                sum += PartialInductance.BarBarMutual(w / 2, t / 2, l / 2, w / 2, t / 2, l / 2, e, p, l3);
            }
        double composed = sum / 16;
        double whole = PartialInductance.SelfInductance(l, w, t);
        Assert.True(RelativeError(composed, whole) < tolerance,
            $"8-cell composition {composed:r} vs whole bar {whole:r}");
    }

    // ------------------------------------------------------------------
    // Pinned values (60-digit evaluation of the closed form, mpmath).
    // ------------------------------------------------------------------

    [Fact]
    public void UnitCubeSelf_IsPinned_InBothRegimes()
    {
        const double reference = 1.8823126443896602e-7;
        var cube = Self(1, 1, 1);
        Assert.True(cube.Certified);
        Assert.True(RelativeError(cube.ClosedForm(), reference) < 1e-13);
        Assert.True(RelativeError(cube.Quadrature(), reference) < 1e-9);
        Assert.True(RelativeError(cube.Quadrature(2), reference) < 1e-12);
    }

    [Fact]
    public void ThePrimitive_KeepsThePapersPrincipalBranch()
    {
        // f(1, 1, −1): a negative coordinate makes z·r negative, where atan2 would sit π
        // away from the paper's atan. With atan2 this value — and the unit-cube self,
        // by 334 % — comes out wrong but FINITE, so only a pin can catch it.
        Assert.Equal(-0.1057649943248329887, RectangularBarKernel.Primitive(1, 1, -1), 1e-15);
        Assert.Equal(-6.837549061025062744, RectangularBarKernel.Primitive(-2, 3, -0.5), 1e-13);

        // f is EVEN in each argument (the principal branch is what makes it so).
        foreach (int sx in new[] { 1, -1 })
            foreach (int sy in new[] { 1, -1 })
                foreach (int sz in new[] { 1, -1 })
                    Assert.Equal(RectangularBarKernel.Primitive(2, 3, 0.5),
                        RectangularBarKernel.Primitive(2 * sx, 3 * sy, 0.5 * sz), 1e-13);
    }

    [Fact]
    public void ThePrimitive_IsFiniteOnEveryCoordinatePlaneAndAxis()
    {
        foreach (double x in new[] { 0.0, 1.0, -2.0 })
            foreach (double y in new[] { 0.0, 1.0, -2.0 })
                foreach (double z in new[] { 0.0, 1.0, -2.0 })
                    Assert.True(double.IsFinite(RectangularBarKernel.Primitive(x, y, z)), $"f({x}, {y}, {z})");
        Assert.Equal(0.0, RectangularBarKernel.Primitive(0, 0, 0));
    }

    // ------------------------------------------------------------------
    // Near-touching pairs inside the certified ratio: the asinh form is load-bearing.
    // ------------------------------------------------------------------

    /// <summary>The closed form with the paper's logarithm written LITERALLY,
    /// p·ln((p + r)/ρ) — the negative control.</summary>
    private static double LiteralLogClosedForm(Pair g)
    {
        static double Log(double p, double q, double s)
        {
            double rho = Math.Sqrt(q * q + s * s);
            if (rho == 0) return 0;                        // the same ρ = 0 limit as shipped
            double r = Math.Sqrt(p * p + q * q + s * s);
            return p * Math.Log((p + r) / rho);
        }
        static double Atan(double numerator, double denominator) =>
            numerator == 0 ? 0 : Math.Atan(numerator / denominator);
        static double F(double x, double y, double z)
        {
            double x2 = x * x, y2 = y * y, z2 = z * z, r = Math.Sqrt(x2 + y2 + z2);
            return (y2 * z2 / 4 - y2 * y2 / 24 - z2 * z2 / 24) * Log(x, y, z)
                   + (x2 * z2 / 4 - x2 * x2 / 24 - z2 * z2 / 24) * Log(y, x, z)
                   + (x2 * y2 / 4 - x2 * x2 / 24 - y2 * y2 / 24) * Log(z, x, y)
                   + (x2 * x2 + y2 * y2 + z2 * z2 - 3 * x2 * y2 - 3 * y2 * z2 - 3 * z2 * x2) * r / 60
                   - x * y * z * z2 / 6 * Atan(x * y, z * r)
                   - x * y * y2 * z / 6 * Atan(x * z, y * r)
                   - x * x2 * y * z / 6 * Atan(y * z, x * r);
        }
        double[] q = { g.E - g.A, g.E + g.C - g.A, g.E + g.C, g.E };
        double[] r = { g.P - g.B, g.P + g.D - g.B, g.P + g.D, g.P };
        double[] s = { g.L3 - g.L1, g.L3 + g.L2 - g.L1, g.L3 + g.L2, g.L3 };
        double sum = 0;
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 4; k++)
                    sum += (((i + j + k) & 1) == 0 ? 1 : -1) * F(q[i], r[j], s[k]);
        return 1e-7 * sum / (g.A * g.B * g.C * g.D);
    }

    public static TheoryData<string, double[], double> NearTouchingPairs() => new()
    {
        { "unit boxes a hair apart, E = 1 + 1e-12", new[] { 1.0, 1, 1, 1, 1, 1, 1 + 1e-12, 0, 0 }, 9.8088518360005225e-8 },
        { "touching side by side with a tiny transverse offset, P = 1e-12", new[] { 1.0, 1, 1, 1, 1, 1, 1, 1e-12, 0 }, 9.8088518360097823e-8 },
        { "nearly adjoining along the run, l3 = l1·(1 + 1e-12)", new[] { 1.0, 1, 1, 1, 1, 1, 0, 0, 1 + 1e-12 }, 9.8088518360005225e-8 }
    };

    [Theory]
    [MemberData(nameof(NearTouchingPairs))]
    public void NearTouchingPairs_AreFiniteAndRight_WhereTheLiteralLogarithmIsNot(
        string name, double[] g, double reference)
    {
        var pair = new Pair(g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7], g[8]);
        Assert.True(pair.Certified, name);

        double value = pair.Mutual();
        Assert.True(double.IsFinite(value), name);
        Assert.True(RelativeError(value, reference) < 1e-10, $"{name}: {value:r}");
        Assert.True(RelativeError(value, pair.Quadrature(2)) < 1e-10, name);

        Assert.False(double.IsFinite(LiteralLogClosedForm(pair)),
            $"{name}: the literal ln((p + r)/ρ) was expected to break here — if it no longer does, " +
            "this case has stopped proving that the asinh form is needed.");
    }

    // ------------------------------------------------------------------
    // Regime boundary: derived, not chosen.
    // ------------------------------------------------------------------

    private static readonly (string Name, Func<double, Pair> At)[] Families =
    {
        ("self, long", r => Self(1, 1, r)),
        ("self, short and wide", r => Self(r, 1, 1)),
        ("self, short and tall", r => Self(1, r, 1)),
        ("self, flat plate", r => Self(r, 1, r)),
        ("self, three scales", r => Self(Math.Sqrt(r), 1, r)),
        ("side by side, long", r => new Pair(1, 1, r, 1, 1, r, 1.5, 0, 0)),
        ("side by side, far", r => new Pair(1, 1, 1, 1, 1, 1, r, 0, 0)),
        ("stacked, far", r => new Pair(1, 1, 1, 1, 1, 1, 0, r, 0)),
        ("diagonal, far", r => new Pair(1, 1, 1, 1, 1, 1, r, r, 0)),
        ("stacked wide plates", r => new Pair(r, 1, r, r, 1, r, 0, 2.5, 0)),
        ("collinear adjoining, long", r => new Pair(1, 1, r, 1, 1, r, 0, 0, r)),
        ("collinear gap", r => new Pair(1, 1, 1, 1, 1, 1, 0, 0, r)),
        ("unequal lengths", r => new Pair(1, 1, r, 1, 1, 1, 1.5, 0, 0.3 * r)),
        ("unequal sections", r => new Pair(r, 1, r, 1, 1, r / 2, 0.3 * r, 1.7, 0.1 * r)),
        ("far on every axis", r => new Pair(1, 1, 1, 1, 1, 1, r, r, r)),
        ("far on every axis, negative offsets", r => new Pair(1, 1, 1, 1, 1, 1, -r, r, -r)),
        ("far on every axis, unequal offsets", r => new Pair(1, 1, 1, 1, 1, 1, r, 0.8 * r, r))
    };

    private static readonly double[] RatioGrid = { 2, 3, 4, 6, 8, 12, 16, 20, 24, 32, 48, 64, 96, 128, 192, 256 };

    [Fact]
    public void ClosedFormRatio_IsTheOneTheQuadratureCertifies()
    {
        // For every family, the smallest conditioning ratio at which the closed form
        // and the quadrature part by more than 1e-8. The closed form is the one that
        // has failed there: the quadrature's own convergence is asserted alongside.
        double firstFailure = double.PositiveInfinity;
        string failing = "";
        foreach (var (name, at) in Families)
            foreach (double r in RatioGrid)
            {
                var pair = at(r);
                double quadrature = pair.Quadrature();
                Assert.True(RelativeError(quadrature, pair.Quadrature(2)) < 1e-10,
                    $"{name} at {r}: the quadrature itself has not converged");

                double error = RelativeError(pair.ClosedForm(), quadrature);
                if (pair.Ratio <= RectangularBarKernel.ClosedFormMaxRatio)
                    Assert.True(error < 1e-8, $"{name} at L/s = {pair.Ratio}: closed form off by {error:e2}");
                if (error >= 1e-8 && pair.Ratio < firstFailure)
                {
                    firstFailure = pair.Ratio;
                    failing = name;
                }
            }

        // The production constant sits below the first failure — and not so far below
        // that it has stopped being a derived number.
        Assert.True(double.IsFinite(firstFailure), "No family failed: the grid does not reach the boundary.");
        Assert.True(RectangularBarKernel.ClosedFormMaxRatio < firstFailure,
            $"'{failing}' fails at L/s = {firstFailure}");
        Assert.True(RectangularBarKernel.ClosedFormMaxRatio >= firstFailure / 4,
            $"first failure at L/s = {firstFailure} ('{failing}'): the constant is needlessly tight");
    }

    [Fact]
    public void TheRegimes_AgreeAcrossTheBoundary_AndTheRoutingIsWhatItSays()
    {
        foreach (var (name, at) in Families)
        {
            // Scale the family parameter so the pair sits exactly on the boundary...
            var probe = at(RectangularBarKernel.ClosedFormMaxRatio);
            double parameter = RectangularBarKernel.ClosedFormMaxRatio
                               * RectangularBarKernel.ClosedFormMaxRatio / probe.Ratio;
            var inside = at(parameter * (1 - 1e-9));
            var outside = at(parameter * (1 + 1e-6));
            Assert.True(inside.Certified, name);
            Assert.False(outside.Certified, name);

            // ...where the two evaluations of one integral agree to 1e-8,
            Assert.True(RelativeError(inside.ClosedForm(), inside.Quadrature()) < 1e-8, name);
            Assert.True(RelativeError(outside.ClosedForm(), outside.Quadrature()) < 1e-8, name);
            // and each side is routed to the evaluation that is certified for it.
            Assert.Equal(inside.ClosedForm(), inside.Mutual());
            Assert.Equal(outside.Quadrature(), outside.Mutual());
        }
    }

    [Fact]
    public void AShortWideBar_IsRoutedAwayFromTheClosedForm()
    {
        // The metre-long foil turned on its side: l = 5 µm, w = 1 m, t = 1 mm. Judged by
        // length over section it looks stubby (0.005) — and the closed form is 5 % out.
        // The ratio therefore takes ALL six dimensions.
        var bar = Self(1, 1e-3, 5e-6);
        const double reference = 4.0480152728265504e-17;
        Assert.False(bar.Certified);
        Assert.True(RelativeError(bar.Mutual(), reference) < 1e-9, $"{bar.Mutual():r}");
        Assert.True(RelativeError(bar.ClosedForm(), reference) > 1e-2);

        // Unequal lengths count too: a short bar beside a long one.
        Assert.False(new Pair(1, 1, 100, 1, 1, 1, 1.5, 0, 50).Certified);
    }

    // ------------------------------------------------------------------
    // The quadrature's order rule: near cells, far cells, and everything between.
    // ------------------------------------------------------------------

    public static TheoryData<string, double[]> SingularPairs() => new()
    {
        { "reference trace, self", new[] { 1e-3, 35e-6, 10e-3, 1e-3, 35e-6, 10e-3, 0, 0, 0 } },
        { "side by side, touching", new[] { 1e-3, 35e-6, 10e-3, 1e-3, 35e-6, 10e-3, 1e-3, 0, 0 } },
        { "collinear, adjoining", new[] { 1e-3, 35e-6, 1e-3, 1e-3, 35e-6, 1e-3, 0, 0, 1e-3 } },
        { "sections and runs partly overlapping", new[] { 1e-3, 35e-6, 10e-3, 0.6e-3, 35e-6, 7e-3, 0.7e-3, 0, 2e-3 } },
        { "metre-long foil, self", new[] { 1e-3, 5e-6, 1.0, 1e-3, 5e-6, 1.0, 0, 0, 0 } },
        { "short wide bar, self", new[] { 1.0, 1e-3, 5e-6, 1.0, 1e-3, 5e-6, 0, 0, 0 } }
    };

    [Theory]
    [MemberData(nameof(SingularPairs))]
    public void TheQuadrature_HasConverged_WhereTheIntegrandIsSingular(string name, double[] g)
    {
        var pair = new Pair(g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7], g[8]);
        Assert.True(RelativeError(pair.Quadrature(), pair.Quadrature(2)) < 1e-10, name);
    }

    [Fact]
    public void TheLowOrderFarRule_MatchesTheHighOrderRule_AtEverySeparation()
    {
        // Far cells take two or three points per direction, near ones eight. Sweeping
        // the separation from touching to 300 widths — laterally, vertically,
        // diagonally and along the run — crosses every order the rule can choose; at
        // each, the production rule equals the doubled-order rule. There is no
        // cross-over distance to tune, and no jump where one would be.
        const double w = 1e-3, t = 35e-6, l = 10e-3;
        var directions = new (string Name, double X, double Y, double Z)[]
        {
            ("lateral", 1, 0, 0), ("vertical", 0, 1, 0),
            ("diagonal", Math.Sqrt(0.5), Math.Sqrt(0.5), 0), ("along the run", 0, 0, 1)
        };
        foreach (var (name, x, y, z) in directions)
        {
            double previous = double.NaN;
            for (double separation = 1.05e-3; separation < 0.3; separation *= 1.25)
            {
                // Centre offset along the direction; along the run the bars are collinear
                // with a gap of `separation`.
                var pair = new Pair(w, t, l, w, t, l, x * separation, y * separation,
                    z * (l + separation));
                double value = pair.Quadrature();
                Assert.True(RelativeError(value, pair.Quadrature(2)) < 1e-10,
                    $"{name} at {separation * 1e3:g4} mm: {RelativeError(value, pair.Quadrature(2)):e2}");
                if (!double.IsNaN(previous))
                    Assert.True(value < previous, $"{name}: coupling must fall with distance");
                previous = value;
            }
        }
    }

    // ------------------------------------------------------------------
    // What finite sections do to the mean distance — and what the removed
    // small-section expansion ln GMD = ln d − (w² + t²)/(24d²) got wrong.
    // ------------------------------------------------------------------

    [Fact]
    public void FiniteSections_ShiftCoupling_ByOrientation_AsTheSecondOrderExpansionSays()
    {
        // For two identical w × t sections a distance d apart, averaging ln|d + δ| over
        // the section-difference δ gives, to second order,
        //     ln GMD = ln d + (⟨δ⊥²⟩ − ⟨δ∥²⟩)/(2d²),   ⟨δx²⟩ = w²/6, ⟨δy²⟩ = t²/6:
        //     side by side (offset along the width):  ln d − (w² − t²)/(12d²)
        //     stacked (offset along the thickness):   ln d + (w² − t²)/(12d²).
        // So at equal centre distance a wide trace couples MORE to a neighbour beside
        // it than to one above it, by (µ₀/2π)·l·(w² − t²)/(6d²) for long bars. The
        // removed form had no orientation in it at all, and for the stacked
        // (trace-over-image) pair the wrong sign.
        const double w = 1e-3, t = 35e-6, l = 1.0, d = 10e-3;
        double beside = PartialInductance.BarBarMutual(w, t, l, w, t, l, d, 0, 0);
        double above = PartialInductance.BarBarMutual(w, t, l, w, t, l, 0, d, 0);
        double filament = Mu0Over2Pi * l * (Math.Asinh(l / d) - Math.Sqrt(1 + d * d / (l * l)) + d / l);
        double shift = Mu0Over2Pi * l * (w * w - t * t) / (12 * d * d);

        Assert.True(beside > filament && filament > above);
        Assert.Equal(shift, beside - filament, shift * 0.02);
        Assert.Equal(shift, filament - above, shift * 0.02);

        // The removed expansion moved BOTH by +(w² + t²)/(24d²) in ln-GMD terms.
        double removed = Mu0Over2Pi * l * (w * w + t * t) / (24 * d * d);
        Assert.True(Math.Abs((filament - above) - (-removed)) > shift);
    }

    // ------------------------------------------------------------------
    // Contract.
    // ------------------------------------------------------------------

    [Fact]
    public void TheKernel_IsSymmetric_Deterministic_AndRejectsNonsense()
    {
        var pair = new Pair(1e-3, 35e-6, 10e-3, 0.3e-3, 70e-6, 4e-3, 1.7e-3, 0.2e-3, 8e-3);
        // Swapping the bars negates the corner offsets after exchanging the boxes.
        var swapped = new Pair(pair.C, pair.D, pair.L2, pair.A, pair.B, pair.L1, -pair.E, -pair.P, -pair.L3);
        Assert.Equal(pair.Mutual(), swapped.Mutual(), pair.Mutual() * 1e-11);
        Assert.Equal(pair.Mutual(), pair.Mutual());          // bitwise repeatable

        // The integral of 1/r over two boxes does not know which axis carries the
        // current: adjoining unit cubes couple equally end to end and side by side.
        double endToEnd = new Pair(1, 1, 1, 1, 1, 1, 0, 0, 1).Mutual();
        double sideBySide = new Pair(1, 1, 1, 1, 1, 1, 1, 0, 0).Mutual();
        Assert.Equal(endToEnd, sideBySide, endToEnd * 1e-12);

        Assert.Throws<ArgumentOutOfRangeException>(() => PartialInductance.BarBarMutual(0, 1, 1, 1, 1, 1, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PartialInductance.BarBarMutual(1, 1, 1, 1, -1, 1, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PartialInductance.BarBarMutual(1, 1, 1, 1, 1, 1, double.NaN, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PartialInductance.SelfInductance(1, 0, 1));
    }
}
