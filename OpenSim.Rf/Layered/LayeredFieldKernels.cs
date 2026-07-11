using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The per-observation-height ingredients of the Stage D FIELD kernels — everything
/// the per-z Sommerfeld remainder needs beyond <see cref="SpectralProfiles"/>:
///
/// QUASI-STATIC IMAGES (the k_ρ → ∞ asymptotes, subtracted with the e^{−jk₀R}
/// dynamic phase exactly like the z = d extraction):
///  • G̃_A: coefficients (+1, −1) at heights (|z−d|, z+d) — for the vector potential
///    the slab's exponential ladder cancels IDENTICALLY in D̂_TE's leading order
///    (Ŝ_d − jĈ_d = −j exactly, every E_d² term gone), so two images capture all
///    exponential orders at every z, just as at z = d.
///  • K̃_Φ: the classical dielectric image ladder. In-slab (z ≤ d):
///    c₀ at (d−z), −c₀ at (d+z), −c₀γ at (3d−z), +c₀γ at (3d+z), γ = (ε_c−1)/(ε_c+1)
///    — truncated after the first reflection level; at z = d the middle pair merges
///    into the Stage C c₁ = −c₀(1+γ). Above (z ≥ d): c₀ at (z−d), c₁ at (z+d),
///    −γc₁ at (z+3d). At ε_r = 1 (γ = 0, c₀ = 1) both reduce to the EXACT
///    two-exponential form.
///  • W̃ (the A_z coupling): NO image extraction — its spectral decay is already
///    1/k_ρ² × exponentials (log-singular at worst spatially), and a lone
///    e^{−k_ρh}/k_ρ² subtraction has no closed-form spatial counterpart of the g(R)
///    family. The partition–extrapolation tail carries it.
///
/// POLE RESIDUES per z: every profile shares the D̂_TE·D̂_TM denominator, so
/// Res F(z) = Num_F(k_p, z) / (D̂_TE′D̂_TM + D̂_TE D̂_TM′)|k_p — analytic through
/// <see cref="SpectralKernels.Dispersion"/>, valid at TM and TE poles alike (one
/// factor vanishes, the derivative rule keeps the right term). Above the slab the
/// numerators carry e^{−jk_z0p(z−d)} — the surface wave's evanescent tail.
/// </summary>
internal static class LayeredFieldKernels
{
    /// <summary>One quasi-static image: coefficient × g_dynamic(√(ρ² + Height²)).</summary>
    public readonly record struct KernelImage(double Height, Complex CoefficientA, Complex CoefficientPhi);

    /// <summary>The image list for observation height z (up to 3 entries; unused slots
    /// have zero coefficients). Heights are z-dependent; the spectral subtraction is
    /// Σ coeff·e^{−jk_z0·h}/(jk_z0) per kernel scale (µ₀ for A, 1/ε₀ for Φ).</summary>
    public static KernelImage[] Images(SubstrateStackup substrate, double z)
    {
        double d = substrate.ThicknessMeters;
        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        var c0 = 2 / (epsC + 1);
        var gamma = (epsC - 1) / (epsC + 1);
        if (z >= d)
        {
            var c1 = -4 * epsC / ((epsC + 1) * (epsC + 1));
            return new[]
            {
                new KernelImage(z - d, 1, c0),
                new KernelImage(z + d, -1, c1),
                new KernelImage(z + 3 * d, 0, -gamma * c1)
            };
        }
        return new[]
        {
            new KernelImage(d - z, 1, c0),
            new KernelImage(d + z, -1, -c0),
            new KernelImage(3 * d - z, 0, -c0 * gamma),
            new KernelImage(3 * d + z, 0, c0 * gamma)
        };
    }

    /// <summary>All four field-kernel profiles at one (k_ρ, z): the three potentials
    /// plus ∂z of K̃_Φ (E_z's ∇Φ leg). kz0 in closed form as everywhere.</summary>
    public static (Complex A, Complex W, Complex Phi, Complex DzPhi) EvaluateAll(
        SubstrateStackup substrate, double k0, Complex kRho, Complex kz0, double z)
    {
        var (a, w, phi) = SpectralProfiles.Evaluate(substrate, k0, kRho, kz0, z);
        var (_, _, dzPhi) = SpectralProfiles.EvaluateDz(substrate, k0, kRho, kz0, z);
        return (a, w, phi, dzPhi);
    }

    /// <summary>The per-z residues of all four kernels at one surface-wave pole.
    /// Gated against the z = d residues of <see cref="SurfaceWavePoles"/> (identity)
    /// and against Richardson extrapolation of the profiles near k_p.</summary>
    public static (Complex A, Complex W, Complex Phi, Complex DzPhi) PoleResidues(
        SubstrateStackup substrate, double k0, Complex poleKRho, double z)
    {
        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        double k0Sq = k0 * k0;
        var kz0 = SpectralKernels.Kz(k0Sq, poleKRho);
        var kz1 = SpectralKernels.Kz(epsC * k0Sq, poleKRho);
        double d = substrate.ThicknessMeters;
        var j = Complex.ImaginaryOne;
        var (dTe, dTm, dTePrime, dTmPrime, s, n) =
            SpectralKernels.Dispersion(substrate, k0, poleKRho);
        var (sd, cd) = SpectralKernels.ReducedTrig(kz1 * d);

        // d/dk_ρ (D̂_TE·D̂_TM) — at a pole one factor is ~0, so this is D′·other.
        var denom = dTePrime * dTm + dTe * dTmPrime;

        double zIn = Math.Min(z, d);
        var (sz, cz) = SpectralKernels.ReducedTrig(kz1 * zIn);
        var phaseIn = Complex.Exp(-j * kz1 * (d - zIn));
        // Numerators over the COMMON D̂_TE·D̂_TM denominator (in-slab forms; the
        // reduced e^{j k_z1 d} bookkeeping matches SpectralProfiles exactly).
        var numA = 2 * RfConstants.Mu0 * sz * phaseIn * dTm;
        var numW = (epsC - 1) * 2 * RfConstants.Mu0 * sd * (cz * phaseIn) / j;
        var numPhi = (2 / RfConstants.Eps0) * n * sz * phaseIn;
        var numDzPhi = kz1 * (2 / RfConstants.Eps0) * n * cz * phaseIn;

        if (z > d)
        {
            // Boundary numerators × the evanescent vertical decay e^{−jk_z0p(z−d)}.
            var e0 = Complex.Exp(-j * kz0 * (z - d));
            numA *= e0;
            numW *= e0;
            numPhi *= e0;
            numDzPhi = -j * kz0 * numPhi; // region-0 ∂z is the shared −jk_z0 factor
        }
        return (numA / denom, numW / denom, numPhi / denom, numDzPhi / denom);
    }
}
