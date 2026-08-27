using System.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage C1 (spatial) — the multi-layer VERTICAL-current spatial kernels
/// (<see cref="MultiLayerVerticalKernelSet"/>): images, Sommerfeld remainder and per-(z, z′)
/// pole residues composed into G_A^zz, G_A^xz and K_Φ at one (ρ, z, z′).
///
/// <para>The headline is the N = 1 identity against the shipped single-slab
/// <see cref="VerticalKernelSet"/> — two independent decompositions of the same physics (five
/// images and a closed-form kernel on one side, two images and a TLGF assembly on the other), so
/// agreement pins the image coefficients, the residues and the contour all at once.</para>
///
/// <para>Its band is 5e-9 and the reason is MEASURED rather than assumed: the vertical pole
/// residues inherit the Stage F null-vector residue's own accuracy, whose finite-difference matrix
/// derivative is documented at a 1e-8 target. <see
/// cref="TheResidueFloorIsTheShippedNullVectorMachinerys"/> pins that attribution directly — the
/// shipped HORIZONTAL residue misses the single-slab analytic one by the same 4.5e-10, so
/// sharpening that machinery would tighten both together and a regression here would show up as
/// the two diverging.</para>
/// </summary>
public class MultiLayerVerticalSpatialTests
{
    private const double FrequencyHz = 10e9;
    private static double K0 => 2 * Math.PI * FrequencyHz / 299_792_458.0;
    private static readonly SubstrateStackup Balanis = new(2.2, 0.0009, 1.588e-3);

    /// <summary>A genuine three-material stack — nothing about it is a split of one slab.</summary>
    private static LayeredStackup ThreeLayer() => new(new[]
    {
        new LayeredStackup.Layer(4.4, 0.0, 0.8e-3),
        new LayeredStackup.Layer(2.2, 0.0, 0.5e-3),
        new LayeredStackup.Layer(9.8, 0.0, 0.3e-3),
    });

    private static double Rel(Complex a, Complex b) =>
        (a - b).Magnitude / Math.Max(Math.Max(a.Magnitude, b.Magnitude), 1e-300);

    private static Complex Kz(Complex kSq, Complex kRho)
    {
        var k = Complex.Sqrt(kSq - kRho * kRho);
        return k.Imaginary > 0 ? -k : k;
    }

    public static IEnumerable<object[]> Geometries()
    {
        foreach (double rhoOverD in new[] { 0.05, 0.5, 3.0 })
            foreach (double zOverD in new[] { 0.05, 0.5, 0.95 })
                foreach (double zpOverD in new[] { 0.2, 0.95 })
                    yield return new object[] { rhoOverD, zOverD, zpOverD };
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void AtOneLayer_TheyAreTheSingleSlabSpatialKernels(
        double rhoOverD, double zOverD, double zpOverD)
    {
        double d = Balanis.ThicknessMeters;
        double rho = rhoOverD * d, z = zOverD * d, zPrime = zpOverD * d;
        var single = new VerticalKernelSet(Balanis, FrequencyHz);
        var multi = new MultiLayerVerticalKernelSet(LayeredStackup.FromSubstrate(Balanis), FrequencyHz);

        var expected = single.Evaluate(rho, z, zPrime);
        var got = multi.Evaluate(rho, z, zPrime);

        string at = $"ρ/d = {rhoOverD}, z/d = {zOverD}, z'/d = {zpOverD}";
        Assert.True(Rel(expected.GAzz, got.GAzz) < 5e-9,
            $"G_A^zz at {at}: {expected.GAzz} vs {got.GAzz} (rel {Rel(expected.GAzz, got.GAzz):g3})");
        Assert.True(Rel(expected.GAxz, got.GAxz) < 5e-9,
            $"G_A^xz at {at}: {expected.GAxz} vs {got.GAxz} (rel {Rel(expected.GAxz, got.GAxz):g3})");
        Assert.True(Rel(expected.KPhi, got.KPhi) < 5e-9,
            $"K_Φ at {at}: {expected.KPhi} vs {got.KPhi} (rel {Rel(expected.KPhi, got.KPhi):g3})");
    }

    /// <summary>The N = 1 identity's band is set by the residues, and the residues' error is the
    /// SHIPPED machinery's — not this assembly's. Measured through two independent routes: the new
    /// vertical residues and the shipped horizontal ones miss their analytic single-slab
    /// counterparts by the same 4.5e-10, a COMMON SCALAR (identical across every (z, z′) and
    /// across all three kernels), which is the signature of the 1/(vᵀM′u) normalization built on a
    /// finite-difference matrix derivative rather than of anything per-kernel.</summary>
    [Fact]
    public void TheResidueFloorIsTheShippedNullVectorMachinerys()
    {
        double k0 = K0, d = Balanis.ThicknessMeters;
        var stack = LayeredStackup.FromSubstrate(Balanis);
        var poles = SurfaceWavePoles.Find(Balanis, k0);
        Assert.NotEmpty(poles);

        double worstVertical = 0, worstHorizontal = 0;
        foreach (var pole in poles)
        {
            var horizontal = TransmissionLineGreens.PoleResidues(stack, k0, pole.KRho, pole.IsTm);
            worstHorizontal = Math.Max(worstHorizontal, Rel(pole.ResiduePhi, horizontal.ResiduePhi));
            foreach (double zOverD in new[] { 0.05, 0.5, 0.95 })
                foreach (double zpOverD in new[] { 0.2, 0.95 })
                {
                    double z = zOverD * d, zPrime = zpOverD * d;
                    var expected = VerticalSpatialKernels.PoleResidues(Balanis, k0, pole.KRho, z, zPrime);
                    var got = TransmissionLineGreens.PoleVerticalResidues(
                        stack, k0, pole.KRho, pole.IsTm, z, zPrime);
                    worstVertical = Math.Max(worstVertical, Rel(expected.GAzz, got.GAzz));
                    worstVertical = Math.Max(worstVertical, Rel(expected.GAxz, got.GAxz));
                    worstVertical = Math.Max(worstVertical, Rel(expected.KPhi, got.KPhi));
                }
        }

        Assert.True(worstVertical < 5e-9,
            $"vertical residues vs the single-slab analytic ones: {worstVertical:g3}");
        // The attribution, not a second tolerance: the vertical assembly must not be materially
        // worse than the horizontal read-out that shares the same residue machinery. A genuine
        // assembly error would be orders larger, and a sharpened MatrixDerivative tightens both.
        Assert.True(worstVertical < 20 * worstHorizontal + 1e-13,
            $"the vertical residues ({worstVertical:g3}) should inherit the shipped horizontal "
            + $"residue floor ({worstHorizontal:g3}), not exceed it — this is the attribution pin.");
    }

    /// <summary>At N = 1 the two-image set IS images 0 and 1 of the shipped five-image single-slab
    /// set — the plan's claim, checked as an exact identity (the deeper three are the top-interface
    /// reflection family the remainder carries here).</summary>
    [Theory]
    [InlineData(0.05, 0.2)]
    [InlineData(0.5, 0.95)]
    [InlineData(0.95, 0.95)]
    public void AtOneLayer_TheImagesArePrimaryAndGroundImageOfTheSingleSlabSet(double zOverD, double zpOverD)
    {
        double d = Balanis.ThicknessMeters;
        double z = zOverD * d, zPrime = zpOverD * d;
        var expected = VerticalSpatialKernels.Images(Balanis, z, zPrime);
        var got = MultiLayerVerticalImages.Images(LayeredStackup.FromSubstrate(Balanis), z, zPrime);

        Assert.Equal(2, got.Length);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(expected[i].Height, got[i].Height, 15);
            Assert.True(Rel(expected[i].CoefficientGAzz, got[i].CoefficientGAzz) < 1e-15,
                $"image {i} G_A^zz coefficient: {expected[i].CoefficientGAzz} vs {got[i].CoefficientGAzz}");
            Assert.True(Rel(expected[i].CoefficientKPhi, got[i].CoefficientKPhi) < 1e-15,
                $"image {i} K_Φ coefficient: {expected[i].CoefficientKPhi} vs {got[i].CoefficientKPhi}");
        }
    }

    /// <summary>The image coefficients are the KERNEL'S OWN k_ρ → ∞ asymptote, which is how they
    /// were chosen in the first place. Extracting the leading exponential from the assembled
    /// spectral kernels must return exactly what <see cref="MultiLayerVerticalImages"/> encodes:
    /// 1 for G̃_A^zz, ε-independently, and 2/(ε(z) + ε(z′)) for K̃_Φ. Gated where the rule is
    /// claimed EXACT — the same layer, and straddling one interface, which is the only regime
    /// |z − z′| → 0 can reach.</summary>
    [Theory]
    [InlineData(0.4e-3, 0.6e-3)]     // both in layer 0
    [InlineData(0.9e-3, 1.1e-3)]     // both in layer 1
    [InlineData(1.4e-3, 1.5e-3)]     // both in layer 2
    [InlineData(0.7e-3, 0.9e-3)]     // straddling the first interface
    [InlineData(0.9e-3, 0.7e-3)]     // ... and the other way round
    [InlineData(1.2e-3, 1.4e-3)]     // straddling the second interface
    public void TheImageCoefficientsAreTheKernelsOwnLargeKRhoAsymptote(double z, double zPrime)
    {
        var stack = ThreeLayer();
        double k0 = K0, h = Math.Abs(z - zPrime);
        var image = MultiLayerVerticalImages.Images(stack, z, zPrime)[0];
        Assert.Equal(h, image.Height, 15);

        // k_ρ h = 10 already puts the ground image and every reflection at e^{-20} or smaller, so
        // what is left is the primary alone. It is read at TWO depths, which is what makes this a
        // converged ASYMPTOTE rather than a coincidence at one point — and the window has a far end
        // as well as a near one: past k_ρ h ≈ 16 the kernel is e^{-16} of its own scale and
        // G̃_A^zz, a cancelling bracket, has spent its relative digits (measured: this same pair
        // reads 1.00016 at 14 and 1.0236 at 20). That is the spectral-level cancellation the N = 1
        // spectral gate already documents, not a defect in the coefficient.
        (Complex Zz, Complex Phi) Extract(double kRhoH)
        {
            var kRho = new Complex(kRhoH / h, 0);
            var kz0 = Kz(k0 * k0, kRho);
            var (gzz, _, kPhi) = TransmissionLineGreens.EvaluateVertical(stack, k0, kRho, kz0, z, zPrime);
            var jkz0 = Complex.ImaginaryOne * kz0;
            var grow = Complex.Exp(jkz0 * h);
            return (gzz * jkz0 / RfConstants.Mu0 * grow, kPhi * jkz0 * RfConstants.Eps0 * grow);
        }

        foreach (double kRhoH in new[] { 10.0, 14.0 })
        {
            var (zz, phi) = Extract(kRhoH);
            Assert.True(Rel(zz, image.CoefficientGAzz) < 2e-3,
                $"G̃_A^zz asymptote at z = {z}, z' = {zPrime}, k_ρh = {kRhoH}: "
                + $"measured {zz}, encoded {image.CoefficientGAzz}");
            Assert.True(Rel(phi, image.CoefficientKPhi) < 2e-3,
                $"K̃_Φ asymptote at z = {z}, z' = {zPrime}, k_ρh = {kRhoH}: "
                + $"measured {phi}, encoded {image.CoefficientKPhi}");
        }

    }

    /// <summary>G̃_A^xz gets NO image, and this is the measurement that says it needs none: its
    /// weight RELATIVE to G̃_A^zz at the same spectral point falls like 1/k_ρ, so the kernel decays
    /// a full order faster than the two that are extracted and the tail machinery carries it (the
    /// W̃ precedent). Doubling k_ρ must halve the ratio. Only pairs with a genuine ε contrast
    /// between them are gated — with none, this kernel sits at roundoff and the ratio is 0/0.</summary>
    [Theory]
    [InlineData(0.7e-3, 0.9e-3)]
    [InlineData(0.9e-3, 0.7e-3)]
    [InlineData(1.2e-3, 1.4e-3)]
    [InlineData(0.7e-3, 1.4e-3)]
    public void TheXzKernelNeedsNoImage_ItDecaysAFullOrderFaster(double z, double zPrime)
    {
        var stack = ThreeLayer();
        double k0 = K0, h = Math.Abs(z - zPrime);

        double Weight(double kRhoH)
        {
            var kRho = new Complex(kRhoH / h, 0);
            var kz0 = Kz(k0 * k0, kRho);
            var (gzz, gxz, _) = TransmissionLineGreens.EvaluateVertical(stack, k0, kRho, kz0, z, zPrime);
            return gxz.Magnitude / gzz.Magnitude;
        }

        double near = Weight(10.0), far = Weight(20.0);
        Assert.True(near > 1e-9, $"the xz kernel is at roundoff here ({near:g3}) — nothing to measure");
        Assert.True(far / near > 0.4 && far / near < 0.6,
            $"the xz/zz weight should HALVE when k_ρ doubles (1/k_ρ decay): {near:g3} -> {far:g3}, "
            + $"ratio {far / near:g3}");
    }

    /// <summary>An ALL-AIR grounded stack has no surface waves and no dielectric reflections, so
    /// the two extracted images are the WHOLE answer — primary plus a POSITIVELY imaging vertical
    /// dipole for G_A^zz, primary minus its image for K_Φ, and G_A^xz identically zero. The
    /// remainder must therefore integrate to nothing, which is the sharpest absolute check
    /// available: it shares no machinery with the single-slab set.</summary>
    [Fact]
    public void AllAir_IsExactlyThePrimaryPlusItsPecImage()
    {
        var air = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(1.0, 0.0, 0.6e-3),
            new LayeredStackup.Layer(1.0, 0.0, 0.5e-3),
            new LayeredStackup.Layer(1.0, 0.0, 0.5e-3),
        });
        var set = new MultiLayerVerticalKernelSet(air, FrequencyHz);
        Assert.Empty(set.Poles);
        double k0 = set.K0;

        Complex G(double r)
        {
            var (sin, cos) = Math.SinCos(k0 * r);
            return new Complex(cos, -sin) / (4 * Math.PI * r);
        }

        foreach (var (rho, z, zPrime) in new[]
        {
            (0.05e-3, 0.2e-3, 0.9e-3), (0.4e-3, 0.7e-3, 0.7e-3), (2.0e-3, 0.1e-3, 1.5e-3),
        })
        {
            var g1 = G(Math.Sqrt(rho * rho + (z - zPrime) * (z - zPrime)));
            var g2 = G(Math.Sqrt(rho * rho + (z + zPrime) * (z + zPrime)));
            var got = set.Evaluate(rho, z, zPrime);
            string at = $"ρ = {rho}, z = {z}, z' = {zPrime}";
            Assert.True(Rel(RfConstants.Mu0 * (g1 + g2), got.GAzz) < 1e-9,
                $"G_A^zz at {at}: {RfConstants.Mu0 * (g1 + g2)} vs {got.GAzz}");
            Assert.True(Rel((g1 - g2) / RfConstants.Eps0, got.KPhi) < 1e-9,
                $"K_Φ at {at}: {(g1 - g2) / RfConstants.Eps0} vs {got.KPhi}");
            Assert.True(got.GAxz.Magnitude < 1e-9 * got.GAzz.Magnitude,
                $"G_A^xz at {at} should vanish with no dielectric contrast: {got.GAxz}");
        }
    }

    /// <summary>G_A^zz and K_Φ are RECIPROCAL — swapping source and observation must return the
    /// same number, through two entirely separate solves (different split stackups, different
    /// source interfaces, different observation layers). G_A^xz is deliberately NOT asserted
    /// symmetric: it is the coupling kernel normalized per −jk_x·J̃_z, so its two orderings are
    /// different physical objects and measure differently.</summary>
    [Theory]
    [InlineData(0.1e-3, 0.4e-3, 0.6e-3)]
    [InlineData(0.1e-3, 0.7e-3, 1.4e-3)]
    [InlineData(0.6e-3, 0.2e-3, 1.5e-3)]
    public void TheSpatialKernelsAreReciprocal(double rho, double z, double zPrime)
    {
        var set = new MultiLayerVerticalKernelSet(ThreeLayer(), FrequencyHz);
        var forward = set.Evaluate(rho, z, zPrime);
        var swapped = set.Evaluate(rho, zPrime, z);
        Assert.True(Rel(forward.GAzz, swapped.GAzz) < 1e-8,
            $"G_A^zz reciprocity: {forward.GAzz} vs {swapped.GAzz} (rel {Rel(forward.GAzz, swapped.GAzz):g3})");
        Assert.True(Rel(forward.KPhi, swapped.KPhi) < 1e-8,
            $"K_Φ reciprocity: {forward.KPhi} vs {swapped.KPhi} (rel {Rel(forward.KPhi, swapped.KPhi):g3})");
    }

    /// <summary>Describing one layer as two must change nothing at the SPATIAL level either — the
    /// spectral gate's claim carried through the images, the residues and the contour, each of
    /// which sees a different number of layers when the stack is split.</summary>
    [Theory]
    [InlineData(0.05e-3, 0.3e-3, 1.2e-3)]
    [InlineData(0.5e-3, 0.9e-3, 1.5e-3)]
    public void SplittingALayerChangesNothing(double rho, double z, double zPrime)
    {
        var whole = LayeredStackup.FromSubstrate(Balanis);
        var (split, _) = whole.SplitAt(0.6 * Balanis.ThicknessMeters);
        Assert.Equal(2, split.Layers.Count);

        var a = new MultiLayerVerticalKernelSet(whole, FrequencyHz).Evaluate(rho, z, zPrime);
        var b = new MultiLayerVerticalKernelSet(split, FrequencyHz).Evaluate(rho, z, zPrime);
        Assert.True(Rel(a.GAzz, b.GAzz) < 1e-9, $"G_A^zz: {a.GAzz} vs {b.GAzz}");
        Assert.True(Rel(a.GAxz, b.GAxz) < 1e-9, $"G_A^xz: {a.GAxz} vs {b.GAxz}");
        Assert.True(Rel(a.KPhi, b.KPhi) < 1e-9, $"K_Φ: {a.KPhi} vs {b.KPhi}");
    }

    /// <summary>The remainder's own convergence, on a genuine three-material stack and at the
    /// geometries that stress it: a pair hugging the ground (where the extracted ground image
    /// carries the singularity), a pair straddling an interface, and a pair separated by a whole
    /// layer (where the primary coefficient is an accelerator rather than the exact asymptote, so
    /// the mismatch has to ride an exponentially damped remainder).</summary>
    [Theory]
    [InlineData(0.1e-3, 0.4e-3, 0.6e-3)]
    [InlineData(0.1e-3, 0.7e-3, 0.9e-3)]
    [InlineData(0.1e-3, 0.7e-3, 1.4e-3)]
    [InlineData(0.1e-3, 0.05e-3, 0.1e-3)]
    [InlineData(0.05e-3, 1.55e-3, 1.58e-3)]
    public void TheRemainderSelfConverges(double rho, double z, double zPrime)
    {
        var set = new MultiLayerVerticalKernelSet(ThreeLayer(), FrequencyHz);
        var coarse = set.Evaluate(rho, z, zPrime);
        var fine = set.Evaluate(rho, z, zPrime, refinement: 3);
        Assert.True(Rel(coarse.GAzz, fine.GAzz) < 1e-8,
            $"G_A^zz: {coarse.GAzz} vs {fine.GAzz} (rel {Rel(coarse.GAzz, fine.GAzz):g3})");
        Assert.True(Rel(coarse.GAxz, fine.GAxz) < 1e-8,
            $"G_A^xz: {coarse.GAxz} vs {fine.GAxz} (rel {Rel(coarse.GAxz, fine.GAxz):g3})");
        Assert.True(Rel(coarse.KPhi, fine.KPhi) < 1e-8,
            $"K_Φ: {coarse.KPhi} vs {fine.KPhi} (rel {Rel(coarse.KPhi, fine.KPhi):g3})");
    }

    /// <summary>The prepared-geometry path a Sommerfeld sweep uses must be BITWISE the per-call
    /// wrapper. Hoisting the stack splits out of the k_ρ loop is a pure re-derivation of geometry,
    /// not of arithmetic — so this is an exact identity, not a tolerance, and it is the pin that
    /// lets the integrator take the fast path without any claim about "close enough".</summary>
    [Theory]
    [InlineData(0.4e-3, 0.6e-3)]
    [InlineData(0.7e-3, 1.4e-3)]
    [InlineData(0.9e-3, 0.9e-3)]
    public void ThePreparedGeometryPathIsBitwiseTheWrapper(double z, double zPrime)
    {
        var stack = ThreeLayer();
        double k0 = K0;
        var geometry = TransmissionLineGreens.PrepareVertical(stack, z, zPrime);
        foreach (double kRhoOverK0 in new[] { 0.4, 1.3, 9.0, 400.0 })
        {
            var kRho = new Complex(kRhoOverK0 * k0, 0);
            var kz0 = Kz(k0 * k0, kRho);
            var viaWrapper = TransmissionLineGreens.EvaluateVertical(stack, k0, kRho, kz0, z, zPrime);
            var viaGeometry = TransmissionLineGreens.EvaluateVertical(geometry, k0, kRho, kz0, z, zPrime);
            Assert.Equal(viaWrapper.GAzz, viaGeometry.GAzz);
            Assert.Equal(viaWrapper.GAxz, viaGeometry.GAxz);
            Assert.Equal(viaWrapper.KPhi, viaGeometry.KPhi);
        }
    }

    [Fact]
    public void TheTypedFailuresName_TheirCause()
    {
        var set = new MultiLayerVerticalKernelSet(ThreeLayer(), FrequencyHz);
        double d = ThreeLayer().TotalThicknessMeters;

        var zero = Assert.Throws<ArgumentOutOfRangeException>(() => set.Evaluate(0, 0.4e-3, 0.6e-3));
        Assert.Contains("ρ_eff", zero.Message);

        var high = Assert.Throws<ArgumentOutOfRangeException>(() => set.Evaluate(1e-4, d + 1e-6, 0.6e-3));
        Assert.Contains("inside the stack", high.Message);

        // A vertical element sitting exactly on a material interface: its source strength is
        // −2µ₀/ε and the two sides disagree, so the kernel is an approach-dependent limit.
        var onInterface = Assert.Throws<ArgumentException>(() => set.Evaluate(1e-4, 0.4e-3, 0.8e-3));
        Assert.Contains("material interface", onInterface.Message);
        Assert.Contains("Split the current element", onInterface.Message);
    }
}
