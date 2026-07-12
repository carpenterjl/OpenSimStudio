using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The per-(z, z′) ingredients of the SPATIAL vertical-current kernels — what the
/// probe-feed Sommerfeld remainder needs beyond <see cref="VerticalSpectralKernels"/>:
///
/// QUASI-STATIC IMAGES (k_ρ → ∞ asymptotes, subtracted in the e^{−jk_z0h}/(jk_z0)
/// spectral form / added back as e^{−jk₀R}/(4πR) — the same kz0-flavored convention as
/// <see cref="LayeredFieldKernels"/>; the k_z1-vs-k_z0 flavor difference decays a full
/// 1/k_ρ² faster and rides the tail):
///  • G̃_A^zz (scale µ₀): +1 at |z−z′| (primary), +1 at z+z′ (a vertical dipole images
///    POSITIVELY over PEC), −η at 2d−z−z′ and −η at 2d∓|z−z′| (the first dielectric
///    reflection level, η = (ε_c−1)/(ε_c+1); the 2d−z−z′ term is CRITICAL near the
///    junction where its height → 0). At z = z′ = d the set collapses to (1−η) = c₀'s
///    2/(ε_c+1) — the Stage C boundary coefficient, an algebraic consistency check.
///  • K̃_Φ (scale 1/ε₀, in-dielectric primary): +1/ε_c at |z−z′|, −1/ε_c at z+z′,
///    +γ/ε_c at 2d−z−z′, −γ/ε_c at 2d∓|z−z′| (γ = η) — the classical two-height
///    dielectric ladder truncated after the first reflection level, exactly the
///    Stage D depth. At εr = 1 both sets reduce to the EXACT two-exponential forms.
///  • G̃_A^xz: NO extraction — 1/k_ρ² spectral decay (the W̃ precedent).
///
/// POLE RESIDUES per (z, z′) over the common D̂_TE·D̂_TM denominator, analytic through
/// <see cref="SpectralKernels.Dispersion"/> — same reduced bookkeeping as the
/// closed-form kernels (the classical G_zz track carries an extra D̂_TE in its
/// numerator to sit over the product denominator).
/// </summary>
internal static class VerticalSpatialKernels
{
    /// <summary>One quasi-static image: coefficient × g_dynamic(√(ρ² + Height²)).</summary>
    public readonly record struct KernelImage(double Height, Complex CoefficientGAzz, Complex CoefficientKPhi);

    /// <summary>The five-image set for source z′ and observation z, both in [0, d].</summary>
    public static KernelImage[] Images(SubstrateStackup substrate, double z, double zPrime)
    {
        double d = substrate.ThicknessMeters;
        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        var eta = (epsC - 1) / (epsC + 1);
        double dz = Math.Abs(z - zPrime);
        return new[]
        {
            new KernelImage(dz, 1, 1 / epsC),
            new KernelImage(z + zPrime, 1, -1 / epsC),
            new KernelImage(2 * d - z - zPrime, -eta, eta / epsC),
            new KernelImage(2 * d - dz, -eta, -eta / epsC),
            new KernelImage(2 * d + dz, -eta, -eta / epsC)
        };
    }

    /// <summary>Residues of (G̃_A^zz, G̃_A^xz, K̃_Φ) at one surface-wave pole for the
    /// in-slab observation branch (z ≤ d — the probe's world). Valid at TM and TE
    /// poles alike: numerators over D̂_TE·D̂_TM, derivative rule keeps the live factor.</summary>
    public static (Complex GAzz, Complex GAxz, Complex KPhi) PoleResidues(
        SubstrateStackup substrate, double k0, Complex poleKRho, double z, double zPrime)
    {
        double d = substrate.ThicknessMeters;
        if (z < 0 || z > d || zPrime < 0 || zPrime > d)
            throw new ArgumentOutOfRangeException(nameof(z),
                "Vertical-kernel pole residues are the in-slab branch — both heights must lie in [0, d].");
        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        double k0Sq = k0 * k0;
        var kz0 = SpectralKernels.Kz(k0Sq, poleKRho);
        var kz1 = SpectralKernels.Kz(epsC * k0Sq, poleKRho);
        var j = Complex.ImaginaryOne;
        var (dTe, dTm, dTePrime, dTmPrime, _, _) =
            SpectralKernels.Dispersion(substrate, k0, poleKRho);
        var denom = dTePrime * dTm + dTe * dTmPrime;

        var (sZ, cZ) = SpectralKernels.ReducedTrig(kz1 * z);
        var (sZp, cZp) = SpectralKernels.ReducedTrig(kz1 * zPrime);
        double zHi = Math.Max(z, zPrime);
        var (sLo, cLo) = z <= zPrime ? (sZ, cZ) : (sZp, cZp);
        var (sDh, cDh) = SpectralKernels.ReducedTrig(kz1 * (d - zHi));
        var u = j * kz0 * sDh + kz1 * cDh;
        var v = epsC * kz0 * sDh - j * kz1 * cDh;
        var p1 = Complex.Exp(-j * kz1 * Math.Abs(z - zPrime));
        var p2 = Complex.Exp(-j * kz1 * (2 * d - z - zPrime));
        double mu0 = RfConstants.Mu0;

        var numGAzz = (2 * mu0 / kz1) * cLo * v * p1 * dTe
            + 2 * j * (epsC - 1) * mu0 * kz1 * cZ * cZp * p2;
        var numGAxz = 2 * j * (epsC - 1) * mu0 * sZ * cZp * p2;
        var numKPhi = (2 / (RfConstants.Eps0 * epsC))
            * (sLo * u * p1 * dTm / kz1 + j * (epsC - 1) * kz1 * sZ * sZp * p2);
        return (numGAzz / denom, numGAxz / denom, numKPhi / denom);
    }
}
