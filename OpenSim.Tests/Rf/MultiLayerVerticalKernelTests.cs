using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage C1 — the multi-layer VERTICAL kernels
/// (<see cref="TransmissionLineGreens.EvaluateVertical"/>).
///
/// <para>The headline gate is the N = 1 identity: on a one-layer stackup the assembled kernels
/// must reproduce <see cref="VerticalSpectralKernels"/>' closed forms. That is what FIXES every
/// convention in the new code — the source strength −2µ₀/ε, which side the profile is read from,
/// the 1/(k₀²ε) gauge factor, the sign of each of the five assembled terms — so none of them had
/// to be guessed and none is a free parameter. A single wrong sign moves it far past 1e-12.</para>
/// </summary>
public class MultiLayerVerticalKernelTests
{
    private const double FrequencyHz = 10e9;
    private static double K0 => 2 * Math.PI * FrequencyHz / 299_792_458.0;
    private static readonly SubstrateStackup Balanis = new(2.2, 0.0009, 1.588e-3);

    private static Complex Kz(Complex kSq, Complex kRho)
    {
        var k = Complex.Sqrt(kSq - kRho * kRho);
        return k.Imaginary > 0 ? -k : k;
    }

    private static double Rel(Complex a, Complex b) =>
        (a - b).Magnitude / Math.Max(Math.Max(a.Magnitude, b.Magnitude), 1e-300);

    public static IEnumerable<object[]> Samples()
    {
        foreach (double zOverD in new[] { 0.15, 0.45, 0.62, 0.9 })
            foreach (double zPrimeOverD in new[] { 0.3, 0.62, 0.85 })
                foreach (double kRhoOverK0 in new[] { 0.4, 0.95, 1.3, 3.0, 9.0 })
                    yield return new object[] { zOverD, zPrimeOverD, kRhoOverK0 };
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void AtOneLayer_TheyAreTheSingleSlabClosedForms(
        double zOverD, double zPrimeOverD, double kRhoOverK0)
    {
        double d = Balanis.ThicknessMeters, k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        double z = zOverD * d, zPrime = zPrimeOverD * d;

        var expected = VerticalSpectralKernels.Evaluate(Balanis, k0, kRho, kz0, z, zPrime);
        var got = TransmissionLineGreens.EvaluateVertical(
            LayeredStackup.FromSubstrate(Balanis), k0, kRho, kz0, z, zPrime);

        string at = $"z/d = {zOverD}, z'/d = {zPrimeOverD}, k_rho/k0 = {kRhoOverK0}";
        Assert.True(Rel(expected.KPhi, got.KPhi) < 1e-12,
            $"K_Phi at {at}: expected {expected.KPhi}, got {got.KPhi} (rel {Rel(expected.KPhi, got.KPhi):g3})");
        // G_A^xz gets a decade more room than the others, and the reason is measured rather than
        // assumed (see TheXzAssemblyIsACancellingCombination below): it is assembled as a SUM whose
        // terms cancel by up to ~230x at the largest height separations deep in the evanescent
        // tail, so its relative accuracy floors near 1e-11 there. K_Phi is a direct read-out with
        // no cancellation at all and holds 1e-12; G_A^zz shares the cancelling bracket but adds the
        // dominant classical term on top, which dilutes it.
        Assert.True(Rel(expected.GAxz, got.GAxz) < 1e-10,
            $"G_A^xz at {at}: expected {expected.GAxz}, got {got.GAxz} (rel {Rel(expected.GAxz, got.GAxz):g3})");
        Assert.True(Rel(expected.GAzz, got.GAzz) < 1e-11,
            $"G_A^zz at {at}: expected {expected.GAzz}, got {got.GAzz} (rel {Rel(expected.GAzz, got.GAzz):g3})");
    }

    [Fact]
    public void AVerticalSourceOnAMaterialInterfaceIsATypedFailure()
    {
        // The source strength is −2µ₀/ε and the two sides disagree, so the kernel is a limit that
        // depends on the approach rather than a value. Refusing beats picking a side silently; a
        // tube element is split at every interface, so a solve never asks.
        var covered = LayeredStackup.CoveredPatch(4.4, 0.02, 0.8e-3, 2.2, 0.0009, 0.5e-3);
        double k0 = K0;
        var kRho = new Complex(1.2 * k0, 0);
        var e = Assert.Throws<ArgumentException>(() => TransmissionLineGreens.EvaluateVertical(
            covered, k0, kRho, Kz(k0 * k0, kRho), z: 0.4e-3, zPrime: 0.8e-3));
        Assert.Contains("material interface", e.Message);

        // The top plane is the same situation (dielectric below, air above).
        var slab = LayeredStackup.FromSubstrate(Balanis);
        Assert.Throws<ArgumentException>(() => TransmissionLineGreens.EvaluateVertical(
            slab, k0, kRho, Kz(k0 * k0, kRho), z: 0.5e-3, zPrime: Balanis.ThicknessMeters));
    }

    [Fact]
    public void TheXzAssemblyIsACancellingCombination_WhichSetsItsBand()
    {
        // Why G_A^xz is banded a decade looser than its siblings, checked rather than asserted.
        // The kernel is scale x [dz A_z^cl + d2 g_TE + dz dz' a_z], and the first two carry the
        // primaries that largely cancel, leaving the epsilon-contrast coupling. Measuring the
        // ratio |d2 g_TE| / |bracket| says how many digits that costs: ~0.7 (none) near the axis,
        // ~46 mid-band, ~233 at the largest separation deep in the evanescent tail. A refactor
        // that made the assembly better or worse conditioned would move these, and the band above
        // would then be either wasteful or wrong - so the numbers are pinned here, loosely, rather
        // than living only in a comment.
        double d = Balanis.ThicknessMeters, k0 = K0;
        var stack = LayeredStackup.FromSubstrate(Balanis);
        double worst = 0;
        foreach (var (zOverD, zPrimeOverD, kRhoOverK0) in new[]
                 { (0.15, 0.85, 9.0), (0.15, 0.85, 0.4), (0.45, 0.3, 3.0) })
        {
            var kRho = new Complex(kRhoOverK0 * k0, 0);
            var kz0 = Kz(k0 * k0, kRho);
            double z = zOverD * d, zPrime = zPrimeOverD * d;
            var got = TransmissionLineGreens.EvaluateVertical(stack, k0, kRho, kz0, z, zPrime);
            var (split, _) = stack.SplitAt(zPrime);
            var (splitZ, mz) = split.SplitAt(z);
            var d2 = TransmissionLineGreens.EvaluateField(splitZ, k0, kRho, kz0, mz, zPrime).DzA;
            double bracket = got.GAxz.Magnitude * (k0 * k0 * Balanis.RelativePermittivity);
            worst = Math.Max(worst, d2.Magnitude / bracket);
        }
        Assert.InRange(worst, 50, 2000);
    }

    [Theory]
    [InlineData(0.3, 0.7, 1.1)]
    [InlineData(0.3, 0.7, 4.0)]
    [InlineData(0.8, 0.25, 0.6)]
    [InlineData(0.55, 0.55, 2.5)]
    public void SplittingTheStackLeavesTheVerticalKernelsUnchanged(
        double zOverD, double zPrimeOverD, double kRhoOverK0)
    {
        // A layer described as two is the same stack, so the vertical kernels cannot know. This
        // is the invariance the whole arbitrary-height construction rests on, applied to the
        // kernels themselves rather than to the horizontal ones it was gated on: three extra
        // interfaces, none of them at either height, must change nothing.
        double d = Balanis.ThicknessMeters, k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        double z = zOverD * d, zPrime = zPrimeOverD * d;

        var plain = LayeredStackup.FromSubstrate(Balanis);
        var split = plain;
        foreach (double cut in new[] { 0.1 * d, 0.42 * d, 0.93 * d })
            (split, _) = split.SplitAt(cut);
        Assert.Equal(4, split.Layers.Count);

        var a = TransmissionLineGreens.EvaluateVertical(plain, k0, kRho, kz0, z, zPrime);
        var b = TransmissionLineGreens.EvaluateVertical(split, k0, kRho, kz0, z, zPrime);
        Assert.True(Rel(a.KPhi, b.KPhi) < 1e-11, $"K_Phi: {a.KPhi} vs {b.KPhi}");
        Assert.True(Rel(a.GAzz, b.GAzz) < 1e-10, $"G_A^zz: {a.GAzz} vs {b.GAzz}");
        Assert.True(Rel(a.GAxz, b.GAxz) < 1e-9, $"G_A^xz: {a.GAxz} vs {b.GAxz}");
    }

    [Theory]
    [InlineData(0.2, 0.6, 0.7)]
    [InlineData(0.2, 0.6, 3.0)]
    [InlineData(0.75, 0.4, 1.4)]
    public void AllAirIsPrimaryPlusPecImage(double zOverD, double zPrimeOverD, double kRhoOverK0)
    {
        // With every layer at vacuum the stack IS free space over a PEC, and the kernels collapse
        // to primary + image: coefficient +1 for G_A^zz (a vertical dipole images POSITIVELY over
        // a ground plane) and -1 for K_Phi, with G_A^xz identically zero since it is proportional
        // to the permittivity contrast. Run through a THREE-layer air stack, so the multi-layer
        // machinery is exercised and not just the degenerate one.
        double d = 1.588e-3, k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        double z = zOverD * d, zPrime = zPrimeOverD * d;
        var air = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(1, 0, 0.4 * d),
            new LayeredStackup.Layer(1, 0, 0.35 * d),
            new LayeredStackup.Layer(1, 0, 0.25 * d)
        });

        var got = TransmissionLineGreens.EvaluateVertical(air, k0, kRho, kz0, z, zPrime);
        var j = Complex.ImaginaryOne;
        var primary = Complex.Exp(-j * kz0 * Math.Abs(z - zPrime));
        var image = Complex.Exp(-j * kz0 * (z + zPrime));
        var expectedZz = -j * RfConstants.Mu0 / kz0 * (primary + image);
        var expectedPhi = -j / (RfConstants.Eps0 * kz0) * (primary - image);

        Assert.True(Rel(expectedZz, got.GAzz) < 1e-10,
            $"G_A^zz in air: expected {expectedZz}, got {got.GAzz} (rel {Rel(expectedZz, got.GAzz):g3})");
        Assert.True(Rel(expectedPhi, got.KPhi) < 1e-11,
            $"K_Phi in air: expected {expectedPhi}, got {got.KPhi} (rel {Rel(expectedPhi, got.KPhi):g3})");
        Assert.True(got.GAxz.Magnitude < 1e-12 * expectedZz.Magnitude,
            $"G_A^xz must vanish without a permittivity contrast; got {got.GAxz}");
    }

    // ------------------------------------------------------------------
    // The independent oracle: a vertical source solved WITHOUT formulation C
    // ------------------------------------------------------------------

    /// <summary>An INDEPENDENT solve for the classical vertical potential Ã_z: the TM line with a
    /// z-directed source, assembled in the GLOBAL e^{±jk_z z} basis (no reduced referencing, no
    /// shared helper) as ONE dense system, and with its own sub-layer split at the source height
    /// rather than the production <see cref="LayeredStackup.SplitAt"/>. A vertical current excites
    /// no A_x at all, so there is no TE half and no ε-contrast coupling — which is exactly what
    /// makes this a clean reference: it never touches the gauge machinery under test.</summary>
    private static (Complex Az, Complex DzAz, Complex Eps, Complex Kz) OracleVerticalAz(
        LayeredStackup stack, double k0, Complex kRho, double z, double zPrime)
    {
        // Build the sub-layer list, splitting the host layer at z' independently of production.
        var eps = new List<Complex>();
        var bottom = new List<double>();
        var top = new List<double>();
        double running = 0;
        int sourceInterface = -1;
        foreach (var layer in stack.Layers)
        {
            double lo = running, hi = running + layer.ThicknessMeters;
            if (zPrime > lo + 1e-15 && zPrime < hi - 1e-15)
            {
                eps.Add(layer.ComplexPermittivity); bottom.Add(lo); top.Add(zPrime);
                sourceInterface = eps.Count - 1;
                eps.Add(layer.ComplexPermittivity); bottom.Add(zPrime); top.Add(hi);
            }
            else
            {
                eps.Add(layer.ComplexPermittivity); bottom.Add(lo); top.Add(hi);
                if (Math.Abs(zPrime - hi) <= 1e-15) sourceInterface = eps.Count - 1;
            }
            running = hi;
        }
        Assert.True(sourceInterface >= 0, "the oracle could not place the source height");

        int n = eps.Count;
        double dTot = top[n - 1];
        var kz = new Complex[n];
        for (int i = 0; i < n; i++) kz[i] = Kz(eps[i] * k0 * k0, kRho);
        var kz0 = Kz(k0 * k0, kRho);
        var j = Complex.ImaginaryOne;

        int size = 2 * n + 1, sIdx = 2 * n;
        var mat = new ComplexDenseMatrix(size, size);
        var rhs = new Complex[size];
        Complex Ep(int i, double zz) => Complex.Exp(-j * kz[i] * zz);
        Complex Em(int i, double zz) => Complex.Exp(j * kz[i] * zz);
        int row = 0;

        // Ground: ∂_z A_z(0) = 0 (the TM line is OPEN at the PEC).
        mat[row, 0] = -j * kz[0] * Ep(0, 0);
        mat[row, 1] = j * kz[0] * Em(0, 0);
        row++;

        for (int i = 0; i < n - 1; i++)
        {
            double zi = top[i];
            int a = 2 * i, b = 2 * (i + 1);
            mat[row, a] = Ep(i, zi); mat[row, a + 1] = Em(i, zi);
            mat[row, b] = -Ep(i + 1, zi); mat[row, b + 1] = -Em(i + 1, zi);
            row++;
            // (1/ε)∂_z above − (1/ε)∂_z below = the source, and −2µ₀ for the raw derivative jump.
            Complex above = 1 / eps[i + 1], below = 1 / eps[i];
            mat[row, b] = above * (-j * kz[i + 1]) * Ep(i + 1, zi);
            mat[row, b + 1] = above * (j * kz[i + 1]) * Em(i + 1, zi);
            mat[row, a] = -below * (-j * kz[i]) * Ep(i, zi);
            mat[row, a + 1] = -below * (j * kz[i]) * Em(i, zi);
            if (i == sourceInterface) rhs[row] = -2 * RfConstants.Mu0 / eps[i];
            row++;
        }

        mat[row, 2 * (n - 1)] = Ep(n - 1, dTot);
        mat[row, 2 * (n - 1) + 1] = Em(n - 1, dTot);
        mat[row, sIdx] = -1;
        row++;
        Complex invTop = 1 / eps[n - 1];
        mat[row, sIdx] = -j * kz0;
        mat[row, 2 * (n - 1)] = -invTop * (-j * kz[n - 1]) * Ep(n - 1, dTot);
        mat[row, 2 * (n - 1) + 1] = -invTop * (j * kz[n - 1]) * Em(n - 1, dTot);
        if (sourceInterface == n - 1) rhs[row] = -2 * RfConstants.Mu0 / eps[n - 1];

        var sol = ComplexLu.Factor(mat).Solve(rhs);

        if (z >= dTot)
        {
            var e0 = Complex.Exp(-j * kz0 * (z - dTot));
            Complex azAir = sol[sIdx] * e0;
            return (azAir, -j * kz0 * azAir, Complex.One, kz0);
        }
        int layerAt = 0;
        while (layerAt < n - 1 && z > top[layerAt]) layerAt++;
        int p = 2 * layerAt;
        Complex az = sol[p] * Ep(layerAt, z) + sol[p + 1] * Em(layerAt, z);
        Complex dzAz = -j * kz[layerAt] * sol[p] * Ep(layerAt, z)
                       + j * kz[layerAt] * sol[p + 1] * Em(layerAt, z);
        return (az, dzAz, eps[layerAt], kz[layerAt]);
    }

    public static IEnumerable<object[]> OracleSamples()
    {
        foreach (var (z, zPrime) in new[]
                 {
                     (0.25e-3, 0.55e-3), (0.55e-3, 0.25e-3), (0.4e-3, 1.2e-3),
                     (1.35e-3, 0.6e-3), (0.95e-3, 1.05e-3)
                 })
            foreach (double kRhoOverK0 in new[] { 0.5, 0.95, 1.6, 4.0 })
                yield return new object[] { z, zPrime, kRhoOverK0 };
    }

    [Theory]
    [MemberData(nameof(OracleSamples))]
    public void OnAMultiMaterialStack_TheAssembledFieldMatchesTheOracle(
        double z, double zPrime, double kRhoOverK0)
    {
        // The gate the plan asks for: compare the ASSEMBLED, gauge-independent FIELD rather than
        // the formulation-C split, because the split is a choice and the field is not. The
        // production side builds
        //     Ẽ_x/(−jk_x) = −jωG̃_A^xz − ∂_z′K̃_Φ/(jω),   Ẽ_z = −jωG̃_A^zz − ∂_z∂_z′K̃_Φ/(jω)
        // and the oracle builds the same two components from its own Ã_z alone,
        //     Ẽ_x/(−jk_x) = ∂_zÃ_z/(jωµ₀ε₀ε),   Ẽ_z = −jωÃ_z − k_z²Ã_z/(jωµ₀ε₀ε),
        // having never heard of K̃_Φ. The two source-height derivatives come from CENTRAL
        // DIFFERENCES of the shipped charge kernel, which is what sets the band: 2e-5 is the
        // difference quotient's own accuracy at this step (measured in TwoHeightKernelTests), not
        // a tolerance on the kernels — the sharp 1e-12 claim is carried by the N = 1 identity.
        var stack = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
            new LayeredStackup.Layer(3.0, 0.005, 0.3e-3),
            new LayeredStackup.Layer(2.2, 0.0009, 0.5e-3)
        });
        double k0 = K0;
        double omega = k0 * 299_792_458.0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var j = Complex.ImaginaryOne;
        double h = 2e-7;

        var got = TransmissionLineGreens.EvaluateVertical(stack, k0, kRho, kz0, z, zPrime);

        Complex KPhiAt(double source, double observe)
        {
            var (split, m) = stack.SplitAt(source);
            return TransmissionLineGreens.EvaluateField(split, k0, kRho, kz0, m, observe).Phi;
        }
        Complex DzKPhiAt(double source, double observe)
        {
            var (split, m) = stack.SplitAt(source);
            return TransmissionLineGreens.EvaluateField(split, k0, kRho, kz0, m, observe).DzPhi;
        }
        var dzPrimeKPhi = (KPhiAt(zPrime + h, z) - KPhiAt(zPrime - h, z)) / (2 * h);
        var mixedKPhi = (DzKPhiAt(zPrime + h, z) - DzKPhiAt(zPrime - h, z)) / (2 * h);

        var exFromKernels = -j * omega * got.GAxz - dzPrimeKPhi / (j * omega);
        var ezFromKernels = -j * omega * got.GAzz - mixedKPhi / (j * omega);

        var (az, dzAz, epsAt, kzAt) = OracleVerticalAz(stack, k0, kRho, z, zPrime);
        Complex norm = j * omega * RfConstants.Mu0 * RfConstants.Eps0 * epsAt;
        var exOracle = dzAz / norm;
        var ezOracle = -j * omega * az - kzAt * kzAt * az / norm;

        string at = $"z = {z * 1e3:g4} mm, z' = {zPrime * 1e3:g4} mm, k_rho/k0 = {kRhoOverK0}";
        Assert.True(Rel(exOracle, exFromKernels) < 2e-5,
            $"E_x at {at}: oracle {exOracle}, kernels {exFromKernels} (rel {Rel(exOracle, exFromKernels):g3})");
        Assert.True(Rel(ezOracle, ezFromKernels) < 2e-5,
            $"E_z at {at}: oracle {ezOracle}, kernels {ezFromKernels} (rel {Rel(ezOracle, ezFromKernels):g3})");
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    public void AtANodeOfTheStandingWave_TheKernelsStayFinite(double kRhoOverK0)
    {
        // The reason the mixed partial is grouped as d1g x (d2g/g) rather than d1g*d2g/g. On a
        // thick enough slab the ground-pinned solution u(z) ~ sin(k_z1 z) has an interior NODE,
        // and there g and d2g vanish together: the naive quotient is 0/0 and loses every digit,
        // while the logarithmic derivative it is really made of stays finite. The Balanis slab is
        // far too thin to have one (k_z1 d ~ 0.5 rad), so this fixture is deliberately thick
        // enough that a node lands inside it, and the single-slab closed form is the reference.
        var thick = new SubstrateStackup(2.2, 0.0009, 15e-3);
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var kz1 = Complex.Sqrt(2.2 * k0 * k0 - kRho * kRho);
        double node = (Math.PI / kz1).Real;                    // sin(k_z1 z) = 0 here
        Assert.InRange(node, 1e-3, thick.ThicknessMeters - 1e-3);

        double zPrime = thick.ThicknessMeters * 0.9;
        var expected = VerticalSpectralKernels.Evaluate(thick, k0, kRho, kz0, node, zPrime);
        var got = TransmissionLineGreens.EvaluateVertical(
            LayeredStackup.FromSubstrate(thick), k0, kRho, kz0, node, zPrime);

        Assert.False(double.IsNaN(got.GAzz.Real) || double.IsInfinity(got.GAzz.Real),
            $"G_A^zz went non-finite at the node: {got.GAzz}");
        Assert.True(Rel(expected.KPhi, got.KPhi) < 1e-11,
            $"K_Phi at the node (k_rho/k0 = {kRhoOverK0}): expected {expected.KPhi}, got {got.KPhi}");
        Assert.True(Rel(expected.GAzz, got.GAzz) < 1e-9,
            $"G_A^zz at the node (k_rho/k0 = {kRhoOverK0}): expected {expected.GAzz}, got {got.GAzz}");
    }
}
