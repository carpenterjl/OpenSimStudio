using System.Numerics;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage F extraction gates (F-extraction, part 1): the multi-layer quasi-static image
/// series <see cref="MultiLayerImages"/> that the Sommerfeld integrator subtracts. It must
/// (a) reproduce the single-slab (c₀, c₁) at N = 1, (b) collapse to primary + PEC ground
/// image when every layer is air, (c) have a summed spectral asymptote that is split-slab
/// invariant, and (d) — the physics essence — MATCH the TLGF kernel's k_ρ → ∞ limit, with
/// the mismatch decaying algebraically (∝ 1/k_ρ²) so the remainder is integrable.
/// </summary>
public class MultiLayerExtractionTests
{
    private static double K0 => 2 * Math.PI * 10e9 / 299_792_458.0;
    private static readonly double Mu0 = 4e-7 * Math.PI;
    private static readonly double Eps0 = 1.0 / (Mu0 * 299_792_458.0 * 299_792_458.0);

    private static Complex Kz(Complex kSq, Complex kRho)
    {
        var s = Complex.Sqrt(kSq - kRho * kRho);
        return s.Imaginary > 0 ? -s : s;
    }

    /// <summary>Σ Coeff·e^{−jk_z0 D}/(jk_z0) — the spectral form the integrator subtracts
    /// (the caller applies the µ₀ or 1/ε₀ scale, as the production integrand does).</summary>
    private static Complex Asymptote(IReadOnlyList<MultiLayerImages.Image> images, Complex kz0)
    {
        var jKz0 = Complex.ImaginaryOne * kz0;
        Complex sum = Complex.Zero;
        foreach (var img in images)
            sum += img.Coeff * Complex.Exp(-jKz0 * img.Depth) / jKz0;
        return sum;
    }

    [Fact]
    public void SingleSlab_PhiImages_ReproduceTheClosedFormCoefficients()
    {
        var substrate = new SubstrateStackup(2.2, 0.02, 1.588e-3);
        var epsC = SpectralKernels.ComplexPermittivity(substrate);
        var (c0, c1) = SommerfeldIntegrator.PhiImageCoefficients(epsC);
        var images = MultiLayerImages.PhiImages(LayeredStackup.FromSubstrate(substrate));

        Assert.Equal(0, images[0].Depth, 12);
        Assert.True((images[0].Coeff - c0).Magnitude < 1e-12 * c0.Magnitude,
            $"c₀ {images[0].Coeff} vs closed {c0}");
        Assert.Equal(2 * substrate.ThicknessMeters, images[1].Depth, 12);
        Assert.True((images[1].Coeff - c1).Magnitude < 1e-11 * c1.Magnitude,
            $"c₁ {images[1].Coeff} vs closed {c1}");
        // Deeper images (4d, 6d, …) are the tanh expansion's higher terms — present but
        // geometrically smaller; the single-slab code leaves them in the remainder.
        Assert.All(images.Skip(2), img => Assert.True(img.Coeff.Magnitude < c1.Magnitude));
    }

    [Fact]
    public void AllAir_PhiImages_ArePrimaryPlusOneNegativeGroundImage()
    {
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(1, 0, 0.9e-3),
            new LayeredStackup.Layer(1, 0, 0.6e-3),
            new LayeredStackup.Layer(1, 0, 1.0e-3),
        });
        var images = MultiLayerImages.PhiImages(stackup);
        Assert.Equal(2, images.Count);
        Assert.Equal(0, images[0].Depth, 12);
        Assert.True((images[0].Coeff - Complex.One).Magnitude < 1e-12);
        Assert.Equal(2 * stackup.TotalThicknessMeters, images[1].Depth, 12);
        Assert.True((images[1].Coeff + Complex.One).Magnitude < 1e-12);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(3.0)]
    [InlineData(30.0)]
    public void SplitSlab_SummedAsymptoteIsInvariant(double kRhoOverK0)
    {
        var substrate = new SubstrateStackup(6.0, 0.01, 1.2e-3);
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var one = Asymptote(MultiLayerImages.PhiImages(LayeredStackup.FromSubstrate(substrate)), kz0);
        foreach (int parts in new[] { 2, 3, 5 })
        {
            var split = new LayeredStackup(Enumerable.Range(0, parts)
                .Select(_ => new LayeredStackup.Layer(6.0, 0.01, substrate.ThicknessMeters / parts))
                .ToArray());
            var got = Asymptote(MultiLayerImages.PhiImages(split), kz0);
            Assert.True((got - one).Magnitude < 1e-12 * one.Magnitude,
                $"split-{parts} asymptote {got} vs one-slab {one}");
        }
    }

    private static Complex FreeSpaceG(double k0, double r)
    {
        var (s, c) = Math.SinCos(k0 * r);
        return new Complex(c, -s) / (4 * Math.PI * r);
    }

    /// <summary>The full spatial kernels of a multi-layer stackup: closed-form images
    /// + the Sommerfeld remainder + closed-form surface-wave pole terms — the same
    /// composition the (future) multi-layer table performs, assembled here for the gates.</summary>
    private static (Complex GA, Complex KPhi) FullKernel(LayeredStackup stackup, double k0,
        double rho, int refinement)
    {
        var poles = SurfaceWavePoles.Find(stackup, k0);
        var gaImages = MultiLayerImages.GaImages(stackup);
        var phiImages = MultiLayerImages.PhiImages(stackup);
        var (remA, remPhi) = SommerfeldIntegrator.RemainderMultiLayer(
            stackup, k0, poles, gaImages, phiImages, rho, refinement);

        Complex imageA = Complex.Zero, imagePhi = Complex.Zero;
        foreach (var img in gaImages)
            imageA += Mu0 * img.Coeff * FreeSpaceG(k0, Math.Sqrt(rho * rho + img.Depth * img.Depth));
        foreach (var img in phiImages)
            imagePhi += img.Coeff * FreeSpaceG(k0, Math.Sqrt(rho * rho + img.Depth * img.Depth)) / Eps0;

        Complex poleA = Complex.Zero, polePhi = Complex.Zero;
        foreach (var pole in poles)
        {
            var factor = new Complex(0, -0.25) * pole.KRho * Bessel.H02(pole.KRho * rho);
            if (pole.ResidueA != Complex.Zero) poleA += pole.ResidueA * factor;
            polePhi += pole.ResiduePhi * factor;
        }
        return (imageA + remA + poleA, imagePhi + remPhi + polePhi);
    }

    [Theory]
    [InlineData(0.5e-3)]
    [InlineData(2.0e-3)]
    [InlineData(8.0e-3)]
    public void FullKernel_AtNEqualsOne_MatchesThePinnedSingleSlabSpatialKernel(double rho)
    {
        // The extraction split (how many images vs remainder) differs from the single-slab
        // table, but the SUM is the same physical kernel — so this pins the whole multi-layer
        // composition against the trusted single-slab spatial kernel to integration accuracy.
        var substrate = new SubstrateStackup(2.2, 0.001, 1.588e-3);
        double k0 = K0;
        var table = new LayeredKernelTable(substrate, 10e9, 20e-3);
        var (gaRef, phiRef) = table.EvaluateKernelsDirect(rho, refinement: 3);
        var (gA, kPhi) = FullKernel(LayeredStackup.FromSubstrate(substrate), k0, rho, refinement: 3);
        Assert.True((gA - gaRef).Magnitude < 1e-7 * gaRef.Magnitude, $"G_A {gA} vs single-slab {gaRef}");
        Assert.True((kPhi - phiRef).Magnitude < 1e-7 * phiRef.Magnitude, $"K_Φ {kPhi} vs single-slab {phiRef}");
    }

    [Theory]
    [InlineData(0.4e-3)]
    [InlineData(3.0e-3)]
    public void FullKernel_MultiLayer_SelfConverges(double rho)
    {
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
            new LayeredStackup.Layer(3.0, 0.005, 0.3e-3),
            new LayeredStackup.Layer(10.2, 0.0023, 0.5e-3),
        });
        double k0 = K0;
        var (gA1, phi1) = FullKernel(stackup, k0, rho, refinement: 1);
        var (gA2, phi2) = FullKernel(stackup, k0, rho, refinement: 2);
        Assert.True((gA2 - gA1).Magnitude < 1e-8 * gA2.Magnitude, $"G_A refine 1→2 moved {(gA2 - gA1).Magnitude / gA2.Magnitude:e3}");
        Assert.True((phi2 - phi1).Magnitude < 1e-8 * phi2.Magnitude, $"K_Φ refine 1→2 moved {(phi2 - phi1).Magnitude / phi2.Magnitude:e3}");
    }

    [Theory]
    [InlineData(0.5e-3)]
    [InlineData(4.0e-3)]
    public void FullKernel_SplitSlabInvariant(double rho)
    {
        var substrate = new SubstrateStackup(6.0, 0.01, 1.2e-3);
        double k0 = K0;
        var one = FullKernel(LayeredStackup.FromSubstrate(substrate), k0, rho, refinement: 3);
        var split = new LayeredStackup(Enumerable.Range(0, 3)
            .Select(_ => new LayeredStackup.Layer(6.0, 0.01, substrate.ThicknessMeters / 3))
            .ToArray());
        var got = FullKernel(split, k0, rho, refinement: 3);
        Assert.True((got.GA - one.GA).Magnitude < 1e-7 * one.GA.Magnitude, $"split G_A {got.GA} vs {one.GA}");
        Assert.True((got.KPhi - one.KPhi).Magnitude < 1e-7 * one.KPhi.Magnitude, $"split K_Φ {got.KPhi} vs {one.KPhi}");
    }

    [Theory]
    [InlineData(0.3e-3)]
    [InlineData(2.0e-3)]
    [InlineData(9.0e-3)]
    public void Table_SplineMatchesDirectIntegration(double rho)
    {
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
            new LayeredStackup.Layer(2.2, 0.0009, 0.5e-3),
        });
        var table = new MultiLayerKernelTable(stackup, 10e9, 15e-3);
        var (gaSpline, phiSpline) = table.EvaluateKernels(rho);
        var (gaDirect, phiDirect) = table.EvaluateKernelsDirect(rho, refinement: 3);
        Assert.True((gaSpline - gaDirect).Magnitude < 1e-5 * gaDirect.Magnitude,
            $"G_A spline {gaSpline} vs direct {gaDirect}");
        Assert.True((phiSpline - phiDirect).Magnitude < 1e-5 * phiDirect.Magnitude,
            $"K_Φ spline {phiSpline} vs direct {phiDirect}");
    }

    [Theory]
    [InlineData(0.4e-3)]
    [InlineData(3.0e-3)]
    public void Table_AtNEqualsOne_MatchesTheSingleSlabTable(double rho)
    {
        var substrate = new SubstrateStackup(2.2, 0.001, 1.588e-3);
        var single = new LayeredKernelTable(substrate, 10e9, 15e-3);
        var multi = new MultiLayerKernelTable(LayeredStackup.FromSubstrate(substrate), 10e9, 15e-3);
        var (gaRef, phiRef) = single.EvaluateKernels(rho);
        var (gA, kPhi) = multi.EvaluateKernels(rho);
        // Spline-vs-spline over different image splits — table accuracy, not the sharp
        // direct-integration gate above; the two-decimal spline error dominates.
        Assert.True((gA - gaRef).Magnitude < 1e-4 * gaRef.Magnitude, $"G_A {gA} vs single-slab table {gaRef}");
        Assert.True((kPhi - phiRef).Magnitude < 1e-4 * phiRef.Magnitude, $"K_Φ {kPhi} vs single-slab table {phiRef}");
    }

    [Fact]
    public void ExtractedAsymptote_MatchesTheKernelLimit_DecayingAsOneOverKRhoSquared()
    {
        // A genuinely layered stack — the asymptote must track the true TLGF kernel.
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
            new LayeredStackup.Layer(3.0, 0.005, 0.3e-3),
            new LayeredStackup.Layer(10.2, 0.0023, 0.5e-3),
        });
        double k0 = K0;
        var phiImages = MultiLayerImages.PhiImages(stackup);
        var gaImages = MultiLayerImages.GaImages(stackup);

        double Rel(double kRhoOverK0, bool phi)
        {
            var kRho = new Complex(kRhoOverK0 * k0, 0);
            var kz0 = Kz(k0 * k0, kRho);
            var (gA, kPhi) = TransmissionLineGreens.Evaluate(stackup, k0, kRho, kz0);
            if (phi)
            {
                var asym = Asymptote(phiImages, kz0) / Eps0;
                return (kPhi - asym).Magnitude / kPhi.Magnitude;
            }
            var asymA = Mu0 * Asymptote(gaImages, kz0);
            return (gA - asymA).Magnitude / gA.Magnitude;
        }

        // Deep in the evanescent tail the asymptote IS the kernel to leading order; the
        // mismatch is the O(k₀²/k_ρ²) dielectric correction, so it drops ~4× per k_ρ
        // doubling. (G̃_A's TE image is ε-independent, so it converges the same way.)
        foreach (bool phi in new[] { true, false })
        {
            double e1 = Rel(60, phi), e2 = Rel(120, phi), e3 = Rel(240, phi);
            Assert.True(e1 < 5e-3, $"{(phi ? "K_Φ" : "G_A")} mismatch at 60·k₀ = {e1:e3} too large");
            Assert.True(e2 < 0.4 * e1, $"{(phi ? "K_Φ" : "G_A")} not decaying: {e1:e3} → {e2:e3}");
            Assert.True(e3 < 0.4 * e2, $"{(phi ? "K_Φ" : "G_A")} not decaying: {e2:e3} → {e3:e3}");
        }
    }
}
