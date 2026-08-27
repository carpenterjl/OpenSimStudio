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
        => EvaluateVertical(PrepareVertical(stackup, z, zPrime), k0, kRho, kz0, z, zPrime);

    /// <summary>The same kernels from a geometry prepared ONCE for a (z, z′) pair. The stack
    /// splits, the source interface and the swapped-problem split depend only on the two heights,
    /// never on k_ρ — so a Sommerfeld sweep, which asks for a thousand spectral points at ONE
    /// (z, z′), can hoist all of it out of the loop. Every arithmetic step below is the one the
    /// per-call wrapper performs, on the identical stackup objects, so the two agree BITWISE
    /// (gated); this only stops re-deriving geometry a thousand times over.</summary>
    internal static (Complex GAzz, Complex GAxz, Complex KPhi) EvaluateVertical(
        VerticalGeometry geometry, double k0, Complex kRho, Complex kz0, double z, double zPrime)
    {
        var (split, m, splitZ, mz) = geometry;
        int n = split.Layers.Count;
        var sp = Spectral(split, k0, kRho);

        RefuseSourceOnAnInterface(sp, m, n, zPrime);
        Complex epsBelow = sp.Eps[m];

        // ---- The vertical TM solve: the homogeneous line with ONE source row. A z-directed
        // current excites no A_x at all, so every ε-contrast shunt source vanishes with it.
        var (tmM, tmRhs, _) = TmSystem(sp, kz0, new Complex[n], Complex.Zero);
        tmRhs[VerticalSourceRow(m, n)] = -2 * RfConstants.Mu0 / epsBelow;
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
        var axPrime = SourceHeightShunts(split, sp, k0, kRho, kz0, zPrime, residue: false);
        var (tmD, tmDRhs, _) = TmSystem(sp, kz0, axPrime, axPrime[n - 1]);
        var derivativeTm = ComplexLu.Factor(tmD).Solve(tmDRhs);
        var (_, _, dzPrimeAz, dzDzPrimeAz, _, _) = Profile(split, sp, kz0, zeroTe, derivativeTm, z);

        // ---- Assemble. Both forms carry 1/(k₀²ε(z)): the ω's of the gauge transformation cancel
        // against µ₀ε₀ exactly, which is why the single-slab closed forms show no ω either.
        Complex scale = 1 / (k0 * k0 * epsAt);
        Complex gAxz = scale * (dzAzCl + d2GTe + dzDzPrimeAz);
        // ∂₁∂₂g_TE by separability, grouped as ∂₁g × (∂₂g/g) rather than ∂₁g·∂₂g/g. The two agree
        // to a rounding, but the grouping is the load-bearing part: ∂₂g/g is a LOGARITHMIC
        // DERIVATIVE — v′/v above the source, u′/u below — so it is REGULAR where g is not. That
        // buys two things. It stays finite at a node of u or v, where g and ∂₂g vanish together
        // and the naive quotient is 0/0; and it leaves the whole assembly LINEAR in the quantities
        // that carry surface-wave poles, so a pole residue is a sum of residues with this factor
        // as a regular coefficient, rather than the residue of a quotient.
        Complex logDerivative = gTe == Complex.Zero ? Complex.Zero : d2GTe / gTe;
        Complex mixed = d1GTe * logDerivative;
        // Within a constant-ε layer ∂zz of a line solution is −k_z², which is what turns the two
        // second derivatives below into their own values.
        Complex dzGAxz = scale * (-kzAt * kzAt * azCl + mixed - kzAt * kzAt * dzPrimeAz);
        return (azCl + dzGAxz, gAxz, kPhi);
    }

    /// <summary>∂_zA_x at <paramref name="z"/> for a HED source at interface <paramref name="m"/> —
    /// the TE half of the per-z read-out, without the TM solve the full read-out would also do.
    /// Used for the source-height derivatives, which need this quantity once per interface.</summary>
    private static Complex DzAx(LayeredStackup stackup, double k0, Complex kRho, Complex kz0,
        int m, double z) => DzAx(stackup, Spectral(stackup, k0, kRho), kz0, m, z);

    /// <summary>∂_zA_x with the layer spectral profile already in hand — the same arithmetic, for
    /// callers that have solved on this very stackup at this very k_ρ (the shunt read-outs, which
    /// ask once per contrast interface).</summary>
    private static Complex DzAx(LayeredStackup stackup, LayerSpectral sp, Complex kz0,
        int m, double z)
    {
        int n = stackup.Layers.Count;
        var (teM, teRhs, _) = m == n - 1 ? TeSystem(sp, kz0) : TeSystemInterior(sp, kz0, m);
        var teSol = ComplexLu.Factor(teM).Solve(teRhs);
        var (_, dzAx, _, _, _, _) = Profile(stackup, sp, kz0, teSol, new Complex[2 * n + 1], z);
        return dzAx;
    }

    /// <summary>The residues of the three vertical kernels at a surface-wave pole, per (z, z′) —
    /// what the spatial remainder integrator extracts so that a lossless stack’s real-axis pole
    /// never sits on the integration path. The mode is source-independent, so only the right-hand
    /// sides and the read-out heights move; each singular solve becomes the null-vector matrix
    /// residue (<see cref="MatrixResidue"/>), exactly as <see cref="PoleFieldResidues"/> does.
    ///
    /// <para><b>Which pieces are singular is decided by the pole type, and the split is clean.</b>
    /// At a TE pole the TE line is singular, so g_TE, its two first derivatives and every
    /// interface read-out are — while the vertical TM solve, whose source carries no A_x at all, is
    /// REGULAR and contributes nothing. At a TM pole it is exactly the other way round: the TE
    /// quantities are regular (so the mixed term contributes nothing) and both TM solves are
    /// singular.</para>
    ///
    /// <para><b>The mixed term is why the assembly is grouped as a logarithmic derivative.</b>
    /// ∂₁g·(∂₂g/g) has a SIMPLE pole because the ratio is regular there: writing g = u(z_&lt;)v(z_&gt;)/W,
    /// the ratio is v′/v above the source and u′/u below — free of the Wronskian W whose vanishing IS
    /// the pole — so the residue is Res[∂₁g] × (that ratio), a sum of residues rather than the residue
    /// of a quotient. Written the naive way the same number would have to arrive as a double pole
    /// divided by a simple one.</para></summary>
    public static (Complex GAzz, Complex GAxz, Complex KPhi) PoleVerticalResidues(
        LayeredStackup stackup, double k0, Complex kp, bool isTm, double z, double zPrime)
    {
        ValidateVerticalHeights(stackup, z);
        var (split, m) = stackup.SplitAt(zPrime);
        int n = split.Layers.Count;
        var sp = Spectral(split, k0, kp);
        var kz0 = SpectralKernels.Kz(k0 * k0, kp);
        RefuseSourceOnAnInterface(sp, m, n, zPrime);
        var zeroTe = new Complex[2 * n + 1];
        var zeroTm = new Complex[2 * n + 1];

        // K̃_Φ is a plain read-out of the horizontal problem, so its residue is the shipped per-z
        // field residue at the split interface — no new derivation, and the N = 1 identity gates it
        // against the single-slab analytic residues transitively.
        Complex resKPhi = PoleFieldResidues(split, k0, kp, isTm, m, z).Phi;

        if (isTm)
        {
            var tmDeriv = MatrixDerivative(split, k0, kp, tm: true);
            var (tmM, tmRhs, _) = TmSystem(sp, kz0, new Complex[n], Complex.Zero);
            tmRhs[VerticalSourceRow(m, n)] = -2 * RfConstants.Mu0 / sp.Eps[m];
            var resVertical = MatrixResidue(tmM, tmDeriv, tmRhs);
            var (_, _, resAzCl, resDzAzCl, epsAt, kzAt) =
                Profile(split, sp, kz0, zeroTe, resVertical, z);

            // The TE line is regular at a TM pole, so the derivative system’s shunt sources are
            // ordinary values and only its MATRIX is singular.
            var axPrime = SourceHeightShunts(split, sp, k0, kp, kz0, zPrime, residue: false);
            var (tmD, tmDRhs, _) = TmSystem(sp, kz0, axPrime, axPrime[n - 1]);
            var resDeriv = MatrixResidue(tmD, tmDeriv, tmDRhs);
            var (_, _, resDzPrimeAz, resDzDzPrimeAz, _, _) =
                Profile(split, sp, kz0, zeroTe, resDeriv, z);

            Complex scale = 1 / (k0 * k0 * epsAt);
            Complex gAxz = scale * (resDzAzCl + resDzDzPrimeAz);
            Complex gAzz = resAzCl + scale * (-kzAt * kzAt * resAzCl - kzAt * kzAt * resDzPrimeAz);
            return (gAzz, gAxz, resKPhi);
        }
        else
        {
            var (teM, teRhs, _) = m == n - 1 ? TeSystem(sp, kz0) : TeSystemInterior(sp, kz0, m);
            var teDeriv = m == n - 1
                ? MatrixDerivative(split, k0, kp, tm: false)
                : MatrixDerivativeTeInterior(split, k0, kp, m);
            var resTe = MatrixResidue(teM, teDeriv, teRhs);
            var (resGTe, resD1GTe, _, _, epsAt, kzAt) = Profile(split, sp, kz0, resTe, zeroTm, z);

            var (splitZ, mz) = split.SplitAt(z);
            Complex resD2GTe = DzAxResidue(splitZ, k0, kp, kz0, mz, zPrime);

            var resAxPrime = SourceHeightShunts(split, sp, k0, kp, kz0, zPrime, residue: true);
            var (tmD, tmDRhs, _) = TmSystem(sp, kz0, resAxPrime, resAxPrime[n - 1]);
            var resDeriv = ComplexLu.Factor(tmD).Solve(tmDRhs);   // M regular, RHS = Res(b)
            var (_, _, resDzPrimeAz, resDzDzPrimeAz, _, _) =
                Profile(split, sp, kz0, zeroTe, resDeriv, z);

            Complex scale = 1 / (k0 * k0 * epsAt);
            Complex resMixed = resGTe == Complex.Zero
                ? Complex.Zero : resD1GTe * (resD2GTe / resGTe);
            Complex gAxz = scale * (resD2GTe + resDzDzPrimeAz);
            Complex gAzz = scale * (resMixed - kzAt * kzAt * resDzPrimeAz);
            return (gAzz, gAxz, resKPhi);
        }
    }

    /// <summary>The interface shunt sources of the source-height-derivative TM system — ∂₂g_TE at
    /// every contrast interface, as values (<paramref name="residue"/> false) or as their residues
    /// at a TE pole (true). Zero-contrast interfaces are skipped: they launch no ã_z, and the
    /// split interface is one of them by construction, which is what keeps the source kink out of
    /// the sum.</summary>
    private static Complex[] SourceHeightShunts(LayeredStackup split, LayerSpectral sp, double k0,
        Complex kRho, Complex kz0, double zPrime, bool residue)
    {
        int n = split.Layers.Count;
        var shunts = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            if (EpsContrast(sp, i, n) == Complex.Zero) continue;
            shunts[i] = residue
                ? DzAxResidue(split, k0, kRho, kz0, i, zPrime)
                : DzAx(split, sp, kz0, i, zPrime);
        }
        return shunts;
    }

    /// <summary>The geometry a (z, z′) pair implies: the stack split so the source sits on an
    /// interface, and the same stack split AGAIN at the observation height so the swapped read-out
    /// has an interface to source from. <c>ReferenceEquals(SplitZ, Split) &amp;&amp; Mz == M</c> is
    /// how the coincident-height branch is recognised — <see cref="LayeredStackup.SplitAt"/>
    /// returns THIS stackup when the height already is an interface.</summary>
    internal readonly record struct VerticalGeometry(
        LayeredStackup Split, int M, LayeredStackup SplitZ, int Mz);

    /// <summary>Prepare the splits for one (z, z′) pair. Deterministic and k_ρ-independent.</summary>
    internal static VerticalGeometry PrepareVertical(LayeredStackup stackup, double z, double zPrime)
    {
        ValidateVerticalHeights(stackup, z);
        var (split, m) = stackup.SplitAt(zPrime);
        var (splitZ, mz) = split.SplitAt(z);
        return new VerticalGeometry(split, m, splitZ, mz);
    }

    /// <summary>∂_zA_x at <paramref name="z"/> for a HED source at interface <paramref name="m"/>,
    /// AT a TE pole — the residue counterpart of <see cref="DzAx"/>.</summary>
    private static Complex DzAxResidue(LayeredStackup stackup, double k0, Complex kp, Complex kz0,
        int m, double z)
    {
        var sp = Spectral(stackup, k0, kp);
        int n = stackup.Layers.Count;
        var (teM, teRhs, _) = m == n - 1 ? TeSystem(sp, kz0) : TeSystemInterior(sp, kz0, m);
        var deriv = m == n - 1
            ? MatrixDerivative(stackup, k0, kp, tm: false)
            : MatrixDerivativeTeInterior(stackup, k0, kp, m);
        var res = MatrixResidue(teM, deriv, teRhs);
        var (_, dzAx, _, _, _, _) = Profile(stackup, sp, kz0, res, new Complex[2 * n + 1], z);
        return dzAx;
    }

    /// <summary>The TM row a vertical source at interface <paramref name="m"/> drives (the top
    /// plane’s radiation row, else that interface’s derivative-jump row).</summary>
    private static int VerticalSourceRow(int m, int n) => m == n - 1 ? 2 * n : 2 + 2 * m;

    /// <summary>The ε-contrast that launches ã_z at interface i — 1/ε below minus 1/ε above, with
    /// air above the top.</summary>
    private static Complex EpsContrast(LayerSpectral sp, int i, int n) =>
        i == n - 1 ? 1 / sp.Eps[n - 1] - 1 : 1 / sp.Eps[i] - 1 / sp.Eps[i + 1];

    private static void ValidateVerticalHeights(LayeredStackup stackup, double z)
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
                + "step. The MoM never asks — a probe tube’s current lives inside the substrate.");
    }

    private static void RefuseSourceOnAnInterface(LayerSpectral sp, int m, int n, double zPrime)
    {
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
    }
}
