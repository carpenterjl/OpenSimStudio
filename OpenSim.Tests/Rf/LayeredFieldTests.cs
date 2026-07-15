using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Surface;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage D checkpoint D1: the z-dependent spectral profiles and the voltage kernel
/// K̃_V, gated against an INDEPENDENT numeric boundary-value re-derivation before any
/// spatial-domain machinery consumes them. The oracle solves the raw 4×4 matching
/// system in plain (non-reduced) trig with a dense LU — it shares no code and no
/// algebra with SpectralProfiles/VoltageKernel — and its E_z is integrated NUMERICALLY
/// for the voltage, so it also knows nothing about the ∇Φ / A_z gauge split (the
/// gauge gate: formulation C's Φ alone measured 70–85 Ω against the cavity 228 Ω in
/// Stage C; the sum of both legs is what K̃_V must equal).
/// </summary>
public class LayeredFieldTests
{
    private static readonly SubstrateStackup Balanis = new(2.2, 0.0, 1.588e-3);
    private const double BalanisF = 10e9;

    /// <summary>The independent oracle: solve the interface-matching system for
    /// (C, P, S, Q) — region-0 A_x / region-1 A_x / region-0 A_z / region-1 A_z
    /// amplitudes for a unit horizontal source at z′ = d — then evaluate the profiles
    /// in plain trig. Valid while |Im(k_z1)|·d stays far from overflow (the oracle's
    /// only limitation; the reduced production forms have none).</summary>
    private static (Func<double, Complex> GA, Func<double, Complex> W,
        Func<double, Complex> Phi, Func<double, Complex> DPhi, Complex Kz0, Complex Kz1)
        Oracle(SubstrateStackup substrate, double k0, Complex kRho)
    {
        var epsC = substrate.RelativePermittivity * new Complex(1, -substrate.LossTangent);
        double k0Sq = k0 * k0;
        var kz0 = Sqrt(k0Sq - kRho * kRho);
        var kz1 = Sqrt(epsC * k0Sq - kRho * kRho);
        double d = substrate.ThicknessMeters;
        var s1 = Complex.Sin(kz1 * d);
        var c1 = Complex.Cos(kz1 * d);
        var j = Complex.ImaginaryOne;
        double mu0 = 4e-7 * Math.PI;
        double eps0 = 1.0 / (mu0 * 299_792_458.0 * 299_792_458.0);

        // Unknown order: [C, P, S, Q].
        var m = new ComplexDenseMatrix(4, 4);
        var rhs = new Complex[4];
        // A_x continuity at z = d.
        m[0, 0] = 1; m[0, 1] = -s1;
        // ∂z A_x source jump at z = d.
        m[1, 0] = j * kz0; m[1, 1] = kz1 * c1; rhs[1] = 2 * mu0;
        // A_z continuity at z = d.
        m[2, 2] = 1; m[2, 3] = -c1;
        // Φ continuity at z = d (the mixed 1/ε condition).
        m[3, 0] = 1; m[3, 2] = -j * kz0; m[3, 1] = -s1 / epsC; m[3, 3] = kz1 * s1 / epsC;
        var x = ComplexLu.Factor(m).Solve(rhs);
        Complex cAmp = x[0], p = x[1], sAmp = x[2], q = x[3];

        Complex GA(double z) => z >= d
            ? cAmp * Complex.Exp(-j * kz0 * (z - d))
            : p * Complex.Sin(kz1 * z);
        Complex W(double z) => z >= d
            ? sAmp * Complex.Exp(-j * kz0 * (z - d))
            : q * Complex.Cos(kz1 * z);
        Complex Phi(double z) => z >= d
            ? (cAmp - j * kz0 * sAmp) * Complex.Exp(-j * kz0 * (z - d)) / (mu0 * eps0)
            : (p - kz1 * q) * Complex.Sin(kz1 * z) / (mu0 * eps0 * epsC);
        Complex DPhi(double z) => z >= d
            ? -j * kz0 * (cAmp - j * kz0 * sAmp) * Complex.Exp(-j * kz0 * (z - d)) / (mu0 * eps0)
            : kz1 * (p - kz1 * q) * Complex.Cos(kz1 * z) / (mu0 * eps0 * epsC);
        return (GA, W, Phi, DPhi, kz0, kz1);

        static Complex Sqrt(Complex v)
        {
            var r = Complex.Sqrt(v);
            return r.Imaginary > 0 ? -r : r;
        }
    }

    [Fact]
    public void EvaluatorEz_IntegratesToTheVoltageKernel_WithTheRightSign()
    {
        // Pins the LayeredFieldEvaluator Φ-leg SIGN (the old −1.027 observation): the
        // straight-path voltage −∫₀^d E_z(r,z) dz assembled by the near-field evaluator
        // (E_z = −ω²A_z(via W) − ∂_zΦ, both gauge legs) must reproduce EdgeVoltage(r) =
        // K_V ∗ q (the SAME two legs, a different code path). Measured ratio ≈ +0.97
        // (POSITIVE, ~3% low from the evaluator's surface-scale quadrature near the metal
        // — not −1.027; that earlier figure was a sign convention in a brute-force probe,
        // NOT an evaluator bug). Independent-path consistency + the sign, not a sharp gate.
        double W = 1.186e-2, L = 0.906e-2, mesh = 1.4e-3, d = 1.588e-3;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(W, L, mesh, z: d, portFraction: 0.08);
        var table = new LayeredKernelTable(Balanis, BalanisF, 0.025);
        var sol = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);
        foreach (double fx in new[] { -0.4, -0.2, 0.2, 0.4 })
        {
            var edge = new Vector3D(fx * W, -L / 2 + 0.5 * mesh, d);
            var vK = LayeredPotentialProbe.EdgeVoltage(grid.Structure!, table, sol, edge);
            var (gn, gw) = GaussLegendre.Rule(8, 0, 1);
            var pts = gn.Select(t => new Vector3D(edge.X, edge.Y, t * d)).ToList();
            var map = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, sol, pts);
            Complex integral = Complex.Zero;
            for (int i = 0; i < gn.Length; i++) integral += gw[i] * d * map.E[i].Z;
            var ratio = -integral / vK;
            Assert.InRange(ratio.Real, 0.90, 1.05);       // positive, near 1 (NOT −1.027)
            Assert.True(Math.Abs(ratio.Imaginary) < 0.05, $"Im(ratio) = {ratio.Imaginary}");
        }
    }

    private static void AssertRel(Complex expected, Complex actual, double tol, string what)
    {
        double scale = Math.Max(expected.Magnitude, actual.Magnitude);
        if (scale == 0) { Assert.Equal(expected, actual); return; }
        double rel = (expected - actual).Magnitude / scale;
        Assert.True(rel <= tol, $"{what}: oracle {expected}, closed form {actual} (rel {rel:e2})");
    }

    public static IEnumerable<object[]> ProfileSamples()
    {
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        double k1 = k0 * Math.Sqrt(2.2);
        double d = 1.588e-3;
        foreach (double kRho in new[] { 0.3 * k0, 0.999 * k0, 0.5 * (k0 + k1), 1.2 * k1, 5 * k1, 40 * k1 })
            foreach (double z in new[] { 0.0, 0.25 * d, 0.5 * d, 0.99 * d, d, 1.5 * d, 2 * d })
                yield return new object[] { kRho, z };
    }

    [Theory]
    [MemberData(nameof(ProfileSamples))]
    public void Profiles_MatchTheIndependentBvpOracle(double kRho, double z)
    {
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        var (oGA, oW, oPhi, oDPhi, kz0, _) = Oracle(Balanis, k0, kRho);
        var (ga, w, kPhi) = SpectralProfiles.Evaluate(Balanis, k0, kRho, kz0, z);
        AssertRel(oGA(z), ga, 1e-12, $"G̃_A(kρ={kRho:g4}, z={z:g4})");
        AssertRel(oW(z), w, 1e-12, $"W̃(kρ={kRho:g4}, z={z:g4})");
        AssertRel(oPhi(z), kPhi, 1e-12, $"K̃_Φ(kρ={kRho:g4}, z={z:g4})");

        var (dGa, dw, dPhi) = SpectralProfiles.EvaluateDz(Balanis, k0, kRho, kz0, z);
        AssertRel(oDPhi(z), dPhi, 1e-12, $"∂zK̃_Φ(kρ={kRho:g4}, z={z:g4})");
        // ∂z of the cos profile via the oracle's trig derivative.
        double d = Balanis.ThicknessMeters;
        var oDw = z >= d
            ? -Complex.ImaginaryOne * kz0 * oW(z)
            : AnalyticDw(kRho, z);
        AssertRel(oDw, dw, 1e-12, $"∂zW̃(kρ={kRho:g4}, z={z:g4})");
        // ∂z of the sin profile G̃_A (Stage S9a, the H = ∇×A leg): P·sin → P·k_z1·cos.
        var oDga = z >= d
            ? -Complex.ImaginaryOne * kz0 * oGA(z)
            : AnalyticDga(kRho, z);
        AssertRel(oDga, dGa, 1e-12, $"∂zG̃_A(kρ={kRho:g4}, z={z:g4})");
    }

    /// <summary>Oracle ∂z of the region-1 A_x profile: P·sin(k_z1 z) → P·k_z1·cos(k_z1 z).
    /// Recover P from the profile at z = d (avoids the cot singularity at z = 0).</summary>
    private static Complex AnalyticDga(double kRho, double z)
    {
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        double d = Balanis.ThicknessMeters;
        var kz1 = Complex.Sqrt(new Complex(2.2, 0) * k0 * k0 - kRho * kRho);
        if (kz1.Imaginary > 0) kz1 = -kz1;
        var (oGA, _, _, _, _, _) = Oracle(Balanis, k0, kRho);
        var p = oGA(d) / Complex.Sin(kz1 * d);        // GA(d) = P·sin(kz1·d)
        return kz1 * p * Complex.Cos(kz1 * z);
    }

    /// <summary>Oracle ∂z of the region-1 A_z profile: Q·cos → −Q·k_z1·sin, with
    /// (Q, k_z1) re-derived through the same independent 4×4 solve.</summary>
    private static Complex AnalyticDw(double kRho, double z)
    {
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        var epsC = new Complex(2.2, 0);
        var kz1 = Complex.Sqrt(epsC * k0 * k0 - kRho * kRho);
        if (kz1.Imaginary > 0) kz1 = -kz1;
        var (_, oW, _, _, _, _) = Oracle(Balanis, k0, kRho);
        // Recover Q from the profile itself at a point where cos ≠ 0 is fussy; use
        // the derivative identity via two profile relations instead:
        // W(z) = Q cos(kz1 z) ⇒ W′(z) = −kz1·Q sin(kz1 z) = −kz1·tan(kz1 z)·W(z).
        var tan = Complex.Sin(kz1 * z) / Complex.Cos(kz1 * z);
        return -kz1 * tan * oW(z);
    }

    [Fact]
    public void VoltageKernel_MatchesTheNumericFieldIntegral_TheGaugeGate()
    {
        // THE gauge gate: K̃_V (closed-form z-integrals of both E_z legs) against a
        // 64-point Gauss–Legendre integration of the ORACLE's Ẽ_z = −ω²W̃ − ∂zΦ̃ per
        // unit charge — the oracle never separates the legs.
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        double omega = k0 * 299_792_458.0;
        double k1 = k0 * Math.Sqrt(2.2);
        double d = Balanis.ThicknessMeters;
        var (nodes, weights) = GaussLegendre.Rule(64);

        foreach (double kRho in new[] { 0.3 * k0, 0.999 * k0, 0.5 * (k0 + k1), 1.2 * k1, 5 * k1, 40 * k1 })
        {
            var (_, oW, _, oDPhi, kz0, _) = Oracle(Balanis, k0, kRho);
            Complex v = Complex.Zero;
            for (int i = 0; i < nodes.Length; i++)
            {
                double z = 0.5 * d * (nodes[i] + 1);
                double wgt = 0.5 * d * weights[i];
                // V = −∫E_z dz = ∫(ω²·W̃ + ∂zΦ̃)dz per unit charge.
                v += wgt * (omega * omega * oW(z) + oDPhi(z));
            }
            var closed = VoltageKernel.Evaluate(Balanis, k0, kRho, kz0);
            AssertRel(v, closed, 1e-12, $"K̃_V(kρ={kRho:g4})");
        }
    }

    [Fact]
    public void EpsilonROne_Profiles_AreExactlyPrimaryPlusImage()
    {
        // At εr = 1 the exact profile is the two-exponential free-space + PEC-image
        // form at heights |z−d| and z+d, and the TM coupling W̃ vanishes identically.
        var air = new SubstrateStackup(1, 0, 1.588e-3);
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        double d = air.ThicknessMeters;
        double mu0 = 4e-7 * Math.PI, eps0 = 1.0 / (mu0 * 299_792_458.0 * 299_792_458.0);
        var j = Complex.ImaginaryOne;

        foreach (double kRho in new[] { 0.4 * k0, 3 * k0, 50 * k0 })
            foreach (double z in new[] { 0.0, 0.3 * d, d, 1.7 * d })
            {
                var kz0 = Complex.Sqrt(k0 * k0 - kRho * kRho);
                if (kz0.Imaginary > 0) kz0 = -kz0;
                var (ga, w, kPhi) = SpectralProfiles.Evaluate(air, k0, kRho, kz0, z);
                var exact = (Complex.Exp(-j * kz0 * Math.Abs(z - d))
                             - Complex.Exp(-j * kz0 * (z + d))) / (j * kz0);
                AssertRel(mu0 * exact, ga, 1e-13, $"εr=1 G̃_A(kρ={kRho:g4}, z={z:g4})");
                AssertRel(exact / eps0, kPhi, 1e-13, $"εr=1 K̃_Φ(kρ={kRho:g4}, z={z:g4})");
                Assert.Equal(Complex.Zero, w);
            }
    }

    [Fact]
    public void Profiles_AtTheSlabTop_ReproduceTheStageCKernels()
    {
        // z → d must fall out as the existing kernels — an identity, not a tolerance:
        // the amplitude expressions are the same reduced-trig operations.
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        double k1 = k0 * Math.Sqrt(2.2);
        foreach (double kRho in new[] { 0.5 * k0, 1.05 * k0, 2 * k1, 100 * k1 })
        {
            var kz0 = Complex.Sqrt(k0 * k0 - kRho * kRho);
            if (kz0.Imaginary > 0) kz0 = -kz0;
            var (gaK, kPhiK) = SpectralKernels.Evaluate(Balanis, k0, kRho, kz0);
            var (ga, w, kPhi) = SpectralProfiles.Evaluate(
                Balanis, k0, kRho, kz0, Balanis.ThicknessMeters);
            Assert.Equal(gaK, ga);
            Assert.Equal(kPhiK, kPhi);
            var ratio = SpectralKernels.AzRatio(Balanis, k0, kRho, kz0);
            AssertRel(ratio * gaK, w, 1e-14, $"W̃(d) vs AzRatio·G̃_A (kρ={kRho:g4})");
        }
    }

    [Fact]
    public void Profiles_SatisfyThePecConditions_Identically()
    {
        // Φ = A_x = 0 and ∂z(A_z profile) = 0 ON the ground — built into the trig
        // forms, asserted rather than trusted.
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        foreach (double kRho in new[] { 0.5 * k0, 3 * k0 * Math.Sqrt(2.2) })
        {
            var kz0 = Complex.Sqrt(k0 * k0 - kRho * kRho);
            if (kz0.Imaginary > 0) kz0 = -kz0;
            var (ga, _, kPhi) = SpectralProfiles.Evaluate(Balanis, k0, kRho, kz0, 0.0);
            Assert.Equal(Complex.Zero, ga);
            Assert.Equal(Complex.Zero, kPhi);
            var (_, dw, _) = SpectralProfiles.EvaluateDz(Balanis, k0, kRho, kz0, 0.0);
            Assert.Equal(Complex.Zero, dw);
        }
    }

    [Fact]
    public void ProfilesAndVoltageKernel_SurviveTheDeepEvanescentTail()
    {
        // kρ·d ≫ 700: the oracle's plain trig would overflow here; the reduced forms
        // must stay finite at every z (the K̃_V antiderivative shares the E-cancellation
        // — proven numerically, not assumed).
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        double d = Balanis.ThicknessMeters;
        double kRho = 1e6 * k0; // kρ·d ≈ 3.3e5
        var kz0 = new Complex(0, -1) * Complex.Sqrt(kRho * kRho - k0 * k0);

        foreach (double z in new[] { 0.0, 0.5 * d, d })
        {
            var (ga, w, kPhi) = SpectralProfiles.Evaluate(Balanis, k0, kRho, kz0, z);
            Assert.True(Complex.IsFinite(ga) && Complex.IsFinite(w) && Complex.IsFinite(kPhi),
                $"profile overflow at kρd={kRho * d:g3}, z={z:g4}");
        }
        var kv = VoltageKernel.Evaluate(Balanis, k0, kRho, kz0);
        Assert.True(Complex.IsFinite(kv), "K̃_V overflow on the deep tail");
        // And the asymptotic content is right: K̃_V → K̃_Φ's c₀/(j kz0 ε₀) image limit
        // (the A_z leg decays a full 1/kρ² faster).
        var (_, kPhiTail) = SpectralKernels.Evaluate(Balanis, k0, kRho, kz0);
        AssertRel(kPhiTail, kv, 1e-9, "K̃_V tail vs K̃_Φ tail");
    }

    [Fact]
    public void VoltageKernel_CollapsesToKPhi_AtEpsilonROne()
    {
        var air = new SubstrateStackup(1, 0, 1.588e-3);
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        foreach (double kRho in new[] { 0.5 * k0, 2 * k0, 30 * k0 })
        {
            var kz0 = Complex.Sqrt(k0 * k0 - kRho * kRho);
            if (kz0.Imaginary > 0) kz0 = -kz0;
            var (_, kPhi) = SpectralKernels.Evaluate(air, k0, kRho, kz0);
            // The A_z leg is exactly zero at εc = 1, but the surviving Φ leg is
            // associated differently ((2/ε₀)·(Ŝ·N̂) vs ((2/ε₀)·Ŝ)·N̂) — mathematically
            // identical, one ULP apart numerically.
            AssertRel(kPhi, VoltageKernel.Evaluate(air, k0, kRho, kz0), 1e-15,
                $"K̃_V vs K̃_Φ at εr=1 (kρ={kRho:g4})");
        }
    }

    // ------------------------------------------------------------------
    // D2: the per-z SPATIAL field kernels (images + poles + remainder)
    // ------------------------------------------------------------------

    /// <summary>Total spatial kernels at (ρ, z): closed-form images + per-z pole
    /// terms + the Sommerfeld remainder — the composition the field evaluator uses.</summary>
    private static (Complex A, Complex W, Complex Phi, Complex DzPhi) TotalFieldKernels(
        SubstrateStackup substrate, double frequencyHz, double rho, double z, int refinement = 1)
    {
        double k0 = 2 * Math.PI * frequencyHz / 299_792_458.0;
        var poles = SurfaceWavePoles.Find(substrate, k0);
        var (rA, rW, rPhi, rDz, _, _) = SommerfeldIntegrator.FieldRemainder(
            substrate, k0, poles, rho, z, refinement);

        double d = substrate.ThicknessMeters;
        Complex gDyn(double r)
        {
            var (sin, cos) = Math.SinCos(k0 * r);
            return new Complex(cos, -sin) / (4 * Math.PI * r);
        }
        Complex gDynPrime(double r)
        {
            var (sin, cos) = Math.SinCos(k0 * r);
            var e = new Complex(cos, -sin);
            return -e * (1 + Complex.ImaginaryOne * k0 * r) / (4 * Math.PI * r * r);
        }
        var images = LayeredFieldKernels.Images(substrate, z);
        Complex imgA = Complex.Zero, imgPhi = Complex.Zero, imgDz = Complex.Zero;
        for (int m = 0; m < images.Length; m++)
        {
            double h = images[m].Height;
            double r = Math.Sqrt(rho * rho + h * h);
            imgA += images[m].CoefficientA * gDyn(r);
            imgPhi += images[m].CoefficientPhi * gDyn(r);
            double dhdz = z >= d ? 1 : (m % 2 == 0 ? -1 : 1);
            imgDz += images[m].CoefficientPhi * dhdz * (h / r) * gDynPrime(r);
        }

        Complex poleA = Complex.Zero, poleW = Complex.Zero, polePhi = Complex.Zero, poleDz = Complex.Zero;
        foreach (var pole in poles)
        {
            var res = LayeredFieldKernels.PoleResidues(substrate, k0, pole.KRho, z);
            var factor = new Complex(0, -0.25) * pole.KRho * Bessel.H02(pole.KRho * rho);
            poleA += res.A * factor;
            poleW += res.W * factor;
            polePhi += res.Phi * factor;
            poleDz += res.DzPhi * factor;
        }

        return (RfConstants.Mu0 * imgA + poleA + rA,
                poleW + rW,
                imgPhi / RfConstants.Eps0 + polePhi + rPhi,
                imgDz / RfConstants.Eps0 + poleDz + rDz);
    }

    [Fact]
    public void FieldKernels_AtEpsilonROne_AreExactlyPrimaryPlusImage()
    {
        // The whole per-z pipeline (image ladder, per-z residues, remainder) must
        // reduce to free space + PEC image with a machine-zero remainder at εr = 1.
        var air = new SubstrateStackup(1, 0, 1.588e-3);
        double d = air.ThicknessMeters;
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        foreach (double rho in new[] { 0.3 * d, 2 * d, 20 * d })
            foreach (double z in new[] { 0.0, 0.4 * d, d, 1.6 * d, 3 * d })
            {
                var (a, w, phi, dz) = TotalFieldKernels(air, BalanisF, rho, z);
                Complex Exact(double h)
                {
                    double r = Math.Sqrt(rho * rho + h * h);
                    var (sin, cos) = Math.SinCos(k0 * r);
                    return new Complex(cos, -sin) / (4 * Math.PI * r);
                }
                var expected = Exact(Math.Abs(z - d)) - Exact(z + d);
                AssertRel(4e-7 * Math.PI * expected, a, 1e-10, $"εr=1 A(ρ={rho:g3}, z={z:g3})");
                double eps0 = 1.0 / (4e-7 * Math.PI * 299_792_458.0 * 299_792_458.0);
                AssertRel(expected / eps0, phi, 1e-10, $"εr=1 Φ(ρ={rho:g3}, z={z:g3})");
                Assert.True(w.Magnitude <= 1e-10 * phi.Magnitude * eps0 * 4e-7 * Math.PI
                            + 1e-30, $"εr=1 W should vanish (|W|={w.Magnitude:e2})");
            }
    }

    [Fact]
    public void FieldKernels_AtTheSlabTop_MatchTheBoundaryKernelTable()
    {
        // z = d through the per-z path (its DIFFERENT image bookkeeping — the 4-term
        // in-slab ladder vs the c₀/c₁ pair) against the production boundary kernels:
        // the TOTALS must agree even though the extractions differ.
        var table = new LayeredKernelTable(Balanis, BalanisF, 0.05);
        foreach (double rho in new[] { 2e-4, 2e-3, 2e-2 })
        {
            var (a, _, phi, _) = TotalFieldKernels(Balanis, BalanisF, rho, Balanis.ThicknessMeters);
            var (gaRef, kPhiRef) = table.EvaluateKernelsDirect(rho, refinement: 2);
            AssertRel(gaRef, a, 1e-9, $"A total at z=d, ρ={rho:g3}");
            AssertRel(kPhiRef, phi, 1e-9, $"Φ total at z=d, ρ={rho:g3}");
        }
    }

    [Fact]
    public void FieldKernels_AreContinuousAcrossTheInterface()
    {
        // Potentials and tangential E are continuous across z = d; D_z = ε·E_z jumps
        // by exactly the permittivity ratio. Approach from both sides.
        double d = Balanis.ThicknessMeters;
        double eps = 1e-6 * d;
        foreach (double rho in new[] { 1e-3, 8e-3 })
        {
            var below = TotalFieldKernels(Balanis, BalanisF, rho, d - eps);
            var above = TotalFieldKernels(Balanis, BalanisF, rho, d + eps);
            // 5e-6, not tighter: at finite ε the potentials genuinely vary by
            // ~ε·|∂z ln F| per side (the surface wave's e^{−|k_z0p|(z−d)} tail alone
            // is ~4e-7 here) — the gate bounds [true jump 0] + that variation.
            AssertRel(below.A, above.A, 5e-6, $"A continuity at ρ={rho:g3}");
            AssertRel(below.W, above.W, 5e-6, $"W continuity at ρ={rho:g3}");
            AssertRel(below.Phi, above.Phi, 5e-6, $"Φ continuity at ρ={rho:g3}");

            // D_z continuity at finite offset ε measures the field's genuine z-slope
            // (the mismatch is C·ε — measured EXACTLY linear: 3.0e-5 at 1e-6·d,
            // 3.0e-3 at 1e-4·d). The gate is therefore the LINEARITY of vanishing:
            // small at tiny ε AND scaling ∝ ε, which pins the ε → 0 intercept at
            // zero. The sharp form of the same physics is the spectral jump
            // identity below.
            double omega = 2 * Math.PI * BalanisF;
            var epsC = new Complex(2.2, 0);
            Complex Ez((Complex A, Complex W, Complex Phi, Complex DzPhi) k) =>
                -omega * omega * k.W - k.DzPhi;
            double MismatchAt(double offset)
            {
                var lo = TotalFieldKernels(Balanis, BalanisF, rho, d - offset);
                var hi = TotalFieldKernels(Balanis, BalanisF, rho, d + offset);
                return (epsC * Ez(lo) - Ez(hi)).Magnitude / Ez(hi).Magnitude;
            }
            double tiny = MismatchAt(1e-6 * d);
            double small = MismatchAt(1e-4 * d);
            Assert.True(tiny <= 1e-4, $"D_z mismatch at 1e-6·d: {tiny:e2}");
            Assert.InRange(small / tiny, 80.0, 120.0); // ∝ ε ⇒ zero intercept
        }
    }

    [Fact]
    public void SpectralDzJump_IsExactlyTheSourceSheetCharge()
    {
        // The sharp interface-physics gate: ε_c·Ẽ_z(d⁻) − Ẽ_z(d⁺) = −2/ε₀ at EVERY
        // k_ρ (the spectral source is a charge sheet at the interface; spatially the
        // jump localizes to a delta at the origin). Derivable from the matching rows,
        // but asserted numerically against the closed profiles.
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        double omega = 2 * Math.PI * BalanisF;
        double d = Balanis.ThicknessMeters;
        double eps0 = 1.0 / (4e-7 * Math.PI * 299_792_458.0 * 299_792_458.0);
        var epsC = new Complex(2.2, 0);
        foreach (double mult in new[] { 0.5, 1.4, 3.0, 20.0 })
        {
            double kRho = mult * k0;
            var kz0 = Complex.Sqrt(k0 * k0 - kRho * kRho);
            if (kz0.Imaginary > 0) kz0 = -kz0;
            double eps = 1e-9 * d;
            var below = LayeredFieldKernels.EvaluateAll(Balanis, k0, kRho, kz0, d - eps);
            var above = LayeredFieldKernels.EvaluateAll(Balanis, k0, kRho, kz0, d + eps);
            var ezB = -omega * omega * below.W - below.DzPhi;
            var ezA = -omega * omega * above.W - above.DzPhi;
            AssertRel(new Complex(-2, 0), (epsC * ezB - ezA) * eps0, 1e-7,
                $"spectral D_z jump at kρ={mult}k0");
        }
    }

    [Fact]
    public void FieldKernels_SelfConverge()
    {
        // Doubling the integration refinement moves nothing beyond 1e-8 — the C1
        // discipline applied to the per-z machinery, at an in-slab AND an above-slab
        // height.
        double d = Balanis.ThicknessMeters;
        foreach (double z in new[] { 0.5 * d, 2.5 * d })
        {
            var coarse = TotalFieldKernels(Balanis, BalanisF, 4e-3, z, refinement: 1);
            var fine = TotalFieldKernels(Balanis, BalanisF, 4e-3, z, refinement: 2);
            AssertRel(fine.A, coarse.A, 1e-8, $"A self-convergence z={z:g3}");
            AssertRel(fine.W, coarse.W, 1e-8, $"W self-convergence z={z:g3}");
            AssertRel(fine.Phi, coarse.Phi, 1e-8, $"Φ self-convergence z={z:g3}");
            AssertRel(fine.DzPhi, coarse.DzPhi, 1e-8, $"∂zΦ self-convergence z={z:g3}");
        }
    }

    [Fact]
    public void PerZPoleResidues_AtTheSlabTop_MatchTheStageCResidues()
    {
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        var poles = SurfaceWavePoles.Find(Balanis, k0);
        foreach (var pole in poles)
        {
            var res = LayeredFieldKernels.PoleResidues(Balanis, k0, pole.KRho,
                Balanis.ThicknessMeters);
            AssertRel(pole.ResiduePhi, res.Phi, 1e-12, "Res_Φ(z=d)");
            if (pole.ResidueA != Complex.Zero)
                AssertRel(pole.ResidueA, res.A, 1e-12, "Res_A(z=d)");
        }
    }

    // ------------------------------------------------------------------
    // D2: the assembled E-field evaluator
    // ------------------------------------------------------------------

    [Fact]
    public void FieldEvaluator_AtEpsilonROne_MatchesTheFreeSpaceProbe()
    {
        // The evaluator's ASSEMBLY (per-z tables, RWG quadrature, boundary-trick
        // gradient) against SurfaceFieldProbe's independent assembly (pointwise ∇g,
        // subdivision, ε-regularization) on identical εr = 1 physics. 5e-3, not
        // tighter: both are map-grade quadratures and they differ by strategy — the
        // kernel-level εr = 1 gates carry the 1e-10 physics claim. Measured 1.0e-3.
        double f = 300e6, lambda = 299_792_458.0 / f, h = 0.05 * lambda;
        var grounded = SurfaceMeshBuilder.BuildPatchOverGround(
            0.3 * lambda, 0.5 * lambda, h, 0, lambda / 12);
        var bare = SurfaceMeshBuilder.BuildRectangularPlate(
            0.3 * lambda, 0.5 * lambda, lambda / 12, z: h, portFraction: 0);
        var table = new LayeredKernelTable(new SubstrateStackup(1, 0, h), f, 1.5 * lambda);
        var solG = new SurfaceMomSolver().Solve(grounded.Structure!, f, grounded.Port!);
        var solL = new SurfaceMomSolver().Solve(bare.Structure!, table, bare.Port!);

        var points = new List<Vector3D>();
        foreach (double zf in new[] { 1.5, 2.5, 5.0 })
            foreach (double xf in new[] { 0.0, 0.2, 0.45 })
                points.Add(new Vector3D(xf * lambda, 0.1 * lambda, zf * h));
        var mapFree = SurfaceFieldProbe.Evaluate(grounded.Structure!, solG, points);
        var mapLayered = LayeredFieldEvaluator.Evaluate(bare.Structure!, table, solL, points);
        for (int i = 0; i < points.Count; i++)
        {
            double rel = Math.Abs(mapFree.Magnitude[i] - mapLayered.Magnitude[i])
                         / Math.Max(mapFree.Magnitude[i], 1e-30);
            Assert.True(rel <= 5e-3, $"|E| at {points[i]}: free {mapFree.Magnitude[i]:e3} " +
                $"vs layered {mapLayered.Magnitude[i]:e3} (rel {rel:e2})");
        }
    }

    [Fact]
    public void FieldEvaluator_TangentialE_VanishesOnThePec()
    {
        // Real substrate (εr = 2.2): E_tan just above the ground must vanish
        // relative to the mid-slab field. Measured 1.4e-5 (quadrature floor).
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            1.186e-2, 0.906e-2, 1.4e-3, z: Balanis.ThicknessMeters, portFraction: 0);
        var table = new LayeredKernelTable(Balanis, BalanisF, 0.05);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);

        double d = Balanis.ThicknessMeters;
        var pecPoints = new List<Vector3D>();
        var midPoints = new List<Vector3D>();
        foreach (double xf in new[] { 0.0, 0.3, 0.8 })
        {
            pecPoints.Add(new Vector3D(xf * 1.186e-2, 0.2e-2, 1e-5 * d));
            midPoints.Add(new Vector3D(xf * 1.186e-2, 0.2e-2, 0.5 * d));
        }
        var pec = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, pecPoints);
        var mid = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, midPoints);
        for (int i = 0; i < pecPoints.Count; i++)
        {
            var (ex, ey, _) = pec.E[i];
            double tan = Math.Sqrt(ex.Magnitude * ex.Magnitude + ey.Magnitude * ey.Magnitude);
            Assert.True(tan <= 1e-3 * mid.Magnitude[i],
                $"E_tan on PEC at {pecPoints[i]}: {tan:e3} vs mid-slab {mid.Magnitude[i]:e3}");
        }
        // At or below the ground: exactly zero.
        var interior = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution,
            new[] { new Vector3D(0, 0, -0.1 * d), new Vector3D(0, 0, 0.0) });
        Assert.Equal(0.0, interior.Magnitude[0]);
        Assert.Equal(0.0, interior.Magnitude[1]);
    }

    [Fact]
    public void FieldEvaluator_IsBitwiseIdentical_AtAnyDop()
    {
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            1.186e-2, 0.906e-2, 1.4e-3, z: Balanis.ThicknessMeters, portFraction: 0);
        var table = new LayeredKernelTable(Balanis, BalanisF, 0.05);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);
        var points = new[]
        {
            new Vector3D(0, 0, 2 * Balanis.ThicknessMeters),
            new Vector3D(4e-3, 1e-3, 0.4 * Balanis.ThicknessMeters)
        };
        var serial = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, points,
            maxDegreeOfParallelism: 1);
        var parallel = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, points);
        for (int i = 0; i < points.Length; i++)
        {
            Assert.Equal(serial.E[i], parallel.E[i]);
            Assert.Equal(serial.H![i], parallel.H![i]);   // H is deterministic too
        }
    }

    // ------------------------------------------------------------------
    // S9a: the layered H field, H = ∇×A/µ₀ over a substrate.
    // ------------------------------------------------------------------

    [Fact]
    public void LayeredH_AtEpsilonROne_MatchesTheFreeSpaceProbe()
    {
        // The headline identity: at εr = 1 the layered H assembly (boundary-trick curl,
        // ∂zG̃_A kernel) must equal SurfaceFieldProbe's independent free-space + PEC-image
        // H (pointwise ∇×g). W̃ = 0 at εr = 1, so this exercises the G̃_A / ∂zG̃_A legs. The
        // band is 1.5e-2 (measured 8.7e-3), wider than the E analog's 5e-3: H is a
        // DERIVATIVE (curl) of A, so the two assembly strategies' map-grade quadrature
        // differences amplify — the kernel-level ∂z gates carry the 1e-12 physics claim.
        double f = 300e6, lambda = 299_792_458.0 / f, h = 0.05 * lambda;
        var grounded = SurfaceMeshBuilder.BuildPatchOverGround(
            0.3 * lambda, 0.5 * lambda, h, 0, lambda / 12);
        var bare = SurfaceMeshBuilder.BuildRectangularPlate(
            0.3 * lambda, 0.5 * lambda, lambda / 12, z: h, portFraction: 0);
        var table = new LayeredKernelTable(new SubstrateStackup(1, 0, h), f, 1.5 * lambda);
        var solG = new SurfaceMomSolver().Solve(grounded.Structure!, f, grounded.Port!);
        var solL = new SurfaceMomSolver().Solve(bare.Structure!, table, bare.Port!);

        var points = new List<Vector3D>();
        foreach (double zf in new[] { 1.5, 2.5, 5.0 })
            foreach (double xf in new[] { 0.0, 0.2, 0.45 })
                points.Add(new Vector3D(xf * lambda, 0.1 * lambda, zf * h));
        var mapFree = SurfaceFieldProbe.Evaluate(grounded.Structure!, solG, points);
        var mapLayered = LayeredFieldEvaluator.Evaluate(bare.Structure!, table, solL, points);
        for (int i = 0; i < points.Count; i++)
        {
            double rel = Math.Abs(mapFree.HMagnitude![i] - mapLayered.HMagnitude![i])
                         / Math.Max(mapFree.HMagnitude[i], 1e-30);
            Assert.True(rel <= 1.5e-2, $"|H| at {points[i]}: free {mapFree.HMagnitude[i]:e3} " +
                $"vs layered {mapLayered.HMagnitude[i]:e3} (rel {rel:e2})");
        }
    }

    [Fact]
    public void LayeredH_NormalComponent_VanishesApproachingTheGround()
    {
        // B_normal = µ₀H_z = 0 on a PEC. On a real substrate (εr = 2.2), H_z just above
        // the ground plane must vanish relative to the mid-slab field — a gate on the
        // H_z boundary-integral assembly (∮ G̃_A(J·t̂)) over a substrate.
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            1.186e-2, 0.906e-2, 1.4e-3, z: Balanis.ThicknessMeters, portFraction: 0);
        var table = new LayeredKernelTable(Balanis, BalanisF, 0.05);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);
        double d = Balanis.ThicknessMeters;

        var pecPoints = new List<Vector3D>();
        var midPoints = new List<Vector3D>();
        foreach (double xf in new[] { 0.1, 0.3, 0.8 })
        {
            pecPoints.Add(new Vector3D(xf * 1.186e-2, 0.2e-2, 1e-4 * d));
            midPoints.Add(new Vector3D(xf * 1.186e-2, 0.2e-2, 0.5 * d));
        }
        var pec = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, pecPoints);
        var mid = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, midPoints);
        for (int i = 0; i < pecPoints.Count; i++)
            Assert.True(pec.H![i].Z.Magnitude <= 1e-2 * mid.HMagnitude![i],
                $"H_z on PEC at {pecPoints[i]}: {pec.H[i].Z.Magnitude:e3} vs mid {mid.HMagnitude![i]:e3}");
    }

    [Fact]
    public void LayeredH_IsDivergenceFree_OverTheSubstrate()
    {
        // ∇·B = 0: the assembled H (all three components, INCLUDING the A_z/W̃ leg that
        // only fires at εr > 1) must be a genuine curl. A wrong sign or missing leg breaks
        // ∇·H ≠ 0 at O(|H|/δ); a correct assembly leaves only the map-grade quadrature
        // floor. Measured ~1e-3 relative; gated 3% (well below the O(1) a bug produces).
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(
            1.186e-2, 0.906e-2, 1.4e-3, z: Balanis.ThicknessMeters, portFraction: 0);
        var table = new LayeredKernelTable(Balanis, BalanisF, 0.05);
        var solution = new SurfaceMomSolver().Solve(grid.Structure!, table, grid.Port!);
        double d = Balanis.ThicknessMeters;
        double delta = 0.2 * d;
        var c = new Vector3D(0.35 * 1.186e-2, 0.15e-2, 1.5 * d);

        Complex Comp(Vector3D p, int axis)
        {
            var m = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, new[] { p });
            var h = m.H![0];
            return axis == 0 ? h.X : axis == 1 ? h.Y : h.Z;
        }
        var dx = new Vector3D(delta, 0, 0);
        var dy = new Vector3D(0, delta, 0);
        var dz = new Vector3D(0, 0, delta);
        Complex divH = (Comp(c + dx, 0) - Comp(c - dx, 0)
                      + Comp(c + dy, 1) - Comp(c - dy, 1)
                      + Comp(c + dz, 2) - Comp(c - dz, 2)) / (2 * delta);
        double hScale = LayeredFieldEvaluator.Evaluate(grid.Structure!, table, solution, new[] { c })
            .HMagnitude![0];
        double relative = divH.Magnitude * delta / Math.Max(hScale, 1e-30);
        Assert.True(relative < 0.03, $"∇·H·δ/|H| = {relative:e3} (should be the quadrature floor)");
    }

    [Fact]
    public void VoltageResidue_MatchesRichardsonExtrapolation()
    {
        // Res_V = Res_Φ·(1 + k₀²(εc−1)Ŝ/(j kz1 N̂)) — gate the identity against a
        // numeric residue: (kρ−kp)·K̃_V(kρ) → Res·2kp/(kρ+kp) form, extrapolated.
        double k0 = 2 * Math.PI * BalanisF / 299_792_458.0;
        var table = new LayeredKernelTable(Balanis, BalanisF, 0.025);
        var pole = table.Poles[0];
        var kp = pole.KRho;

        // The extracted pole term is Res·2kp/(kρ²−kp²): so Res ≈ K̃_V·(kρ²−kp²)/(2kp)
        // as kρ → kp. Richardson over a geometric approach from above.
        Complex Estimate(double eps)
        {
            var kRho = kp * (1 + eps);
            var kz0 = new Complex(0, -1) * Complex.Sqrt(kRho * kRho - k0 * k0);
            return VoltageKernel.Evaluate(Balanis, k0, kRho, kz0)
                   * (kRho * kRho - kp * kp) / (2 * kp);
        }
        var r1 = Estimate(1e-4);
        var r2 = Estimate(5e-5);
        var numeric = 2 * r2 - r1; // first-order Richardson in ε
        var analytic = VoltageKernel.ResidueFromPhi(Balanis, k0, kp, pole.ResiduePhi);
        AssertRel(numeric, analytic, 1e-5, "Res K̃_V");
    }
}
