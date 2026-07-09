using OpenSim.Core.Numerics;
using OpenSim.Pcb.Inductance;
using Xunit;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The general straight-filament mutual-inductance kernel against independent oracles:
/// hand-evaluated closed forms for the structured cases and a test-local Gauss–Legendre
/// evaluation of the Neumann double integral for the general ones. The quadrature lives
/// HERE, not in shipped code — one shipped evaluation path, one independent check.
/// </summary>
public class FilamentMutualTests
{
    private static Vector3D Mm(double x, double y, double z) => new(x * 1e-3, y * 1e-3, z * 1e-3);

    // ---------------- Neumann-integral oracle (Gauss–Legendre 64×64) ----------------

    private static (double[] Nodes, double[] Weights) GaussLegendre(int n)
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

    /// <summary>M = (µ₀/4π)·cosε·∬ ds dt / |A(s) − B(t)| by 64×64 Gauss–Legendre.</summary>
    private static double NeumannOracle(Vector3D a1, Vector3D a2, Vector3D b1, Vector3D b2)
    {
        const int n = 64;
        var (nodes, weights) = GaussLegendre(n);
        var da = a2 - a1;
        var db = b2 - b1;
        double l = da.Length, m = db.Length;
        double cos = Vector3D.Dot(da, db) / (l * m);

        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            var pa = a1 + da * (0.5 * (nodes[i] + 1));
            for (int j = 0; j < n; j++)
            {
                var pb = b1 + db * (0.5 * (nodes[j] + 1));
                sum += weights[i] * weights[j] / (pa - pb).Length;
            }
        }
        return 1e-7 * cos * sum * (l / 2) * (m / 2);
    }

    // ---------------- structured cases with closed-form references ----------------

    [Fact]
    public void EqualParallel_WithGmd_MatchesTheLegacyFormulaExactly()
    {
        // Full overlap must reduce identically to Grover's equal-parallel formula that
        // PartialInductance has always shipped (its 2.99 nH pin stays authoritative).
        const double l = 10e-3, d = 2e-3, w = 4e-4, t = 35e-6;
        double gmd = PartialInductance.GeometricMeanDistance(d, w, t);

        double kernel = FilamentMutual.Between(
            Mm(0, 0, 0), Mm(10, 0, 0), Mm(0, 2, 0), Mm(10, 2, 0), gmd);
        double legacy = PartialInductance.MutualInductanceParallel(l, d, w, t);

        Assert.Equal(legacy, kernel, legacy * 1e-12);
    }

    [Fact]
    public void EqualParallel_PureFilament_MatchesHandValueAndOracle()
    {
        // l = 100 mm, d = 10 mm: M = 2e-7·l·[asinh(l/d) − √(1+(d/l)²) + d/l] ≈ 41.86 nH.
        const double l = 0.1, d = 0.01;
        double expected = 2e-7 * l * (Math.Asinh(l / d) - Math.Sqrt(1 + (d / l) * (d / l)) + d / l);

        double kernel = FilamentMutual.Between(
            new Vector3D(0, 0, 0), new Vector3D(l, 0, 0),
            new Vector3D(0, d, 0), new Vector3D(l, d, 0));

        Assert.Equal(41.86e-9, expected, 0.01e-9);         // the hand value itself
        Assert.Equal(expected, kernel, expected * 1e-12);
        double oracle = NeumannOracle(
            new Vector3D(0, 0, 0), new Vector3D(l, 0, 0),
            new Vector3D(0, d, 0), new Vector3D(l, d, 0));
        Assert.Equal(oracle, kernel, Math.Abs(oracle) * 1e-8);
    }

    [Fact]
    public void CollinearTouching_MatchesTheExactLogForm()
    {
        // Equal collinear filaments sharing an endpoint: M = (µ₀/2π)·l·ln 2 exactly.
        double m = FilamentMutual.Between(Mm(0, 0, 0), Mm(10, 0, 0), Mm(10, 0, 0), Mm(20, 0, 0));
        double expected = 2e-7 * 10e-3 * Math.Log(2);
        Assert.Equal(1.3863e-9, expected, 0.0001e-9);
        Assert.Equal(expected, m, 1e-13);
    }

    [Fact]
    public void CollinearWithGap_MatchesTheClosedForm_AndOracle()
    {
        // A = [0, l], B = [l+g, l+g+m] with l = m = 10 mm, g = 5 mm:
        // M = 1e-7·[(2l+g)ln(2l+g) + g·ln g − 2(l+g)ln(l+g)].
        const double l = 10e-3, g = 5e-3;
        var a1 = new Vector3D(0, 0, 0);
        var a2 = new Vector3D(l, 0, 0);
        var b1 = new Vector3D(l + g, 0, 0);
        var b2 = new Vector3D(2 * l + g, 0, 0);

        double expected = 1e-7 * ((2 * l + g) * Math.Log(2 * l + g)
                                  + g * Math.Log(g)
                                  - 2 * (l + g) * Math.Log(l + g));
        double kernel = FilamentMutual.Between(a1, a2, b1, b2);
        Assert.Equal(expected, kernel, Math.Abs(expected) * 1e-12);
        Assert.Equal(NeumannOracle(a1, a2, b1, b2), kernel, Math.Abs(expected) * 1e-6);
    }

    [Fact]
    public void CollinearOverlap_ThrowsInsteadOfDiverging()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FilamentMutual.Between(
            Mm(0, 0, 0), Mm(10, 0, 0), Mm(5, 0, 0), Mm(15, 0, 0)));
        Assert.Contains("degenerate", ex.Message);
    }

    [Fact]
    public void StaggeredParallel_MatchesOracle()
    {
        // Longitudinal offset — the case the old parallel-equal-bar approximation
        // (mean length, midpoint separation) got wrong.
        var a1 = Mm(0, 0, 0);
        var a2 = Mm(10, 0, 0);
        var b1 = Mm(10, 2, 0);
        var b2 = Mm(20, 2, 0);
        double kernel = FilamentMutual.Between(a1, a2, b1, b2);
        double oracle = NeumannOracle(a1, a2, b1, b2);
        Assert.Equal(oracle, kernel, Math.Abs(oracle) * 1e-8);
    }

    [Fact]
    public void AntiParallel_IsTheNegativeOfParallel()
    {
        double parallel = FilamentMutual.Between(Mm(0, 0, 0), Mm(10, 0, 0), Mm(0, 2, 0), Mm(10, 2, 0));
        double opposed = FilamentMutual.Between(Mm(0, 0, 0), Mm(10, 0, 0), Mm(10, 2, 0), Mm(0, 2, 0));
        Assert.True(parallel > 0);
        Assert.Equal(-parallel, opposed, Math.Abs(parallel) * 1e-14);
    }

    [Fact]
    public void RightAngles_AreExactlyZero()
    {
        // cosε multiplies the whole Neumann integral: perpendicular ⇒ 0, not just small.
        Assert.Equal(0.0, FilamentMutual.Between(
            Mm(0, 0, 0), Mm(10, 0, 0), Mm(15, 0, 0), Mm(15, 10, 0)));
        Assert.Equal(0.0, FilamentMutual.Between(          // skew-perpendicular (out of plane)
            Mm(0, 0, 0), Mm(10, 0, 0), Mm(4, -2, 3), Mm(4, 6, 3)));
        Assert.Equal(0.0, FilamentMutual.Between(          // vertical barrel vs planar trace
            Mm(0, 0, 0), Mm(10, 0, 0), Mm(12, 0, 0), Mm(12, 0, -1.6)));
    }

    [Fact]
    public void GeneralSkew_MatchesOracle()
    {
        var a1 = Mm(0, 0, 0);
        var a2 = Mm(10, 0, 0);
        var b1 = Mm(2, 3, 1);
        var b2 = Mm(9, 8, 4);
        double kernel = FilamentMutual.Between(a1, a2, b1, b2);
        double oracle = NeumannOracle(a1, a2, b1, b2);
        Assert.NotEqual(0.0, kernel);
        Assert.Equal(oracle, kernel, Math.Abs(oracle) * 1e-8);
    }

    [Fact]
    public void CoplanarOblique_MatchesOracle()
    {
        // d = 0 exercises the omitted-Ω path of the skew branch.
        var a1 = Mm(0, 0, 0);
        var a2 = Mm(10, 0, 0);
        var b1 = Mm(12, 1, 0);
        var b2 = Mm(18, 6, 0);
        double kernel = FilamentMutual.Between(a1, a2, b1, b2);
        Assert.Equal(NeumannOracle(a1, a2, b1, b2), kernel, Math.Abs(kernel) * 1e-8);
    }

    [Fact]
    public void CornerTouchingChainPair_MatchesGroverMeetingPointForm()
    {
        // b1 = a2 (a chain corner) at 45°. Grover's meeting-point special case:
        // M = (µ₀/2π)·cosε·[m·atanh(l/(m+R)) + l·atanh(m/(l+R))], R = |a1−b2|.
        var a1 = Mm(0, 0, 0);
        var a2 = Mm(10, 0, 0);
        var b1 = Mm(10, 0, 0);
        var b2 = Mm(17, 7, 0);
        double l = (a2 - a1).Length, m = (b2 - b1).Length, r = (b2 - a1).Length;
        double cos = Vector3D.Dot(a2 - a1, b2 - b1) / (l * m);
        double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));
        double expected = 2e-7 * cos * (m * Atanh(l / (m + r)) + l * Atanh(m / (l + r)));

        double kernel = FilamentMutual.Between(a1, a2, b1, b2);
        Assert.Equal(expected, kernel, Math.Abs(expected) * 1e-10);
        // The oracle's fixed grid resolves the integrable corner singularity to ~1e-3.
        Assert.Equal(NeumannOracle(a1, a2, b1, b2), kernel, Math.Abs(expected) * 1e-3);
    }

    [Fact]
    public void NearParallel_SkewBranchStaysStable_AndParallelBranchTakesOver()
    {
        // ε = 1e-4 rad is just above the parallel routing threshold → skew branch.
        const double l = 10e-3, d = 2e-3, epsilon = 1e-4;
        var a1 = new Vector3D(0, 0, 0);
        var a2 = new Vector3D(l, 0, 0);
        var b1 = new Vector3D(0, d, 0);
        var b2 = new Vector3D(l * Math.Cos(epsilon), d + l * Math.Sin(epsilon), 0);
        double skew = FilamentMutual.Between(a1, a2, b1, b2);
        Assert.Equal(NeumannOracle(a1, a2, b1, b2), skew, Math.Abs(skew) * 1e-6);

        // ε = 1e-5 rad is below the threshold → parallel branch, which flattens the tilt
        // and takes an O(ε·l/d) relative error — here ≈ 1e-5·5 — instead of routing the
        // near-singular geometry through the skew branch's 1/sin²ε intermediates. Real
        // traces are either exactly parallel (FP noise, ε ≈ 0) or degrees apart, so the
        // flattening bound only ever applies to noise-level tilts.
        var b2Near = new Vector3D(l * Math.Cos(1e-5), d + l * Math.Sin(1e-5), 0);
        double nearParallel = FilamentMutual.Between(a1, a2, b1, b2Near);
        Assert.Equal(NeumannOracle(a1, a2, b1, b2Near), nearParallel, Math.Abs(nearParallel) * 1e-4);
    }

    [Fact]
    public void Symmetry_AndDeterminism()
    {
        var a1 = Mm(0, 0, 0);
        var a2 = Mm(10, 0, 0);
        var b1 = Mm(2, 3, 1);
        var b2 = Mm(9, 8, 4);
        double ab = FilamentMutual.Between(a1, a2, b1, b2);
        double ba = FilamentMutual.Between(b1, b2, a1, a2);
        // Grover's terms swap pairwise under A↔B: identical value, different FP order.
        Assert.Equal(ab, ba, Math.Abs(ab) * 1e-13);
        Assert.Equal(ab, FilamentMutual.Between(a1, a2, b1, b2));   // bitwise repeatable
    }

    // ---------------- round-wire / tube self-inductance ----------------

    [Fact]
    public void RoundProfiles_MatchTheLogAsymptotes_AndStayPositiveWhenStubby()
    {
        // Long wire, l/r = 1000: the exact GMD form must meet the classic asymptotes
        // ln(2l/r) − ¾ (solid) and ln(2l/r) − 1 (thin tube) — and their difference is
        // exactly (µ₀/2π)·l/4 in the asymptote (the e^(−¼) GMD ratio).
        const double l = 0.1, r = 1e-4;
        double wire = PartialInductance.RoundWireSelfInductance(l, r);
        double tube = PartialInductance.RoundTubeSelfInductance(l, r);
        Assert.Equal(2e-7 * l * (Math.Log(2 * l / r) - 0.75), wire, wire * 3e-3);
        Assert.Equal(2e-7 * l * (Math.Log(2 * l / r) - 1.0), tube, tube * 3e-3);
        Assert.Equal(2e-7 * l * 0.25, wire - tube, 2e-7 * l * 0.25 * 1e-2);

        // A via barrel between adjacent layers is stubbier than the log asymptote
        // tolerates (it would go NEGATIVE at l < e·r/2) — the exact form must not.
        double stubby = PartialInductance.RoundTubeSelfInductance(0.2e-3, 0.15e-3);
        Assert.True(stubby > 0);
        Assert.True(2e-7 * 0.2e-3 * (Math.Log(2 * 0.2e-3 / 0.15e-3) - 1.0) < stubby,
            "The log asymptote under-shoots (goes negative) exactly where the exact form must not.");

        Assert.Throws<ArgumentOutOfRangeException>(() => PartialInductance.RoundWireSelfInductance(0, r));
        Assert.Throws<ArgumentOutOfRangeException>(() => PartialInductance.RoundTubeSelfInductance(l, -1));
    }
}
