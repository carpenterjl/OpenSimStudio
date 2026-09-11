using System.Numerics;

namespace OpenSim.Rf.Layered;

/// <summary>
/// The surface-wave characteristic functions of an N-layer grounded stackup, written as a
/// transfer-matrix (ABCD) shot from the ground to the top of the stack — and the Sturm mode
/// counter that isolates their real roots without ever bracketing a sign change.
///
/// <para><b>Why not the reflection recursion.</b> A dispersion written as
/// jk_z0 + jk_z,top·(1 − R)/(1 + R) has POLES wherever 1 + R = 0 — for one slab at
/// k_z1·d = π/2 (TM) and π (TE) — and a sign-change bracketer converges onto those poles
/// exactly as happily as onto the roots (that was defect D2: εr 10.2 / 1.27 mm / 25 GHz gave
/// an extra "pole" with |D| = 3.2e17). Multiplying through by (1 + R) does not help either:
/// (1 + R) is complex on the segment while the finder brackets the REAL part, so the spurious
/// root survives with zero real part. The characteristic function has to be real-valued and
/// entire on the lossless bound-mode segment. The transfer-matrix form is.</para>
///
/// <para><b>The transverse problem.</b> Both polarizations are a regular Sturm–Liouville
/// problem in z with piecewise-constant coefficients:
///   TE (A_x):  A″ = (k_ρ² − k_i²)·A,          A(0) = 0        (PEC: Dirichlet),
///   TM (A_z):  (A′/ε)′ = (k_ρ² − k_i²)·A/ε,   A′(0) = 0       (PEC: Neumann),
/// closed above the stack by the decaying (Robin) condition A′ + γ₀A = 0 (TM: A′/ε_top +
/// γ₀A = 0), γ₀ = √(k_ρ² − k₀²). With the state (A, B), B = p·A′, p = 1 (TE) or 1/ε (TM), each
/// layer is the entire matrix
///   [[cos(k_zi t), sin(k_zi t)/(p·k_zi)], [−p·k_zi·sin(k_zi t), cos(k_zi t)]]
/// — even in k_zi, hence entire in k_ρ²; where k_ρ &gt; k_i it is the cosh/sinh form with real
/// arguments. The characteristic function is the Robin residual at the top,
///   D = B_top + jk_z0·A_top,
/// which on the lossless segment (jk_z0 = γ₀ real) is REAL and entire. For one slab it is the
/// pinned single-slab function up to a non-vanishing factor: D_TE = [u·cos u + γ₀d·sin u]/u
/// and D_TM = −[u·sin u − εr·γ₀d·cos u]/(εr·d), u = k_z1·d.</para>
///
/// <para><b>Overflow safety is inside the layer matrix.</b> Every entry is evaluated pre-scaled
/// by e^{−|Im(k_zi t_i)|}: cosh(x)e^{−x} = (1 + e^{−2x})/2, sinh(x)e^{−x} = (1 − e^{−2x})/2,
/// the same reduced-basis trick <see cref="SpectralKernels.ReducedTrig"/> uses. The dropped
/// factor is a positive real per layer, so roots, signs and zero counts are untouched, and a
/// 2000-wavelength spacer costs nothing. (Normalising the state AFTER a layer product would be
/// too late — cosh overflows before anything can be divided.)</para>
///
/// <para><b>Root isolation by mode counting, not sign changes.</b> The oscillation theorem gives
/// the number of bound modes with propagation constant ABOVE a given k_ρ exactly:
///   N(k_ρ) = (zeros of the shot A(z) on the half-open (0, d]) + [A_top·D &lt; 0].
/// N is a non-increasing integer step function of k_ρ (raising k_ρ lowers the Prüfer phase;
/// the Robin angle is monotone in γ₀) and EVERY root is a unit step, so roots are located by
/// bisection on N — a step of two inside an interval is bisected further until every step is
/// one, which isolates arbitrarily close pairs that no fixed sampling could separate
/// (εr = [10, 1, 10], k₀t = [1, 5, 2] has TE roots 5.6e-5·k₀ apart). The half-open convention
/// matters: a zero exactly at the top is counted once, as a zero, and the ground (where TE
/// starts at a zero) is excluded; that is what keeps N continuous through a non-eigenvalue top
/// zero (εr = [5, 4], t = [1, 0.8026157699179134] has A_top = 0 at k_ρ = 2 with D ≠ 0).</para>
///
/// <para><b>Lossy stacks</b> take the lossless roots as seeds and follow each one through a
/// geometric continuation in tanδ with one-to-one tracking (<see cref="LossContinuation"/>),
/// because independent Newton solves from two close seeds can land on the SAME lossy pole.</para>
/// </summary>
internal static class SurfaceWaveDispersion
{
    /// <summary>The real lossless shot at one k_ρ: the top state, the zeros of A on (0, d], and
    /// the Robin residual D.</summary>
    internal readonly record struct Shot(double ATop, double BTop, int Zeros, double D);

    /// <summary>A lossless root with the interval that isolates it (N steps by exactly one
    /// across [Lo, Hi]).</summary>
    internal readonly record struct IsolatedRoot(double KRho, double Lo, double Hi);

    /// <summary>The relative residual a polished root must reach against the largest |D| seen
    /// on the segment.</summary>
    internal const double ResidualTolerance = 1e-8;

    /// <summary>Two poles closer than this (relative to k₀) are the same pole.</summary>
    internal const double DistinctnessTolerance = 1e-9;

    private const int ScaleGridSamples = 512;

    private static string Family(bool isTm) => isTm ? "TM" : "TE";

    // ------------------------------------------------------------------ complex D (any stack)

    /// <summary>The characteristic function D = B_top + jk_z0·A_top for a general (lossy)
    /// stack at a complex k_ρ, on the Im(k_z) ≤ 0 sheet. Its zeros are the surface-wave poles
    /// of both spectral kernels. Entire in k_ρ² apart from the k_z0 branch point, and never
    /// singular on the bound-mode segment. On a lossless stack and real k_ρ ∈ (k₀, k_max) it is
    /// real (see <see cref="Shoot"/>, the real-arithmetic twin).</summary>
    public static Complex Dispersion(LayeredStackup stackup, double k0, Complex kRho, bool isTm)
    {
        double k0Sq = k0 * k0;
        Complex a = isTm ? Complex.One : Complex.Zero;
        Complex b = isTm ? Complex.Zero : Complex.One;
        foreach (var layer in stackup.Layers)
        {
            double t = layer.ThicknessMeters;
            Complex eps = layer.ComplexPermittivity;
            Complex p = isTm ? 1 / eps : Complex.One;
            Complex kz = SpectralKernels.Kz(eps * k0Sq, kRho);
            var (c, s) = ScaledTrig(kz * t);
            Complex sOverK = kz == Complex.Zero ? t : s / kz;   // sin(k t)/k → t as k → 0
            Complex aTop = c * a + sOverK / p * b;
            Complex bTop = -p * kz * s * a + c * b;
            a = aTop;
            b = bTop;
        }
        Complex kz0 = SpectralKernels.Kz(k0Sq, kRho);
        return b + Complex.ImaginaryOne * kz0 * a;
    }

    /// <summary>(cos z, sin z) × e^{−|Im z|}: cos z = (e^{jz} + e^{−jz})/2 and
    /// sin z = (e^{jz} − e^{−jz})/2j with each exponential pre-multiplied by e^{−|Im z|}, so
    /// both have non-positive real exponent and nothing overflows for any thickness on either
    /// branch. The dropped factor is a positive real.</summary>
    internal static (Complex Cos, Complex Sin) ScaledTrig(Complex z)
    {
        double scale = -Math.Abs(z.Imaginary);
        var jz = Complex.ImaginaryOne * z;
        Complex u = Complex.Exp(jz + scale);
        Complex v = Complex.Exp(-jz + scale);
        return ((u + v) / 2, (u - v) / new Complex(0, 2));
    }

    // ------------------------------------------------------------------ real lossless shot

    /// <summary>The real transfer-matrix shot of the LOSSLESS twin of <paramref name="stackup"/>
    /// (loss tangents ignored) at a real k_ρ on the bound-mode segment (k_ρ &gt; k₀): top state,
    /// the number of zeros of A on the half-open (0, d], and the real Robin residual D.</summary>
    public static Shot Shoot(LayeredStackup stackup, double k0, double kRho, bool isTm)
    {
        if (!(kRho > k0))
            throw new ArgumentOutOfRangeException(nameof(kRho),
                $"The lossless shot is defined on the bound-mode segment k_ρ > k₀ (k_ρ = {kRho}, k₀ = {k0}).");
        double k0Sq = k0 * k0, kRhoSq = kRho * kRho;
        double a = isTm ? 1 : 0, b = isTm ? 0 : 1;
        int zeros = 0;
        foreach (var layer in stackup.Layers)
        {
            double t = layer.ThicknessMeters;
            double p = isTm ? 1 / layer.RelativePermittivity : 1;
            double k2 = layer.RelativePermittivity * k0Sq - kRhoSq;   // k_zi²
            double aPrime0 = b / p;                                   // A′ at the layer bottom
            // The sign of A just above the bottom node: A itself, or A′ when A starts at a zero.
            int signStart = a != 0 ? Math.Sign(a) : Math.Sign(aPrime0);
            double aTop, bTop;
            int inside;   // zeros strictly inside (0, t)
            if (k2 > 0)
            {
                double k = Math.Sqrt(k2), x = k * t;
                double c = Math.Cos(x), s = Math.Sin(x);
                aTop = a * c + aPrime0 * s / k;
                bTop = -p * k * s * a + c * b;
                // A(s) = R·sin(k s + φ): zeros at k s + φ = mπ. Count those with 0 < s ≤ t, then
                // make the count agree in parity with the computed end signs — A's zeros are
                // simple, so every zero flips the sign, and this pins the one ambiguous case
                // (a zero within rounding of the top) to the sign the next layer will see.
                double phi = Math.Atan2(a, aPrime0 / k);
                double top = (x + phi) / Math.PI;
                int count = (int)Math.Floor(top) - (int)Math.Floor(phi / Math.PI);
                int signEnd = aTop != 0 ? Math.Sign(aTop) : -Math.Sign(bTop);
                int parity = signStart != signEnd ? 1 : 0;
                if ((count & 1) != parity) count += top - Math.Floor(top) < 0.5 ? -1 : 1;
                inside = count;
            }
            else if (k2 < 0)
            {
                // Evanescent: cosh/sinh pre-scaled by e^{−γt}; A has at most one zero here.
                double g = Math.Sqrt(-k2), e = Math.Exp(-2 * g * t);
                double c = (1 + e) / 2, s = (1 - e) / 2;
                aTop = a * c + aPrime0 * s / g;
                bTop = p * g * s * a + c * b;
                inside = a * aTop < 0 ? 1 : 0;
            }
            else
            {
                // Exactly at the layer's cutoff: A is linear.
                aTop = a + aPrime0 * t;
                bTop = b;
                inside = a * aTop < 0 ? 1 : 0;
            }
            zeros += inside + (aTop == 0 ? 1 : 0);   // a zero AT the top counts once, here
            a = aTop;
            b = bTop;
        }
        double gamma0 = Math.Sqrt(kRhoSq - k0Sq);
        return new Shot(a, b, zeros, b + gamma0 * a);
    }

    /// <summary>N(k_ρ): the number of bound modes of the lossless twin whose propagation
    /// constant exceeds k_ρ — zeros of A on (0, d] plus one when the top Robin residual shows the
    /// boundary eigenvalue has been passed (A_top·D &lt; 0). Non-increasing in k_ρ; every root of
    /// D is a unit step.</summary>
    public static int ModeCount(LayeredStackup stackup, double k0, double kRho, bool isTm)
    {
        var shot = Shoot(stackup, k0, kRho, isTm);
        return shot.Zeros + (shot.ATop * shot.D < 0 ? 1 : 0);
    }

    // ------------------------------------------------------------------ lossless roots

    /// <summary>The bound-mode segment (k₀, k_max) nudged inward, exactly as the old sampler
    /// did: at k₀ the top decay vanishes and at k_max the densest layer's k_z does.</summary>
    internal static (double Lo, double Hi) Segment(LayeredStackup stackup, double k0)
    {
        double epsMax = stackup.Layers.Max(l => l.RelativePermittivity);
        return (k0 * (1 + 1e-9), k0 * Math.Sqrt(epsMax) * (1 - 1e-9));
    }

    /// <summary>Every real root of the lossless characteristic function on the bound-mode
    /// segment, isolated by bisection on <see cref="ModeCount"/> and polished by Newton on D
    /// inside its isolating interval. Typed failures: N increasing anywhere it was evaluated
    /// (an integration or branch error), a step of two that no bisection separates (a degenerate
    /// pair), a mode within 1e-9 of the k_max cutoff, a polished root leaving its interval, or a
    /// residual above <see cref="ResidualTolerance"/> of the largest |D| on the segment.</summary>
    public static IReadOnlyList<IsolatedRoot> LosslessRoots(LayeredStackup stackup, double k0, bool isTm)
    {
        if (k0 <= 0) throw new ArgumentOutOfRangeException(nameof(k0));
        var roots = new List<IsolatedRoot>();
        var (lo, hi) = Segment(stackup, k0);
        if (hi <= lo) return roots;   // all air: no bound modes.

        int N(double kr) => ModeCount(stackup, k0, kr, isTm);
        double D(double kr) => Shoot(stackup, k0, kr, isTm).D;
        int nLo = N(lo), nHi = N(hi);
        if (nHi != 0)
            throw new InvalidOperationException(
                $"{nHi} {Family(isTm)} surface-wave mode(s) of this stackup lie within 1e-9 of the "
                + $"k_max = k₀√εr_max cutoff (N({hi}) = {nHi}); a mode that close to cutoff cannot be "
                + "extracted as a pole.");
        double scale = ResidualScale(D, lo, hi);

        void Isolate(double a, int na, double b, int nb)
        {
            int step = na - nb;
            if (step == 0) return;
            if (step == 1)
            {
                roots.Add(Refine(D, N, a, na, b, nb, k0, scale, isTm));
                return;
            }
            if (b - a <= 1e-13 * k0)
                throw new InvalidOperationException(
                    $"{step} {Family(isTm)} surface-wave modes are degenerate within [{a}, {b}] "
                    + "(1e-13·k₀ wide): bisection on the mode count cannot separate them.");
            double m = 0.5 * (a + b);
            int nm = N(m);
            if (nm > na || nm < nb)
                throw new InvalidOperationException(
                    $"The {Family(isTm)} mode count is not monotone: N({a}) = {na}, N({m}) = {nm}, "
                    + $"N({b}) = {nb} — an integration or branch error in the lossless shot.");
            Isolate(a, na, m, nm);
            Isolate(m, nm, b, nb);
        }
        Isolate(lo, nLo, hi, nHi);

        if (roots.Count != nLo)
            throw new InvalidOperationException(
                $"Isolated {roots.Count} {Family(isTm)} roots but the mode count at k₀⁺ is {nLo}.");
        return roots;
    }

    /// <summary>Bisection on N to a 1e-11·k₀ interval, then Newton on D from the midpoint,
    /// accepted only while it stays inside the isolating interval and lowers |D|; then the
    /// residual and interval guards.</summary>
    private static IsolatedRoot Refine(Func<double, double> d, Func<double, int> n,
        double a, int na, double b, int nb, double k0, double scale, bool isTm)
    {
        double lo = a, hi = b;
        for (int iteration = 0; iteration < 200 && hi - lo > 1e-11 * k0; iteration++)
        {
            double m = 0.5 * (lo + hi);
            if (m == lo || m == hi) break;
            int nm = n(m);
            if (nm > na || nm < nb)
                throw new InvalidOperationException(
                    $"The {Family(isTm)} mode count is not monotone inside [{a}, {b}] at k_ρ = {m}.");
            if (nm == na) lo = m; else hi = m;
        }
        double kp = 0.5 * (lo + hi);
        double best = Math.Abs(d(kp));
        double h = 1e-7 * k0;
        for (int iteration = 0; iteration < 20 && best > 0; iteration++)
        {
            double slope = (d(kp + h) - d(kp - h)) / (2 * h);
            if (slope == 0 || !double.IsFinite(slope)) break;
            double next = kp - d(kp) / slope;
            if (!(next > a && next < b)) break;
            double residual = Math.Abs(d(next));
            if (residual >= best) break;
            bool converged = Math.Abs(next - kp) <= 1e-15 * kp;
            kp = next;
            best = residual;
            if (converged) break;
        }
        if (!(kp > a && kp < b))
            throw new InvalidOperationException(
                $"The polished {Family(isTm)} root {kp} left its isolating interval [{a}, {b}].");
        if (best > ResidualTolerance * scale)
            throw new InvalidOperationException(
                $"The {Family(isTm)} root at k_ρ = {kp} has residual |D| = {best:g3} against a segment "
                + $"scale of {scale:g3} (tolerance {ResidualTolerance:g1}); it is not a root.");
        return new IsolatedRoot(kp, a, b);
    }

    private static double ResidualScale(Func<double, double> d, double lo, double hi)
    {
        double scale = 0;
        for (int i = 0; i <= ScaleGridSamples; i++)
        {
            double v = Math.Abs(d(lo + (hi - lo) * i / ScaleGridSamples));
            if (!double.IsFinite(v))
                throw new InvalidOperationException("The lossless characteristic function is not finite on the bound-mode segment.");
            scale = Math.Max(scale, v);
        }
        return scale;
    }

    // ------------------------------------------------------------------ loss continuation

    /// <summary>The stackup with every loss tangent multiplied by <paramref name="fraction"/>
    /// (0 ⇒ the lossless twin).</summary>
    internal static LayeredStackup WithLossFraction(LayeredStackup stackup, double fraction) =>
        new(stackup.Layers.Select(l => new LayeredStackup.Layer(
            l.RelativePermittivity, l.LossTangent * fraction, l.ThicknessMeters)).ToArray());

    /// <summary>Lossless roots closer than this (relative to k₀) form a CLUSTER: below it the
    /// roundoff floor of Newton on D (≈ ε_mach·|D|/|D′|, and |D′| ∝ the separation) exceeds a
    /// quarter of the separation, so the members cannot be told apart at small loss and are
    /// tracked as one multiple root until loss resolves them.</summary>
    internal const double ClusterTolerance = 1e-7;

    /// <summary>
    /// Follow every lossless root of one family into the complex plane by a geometric
    /// continuation in the loss fraction s: tanδ_i(s) = s·tanδ_i, from s = 1e-4 to 1 in
    /// <paramref name="steps"/> geometric steps, complex Newton on <see cref="Dispersion"/> at
    /// each step from the previous converged value. All roots of the family advance together and
    /// a step is accepted only when every Newton converges AND no root moves more than a quarter
    /// of its distance to the nearest other root or to a segment end; otherwise (when
    /// <paramref name="adaptive"/>) the step is halved in ln s and retried — the step size
    /// persists, growing back toward the coarse step after acceptances. That is what keeps the
    /// tracking one-to-one: independent Newton solves from two close seeds can converge to the
    /// SAME lossy pole (ε = [10 − j0.001, 1, 10] loses the pole at 2.192438130 − j1.79e-4 for
    /// every finite-difference step).
    ///
    /// <para><b>Sub-noise clusters.</b> Roots closer than <see cref="ClusterTolerance"/>·k₀ (the
    /// TM family of that same stack has a pair 1.2e-9·k₀ apart: the PEC-backed 1-thick slab and
    /// the 2-thick slab in air share their even-mode roots and couple only through five
    /// wavelengths of air) sit below Newton's roundoff floor, so no step size tracks them
    /// individually. A cluster's members are solved with DEFLATION (D divided by (k − k_found)
    /// for each member already located at this step) and are accepted as resolved only when
    /// pairwise farther apart than four times their combined Newton uncertainty; until then the
    /// cluster is carried as one multiple root at the mean, and the neighbourhood check applies
    /// between clusters, not within one. Loss resolves such a pair (one mode lives in the lossy
    /// slab, the other does not); if it has not by s = 1 the distinctness guard below refuses.</para>
    ///
    /// <para>After the last step every pole must have Im ≤ 0, a residual within
    /// <see cref="ResidualTolerance"/> of the lossy |D| scale on the segment, and be pairwise
    /// farther than <see cref="DistinctnessTolerance"/>·k₀ apart — a collision is a typed
    /// failure, never a silent duplicate. The lossless seed is never tested against the lossy
    /// residual.</para>
    ///
    /// <para><paramref name="adaptive"/> = false is a DIAGNOSTIC mode: no halving, no
    /// neighbourhood check, no clustering — plain independent Newton per step, the failure mode
    /// described above, kept so a test can show the distinctness guard firing on a coarse
    /// continuation.</para>
    /// </summary>
    public static Complex[] LossContinuation(LayeredStackup stackup, double k0, bool isTm,
        IReadOnlyList<double> losslessRoots, int steps = 8, bool adaptive = true)
    {
        if (steps < 1) throw new ArgumentOutOfRangeException(nameof(steps));
        int count = losslessRoots.Count;
        var current = losslessRoots.Select(r => new Complex(r, 0)).ToArray();
        if (count == 0) return current;
        var (segLo, segHi) = Segment(stackup, k0);
        var previousUncertainty = new double[count];

        const double startFraction = 1e-4;
        double coarseStep = -Math.Log(startFraction) / steps;
        double stepLog = coarseStep;
        double positionLog = Math.Log(startFraction) - coarseStep;   // virtual: s = 0
        bool atSeeds = true;
        int consecutiveRejections = 0, attempts = 0;
        string? lastRejection = null;
        while (atSeeds || positionLog < 0)
        {
            if (++attempts > 4000)
                throw new InvalidOperationException(
                    $"Loss continuation of the {Family(isTm)} surface-wave poles stalled after 4000 steps at "
                    + $"loss fraction {(atSeeds ? 0 : Math.Exp(positionLog)):g3} with step {stepLog:g3} in ln s"
                    + (lastRejection is null ? "." : $"; last rejection: {lastRejection}."));
            double targetLog = Math.Min(positionLog + stepLog, 0);
            var atStep = WithLossFraction(stackup, Math.Exp(targetLog));
            Complex D(Complex k) => Dispersion(atStep, k0, k, isTm);
            var next = new Complex[count];
            var uncertainty = new double[count];
            bool accepted = true;
            // Clusters are re-formed every step from the CURRENT positions: two poles that were
            // well separated when lossless can approach each other under loss.
            var clusters = adaptive ? Clusters(current, previousUncertainty, k0)
                : Enumerable.Range(0, count).Select(i => new[] { i }).ToArray();
            foreach (var cluster in clusters)
            {
                var found = new List<Complex>();
                foreach (int i in cluster)
                {
                    var deflateBy = found.ToArray();
                    Complex F(Complex k)
                    {
                        Complex v = D(k);
                        foreach (var kf in deflateBy) v /= k - kf;
                        return v;
                    }
                    bool converged = Newton(F, k0, current[i], out next[i], out uncertainty[i]);
                    if (!converged && !adaptive)
                        throw new InvalidOperationException(
                            $"Newton on the {Family(isTm)} pole seeded at {current[i]} did not converge at loss "
                            + $"fraction {Math.Exp(targetLog):g3} (non-adaptive continuation).");
                    if (!converged)
                    {
                        accepted = false;
                        lastRejection = $"Newton from {current[i]} did not converge";
                        break;
                    }
                    found.Add(next[i]);
                }
                if (!accepted) break;
                if (cluster.Length > 1)
                {
                    bool resolved = true;
                    for (int a = 0; a < cluster.Length && resolved; a++)
                        for (int b = 0; b < a; b++)
                        {
                            int i = cluster[a], j = cluster[b];
                            double floor = Math.Max(4 * (uncertainty[i] + uncertainty[j]), DistinctnessTolerance * k0);
                            if ((next[i] - next[j]).Magnitude <= floor) { resolved = false; break; }
                        }
                    if (!resolved)
                    {
                        Complex mean = Complex.Zero;
                        foreach (int i in cluster) mean += next[i];
                        mean /= cluster.Length;
                        foreach (int i in cluster) next[i] = mean;
                    }
                }
                if (!adaptive) continue;
                // Neighbourhood: the cluster centroid may move at most a quarter of its distance
                // to any other cluster's centroid or to a segment end.
                Complex before = Centroid(current, cluster), after = Centroid(next, cluster);
                double radius = Math.Min(before.Real - segLo, segHi - before.Real);
                foreach (var other in clusters)
                    if (!ReferenceEquals(other, cluster))
                        radius = Math.Min(radius, (before - Centroid(current, other)).Magnitude);
                radius *= 0.25;
                double moved = (after - before).Magnitude;
                if (moved > radius)
                {
                    accepted = false;
                    lastRejection = $"the pole at {before} moved {moved:g3}, beyond its isolating radius {radius:g3}";
                    break;
                }
            }
            if (accepted)
            {
                current = next;
                previousUncertainty = uncertainty;
                positionLog = targetLog;
                atSeeds = false;
                consecutiveRejections = 0;
                stepLog = Math.Min(2 * stepLog, coarseStep);
                continue;
            }
            if (++consecutiveRejections > 60)
                throw new InvalidOperationException(
                    $"Loss continuation of the {Family(isTm)} surface-wave poles could not take the step "
                    + $"from loss fraction {(atSeeds ? 0 : Math.Exp(positionLog)):g3} toward {Math.Exp(targetLog):g3} "
                    + $"one-to-one even after 60 halvings: {lastRejection}.");
            stepLog *= 0.5;
        }

        // Final guards on the fully lossy stack.
        double scale = 0;
        for (int i = 0; i <= ScaleGridSamples; i++)
            scale = Math.Max(scale, Dispersion(stackup, k0, segLo + (segHi - segLo) * i / ScaleGridSamples, isTm).Magnitude);
        for (int i = 0; i < count; i++)
        {
            if (current[i].Imaginary > 0)
                throw new InvalidOperationException(
                    $"A {Family(isTm)} pole converged to the non-physical half-plane (k_ρ = {current[i]}).");
            double residual = Dispersion(stackup, k0, current[i], isTm).Magnitude;
            if (residual > ResidualTolerance * scale)
                throw new InvalidOperationException(
                    $"The lossy {Family(isTm)} pole at k_ρ = {current[i]} has residual |D| = {residual:g3} "
                    + $"against a segment scale of {scale:g3}; it is not a root.");
            for (int j = 0; j < i; j++)
                if ((current[i] - current[j]).Magnitude <= DistinctnessTolerance * k0)
                    throw new InvalidOperationException(
                        $"Two {Family(isTm)} surface-wave modes merged under loss at k_ρ = {current[i]} "
                        + $"(seeds {losslessRoots[j]} and {losslessRoots[i]}); continuation could not separate them.");
        }
        return current;
    }

    /// <summary>Transitive grouping of the current poles: i and j share a cluster when they are
    /// within <see cref="ClusterTolerance"/>·k₀ of each other, or within eight times their
    /// combined Newton uncertainty from the previous step (the roundoff floor measured, not
    /// assumed).</summary>
    private static int[][] Clusters(Complex[] poles, double[] uncertainty, double k0)
    {
        int n = poles.Length;
        var label = Enumerable.Range(0, n).ToArray();
        int Find(int i) => label[i] == i ? i : label[i] = Find(label[i]);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < i; j++)
            {
                double tolerance = Math.Max(ClusterTolerance * k0, 8 * (uncertainty[i] + uncertainty[j]));
                if ((poles[i] - poles[j]).Magnitude <= tolerance) label[Find(i)] = Find(j);
            }
        return Enumerable.Range(0, n).GroupBy(Find).Select(g => g.OrderBy(i => i).ToArray()).ToArray();
    }

    private static Complex Centroid(Complex[] values, int[] members)
    {
        Complex sum = Complex.Zero;
        foreach (int i in members) sum += values[i];
        return sum / members.Length;
    }

    /// <summary>Complex Newton on <paramref name="f"/> with a central-difference slope.
    /// Converged when the step falls to 1e-13 relative, or — the roundoff floor — when the step
    /// is already below 1e-7 relative and the residual has stopped decreasing: at a near-
    /// degenerate pair f′ is tiny, so roundoff in f alone moves the iterate by more than 1e-13.
    /// Returns the smallest-residual iterate and the size of the last step as its uncertainty;
    /// false when 50 iterations leave the step above 1e-7 relative.</summary>
    private static bool Newton(Func<Complex, Complex> f, double k0, Complex seed, out Complex root, out double uncertainty)
    {
        Complex kp = seed;
        Complex best = seed;
        double bestResidual = f(seed).Magnitude;
        double lastStep = double.PositiveInfinity;
        int stagnant = 0;
        for (int iteration = 0; iteration < 50; iteration++)
        {
            Complex delta = 1e-7 * Math.Max(kp.Magnitude, k0);
            Complex slope = (f(kp + delta) - f(kp - delta)) / (2 * delta);
            if (slope == Complex.Zero) break;
            Complex step = f(kp) / slope;
            kp -= step;
            if (!double.IsFinite(kp.Real) || !double.IsFinite(kp.Imaginary)) break;
            lastStep = step.Magnitude;
            double residual = f(kp).Magnitude;
            if (residual < bestResidual)
            {
                best = kp;
                bestResidual = residual;
                stagnant = 0;
            }
            else stagnant++;
            double relativeStep = lastStep / kp.Magnitude;
            if (relativeStep <= 1e-13 || (relativeStep <= 1e-7 && stagnant >= 2))
            {
                root = best;
                uncertainty = lastStep;
                return true;
            }
        }
        root = best;
        uncertainty = lastStep;
        return false;
    }
}
