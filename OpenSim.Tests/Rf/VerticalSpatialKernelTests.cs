using System.Numerics;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage E checkpoint E2a: the SPATIAL vertical-current kernels (direct Sommerfeld
/// with per-(z,z′) image + pole extraction) gated before any basis consumes them.
/// The strongest gate is the boundary cross-composition: K_Φ(ρ; d, d) assembled
/// through the vertical machinery (five-image truncated ladder + its own remainder)
/// must equal the Stage C boundary kernel (c₀/c₁ dynamic images + its remainder) —
/// two DIFFERENT extraction bookkeepings meeting on the same physics.
/// </summary>
public class VerticalSpatialKernelTests
{
    private static readonly SubstrateStackup Balanis = new(2.2, 0.0, 1.588e-3);
    private const double F = 10e9;
    private const double C0 = 299_792_458.0;
    private static readonly double Mu0 = 4e-7 * Math.PI;
    private static readonly double Eps0 = 1.0 / (Mu0 * C0 * C0);

    private static void AssertRel(Complex expected, Complex actual, double tol, string what)
    {
        double scale = Math.Max(expected.Magnitude, actual.Magnitude);
        if (scale == 0) { Assert.Equal(expected, actual); return; }
        double rel = (expected - actual).Magnitude / scale;
        Assert.True(rel <= tol, $"{what}: expected {expected}, got {actual} (rel {rel:e2})");
    }

    [Fact]
    public void EpsilonOne_SpatialKernels_AreExactlyPrimaryPlusImage()
    {
        // εr = 1: the spectral integrand of the remainder cancels IDENTICALLY against
        // the extraction (k_z1 ≡ k_z0, η = 0), so the assembled kernels must equal the
        // closed two-term forms to integration roundoff.
        var air = new SubstrateStackup(1.0, 0.0, Balanis.ThicknessMeters);
        var set = new VerticalKernelSet(air, F);
        double d = air.ThicknessMeters, k0 = set.K0;

        foreach (double rho in new[] { 0.3e-3, 2e-3, 10e-3 })
            foreach (var (zf, zpf) in new[] { (0.3, 0.7), (0.95, 0.95), (0.0, 0.5), (1.0, 1.0) })
            {
                double z = zf * d, zPrime = zpf * d;
                var (gzz, gxz, kPhi) = set.Evaluate(rho, z, zPrime);
                double r1 = Math.Sqrt(rho * rho + (z - zPrime) * (z - zPrime));
                double r2 = Math.Sqrt(rho * rho + (z + zPrime) * (z + zPrime));
                var g1 = G(k0, r1);
                var g2 = G(k0, r2);
                AssertRel(Mu0 * (g1 + g2), gzz, 1e-10, $"G_zz(ρ={rho:g2}, z={zf}d, z′={zpf}d)");
                AssertRel((g1 - g2) / Eps0, kPhi, 1e-10, $"K_Φ(ρ={rho:g2}, z={zf}d, z′={zpf}d)");
                Assert.True(gxz.Magnitude <= 1e-12 * (Mu0 * g1.Magnitude),
                    $"G_xz should vanish at εr = 1, got {gxz}");
            }

        static Complex G(double k0, double r)
        {
            var (sin, cos) = Math.SinCos(k0 * r);
            return new Complex(cos, -sin) / (4 * Math.PI * r);
        }
    }

    [Fact]
    public void BoundaryLimit_MatchesTheStageCKernel_CrossComposition()
    {
        // K_Φ(ρ; d, d) through the vertical bookkeeping ≡ the Stage C boundary kernel
        // through its own (different images, different remainder — same physics).
        var table = new LayeredKernelTable(Balanis, F, rhoMax: 0.05);
        var set = new VerticalKernelSet(Balanis, F);
        double d = Balanis.ThicknessMeters;

        foreach (double rho in new[] { 0.3e-3, 1e-3, 3e-3, 10e-3, 30e-3 })
        {
            var (_, _, kPhi) = set.Evaluate(rho, d, d, refinement: 2);
            var (_, expected) = table.EvaluateKernelsDirect(rho, refinement: 2);
            AssertRel(expected, kPhi, 1e-8, $"K_Φ(ρ={rho:g2}; d, d) vs Stage C");
        }
    }

    [Fact]
    public void SelfConvergence_DoublingEveryKnob_MovesNothing()
    {
        // The C1-style convergence gate, on a LOSSY slab (the pole path exercises the
        // complex-kp Newton branch) and at junction-adjacent heights where the
        // critical 2d−z−z′ image height is smallest.
        var lossy = new SubstrateStackup(2.2, 0.02, Balanis.ThicknessMeters);
        var set = new VerticalKernelSet(lossy, F);
        double d = lossy.ThicknessMeters;

        foreach (double rho in new[] { 0.3e-3, 5e-3 })
            foreach (var (zf, zpf) in new[] { (0.95, 0.95), (0.3, 0.8), (1.0, 0.5) })
            {
                var coarse = set.Evaluate(rho, zf * d, zpf * d, refinement: 1);
                var fine = set.Evaluate(rho, zf * d, zpf * d, refinement: 3);
                AssertRel(fine.GAzz, coarse.GAzz, 1e-8, $"G_zz self-convergence (ρ={rho:g2}, z={zf}d, z′={zpf}d)");
                AssertRel(fine.GAxz, coarse.GAxz, 1e-7, $"G_xz self-convergence (ρ={rho:g2}, z={zf}d, z′={zpf}d)");
                AssertRel(fine.KPhi, coarse.KPhi, 1e-8, $"K_Φ self-convergence (ρ={rho:g2}, z={zf}d, z′={zpf}d)");
            }
    }

    [Fact]
    public void SpatialReciprocity_HoldsForTheSymmetricKernels()
    {
        var set = new VerticalKernelSet(Balanis, F);
        double d = Balanis.ThicknessMeters;
        foreach (double rho in new[] { 0.5e-3, 4e-3 })
        {
            var forward = set.Evaluate(rho, 0.3 * d, 0.8 * d);
            var swapped = set.Evaluate(rho, 0.8 * d, 0.3 * d);
            AssertRel(forward.GAzz, swapped.GAzz, 1e-12, "G_zz(z,z′) = G_zz(z′,z)");
            AssertRel(forward.KPhi, swapped.KPhi, 1e-12, "K_Φ(z,z′) = K_Φ(z′,z)");
        }
    }
}
