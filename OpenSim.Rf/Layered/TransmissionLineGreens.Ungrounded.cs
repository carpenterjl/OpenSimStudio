using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// FU-36 — the transmission-line Green's function of a dielectric stack with NO ground plane: air
/// above it and air below it. A printed antenna on a board whose ground is its own meshed copper
/// (not an infinite plane) sees exactly this: the board as an infinite slab, every piece of metal
/// in the solve. The boundary-value problem is the open stack's interior-source one
/// (<see cref="TeSystemInterior"/>, <see cref="TmSystem"/>) with the PEC row at z = 0 replaced by a
/// second radiating half-space: below the stack A_x = D·e^{+jk_z0 z} and ã_z = E·e^{+jk_z0 z}, both
/// decaying downward on the Im(k_z0) ≤ 0 branch, joined to layer 0 by value and (1/ε)-weighted
/// derivative continuity — with the ε-contrast shunt source D·(1/1 − 1/ε₀) at that bottom face,
/// the same rule every other interface follows.
///
/// <para>Layout: the open systems' unknowns [P_0^+, P_0^-, …, C] (C, S the region-0 amplitudes
/// above, at index 2n) plus D (resp. E) at index 2n + 1, so the interface and read-out helpers
/// apply unchanged.</para>
/// </summary>
internal static partial class TransmissionLineGreens
{
    internal static (ComplexDenseMatrix M, Complex[] Rhs, int CIdx, int DIdx) TeSystemUngrounded(
        LayerSpectral sp, Complex kz0, int m)
    {
        var (_, kz, phi) = sp;
        int n = kz.Length, size = 2 * n + 2, cIdx = 2 * n, dIdx = 2 * n + 1, t = 2 * (n - 1);
        var j = Complex.ImaginaryOne;
        var m0 = new ComplexDenseMatrix(size, size);
        var rhs = new Complex[size];
        int row = 0;
        // Bottom face: A_x(0⁺) = D, ∂A_x(0⁺) = jk_z0·D.
        m0[row, 0] = 1; m0[row, 1] = phi[0]; m0[row, dIdx] = -1; row++;
        m0[row, 0] = -j * kz[0]; m0[row, 1] = j * kz[0] * phi[0]; m0[row, dIdx] = -j * kz0; row++;
        for (int i = 0; i < n - 1; i++)
        {
            int a = 2 * i, b = 2 * (i + 1);
            m0[row, a] = phi[i]; m0[row, a + 1] = 1; m0[row, b] = -1; m0[row, b + 1] = -phi[i + 1]; row++;
            if (i == m)
            {
                m0[row, a] = j * kz[i] * phi[i]; m0[row, a + 1] = -j * kz[i];
                m0[row, b] = -j * kz[i + 1]; m0[row, b + 1] = j * kz[i + 1] * phi[i + 1];
                rhs[row] = -2 * RfConstants.Mu0;
            }
            else
            {
                m0[row, a] = -kz[i] * phi[i]; m0[row, a + 1] = kz[i];
                m0[row, b] = kz[i + 1]; m0[row, b + 1] = -kz[i + 1] * phi[i + 1];
            }
            row++;
        }
        m0[row, t] = phi[n - 1]; m0[row, t + 1] = 1; m0[row, cIdx] = -1; row++;
        m0[row, cIdx] = -j * kz0; m0[row, t] = j * kz[n - 1] * phi[n - 1];
        m0[row, t + 1] = -j * kz[n - 1];
        rhs[row] = m == n - 1 ? -2 * RfConstants.Mu0 : Complex.Zero;
        return (m0, rhs, cIdx, dIdx);
    }

    internal static (ComplexDenseMatrix M, Complex[] Rhs, int SIdx, int EIdx) TmSystemUngrounded(
        LayerSpectral sp, Complex kz0, Complex[] axAt, Complex c, Complex d)
    {
        var (eps, kz, phi) = sp;
        int n = kz.Length, size = 2 * n + 2, sIdx = 2 * n, eIdx = 2 * n + 1, t = 2 * (n - 1);
        var j = Complex.ImaginaryOne;
        var m = new ComplexDenseMatrix(size, size);
        var rhs = new Complex[size];
        int row = 0;
        // Bottom face: ã_z continuous; (1/ε₀)∂ã_z(0⁺) − ∂ã_z(0⁻) = D·(1 − 1/ε₀).
        m[row, 0] = 1; m[row, 1] = phi[0]; m[row, eIdx] = -1; row++;
        Complex y0 = j * kz[0] / eps[0];
        m[row, 0] = -y0; m[row, 1] = y0 * phi[0]; m[row, eIdx] = -j * kz0;
        rhs[row] = d * (1 - 1 / eps[0]); row++;
        for (int i = 0; i < n - 1; i++)
        {
            int a = 2 * i, b = 2 * (i + 1);
            m[row, a] = phi[i]; m[row, a + 1] = 1; m[row, b] = -1; m[row, b + 1] = -phi[i + 1]; row++;
            Complex yi = j * kz[i] / eps[i], yj = j * kz[i + 1] / eps[i + 1];
            m[row, b] = -yj; m[row, b + 1] = yj * phi[i + 1];
            m[row, a] = yi * phi[i]; m[row, a + 1] = -yi;
            rhs[row] = axAt[i] * (1 / eps[i] - 1 / eps[i + 1]); row++;
        }
        Complex epsTop = eps[n - 1], yTop = j * kz[n - 1] / epsTop;
        m[row, t] = phi[n - 1]; m[row, t + 1] = 1; m[row, sIdx] = -1; row++;
        m[row, sIdx] = -j * kz0; m[row, t] = yTop * phi[n - 1]; m[row, t + 1] = -yTop;
        rhs[row] = c * (1 / epsTop - 1);
        return (m, rhs, sIdx, eIdx);
    }

    /// <summary>The solved amplitudes of one spectral point: the TE and TM vectors and the four
    /// half-space amplitudes C, S (above) and D, E (below).</summary>
    private static (LayerSpectral Sp, Complex[] Te, Complex[] Tm, Complex C, Complex S, Complex D, Complex E) SolveUngrounded(
        LayeredStackup stackup, double k0, Complex kRho, Complex kz0, int m)
    {
        int n = stackup.Layers.Count;
        if (m < 0 || m >= n)
            throw new ArgumentOutOfRangeException(nameof(m), $"Source interface {m} is out of range for a {n}-layer stackup.");
        var sp = Spectral(stackup, k0, kRho);
        var (teM, teRhs, cIdx, dIdx) = TeSystemUngrounded(sp, kz0, m);
        var te = ComplexLu.Factor(teM).Solve(teRhs);
        Complex c = te[cIdx], d = te[dIdx];
        var axAt = AxAtInterfaces(te, sp.Phi, c);
        var (tmM, tmRhs, sIdx, eIdx) = TmSystemUngrounded(sp, kz0, axAt, c, d);
        var tm = ComplexLu.Factor(tmM).Solve(tmRhs);
        return (sp, te, tm, c, tm[sIdx], d, tm[eIdx]);
    }

    /// <summary>Both potential kernels of the ungrounded stack at interface m, read out as
    /// <see cref="EvaluateInterior"/> does: G̃_A = A_x(z_m), K̃_Φ = (A_x + ∂_zã_z)/(µ₀ε₀ε_above), from
    /// the region just above (the air when m is the top).</summary>
    public static (Complex GA, Complex KPhi) EvaluateUngrounded(LayeredStackup stackup, double k0, Complex kRho,
        Complex kz0, int m)
    {
        var (sp, te, tm, c, s, _, _) = SolveUngrounded(stackup, k0, kRho, kz0, m);
        int n = stackup.Layers.Count;
        var j = Complex.ImaginaryOne;
        var axAt = AxAtInterfaces(te, sp.Phi, c);
        Complex axm = axAt[m];
        Complex dAzAbove, epsAbove;
        if (m == n - 1)
        {
            dAzAbove = -j * kz0 * s;
            epsAbove = Complex.One;
        }
        else
        {
            int qb = 2 * (m + 1);
            dAzAbove = -j * sp.Kz[m + 1] * tm[qb] + j * sp.Kz[m + 1] * sp.Phi[m + 1] * tm[qb + 1];
            epsAbove = sp.Eps[m + 1];
        }
        return (axm, (axm + dAzAbove) / (RfConstants.Mu0 * RfConstants.Eps0 * epsAbove));
    }

    /// <summary>The radiation amplitudes into BOTH half-spaces: above (C, S/C) and below (D, E/D),
    /// each in <see cref="RadiationAmplitude"/>'s normalization — G̃_A per unit horizontal current
    /// and the A_z/A_x ratio per −j·k⃗_ρ·J̃.</summary>
    public static (Complex GaUp, Complex AzRatioUp, Complex GaDown, Complex AzRatioDown) RadiationAmplitudesUngrounded(
        LayeredStackup stackup, double k0, Complex kRho, Complex kz0, int m)
    {
        var (_, _, _, c, s, d, e) = SolveUngrounded(stackup, k0, kRho, kz0, m);
        return (c, c == Complex.Zero ? Complex.Zero : s / c, d, d == Complex.Zero ? Complex.Zero : e / d);
    }

    /// <summary>The ungrounded kernels' residues at a slab mode, by the contour integral of the
    /// kernels round it (radius under the distance to k₀, the branch point, and to every other pole).</summary>
    public static (Complex ResidueA, Complex ResiduePhi) PoleResiduesUngrounded(
        LayeredStackup stackup, double k0, Complex kp, int m, double radius, int points = 64)
    {
        Complex sumA = Complex.Zero, sumPhi = Complex.Zero;
        for (int i = 0; i < points; i++)
        {
            Complex offset = Complex.FromPolarCoordinates(radius, 2 * Math.PI * (i + 0.5) / points);
            Complex kRho = kp + offset;
            var (a, phi) = EvaluateUngrounded(stackup, k0, kRho, SpectralKernels.Kz(k0 * k0, kRho), m);
            sumA += a * offset;
            sumPhi += phi * offset;
        }
        return (sumA / points, sumPhi / points);
    }
}
