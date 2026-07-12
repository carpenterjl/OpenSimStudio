using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage E checkpoint E1: the vertical-current spectral kernels gated against
/// independent boundary-value re-derivations BEFORE any basis/junction work. Two
/// oracles, both plain (non-reduced) trig with dense LU, sharing no algebra with
/// <see cref="OpenSim.Rf.Layered"/>:
///  - the VERTICAL oracle solves the classical TM problem (Ã_z only) for a ẑ dipole
///    at interior z′ and produces the gauge-INDEPENDENT fields Ẽ_z, Ẽ_x — the
///    formulation-C split (G̃_zz, G̃_xz, shared K̃_Φ) must reassemble them exactly;
///  - the HORIZONTAL oracle solves the x̂-dipole-at-z′ problem and yields the
///    two-height scalar kernel directly (Φ per unit charge), pinning K̃_Φ(z,z′)
///    itself — formulation C's source-independence is that ONE kernel serving both.
/// </summary>
public class VerticalKernelTests
{
    private static readonly SubstrateStackup Balanis = new(2.2, 0.0, 1.588e-3);
    private const double F = 10e9;
    private const double C0 = 299_792_458.0;
    private static readonly double Mu0 = 4e-7 * Math.PI;
    private static readonly double Eps0 = 1.0 / (Mu0 * C0 * C0);
    private static double K0 => 2 * Math.PI * F / C0;
    private static double K1 => K0 * Math.Sqrt(2.2);

    // ── the independent oracles ──────────────────────────────────────────────

    /// <summary>Classical TM solve for a unit ẑ dipole at z′ ∈ (0, d): unknowns
    /// [B, F, G, S] for A_z = B·cos(k_z1 z) below the source, F·cos + G·sin between
    /// source and interface, S·e^{−jk_z0(z−d)} above. Conditions: continuity + the
    /// −2µ₀ derivative jump at z′; A_z and (1/ε)∂_zA_z continuous at d (∂_zA_z(0) = 0
    /// is built into the cos). Returns the potential, the classical gauge potential
    /// Φ = −∂_zA_z/(jωµ₀ε₀ε), and the FIELD Ẽ_z = −jω(k_ρ²/k_i²)·A_z (valid z ≠ z′);
    /// Ẽ_x/(−jk_x) is −Φ. Plain trig — valid while |Im k_z1|·d stays far from
    /// overflow, which the chosen k_ρ grid respects.</summary>
    private static (Func<double, Complex> Az, Func<double, Complex> Phi, Func<double, Complex> Ez)
        VerticalOracle(SubstrateStackup sub, double k0, double kRho, double zPrime)
    {
        var epsC = sub.RelativePermittivity * new Complex(1, -sub.LossTangent);
        var kz0 = BranchSqrt(k0 * k0 - kRho * kRho);
        var kz1 = BranchSqrt(epsC * k0 * k0 - kRho * kRho);
        double d = sub.ThicknessMeters;
        double omega = k0 * C0;
        var j = Complex.ImaginaryOne;
        var s1 = Complex.Sin(kz1 * d);
        var c1 = Complex.Cos(kz1 * d);
        var sP = Complex.Sin(kz1 * zPrime);
        var cP = Complex.Cos(kz1 * zPrime);

        var m = new ComplexDenseMatrix(4, 4);
        var rhs = new Complex[4];
        // Continuity at z′.
        m[0, 0] = cP; m[0, 1] = -cP; m[0, 2] = -sP;
        // ∂z jump at z′: [∂z above] − [∂z below] = −2µ₀.
        m[1, 0] = kz1 * sP; m[1, 1] = -kz1 * sP; m[1, 2] = kz1 * cP; rhs[1] = -2 * Mu0;
        // A_z continuity at d.
        m[2, 1] = c1; m[2, 2] = s1; m[2, 3] = -1;
        // (1/ε)∂zA_z continuity at d.
        m[3, 1] = -kz1 * s1 / epsC; m[3, 2] = kz1 * c1 / epsC; m[3, 3] = j * kz0;
        var x = ComplexLu.Factor(m).Solve(rhs);
        Complex b = x[0], f = x[1], g = x[2], sAmp = x[3];

        Complex Az(double z) => z < zPrime
            ? b * Complex.Cos(kz1 * z)
            : z < d
                ? f * Complex.Cos(kz1 * z) + g * Complex.Sin(kz1 * z)
                : sAmp * Complex.Exp(-j * kz0 * (z - d));
        Complex Phi(double z) => z < zPrime
            ? b * kz1 * Complex.Sin(kz1 * z) / (j * omega * Mu0 * Eps0 * epsC)
            : z < d
                ? kz1 * (f * Complex.Sin(kz1 * z) - g * Complex.Cos(kz1 * z)) / (j * omega * Mu0 * Eps0 * epsC)
                : j * kz0 * sAmp * Complex.Exp(-j * kz0 * (z - d)) / (j * omega * Mu0 * Eps0);
        Complex Ez(double z) => -j * omega * kRho * kRho * Az(z)
            / (z < d ? epsC * k0 * k0 : k0 * k0);
        return (Az, Phi, Ez);
    }

    /// <summary>Classical solve for a unit x̂ dipole at z′ ∈ (0, d): A_x (three-piece)
    /// plus the interface-driven A_z coupling W (per −jk_x; cos in the slab from the
    /// PEC condition, no source jump of its own). Unknowns [Pb, Pc, Ps, Cx, R, Sz].
    /// Yields the two-height scalar kernel K̃_Φ(z,z′) = [A_x + ∂zW]/(µ₀ε₀ε(z)) per
    /// unit charge and the coupling profile W(z).</summary>
    private static (Func<double, Complex> KPhi, Func<double, Complex> W)
        HorizontalOracle(SubstrateStackup sub, double k0, double kRho, double zPrime)
    {
        var epsC = sub.RelativePermittivity * new Complex(1, -sub.LossTangent);
        var kz0 = BranchSqrt(k0 * k0 - kRho * kRho);
        var kz1 = BranchSqrt(epsC * k0 * k0 - kRho * kRho);
        double d = sub.ThicknessMeters;
        var j = Complex.ImaginaryOne;
        var s1 = Complex.Sin(kz1 * d);
        var c1 = Complex.Cos(kz1 * d);
        var sP = Complex.Sin(kz1 * zPrime);
        var cP = Complex.Cos(kz1 * zPrime);

        var m = new ComplexDenseMatrix(6, 6);
        var rhs = new Complex[6];
        // A_x continuity at z′ (below: Pb·sin — A_x(0) = 0 on the PEC).
        m[0, 0] = sP; m[0, 1] = -cP; m[0, 2] = -sP;
        // ∂zA_x jump at z′.
        m[1, 0] = -kz1 * cP; m[1, 1] = -kz1 * sP; m[1, 2] = kz1 * cP; rhs[1] = -2 * Mu0;
        // A_x continuity at d.
        m[2, 1] = c1; m[2, 2] = s1; m[2, 3] = -1;
        // ∂zA_x continuity at d (no source there).
        m[3, 1] = -kz1 * s1; m[3, 2] = kz1 * c1; m[3, 3] = j * kz0;
        // A_z continuity at d (slab: R·cos — ∂zA_z(0) = 0 on the PEC).
        m[4, 4] = c1; m[4, 5] = -1;
        // Φ continuity at d: [A_x + ∂zA_z]/ε matches across.
        m[5, 1] = c1 / epsC; m[5, 2] = s1 / epsC; m[5, 4] = -kz1 * s1 / epsC;
        m[5, 3] = -1; m[5, 5] = j * kz0;
        var x = ComplexLu.Factor(m).Solve(rhs);
        Complex pb = x[0], pc = x[1], ps = x[2], cx = x[3], r = x[4], sz = x[5];

        Complex Ax(double z) => z < zPrime
            ? pb * Complex.Sin(kz1 * z)
            : z < d
                ? pc * Complex.Cos(kz1 * z) + ps * Complex.Sin(kz1 * z)
                : cx * Complex.Exp(-j * kz0 * (z - d));
        Complex W(double z) => z < d
            ? r * Complex.Cos(kz1 * z)
            : sz * Complex.Exp(-j * kz0 * (z - d));
        Complex KPhi(double z) => z < d
            ? (Ax(z) - kz1 * r * Complex.Sin(kz1 * z)) / (Mu0 * Eps0 * epsC)
            : (cx - j * kz0 * sz) * Complex.Exp(-j * kz0 * (z - d)) / (Mu0 * Eps0);
        return (KPhi, W);
    }

    private static Complex BranchSqrt(Complex v)
    {
        var r = Complex.Sqrt(v);
        return r.Imaginary > 0 ? -r : r;
    }

    private static void AssertRel(Complex expected, Complex actual, double tol, string what)
    {
        double scale = Math.Max(expected.Magnitude, actual.Magnitude);
        if (scale == 0) { Assert.Equal(expected, actual); return; }
        double rel = (expected - actual).Magnitude / scale;
        Assert.True(rel <= tol, $"{what}: oracle {expected}, closed form {actual} (rel {rel:e2})");
    }

    /// <summary>The oracle-gated spectral grid tops out at 8k₁ (κ·d ≈ 4): the plain-trig
    /// oracle's TWO-HEIGHT solve cancels amplitudes ~e^{+κd} against each other to
    /// produce answers ~e^{−κ|z−z′|} (error ~ε·e^{~1.7κd}, measured 5.8e-2 at 40k₁ for
    /// the widest height split — the source-at-d Stage D oracle never had interior-point
    /// cancellation). The production kernels are reduced-trig exact up there; the deep
    /// tail is covered by the oracle-free gates: the εr = 1 exact identity, the 10⁶k₀
    /// overflow gate, and the measured quasi-static image decay at 40k₁/80k₁.</summary>
    public static IEnumerable<object[]> SpectralSamples()
    {
        double k0 = K0, k1 = K1;
        foreach (double kRho in new[] { 0.3 * k0, 0.999 * k0, 0.5 * (k0 + k1), 1.2 * k1, 5 * k1, 8 * k1 })
            foreach (double zPrimeFrac in new[] { 0.15, 0.4, 0.65, 0.9, 0.97 })
                yield return new object[] { kRho, zPrimeFrac };
    }

    private static readonly double[] ObservationFractions = { 0.0, 0.2, 0.45, 0.7, 0.99, 1.0, 1.5, 2.5 };

    // ── E1 gates ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(SpectralSamples))]
    public void VerticalFields_ReassembleTheBvpOracle_TheFormulationCGate(double kRho, double zPrimeFrac)
    {
        // THE formulation-C exactness gate: Ẽ_z = −jωG̃_zz − (1/jω)∂z∂z′K̃_Φ and
        // Ẽ_x/(−jk_x) = −jωG̃_xz − (1/jω)∂z′K̃_Φ against the oracle's gauge-independent
        // fields. The oracle knows nothing about the Δ-correction split — if the
        // correction (or its sign) were wrong, both legs fail at O(εc−1), not 1e-12.
        double k0 = K0, d = Balanis.ThicknessMeters, omega = k0 * C0;
        double zPrime = zPrimeFrac * d;
        var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
        var j = Complex.ImaginaryOne;
        var (_, oPhi, oEz) = VerticalOracle(Balanis, k0, kRho, zPrime);

        foreach (double zFrac in ObservationFractions)
        {
            double z = zFrac * d;
            if (z == zPrime) continue;
            var (gzz, gxz, _) = VerticalSpectralKernels.Evaluate(Balanis, k0, kRho, kz0, z, zPrime);
            var (dzp, both) = VerticalSpectralKernels.ChargeGradients(Balanis, k0, kRho, kz0, z, zPrime);
            var ez = -j * omega * gzz - both / (j * omega);
            var ex = -j * omega * gxz - dzp / (j * omega);
            AssertRel(oEz(z), ez, 1e-12, $"Ẽ_z(kρ={kRho:g4}, z={zFrac}d, z′={zPrimeFrac}d)");
            AssertRel(-oPhi(z), ex, 1e-12, $"Ẽ_x(kρ={kRho:g4}, z={zFrac}d, z′={zPrimeFrac}d)");
        }
    }

    [Theory]
    [MemberData(nameof(SpectralSamples))]
    public void TwoHeightScalarKernel_MatchesTheHorizontalOracle_SourceIndependence(
        double kRho, double zPrimeFrac)
    {
        // K̃_Φ(z,z′) is DEFINED by horizontal-current charges (Φ = −∇·A/(jωµε) of the
        // x̂-dipole-at-z′ problem) and REUSED verbatim for the vertical current's
        // charges — matching the independent horizontal oracle pins the kernel itself,
        // and the field gate above proves the same kernel serves the vertical source.
        double k0 = K0, d = Balanis.ThicknessMeters;
        double zPrime = zPrimeFrac * d;
        var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
        var (oKPhi, _) = HorizontalOracle(Balanis, k0, kRho, zPrime);

        foreach (double zFrac in ObservationFractions)
        {
            double z = zFrac * d;
            var (_, _, kPhi) = VerticalSpectralKernels.Evaluate(Balanis, k0, kRho, kz0, z, zPrime);
            AssertRel(oKPhi(z), kPhi, 1e-12, $"K̃_Φ(kρ={kRho:g4}, z={zFrac}d, z′={zPrimeFrac}d)");
        }
    }

    [Theory]
    [MemberData(nameof(SpectralSamples))]
    public void CouplingKernel_IsMinusTheTransposeOfTheHorizontalCoupling(double kRho, double zPrimeFrac)
    {
        // G̃_A^xz(z,z′) = −W̃(z′,z): the formulation-C absorption of the gauge mismatch
        // lands EXACTLY on minus the transpose of the existing horizontal→A_z coupling.
        // W̃(z′,z) means: A_z at height z′ due to a horizontal source at height z — so
        // the oracle solves with the source at z (needs z inside the slab).
        double k0 = K0, d = Balanis.ThicknessMeters;
        double zPrime = zPrimeFrac * d;
        var kz0 = SpectralKernels.Kz(k0 * k0, kRho);

        foreach (double zFrac in new[] { 0.2, 0.45, 0.7, 0.99 })
        {
            double z = zFrac * d;
            var (_, gxz, _) = VerticalSpectralKernels.Evaluate(Balanis, k0, kRho, kz0, z, zPrime);
            var (_, oW) = HorizontalOracle(Balanis, k0, kRho, z);
            AssertRel(-oW(zPrime), gxz, 1e-12, $"G̃_xz(kρ={kRho:g4}, z={zFrac}d, z′={zPrimeFrac}d)");
        }
    }

    [Fact]
    public void KPhiAtSourceHeightD_IsTheStageDKernel()
    {
        // The z′ → d edge of the two-height kernel must be the Stage C/D kernel
        // exactly (the ε_c cancellation D̂_TM + j(ε_c−1)k_z1Ŝ_d = ε_cN̂ is algebra,
        // but the code paths differ — hence a tight relative gate, not bitwise).
        double k0 = K0, d = Balanis.ThicknessMeters, k1 = K1;
        foreach (double kRho in new[] { 0.3 * K0, 1.2 * k1, 5 * k1, 40 * k1 })
        {
            var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
            foreach (double zFrac in ObservationFractions)
            {
                double z = zFrac * d;
                var (_, _, kPhi) = VerticalSpectralKernels.Evaluate(Balanis, k0, kRho, kz0, z, d);
                var (_, _, stageD) = SpectralProfiles.Evaluate(Balanis, k0, kRho, kz0, z);
                AssertRel(stageD, kPhi, 1e-14, $"K̃_Φ(z={zFrac}d, z′=d) vs Stage D");
            }
        }
    }

    [Fact]
    public void EpsilonOne_CollapsesToFreeSpacePlusImages()
    {
        // εr = 1: G̃_zz is EXACTLY primary + POSITIVE image at z+z′ (a vertical dipole
        // images positively over PEC), K̃_Φ primary + NEGATIVE image, and the coupling
        // vanishes identically (its (ε_c−1) factor is structural).
        var air = new SubstrateStackup(1.0, 0.0, Balanis.ThicknessMeters);
        double k0 = K0, d = air.ThicknessMeters;
        var j = Complex.ImaginaryOne;
        foreach (double kRho in new[] { 0.3 * K0, 0.999 * K0, 1.2 * K1, 5 * K1, 40 * K1 })
        {
            var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
            foreach (double zPrimeFrac in new[] { 0.15, 0.5, 0.9 })
                foreach (double zFrac in ObservationFractions)
                {
                    double z = zFrac * d, zPrime = zPrimeFrac * d;
                    var (gzz, gxz, kPhi) = VerticalSpectralKernels.Evaluate(air, k0, kRho, kz0, z, zPrime);
                    var primary = Complex.Exp(-j * kz0 * Math.Abs(z - zPrime)) / (j * kz0);
                    var image = Complex.Exp(-j * kz0 * (z + zPrime)) / (j * kz0);
                    AssertRel(Mu0 * (primary + image), gzz, 1e-13, $"G̃_zz εr=1 (kρ={kRho:g4})");
                    AssertRel((primary - image) / Eps0, kPhi, 1e-13, $"K̃_Φ εr=1 (kρ={kRho:g4})");
                    Assert.Equal(Complex.Zero, gxz);
                }
        }
    }

    [Fact]
    public void PecConditions_AreExactAtTheGround()
    {
        // On the PEC: Φ(0) = 0 and the tangential field Ẽ_x(0) = 0 — both structural
        // (every term carries sin(k_z1·0)), asserted as exact zeros, not tolerances.
        double k0 = K0, d = Balanis.ThicknessMeters, omega = k0 * C0;
        var j = Complex.ImaginaryOne;
        foreach (double kRho in new[] { 0.3 * K0, 1.2 * K1, 5 * K1 })
        {
            var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
            foreach (double zPrimeFrac in new[] { 0.15, 0.5, 0.9 })
            {
                double zPrime = zPrimeFrac * d;
                var (_, gxz, kPhi) = VerticalSpectralKernels.Evaluate(Balanis, k0, kRho, kz0, 0, zPrime);
                var (dzp, _) = VerticalSpectralKernels.ChargeGradients(Balanis, k0, kRho, kz0, 0, zPrime);
                Assert.Equal(Complex.Zero, kPhi);
                Assert.Equal(Complex.Zero, gxz);
                Assert.Equal(Complex.Zero, -j * omega * gxz - dzp / (j * omega));
            }
        }
    }

    [Fact]
    public void DeepEvanescentTail_IsOverflowSafe()
    {
        // The reduced-trig discipline must hold at k_ρ ~ 10⁶k₀ for BOTH heights free —
        // any lone growing exponential in the two-height forms overflows to Inf/NaN
        // here long before the Sommerfeld tail would stop sampling.
        double k0 = K0, d = Balanis.ThicknessMeters;
        double kRho = 1e6 * k0;
        var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
        foreach (double zPrimeFrac in new[] { 0.15, 0.5, 0.97 })
            foreach (double zFrac in ObservationFractions)
            {
                double z = zFrac * d, zPrime = zPrimeFrac * d;
                var (gzz, gxz, kPhi) = VerticalSpectralKernels.Evaluate(Balanis, k0, kRho, kz0, z, zPrime);
                AssertFinite(gzz, "G̃_zz");
                AssertFinite(gxz, "G̃_xz");
                AssertFinite(kPhi, "K̃_Φ");
                if (z != zPrime)
                {
                    var (dzp, both) = VerticalSpectralKernels.ChargeGradients(Balanis, k0, kRho, kz0, z, zPrime);
                    AssertFinite(dzp, "∂z′K̃_Φ");
                    AssertFinite(both, "∂z∂z′K̃_Φ");
                }
            }

        static void AssertFinite(Complex v, string what) =>
            Assert.True(double.IsFinite(v.Real) && double.IsFinite(v.Imaginary),
                $"{what} is not finite on the deep evanescent tail ({v}).");
    }

    [Fact]
    public void LargeKRho_ApproachesTheQuasiStaticImages_MeasuredDecay()
    {
        // Extraction preview: after removing the in-slab primary and the PEC image
        // (coefficients +1/+1 for G_zz, +1/−1 for K_Φ, both over k_z1), the residual is
        // the dielectric-interface family at height 2d−z−z′ — gate its size AND that it
        // keeps decaying (a wrong image coefficient freezes the ratio, the Stage C tell).
        double k0 = K0, d = Balanis.ThicknessMeters;
        double z = 0.6 * d, zPrime = 0.3 * d;
        var j = Complex.ImaginaryOne;

        double ResidualG(double kRho)
        {
            var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
            var epsC = new Complex(Balanis.RelativePermittivity, 0);
            var kz1 = SpectralKernels.Kz(epsC * k0 * k0, kRho);
            var (gzz, _, _) = VerticalSpectralKernels.Evaluate(Balanis, k0, kRho, kz0, z, zPrime);
            var expected = Mu0 * (Complex.Exp(-j * kz1 * Math.Abs(z - zPrime))
                + Complex.Exp(-j * kz1 * (z + zPrime))) / (j * kz1);
            return ((gzz - expected) / gzz).Magnitude;
        }
        double ResidualPhi(double kRho)
        {
            var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
            var epsC = new Complex(Balanis.RelativePermittivity, 0);
            var kz1 = SpectralKernels.Kz(epsC * k0 * k0, kRho);
            var (_, _, kPhi) = VerticalSpectralKernels.Evaluate(Balanis, k0, kRho, kz0, z, zPrime);
            var expected = (Complex.Exp(-j * kz1 * Math.Abs(z - zPrime))
                - Complex.Exp(-j * kz1 * (z + zPrime))) / (j * kz1 * Eps0 * epsC);
            return ((kPhi - expected) / kPhi).Magnitude;
        }

        double g40 = ResidualG(40 * K1), g80 = ResidualG(80 * K1);
        double p40 = ResidualPhi(40 * K1), p80 = ResidualPhi(80 * K1);
        Assert.True(g40 <= 1e-6, $"G̃_zz two-image residual at 40k₁: {g40:e2}");
        Assert.True(p40 <= 1e-6, $"K̃_Φ two-image residual at 40k₁: {p40:e2}");
        Assert.True(g80 <= 1e-3 * g40, $"G̃_zz residual not decaying: {g40:e2} → {g80:e2}");
        Assert.True(p80 <= 1e-3 * p40, $"K̃_Φ residual not decaying: {p40:e2} → {p80:e2}");
    }

    [Fact]
    public void ChargeGradients_RefuseTheKink()
    {
        double k0 = K0, d = Balanis.ThicknessMeters;
        var kz0 = SpectralKernels.Kz(k0 * k0, 2 * K1);
        Assert.Throws<InvalidOperationException>(() =>
            VerticalSpectralKernels.ChargeGradients(Balanis, k0, 2 * K1, kz0, 0.4 * d, 0.4 * d));
    }

    [Fact]
    public void Heights_AreGuardedLoudly()
    {
        double k0 = K0, d = Balanis.ThicknessMeters;
        var kz0 = SpectralKernels.Kz(k0 * k0, 2 * K1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VerticalSpectralKernels.Evaluate(Balanis, k0, 2 * K1, kz0, 0.5 * d, 1.5 * d));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VerticalSpectralKernels.Evaluate(Balanis, k0, 2 * K1, kz0, -0.1 * d, 0.5 * d));
    }
}
