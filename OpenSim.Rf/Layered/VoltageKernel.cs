using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The spectral patch-to-ground VOLTAGE kernel K̃_V(k_ρ): the straight-path line
/// integral V = −∫₀^d E_z dz per unit SURFACE CHARGE at the slab top, assembled from
/// BOTH legs of the gauge-invariant E_z = −∂Φ/∂z − jωA_z. Formulation C's Φ alone is
/// gauge-dependent (the 70–85 Ω vs 228 Ω edge-resistance anomaly of the Stage C
/// batch); the −jωA_z leg comes from the HORIZONTAL patch currents through the TM
/// coupling profile W̃(z) of <see cref="SpectralProfiles"/>. In the spectral domain
/// both legs multiply the same scalar — k⃗·J̃ = ω·q̃ by continuity — so the sum stays
/// ONE radial kernel convolved with the RWG surface charge, and the closed-form
/// z-integrals of the trig profiles give
///
///   K̃_V = (2/ε₀) · [ k₀²(ε_c−1)·Ŝ²/(j·k_z1) + Ŝ·N̂ ] / (D̂_TE·D̂_TM)
///
/// in reduced trig (the e^{2j k_z1 d} of numerator and denominators cancels — no
/// growing exponential at any k_ρ). The Φ(0) endpoint vanishes identically (Φ ∝
/// sin(k_z1 z) is zero ON the PEC), so the ∇Φ leg contributes exactly K̃_Φ(d) and
/// the whole kernel collapses to K̃_Φ at ε_r = 1 (the A_z leg carries ε_c−1) — the
/// gauge gate asserts the sum against a numeric ∫Ẽ_z dz of the independent BVP
/// oracle, which knows nothing about this split.
/// </summary>
internal static class VoltageKernel
{
    /// <summary>K̃_V at one k_ρ, kz0 passed in closed form like every kernel here.</summary>
    public static Complex Evaluate(SubstrateStackup substrate, double k0, Complex kRho, Complex kz0)
    {
        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        double k0Sq = k0 * k0;
        var kz1 = SpectralKernels.Kz(epsC * k0Sq, kRho);
        var (s, c) = SpectralKernels.ReducedTrig(kz1 * substrate.ThicknessMeters);
        var j = Complex.ImaginaryOne;

        var dTe = j * kz0 * s + kz1 * c;
        var dTm = epsC * kz0 * c + j * kz1 * s;
        var n = kz0 * c + j * kz1 * s;

        // k₀²(εc−1)Ŝ²/(j k_z1): the ∫₀^d cos(k_z1 z)dz = sin(k_z1 d)/k_z1 of the A_z
        // profile, times ω²µ₀ε₀ = k₀². Regular at k_z1 → 0 (Ŝ ~ k_z1·d) and exactly
        // zero at ε_r = 1.
        var az = k0Sq * (epsC - 1) * s * s / (j * kz1);
        return (2 / RfConstants.Eps0) * (az + s * n) / (dTe * dTm);
    }

    /// <summary>The residue of K̃_V at a surface-wave pole, from the K̃_Φ residue by
    /// the exact multiplicative identity Res_V = Res_Φ·(1 + k₀²(ε_c−1)Ŝ/(j·k_z1·N̂))
    /// — both kernels share the D̂_TE·D̂_TM denominator, so the ratio of residues is
    /// the ratio of numerators at k_p (analytic, no new derivative machinery).</summary>
    public static Complex ResidueFromPhi(SubstrateStackup substrate, double k0,
        Complex poleKRho, Complex residuePhi)
    {
        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        double k0Sq = k0 * k0;
        var kz0 = SpectralKernels.Kz(k0Sq, poleKRho);
        var kz1 = SpectralKernels.Kz(epsC * k0Sq, poleKRho);
        var (s, c) = SpectralKernels.ReducedTrig(kz1 * substrate.ThicknessMeters);
        var j = Complex.ImaginaryOne;
        var n = kz0 * c + j * kz1 * s;
        return residuePhi * (1 + k0Sq * (epsC - 1) * s / (j * kz1 * n));
    }
}
