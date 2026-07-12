using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Spectral kernels for VERTICAL currents in the grounded slab (probe feeds / via
/// barrels): a ẑ-directed source at height z′ ∈ [0, d] inside the dielectric,
/// observation anywhere z ≥ 0. Same conventions as <see cref="SpectralKernels"/>:
/// G = (1/4π)∫G̃J₀(k_ρρ)k_ρdk_ρ, time factor e^{+jωt}, Im(k_z) ≤ 0.
///
/// Formulation C keeps ONE scalar-potential kernel for every charge. The two-height
/// K̃_Φ(z,z′) is derived from the horizontal-dipole-at-z′ boundary problem
/// (Φ = −∇·A/(jωµε) per unit charge) and reduces EXACTLY to the Stage C/D kernel at
/// z′ = d (the ε_c cancels through D̂_TM + j(ε_c−1)k_z1Ŝ_d = ε_c N̂). The classical
/// vertical Sommerfeld potential Ã_z^cl (TM standing waves; ∂_zA_z = 0 on the PEC;
/// A_z and (1/ε)∂_zA_z continuous at z = d) does NOT satisfy jωΦ̃_cl = ∂_z′K̃_Φ —
/// the mismatch is the smooth SEPARABLE function
///
///   Δ(z,z′) ≡ jωΦ̃_cl − ∂_z′K̃_Φ = −2j(ε_c−1)k₀²·sin(k_z1 z)cos(k_z1 z′)/(ε₀ D̂_TE D̂_TM)
///
/// and formulation C absorbs it into the vector potential, A^C = A^cl − (1/ω²)∇Δ:
///
///   G̃_A^zz = Ã_z^cl − (1/ω²)∂_zΔ,      G̃_A^xz = −(1/ω²)Δ  (per −jk_x·J̃_z)
///
/// G̃_A^xz comes out as exactly −W̃(z′,z) — MINUS THE TRANSPOSE of the existing
/// horizontal→A_z coupling — so no new kernel family appears, and the assembled field
///
///   Ẽ_z = −jωG̃_A^zz − (1/jω)∂_z∂_z′K̃_Φ,   Ẽ_x/(−jk_x) = −jωG̃_A^xz − (1/jω)∂_z′K̃_Φ
///
/// reproduces the classical (gauge-independent) field identically — the test gate
/// checks it against an independent BVP oracle that never splits the gauge legs.
///
/// Closed forms, with z&lt; = min(z,z′), z&gt; = max(z,z′) and the top-matched standing
/// waves U(z) = jk_z0 sin(k_z1(d−z)) + k_z1 cos(k_z1(d−z)) (TE-matched; U(0) = D_TE)
/// and V(z) = ε_c k_z0 sin(k_z1(d−z)) − jk_z1 cos(k_z1(d−z)) (TM-matched):
///
///   Ã_z^cl(z,z′) = (2µ₀/k_z1)·cos(k_z1 z&lt;)·V(z&gt;)/D_TM
///   K̃_Φ(z,z′)   = (2/ε₀ε_c)·[sin(k_z1 z&lt;)·U(z&gt;)/(k_z1 D_TE)
///                             + j(ε_c−1)k_z1·sin(k_z1 z)sin(k_z1 z′)/(D_TM D_TE)]
///
/// with the region-0 continuation (× e^{−jk_z0(z−d)}) from the z = d boundary values.
/// The εr → 1 limit is EXACTLY primary + image at z+z′ — coefficient +1 for G_zz (a
/// vertical dipole images POSITIVELY over PEC) and −1 for K_Φ — a machine-zero gate.
///
/// Numerics: reduced trig throughout (the <see cref="SpectralKernels.ReducedTrig"/>
/// discipline). Every term carries one of the decaying phases e^{−jk_z1(z&gt;−z&lt;)},
/// e^{−jk_z1(2d−z−z′)}, or e^{−jk_z1(d−z′)}e^{−jk_z0(z−d)} — all exponents ≤ 0 on the
/// Im(k_z) ≤ 0 branch, so nothing overflows arbitrarily far up the k_ρ tail.
/// </summary>
internal static class VerticalSpectralKernels
{
    /// <summary>G̃_A^zz, the horizontal coupling G̃_A^xz (per −jk_x·J̃_z), and the
    /// two-height scalar kernel K̃_Φ(z,z′) at one spectral point. <paramref name="kz0"/>
    /// in closed form from the contour, exactly like the boundary kernels.</summary>
    public static (Complex GAzz, Complex GAxz, Complex KPhi) Evaluate(
        SubstrateStackup substrate, double k0, Complex kRho, Complex kz0, double z, double zPrime)
    {
        GuardHeights(substrate, z, zPrime);
        double d = substrate.ThicknessMeters;
        // z = d lands on the REGION-0 branch, i.e. the free-space side: E_z (and the
        // ∂_zΔ correction inside G_zz) genuinely JUMPS at the interface (D_z is what
        // is continuous). The probe machinery, whose currents live in the slab, takes
        // the dielectric side through EvaluateDielectricSide instead.
        return z >= d
            ? Region0(substrate, k0, kRho, kz0, z, zPrime)
            : EvaluateDielectricSide(substrate, k0, kRho, kz0, z, zPrime);
    }

    /// <summary>The in-slab (dielectric-side) branch, valid for z ≤ d INCLUSIVE — the
    /// side the probe integrator extracts against (its images and pole residues are
    /// in-slab forms; mixing sides at z = d leaves a non-decaying remainder, found by
    /// the self-convergence gate). K̃_Φ and G̃_xz are continuous across z = d; only
    /// G̃_zz's correction leg is two-sided.</summary>
    internal static (Complex GAzz, Complex GAxz, Complex KPhi) EvaluateDielectricSide(
        SubstrateStackup substrate, double k0, Complex kRho, Complex kz0, double z, double zPrime)
    {
        GuardHeights(substrate, z, zPrime);
        var (epsC, kz1, dTe, dTm, _, _, _, d) = Common(substrate, k0, kRho, kz0);
        if (z > d)
            throw new ArgumentOutOfRangeException(nameof(z),
                $"The dielectric-side branch needs z ≤ d = {d} m — got {z} m.");
        var j = Complex.ImaginaryOne;
        double mu0 = RfConstants.Mu0, eps0 = RfConstants.Eps0;
        var (sZp, cZp) = SpectralKernels.ReducedTrig(kz1 * zPrime);
        var (sZ, cZ) = SpectralKernels.ReducedTrig(kz1 * z);
        double zHi = Math.Max(z, zPrime);
        var (sLo, cLo) = z <= zPrime ? (sZ, cZ) : (sZp, cZp);
        var (sDh, cDh) = SpectralKernels.ReducedTrig(kz1 * (d - zHi));
        var u = j * kz0 * sDh + kz1 * cDh;
        var v = epsC * kz0 * sDh - j * kz1 * cDh;
        var p1 = Complex.Exp(-j * kz1 * Math.Abs(z - zPrime));
        var p2 = Complex.Exp(-j * kz1 * (2 * d - z - zPrime));

        var gzz = (2 * mu0 / kz1) * cLo * v * p1 / dTm
            + 2 * j * (epsC - 1) * mu0 * kz1 * cZ * cZp * p2 / (dTe * dTm);
        var gxzIn = 2 * j * (epsC - 1) * mu0 * sZ * cZp * p2 / (dTe * dTm);
        var kPhiIn = (2 / (eps0 * epsC)) * (sLo * u * p1 / (kz1 * dTe)
            + j * (epsC - 1) * kz1 * sZ * sZp * p2 / (dTm * dTe));
        return (gzz, gxzIn, kPhiIn);
    }

    /// <summary>Region 0 (z ≥ d): the z = d boundary values × e^{−jk_z0(z−d)}.</summary>
    private static (Complex GAzz, Complex GAxz, Complex KPhi) Region0(
        SubstrateStackup substrate, double k0, Complex kRho, Complex kz0, double z, double zPrime)
    {
        var (epsC, kz1, dTe, dTm, n, s1, _, d) = Common(substrate, k0, kRho, kz0);
        var j = Complex.ImaginaryOne;
        double mu0 = RfConstants.Mu0, eps0 = RfConstants.Eps0;
        var (sZp, cZp) = SpectralKernels.ReducedTrig(kz1 * zPrime);
        var p0 = Complex.Exp(-j * kz1 * (d - zPrime)) * Complex.Exp(-j * kz0 * (z - d));
        var gzzCl = -2 * j * mu0 * cZp / dTm;
        var gzzCorr = 2 * (epsC - 1) * mu0 * kz0 * s1 * cZp / (dTe * dTm);
        var gxz = 2 * j * (epsC - 1) * mu0 * s1 * cZp / (dTe * dTm);
        var kPhi = (2 / eps0) * n * sZp / (dTe * dTm);
        return ((gzzCl + gzzCorr) * p0, gxz * p0, kPhi * p0);
    }

    /// <summary>The analytic charge-kernel derivatives ∂_z′K̃_Φ and ∂_z∂_z′K̃_Φ that
    /// assemble the vertical-current field (Ẽ_x and Ẽ_z legs). Pointwise only away
    /// from the source height: ∂_z′K̃_Φ carries the 1-D Green's-function KINK at
    /// z = z′ (and ∂_z∂_z′ a delta) — the MoM never needs it there because the weak
    /// form integrates ∂_zf·K̃_Φ·∂_z′f′ by parts, so z = z′ is a loud failure.</summary>
    public static (Complex DzPrimeKPhi, Complex DzDzPrimeKPhi) ChargeGradients(
        SubstrateStackup substrate, double k0, Complex kRho, Complex kz0, double z, double zPrime)
    {
        GuardHeights(substrate, z, zPrime);
        var (epsC, kz1, dTe, dTm, n, _, _, d) = Common(substrate, k0, kRho, kz0);
        var j = Complex.ImaginaryOne;
        double eps0 = RfConstants.Eps0;
        var (sZp, cZp) = SpectralKernels.ReducedTrig(kz1 * zPrime);

        if (z >= d)
        {
            var p0 = Complex.Exp(-j * kz1 * (d - zPrime)) * Complex.Exp(-j * kz0 * (z - d));
            var dzp = (2 / eps0) * n * kz1 * cZp * p0 / (dTe * dTm);
            return (dzp, -j * kz0 * dzp);
        }
        if (z == zPrime)
            throw new InvalidOperationException(
                "∂z′K̃_Φ is kinked (and ∂z∂z′ carries a delta) at z = z′ — integrate the weak " +
                "by-parts form there instead of evaluating the pointwise derivative.");

        var (sZ, cZ) = SpectralKernels.ReducedTrig(kz1 * z);
        var p1 = Complex.Exp(-j * kz1 * Math.Abs(z - zPrime));
        var p2 = Complex.Exp(-j * kz1 * (2 * d - z - zPrime));
        var couple = j * (epsC - 1) * kz1 * kz1 * cZp * p2 / (dTm * dTe);
        var term2Dzp = couple * sZ;
        var term2Both = couple * kz1 * cZ;

        Complex dzpIn, bothIn;
        if (zPrime < z)
        {
            // z< = z′: ∂z′ hits sin(k_z1 z′) → k_z1cos, ∂z hits U(z) → U′(z).
            var (sDh, cDh) = SpectralKernels.ReducedTrig(kz1 * (d - z));
            var u = j * kz0 * sDh + kz1 * cDh;
            var uPrime = -kz1 * (j * kz0 * cDh - kz1 * sDh);
            dzpIn = cZp * u * p1 / dTe + term2Dzp;
            bothIn = cZp * uPrime * p1 / dTe + term2Both;
        }
        else
        {
            // z< = z: ∂z′ hits U(z′) → U′(z′), ∂z hits sin(k_z1 z) → k_z1cos.
            var (sDh, cDh) = SpectralKernels.ReducedTrig(kz1 * (d - zPrime));
            var uPrime = -kz1 * (j * kz0 * cDh - kz1 * sDh);
            dzpIn = sZ * uPrime * p1 / (kz1 * dTe) + term2Dzp;
            bothIn = cZ * uPrime * p1 / dTe + term2Both;
        }
        var scale = 2 / (eps0 * epsC);
        return (scale * dzpIn, scale * bothIn);
    }

    private static (Complex EpsC, Complex Kz1, Complex DTe, Complex DTm, Complex N,
        Complex S1, Complex C1, double D) Common(
        SubstrateStackup substrate, double k0, Complex kRho, Complex kz0)
    {
        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        var kz1 = SpectralKernels.Kz(epsC * k0 * k0, kRho);
        double d = substrate.ThicknessMeters;
        var (s1, c1) = SpectralKernels.ReducedTrig(kz1 * d);
        var j = Complex.ImaginaryOne;
        var dTe = j * kz0 * s1 + kz1 * c1;
        var dTm = epsC * kz0 * c1 + j * kz1 * s1;
        var n = kz0 * c1 + j * kz1 * s1;
        return (epsC, kz1, dTe, dTm, n, s1, c1, d);
    }

    private static void GuardHeights(SubstrateStackup substrate, double z, double zPrime)
    {
        double d = substrate.ThicknessMeters;
        if (zPrime < 0 || zPrime > d)
            throw new ArgumentOutOfRangeException(nameof(zPrime),
                $"The vertical-current source height must lie inside the slab [0, {d}] m — " +
                $"got {zPrime} m. Vertical currents above the slab are outside the v1-E scope.");
        if (z < 0)
            throw new ArgumentOutOfRangeException(nameof(z),
                $"The observation height must be ≥ 0 (the PEC ground) — got {z} m.");
    }
}
