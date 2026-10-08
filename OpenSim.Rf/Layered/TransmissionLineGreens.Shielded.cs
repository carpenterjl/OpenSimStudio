using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The two-plane (shielded) transmission-line Green's function (FU-30): the stack between the
/// ground at z = 0 and a second PEC plane on its top, the source and observation at an interior
/// interface m — a stripline's strip. The boundary-value problem is the open stack's
/// (<see cref="TeSystemInterior"/>, <see cref="TmSystem"/>) with the air above replaced by the
/// second plane:
///  • <b>A_x (TE)</b> vanishes on it (Dirichlet), as on the ground;
///  • <b>A_z (TM)</b> has ∂_zA_z = 0 on it (Neumann), as on the ground;
/// and with no air there is no region 0, no k_z0, and no radiation: the spectral kernels are
/// even in every layer's k_z, so they are meromorphic in k_ρ — poles (the parallel-plate modes)
/// and no branch point. The read-out is the interior one (<see cref="EvaluateInterior"/>), from
/// the layer just above the source.
///
/// The unknown vectors keep the open systems' layout ([P_0^+, P_0^-, …, C] and [Q_0^+, …, S]) so
/// the interface helpers apply unchanged; C and S, the region-0 amplitudes, are pinned to zero.
/// </summary>
internal static partial class TransmissionLineGreens
{
    /// <summary>The TE (A_x) system with the source at interior interface <paramref name="m"/>
    /// and a PEC plane on the stack's top.</summary>
    internal static (ComplexDenseMatrix M, Complex[] Rhs, int CIdx) TeSystemShielded(LayerSpectral sp, int m)
    {
        var (_, kz, phi) = sp;
        int n = kz.Length, size = 2 * n + 1, cIdx = 2 * n, t = 2 * (n - 1);
        var j = Complex.ImaginaryOne;
        var m0 = new ComplexDenseMatrix(size, size);
        var rhs = new Complex[size];
        int row = 0;
        m0[row, 0] = 1; m0[row, 1] = phi[0]; row++;                       // ground A_x = 0
        for (int i = 0; i < n - 1; i++)
        {
            int a = 2 * i, b = 2 * (i + 1);
            m0[row, a] = phi[i]; m0[row, a + 1] = 1; m0[row, b] = -1; m0[row, b + 1] = -phi[i + 1]; row++;
            if (i == m)
            {
                // Source jump at the interface: deriv_above − deriv_below = −2µ₀.
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
        m0[row, t] = phi[n - 1]; m0[row, t + 1] = 1; row++;               // top plane A_x = 0
        m0[row, cIdx] = 1;                                                 // no region 0: C = 0
        return (m0, rhs, cIdx);
    }

    /// <summary>The TM (A_z) system under a PEC top: ∂_zA_z = 0 there (the bottom row's form,
    /// divided through by jk_z), driven by the ε-contrast sources at the interior interfaces only —
    /// the top is a conductor, not a contrast.</summary>
    internal static (ComplexDenseMatrix M, Complex[] Rhs, int SIdx) TmSystemShielded(
        LayerSpectral sp, Complex[] axAt)
    {
        var (eps, kz, phi) = sp;
        int n = kz.Length, size = 2 * n + 1, sIdx = 2 * n, t = 2 * (n - 1);
        var j = Complex.ImaginaryOne;
        var m = new ComplexDenseMatrix(size, size);
        var rhs = new Complex[size];
        int row = 0;
        m[row, 0] = -1; m[row, 1] = phi[0]; row++;                     // ground ∂_zA_z = 0
        for (int i = 0; i < n - 1; i++)
        {
            int a = 2 * i, b = 2 * (i + 1);
            m[row, a] = phi[i]; m[row, a + 1] = 1; m[row, b] = -1; m[row, b + 1] = -phi[i + 1]; row++;
            Complex yi = j * kz[i] / eps[i], yj = j * kz[i + 1] / eps[i + 1];
            m[row, b] = -yj; m[row, b + 1] = yj * phi[i + 1];
            m[row, a] = yi * phi[i]; m[row, a + 1] = -yi;
            rhs[row] = axAt[i] * (1 / eps[i] - 1 / eps[i + 1]); row++;
        }
        m[row, t] = -phi[n - 1]; m[row, t + 1] = 1; row++;             // top plane ∂_zA_z = 0
        m[row, sIdx] = 1;                                              // no region 0: S = 0
        return (m, rhs, sIdx);
    }

    /// <summary>Both potential kernels between two PEC planes, source and observation at interior
    /// interface <paramref name="m"/> (0 ≤ m &lt; n − 1: at least one layer on each side). The
    /// normalization is <see cref="EvaluateInterior"/>'s — G̃_A = A_x(z_m), K̃_Φ = (A_x + ∂_zã_z)/
    /// (µ₀ε₀ε_above) — so in one homogeneous dielectric K̃_Φ·ε₀εr = G̃_A/µ₀, both the Dirichlet
    /// Green's function of the plate pair.</summary>
    public static (Complex GA, Complex KPhi) EvaluateShielded(LayeredStackup stackup, double k0, Complex kRho, int m)
    {
        int n = stackup.Layers.Count;
        if (m < 0 || m >= n - 1)
            throw new ArgumentOutOfRangeException(nameof(m),
                $"A source between two planes needs a layer on each side: interface {m} of a {n}-layer stack.");
        var sp = Spectral(stackup, k0, kRho);
        var (teM, teRhs, _) = TeSystemShielded(sp, m);
        var teSol = ComplexLu.Factor(teM).Solve(teRhs);
        var axAt = AxAtInterfaces(teSol, sp.Phi, Complex.Zero);
        var (tmM, tmRhs, _) = TmSystemShielded(sp, axAt);
        var tmSol = ComplexLu.Factor(tmM).Solve(tmRhs);
        var j = Complex.ImaginaryOne;
        int qb = 2 * (m + 1);
        Complex dAzAbove = -j * sp.Kz[m + 1] * tmSol[qb] + j * sp.Kz[m + 1] * sp.Phi[m + 1] * tmSol[qb + 1];
        Complex axm = axAt[m];
        return (axm, (axm + dAzAbove) / (RfConstants.Mu0 * RfConstants.Eps0 * sp.Eps[m + 1]));
    }

    /// <summary>The residues of both shielded kernels at a parallel-plate mode <paramref name="kp"/>,
    /// (1/2πj)∮F dk_ρ on a circle of radius <paramref name="radius"/> round it, by the trapezoidal
    /// rule — exponentially accurate for a function analytic in an annulus round the circle, which
    /// F is: it is meromorphic in k_ρ here (no k_z0, and even in every layer's k_z), and the
    /// radius is kept under half the distance to any other pole. The null-vector residue the
    /// open stack uses (<see cref="PoleResidues"/>) does not carry over: in a guide filled with one
    /// dielectric the TEM pole sits exactly where every layer's k_z vanishes and the reduced basis
    /// is degenerate, so the matrix is singular there for a reason that is not the mode.</summary>
    public static (Complex ResidueA, Complex ResiduePhi) PoleResiduesShielded(
        LayeredStackup stackup, double k0, Complex kp, int m, double radius, int points = 64)
    {
        if (!(radius > 0)) throw new ArgumentOutOfRangeException(nameof(radius));
        Complex sumA = Complex.Zero, sumPhi = Complex.Zero;
        for (int i = 0; i < points; i++)
        {
            double angle = 2 * Math.PI * (i + 0.5) / points;
            Complex offset = Complex.FromPolarCoordinates(radius, angle);
            var (a, phi) = EvaluateShielded(stackup, k0, kp + offset, m);
            // (1/2πj)∮F dk = (1/2πj)Σ F·(j·offset)·Δθ = (1/N)Σ F·offset.
            sumA += a * offset;
            sumPhi += phi * offset;
        }
        return (sumA / points, sumPhi / points);
    }
}
