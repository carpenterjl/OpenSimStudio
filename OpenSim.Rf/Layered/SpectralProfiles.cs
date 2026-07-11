using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The z-dependent spectral profiles of the grounded-slab MPIE potentials for a
/// HORIZONTAL source current at the slab top z′ = d — <see cref="SpectralKernels"/>
/// generalized off the z = d plane. Solving the same boundary-value problem with the
/// observation height free gives closed forms that factor through the z = d kernels:
///
///   region 1 (0 ≤ z ≤ d):   G̃_A(z) = 2µ₀ sin(k_z1 z)/D_TE
///                            K̃_Φ(z) = 2N sin(k_z1 z)/(ε₀ D_TE D_TM)
///                            W̃(z)   = (ε_c−1)·2µ₀ sin(k_z1 d)cos(k_z1 z)/(j D_TM D_TE)
///   region 0 (z ≥ d):        each boundary value × e^{−j k_z0 (z−d)}
///
/// where W̃(z) is the A_z kernel per (−j k⃗_ρ·J̃) — the z-profile behind
/// <see cref="SpectralKernels.AzRatio"/>, whose boundary ratio W̃(d)/G̃_A(d) it
/// reproduces. Both Φ and A_x vanish ∝ sin(k_z1 z) on the PEC (Φ(0) = A_x(0) = 0)
/// and ∂_z of the cos profile vanishes there (∂_z A_z(0) = 0) — the PEC conditions
/// are BUILT INTO the forms, and the tests assert them rather than trust them.
///
/// Numerics: reduced trig throughout (the ReducedTrig discipline). In-slab profiles
/// carry e^{−j k_z1 (d−z)} with 0 ≤ d−z ≤ d, so every exponential decays on the
/// Im(k_z) ≤ 0 branch — overflow-safe arbitrarily far up the k_ρ tail, at every z.
/// </summary>
internal static class SpectralProfiles
{
    /// <summary>All three profiles at observation height z (source at z′ = d).
    /// <paramref name="kz0"/> in closed form, exactly like the boundary kernels.</summary>
    public static (Complex GA, Complex W, Complex KPhi) Evaluate(
        SubstrateStackup substrate, double k0, Complex kRho, Complex kz0, double z)
    {
        var (gaD, wD, kPhiD, kz1, sZ, cZ, phaseIn) = Amplitudes(substrate, k0, kRho, kz0, z);
        double d = substrate.ThicknessMeters;
        if (z >= d)
        {
            var e0 = Complex.Exp(-Complex.ImaginaryOne * kz0 * (z - d));
            return (gaD * e0, wD * e0, kPhiD * e0);
        }
        // In-slab: the direct forms (never the ratio to the boundary value — sin(k_z1 d)
        // has real zeros where the boundary kernel forms 0/0 while these stay finite).
        var (gaIn, wIn, kPhiIn) = InSlab(substrate, k0, kRho, kz0, kz1, sZ, cZ, phaseIn);
        return (gaIn, wIn, kPhiIn);
    }

    /// <summary>Analytic ∂/∂z of all three profiles at height z. Region 0 is the
    /// shared −j·k_z0 factor; region 1 differentiates the trig profiles
    /// (sin → k_z1·cos, cos → −k_z1·sin) — no finite differences anywhere.</summary>
    public static (Complex GA, Complex W, Complex KPhi) EvaluateDz(
        SubstrateStackup substrate, double k0, Complex kRho, Complex kz0, double z)
    {
        double d = substrate.ThicknessMeters;
        if (z >= d)
        {
            var (ga, w, kPhi) = Evaluate(substrate, k0, kRho, kz0, z);
            var factor = -Complex.ImaginaryOne * kz0;
            return (factor * ga, factor * w, factor * kPhi);
        }
        var (_, _, _, kz1, sZ, cZ, phaseIn) = Amplitudes(substrate, k0, kRho, kz0, z);
        // d/dz[sin(kz1 z)] = kz1 cos(kz1 z); d/dz[cos] = −kz1 sin — swap the reduced
        // trig pair under the SAME decaying phase (it differentiates to −j kz1 × itself,
        // already accounted for by the product rule collapsing to the swap).
        var (gaSin, wCos, kPhiSin) = InSlab(substrate, k0, kRho, kz0, kz1, cZ, sZ, phaseIn);
        // gaSin/kPhiSin were assembled with the SWAPPED pair: they now carry cos(kz1 z).
        return (kz1 * gaSin, -kz1 * wCos, kz1 * kPhiSin);
    }

    /// <summary>Common subexpressions: the z = d boundary amplitudes (identical to
    /// <see cref="SpectralKernels.Evaluate"/> plus the A_z one), k_z1, and the reduced
    /// trig pair at k_z1·z with its in-slab decay phase e^{−j k_z1 (d−z)}.</summary>
    private static (Complex GaD, Complex WD, Complex KPhiD, Complex Kz1,
        Complex SZ, Complex CZ, Complex PhaseIn) Amplitudes(
        SubstrateStackup substrate, double k0, Complex kRho, Complex kz0, double z)
    {
        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        double k0Sq = k0 * k0;
        var kz1 = SpectralKernels.Kz(epsC * k0Sq, kRho);
        double d = substrate.ThicknessMeters;
        var (s, c) = SpectralKernels.ReducedTrig(kz1 * d);
        var j = Complex.ImaginaryOne;

        var dTe = j * kz0 * s + kz1 * c;
        var dTm = epsC * kz0 * c + j * kz1 * s;
        var n = kz0 * c + j * kz1 * s;

        var gaD = 2 * RfConstants.Mu0 * s / dTe;
        var kPhiD = (2 / RfConstants.Eps0) * s * n / (dTe * dTm);
        var wD = (epsC - 1) * 2 * RfConstants.Mu0 * s * c / (j * dTm * dTe);

        double zIn = Math.Min(z, d);
        var (sZ, cZ) = SpectralKernels.ReducedTrig(kz1 * zIn);
        var phaseIn = Complex.Exp(-j * kz1 * (d - zIn));
        return (gaD, wD, kPhiD, kz1, sZ, cZ, phaseIn);
    }

    /// <summary>The in-slab forms from the reduced pair (S_z, C_z) at k_z1·z and the
    /// decay phase: sin(k_z1 z)/[e^{j k_z1 d}·] = S_z·e^{−j k_z1(d−z)}, and the shared
    /// e^{j k_z1 d} of every denominator (D̂, N̂ are the reduced dispersion values)
    /// cancels algebraically — no growing exponential survives at any (k_ρ, z).</summary>
    private static (Complex GA, Complex W, Complex KPhi) InSlab(
        SubstrateStackup substrate, double k0, Complex kRho, Complex kz0,
        Complex kz1, Complex sZ, Complex cZ, Complex phaseIn)
    {
        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        double d = substrate.ThicknessMeters;
        var (s, c) = SpectralKernels.ReducedTrig(kz1 * d);
        var j = Complex.ImaginaryOne;

        var dTe = j * kz0 * s + kz1 * c;
        var dTm = epsC * kz0 * c + j * kz1 * s;
        var n = kz0 * c + j * kz1 * s;

        var sinZ = sZ * phaseIn; // sin(kz1 z)·e^{−j kz1 d} — the reduced numerator
        var cosZ = cZ * phaseIn;
        var ga = 2 * RfConstants.Mu0 * sinZ / dTe;
        var kPhi = (2 / RfConstants.Eps0) * n * sinZ / (dTe * dTm);
        // W(z) = Q·cos(kz1 z), Q = (εc−1)·2µ0·sin(kz1 d)/(j D_TM D_TE). In reduced
        // form the numerator's e^{j kz1 d}·e^{j kz1 z} cancels against the
        // denominators' shared e^{2j kz1 d}, leaving Ŝ_d·Ĉ_z·e^{−j kz1 (d−z)} —
        // decaying at every (k_ρ, z), exactly the phaseIn factor cosZ carries.
        var w = (epsC - 1) * 2 * RfConstants.Mu0 * s * cosZ / (j * dTm * dTe);
        return (ga, w, kPhi);
    }
}
