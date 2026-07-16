using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Stage S9b — the per-observation-height FIELD kernels of the multi-layer TLGF: the
/// six spectral profiles a near-field map needs (the three potentials + their analytic
/// ∂z legs) at ANY observation height z, for a horizontal source at interface m.
///
/// <para>The Stage F <see cref="TransmissionLineGreens.Evaluate"/> / <see
/// cref="TransmissionLineGreens.EvaluateInterior"/> read the reduced-basis solution out at
/// ONE plane (the top, or the source plane z_m). Here the SAME solved TE/TM amplitude
/// vectors are evaluated at an arbitrary z through the layer that contains it — the
/// multi-layer analog of <see cref="SpectralProfiles"/>. The kernels follow from the
/// reduced-basis field and its z-derivative:</para>
/// <list type="bullet">
///   <item>G̃_A(z) = A_x(z), ∂zG̃_A(z) = ∂zA_x(z)</item>
///   <item>W̃(z) = ã_z(z), ∂zW̃(z) = ∂z ã_z(z)</item>
///   <item>K̃_Φ(z) = (A_x(z) + ∂z ã_z(z)) / (µ₀ε₀·ε_r(z)) — the divergence read-out with the
///     LOCAL permittivity (F2b's ÷ε_above is this rule at the source plane)</item>
///   <item>∂zK̃_Φ(z) = (∂zA_x(z) − k_z(z)²·ã_z(z)) / (µ₀ε₀·ε_r(z)) — within a constant-ε
///     layer ∂zz ã_z = −k_z² ã_z (the reduced basis solves the Helmholtz line)</item>
/// </list>
/// <para>At N = 1, top source, these reproduce <see cref="SpectralProfiles.Evaluate"/> /
/// <see cref="SpectralProfiles.EvaluateDz"/> to 1e-12 (the identity gate). The forms hold in
/// every layer AND in region 0 (air above the stack); the reduced (locally-decaying) basis
/// keeps them overflow-safe at any thickness / k_ρ, exactly like the boundary kernels.</para>
/// </summary>
internal static partial class TransmissionLineGreens
{
    /// <summary>The six field kernels at (k_ρ, z) for a source at interface <paramref name="m"/>
    /// (m = n−1 ⇒ the coplanar top plane; m &lt; n−1 ⇒ a buried/covered source). Solves the TE
    /// and TM reduced systems once, then profiles the solution at z.</summary>
    public static (Complex GA, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)
        EvaluateField(LayeredStackup stackup, double k0, Complex kRho, Complex kz0, int m, double z)
    {
        int n = stackup.Layers.Count;
        if (m < 0 || m >= n)
            throw new ArgumentOutOfRangeException(nameof(m),
                $"Source interface {m} is out of range for a {n}-layer stackup.");
        var sp = Spectral(stackup, k0, kRho);
        var (teM, teRhs, cIdx) = m == n - 1 ? TeSystem(sp, kz0) : TeSystemInterior(sp, kz0, m);
        var teSol = ComplexLu.Factor(teM).Solve(teRhs);
        Complex c = teSol[cIdx];
        var axAt = AxAtInterfaces(teSol, sp.Phi, c);
        var (tmM, tmRhs, _) = TmSystem(sp, kz0, axAt, c);
        var tmSol = ComplexLu.Factor(tmM).Solve(tmRhs);
        return AssembleField(stackup, sp, kz0, teSol, tmSol, z);
    }

    /// <summary>The per-z residues of the six field kernels at a surface-wave pole
    /// <paramref name="kp"/>. The mode (null vector) is source-independent; only the RHS
    /// (source at interface m) and the read-out height z move. Profiling the RESIDUE amplitude
    /// vectors at z gives every kernel's residue — G̃_A/∂zG̃_A vanish at a TM pole (A_x regular),
    /// as the null-vector method makes explicit (a zero TE residue vector).</summary>
    public static (Complex GA, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)
        PoleFieldResidues(LayeredStackup stackup, double k0, Complex kp, bool isTm, int m, double z)
    {
        int n = stackup.Layers.Count;
        var sp = Spectral(stackup, k0, kp);
        var kz0 = SpectralKernels.Kz(k0 * k0, kp);
        var (teM, teRhs, cIdx) = m == n - 1 ? TeSystem(sp, kz0) : TeSystemInterior(sp, kz0, m);
        if (!isTm)
        {
            // TE pole: A_x (and every interface value) is singular; ã_z inherits it through
            // the regular TM system with the residue source.
            var teDeriv = m == n - 1
                ? MatrixDerivative(stackup, k0, kp, tm: false)
                : MatrixDerivativeTeInterior(stackup, k0, kp, m);
            var resTe = MatrixResidue(teM, teDeriv, teRhs);
            Complex resC = resTe[cIdx];
            var resAx = AxAtInterfaces(resTe, sp.Phi, resC);
            var (tmM, resB, _) = TmSystem(sp, kz0, resAx, resC);
            var resTm = ComplexLu.Factor(tmM).Solve(resB);
            return AssembleField(stackup, sp, kz0, resTe, resTm, z);
        }
        else
        {
            // TM pole: A_x regular ⇒ zero TE residue; only ã_z is singular.
            var teSol = ComplexLu.Factor(teM).Solve(teRhs);
            Complex c = teSol[cIdx];
            var axAt = AxAtInterfaces(teSol, sp.Phi, c);
            var (tmM, tmRhs, _) = TmSystem(sp, kz0, axAt, c);
            var resTm = MatrixResidue(tmM, MatrixDerivative(stackup, k0, kp, tm: true), tmRhs);
            var zeroTe = new Complex[teSol.Length];
            return AssembleField(stackup, sp, kz0, zeroTe, resTm, z);
        }
    }

    /// <summary>The six kernels from the reduced-basis field profile at z: the two potentials,
    /// the TM coupling W̃ = ã_z, and the divergence read-outs K̃_Φ / ∂zK̃_Φ with the local ε_r.</summary>
    private static (Complex GA, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)
        AssembleField(LayeredStackup stackup, LayerSpectral sp, Complex kz0,
        Complex[] teVec, Complex[] tmVec, double z)
    {
        var (ax, dzAx, az, dzAz, epsAt, kzAt) = Profile(stackup, sp, kz0, teVec, tmVec, z);
        Complex norm = RfConstants.Mu0 * RfConstants.Eps0 * epsAt;
        var phi = (ax + dzAz) / norm;
        var dzPhi = (dzAx - kzAt * kzAt * az) / norm;
        return (ax, az, phi, dzPhi, dzAx, dzAz);
    }

    /// <summary>A_x, ∂zA_x, ã_z, ∂z ã_z, and the local (ε_r, k_z) at observation height z,
    /// from solved (or residue) TE/TM amplitude vectors. Region 0 (z ≥ d_total) is the single
    /// upgoing decaying wave C·e^{−jk_z0(z−d)}; inside layer i it is the reduced pair
    /// P⁺e^{−jk_z(z−z_b)} + P⁻e^{+jk_z(z−z_t)}, both magnitudes ≤ 1 on the Im(k_z) ≤ 0 branch.</summary>
    private static (Complex Ax, Complex DzAx, Complex Az, Complex DzAz, Complex Eps, Complex Kz)
        Profile(LayeredStackup stackup, LayerSpectral sp, Complex kz0,
        Complex[] teVec, Complex[] tmVec, double z)
    {
        int n = stackup.Layers.Count;
        var heights = stackup.InterfaceHeights();
        double dTot = heights[n - 1];
        var j = Complex.ImaginaryOne;
        if (z >= dTot)
        {
            var e0 = Complex.Exp(-j * kz0 * (z - dTot));
            Complex ax0 = teVec[2 * n] * e0, az0 = tmVec[2 * n] * e0;
            return (ax0, -j * kz0 * ax0, az0, -j * kz0 * az0, Complex.One, kz0);
        }
        int i = 0;
        while (i < n - 1 && z > heights[i]) i++;   // first layer whose top is ≥ z
        double zt = heights[i], zb = i == 0 ? 0 : heights[i - 1];
        var kz = sp.Kz[i];
        var eDown = Complex.Exp(-j * kz * (z - zb));
        var eUp = Complex.Exp(j * kz * (z - zt));
        Complex p = teVec[2 * i], pm = teVec[2 * i + 1];
        Complex ax = p * eDown + pm * eUp;
        Complex dzAx = -j * kz * p * eDown + j * kz * pm * eUp;
        Complex q = tmVec[2 * i], qm = tmVec[2 * i + 1];
        Complex az = q * eDown + qm * eUp;
        Complex dzAz = -j * kz * q * eDown + j * kz * qm * eUp;
        return (ax, dzAx, az, dzAz, sp.Eps[i], kz);
    }
}
