using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Exact spectral-domain MPIE kernels (Michalski–Mosig formulation C) for a grounded
/// dielectric slab with source AND observation at the top surface z = d. Convention:
///
///     G(ρ) = (1/4π) ∫₀^∞ G̃(k_ρ) J₀(k_ρ ρ) k_ρ dk_ρ,   time factor e^{+jωt},
///
/// so the free-space vector potential is G̃_A = µ₀/(j k_z0) (the Sommerfeld identity).
/// Derived by matching Sommerfeld potentials (Ax, Az) across the interface with
/// Ax = ∂A_z/∂z = 0 … i.e. Ax = 0 and ∂_z A_z = 0 on the PEC — the closed forms are
///
///     G̃_A^xx = 2µ₀ / D_TE,                    D_TE = j k_z0 + k_z1 cot(k_z1 d)
///     K̃_Φ    = (2/ε₀) · N / (D_TE · D_TM),    N    = k_z0 + j k_z1 tan(k_z1 d)
///                                              D_TM = ε_c k_z0 + j k_z1 tan(k_z1 d)
///
/// with k_z = √(k² − k_ρ²) on the Im(k_z) ≤ 0 branch and ε_c = εr(1 − j tanδ). The
/// εr → 1 limit collapses BOTH to (1 − e^{−2j k_z0 d})·(free-space kernel) — exactly
/// the primary + negative image at 2d that the PEC ground demands — and that limit is
/// a hard test gate, alongside an independent numeric boundary-value re-derivation.
///
/// Numerics: everything is evaluated in REDUCED trig form. With z = k_z1 d and
/// E = e^{−jz} (|E| ≤ 1 on our branch), sin z = e^{jz}·S and cos z = e^{jz}·C for
/// S = (1 − E²)/2j, C = (1 + E²)/2; the kernels are homogeneous ratios in (sin, cos),
/// so the growing e^{jz} cancels ALGEBRAICALLY and no coth/cosh ever overflows, no
/// matter how far up the k_ρ tail the Sommerfeld integrator reaches.
/// </summary>
internal static class SpectralKernels
{
    /// <summary>ε_c = εr(1 − j·tanδ): the e^{+jωt} lossy-dielectric convention.</summary>
    public static Complex ComplexPermittivity(SubstrateStackup substrate) =>
        substrate.RelativePermittivity * new Complex(1, -substrate.LossTangent);

    /// <summary>k_z = √(k² − k_ρ²) on the radiation/decay branch Im(k_z) ≤ 0
    /// (e^{−j k_z z} then decays upward for evanescent k_ρ).</summary>
    public static Complex Kz(Complex kSquared, Complex kRho)
    {
        var s = Complex.Sqrt(kSquared - kRho * kRho);
        return s.Imaginary > 0 ? -s : s;
    }

    /// <summary>Both spectral kernels at one k_ρ (they share every subexpression).
    /// <paramref name="k0"/> is the free-space wavenumber ω/c.</summary>
    public static (Complex GA, Complex KPhi) Evaluate(
        SubstrateStackup substrate, double k0, Complex kRho) =>
        Evaluate(substrate, k0, kRho, Kz(k0 * k0, kRho));

    /// <summary>The overload the Sommerfeld path integrator uses: it parametrizes the
    /// contour so k_z0 is known IN CLOSED FORM (k₀cos t on the propagating segment,
    /// −js beyond), and passing that value avoids the catastrophic k₀² − k_ρ²
    /// cancellation right at the branch point that recomputing k_z0 from k_ρ incurs.</summary>
    public static (Complex GA, Complex KPhi) Evaluate(
        SubstrateStackup substrate, double k0, Complex kRho, Complex kz0)
    {
        var epsC = ComplexPermittivity(substrate);
        double k0Sq = k0 * k0;
        var kz1 = Kz(epsC * k0Sq, kRho);
        var (s, c) = ReducedTrig(kz1 * substrate.ThicknessMeters);

        // D_TE in reduced form (the common e^{jz} of numerator and denominator cancels):
        var dTe = Complex.ImaginaryOne * kz0 * s + kz1 * c;
        var gA = 2 * RfConstants.Mu0 * s / dTe;

        var n = kz0 * c + Complex.ImaginaryOne * kz1 * s;
        var dTm = epsC * kz0 * c + Complex.ImaginaryOne * kz1 * s;
        var kPhi = (2 / RfConstants.Eps0) * s * n / (dTe * dTm);
        return (gA, kPhi);
    }

    /// <summary>The reduced TM dispersion function D̂_TM = ε_c k_z0 cos + j k_z1 sin
    /// (up to the never-vanishing e^{jz} factor) — its zeros ARE the TM surface-wave
    /// poles of K̃_Φ. Exposed for the pole finder and its residues.</summary>
    public static Complex DTm(SubstrateStackup substrate, double k0, Complex kRho)
    {
        var epsC = ComplexPermittivity(substrate);
        double k0Sq = k0 * k0;
        var kz0 = Kz(k0Sq, kRho);
        var kz1 = Kz(epsC * k0Sq, kRho);
        var (s, c) = ReducedTrig(kz1 * substrate.ThicknessMeters);
        return epsC * kz0 * c + Complex.ImaginaryOne * kz1 * s;
    }

    /// <summary>The reduced TE dispersion function D̂_TE = j k_z0 sin + k_z1 cos —
    /// its zeros are the TE surface-wave poles (of both kernels).</summary>
    public static Complex DTe(SubstrateStackup substrate, double k0, Complex kRho)
    {
        var epsC = ComplexPermittivity(substrate);
        double k0Sq = k0 * k0;
        var kz0 = Kz(k0Sq, kRho);
        var kz1 = Kz(epsC * k0Sq, kRho);
        var (s, c) = ReducedTrig(kz1 * substrate.ThicknessMeters);
        return Complex.ImaginaryOne * kz0 * s + kz1 * c;
    }

    /// <summary>W̃ = S/C: the ratio of the region-0 A_z amplitude (per −j·k⃗_ρ·J̃) to the
    /// A_x amplitude, from the boundary-value solve: W̃ = (ε_c − 1)·cos(k_z1 d)/(j·D_TM)
    /// in reduced trig (vanishes at εr = 1 — no TM coupling without a dielectric
    /// contrast). The far-field evaluator needs it: the stationary-phase E_θ carries
    /// the factor (cosθ + j k₀ sin²θ · W̃).</summary>
    public static Complex AzRatio(SubstrateStackup substrate, double k0, Complex kRho, Complex kz0)
    {
        var epsC = ComplexPermittivity(substrate);
        var kz1 = Kz(epsC * k0 * k0, kRho);
        var (s, c) = ReducedTrig(kz1 * substrate.ThicknessMeters);
        var dTm = epsC * kz0 * c + Complex.ImaginaryOne * kz1 * s;
        return (epsC - 1) * c / (Complex.ImaginaryOne * dTm);
    }

    /// <summary>Every reduced dispersion quantity plus the ANALYTIC k_ρ derivatives of
    /// D̂_TE and D̂_TM — the pole residues are Res[F̃] = numerator/(D′·other D), and an
    /// analytic derivative keeps them deterministic to machine precision (chain rule
    /// through k_z′ = −k_ρ/k_z, z′ = d·k_z1′, E′ = −j z′E, S′ = z′E², C′ = −j z′E²).</summary>
    public static (Complex DTe, Complex DTm, Complex DTePrime, Complex DTmPrime,
        Complex S, Complex N) Dispersion(SubstrateStackup substrate, double k0, Complex kRho)
    {
        var epsC = ComplexPermittivity(substrate);
        double k0Sq = k0 * k0;
        var kz0 = Kz(k0Sq, kRho);
        var kz1 = Kz(epsC * k0Sq, kRho);
        double d = substrate.ThicknessMeters;
        var (s, c) = ReducedTrig(kz1 * d);
        var j = Complex.ImaginaryOne;

        var dTe = j * kz0 * s + kz1 * c;
        var dTm = epsC * kz0 * c + j * kz1 * s;
        var n = kz0 * c + j * kz1 * s;

        var kz0Prime = -kRho / kz0;
        var kz1Prime = -kRho / kz1;
        var zPrime = d * kz1Prime;
        var e = Complex.Exp(-j * kz1 * d);
        var e2 = e * e;
        var sPrime = zPrime * e2;
        var cPrime = -j * zPrime * e2;

        var dTePrime = j * (kz0Prime * s + kz0 * sPrime) + kz1Prime * c + kz1 * cPrime;
        var dTmPrime = epsC * (kz0Prime * c + kz0 * cPrime) + j * (kz1Prime * s + kz1 * sPrime);
        return (dTe, dTm, dTePrime, dTmPrime, s, n);
    }

    /// <summary>(sin z, cos z) divided by the common growing factor e^{jz}: with
    /// E = e^{−jz} this is S = (1 − E²)/2j, C = (1 + E²)/2. On the Im(z) ≤ 0 branch
    /// |E| ≤ 1, so the pair is overflow-safe for arbitrarily evanescent k_z1. The
    /// e^{jz} scale is common to BOTH members, so any ratio homogeneous of equal
    /// degree in (sin, cos) — every kernel here — is exact.</summary>
    internal static (Complex S, Complex C) ReducedTrig(Complex z)
    {
        // Guard the branch assumption rather than silently mis-scaling: the kernels
        // only ever call this with Im(z) ≤ 0 (the Kz branch), where E decays.
        var e = Complex.Exp(-Complex.ImaginaryOne * z);
        if (double.IsInfinity(e.Real) || double.IsInfinity(e.Imaginary)
            || double.IsNaN(e.Real) || double.IsNaN(e.Imaginary))
            throw new InvalidOperationException(
                $"ReducedTrig was called off the Im(z) ≤ 0 branch (z = {z}).");
        var e2 = e * e;
        return ((1 - e2) / new Complex(0, 2), (1 + e2) / 2);
    }
}
