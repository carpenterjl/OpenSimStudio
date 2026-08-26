using System.Numerics;
using OpenSim.Core.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// Stage C1 — the formulation-C kernels for a VERTICAL current at an arbitrary height inside an
/// N-layer grounded stackup: the multi-layer generalization of <see cref="VerticalSpectralKernels"/>,
/// which only ever knew one slab.
///
/// <para><b>The source goes to an arbitrary height by SPLITTING the stack, not by a second source
/// formulation.</b> Every jump condition in this file is written at an interface, so
/// <see cref="LayeredStackup.SplitAt"/> makes the source height one — and splitting a layer in two
/// is gated to change nothing (LayeredStackupSplitTests). The vertical source is then a single
/// extra row on the EXISTING TM system: a z-directed element makes ∂_z ã_z jump by −2µ₀, and
/// because that row is written in (1/ε)∂_z form its right-hand side is −2µ₀/ε_source.</para>
///
/// <para><b>What formulation C needs, and where each piece comes from.</b> With Ã_z^cl the
/// classical vertical potential and K̃_Φ the single charge kernel every charge in the formulation
/// shares,</para>
/// <code>
///   Δ(z,z′) = jωΦ̃_cl − ∂_z′K̃_Φ,   G̃_A^xz = −Δ/ω²,   G̃_A^zz = Ã_z^cl + ∂_z G̃_A^xz
/// </code>
/// <para>and expanding Φ̃_cl = −∂_zÃ_z^cl/(jωµ₀ε₀ε) and K̃_Φ = (A_x + ∂_z ã_z)/(µ₀ε₀ε) gives the
/// two assembled forms below, in which every term is already available:</para>
/// <list type="bullet">
///   <item>Ã_z^cl and ∂_zÃ_z^cl — the vertical TM solve (the one new row).</item>
///   <item>g_TE(z,z′) and ∂₁g_TE — the shipped per-z read-out with the source at the split
///     interface.</item>
///   <item>∂₂g_TE — the SAME read-out with the two heights swapped, valid because the kernels are
///     RECIPROCAL (gated at 1e-12 in TwoHeightKernelTests, for K̃_Φ as well as G̃_A). This is what
///     turns a source-height derivative, which no read-out returns, into an observation-height one,
///     which every read-out returns analytically.</item>
///   <item>∂₁∂₂g_TE — from <b>separability</b>: a one-dimensional Green's function is
///     u(z&lt;)v(z&gt;)/W away from its source, so g·∂₁∂₂g ≡ ∂₁g·∂₂g. No combination of
///     single-argument derivatives of a symmetric function can produce a mixed partial; the rank-1
///     structure hands it over. Also gated.</item>
///   <item>∂_z′ã_z and ∂_z∂_z′ã_z — ONE more TM solve. ã_z is launched only by the ε-contrasts
///     × A_x at each interface, and the TM system is linear in those sources, so differentiating
///     with respect to the source height just replaces the shunt vector A_x(h_i; z′) by
///     ∂₂g_TE(h_i, z′) — analytic, by the same reciprocity. Profiling that solve at z returns both
///     legs at once.</item>
/// </list>
///
/// <para><b>Why the result is smooth where its parts are not.</b> ∂_zÃ_z^cl jumps by −2µ₀ across
/// the source plane and ∂₂g_TE jumps by +2µ₀ (the Wronskian identity), so their SUM — which is what
/// G̃_A^xz is built from — is continuous, and its derivative carries no delta. The kernels are
/// therefore finite at z = z′, and the implementation evaluates every kinked piece from the same
/// side (the profile takes the layer below an interface) so the cancellation is exact rather than
/// approximate.</para>
///
/// <para><b>A vertical source AT a material discontinuity is refused.</b> Its magnitude is
/// −2µ₀/ε and the two sides disagree, so the kernel is genuinely two-valued there — a limit that
/// depends on the approach, not a value. It never arises in a solve: tube elements are split at
/// every interface, so a quadrature node is always strictly inside one material.</para>
/// </summary>
internal static partial class TransmissionLineGreens
{
    /// <summary>The three vertical kernels at one spectral point: G̃_A^zz, the horizontal coupling
    /// G̃_A^xz (per −jk_x·J̃_z, the same normalization <see cref="VerticalSpectralKernels"/> uses),
    /// and the two-height charge kernel K̃_Φ(z, z′). At N = 1 these reproduce the single-slab
    /// closed forms to 1e-12 — the identity that fixes every convention here, so none had to be
    /// guessed.</summary>
    public static (Complex GAzz, Complex GAxz, Complex KPhi) EvaluateVertical(
        LayeredStackup stackup, double k0, Complex kRho, Complex kz0, double z, double zPrime)
    {
        if (z < 0)
            throw new ArgumentOutOfRangeException(nameof(z),
                $"The observation height must be ≥ 0 (the PEC ground) — got {z} m.");
        if (z > stackup.TotalThicknessMeters)
            throw new ArgumentOutOfRangeException(nameof(z),
                $"Observation above the stack (z = {z} m > {stackup.TotalThicknessMeters} m) is not "
                + "in this scope. ∂₂g_TE is obtained by SWAPPING the two heights, and a source in "
                + "the air above the stack is not an interface any of these systems can place one "
                + "at; region 0 needs the logarithmic-derivative form instead, and is a named next "
                + "step. The MoM never asks — a probe tube's current lives inside the substrate.");

        var (split, m) = stackup.SplitAt(zPrime);
        int n = split.Layers.Count;
        var sp = Spectral(split, k0, kRho);

        Complex epsBelow = sp.Eps[m];
        Complex epsAbove = m == n - 1 ? Complex.One : sp.Eps[m + 1];
        if (epsBelow != epsAbove)
            throw new ArgumentException(
                $"A vertical current element at z′ = {zPrime} m sits exactly on a material "
                + $"interface (εr {epsBelow} below, {epsAbove} above). Its source strength is "
                + "−2µ₀/ε and the two sides disagree, so the kernel there is a limit that depends "
                + "on which side you approach from, not a value. Split the current element at the "
                + "interface and integrate each piece strictly inside one material.",
                nameof(zPrime));

        // ---- The vertical TM solve: the homogeneous line with ONE source row. A z-directed
        // current excites no A_x at all, so every ε-contrast shunt source vanishes with it.
        var (tmM, tmRhs, _) = TmSystem(sp, kz0, new Complex[n], Complex.Zero);
        int sourceRow = m == n - 1 ? 2 * n : 2 + 2 * m;
        tmRhs[sourceRow] = -2 * RfConstants.Mu0 / epsBelow;
        var verticalTm = ComplexLu.Factor(tmM).Solve(tmRhs);
        var zeroTe = new Complex[2 * n + 1];
        var (_, _, azCl, dzAzCl, epsAt, kzAt) = Profile(split, sp, kz0, zeroTe, verticalTm, z);

        // ---- The horizontal auxiliary problem with its source at the SAME split interface:
        // g_TE(z, z′), its observation derivative, and the shipped charge kernel.
        var (teM, teRhs, cIdx) = m == n - 1 ? TeSystem(sp, kz0) : TeSystemInterior(sp, kz0, m);
        var teSol = ComplexLu.Factor(teM).Solve(teRhs);
        Complex c = teSol[cIdx];
        var axAt = AxAtInterfaces(teSol, sp.Phi, c);
        var (tmH, tmHRhs, _) = TmSystem(sp, kz0, axAt, c);
        var tmH_Sol = ComplexLu.Factor(tmH).Solve(tmHRhs);
        var horizontal = AssembleField(split, sp, kz0, teSol, tmH_Sol, z);
        Complex gTe = horizontal.GA, d1GTe = horizontal.DzA, kPhi = horizontal.Phi;

        // ---- ∂₂g_TE(z, z′): the same Green's function read with the heights swapped. Splitting
        // again at z costs nothing (splitting is gated to change nothing) and turns the source
        // height into an interface the read-out can source from.
        var (splitZ, mz) = split.SplitAt(z);
        Complex d2GTe = DzAx(splitZ, k0, kRho, kz0, mz, zPrime);
        if (ReferenceEquals(splitZ, split) && mz == m)
        {
            // z IS the source plane, and there the two kinked terms must be read on the SAME side
            // of it or their ∓2µ₀ jumps stop cancelling. The profile reads an interface from
            // below, which in the SWAPPED problem means observation-below-source — the opposite
            // branch from ∂_zÃ_z^cl, which is read at its own source and so lands on
            // observation-below-source in the ORIGINAL labelling, i.e. the other one. Symmetry
            // gives the step exactly: at coincident heights ∂₂g on one branch equals ∂₁g on the
            // other, and ∂₁g steps by the source jump −2µ₀ across the plane.
            //
            // The correction is derived twice and measured once: without it the assembled G̃_A^xz
            // sits high by exactly 2µ₀/(k₀²ε) at z = z′ and is right everywhere else — which is
            // the signature of a single mismatched one-sided limit, and matches this step to the
            // digit. Everywhere else the two branches are unambiguous and nothing is applied.
            d2GTe -= 2 * RfConstants.Mu0;
        }

        // ---- The source-height derivative of ã_z: the SAME TM system with its shunt vector
        // differentiated. The interface at the split carries zero contrast by construction (both
        // halves are the same material), which is exactly what keeps the kink at the source out
        // of the sum.
        var axPrime = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            Complex contrast = i == n - 1 ? 1 / sp.Eps[n - 1] - 1 : 1 / sp.Eps[i] - 1 / sp.Eps[i + 1];
            if (contrast == Complex.Zero) continue;
            axPrime[i] = DzAx(split, k0, kRho, kz0, i, zPrime);
        }
        var (tmD, tmDRhs, _) = TmSystem(sp, kz0, axPrime, axPrime[n - 1]);
        var derivativeTm = ComplexLu.Factor(tmD).Solve(tmDRhs);
        var (_, _, dzPrimeAz, dzDzPrimeAz, _, _) = Profile(split, sp, kz0, zeroTe, derivativeTm, z);

        // ---- Assemble. Both forms carry 1/(k₀²ε(z)): the ω's of the gauge transformation cancel
        // against µ₀ε₀ exactly, which is why the single-slab closed forms show no ω either.
        Complex scale = 1 / (k0 * k0 * epsAt);
        Complex gAxz = scale * (dzAzCl + d2GTe + dzDzPrimeAz);
        // ∂₁∂₂g_TE by separability; within a constant-ε layer ∂zz of a line solution is −k_z².
        Complex mixed = gTe == Complex.Zero ? Complex.Zero : d1GTe * d2GTe / gTe;
        Complex dzGAxz = scale * (-kzAt * kzAt * azCl + mixed - kzAt * kzAt * dzPrimeAz);
        return (azCl + dzGAxz, gAxz, kPhi);
    }

    /// <summary>∂_zA_x at <paramref name="z"/> for a HED source at interface <paramref name="m"/> —
    /// the TE half of the per-z read-out, without the TM solve the full read-out would also do.
    /// Used for the source-height derivatives, which need this quantity once per interface.</summary>
    private static Complex DzAx(LayeredStackup stackup, double k0, Complex kRho, Complex kz0,
        int m, double z)
    {
        var sp = Spectral(stackup, k0, kRho);
        int n = stackup.Layers.Count;
        var (teM, teRhs, _) = m == n - 1 ? TeSystem(sp, kz0) : TeSystemInterior(sp, kz0, m);
        var teSol = ComplexLu.Factor(teM).Solve(teRhs);
        var (_, dzAx, _, _, _, _) = Profile(stackup, sp, kz0, teSol, new Complex[2 * n + 1], z);
        return dzAx;
    }
}
