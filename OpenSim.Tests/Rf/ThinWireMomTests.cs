using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using Xunit;

namespace OpenSim.Tests.Rf;

public class GaussLegendreTests
{
    [Fact]
    public void RuleIntegratesMaximalDegreePolynomialExactly()
    {
        // n-point Gauss–Legendre is exact through degree 2n−1: ∫₀¹ x¹⁵ dx = 1/16.
        var (nodes, weights) = GaussLegendre.Rule(8, 0, 1);
        double sum = 0;
        for (int i = 0; i < nodes.Length; i++)
            sum += weights[i] * Math.Pow(nodes[i], 15);
        Assert.Equal(1.0 / 16, sum, 1e-14);
    }

    [Fact]
    public void WeightsSumToTheIntervalLength()
    {
        foreach (int n in new[] { 1, 2, 5, 12, 16, 32 })
        {
            var (_, weights) = GaussLegendre.Rule(n, -1, 1);
            Assert.Equal(2.0, weights.Sum(), 1e-13);
        }
    }
}

/// <summary>
/// The moment-method engine, gated by (1) an independent quadrature oracle for the
/// kernel moments — the self/near singular handling is the highest-risk code — and
/// (2) the classic dipole physics values.
/// </summary>
public class ThinWireMomTests
{
    private const double Frequency = 300e6;
    private static readonly double Lambda = 299_792_458.0 / Frequency;

    // ------------------------------------------------------------------
    // Oracle: brute-force kernel moments with geometric panel refinement.
    // Independent of the shipping evaluation: the self case reduces to 1D by Fubini on
    // the difference kernel but evaluates the overlap weights by quadrature (not the
    // closed-form polynomials), and near/far cases integrate the raw 2D kernel over
    // panels that shrink toward the close-approach corner.
    // ------------------------------------------------------------------

    private static Complex Kernel(double r, double k) => Complex.Exp(new Complex(0, -k * r)) / r;

    private static ThinWireMomSolver.Moments OracleSelf(double length, double c, double k)
    {
        // M_ab = ∫₀ᴸ [Q_ab(t) + Q_ba(t)]·g(√(t²+c²)) dt, Q by numeric quadrature.
        double QNumeric(int a, int b, double t)
        {
            var (nodes, weights) = GaussLegendre.Rule(8, 0, length - t);
            double sum = 0;
            for (int i = 0; i < nodes.Length; i++)
                sum += weights[i] * Math.Pow(nodes[i] / length, a) * Math.Pow((nodes[i] + t) / length, b);
            return sum;
        }

        Complex M(int a, int b)
        {
            Complex total = Complex.Zero;
            double edge = 0, first = c / 2;
            while (edge < length)
            {
                double next = edge == 0 ? first : Math.Min(edge * 2, length);
                var (nodes, weights) = GaussLegendre.Rule(16, edge, next);
                for (int i = 0; i < nodes.Length; i++)
                {
                    double t = nodes[i];
                    total += weights[i] * (QNumeric(a, b, t) + QNumeric(b, a, t))
                             * Kernel(Math.Sqrt(t * t + c * c), k);
                }
                edge = next;
            }
            return total;
        }

        return new ThinWireMomSolver.Moments(M(0, 0), M(0, 1), M(1, 0), M(1, 1));
    }

    private static ThinWireMomSolver.Moments Oracle2D(Vector3D p0, Vector3D p1, Vector3D q0, Vector3D q1,
        double c, double k, bool refineTowardsCorner)
    {
        double lengthP = (p1 - p0).Length, lengthQ = (q1 - q0).Length;
        var directionP = (p1 - p0) / lengthP;
        var directionQ = (q1 - q0) / lengthQ;

        List<(double A, double B)> Panels(double length)
        {
            var panels = new List<(double, double)>();
            if (!refineTowardsCorner)
            {
                for (int i = 0; i < 4; i++) panels.Add((length * i / 4, length * (i + 1) / 4));
                return panels;
            }
            double edge = 0, first = c / 2;
            while (edge < length)
            {
                double next = edge == 0 ? first : Math.Min(edge * 2, length);
                panels.Add((edge, next));
                edge = next;
            }
            return panels;
        }

        // For corner refinement, parametrize each element FROM the shared node.
        bool pFromStart = true, qFromStart = true;
        if (refineTowardsCorner)
        {
            double best = double.MaxValue;
            foreach (var (pf, pPoint) in new[] { (true, p0), (false, p1) })
                foreach (var (qf, qPoint) in new[] { (true, q0), (false, q1) })
                {
                    double gap = (pPoint - qPoint).Length;
                    if (gap < best) { best = gap; pFromStart = pf; qFromStart = qf; }
                }
        }
        Vector3D PointP(double s) => pFromStart ? p0 + directionP * s : p1 - directionP * s;
        Vector3D PointQ(double u) => qFromStart ? q0 + directionQ * u : q1 - directionQ * u;
        double RiseP(double s) => (pFromStart ? s : lengthP - s) / lengthP;
        double RiseQ(double u) => (qFromStart ? u : lengthQ - u) / lengthQ;

        Complex m00 = Complex.Zero, m01 = Complex.Zero, m10 = Complex.Zero, m11 = Complex.Zero;
        foreach (var (pa, pb) in Panels(lengthP))
        {
            var (sNodes, sWeights) = GaussLegendre.Rule(8, pa, pb);
            foreach (var (qa, qb) in Panels(lengthQ))
            {
                var (uNodes, uWeights) = GaussLegendre.Rule(8, qa, qb);
                for (int i = 0; i < sNodes.Length; i++)
                    for (int j = 0; j < uNodes.Length; j++)
                    {
                        double r = Math.Sqrt((PointP(sNodes[i]) - PointQ(uNodes[j])).LengthSquared + c * c);
                        Complex w = sWeights[i] * uWeights[j] * Kernel(r, k);
                        m00 += w;
                        m01 += w * RiseQ(uNodes[j]);
                        m10 += w * RiseP(sNodes[i]);
                        m11 += w * RiseP(sNodes[i]) * RiseQ(uNodes[j]);
                    }
            }
        }
        return new ThinWireMomSolver.Moments(m00, m01, m10, m11);
    }

    private static void AssertMomentsMatch(ThinWireMomSolver.Moments actual,
        ThinWireMomSolver.Moments oracle, double relativeTolerance)
    {
        foreach (var (a, o) in new[]
        {
            (actual.M00, oracle.M00), (actual.M01, oracle.M01),
            (actual.M10, oracle.M10), (actual.M11, oracle.M11)
        })
        {
            Assert.True((a - o).Magnitude <= relativeTolerance * o.Magnitude,
                $"moment {a} vs oracle {o} (rel {(a - o).Magnitude / o.Magnitude:g2})");
        }
    }

    private static WireStructure Structure(bool isLoop, double radius, params Vector3D[] nodes) =>
        new(nodes, Enumerable.Repeat(radius, isLoop ? nodes.Length : nodes.Length - 1).ToArray(), isLoop);

    [Fact]
    public void SelfMoments_MatchTheOracle()
    {
        double length = Lambda / 20, radius = Lambda / 2000, k = 2 * Math.PI / Lambda;
        var wire = Structure(false, radius,
            new Vector3D(0, 0, 0), new Vector3D(length, 0, 0), new Vector3D(2 * length, 0, 0));
        var actual = ThinWireMomSolver.PairMoments(wire, 0, 0, k);
        var oracle = OracleSelf(length, radius, k);
        AssertMomentsMatch(actual, oracle, 1e-8);
    }

    [Fact]
    public void AdjacentBentMoments_MatchTheOracle()
    {
        // Two elements meeting at a right angle — the log-like corner case.
        double length = Lambda / 20, radius = Lambda / 2000, k = 2 * Math.PI / Lambda;
        var wire = Structure(false, radius,
            new Vector3D(-length, 0, 0), new Vector3D(0, 0, 0), new Vector3D(0, length, 0));
        var actual = ThinWireMomSolver.PairMoments(wire, 0, 1, k);
        var oracle = Oracle2D(new Vector3D(-length, 0, 0), new Vector3D(0, 0, 0),
            new Vector3D(0, 0, 0), new Vector3D(0, length, 0), radius, k, refineTowardsCorner: true);
        AssertMomentsMatch(actual, oracle, 1e-6);
    }

    [Fact]
    public void AdjacentCollinearMoments_MatchTheOracle()
    {
        // Collinear neighbours — the configuration every straight dipole is made of.
        double length = Lambda / 20, radius = Lambda / 2000, k = 2 * Math.PI / Lambda;
        var wire = Structure(false, radius,
            new Vector3D(0, 0, 0), new Vector3D(length, 0, 0), new Vector3D(2 * length, 0, 0));
        var actual = ThinWireMomSolver.PairMoments(wire, 0, 1, k);
        var oracle = Oracle2D(new Vector3D(0, 0, 0), new Vector3D(length, 0, 0),
            new Vector3D(length, 0, 0), new Vector3D(2 * length, 0, 0), radius, k, refineTowardsCorner: true);
        AssertMomentsMatch(actual, oracle, 1e-6);
    }

    [Fact]
    public void NearButSeparatedMoments_MatchTheOracle()
    {
        // Parallel elements half an element apart (the meander-trace case).
        double length = Lambda / 20, radius = Lambda / 2000, k = 2 * Math.PI / Lambda;
        var wire = Structure(false, radius,
            new Vector3D(0, 0, 0), new Vector3D(length, 0, 0),
            new Vector3D(length, length / 2, 0), new Vector3D(0, length / 2, 0));
        var actual = ThinWireMomSolver.PairMoments(wire, 0, 2, k);
        var oracle = Oracle2D(new Vector3D(0, 0, 0), new Vector3D(length, 0, 0),
            new Vector3D(length, length / 2, 0), new Vector3D(0, length / 2, 0),
            radius, k, refineTowardsCorner: false);
        AssertMomentsMatch(actual, oracle, 1e-6);
    }

    [Fact]
    public void FarMoments_MatchTheOracle()
    {
        double length = Lambda / 20, radius = Lambda / 2000, k = 2 * Math.PI / Lambda;
        var wire = Structure(false, radius,
            new Vector3D(0, 0, 0), new Vector3D(length, 0, 0),
            new Vector3D(length, 10 * length, 0), new Vector3D(0, 10 * length, 0));
        var actual = ThinWireMomSolver.PairMoments(wire, 0, 2, k);
        var oracle = Oracle2D(new Vector3D(0, 0, 0), new Vector3D(length, 0, 0),
            new Vector3D(length, 10 * length, 0), new Vector3D(0, 10 * length, 0),
            radius, k, refineTowardsCorner: false);
        AssertMomentsMatch(actual, oracle, 1e-9);
    }

    // ------------------------------------------------------------------
    // Physics gates
    // ------------------------------------------------------------------

    private static MomSolution SolveDipole(double lengthOverLambda, int elements)
    {
        double length = lengthOverLambda * Lambda;
        var grid = WireGridBuilder.Build(
            new[] { new WireSegment(new Vector3D(0, 0, -length / 2), new Vector3D(0, 0, length / 2), Lambda / 2000) },
            maxElementLength: length / elements);
        Assert.NotNull(grid.Structure);
        int feed = grid.Structure!.NearestBasis(Vector3D.Zero);
        return new ThinWireMomSolver().Solve(grid.Structure, Frequency, feed);
    }

    [Fact]
    public void HalfWaveDipole_InputImpedance_IsInTheClassicBand()
    {
        // The canonical antenna value: a λ/2 dipole of radius λ/2000 has R ≈ 73 Ω and
        // inductive X (the exact +42.5 Ω belongs to the infinitely thin sinusoidal
        // limit; finite radius and the delta-gap feed move X tens of percent while R
        // stays put — hence the asymmetric band).
        var solution = SolveDipole(0.5, elements: 40);
        Assert.InRange(solution.InputImpedance.Real, 70, 85);
        Assert.InRange(solution.InputImpedance.Imaginary, 25, 60);
    }

    [Fact]
    public void DipoleResonance_IsBracketedNearTheClassicLength()
    {
        // Thin dipoles resonate slightly short of λ/2 (≈ 0.475 λ): X must change sign
        // between 0.45 λ and 0.50 λ.
        var below = SolveDipole(0.45, elements: 36);
        var above = SolveDipole(0.50, elements: 40);
        Assert.True(below.InputImpedance.Imaginary < 0,
            $"X(0.45λ) = {below.InputImpedance.Imaginary:g4} should be capacitive");
        Assert.True(above.InputImpedance.Imaginary > 0,
            $"X(0.50λ) = {above.InputImpedance.Imaginary:g4} should be inductive");
    }

    [Fact]
    public void ShortDipole_RadiationResistance_ApproachesTheThinLimit_FromBelow()
    {
        // R_rad = 20π²(l/λ)² = 0.4935 Ω is the INFINITELY-thin limit: at finite
        // thickness the current sags below triangular by O(1/Ω), Ω = 2ln(2l/a), so the
        // MoM value sits BELOW the formula and climbs toward it as the wire thins —
        // measured here: ratio 0.82 at Ω = 10.6, 0.92 at 15.2, 0.95 at 19.8, 0.96 at
        // 24.4 (~1/Ω law). A one-sided band, exactly like the TET4 bending gate.
        double expected = 20 * Math.PI * Math.PI * 0.05 * 0.05;
        var solution = SolveThinDipole(0.05, elements: 20, radius: Lambda / 20000);   // Ω = 15.2
        Assert.InRange(solution.InputImpedance.Real, 0.85 * expected, 1.0 * expected);
        Assert.True(solution.InputImpedance.Imaginary < -1000,
            $"X = {solution.InputImpedance.Imaginary:g4} should be strongly capacitive");

        // The physical consistency check: thinner wire ⇒ closer to the analytic limit.
        var thinner = SolveThinDipole(0.05, elements: 20, radius: Lambda / 200000);   // Ω = 19.8
        Assert.True(thinner.InputImpedance.Real > solution.InputImpedance.Real,
            $"R should rise toward the thin limit: {solution.InputImpedance.Real:g4} → " +
            $"{thinner.InputImpedance.Real:g4}");
        Assert.True(thinner.InputImpedance.Real < expected);
    }

    private static MomSolution SolveThinDipole(double lengthOverLambda, int elements, double radius)
    {
        double length = lengthOverLambda * Lambda;
        var grid = WireGridBuilder.Build(
            new[] { new WireSegment(new Vector3D(0, 0, -length / 2), new Vector3D(0, 0, length / 2), radius) },
            maxElementLength: length / elements);
        Assert.NotNull(grid.Structure);
        return new ThinWireMomSolver().Solve(grid.Structure!,
            Frequency, grid.Structure!.NearestBasis(Vector3D.Zero));
    }


    [Fact]
    public void HalfWaveDipole_CurrentDistribution_IsSymmetricAndPeaksAtTheFeed()
    {
        var solution = SolveDipole(0.5, elements: 40);
        var magnitudes = solution.BasisCurrents.Select(c => c.Magnitude).ToArray();
        int feed = magnitudes.Length / 2;
        // The delta gap slightly depresses the current at its own node (charge piles at
        // the gap), so the numerical peak may sit one node off the feed — a known feed
        // artifact, not an error. The feed still carries essentially the full peak.
        int peak = Array.IndexOf(magnitudes, magnitudes.Max());
        Assert.True(Math.Abs(peak - feed) <= 1,
            $"peak at basis {peak}, feed at {feed} — the maximum must sit at or beside the feed");
        Assert.True(magnitudes[feed] >= 0.98 * magnitudes.Max(),
            $"feed current {magnitudes[feed]:g4} vs peak {magnitudes.Max():g4}");
        for (int i = 0; i < magnitudes.Length; i++)
            Assert.Equal(magnitudes[i], magnitudes[^(i + 1)], magnitudes.Max() * 1e-6);
    }

    [Fact]
    public void ImpedanceMatrix_IsComplexSymmetric_Bitwise()
    {
        // Galerkin + single-evaluation-per-element-pair scatter ⇒ exact symmetry.
        double length = Lambda / 15, radius = Lambda / 2000, k = 2 * Math.PI / Lambda;
        var wire = Structure(false, radius,
            new Vector3D(0, 0, 0), new Vector3D(length, 0, 0), new Vector3D(length, length, 0),
            new Vector3D(length, length, length), new Vector3D(0, length, length));
        var z = ThinWireMomSolver.AssembleImpedanceMatrix(wire, k, 2 * Math.PI * Frequency);
        for (int i = 0; i < wire.BasisCount; i++)
            for (int j = 0; j < wire.BasisCount; j++)
                Assert.Equal(z[i, j], z[j, i]);
    }

    [Fact]
    public void Solve_IsBitwiseDeterministic()
    {
        var first = SolveDipole(0.5, elements: 30);
        var second = SolveDipole(0.5, elements: 30);
        Assert.Equal(first.InputImpedance, second.InputImpedance);
        for (int i = 0; i < first.BasisCurrents.Length; i++)
            Assert.Equal(first.BasisCurrents[i], second.BasisCurrents[i]);
    }

    [Fact]
    public void DipoleResistance_IsStableUnderRefinement()
    {
        // R converges quickly (X carries the delta-gap's known log drift — not asserted).
        var coarse = SolveDipole(0.5, elements: 20);
        var fine = SolveDipole(0.5, elements: 40);
        Assert.True(Math.Abs(fine.InputImpedance.Real - coarse.InputImpedance.Real) < 6,
            $"R moved {coarse.InputImpedance.Real:g4} → {fine.InputImpedance.Real:g4} under refinement");
    }

    // ------------------------------------------------------------------
    // Grid builder contracts
    // ------------------------------------------------------------------

    [Fact]
    public void GridBuilder_RejectsBranchesAndDisconnectedPieces_Typed()
    {
        double r = 1e-3;
        var branch = WireGridBuilder.Build(new[]
        {
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), r),
            new WireSegment(new Vector3D(1, 0, 0), new Vector3D(2, 0, 0), r),
            new WireSegment(new Vector3D(1, 0, 0), new Vector3D(1, 1, 0), r)
        }, 0.2);
        Assert.Null(branch.Structure);
        Assert.Contains("junction", branch.FailureReason);

        var pieces = WireGridBuilder.Build(new[]
        {
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), r),
            new WireSegment(new Vector3D(3, 0, 0), new Vector3D(4, 0, 0), r)
        }, 0.2);
        Assert.Null(pieces.Structure);
        Assert.Contains("disconnected", pieces.FailureReason);
    }

    [Fact]
    public void GridBuilder_DetectsALoop_AndSplitsToTheElementCeiling()
    {
        double r = 1e-3;
        var square = WireGridBuilder.Build(new[]
        {
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), r),
            new WireSegment(new Vector3D(1, 0, 0), new Vector3D(1, 1, 0), r),
            new WireSegment(new Vector3D(1, 1, 0), new Vector3D(0, 1, 0), r),
            new WireSegment(new Vector3D(0, 1, 0), new Vector3D(0, 0, 0), r)
        }, 0.3);
        Assert.NotNull(square.Structure);
        Assert.True(square.Structure!.IsLoop);
        Assert.Equal(16, square.Structure.ElementCount);          // each side split in 4
        Assert.Equal(square.Structure.ElementCount, square.Structure.BasisCount);
        Assert.Equal(4.0, square.Structure.TotalLength(), 1e-12);
    }

    [Fact]
    public void GridBuilder_MergesArcTessellationSlivers()
    {
        // 20 collinear 1 mm chords: without merging these become near-radius elements;
        // with it they fuse toward the ceiling and the total length is preserved.
        double r = 0.4e-3;
        var chords = Enumerable.Range(0, 20)
            .Select(i => new WireSegment(
                new Vector3D(i * 1e-3, 0, 0), new Vector3D((i + 1) * 1e-3, 0, 0), r))
            .ToArray();
        var grid = WireGridBuilder.Build(chords, maxElementLength: 5e-3);
        Assert.NotNull(grid.Structure);
        Assert.True(grid.Structure!.ElementCount < 20,
            $"expected merging, got {grid.Structure.ElementCount} elements");
        Assert.Equal(0.02, grid.Structure.TotalLength(), 1e-12);
    }

    [Fact]
    public void GridBuilder_CapsUnknowns_Actionably()
    {
        var result = WireGridBuilder.Build(
            new[] { new WireSegment(new Vector3D(0, 0, 0), new Vector3D(10, 0, 0), 1e-4) },
            maxElementLength: 1e-3, maxUnknowns: 500);
        Assert.Null(result.Structure);
        Assert.Contains("cap", result.FailureReason);
    }
}
