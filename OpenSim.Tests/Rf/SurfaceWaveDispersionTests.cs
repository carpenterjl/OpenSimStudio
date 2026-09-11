using System.Numerics;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// WI-4 (defect D2) gates for the transfer-matrix surface-wave characteristic function and
/// the Sturm mode-count root isolation in <see cref="SurfaceWaveDispersion"/>: the single-slab
/// identities (finite where the old reflection-recursion form had poles), the closed-form
/// cutoff counts, the coupled-layer close pair no sampling could separate, the top-zero and
/// single-slab TM regressions from the plan's review rounds, the deep-spacer overflow case,
/// and the one-to-one loss continuation with its distinctness guard.
/// </summary>
public class SurfaceWaveDispersionTests
{
    private static LayeredStackup Stack(params (double EpsR, double TanD, double T)[] layers) =>
        new(layers.Select(l => new LayeredStackup.Layer(l.EpsR, l.TanD, l.T)).ToArray());

    private static double K0(double fHz) => 2 * Math.PI * fHz / 299_792_458.0;

    /// <summary>max |D| on a 4000-point grid of the bound-mode segment — the residual scale.</summary>
    private static double Scale(LayeredStackup s, double k0, bool isTm)
    {
        var (lo, hi) = SurfaceWaveDispersion.Segment(s, k0);
        double scale = 0;
        for (int i = 0; i <= 4000; i++)
            scale = Math.Max(scale, Math.Abs(SurfaceWaveDispersion.Shoot(s, k0, lo + (hi - lo) * i / 4000, isTm).D));
        return scale;
    }

    private static void AssertModeCountNonIncreasing(LayeredStackup s, double k0, bool isTm)
    {
        var (lo, hi) = SurfaceWaveDispersion.Segment(s, k0);
        int previous = int.MaxValue;
        for (int i = 0; i <= 4000; i++)
        {
            double kr = lo + (hi - lo) * i / 4000;
            int n = SurfaceWaveDispersion.ModeCount(s, k0, kr, isTm);
            Assert.True(n <= previous, $"N rose from {previous} to {n} at k_ρ/k₀ = {kr / k0} ({(isTm ? "TM" : "TE")})");
            previous = n;
        }
    }

    // ---------------------------------------------------------------- single-slab identities

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleSlab_IsThePinnedFunctionUpToANonVanishingFactor_AndFiniteAtTheOldPoles(bool isTm)
    {
        // εr 10.2 / 6 mm / 10 GHz (u_max = 3.81): the old (1 − R)/(1 + R) form had poles at
        // u = k_z1·d = π/2 (TM) and π (TE) on this segment that the bracketer converged onto.
        // Transfer-matrix form: D_TE = [u·cos u + γ₀d·sin u]/u, D_TM = −[u·sin u − εr·γ₀d·cos u]/(εr·d).
        const double epsR = 10.2, d = 6.0e-3;
        double k0 = K0(10e9), k1 = k0 * Math.Sqrt(epsR);
        double uMax = d * Math.Sqrt(k1 * k1 - k0 * k0);
        Assert.True(uMax > Math.PI, $"u_max = {uMax} must pass π for both old poles to lie on the segment");
        var stack = Stack((epsR, 0, d));

        var us = Enumerable.Range(1, 199).Select(i => uMax * i / 200).Append(Math.PI / 2).Append(Math.PI).ToArray();
        double scale = 0;
        var pairs = new List<(double U, double Pinned, Complex D)>();
        foreach (double u in us)
        {
            double kRho = Math.Sqrt(k1 * k1 - (u / d) * (u / d));
            double gamma0d = Math.Sqrt(uMax * uMax - u * u);
            double pinned = isTm
                ? u * Math.Sin(u) - epsR * gamma0d * Math.Cos(u)
                : u * Math.Cos(u) + gamma0d * Math.Sin(u);
            Complex dNew = isTm
                ? TransmissionLineGreens.TmDispersion(stack, k0, kRho)
                : TransmissionLineGreens.TeDispersion(stack, k0, kRho);
            Assert.True(double.IsFinite(dNew.Real) && double.IsFinite(dNew.Imaginary), $"D not finite at u = {u}");
            Assert.Equal(0.0, dNew.Imaginary);   // exactly real on the lossless segment
            scale = Math.Max(scale, Math.Abs(pinned));
            pairs.Add((u, pinned, dNew));
        }
        foreach (var (u, pinned, dNew) in pairs)
        {
            double asPinned = isTm ? -epsR * d * dNew.Real : u * dNew.Real;
            Assert.True(Math.Abs(asPinned - pinned) <= 1e-10 * scale,
                $"u = {u}: D_new scaled = {asPinned} vs pinned {pinned} (scale {scale})");
        }
        // The old poles: bounded, and by the same factor as everywhere else.
        foreach (double u in new[] { Math.PI / 2, Math.PI })
        {
            var (_, pinned, dNew) = pairs.Last(p => p.U == u);
            Assert.True(dNew.Magnitude <= 10 * scale / (isTm ? epsR * d : u), $"|D| at u = {u} is {dNew.Magnitude}");
            Assert.True(Math.Abs(pinned) <= scale);
        }
    }

    // ---------------------------------------------------------------- mode counting

    [Theory]
    [InlineData(10.0, 1.0)]     // u_max = 3      → TM 1, TE 1
    [InlineData(10.0, 2.0)]     // u_max = 6      → TM 2, TE 2
    [InlineData(10.0, 5.0)]     // u_max = 15     → TM 5, TE 5
    [InlineData(2.2, 3.0)]      // u_max = 3.286  → TM 2, TE 1
    [InlineData(4.0, 0.3)]      // u_max = 0.520  → TM 1, TE 0
    public void SingleSlab_ModeCountAtK0_IsTheClosedFormCutoffCount(double epsR, double t)
    {
        // Cutoffs sit at u_max = nπ/2: n even ⇒ TM (TM0 always exists), n odd ⇒ TE.
        const double k0 = 1;
        double uMax = t * Math.Sqrt(epsR - 1);
        int tm = (int)Math.Floor(uMax / Math.PI) + 1;
        int te = (int)Math.Floor(uMax / Math.PI + 0.5);
        var stack = Stack((epsR, 0, t));
        var (lo, _) = SurfaceWaveDispersion.Segment(stack, k0);
        Assert.Equal(tm, SurfaceWaveDispersion.ModeCount(stack, k0, lo, isTm: true));
        Assert.Equal(te, SurfaceWaveDispersion.ModeCount(stack, k0, lo, isTm: false));
        Assert.Equal(tm, SurfaceWaveDispersion.LosslessRoots(stack, k0, isTm: true).Count);
        Assert.Equal(te, SurfaceWaveDispersion.LosslessRoots(stack, k0, isTm: false).Count);
        AssertModeCountNonIncreasing(stack, k0, isTm: true);
        AssertModeCountNonIncreasing(stack, k0, isTm: false);
        var poles = SurfaceWavePoles.Find(stack, k0);
        Assert.Equal(tm, poles.Count(p => p.IsTm));
        Assert.Equal(te, poles.Count(p => !p.IsTm));
    }

    [Fact]
    public void SingleSlabTm_ModeCountStepsAtThePinnedRoot()
    {
        // Round-4 regression: a γ₀·p_top Robin angle moved this transition to 2.937825. The
        // count must step 1 → 0 at the pinned finder's TM0 root, 2.777119337, and be 0 at 2.85.
        const double k0 = 1;
        var stack = Stack((10.0, 0, 1.0));
        var pinned = Assert.Single(SurfaceWavePoles.Find(new SubstrateStackup(10.0, 0, 1.0), k0), p => p.IsTm).KRho.Real;
        Assert.True(Math.Abs(pinned - 2.777119337) < 1e-9, $"pinned TM0 root {pinned}");
        Assert.Equal(1, SurfaceWaveDispersion.ModeCount(stack, k0, pinned - 1e-6, isTm: true));
        Assert.Equal(0, SurfaceWaveDispersion.ModeCount(stack, k0, pinned + 1e-6, isTm: true));
        Assert.Equal(0, SurfaceWaveDispersion.ModeCount(stack, k0, 2.85, isTm: true));
        var root = Assert.Single(SurfaceWaveDispersion.LosslessRoots(stack, k0, isTm: true));
        Assert.True(Math.Abs(root.KRho - pinned) < 1e-9, $"isolated {root.KRho} vs pinned {pinned}");
    }

    [Fact]
    public void TopZeroThatIsNotAnEigenvalue_DoesNotStepTheModeCount()
    {
        // Round-5 regression: εr = [5, 4], t = [1, 0.8026157699179134] has A_top = 0 at k_ρ = 2
        // for TM with D = −0.168 ≠ 0. An open-interval zero count reads 1, 0, 1 across k_ρ = 2;
        // the half-open convention keeps N flat, so bisection on N never isolates k_ρ = 2.
        const double k0 = 1;
        var stack = Stack((5.0, 0, 1.0), (4.0, 0, 0.8026157699179134));
        var shot = SurfaceWaveDispersion.Shoot(stack, k0, 2.0, isTm: true);
        Assert.True(Math.Abs(shot.ATop) < 1e-12, $"A_top at k_ρ = 2 is {shot.ATop}, not a top zero");
        Assert.True(Math.Abs(shot.D + 0.168) < 1e-3, $"D at k_ρ = 2 is {shot.D}");
        int below = SurfaceWaveDispersion.ModeCount(stack, k0, 2 - 1e-8, isTm: true);
        int at = SurfaceWaveDispersion.ModeCount(stack, k0, 2.0, isTm: true);
        int above = SurfaceWaveDispersion.ModeCount(stack, k0, 2 + 1e-8, isTm: true);
        Assert.Equal(below, at);
        Assert.Equal(at, above);
        AssertModeCountNonIncreasing(stack, k0, isTm: true);
        foreach (var root in SurfaceWaveDispersion.LosslessRoots(stack, k0, isTm: true))
            Assert.True(Math.Abs(root.KRho - 2.0) > 1e-6, $"a root was isolated at the top zero: {root.KRho}");
    }

    [Fact]
    public void CoupledLayers_IsolateTheClosePair_ThatNoSamplingCouldSeparate()
    {
        // Round-3 counterexample: εr = [10, 1, 10], k₀·t = [1, 5, 2] has TE roots at
        // k_ρ/k₀ ≈ 2.192410, 2.192466 (5.6e-5·k₀ apart) and 2.937825. A 4000-point sign-change
        // sampler and a 256-panel winding number both returned fewer. N(k₀⁺) = 3 and bisection
        // on N isolates all three, each polished inside its own interval.
        const double k0 = 1;
        var stack = Stack((10.0, 0, 1.0), (1.0, 0, 5.0), (10.0, 0, 2.0));
        var (lo, _) = SurfaceWaveDispersion.Segment(stack, k0);
        Assert.Equal(3, SurfaceWaveDispersion.ModeCount(stack, k0, lo, isTm: false));
        AssertModeCountNonIncreasing(stack, k0, isTm: false);

        var roots = SurfaceWaveDispersion.LosslessRoots(stack, k0, isTm: false).OrderBy(r => r.KRho).ToArray();
        Assert.Equal(3, roots.Length);
        double[] expected = { 2.192410, 2.192466, 2.937825 };
        double scale = Scale(stack, k0, isTm: false);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(Math.Abs(roots[i].KRho - expected[i]) < 1e-6, $"root {i}: {roots[i].KRho} vs {expected[i]}");
            Assert.True(roots[i].KRho > roots[i].Lo && roots[i].KRho < roots[i].Hi,
                $"root {roots[i].KRho} outside its isolating interval [{roots[i].Lo}, {roots[i].Hi}]");
            double residual = Math.Abs(SurfaceWaveDispersion.Shoot(stack, k0, roots[i].KRho, isTm: false).D);
            Assert.True(residual <= 1e-8 * scale, $"root {i} residual {residual} vs scale {scale}");
        }
        double gap = roots[1].KRho - roots[0].KRho;
        Assert.InRange(gap, 5.0e-5, 6.5e-5);
        Assert.True(roots[0].Hi <= roots[1].Lo, "the pair's isolating intervals overlap");

        var poles = SurfaceWavePoles.Find(stack, k0);
        Assert.Equal(3, poles.Count(p => !p.IsTm));
    }

    [Fact]
    public void DeepAirSpacer_StaysFinite_AndReproducesTheUngroundedSymmetricSlab()
    {
        // A slab εr = 10, k₀t = 1 over an air spacer of k₀t = 2000 on the PEC: cosh(2000·γ)
        // overflows any post-hoc normalisation, but the pre-scaled layer matrix is bounded. The
        // deep spacer makes the ground invisible, so the poles are those of the SYMMETRIC slab
        // (half-thickness a = 0.5/k₀) in free space — even modes only, since k_z1·a ≤ 1.5 < π/2:
        //   TE (E_y):  k_z1·tan(k_z1 a) = γ₀,      TM (H_y):  (k_z1/εr)·tan(k_z1 a) = γ₀.
        // (Removing the spacer would put the slab ON the PEC — a different problem: 2.579 vs 2.192.)
        const double k0 = 1, epsR = 10, a = 0.5;
        var stack = Stack((1.0, 0, 2000.0), (epsR, 0, 2 * a));
        var (lo, hi) = SurfaceWaveDispersion.Segment(stack, k0);
        foreach (bool isTm in new[] { false, true })
        {
            for (int i = 0; i <= 4000; i++)
            {
                double kr = lo + (hi - lo) * i / 4000;
                var shot = SurfaceWaveDispersion.Shoot(stack, k0, kr, isTm);
                Assert.True(double.IsFinite(shot.D) && double.IsFinite(shot.ATop) && double.IsFinite(shot.BTop), $"real shot not finite at {kr}");
                var d = SurfaceWaveDispersion.Dispersion(stack, k0, kr, isTm);
                Assert.True(double.IsFinite(d.Real) && double.IsFinite(d.Imaginary), $"complex D not finite at {kr}");
            }
            AssertModeCountNonIncreasing(stack, k0, isTm);

            double Even(double kr)
            {
                double kz1 = Math.Sqrt(epsR - kr * kr), g0 = Math.Sqrt(kr * kr - 1);
                return kz1 * Math.Tan(kz1 * a) / (isTm ? epsR : 1) - g0;
            }
            double x0 = 1 + 1e-9, x1 = Math.Sqrt(epsR) - 1e-9;
            Assert.True(Even(x0) > 0 && Even(x1) < 0, "the even-mode closed form must bracket exactly one root");
            for (int i = 0; i < 200; i++)
            {
                double m = 0.5 * (x0 + x1);
                if (Even(m) > 0) x0 = m; else x1 = m;
            }
            double closedForm = 0.5 * (x0 + x1);
            var root = Assert.Single(SurfaceWaveDispersion.LosslessRoots(stack, k0, isTm));
            Assert.True(Math.Abs(root.KRho - closedForm) < 1e-9,
                $"{(isTm ? "TM" : "TE")} over the deep spacer: {root.KRho} vs symmetric slab {closedForm}");
        }
        Assert.Equal(2, SurfaceWavePoles.Find(stack, k0).Count);
    }

    // ---------------------------------------------------------------- the D2 reproduction

    [Theory]
    [InlineData(25e9, 1e-6)]    // above onset: the old finder returned a third, spurious TM pole
    [InlineData(12e9, 1e-11)]   // below onset: the control that already agreed
    public void ThinHighEpsSlab_MultiLayerTableEqualsTheSingleSlabTable(double fHz, double tolerance)
    {
        var substrate = new SubstrateStackup(10.2, 0, 1.27e-3);
        double k0 = K0(fHz);
        var reference = SurfaceWavePoles.Find(substrate, k0);
        var general = SurfaceWavePoles.Find(LayeredStackup.FromSubstrate(substrate), k0);
        Assert.Equal(reference.Count, general.Count);
        foreach (var r in reference)
        {
            var g = general.Single(p => p.IsTm == r.IsTm && Math.Abs(p.KRho.Real - r.KRho.Real) < 1e-6 * k0);
            Assert.True((g.KRho - r.KRho).Magnitude < 1e-9 * k0, $"k_p {g.KRho} vs pinned {r.KRho}");
            Assert.True((g.ResiduePhi - r.ResiduePhi).Magnitude < 1e-7 * r.ResiduePhi.Magnitude,
                $"Res_Φ {g.ResiduePhi} vs pinned {r.ResiduePhi}");
        }

        const double rhoMax = 15e-3;
        var single = new LayeredKernelTable(substrate, fHz, rhoMax);
        var multi = new MultiLayerKernelTable(LayeredStackup.FromSubstrate(substrate), fHz, rhoMax);
        foreach (double rho in new[] { 0.2e-3, 0.5e-3, 1e-3, 2e-3, 5e-3, 10e-3, 15e-3 })
        {
            var (_, phiRef) = single.EvaluateKernelsDirect(rho, refinement: 3);
            var (_, phi) = multi.EvaluateKernelsDirect(rho, refinement: 3);
            double rel = (phi - phiRef).Magnitude / phiRef.Magnitude;
            Assert.True(rel < tolerance, $"K_Φ at ρ = {rho * 1e3} mm, {fHz / 1e9} GHz: {phi} vs single-slab {phiRef} (rel {rel:e2})");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoLayerThickStack_EveryRootPassesBothGuards(bool isTm)
    {
        // A genuinely two-layer stack above onset: εr 10.2 / 1.27 mm under 2.2 / 1.0 mm at 25 GHz.
        double k0 = K0(25e9);
        var stack = Stack((10.2, 0, 1.27e-3), (2.2, 0, 1.0e-3));
        var roots = SurfaceWaveDispersion.LosslessRoots(stack, k0, isTm);
        Assert.NotEmpty(roots);
        double scale = Scale(stack, k0, isTm);
        foreach (var root in roots)
        {
            Assert.True(root.KRho > root.Lo && root.KRho < root.Hi, $"{root.KRho} outside [{root.Lo}, {root.Hi}]");
            double residual = Math.Abs(SurfaceWaveDispersion.Shoot(stack, k0, root.KRho, isTm).D);
            Assert.True(residual <= 1e-8 * scale, $"residual {residual} vs scale {scale}");
        }
        AssertModeCountNonIncreasing(stack, k0, isTm);
        var (lo, _) = SurfaceWaveDispersion.Segment(stack, k0);
        Assert.Equal(SurfaceWaveDispersion.ModeCount(stack, k0, lo, isTm), roots.Count);
    }

    // ---------------------------------------------------------------- lossy continuation

    private static LayeredStackup LossyClosePair() =>
        Stack((10.0, 1e-4, 1.0), (1.0, 0, 5.0), (10.0, 0, 2.0));   // ε₀ = 10(1 − j1e-4) = 10 − j0.001

    [Fact]
    public void LossyClosePair_ContinuationKeepsBothPolesDistinct()
    {
        // ε = [10 − j0.001, 1, 10]: the two TE poles near 2.19244 separate under loss in the
        // IMAGINARY direction (≈ −4.3e-6 and −1.79e-4) while their real parts stay within 1e-8
        // of each other — independent Newton from the two lossless seeds lands on the same one.
        const double k0 = 1;
        var te = SurfaceWavePoles.Find(LossyClosePair(), k0).Where(p => !p.IsTm).OrderBy(p => p.KRho.Real).ToArray();
        Assert.Equal(3, te.Length);
        var pair = te.Take(2).OrderBy(p => -p.KRho.Imaginary).ToArray();   // shallow first
        foreach (var p in pair) Assert.True(Math.Abs(p.KRho.Real - 2.192438) < 1e-5, $"Re {p.KRho}");
        Assert.InRange(pair[0].KRho.Imaginary, -4.3e-6 * 1.15, -4.3e-6 * 0.85);
        Assert.InRange(pair[1].KRho.Imaginary, -1.79e-4 * 1.15, -1.79e-4 * 0.85);
        Assert.True(Math.Abs(te[2].KRho.Real - 2.937825) < 1e-4 && te[2].KRho.Imaginary < 0, $"third {te[2].KRho}");
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < i; j++)
                Assert.True((te[i].KRho - te[j].KRho).Magnitude > 1e-9 * k0, "duplicate pole");
    }

    [Fact]
    public void LossyClosePair_ACoarseIndependentContinuationCollides_AndTheGuardFires()
    {
        // The failure the one-to-one tracking exists to prevent, reproduced on purpose: a single
        // non-adaptive step (plain Newton from each lossless seed) merges the pair, and the
        // distinctness guard refuses it by name instead of returning a silent duplicate.
        const double k0 = 1;
        var stack = LossyClosePair();
        var seeds = SurfaceWaveDispersion.LosslessRoots(stack, k0, isTm: false).Select(r => r.KRho).ToArray();
        Assert.Equal(3, seeds.Length);
        var e = Assert.Throws<InvalidOperationException>(() =>
            SurfaceWaveDispersion.LossContinuation(stack, k0, isTm: false, seeds, steps: 1, adaptive: false));
        Assert.Contains("merged under loss", e.Message);
        // And the shipped (adaptive) continuation from the same seeds separates them.
        var tracked = SurfaceWaveDispersion.LossContinuation(stack, k0, isTm: false, seeds);
        Assert.Equal(3, tracked.Length);
        Assert.True((tracked[0] - tracked[1]).Magnitude > 1e-4, $"pair {tracked[0]} / {tracked[1]} not separated");
    }
}
