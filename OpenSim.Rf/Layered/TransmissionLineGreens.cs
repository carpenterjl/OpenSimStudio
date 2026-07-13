using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The Stage F transmission-line Green's function: the single-slab MPIE formulation-C
/// kernels (<see cref="SpectralKernels"/>) generalized to an N-layer grounded stackup
/// (<see cref="LayeredStackup"/>). It solves the SAME spectral boundary-value problem the
/// single-slab oracle solves — only now with N slabs — and returns the two potential
/// kernels G̃_A^xx and K̃_Φ.
///
/// The physics splits exactly as it does for one slab:
///  • <b>A_x (TE)</b> is a scalar Helmholtz Green's function: A_x = 0 on the PEC ground,
///    outgoing above the stack, a unit HED source jump at the top plane z = d, and A_x with
///    ∂_zA_x continuous at every interior interface (µ is uniform). It decouples from A_z.
///  • <b>A_z (TM)</b> is a transmission line of characteristic admittance ∝ k_z/ε, OPEN at
///    the ground (∂_zA_z = 0), outgoing above, driven by a shunt current source
///    A_x(h)·(1/ε_below − 1/ε_above) at EVERY interface — the permittivity contrast is what
///    launches A_z from the horizontal current. (For one slab that single source at z = d
///    reproduces the closed-form N-factor.)
/// Then K̃_Φ = (C − j k_z0 S)/(µ₀ε₀), with C the region-0 A_x amplitude and S the region-0
/// A_z amplitude — identical to the single-slab oracle's read-out.
///
/// Numerics: each layer's field is written in a locally-DECAYING reduced basis,
///   f(z) = A⁺ e^{−jk_z(z − z_bottom)} + A⁻ e^{+jk_z(z − z_top)},
/// so on the Im(k_z) ≤ 0 branch both exponentials have non-positive real exponent inside
/// their own layer and NOTHING overflows, no matter how thick the stack or how far up the
/// k_ρ tail — the multi-layer analog of <see cref="SpectralKernels.ReducedTrig"/>. The
/// per-k_ρ system is a small dense complex solve (2 unknowns per layer per polarization);
/// N ≤ a handful of layers and the table has a few hundred knots, so it is negligible.
/// </summary>
internal static class TransmissionLineGreens
{
    /// <summary>The per-k_ρ layer quantities the recursion is built from: complex
    /// permittivity, vertical wavenumber, and the reduced (decaying) per-layer phase.</summary>
    internal readonly record struct LayerSpectral(Complex[] Eps, Complex[] Kz, Complex[] Phi);

    internal static LayerSpectral Spectral(LayeredStackup stackup, double k0, Complex kRho)
    {
        int n = stackup.Layers.Count;
        double k0Sq = k0 * k0;
        var kz = new Complex[n];
        var eps = new Complex[n];
        var phi = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            var layer = stackup.Layers[i];
            eps[i] = layer.ComplexPermittivity;
            kz[i] = SpectralKernels.Kz(eps[i] * k0Sq, kRho);
            phi[i] = ReducedPhase(kz[i] * layer.ThicknessMeters);
        }
        return new LayerSpectral(eps, kz, phi);
    }

    /// <summary>The TE (A_x) dense system in the reduced basis: value + derivative
    /// continuity, PEC Dirichlet at the ground, HED source jump at the top. Unknowns
    /// [P_0^+, P_0^-, …, C]; C at index 2n. Decoupled from A_z.</summary>
    internal static (ComplexDenseMatrix M, Complex[] Rhs, int CIdx) TeSystem(
        LayerSpectral sp, Complex kz0)
    {
        var (_, kz, phi) = sp;
        int n = kz.Length, size = 2 * n + 1, cIdx = 2 * n, t = 2 * (n - 1);
        var j = Complex.ImaginaryOne;
        var m = new ComplexDenseMatrix(size, size);
        var rhs = new Complex[size];
        int row = 0;
        m[row, 0] = 1; m[row, 1] = phi[0]; row++;                       // ground A_x = 0
        for (int i = 0; i < n - 1; i++)
        {
            int a = 2 * i, b = 2 * (i + 1);
            m[row, a] = phi[i]; m[row, a + 1] = 1; m[row, b] = -1; m[row, b + 1] = -phi[i + 1]; row++;
            m[row, a] = -kz[i] * phi[i]; m[row, a + 1] = kz[i];
            m[row, b] = kz[i + 1]; m[row, b + 1] = -kz[i + 1] * phi[i + 1]; row++;
        }
        m[row, t] = phi[n - 1]; m[row, t + 1] = 1; m[row, cIdx] = -1; row++;
        m[row, cIdx] = -j * kz0; m[row, t] = j * kz[n - 1] * phi[n - 1];
        m[row, t + 1] = -j * kz[n - 1]; rhs[row] = -2 * RfConstants.Mu0;
        return (m, rhs, cIdx);
    }

    /// <summary>The TM (A_z) dense system in the reduced basis: A_z continuity, open at the
    /// ground (∂_zA_z = 0), radiation above, driven by the shunt source A_x(h)(1/ε_below −
    /// 1/ε_above) at every interface. Unknowns [Q_0^+, Q_0^-, …, S]; S at index 2n. The
    /// right-hand side needs the TE interface values <paramref name="axAt"/> and top C.</summary>
    internal static (ComplexDenseMatrix M, Complex[] Rhs, int SIdx) TmSystem(
        LayerSpectral sp, Complex kz0, Complex[] axAt, Complex c)
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
        Complex epsTop = eps[n - 1], yTop = j * kz[n - 1] / epsTop;
        m[row, t] = phi[n - 1]; m[row, t + 1] = 1; m[row, sIdx] = -1; row++;
        m[row, sIdx] = -j * kz0; m[row, t] = yTop * phi[n - 1]; m[row, t + 1] = -yTop;
        rhs[row] = c * (1 / epsTop - 1);
        return (m, rhs, sIdx);
    }

    /// <summary>A_x at every interface top h_i from the solved TE unknowns (h_{n-1} = d is
    /// exactly C) — these are the A_z shunt-source amplitudes.</summary>
    internal static Complex[] AxAtInterfaces(Complex[] teSol, Complex[] phi, Complex c)
    {
        int n = phi.Length;
        var axAt = new Complex[n];
        for (int i = 0; i < n - 1; i++) axAt[i] = phi[i] * teSol[2 * i] + teSol[2 * i + 1];
        axAt[n - 1] = c;
        return axAt;
    }

    /// <summary>Both potential kernels at one k_ρ, with k_z0 supplied in closed form from
    /// the Sommerfeld contour (as <see cref="SpectralKernels.Evaluate(SubstrateStackup,double,Complex,Complex)"/>
    /// does — avoids the k₀²−k_ρ² cancellation at the branch point).</summary>
    public static (Complex GA, Complex KPhi) Evaluate(
        LayeredStackup stackup, double k0, Complex kRho, Complex kz0)
    {
        var sp = Spectral(stackup, k0, kRho);
        var (teM, teRhs, cIdx) = TeSystem(sp, kz0);
        var teSol = ComplexLu.Factor(teM).Solve(teRhs);
        Complex c = teSol[cIdx];
        var axAt = AxAtInterfaces(teSol, sp.Phi, c);

        var (tmM, tmRhs, sIdx) = TmSystem(sp, kz0, axAt, c);
        Complex s = ComplexLu.Factor(tmM).Solve(tmRhs)[sIdx];

        var j = Complex.ImaginaryOne;
        return (c, (c - j * kz0 * s) / (RfConstants.Mu0 * RfConstants.Eps0));
    }

    /// <summary>The overload that computes k_z0 from k_ρ itself (for callers not on the
    /// Sommerfeld contour, e.g. spot checks and gates).</summary>
    public static (Complex GA, Complex KPhi) Evaluate(LayeredStackup stackup, double k0, Complex kRho) =>
        Evaluate(stackup, k0, kRho, SpectralKernels.Kz(k0 * k0, kRho));

    /// <summary>The TE (A_x) system for a HED source at an arbitrary INTERIOR interface
    /// <paramref name="m"/> (top of layer m; 0 ≤ m ≤ n−1) — the covered-patch case where
    /// dielectric sits ABOVE the metal. The −2µ₀ derivative jump moves from the top plane to
    /// interface m, and the top interface becomes plain radiation continuity. Value continuity
    /// holds at every interface (A_x is continuous through the current sheet). m = n−1 recovers
    /// <see cref="TeSystem"/> exactly (gated). Unknowns [P_0^+, P_0^-, …, C]; C at 2n.</summary>
    internal static (ComplexDenseMatrix M, Complex[] Rhs, int CIdx) TeSystemInterior(
        LayerSpectral sp, Complex kz0, int m)
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
                // Source jump at the interior interface: deriv_above − deriv_below = −2µ₀.
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
        m0[row, t] = phi[n - 1]; m0[row, t + 1] = 1; m0[row, cIdx] = -1; row++;   // top value → C
        // Top derivative: the SAME LHS (deriv_above − deriv_below); RHS is the source only
        // when the source IS the top plane (m == n−1), otherwise plain radiation continuity.
        m0[row, cIdx] = -j * kz0; m0[row, t] = j * kz[n - 1] * phi[n - 1];
        m0[row, t + 1] = -j * kz[n - 1];
        rhs[row] = m == n - 1 ? -2 * RfConstants.Mu0 : Complex.Zero;
        return (m0, rhs, cIdx);
    }

    /// <summary>Both potential kernels for a source at interior interface <paramref name="m"/>,
    /// READ OUT at that same plane z_m (source ≡ observation, what the covered-patch MoM needs):
    /// G̃_A^xx = A_x(z_m) and K̃_Φ = (A_x(z_m) + ∂_z ã_z(z_m))/(µ₀ε₀), the divergence read-out
    /// evaluated from the region just ABOVE the source (matching the m = n−1 top read-out
    /// C − jk_z0 S, where ∂_z ã_z = −jk_z0 S in region 0). The TM system is UNCHANGED — the
    /// x-directed HED never sources ã_z directly; ã_z is launched only by the ε-contrasts (× A_x)
    /// the existing <see cref="TmSystem"/> already carries. When ε is continuous across the metal
    /// (patch buried in a homogeneous slab, or air cover) ∂_z ã_z is continuous at z_m and this
    /// read-out is unambiguous.</summary>
    public static (Complex GA, Complex KPhi) EvaluateInterior(
        LayeredStackup stackup, double k0, Complex kRho, Complex kz0, int m)
    {
        int n = stackup.Layers.Count;
        if (m < 0 || m >= n)
            throw new ArgumentOutOfRangeException(nameof(m),
                $"Source interface {m} is out of range for a {n}-layer stackup.");
        var sp = Spectral(stackup, k0, kRho);
        var (teM, teRhs, cIdx) = TeSystemInterior(sp, kz0, m);
        var teSol = ComplexLu.Factor(teM).Solve(teRhs);
        Complex c = teSol[cIdx];
        var axAt = AxAtInterfaces(teSol, sp.Phi, c);

        var (tmM, tmRhs, sIdx) = TmSystem(sp, kz0, axAt, c);
        var tmSol = ComplexLu.Factor(tmM).Solve(tmRhs);
        Complex s = tmSol[sIdx];

        var j = Complex.ImaginaryOne;
        Complex axm = axAt[m];
        // ∂_z ã_z(z_m) from ABOVE: region 0 (air) when the source is the top plane, else the
        // reduced-basis derivative of layer m+1 at its bottom node (= z_m).
        Complex dAzAbove;
        Complex epsAbove;
        if (m == n - 1)
        {
            dAzAbove = -j * kz0 * s;
            epsAbove = Complex.One;                          // region 0 is air
        }
        else
        {
            int qb = 2 * (m + 1);
            Complex qp = tmSol[qb], qm = tmSol[qb + 1];
            dAzAbove = -j * sp.Kz[m + 1] * qp + j * sp.Kz[m + 1] * sp.Phi[m + 1] * qm;
            epsAbove = sp.Eps[m + 1];
        }
        // Φ = −∇·A/(jωµ₀ε₀·εr_local): the divergence read-out divides by the permittivity of
        // the region it is read in (here, ABOVE the source). Invisible for the top source
        // (air, εr = 1) — this is why the single-slab path never needed it — but a buried
        // charge in εr must carry the 1/εr of its host medium (c₀ = 1/εr, the free-medium
        // Coulomb kernel). Unambiguous where ε is continuous across the metal.
        return (axm, (axm + dAzAbove) / (RfConstants.Mu0 * RfConstants.Eps0 * epsAbove));
    }

    /// <summary>The interior-source overload computing k_z0 from k_ρ itself.</summary>
    public static (Complex GA, Complex KPhi) EvaluateInterior(
        LayeredStackup stackup, double k0, Complex kRho, int m) =>
        EvaluateInterior(stackup, k0, kRho, SpectralKernels.Kz(k0 * k0, kRho), m);

    /// <summary>The region-0 (top air) radiation amplitudes the far field needs from a source
    /// at interface <paramref name="m"/> (m = n−1 ⇒ the coplanar-top plane, else a covered patch):
    /// G̃_A = C (the A_x amplitude per unit horizontal current) and W̃ = S/C (the A_z/A_x
    /// amplitude ratio per −j·k⃗_ρ·J̃; zero at εr = 1 — no TM coupling without a contrast). These
    /// are the SAME C, S the potential read-out uses, WITHOUT the K̃_Φ divergence normalization:
    /// the far field radiates the raw region-0 vector potential. At N = 1, m = 0 this equals
    /// (<see cref="SpectralKernels.Evaluate"/>.GA, <see cref="SpectralKernels.AzRatio"/>) — an
    /// identity gate, not a re-derivation. Source depth changes C and S (the cover loads the
    /// radiation) but the region-0 propagation is unchanged, so the far-field formula is the same.</summary>
    public static (Complex GA, Complex AzRatio) RadiationAmplitude(
        LayeredStackup stackup, double k0, Complex kRho, Complex kz0, int m)
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
        var (tmM, tmRhs, sIdx) = TmSystem(sp, kz0, axAt, c);
        Complex s = ComplexLu.Factor(tmM).Solve(tmRhs)[sIdx];
        return (c, s / c);
    }

    /// <summary>The TE surface-wave dispersion function: its zeros are the TE poles (of
    /// both G̃_A and K̃_Φ). D̃_TE = jk_z0 + jk_z,top·(1 − R)/(1 + R), R the bottom-up TE
    /// reflection at the top of the stack (ground = short, Γ = −1). Proportional (never
    /// equal) to the single-slab reduced D̂_TE — only the ROOTS are used, so the constant
    /// is irrelevant; residues come from the matrix, not this function's derivative.</summary>
    public static Complex TeDispersion(LayeredStackup stackup, double k0, Complex kRho)
    {
        var sp = Spectral(stackup, k0, kRho);
        var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
        Complex r = Reflection(sp, groundVoltageReflection: -1, tmAdmittance: false);
        var j = Complex.ImaginaryOne;
        Complex kzTop = sp.Kz[^1];
        return j * kz0 + j * kzTop * (1 - r) / (1 + r);
    }

    /// <summary>The TM surface-wave dispersion function: its zeros are the TM poles. The
    /// A_z line has admittance k_z/ε and is OPEN at the ground (∂_zA_z = 0, Γ = +1);
    /// j·(Y_up + Y_down) with Y_up = k_z0 (air). The leading j matches <see cref="TeDispersion"/>'s
    /// convention: on the lossless bound-mode segment (k₀, k₀√εr_max) a lossless stack's
    /// admittance is reactive and jk_z0 = γ₀, so the function is REAL there — the root
    /// finder works on that real value. Zeros only; residues come from the matrix.</summary>
    public static Complex TmDispersion(LayeredStackup stackup, double k0, Complex kRho)
    {
        var sp = Spectral(stackup, k0, kRho);
        var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
        Complex r = Reflection(sp, groundVoltageReflection: 1, tmAdmittance: true);
        Complex yTop = sp.Kz[^1] / sp.Eps[^1];
        return Complex.ImaginaryOne * (kz0 + yTop * (1 - r) / (1 + r));
    }

    /// <summary>Bottom-up voltage-reflection recursion at the top of the stack. The ground
    /// terminates layer 0 (<paramref name="groundVoltageReflection"/>: −1 short for TE,
    /// +1 open for the A_z line). Each interface uses the Fresnel voltage reflection from
    /// the characteristic admittances (k_z for TE, k_z/ε for TM). Every step multiplies by
    /// φ² = e^{−2jk_z t} with |φ| ≤ 1, so |R| ≤ 1 and nothing overflows.</summary>
    private static Complex Reflection(LayerSpectral sp, double groundVoltageReflection, bool tmAdmittance)
    {
        var (eps, kz, phi) = sp;
        int n = kz.Length;
        Complex Y(int i) => tmAdmittance ? kz[i] / eps[i] : kz[i];
        Complex r = groundVoltageReflection * phi[0] * phi[0];
        for (int i = 1; i < n; i++)
        {
            Complex ya = Y(i), yb = Y(i - 1);
            Complex gamma = (ya - yb) / (ya + yb);          // wave in layer i onto layer i-1
            r = (gamma + r) / (1 + gamma * r);
            r *= phi[i] * phi[i];
        }
        return r;
    }

    /// <summary>The residues of G̃_A and K̃_Φ at a surface-wave pole <paramref name="kp"/>
    /// (already located), computed ANALYTICALLY from the singular TE or TM system by the
    /// null-vector residue of a matrix inverse: near a simple pole M(k_ρ)⁻¹ ~ u vᵀ/((k_ρ−k_p)
    /// vᵀM′u), so Res x = u (vᵀb)/(vᵀM′u), with u, v the right/left null vectors of M(k_p)
    /// and M′ the (finite-difference) k_ρ derivative. No closed-form numerator is needed —
    /// the mode normalization falls out of the null vectors — and it is general for any N.
    ///
    /// G̃_A = C carries ONLY TE poles (ResidueA = 0 at a TM pole). K̃_Φ = (C − jk_z0 S)/(µ₀ε₀)
    /// carries both: at a TE pole C is singular and S inherits it through the A_z source; at
    /// a TM pole only S is singular. Gated against the single-slab analytic residues and a
    /// Richardson limit (see the F-pole tests).</summary>
    public static (Complex ResidueA, Complex ResiduePhi) PoleResidues(
        LayeredStackup stackup, double k0, Complex kp, bool isTm)
    {
        var sp = Spectral(stackup, k0, kp);
        var kz0 = SpectralKernels.Kz(k0 * k0, kp);
        var j = Complex.ImaginaryOne;
        double normFactor = RfConstants.Mu0 * RfConstants.Eps0;

        var (teM, teRhs, cIdx) = TeSystem(sp, kz0);
        if (!isTm)
        {
            // TE pole: C (and every A_x interface value) is singular; S inherits it.
            var resX = MatrixResidue(teM, MatrixDerivative(stackup, k0, kp, tm: false), teRhs);
            Complex resC = resX[cIdx];
            var resAx = AxAtInterfaces(resX, sp.Phi, resC);
            var (tmM, resB, sIdx) = TmSystem(sp, kz0, resAx, resC);   // M regular, RHS = Res(b)
            Complex resS = ComplexLu.Factor(tmM).Solve(resB)[sIdx];
            return (resC, (resC - j * kz0 * resS) / normFactor);
        }
        else
        {
            // TM pole: C, A_x regular; only S is singular.
            var teSol = ComplexLu.Factor(teM).Solve(teRhs);
            Complex c = teSol[cIdx];
            var axAt = AxAtInterfaces(teSol, sp.Phi, c);
            var (tmM, tmRhs, sIdx) = TmSystem(sp, kz0, axAt, c);
            Complex resS = MatrixResidue(tmM, MatrixDerivative(stackup, k0, kp, tm: true), tmRhs)[sIdx];
            return (Complex.Zero, -j * kz0 * resS / normFactor);
        }
    }

    /// <summary>The residues of the INTERIOR-source kernels at a surface-wave pole (covered
    /// patch: source & observation at interface <paramref name="m"/>). The pole LOCATION and the
    /// mode (null vector) are source-independent — only the RHS (the interior source, via
    /// <see cref="TeSystemInterior"/>) and the read-out plane move to z_m, with the same
    /// ÷ε_above divergence normalization as <see cref="EvaluateInterior"/>. m = n−1 reproduces
    /// <see cref="PoleResidues"/> (gated).</summary>
    public static (Complex ResidueA, Complex ResiduePhi) PoleResiduesInterior(
        LayeredStackup stackup, double k0, Complex kp, bool isTm, int m)
    {
        int n = stackup.Layers.Count;
        var sp = Spectral(stackup, k0, kp);
        var kz0 = SpectralKernels.Kz(k0 * k0, kp);
        var j = Complex.ImaginaryOne;
        Complex epsAbove = m == n - 1 ? Complex.One : sp.Eps[m + 1];
        double eps0mu0 = RfConstants.Mu0 * RfConstants.Eps0;
        Complex norm = eps0mu0 * epsAbove;

        // ∂_z ã_z(z_m) from ABOVE, read off a solved (or residue) TM vector — region 0 when
        // the source is the top plane, else layer m+1's reduced-basis derivative at its bottom.
        Complex DAzAbove(Complex[] tmVec, Complex sVal) =>
            m == n - 1
                ? -j * kz0 * sVal
                : -j * sp.Kz[m + 1] * tmVec[2 * (m + 1)]
                    + j * sp.Kz[m + 1] * sp.Phi[m + 1] * tmVec[2 * (m + 1) + 1];

        var (teM, teRhs, cIdx) = TeSystemInterior(sp, kz0, m);
        if (!isTm)
        {
            // TE pole: A_x (and every interface value) is singular; ã_z inherits it through
            // the A_z source (regular TM matrix, residue RHS).
            var resX = MatrixResidue(teM, MatrixDerivativeTeInterior(stackup, k0, kp, m), teRhs);
            Complex resC = resX[cIdx];
            var resAx = AxAtInterfaces(resX, sp.Phi, resC);
            var (tmM, resB, sIdx) = TmSystem(sp, kz0, resAx, resC);
            var resTm = ComplexLu.Factor(tmM).Solve(resB);
            Complex resAxm = resAx[m];
            return (resAxm, (resAxm + DAzAbove(resTm, resTm[sIdx])) / norm);
        }
        else
        {
            // TM pole: A_x regular ⇒ zero residue; only ã_z is singular.
            var teSol = ComplexLu.Factor(teM).Solve(teRhs);
            Complex c = teSol[cIdx];
            var axAt = AxAtInterfaces(teSol, sp.Phi, c);
            var (tmM, tmRhs, sIdx) = TmSystem(sp, kz0, axAt, c);
            var resTm = MatrixResidue(tmM, MatrixDerivative(stackup, k0, kp, tm: true), tmRhs);
            return (Complex.Zero, DAzAbove(resTm, resTm[sIdx]) / norm);
        }
    }

    /// <summary>Central-difference k_ρ derivative of the INTERIOR TE pole matrix (the top-source
    /// <see cref="MatrixDerivative"/> stays untouched, so the F-pole residue pins never move).</summary>
    private static ComplexDenseMatrix MatrixDerivativeTeInterior(
        LayeredStackup stackup, double k0, Complex kp, int m)
    {
        Complex delta = 1e-6 * (kp.Magnitude == 0 ? 1 : kp.Magnitude);
        ComplexDenseMatrix Build(Complex kRho)
        {
            var sp = Spectral(stackup, k0, kRho);
            var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
            return TeSystemInterior(sp, kz0, m).M;
        }
        var plus = Build(kp + delta);
        var minus = Build(kp - delta);
        int size = plus.Rows;
        var d = new ComplexDenseMatrix(size, size);
        for (int i = 0; i < size; i++)
            for (int k = 0; k < size; k++) d[i, k] = (plus[i, k] - minus[i, k]) / (2 * delta);
        return d;
    }

    /// <summary>Res x = u (vᵀb)/(vᵀM′u) for the singular system M x = b at a simple pole.</summary>
    private static Complex[] MatrixResidue(ComplexDenseMatrix m, ComplexDenseMatrix mPrime, Complex[] b)
    {
        int n = m.Rows;
        var u = NullVector(m, transpose: false);
        var v = NullVector(m, transpose: true);
        Complex vb = Complex.Zero, vmu = Complex.Zero;
        for (int i = 0; i < n; i++)
        {
            vb += v[i] * b[i];
            Complex mu = Complex.Zero;
            for (int k = 0; k < n; k++) mu += mPrime[i, k] * u[k];
            vmu += v[i] * mu;
        }
        Complex scale = vb / vmu;
        var res = new Complex[n];
        for (int i = 0; i < n; i++) res[i] = u[i] * scale;
        return res;
    }

    /// <summary>Right (or left, via the transpose) null vector of the near-singular pole
    /// matrix by inverse iteration: a solve against a near-singular M amplifies the null
    /// direction by the reciprocal of the tiny smallest singular value, so a couple of
    /// normalized solves converge to it.</summary>
    private static Complex[] NullVector(ComplexDenseMatrix m, bool transpose)
    {
        int n = m.Rows;
        var a = new ComplexDenseMatrix(n, n);
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++) a[i, k] = transpose ? m[k, i] : m[i, k];
        var lu = ComplexLu.Factor(a);
        var z = new Complex[n];
        for (int i = 0; i < n; i++) z[i] = 1.0;
        for (int iter = 0; iter < 3; iter++)
        {
            z = lu.Solve(z);
            double norm = Math.Sqrt(z.Sum(c => c.Magnitude * c.Magnitude));
            for (int i = 0; i < n; i++) z[i] /= norm;
        }
        return z;
    }

    /// <summary>Entrywise k_ρ derivative of the TE or TM pole matrix by central difference
    /// (the matrix does not depend on the A_z source amplitudes, so dummy zeros suffice for
    /// the TM builder). The step is relative to |k_ρ|; the O(δ²) error is far below the
    /// 1e-8 residue target.</summary>
    private static ComplexDenseMatrix MatrixDerivative(
        LayeredStackup stackup, double k0, Complex kp, bool tm)
    {
        Complex delta = 1e-6 * (kp.Magnitude == 0 ? 1 : kp.Magnitude);
        ComplexDenseMatrix Build(Complex kRho)
        {
            var sp = Spectral(stackup, k0, kRho);
            var kz0 = SpectralKernels.Kz(k0 * k0, kRho);
            int n = sp.Kz.Length;
            var dummy = new Complex[n];
            return tm ? TmSystem(sp, kz0, dummy, Complex.Zero).M : TeSystem(sp, kz0).M;
        }
        var plus = Build(kp + delta);
        var minus = Build(kp - delta);
        int size = plus.Rows;
        var d = new ComplexDenseMatrix(size, size);
        for (int i = 0; i < size; i++)
            for (int k = 0; k < size; k++) d[i, k] = (plus[i, k] - minus[i, k]) / (2 * delta);
        return d;
    }

    /// <summary>e^{−j k_z t} with a hard branch guard, mirroring
    /// <see cref="SpectralKernels.ReducedTrig"/>: the recursion only ever calls this on the
    /// Im(k_z) ≤ 0 branch, where the phase decays. An overflow here means a caller handed a
    /// k_z off that branch — fail loudly rather than silently mis-scale.</summary>
    private static Complex ReducedPhase(Complex kzT)
    {
        var e = Complex.Exp(-Complex.ImaginaryOne * kzT);
        if (double.IsInfinity(e.Real) || double.IsInfinity(e.Imaginary)
            || double.IsNaN(e.Real) || double.IsNaN(e.Imaginary))
            throw new InvalidOperationException(
                $"TransmissionLineGreens hit a growing phase (k_z·t = {kzT}) — a k_z off the "
                + "Im(k_z) ≤ 0 branch reached the reduced basis.");
        return e;
    }
}
