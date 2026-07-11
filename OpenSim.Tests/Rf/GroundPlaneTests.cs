using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// The infinite-PEC-ground image pass, gated by (1) a sign oracle that rebuilds the
/// image block from explicitly mirrored-and-swapped geometry with an independent
/// brute-force quadrature, (2) the DISCRETE identity Z_monopole ≡ Z_dipole/2 — the
/// symmetric reduction of the dipole system IS the monopole+image system, so any image
/// sign slip anywhere breaks it at machine precision — and (3) the classic monopole /
/// dipole-over-ground physics values with the hemisphere power-balance hard gate.
/// </summary>
public class GroundPlaneTests
{
    private const double Frequency = 300e6;
    private static readonly double Lambda = 299_792_458.0 / Frequency;

    private static WireStructure Structure(double radius, GroundPlane? ground, params Vector3D[] nodes)
    {
        bool startGrounded = ground is not null && nodes[0].Z == ground.SurfaceZ;
        bool endGrounded = ground is not null && nodes[^1].Z == ground.SurfaceZ;
        return new WireStructure(nodes, Enumerable.Repeat(radius, nodes.Length - 1).ToArray(),
            isLoop: false, ground, startGrounded, endGrounded);
    }

    // ------------------------------------------------------------------
    // Sign oracle: Z_image = Z(with ground) − Z(free space) must match an independent
    // composition over the explicitly mirrored-and-swapped image elements, integrated
    // by a brute-force panelled 2D rule.
    // ------------------------------------------------------------------

    private static Complex Kernel(double r, double k) => Complex.Exp(new Complex(0, -k * r)) / r;

    /// <summary>Brute-force kernel moments over two segments given by raw endpoints —
    /// independent of the shipping NearMoments/FarMoments (plain 8-pt Gauss on 4×4 equal
    /// panels; adequate because image pairs here are separated, never singular).</summary>
    private static ThinWireMomSolver.Moments OracleMoments(Vector3D p0, Vector3D p1,
        Vector3D q0, Vector3D q1, double c, double k)
    {
        double lengthP = (p1 - p0).Length, lengthQ = (q1 - q0).Length;
        var directionP = (p1 - p0) / lengthP;
        var directionQ = (q1 - q0) / lengthQ;

        Complex m00 = Complex.Zero, m01 = Complex.Zero, m10 = Complex.Zero, m11 = Complex.Zero;
        for (int panelP = 0; panelP < 4; panelP++)
        {
            var (sNodes, sWeights) = GaussLegendre.Rule(8, lengthP * panelP / 4, lengthP * (panelP + 1) / 4);
            for (int panelQ = 0; panelQ < 4; panelQ++)
            {
                var (uNodes, uWeights) = GaussLegendre.Rule(8, lengthQ * panelQ / 4, lengthQ * (panelQ + 1) / 4);
                for (int i = 0; i < sNodes.Length; i++)
                    for (int j = 0; j < uNodes.Length; j++)
                    {
                        var x = p0 + directionP * sNodes[i];
                        var y = q0 + directionQ * uNodes[j];
                        double r = Math.Sqrt((x - y).LengthSquared + c * c);
                        Complex w = sWeights[i] * uWeights[j] * Kernel(r, k);
                        double riseP = sNodes[i] / lengthP, riseQ = uNodes[j] / lengthQ;
                        m00 += w;
                        m01 += w * riseQ;
                        m10 += w * riseP;
                        m11 += w * riseP * riseQ;
                    }
            }
        }
        return new ThinWireMomSolver.Moments(m00, m01, m10, m11);
    }

    private static Complex CombineRooftop(ThinWireMomSolver.Moments m, bool risingP, bool risingQ) =>
        (risingP, risingQ) switch
        {
            (true, true) => m.M11,
            (true, false) => m.M10 - m.M11,
            (false, true) => m.M01 - m.M11,
            (false, false) => m.M00 - m.M01 - m.M10 + m.M11
        };

    [Fact]
    public void ImageBlock_MatchesTheExplicitMirroredGeometryOracle()
    {
        // A 3-element bent wire (horizontal, horizontal, vertical) at height L/2 —
        // every image pair lands in the NEAR regime, the risky one.
        double length = Lambda / 20, radius = Lambda / 2000;
        double k = 2 * Math.PI / Lambda, omega = 2 * Math.PI * Frequency;
        double h = length / 2;
        var nodes = new[]
        {
            new Vector3D(0, 0, h), new Vector3D(length, 0, h),
            new Vector3D(length, length, h), new Vector3D(length, length, h + length)
        };
        var ground = new GroundPlane(0);
        var free = Structure(radius, null, nodes);
        var grounded = Structure(radius, ground, nodes);

        var zFree = ThinWireMomSolver.AssembleImpedanceMatrix(free, k, omega);
        var zGround = ThinWireMomSolver.AssembleImpedanceMatrix(grounded, k, omega);

        // Independent composition: full ordered double sum over (real element of m,
        // image element of n) with the mirrored-and-swapped geometry made explicit.
        Complex vectorFactor = Complex.ImaginaryOne * omega * RfConstants2.Mu0 / (4 * Math.PI);
        Complex chargeFactor = -Complex.ImaginaryOne / (4 * Math.PI * RfConstants2.Eps0 * omega);
        Vector3D Mirror(Vector3D p) => new(p.X, p.Y, -p.Z);

        int n = free.BasisCount;
        for (int m = 0; m < n; m++)
            for (int s = 0; s < n; s++)
            {
                Complex oracle = Complex.Zero;
                foreach (var (p, risingP) in new[] { (free.RisingElement(m), true), (free.FallingElement(m), false) })
                    foreach (var (q, risingQ) in new[] { (free.RisingElement(s), true), (free.FallingElement(s), false) })
                    {
                        // Image of q: mirrored AND endpoint-swapped; the swap flips the
                        // basis role — that is the whole sign convention under test.
                        var imageStart = Mirror(free.ElementEnd(q));
                        var imageEnd = Mirror(free.ElementStart(q));
                        double c = Math.Sqrt(free.ElementRadii[p] * free.ElementRadii[q]);
                        var moments = OracleMoments(free.ElementStart(p), free.ElementEnd(p),
                            imageStart, imageEnd, c, k);
                        double lengthP = free.ElementLength(p), lengthQ = free.ElementLength(q);
                        double dot = Vector3D.Dot(free.ElementDirection(p),
                            (imageEnd - imageStart) / lengthQ);
                        bool imageRising = !risingQ;
                        Complex vector = vectorFactor * dot * CombineRooftop(moments, risingP, imageRising);
                        double slopeP = (risingP ? 1.0 : -1.0) / lengthP;
                        double slopeQ = (imageRising ? 1.0 : -1.0) / lengthQ;
                        oracle += vector + chargeFactor * slopeP * slopeQ * moments.M00;
                    }

                Complex actual = zGround[m, s] - zFree[m, s];
                Assert.True((actual - oracle).Magnitude <= 1e-6 * oracle.Magnitude,
                    $"Z_image[{m},{s}] = {actual} vs oracle {oracle} " +
                    $"(rel {(actual - oracle).Magnitude / oracle.Magnitude:g2})");
            }
    }

    // ------------------------------------------------------------------
    // The discrete identity: monopole ≡ half dipole
    // ------------------------------------------------------------------

    [Fact]
    public void Monopole_InputImpedance_IsExactlyHalfTheDipoles()
    {
        // Bitwise-matched discretizations: monopole nodes at i·dz above the ground plane
        // at z = 0; dipole nodes at (j−20)·dz. The monopole+image system is the exact
        // symmetric reduction of the dipole system (the image pass reproduces the lower
        // half), so Z_mono = Z_dip/2 up to summation-order roundoff — any image sign
        // error anywhere destroys this at the first digit.
        double height = 0.25 * Lambda, radius = Lambda / 2000;
        double dz = height / 20;
        var monoNodes = Enumerable.Range(0, 21).Select(i => new Vector3D(0, 0, i * dz)).ToArray();
        var dipNodes = Enumerable.Range(0, 41).Select(j => new Vector3D(0, 0, (j - 20) * dz)).ToArray();

        var monopole = Structure(radius, new GroundPlane(0), monoNodes);
        var dipole = Structure(radius, null, dipNodes);
        Assert.True(monopole.StartGrounded);
        Assert.Equal(20, monopole.BasisCount);

        var solver = new ThinWireMomSolver();
        var monoSolution = solver.Solve(monopole, Frequency, monopole.NearestBasis(Vector3D.Zero));
        var dipSolution = solver.Solve(dipole, Frequency, dipole.NearestBasis(Vector3D.Zero));

        Complex half = dipSolution.InputImpedance / 2;
        Assert.True((monoSolution.InputImpedance - half).Magnitude <= 1e-8 * half.Magnitude,
            $"Z_mono = {monoSolution.InputImpedance} vs Z_dip/2 = {half} " +
            $"(rel {(monoSolution.InputImpedance - half).Magnitude / half.Magnitude:g2})");
    }

    // ------------------------------------------------------------------
    // Classic physics values
    // ------------------------------------------------------------------

    private static (WireStructure Structure, MomSolution Solution) SolveMonopole(
        double heightOverLambda, int elements, double radius)
    {
        double height = heightOverLambda * Lambda;
        var grid = WireGridBuilder.Build(CanonicalAntennas.Monopole(height, radius),
            maxElementLength: height / elements, ground: new GroundPlane(0));
        Assert.NotNull(grid.Structure);
        int feed = grid.Structure!.NearestBasis(Vector3D.Zero);
        Assert.Equal(0, grid.Structure.BasisNode(feed));   // the feed is the grounded base
        return (grid.Structure, new ThinWireMomSolver().Solve(grid.Structure, Frequency, feed));
    }

    [Fact]
    public void QuarterWaveMonopole_InputImpedance_IsInTheClassicBand()
    {
        // 36.5 + j21.25 Ω — exactly half the dipole's band, by the discrete identity.
        var (_, solution) = SolveMonopole(0.25, elements: 20, radius: Lambda / 2000);
        Assert.InRange(solution.InputImpedance.Real, 35, 42.5);
        Assert.InRange(solution.InputImpedance.Imaginary, 12.5, 30);
    }

    [Fact]
    public void ShortMonopole_RadiationResistance_ApproachesTheThinLimit_FromBelow()
    {
        // R_rad = 40π²(h/λ)² is the infinitely-thin limit (twice the short-dipole
        // slope at half the height): the MoM value sits BELOW it and climbs as the wire
        // thins — the same one-sided band + trend as the free-space short-dipole gate.
        double expected = 40 * Math.PI * Math.PI * 0.05 * 0.05;
        var (_, solution) = SolveMonopole(0.05, elements: 20, radius: Lambda / 20000);
        Assert.InRange(solution.InputImpedance.Real, 0.85 * expected, 1.0 * expected);

        var (_, thinner) = SolveMonopole(0.05, elements: 20, radius: Lambda / 200000);
        Assert.True(thinner.InputImpedance.Real > solution.InputImpedance.Real,
            $"R should rise toward the thin limit: {solution.InputImpedance.Real:g4} → " +
            $"{thinner.InputImpedance.Real:g4}");
        Assert.True(thinner.InputImpedance.Real < expected);
    }

    [Fact]
    public void MonopoleDirectivity_IsDoubleTheDipoles()
    {
        // Image theory halves the radiated power at unchanged peak intensity: the short
        // monopole shows D = 3.00 (vs 1.5) and the λ/4 monopole 3.286 (vs 1.643).
        var (shortWire, shortSolution) = SolveMonopole(0.05, elements: 20, radius: Lambda / 20000);
        var shortPattern = FarFieldEvaluator.Compute(shortWire, shortSolution);
        Assert.InRange(shortPattern.MaxDirectivity, 0.98 * 3.0, 1.02 * 3.0);

        var (quarterWire, quarterSolution) = SolveMonopole(0.25, elements: 20, radius: Lambda / 2000);
        var quarterPattern = FarFieldEvaluator.Compute(quarterWire, quarterSolution);
        Assert.InRange(quarterPattern.MaxDirectivity, 0.98 * 3.286, 1.02 * 3.286);
    }

    [Fact]
    public void HemispherePowerBalance_HoldsForTheMonopole()
    {
        // THE Stage-A hard gate: radiated power from the hemisphere quadrature must
        // equal the circuit input power ½·Re(V·I*) — one identity across the image
        // assembly, the solve, the image radiation integral, and the hemisphere weights.
        var (wire, solution) = SolveMonopole(0.25, elements: 20, radius: Lambda / 2000);
        double inputPower = 0.5 * (Complex.One * Complex.Conjugate(solution.BasisCurrents[
            wire.NearestBasis(Vector3D.Zero)])).Real;
        var pattern = FarFieldEvaluator.Compute(wire, solution);
        Assert.InRange(pattern.TotalRadiatedPowerWatts / inputPower, 0.98, 1.02);
    }

    private static (WireStructure Structure, MomSolution Solution) SolveHorizontalDipole(
        double heightOverLambda, bool grounded = true)
    {
        double length = 0.5 * Lambda, h = heightOverLambda * Lambda;
        var grid = WireGridBuilder.Build(
            new[]
            {
                new WireSegment(new Vector3D(-length / 2, 0, h), new Vector3D(length / 2, 0, h),
                    Lambda / 2000)
            },
            maxElementLength: length / 40, ground: grounded ? new GroundPlane(0) : null);
        Assert.NotNull(grid.Structure);
        int feed = grid.Structure!.NearestBasis(new Vector3D(0, 0, h));
        return (grid.Structure, new ThinWireMomSolver().Solve(grid.Structure, Frequency, feed));
    }

    [Fact]
    public void HorizontalDipoleOverGround_MatchesMutualImpedanceTheory_AndCancelsAtTheHorizon()
    {
        // Mutual-impedance theory for sinusoidal currents: R(h)/R_free = 1 − R_m(2h)/R_self,
        // = 1.171 at h = 0.25λ (R_m(0.5λ) ≈ −12.5/73.1). The RATIO divides the geometry
        // factor out (this discretization's free-space R is 83.2 Ω, not the sinusoidal
        // 73.1) — the same style as the AC benchmarks dividing out R_dc. The MoM current
        // is not exactly sinusoidal, so the band is [1.10, 1.35] around the measured
        // 1.237; the sharp assertions here are the power balance and the horizon null.
        var (_, freeSolution) = SolveHorizontalDipole(0.25, grounded: false);
        var (wire, solution) = SolveHorizontalDipole(0.25);
        double ratio = solution.InputImpedance.Real / freeSolution.InputImpedance.Real;
        Assert.InRange(ratio, 1.10, 1.35);

        // At h = 0.5λ the mutual resistance flips SIGN (R_m(1.0λ) ≈ +4 Ω): the ground
        // now LOWERS the resistance below free space — a sign-sensitive physics check.
        var (_, half) = SolveHorizontalDipole(0.5);
        Assert.True(half.InputImpedance.Real < freeSolution.InputImpedance.Real,
            $"R(0.5λ over ground) = {half.InputImpedance.Real:g4} should drop below the " +
            $"free-space {freeSolution.InputImpedance.Real:g4} (positive mutual resistance)");

        // Power balance holds over ground for a horizontal wire too.
        double inputPower = 0.5 * Complex.Conjugate(
            solution.BasisCurrents[wire.NearestBasis(new Vector3D(0, 0, 0.25 * Lambda))]).Real;
        var pattern = FarFieldEvaluator.Compute(wire, solution);
        Assert.InRange(pattern.TotalRadiatedPowerWatts / inputPower, 0.98, 1.02);

        // Tangential E vanishes on a PEC: the intensity at the horizon is a deep null.
        double maxIntensity = 0, horizonIntensity = 0;
        int horizonIndex = 0;
        double smallestU = double.MaxValue;
        for (int ti = 0; ti < pattern.ThetaRadians.Count; ti++)
        {
            double u = Math.Cos(pattern.ThetaRadians[ti]);
            if (u < smallestU) { smallestU = u; horizonIndex = ti; }
        }
        for (int ti = 0; ti < pattern.ThetaRadians.Count; ti++)
            for (int pi = 0; pi < pattern.PhiRadians.Count; pi++)
                maxIntensity = Math.Max(maxIntensity, pattern.IntensityWattsPerSteradian[ti, pi]);
        for (int pi = 0; pi < pattern.PhiRadians.Count; pi++)
            horizonIntensity = Math.Max(horizonIntensity,
                pattern.IntensityWattsPerSteradian[horizonIndex, pi]);
        Assert.True(horizonIntensity < 1e-3 * maxIntensity,
            $"horizon intensity {horizonIntensity:g3} vs max {maxIntensity:g3} — image cancellation missing");
    }

    [Fact]
    public void HorizontalDipole_ResistanceCollapsesAsItApproachesTheGround()
    {
        // h → 0: the opposite image cancels the radiation, R → 0 monotonically.
        var (_, low) = SolveHorizontalDipole(0.05);
        var (_, mid) = SolveHorizontalDipole(0.10);
        var (_, high) = SolveHorizontalDipole(0.25);
        Assert.True(low.InputImpedance.Real < 20,
            $"R(0.05λ) = {low.InputImpedance.Real:g4} should be nearly cancelled");
        Assert.True(low.InputImpedance.Real < mid.InputImpedance.Real,
            $"R must fall toward the plane: R(0.05λ) = {low.InputImpedance.Real:g4} vs " +
            $"R(0.10λ) = {mid.InputImpedance.Real:g4}");
        Assert.True(mid.InputImpedance.Real < high.InputImpedance.Real,
            $"R must fall toward the plane: R(0.10λ) = {mid.InputImpedance.Real:g4} vs " +
            $"R(0.25λ) = {high.InputImpedance.Real:g4}");
    }

    // ------------------------------------------------------------------
    // Field probe over ground
    // ------------------------------------------------------------------

    [Fact]
    public void FieldProbe_ReturnsExactlyZeroBelowThePlane()
    {
        var (wire, solution) = SolveMonopole(0.25, elements: 20, radius: Lambda / 2000);
        var map = FieldProbe.Evaluate(wire, solution, new[]
        {
            new Vector3D(0.3 * Lambda, 0, -0.1 * Lambda),   // below
            new Vector3D(0.3 * Lambda, 0, 0),               // on the plane (PEC surface)
            new Vector3D(0.3 * Lambda, 0, 0.1 * Lambda)     // above
        });
        Assert.Equal(0.0, map.Magnitude[0]);
        Assert.Equal(0.0, map.Magnitude[1]);
        Assert.True(map.Magnitude[2] > 0);
    }

    // ------------------------------------------------------------------
    // Grid-builder ground contracts (typed failures)
    // ------------------------------------------------------------------

    [Fact]
    public void GridBuilder_RejectsWiresCrossingOrTouchingThePlane_Typed()
    {
        double r = 1e-3;
        var ground = new GroundPlane(0);

        var crossing = WireGridBuilder.Build(
            new[] { new WireSegment(new Vector3D(0, 0, -0.2), new Vector3D(0, 0, 0.5), r) },
            0.1, ground: ground);
        Assert.Null(crossing.Structure);
        Assert.Contains("ground plane", crossing.FailureReason);

        var touchingMidRun = WireGridBuilder.Build(new[]
        {
            new WireSegment(new Vector3D(0, 0, 0.3), new Vector3D(0.3, 0, 0), r),
            new WireSegment(new Vector3D(0.3, 0, 0), new Vector3D(0.6, 0, 0.3), r)
        }, 0.1, ground: ground);
        Assert.Null(touchingMidRun.Structure);
        Assert.Contains("mid-run", touchingMidRun.FailureReason);

        var below = WireGridBuilder.Build(
            new[] { new WireSegment(new Vector3D(0, 0, -0.5), new Vector3D(0, 0, -0.2), r) },
            0.1, ground: ground);
        Assert.Null(below.Structure);
        Assert.Contains("below", below.FailureReason);

        var inPlane = WireGridBuilder.Build(
            new[] { new WireSegment(new Vector3D(0, 0, 0), new Vector3D(0.5, 0, 0), r) },
            0.1, ground: ground);
        Assert.Null(inPlane.Structure);
        Assert.Contains("IN the ground plane", inPlane.FailureReason);

        var loop = WireGridBuilder.Build(new[]
        {
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(1, 0, 0.5), r),
            new WireSegment(new Vector3D(1, 0, 0.5), new Vector3D(0, 0, 1), r),
            new WireSegment(new Vector3D(0, 0, 1), new Vector3D(-1, 0, 0.5), r),
            new WireSegment(new Vector3D(-1, 0, 0.5), new Vector3D(0, 0, 0), r)
        }, 0.4, ground: ground);
        Assert.Null(loop.Structure);
        Assert.Contains("loop", loop.FailureReason);
    }

    [Fact]
    public void GridBuilder_GroundsBothEndsOfAnArch()
    {
        double r = 1e-3;
        var arch = WireGridBuilder.Build(new[]
        {
            new WireSegment(new Vector3D(0, 0, 0), new Vector3D(0, 0, 0.5), r),
            new WireSegment(new Vector3D(0, 0, 0.5), new Vector3D(0.5, 0, 0.5), r),
            new WireSegment(new Vector3D(0.5, 0, 0.5), new Vector3D(0.5, 0, 0), r)
        }, 0.1, ground: new GroundPlane(0));
        Assert.NotNull(arch.Structure);
        Assert.True(arch.Structure!.StartGrounded);
        Assert.True(arch.Structure.EndGrounded);
        // Both grounded ends carry a basis: N nodes ⇒ N − 2 interior + 2 grounded.
        Assert.Equal(arch.Structure.Nodes.Count, arch.Structure.BasisCount);
    }

    // ------------------------------------------------------------------
    // Determinism + symmetry with a ground present
    // ------------------------------------------------------------------

    [Fact]
    public void GroundedImpedanceMatrix_IsComplexSymmetric_Bitwise()
    {
        double length = Lambda / 15, radius = Lambda / 2000, k = 2 * Math.PI / Lambda;
        // A grounded bent wire: base at the plane, then up, over, and up again — image
        // self-pairs with two bases per element exercise the ordered-pair scatter.
        var wire = Structure(radius, new GroundPlane(0),
            new Vector3D(0, 0, 0), new Vector3D(0, 0, length), new Vector3D(length, 0, length),
            new Vector3D(length, 0, 2 * length));
        var z = ThinWireMomSolver.AssembleImpedanceMatrix(wire, k, 2 * Math.PI * Frequency);
        for (int i = 0; i < wire.BasisCount; i++)
            for (int j = 0; j < wire.BasisCount; j++)
                Assert.Equal(z[i, j], z[j, i]);
    }

    [Fact]
    public void GroundedSolve_IsBitwiseDeterministic()
    {
        var (_, first) = SolveMonopole(0.25, elements: 20, radius: Lambda / 2000);
        var (_, second) = SolveMonopole(0.25, elements: 20, radius: Lambda / 2000);
        Assert.Equal(first.InputImpedance, second.InputImpedance);
        for (int i = 0; i < first.BasisCurrents.Length; i++)
            Assert.Equal(first.BasisCurrents[i], second.BasisCurrents[i]);
    }
}

/// <summary>Test-local copies of the free-space constants (RfConstants is internal to
/// OpenSim.Rf but visible here; the duplicate keeps the oracle independent).</summary>
file static class RfConstants2
{
    public const double Mu0 = 4e-7 * Math.PI;
    public const double Eps0 = 8.8541878128e-12;
}
