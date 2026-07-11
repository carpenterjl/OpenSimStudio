using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>Monomial-exactness oracle for the all-positive Dunavant rules: every rule
/// must integrate x^a·y^b exactly (∫ over the reference triangle = a!·b!/(a+b+2)!) for
/// all monomials up to its degree.</summary>
public class TriangleQuadratureTests
{
    private static double Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Rule_IntegratesEveryMonomialUpToItsDegree_Exactly(int degree)
    {
        var (l1, l2, _, w) = TriangleQuadrature.Rule(degree);
        // Reference triangle (0,0)-(1,0)-(0,1): x = L1, y = L2 (area = 1/2; the rule's
        // weights sum to 1 and integrate against dS/area).
        for (int a = 0; a + 0 <= degree; a++)
            for (int b = 0; a + b <= degree; b++)
            {
                double sum = 0;
                for (int i = 0; i < w.Length; i++)
                    sum += w[i] * Math.Pow(l1[i], a) * Math.Pow(l2[i], b);
                double exact = Factorial(a) * Factorial(b) / Factorial(a + b + 2) * 2;
                Assert.True(Math.Abs(sum - exact) <= 1e-14 * Math.Max(1, Math.Abs(exact)),
                    $"degree-{degree} rule, x^{a}·y^{b}: {sum} vs exact {exact}");
            }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Rule_HasAllPositiveWeights_SummingToOne(int degree)
    {
        var (_, _, _, w) = TriangleQuadrature.Rule(degree);
        Assert.All(w, weight => Assert.True(weight > 0, "negative quadrature weight"));
        Assert.Equal(1.0, w.Sum(), 1e-14);
    }
}

/// <summary>
/// The Wilton–Rao analytic potentials, gated by two INDEPENDENT oracles: in-plane
/// observations (the dominant coplanar-patch case, and the singular one) use a polar
/// ray-sweep — I0 = Σ_edges ∫ ρ_edge(θ) dθ and Iρ = Σ ∫ û(θ)·ρ²/2 dθ, with ρ from
/// geometric ray/line intersection (no shared algebra with the edge-sum closed forms);
/// off-plane observations use plain subdivision quadrature on the then-smooth kernel.
/// </summary>
public class TrianglePotentialTests
{
    // A deliberately non-axis-aligned triangle so nothing is accidentally zero.
    private static readonly Vector3D A = new(0.02, 0.01, 0.005);
    private static readonly Vector3D B = new(0.11, 0.03, 0.025);
    private static readonly Vector3D C = new(0.04, 0.12, 0.041);

    private static (Vector3D E1, Vector3D E2, Vector3D N) PlaneBasis()
    {
        var n = Vector3D.Cross(B - A, C - A).Normalized();
        var e1 = (B - A).Normalized();
        var e2 = Vector3D.Cross(n, e1);
        return (e1, e2, n);
    }

    private static (double X, double Y) To2D(Vector3D p, Vector3D e1, Vector3D e2) =>
        (Vector3D.Dot(p - A, e1), Vector3D.Dot(p - A, e2));

    /// <summary>The in-plane ray-sweep oracle (see class remarks).</summary>
    private static (double I0, double Ix, double Iy) OracleInPlane(
        (double X, double Y)[] triangle2D, (double X, double Y) o)
    {
        double i0 = 0, ix = 0, iy = 0;
        for (int e = 0; e < 3; e++)
        {
            var w1 = triangle2D[e];
            var w2 = triangle2D[(e + 1) % 3];
            double d1X = w1.X - o.X, d1Y = w1.Y - o.Y;
            double d2X = w2.X - o.X, d2Y = w2.Y - o.Y;
            double r1 = Math.Sqrt(d1X * d1X + d1Y * d1Y);
            double r2 = Math.Sqrt(d2X * d2X + d2Y * d2Y);
            if (r1 < 1e-12 || r2 < 1e-12) continue;   // degenerate fan at a vertex → 0

            double theta1 = Math.Atan2(d1Y, d1X);
            double theta2 = Math.Atan2(d2Y, d2X);
            double delta = theta2 - theta1;
            while (delta > Math.PI) delta -= 2 * Math.PI;
            while (delta < -Math.PI) delta += 2 * Math.PI;
            if (Math.Abs(delta) < 1e-15) continue;

            double edgeX = w2.X - w1.X, edgeY = w2.Y - w1.Y;
            var (nodes, weights) = GaussLegendre.Rule(64, theta1, theta1 + delta);
            for (int i = 0; i < nodes.Length; i++)
            {
                double ux = Math.Cos(nodes[i]), uy = Math.Sin(nodes[i]);
                // Ray o + t·u meets the edge LINE at t = cross(w1−o, ê)/cross(u, ê).
                double crossUe = ux * edgeY - uy * edgeX;
                double rho = (d1X * edgeY - d1Y * edgeX) / crossUe;
                i0 += weights[i] * rho;
                ix += weights[i] * ux * rho * rho / 2;
                iy += weights[i] * uy * rho * rho / 2;
            }
        }
        return (i0, ix, iy);
    }

    private static void AssertInPlaneCase(Vector3D observation, double relTol = 1e-8)
    {
        var (e1, e2, _) = PlaneBasis();
        var tri2D = new[] { To2D(A, e1, e2), To2D(B, e1, e2), To2D(C, e1, e2) };
        var o2D = To2D(observation, e1, e2);

        var oracle = OracleInPlane(tri2D, o2D);
        var (i0, iRho, _) = TrianglePotentials.Integrals(A, B, C, observation);
        double iRhoX = Vector3D.Dot(iRho, e1);
        double iRhoY = Vector3D.Dot(iRho, e2);

        double scale = Math.Abs(oracle.I0);
        Assert.True(Math.Abs(i0 - oracle.I0) <= relTol * scale,
            $"I0 {i0} vs oracle {oracle.I0} (rel {Math.Abs(i0 - oracle.I0) / scale:g2})");
        double vecScale = Math.Max(Math.Sqrt(oracle.Ix * oracle.Ix + oracle.Iy * oracle.Iy), 1e-30);
        Assert.True(Math.Abs(iRhoX - oracle.Ix) <= relTol * vecScale,
            $"Iρ·e1 {iRhoX} vs oracle {oracle.Ix} (rel {Math.Abs(iRhoX - oracle.Ix) / vecScale:g2})");
        Assert.True(Math.Abs(iRhoY - oracle.Iy) <= relTol * vecScale,
            $"Iρ·e2 {iRhoY} vs oracle {oracle.Iy} (rel {Math.Abs(iRhoY - oracle.Iy) / vecScale:g2})");
    }

    [Fact]
    public void Potentials_AtAVertex_MatchTheRaySweepOracle() => AssertInPlaneCase(A);

    [Fact]
    public void Potentials_AtAnEdgeMidpoint_MatchTheRaySweepOracle() =>
        AssertInPlaneCase((A + B) / 2);

    [Fact]
    public void Potentials_AtTheCentroid_MatchTheRaySweepOracle() =>
        AssertInPlaneCase((A + B + C) / 3);

    [Fact]
    public void Potentials_InPlaneJustOutside_MatchTheRaySweepOracle()
    {
        // Reflect the centroid across edge AB (stays exactly in the plane).
        var centroid = (A + B + C) / 3;
        var s = (B - A).Normalized();
        var foot = A + s * Vector3D.Dot(centroid - A, s);
        AssertInPlaneCase(foot + (foot - centroid) * 0.8);
    }

    [Fact]
    public void Potentials_OffPlane_MatchTheSubdivisionOracle()
    {
        var (_, _, n) = PlaneBasis();
        var centroid = (A + B + C) / 3;
        double size = (B - A).Length;
        var observation = centroid + n * (0.37 * size) + (B - A) * 0.11;

        // The kernel is smooth off-plane: 4^5 uniform sub-triangles × degree-6 rule.
        var (l1, l2, l3, w) = TriangleQuadrature.Rule(6);
        double i0 = 0;
        var iVec = new Vector3D(0, 0, 0);
        int levels = 32;   // 32×32 barycentric grid of sub-triangles
        var (_, _, projection) = TrianglePotentials.Integrals(A, B, C, observation);
        void Integrate(Vector3D p, Vector3D q, Vector3D r)
        {
            double area = Vector3D.Cross(q - p, r - p).Length / 2;
            for (int i = 0; i < w.Length; i++)
            {
                var point = p * l1[i] + q * l2[i] + r * l3[i];
                double distance = (point - observation).Length;
                i0 += w[i] * area / distance;
                iVec += (point - projection) * (w[i] * area / distance);
            }
        }
        for (int i = 0; i < levels; i++)
            for (int j = 0; j < levels - i; j++)
            {
                // The standard barycentric lattice: one upward sub-triangle per cell,
                // plus a downward one where the cell isn't on the hypotenuse.
                Integrate(Corner(i, j), Corner(i + 1, j), Corner(i, j + 1));
                if (j < levels - i - 1)
                    Integrate(Corner(i + 1, j), Corner(i + 1, j + 1), Corner(i, j + 1));
            }
        Vector3D Corner(int i, int j) =>
            A + (B - A) * ((double)i / levels) + (C - A) * ((double)j / levels);

        var (analytic0, analyticRho, _) = TrianglePotentials.Integrals(A, B, C, observation);
        Assert.True(Math.Abs(analytic0 - i0) <= 1e-8 * Math.Abs(i0),
            $"I0 {analytic0} vs oracle {i0}");
        Assert.True((analyticRho - iVec).Length <= 1e-8 * iVec.Length,
            $"Iρ {analyticRho} vs oracle {iVec} (rel {(analyticRho - iVec).Length / iVec.Length:g2})");
    }
}

/// <summary>
/// The RWG solver, gated by (1) an independent kernel-moment oracle per singular
/// regime (the oracle's outer panelling refines toward the singular set several levels
/// DEEPER than the shipping code and validates its own convergence between depths),
/// (2) the DISCRETE identity strip-monopole ≡ ½ strip-dipole (the surface image sign),
/// and (3) classic strip physics with the power-balance hard gates.
/// </summary>
public class SurfaceMomSolverTests
{
    private const double Frequency = 300e6;
    private static readonly double Lambda = 299_792_458.0 / Frequency;

    // ------------------------------------------------------------------
    // Kernel-moment oracle
    // ------------------------------------------------------------------

    private static SurfaceMomSolver.SurfaceMoments OracleMoments(
        (Vector3D A, Vector3D B, Vector3D C) p, (Vector3D A, Vector3D B, Vector3D C) q,
        double k, int depth)
    {
        var moments = new SurfaceMomSolver.SurfaceMoments();

        // Q's affine barycentric data (analytic inner — TrianglePotentials is itself
        // oracle-gated above, so it is a legitimate building block here, like the wire
        // oracle's numeric overlap weights).
        var nQ = Vector3D.Cross(q.B - q.A, q.C - q.A);
        double twoAreaQ = nQ.Length;
        var nHatQ = nQ / twoAreaQ;
        var gradQ = new[]
        {
            Vector3D.Cross(nHatQ, q.C - q.B) / twoAreaQ,
            Vector3D.Cross(nHatQ, q.A - q.C) / twoAreaQ,
            Vector3D.Cross(nHatQ, q.B - q.A) / twoAreaQ
        };
        var qVerts = new[] { q.A, q.B, q.C };
        var nP = Vector3D.Cross(p.B - p.A, p.C - p.A);
        double twoAreaP = nP.Length;
        var nHatP = nP / twoAreaP;
        var gradP = new[]
        {
            Vector3D.Cross(nHatP, p.C - p.B) / twoAreaP,
            Vector3D.Cross(nHatP, p.A - p.C) / twoAreaP,
            Vector3D.Cross(nHatP, p.B - p.A) / twoAreaP
        };
        var pVerts = new[] { p.A, p.B, p.C };

        // Distance from a point to triangle Q — the singular set of the
        // inner-integrated static outer integrand.
        double DistanceToQ(Vector3D r)
        {
            double best = double.MaxValue;
            foreach (var (a, b) in new[] { (q.A, q.B), (q.B, q.C), (q.C, q.A) })
            {
                var d = b - a;
                double t = Math.Clamp(Vector3D.Dot(r - a, d) / d.LengthSquared, 0, 1);
                best = Math.Min(best, (r - (a + d * t)).Length);
            }
            double h = Vector3D.Dot(r - q.A, nHatQ);
            var projection = r - nHatQ * h;
            if (SameSide(projection, q.A, q.B, q.C) && SameSide(projection, q.B, q.C, q.A)
                && SameSide(projection, q.C, q.A, q.B))
                best = Math.Min(best, Math.Abs(h));
            return best;

            static bool SameSide(Vector3D x, Vector3D a, Vector3D b, Vector3D c)
            {
                var cross1 = Vector3D.Cross(b - a, x - a);
                var cross2 = Vector3D.Cross(b - a, c - a);
                return Vector3D.Dot(cross1, cross2) >= 0;
            }
        }

        var (l1, l2, l3, w) = TriangleQuadrature.Rule(6);
        void IntegrateStatic((Vector3D A, Vector3D B, Vector3D C) panel, int level)
        {
            double size = Math.Max((panel.B - panel.A).Length,
                Math.Max((panel.C - panel.B).Length, (panel.A - panel.C).Length));
            var centroid = (panel.A + panel.B + panel.C) / 3;
            if (level < depth && DistanceToQ(centroid) < 1.5 * size)
            {
                foreach (var sub in Subdivide(panel)) IntegrateStatic(sub, level + 1);
                return;
            }
            double area = Vector3D.Cross(panel.B - panel.A, panel.C - panel.A).Length / 2;
            for (int i = 0; i < w.Length; i++)
            {
                var r = panel.A * l1[i] + panel.B * l2[i] + panel.C * l3[i];
                var (i0, iRho, projection) = TrianglePotentials.Integrals(q.A, q.B, q.C, r);
                double weight = w[i] * area;
                for (int a = 0; a < 3; a++)
                {
                    double lambdaA = 1 + Vector3D.Dot(gradP[a], r - pVerts[a]);
                    for (int b = 0; b < 3; b++)
                    {
                        double lambdaAtProjection = 1 + Vector3D.Dot(gradQ[b], projection - qVerts[b]);
                        moments[a, b] += weight * lambdaA *
                            (lambdaAtProjection * i0 + Vector3D.Dot(gradQ[b], iRho));
                    }
                }
            }
        }
        IntegrateStatic(p, 0);

        // Smooth remainder — brute force over 64×64 uniform sub-triangle pairs (one
        // level deeper than the shipping code's 16, so the comparison isn't circular).
        var subP = Subdivide(p).SelectMany(Subdivide).SelectMany(Subdivide).ToArray();
        var subQ = Subdivide(q).SelectMany(Subdivide).SelectMany(Subdivide).ToArray();
        var (s1, s2, s3, sw) = TriangleQuadrature.Rule(5);
        foreach (var tp in subP)
        {
            double areaTp = Vector3D.Cross(tp.B - tp.A, tp.C - tp.A).Length / 2;
            foreach (var tq in subQ)
            {
                double areaTq = Vector3D.Cross(tq.B - tq.A, tq.C - tq.A).Length / 2;
                for (int i = 0; i < sw.Length; i++)
                {
                    var r = tp.A * s1[i] + tp.B * s2[i] + tp.C * s3[i];
                    for (int j = 0; j < sw.Length; j++)
                    {
                        var rPrime = tq.A * s1[j] + tq.B * s2[j] + tq.C * s3[j];
                        double distance = (r - rPrime).Length;
                        System.Numerics.Complex gs = distance == 0
                            ? new System.Numerics.Complex(0, -k)
                            : (System.Numerics.Complex.Exp(new System.Numerics.Complex(0, -k * distance)) - 1) / distance;
                        var weight = sw[i] * sw[j] * areaTp * areaTq * gs;
                        for (int a = 0; a < 3; a++)
                        {
                            double lambdaA = 1 + Vector3D.Dot(gradP[a], r - pVerts[a]);
                            for (int b = 0; b < 3; b++)
                            {
                                double lambdaB = 1 + Vector3D.Dot(gradQ[b], rPrime - qVerts[b]);
                                moments[a, b] += weight * lambdaA * lambdaB;
                            }
                        }
                    }
                }
            }
        }
        return moments;
    }

    private static IEnumerable<(Vector3D A, Vector3D B, Vector3D C)> Subdivide(
        (Vector3D A, Vector3D B, Vector3D C) t)
    {
        var mab = (t.A + t.B) / 2;
        var mbc = (t.B + t.C) / 2;
        var mca = (t.C + t.A) / 2;
        yield return (t.A, mab, mca);
        yield return (mab, t.B, mbc);
        yield return (mca, mbc, t.C);
        yield return (mab, mbc, mca);
    }

    private static void AssertMomentsMatch(SurfaceMomSolver.SurfaceMoments actual,
        SurfaceMomSolver.SurfaceMoments oracle, double relativeTolerance)
    {
        double scale = 0;
        for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
                scale = Math.Max(scale, oracle[a, b].Magnitude);
        for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
                Assert.True((actual[a, b] - oracle[a, b]).Magnitude <= relativeTolerance * scale,
                    $"M[{a},{b}] = {actual[a, b]} vs oracle {oracle[a, b]} " +
                    $"(rel {(actual[a, b] - oracle[a, b]).Magnitude / scale:g2})");
    }

    private static void AssertOracleGate((Vector3D A, Vector3D B, Vector3D C) p,
        (Vector3D A, Vector3D B, Vector3D C) q, double tolerance)
    {
        double k = 2 * Math.PI / Lambda;
        // The oracle validates its own convergence between refinement depths first.
        var coarse = OracleMoments(p, q, k, depth: 8);
        var oracle = OracleMoments(p, q, k, depth: 10);
        AssertMomentsMatch(coarse, oracle, 2e-7);

        var actual = SurfaceMomSolver.GeometricPairMoments(p, q, k);
        AssertMomentsMatch(actual, oracle, tolerance);
    }

    private static readonly Vector3D T0 = new(0, 0, 0);

    [Fact]
    public void SelfMoments_MatchTheOracle()
    {
        // 1e-5 (measured 2.6e-6, ×4 margin), not the 1e-6 of the other regimes: the
        // SELF static outer converges only ~2^-depth against its boundary-log edges and
        // the smooth remainder ~h³ against the R kink — the wire solver's exact SELF
        // came from a 1D analytic collapse that has no 2D counterpart. The physics
        // gates (power balance 2%, cross-solver R) carry the end-to-end precision.
        double s = Lambda / 20;
        AssertOracleGate(
            (T0, new Vector3D(s, 0, 0), new Vector3D(0.3 * s, 0.9 * s, 0)),
            (T0, new Vector3D(s, 0, 0), new Vector3D(0.3 * s, 0.9 * s, 0)), 1e-5);
    }

    [Fact]
    public void EdgeAdjacentMoments_MatchTheOracle()
    {
        double s = Lambda / 20;
        AssertOracleGate(
            (T0, new Vector3D(s, 0, 0), new Vector3D(0.4 * s, 0.8 * s, 0)),
            (new Vector3D(s, 0, 0), T0, new Vector3D(0.6 * s, -0.9 * s, 0)), 1e-6);
    }

    [Fact]
    public void VertexAdjacentMoments_MatchTheOracle()
    {
        double s = Lambda / 20;
        AssertOracleGate(
            (T0, new Vector3D(s, 0, 0), new Vector3D(0.4 * s, 0.8 * s, 0)),
            (T0, new Vector3D(-s, -0.1 * s, 0), new Vector3D(-0.4 * s, -0.9 * s, 0)), 1e-6);
    }

    [Fact]
    public void NearCoplanarMoments_MatchTheOracle()
    {
        // The regime that dominates patch metal: separated by half a diameter, coplanar.
        double s = Lambda / 20;
        AssertOracleGate(
            (T0, new Vector3D(s, 0, 0), new Vector3D(0.4 * s, 0.8 * s, 0)),
            (new Vector3D(1.5 * s, 0, 0), new Vector3D(2.5 * s, 0.1 * s, 0),
             new Vector3D(1.9 * s, 0.9 * s, 0)), 1e-6);
    }

    [Fact]
    public void FarMoments_MatchTheOracle()
    {
        double s = Lambda / 20;
        AssertOracleGate(
            (T0, new Vector3D(s, 0, 0), new Vector3D(0.4 * s, 0.8 * s, 0)),
            (new Vector3D(30 * s, 0, 0), new Vector3D(31 * s, 0, 0),
             new Vector3D(30.4 * s, 0.8 * s, 0)), 1e-9);
    }

    // ------------------------------------------------------------------
    // Structure / assembly contracts
    // ------------------------------------------------------------------

    [Fact]
    public void ImpedanceMatrix_IsComplexSymmetric_Bitwise_WithAndWithoutGround()
    {
        double k = 2 * Math.PI / Lambda, omega = 2 * Math.PI * Frequency;
        var plate = SurfaceMeshBuilder.BuildRectangularPlate(
            Lambda / 10, Lambda / 5, Lambda / 20).Structure!;
        var z = SurfaceMomSolver.AssembleImpedanceMatrix(plate, k, omega);
        for (int i = 0; i < plate.BasisCount; i++)
            for (int j = 0; j < plate.BasisCount; j++)
                Assert.Equal(z[i, j], z[j, i]);

        var patch = SurfaceMeshBuilder.BuildPatchOverGround(
            Lambda / 10, Lambda / 5, Lambda / 20, 0, Lambda / 20).Structure!;
        var zg = SurfaceMomSolver.AssembleImpedanceMatrix(patch, k, omega);
        for (int i = 0; i < patch.BasisCount; i++)
            for (int j = 0; j < patch.BasisCount; j++)
                Assert.Equal(zg[i, j], zg[j, i]);
    }

    private static (SurfaceStructure Structure, SurfacePort Port, SurfaceMomSolution Solution)
        SolveStripDipole(double lengthOverLambda = 0.5)
    {
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            Lambda / 100, lengthOverLambda * Lambda, Lambda / 40);
        Assert.NotNull(grid.Structure);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, Frequency, grid.Port!);
        return (grid.Structure!, grid.Port!, solution);
    }

    [Fact]
    public void StripDipoleZin_IsPinned_ThroughTheKernelSeam()
    {
        // BITWISE regression pin, captured before the Stage C kernel seam landed: the
        // free-space kernel routed through IPotentialKernel must produce the SAME
        // doubles as the pre-seam direct code — any drift here means the seam changed
        // arithmetic, not just structure. If this ever moves for a understood reason
        // (e.g. a deliberate quadrature change), re-derive the pin from that change's
        // own verification, never by copying the new output blindly.
        var (_, _, solution) = SolveStripDipole();
        Assert.Equal(89.11838474782336, solution.InputImpedance.Real);
        Assert.Equal(43.90982417156413, solution.InputImpedance.Imaginary);
    }

    [Fact]
    public void Solve_IsBitwiseDeterministic()
    {
        var (_, _, first) = SolveStripDipole();
        var (_, _, second) = SolveStripDipole();
        Assert.Equal(first.InputImpedance, second.InputImpedance);
        for (int i = 0; i < first.EdgeCurrents.Length; i++)
            Assert.Equal(first.EdgeCurrents[i], second.EdgeCurrents[i]);
    }

    // ------------------------------------------------------------------
    // Strip physics
    // ------------------------------------------------------------------

    [Fact]
    public void StripDipole_MatchesTheEquivalentWireDipole()
    {
        // The r = w/4 strip↔wire equivalence the trace adapter already uses: the two
        // SOLVERS must agree on R (X carries each feed model's own artifact — compared
        // loosely, documented, not tightened). NOTE the absolute band sits ABOVE the
        // classic thin-dipole 73 Ω: the equivalent wire radius is λ/400 (w = λ/100),
        // and at that thickness the wire solver itself measures R = 91.5 Ω at exactly
        // 0.5λ — thickness moves the R(length) curve, and both solvers move together
        // (measured ratio 0.974 at λ/40 elements, converging to 0.987 refined).
        var (_, _, strip) = SolveStripDipole();

        var wireGrid = WireGridBuilder.Build(
            new[]
            {
                new WireSegment(new Vector3D(0, -0.25 * Lambda, 0),
                    new Vector3D(0, 0.25 * Lambda, 0), Lambda / 400)
            },
            maxElementLength: 0.5 * Lambda / 40);
        var wire = wireGrid.Structure!;
        var wireSolution = new ThinWireMomSolver().Solve(wire, Frequency, wire.NearestBasis(Vector3D.Zero));

        Assert.InRange(strip.InputImpedance.Real, 82, 96);
        Assert.InRange(strip.InputImpedance.Real / wireSolution.InputImpedance.Real, 0.92, 1.08);
        Assert.True(Math.Abs(strip.InputImpedance.Imaginary - wireSolution.InputImpedance.Imaginary) < 15,
            $"X_strip = {strip.InputImpedance.Imaginary:g4} vs X_wire = {wireSolution.InputImpedance.Imaginary:g4}");
    }

    [Fact]
    public void StripDipoleResonance_IsBracketedNearTheClassicLength()
    {
        var below = SolveStripDipole(0.45).Solution;
        var above = SolveStripDipole(0.50).Solution;
        Assert.True(below.InputImpedance.Imaginary < 0,
            $"X(0.45λ) = {below.InputImpedance.Imaginary:g4} should be capacitive");
        Assert.True(above.InputImpedance.Imaginary > 0,
            $"X(0.50λ) = {above.InputImpedance.Imaginary:g4} should be inductive");
    }

    [Fact]
    public void StripDipole_PowerBalance_AndDirectivity()
    {
        // THE Stage-B hard gate: radiated power from the sphere quadrature must equal
        // the circuit input power ½·Re(V·I*) — one identity across the RWG fill, the
        // solve, the surface radiation integral, and the sphere weights.
        var (structure, _, solution) = SolveStripDipole();
        double inputPower = 0.5 * (System.Numerics.Complex.One / solution.InputImpedance).Real;
        var pattern = SurfaceFarFieldEvaluator.Compute(structure, solution);
        Assert.InRange(pattern.TotalRadiatedPowerWatts / inputPower, 0.98, 1.02);
        Assert.InRange(pattern.MaxDirectivity, 0.98 * 1.641, 1.02 * 1.641);
    }
}
