using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

file static class LayeredConstants
{
    public const double C0 = 299_792_458.0;
    public static readonly double Mu0 = 4e-7 * Math.PI;
    public static readonly double Eps0 = 1.0 / (Mu0 * C0 * C0);

    /// <summary>The Balanis Example 14.1 substrate at 10 GHz — the Stage C reference
    /// stackup (εr 2.2, h 0.1588 cm): thin enough that only TM0 propagates.</summary>
    public static readonly SubstrateStackup Balanis = new(2.2, 0, 1.588e-3);
    public const double BalanisF = 10e9;
    public static double BalanisK0 => 2 * Math.PI * BalanisF / C0;
}

/// <summary>Identity-based oracles for the first-party Bessel evaluations. The series
/// and asymptotic branches share no algebra, so their agreement across the crossover
/// is a real cross-check; J₀ additionally has the integral representation
/// (1/π)∫cos(x sin θ)dθ, whose entire integrand a dense Gauss rule nails to machine
/// precision; and H₀⁽²⁾ (implemented through complex K₀) must reproduce J₀ − jY₀ on
/// the real axis, tying all three implementations together.</summary>
public class BesselTests
{
    private static double J0Integral(double x)
    {
        var (nodes, weights) = GaussLegendre.Rule(200, 0, Math.PI);
        double sum = 0;
        for (int i = 0; i < nodes.Length; i++)
            sum += weights[i] * Math.Cos(x * Math.Sin(nodes[i]));
        return sum / Math.PI;
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(1.0)]
    [InlineData(2.404825557695773)] // first zero of J0
    [InlineData(5.0)]
    [InlineData(13.9)]
    [InlineData(14.1)]
    [InlineData(25.0)]
    [InlineData(60.0)]
    public void J0_MatchesTheIntegralRepresentation(double x)
    {
        // The design floor sits at the x = 14 crossover, ~1e-11: the series loses
        // e^x·ulp to alternating cancellation while the asymptotic optimal-truncation
        // floor is ~e^{−2x} — measured worst 3e-12, gated with margin. Downstream
        // gates are 1e-8 relative, three orders above this.
        Assert.True(Math.Abs(Bessel.J0(x) - J0Integral(x)) < 2e-11,
            $"J0({x}) = {Bessel.J0(x):R} vs oracle {J0Integral(x):R}");
    }

    [Theory]
    [InlineData(12.5)]
    [InlineData(13.5)]
    [InlineData(14.5)]
    [InlineData(15.5)]
    public void SeriesAndAsymptotic_AgreeAcrossTheCrossover(double x)
    {
        // Above the crossover the SERIES side keeps degrading (e^x cancellation) while
        // only the asymptotic is used — the agreement band widens with x accordingly
        // (measured 5.2e-11 at 15.5). What matters is that two algebra-free-of-each-
        // other methods agree at all: a wrong P/Q combination or series term would
        // miss by orders, not by 1e-10.
        Assert.True(Math.Abs(Bessel.J0Series(x) - Bessel.J0Asymptotic(x)) < 2e-10,
            $"J0 branches at {x}: {Bessel.J0Series(x):R} vs {Bessel.J0Asymptotic(x):R}");
        Assert.True(Math.Abs(Bessel.Y0Series(x) - Bessel.Y0Asymptotic(x)) < 2e-10,
            $"Y0 branches at {x}: {Bessel.Y0Series(x):R} vs {Bessel.Y0Asymptotic(x):R}");
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    [InlineData(15.0)]
    [InlineData(40.0)]
    public void K0_MatchesTheIntegralRepresentation_OnTheRealAxis(double x)
    {
        // No point between 4 and 12: there the ascending series' two halves grow like
        // e^x while K0 ~ e^{−x}, cancelling ~2x/ln10 digits — a REAL-argument-only
        // pathology. The pole term evaluates K0 at nearly-IMAGINARY argument
        // (|e^z| ≈ 1, cancellation e^{|Im k_p|ρ} ≈ 1), which the H0² guard enforces;
        // the real-axis points here validate series (≤ 2) and asymptotic (≥ 15) where
        // the real axis is honest.
        // K0(x) = ∫₀^∞ e^{−x cosh t} dt, truncated where the integrand is ~e^{−60} down.
        double tMax = Math.Acosh(1 + 60.0 / x);
        var (nodes, weights) = GaussLegendre.Rule(30);
        double sum = 0;
        const int Panels = 40;
        for (int p = 0; p < Panels; p++)
        {
            double a = tMax * p / Panels, b = tMax * (p + 1) / Panels;
            double mid = 0.5 * (a + b), half = 0.5 * (b - a);
            for (int i = 0; i < nodes.Length; i++)
                sum += weights[i] * half * Math.Exp(-x * Math.Cosh(mid + half * nodes[i]));
        }
        var k0 = Bessel.K0(new Complex(x, 0));
        Assert.True(Math.Abs(k0.Imaginary) < 1e-16 * Math.Abs(k0.Real) + 1e-300);
        Assert.True(Math.Abs(k0.Real - sum) < 1e-11 * sum,
            $"K0({x}) = {k0.Real:R} vs oracle {sum:R}");
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(2.0)]
    [InlineData(9.0)]   // K0 series side of ITS crossover (|jx| = 9)
    [InlineData(20.0)]  // K0 asymptotic side
    [InlineData(80.0)]
    public void H02_EqualsJ0MinusJY0_OnTheRealAxis(double x)
    {
        var h = Bessel.H02(new Complex(x, 0));
        var expected = new Complex(Bessel.J0(x), -Bessel.Y0(x));
        Assert.True((h - expected).Magnitude < 5e-11 * expected.Magnitude,
            $"H02({x}) = {h} vs J0 − jY0 = {expected}");
    }
}

/// <summary>The spectral kernels against an INDEPENDENT numeric re-derivation: the
/// 4×4 spectral boundary-value system for the Sommerfeld potential amplitudes
/// (Ax continuity, the source jump, Az continuity, (1/ε)∇·A continuity, with
/// Ax = ∂A_z/∂z = 0 built into the region-1 basis) solved by dense complex LU per
/// k_ρ — same physics, none of the closed-form algebra. Plus the εr → 1 image limit
/// and the reduced-form overflow safety far up the evanescent tail.</summary>
public class SpectralKernelTests
{
    private static Complex Kz(Complex kSq, Complex kRho)
    {
        var s = Complex.Sqrt(kSq - kRho * kRho);
        return s.Imaginary > 0 ? -s : s;
    }

    private static (Complex GA, Complex KPhi) SolveBoundaryValueProblem(
        SubstrateStackup substrate, double k0, Complex kRho)
    {
        var epsC = substrate.RelativePermittivity * new Complex(1, -substrate.LossTangent);
        var kz0 = Kz(k0 * k0, kRho);
        var kz1 = Kz(epsC * k0 * k0, kRho);
        double d = substrate.ThicknessMeters;
        var s1 = Complex.Sin(kz1 * d);
        var c1 = Complex.Cos(kz1 * d);
        var j = Complex.ImaginaryOne;

        // Unknowns (C, P, Q, S): Ax0 = C e^{−jk_z0(z−d)}, Ax1 = P sin(k_z1 z),
        // Az0 = −jk_x S e^{−jk_z0(z−d)}, Az1 = −jk_x Q cos(k_z1 z).
        var m = new ComplexDenseMatrix(4, 4);
        var rhs = new Complex[4];
        m[0, 0] = 1; m[0, 1] = -s1;                              // Ax continuity
        m[1, 0] = j * kz0; m[1, 1] = kz1 * c1; rhs[1] = 2 * LayeredConstants.Mu0; // ∂zAx jump
        m[2, 2] = -c1; m[2, 3] = 1;                              // Az continuity
        m[3, 0] = 1; m[3, 3] = -j * kz0;                         // (1/ε)∇·A continuity
        m[3, 1] = -s1 / epsC; m[3, 2] = kz1 * s1 / epsC;
        var solution = ComplexLu.Factor(m).Solve(rhs);
        var c = solution[0];
        var sAmp = solution[3];
        return (c, (c - j * kz0 * sAmp) / (LayeredConstants.Mu0 * LayeredConstants.Eps0));
    }

    public static TheoryData<double, double, double> Sweep => new()
    {
        // (εr, tanδ, kρ/k0) — below the branch point, between k0 and k1, evanescent.
        { 2.2, 0, 0.3 }, { 2.2, 0, 0.95 }, { 2.2, 0, 1.2 }, { 2.2, 0, 3.0 }, { 2.2, 0, 10.0 },
        { 2.2, 0.02, 0.3 }, { 2.2, 0.02, 1.2 }, { 2.2, 0.02, 10.0 },
        { 6.0, 0, 0.5 }, { 6.0, 0, 1.8 }, { 6.0, 0, 8.0 },
    };

    [Theory]
    [MemberData(nameof(Sweep))]
    public void Kernels_MatchTheNumericBoundaryValueSolve(double epsR, double tanD, double kRhoOverK0)
    {
        var substrate = new SubstrateStackup(epsR, tanD, 1.588e-3);
        double k0 = LayeredConstants.BalanisK0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var (gA, kPhi) = SpectralKernels.Evaluate(substrate, k0, kRho);
        var (gARef, kPhiRef) = SolveBoundaryValueProblem(substrate, k0, kRho);
        Assert.True((gA - gARef).Magnitude < 1e-12 * gARef.Magnitude,
            $"G_A {gA} vs BVP {gARef}");
        Assert.True((kPhi - kPhiRef).Magnitude < 1e-12 * kPhiRef.Magnitude,
            $"K_Φ {kPhi} vs BVP {kPhiRef}");
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(1.7)]
    [InlineData(12.0)]
    public void EpsilonROne_ReducesToPrimaryPlusGroundImage(double kRhoOverK0)
    {
        var substrate = new SubstrateStackup(1, 0, 2.5e-3);
        double k0 = LayeredConstants.BalanisK0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var j = Complex.ImaginaryOne;
        var closed = (1 - Complex.Exp(-2 * j * kz0 * substrate.ThicknessMeters)) / (j * kz0);
        var (gA, kPhi) = SpectralKernels.Evaluate(substrate, k0, kRho);
        Assert.True((gA - LayeredConstants.Mu0 * closed).Magnitude
                    < 1e-12 * (LayeredConstants.Mu0 * closed).Magnitude);
        Assert.True((kPhi - closed / LayeredConstants.Eps0).Magnitude
                    < 1e-12 * (closed / LayeredConstants.Eps0).Magnitude);
    }

    [Fact]
    public void ReducedTrigForm_SurvivesTheDeepEvanescentTail()
    {
        // k_ρ = 1e6·k0 puts γ₁d ≈ 3e5 — any cosh/coth evaluation overflows here; the
        // reduced form must return the exact algebraic asymptotes instead.
        var substrate = LayeredConstants.Balanis;
        double k0 = LayeredConstants.BalanisK0;
        double kRho = 1e6 * k0;
        var (gA, kPhi) = SpectralKernels.Evaluate(substrate, k0, kRho);
        double expectedA = LayeredConstants.Mu0 / kRho;
        double expectedPhi = 2 / (LayeredConstants.Eps0 * (substrate.RelativePermittivity + 1) * kRho);
        Assert.True((gA - expectedA).Magnitude < 1e-9 * expectedA);
        Assert.True((kPhi - expectedPhi).Magnitude < 1e-9 * expectedPhi);
    }
}

/// <summary>Surface-wave poles: locations against an independent dispersion form and a
/// test-local bisection, mode counts against the cutoff arithmetic, residues against a
/// Richardson limit of (k_ρ − k_p)·K̃, and the closed-form pole transform constant
/// against a brute-force Sommerfeld integral with a well-off-axis pole.</summary>
public class SurfaceWavePoleTests
{
    [Fact]
    public void BalanisSubstrate_HasExactlyTm0_AtTheIndependentRoot()
    {
        var substrate = LayeredConstants.Balanis;
        double k0 = LayeredConstants.BalanisK0;
        var poles = SurfaceWavePoles.Find(substrate, k0);
        var pole = Assert.Single(poles);
        Assert.True(pole.IsTm);
        Assert.Equal(0, pole.KRho.Imaginary);
        Assert.InRange(pole.KRho.Real / k0, 1.0, Math.Sqrt(2.2));

        // Independent form: r(k_ρ) = k_z1·tan(k_z1 d) − εr·γ₀ (no reduced-trig algebra),
        // bisected directly in k_ρ.
        double k1 = k0 * Math.Sqrt(2.2), d = substrate.ThicknessMeters;
        double Dispersion(double kr)
        {
            double kz1 = Math.Sqrt(k1 * k1 - kr * kr);
            double gamma0 = Math.Sqrt(kr * kr - k0 * k0);
            return kz1 * Math.Tan(kz1 * d) - 2.2 * gamma0;
        }
        double lo = k0 * (1 + 1e-12), hi = k1 * (1 - 1e-12);
        Assert.True(Dispersion(lo) > 0 && Dispersion(hi) < 0);
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (mid == lo || mid == hi) break;
            if (Dispersion(mid) > 0) lo = mid; else hi = mid;
        }
        Assert.True(Math.Abs(pole.KRho.Real - 0.5 * (lo + hi)) < 1e-12 * k0,
            $"TM0 at {pole.KRho.Real / k0:R}·k0 vs bisection {(0.5 * (lo + hi)) / k0:R}·k0");
    }

    [Fact]
    public void ThickSlab_FindsTheHigherModes_PerTheCutoffCount()
    {
        // εr = 4, k0·d = 2 ⇒ u_max = 2√3 ≈ 3.46: TM0 + TM1 (u_max > π) + TE1 (> π/2).
        double k0 = LayeredConstants.BalanisK0;
        var substrate = new SubstrateStackup(4, 0, 2.0 / k0);
        var poles = SurfaceWavePoles.Find(substrate, k0);
        Assert.Equal(2, poles.Count(p => p.IsTm));
        Assert.Equal(1, poles.Count(p => !p.IsTm));
        foreach (var pole in poles)
        {
            // Each root must actually zero its dispersion function, judged against the
            // function's own scale a hair off the root.
            var at = pole.IsTm
                ? SpectralKernels.DTm(substrate, k0, pole.KRho)
                : SpectralKernels.DTe(substrate, k0, pole.KRho);
            var near = pole.IsTm
                ? SpectralKernels.DTm(substrate, k0, pole.KRho * 1.01)
                : SpectralKernels.DTe(substrate, k0, pole.KRho * 1.01);
            Assert.True(at.Magnitude < 1e-9 * near.Magnitude,
                $"{(pole.IsTm ? "TM" : "TE")} root residual {at.Magnitude:g3} vs scale {near.Magnitude:g3}");
        }
    }

    [Fact]
    public void LossySubstrate_MovesTm0_IntoThePhysicalHalfPlane()
    {
        double k0 = LayeredConstants.BalanisK0;
        var lossy = new SubstrateStackup(2.2, 0.02, 1.588e-3);
        var pole = Assert.Single(SurfaceWavePoles.Find(lossy, k0));
        Assert.True(pole.KRho.Imaginary < 0, $"Im(k_p) = {pole.KRho.Imaginary:g3}");
        var at = SpectralKernels.DTm(lossy, k0, pole.KRho);
        var near = SpectralKernels.DTm(lossy, k0, pole.KRho * 1.01);
        Assert.True(at.Magnitude < 1e-9 * near.Magnitude);
    }

    [Fact]
    public void Tm0Residue_MatchesTheRichardsonLimit()
    {
        var substrate = LayeredConstants.Balanis;
        double k0 = LayeredConstants.BalanisK0;
        var pole = Assert.Single(SurfaceWavePoles.Find(substrate, k0));

        Complex Limit(double delta)
        {
            var kRho = pole.KRho + delta * k0;
            var (_, kPhi) = SpectralKernels.Evaluate(substrate, k0, kRho);
            return delta * k0 * kPhi;
        }
        // (k_ρ−k_p)·K̃ = Res + O(δ): two-point Richardson kills the linear term.
        var refined = 2 * Limit(5e-5) - Limit(1e-4);
        Assert.True((refined - pole.ResiduePhi).Magnitude < 1e-5 * pole.ResiduePhi.Magnitude,
            $"Res = {pole.ResiduePhi} vs limit {refined}");
    }

    [Fact]
    public void PoleTransformConstant_MatchesBruteForce()
    {
        // (1/4π)∫ Res·2k_p/(k_ρ²−k_p²)·J0(k_ρρ)·k_ρ dk_ρ = −(j/4)·Res·k_p·H0²(k_pρ):
        // pin the constant with a well-off-axis pole where plain panels converge.
        double k = 100.0;
        var kp = k * new Complex(1, -0.05);
        double rho = 2.0 / k;

        var (nodes, weights) = GaussLegendre.Rule(20);
        Complex sum = Complex.Zero;
        double panelWidth = k / 40;
        double reach = 60 * k; // then PE partitions handle the 1/k_ρ·J0 tail
        int panels = (int)Math.Ceiling(reach / panelWidth);
        for (int p = 0; p < panels; p++)
        {
            double a = p * panelWidth, b = (p + 1) * panelWidth;
            double mid = 0.5 * (a + b), half = 0.5 * (b - a);
            for (int i = 0; i < nodes.Length; i++)
            {
                double kr = mid + half * nodes[i];
                sum += weights[i] * half * 2 * kp / (kr * kr - kp * kp) * Bessel.J0(kr * rho) * kr;
            }
        }
        double delta = Math.PI / rho;
        const int Partitions = 14;
        var partials = new Complex[Partitions];
        Complex acc = Complex.Zero;
        for (int n = 0; n < Partitions; n++)
        {
            double a = reach + n * delta, b = reach + (n + 1) * delta;
            double mid = 0.5 * (a + b), half = 0.5 * (b - a);
            Complex v = Complex.Zero;
            for (int i = 0; i < nodes.Length; i++)
            {
                double kr = mid + half * nodes[i];
                v += weights[i] * half * 2 * kp / (kr * kr - kp * kp) * Bessel.J0(kr * rho) * kr;
            }
            acc += v;
            partials[n] = acc;
        }
        for (int m = 1; m < Partitions; m++)
            for (int i = 0; i < Partitions - m; i++)
                partials[i] = 0.5 * (partials[i] + partials[i + 1]);
        var integral = (sum + partials[0]) / (4 * Math.PI);

        var closed = new Complex(0, -0.25) * kp * Bessel.H02(kp * rho);
        Assert.True((integral - closed).Magnitude < 1e-6 * closed.Magnitude,
            $"transform {integral} vs closed form {closed}");
    }
}

/// <summary>The C1/C2 go/no-go gates on the assembled pipeline (poles + images +
/// Sommerfeld remainder + spline table), all anchored at measured values with one to
/// three orders of margin — never at hopes.</summary>
public class LayeredKernelTableTests
{
    private static double Lambda => LayeredConstants.C0 / LayeredConstants.BalanisF;

    /// <summary>Kernel magnitude scale at ρ — gates divide by this, not by possibly
    /// cancelling differences.</summary>
    private static (double A, double Phi) Scales(double rho) =>
        (LayeredConstants.Mu0 / (4 * Math.PI * rho), 1 / (4 * Math.PI * LayeredConstants.Eps0 * rho));

    [Fact]
    public void C1_EpsilonROne_TheRemainderIsMachineZero()
    {
        // εr = 1 makes the dynamic images the EXACT kernel; the full numeric pipeline
        // must integrate its remainder to nothing (measured ~2e-15 of kernel scale).
        var table = new LayeredKernelTable(new SubstrateStackup(1, 0, 0.05 * Lambda),
            LayeredConstants.BalanisF, 2 * Lambda);
        Assert.Equal(0, table.PoleCount);
        foreach (double rho in new[] { 1e-3 * Lambda, 0.01 * Lambda, 0.1 * Lambda, Lambda })
        {
            var (gA, kPhi) = table.EvaluateKernelsDirect(rho);
            var (imA, imPhi) = table.ImageTerms(rho);
            var (scaleA, scalePhi) = Scales(rho);
            Assert.True((gA - imA).Magnitude < 1e-12 * scaleA,
                $"A remainder {(gA - imA).Magnitude / scaleA:e2} of scale at ρ = {rho / Lambda}λ");
            Assert.True((kPhi - imPhi).Magnitude < 1e-12 * scalePhi,
                $"Φ remainder {(kPhi - imPhi).Magnitude / scalePhi:e2} of scale at ρ = {rho / Lambda}λ");
        }
    }

    [Fact]
    public void C1_StaticLimit_MatchesTheClassicalImageSeries()
    {
        // k₀ → 0 (1 kHz on a 1.588 mm slab): K_Φ must reproduce the classical
        // grounded-slab image series (2/(εr+1))[1/ρ − (1+η)Σ(−η)^{n−1}/R_{2nd}] and
        // G_A the exact primary − image (the µ-uniform slab has NO further G_A
        // images — an identity, not an approximation). Measured 5e-11 / 2e-12.
        const double D = 1.588e-3, EpsR = 2.2;
        var table = new LayeredKernelTable(new SubstrateStackup(EpsR, 0, D), 1e3, 100 * D);
        double eta = (EpsR - 1) / (EpsR + 1);
        foreach (double rho in new[] { 0.1 * D, D, 5 * D, 30 * D })
        {
            var (gA, kPhi) = table.EvaluateKernelsDirect(rho);
            double series = 1 / rho;
            for (int n = 1; n < 4000; n++)
            {
                double term = (1 + eta) * Math.Pow(-eta, n - 1)
                              / Math.Sqrt(rho * rho + 4.0 * n * n * D * D);
                series -= term;
                if (Math.Abs(term) < 1e-17 / rho) break;
            }
            double kPhiStatic = series * 2 / ((EpsR + 1) * 4 * Math.PI * LayeredConstants.Eps0);
            double gAStatic = LayeredConstants.Mu0 / (4 * Math.PI)
                              * (1 / rho - 1 / Math.Sqrt(rho * rho + 4 * D * D));
            Assert.True(Math.Abs(kPhi.Real - kPhiStatic) < 1e-9 * kPhiStatic,
                $"K_Φ static rel {(kPhi.Real - kPhiStatic) / kPhiStatic:e2} at ρ = {rho / D}d");
            Assert.True(Math.Abs(kPhi.Imaginary) < 1e-12 * kPhiStatic);
            Assert.True(Math.Abs(gA.Real - gAStatic) < 1e-10 * gAStatic,
                $"G_A static rel {(gA.Real - gAStatic) / gAStatic:e2} at ρ = {rho / D}d");
        }
    }

    [Fact]
    public void C1_SelfConvergence_DoublingEveryKnob_MovesNothing()
    {
        // Refinement 2 doubles panel counts, the head's exponential reach, the tail
        // partitions, and the tail sub-panels. Measured worst movement 4.4e-11 of
        // kernel scale — gated at 1e-8 per the plan.
        var table = new LayeredKernelTable(LayeredConstants.Balanis,
            LayeredConstants.BalanisF, 2 * Lambda);
        foreach (double rho in new[] { 1e-3 * Lambda, 0.01 * Lambda, 0.06 * Lambda, 0.3 * Lambda, 1.5 * Lambda })
        {
            var coarse = table.EvaluateKernelsDirect(rho, 1);
            var fine = table.EvaluateKernelsDirect(rho, 2);
            var (scaleA, scalePhi) = Scales(rho);
            Assert.True((coarse.GA - fine.GA).Magnitude < 1e-8 * scaleA,
                $"G_A moved {(coarse.GA - fine.GA).Magnitude / scaleA:e2} at ρ = {rho / Lambda}λ");
            Assert.True((coarse.KPhi - fine.KPhi).Magnitude < 1e-8 * scalePhi,
                $"K_Φ moved {(coarse.KPhi - fine.KPhi).Magnitude / scalePhi:e2} at ρ = {rho / Lambda}λ");
        }
    }

    [Fact]
    public void Table_MatchesDirectIntegration_OffGrid()
    {
        // The spline against direct integration at deliberately off-grid radii
        // (measured worst 9e-9 of scale, in the far region's oscillating residual).
        var table = new LayeredKernelTable(LayeredConstants.Balanis,
            LayeredConstants.BalanisF, 2 * Lambda);
        for (int i = 0; i < 14; i++)
        {
            double rho = 1.3e-3 * Lambda * Math.Pow(1.9 * Lambda / (1.3e-3 * Lambda), i / 13.0);
            var spline = table.EvaluateKernels(rho);
            var direct = table.EvaluateKernelsDirect(rho, 2);
            var (scaleA, scalePhi) = Scales(rho);
            Assert.True((spline.GA - direct.GA).Magnitude < 1e-7 * scaleA,
                $"A spline error {(spline.GA - direct.GA).Magnitude / scaleA:e2} at ρ = {rho / Lambda}λ");
            Assert.True((spline.KPhi - direct.KPhi).Magnitude < 1e-7 * scalePhi,
                $"Φ spline error {(spline.KPhi - direct.KPhi).Magnitude / scalePhi:e2} at ρ = {rho / Lambda}λ");
        }
    }

    [Fact]
    public void C2_SurfaceWave_DominatesTheFarKernel_WithTheMeasuredTrend()
    {
        // The plan's provisional "pole ≡ kernel to 1% at 10λ" figure measured at
        // 7.6e-2 — NOT extraction error, but the legitimate space wave (the 2d image
        // ~1/ρ and the branch-cut residual ~1/ρ²) which is part of the physical
        // kernel and decays strictly faster than the pole's 1/√ρ. The honest gate is
        // one-sided-with-trend: bounded at 10λ, and halving-ish per doubling of ρ
        // (measured 0.455 then 0.409). A WRONG residue leaves pole content in the
        // remainder and freezes this ratio — exactly what the trend gate catches.
        var table = new LayeredKernelTable(LayeredConstants.Balanis,
            LayeredConstants.BalanisF, 2 * Lambda);
        Assert.Equal(1, table.PoleCount);
        double Ratio(double rho)
        {
            var (_, kPhi) = table.EvaluateKernelsDirect(rho, 2);
            var (_, polePhi) = table.PoleTerms(rho);
            return (kPhi - polePhi).Magnitude / polePhi.Magnitude;
        }
        double r10 = Ratio(10 * Lambda), r20 = Ratio(20 * Lambda), r40 = Ratio(40 * Lambda);
        Assert.True(r10 < 0.10, $"space-wave/pole ratio {r10:e2} at 10λ");
        Assert.InRange(r20 / r10, 0.30, 0.60);
        Assert.InRange(r40 / r20, 0.30, 0.60);
    }

    [Fact]
    public void TableBuild_IsBitwiseDeterministic()
    {
        var first = new LayeredKernelTable(LayeredConstants.Balanis,
            LayeredConstants.BalanisF, 2 * Lambda);
        var second = new LayeredKernelTable(LayeredConstants.Balanis,
            LayeredConstants.BalanisF, 2 * Lambda);
        for (int i = 0; i < 9; i++)
        {
            double rho = 1.7e-3 * Lambda * Math.Pow(1000, i / 8.0);
            Assert.Equal(first.EvaluateSmooth(rho), second.EvaluateSmooth(rho));
        }
    }

    [Fact]
    public void Stackup_RejectsNonPhysicalInputs_Loudly()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubstrateStackup(0.9, 0, 1e-3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubstrateStackup(2.2, -0.01, 1e-3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubstrateStackup(2.2, 0, 0));
    }
}
