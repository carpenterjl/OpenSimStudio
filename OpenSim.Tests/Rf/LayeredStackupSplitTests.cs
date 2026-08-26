using System.Numerics;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// <see cref="LayeredStackup.SplitAt"/> — the first piece of the multi-layer VERTICAL kernels.
///
/// <para>The transmission-line machinery only ever places a source AT an interface: that is what
/// its jump conditions are written against. Rather than derive a second source formulation for a
/// height inside a layer, the stack is SPLIT there and the existing interface source is used. The
/// whole method therefore rests on one claim — that a layer cut in half is the same stack — and
/// these gates are that claim, made against the shipped kernels rather than against arithmetic:
/// the boundary kernels, the per-z field kernels and the total height all come out unchanged, and
/// a split ON an existing interface returns that interface rather than manufacturing a
/// zero-thickness layer (whose reduced phase is exactly 1, leaving its two amplitudes degenerate
/// and the per-k_ρ system singular).</para>
/// </summary>
public class LayeredStackupSplitTests
{
    private const double FrequencyHz = 10e9;
    private static double K0 => 2 * Math.PI * FrequencyHz / 299_792_458.0;

    /// <summary>A genuine three-material stack — different εr AND different tanδ per layer, so a
    /// split that silently copied the wrong material would move every kernel.</summary>
    private static LayeredStackup Stack() => new(new[]
    {
        new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
        new LayeredStackup.Layer(3.0, 0.005, 0.3e-3),
        new LayeredStackup.Layer(2.2, 0.0009, 0.5e-3)
    });

    private static void AssertRel(Complex expected, Complex actual, double tol, string what)
    {
        double scale = Math.Max(expected.Magnitude, 1e-300);
        Assert.True((expected - actual).Magnitude / scale < tol,
            $"{what}: expected {expected}, got {actual} (rel {(expected - actual).Magnitude / scale:g3})");
    }

    [Fact]
    public void SplittingOnAnExistingInterface_ReturnsThatInterface_AndTheSameStackup()
    {
        var stack = Stack();
        var heights = stack.InterfaceHeights();
        for (int i = 0; i < heights.Length; i++)
        {
            var (split, index) = stack.SplitAt(heights[i]);
            Assert.Same(stack, split);
            Assert.Equal(i, index);
        }
    }

    [Fact]
    public void ASplitLandsOnTheTopOfTheLOWERHalf()
    {
        // The returned index must name the interface AT z, which is the top of the first half —
        // off by one and a source would land a whole layer away with no error anywhere.
        var stack = Stack();
        var heights = stack.InterfaceHeights();
        double z = 0.5e-3;                                  // inside layer 0 (0 … 0.8 mm)
        var (split, index) = stack.SplitAt(z);
        Assert.Equal(0, index);
        Assert.Equal(4, split.Layers.Count);
        Assert.Equal(z, split.InterfaceHeights()[index], 15);
        // Both halves keep the host layer's material, exactly.
        Assert.Equal(stack.Layers[0].RelativePermittivity, split.Layers[0].RelativePermittivity);
        Assert.Equal(stack.Layers[0].LossTangent, split.Layers[0].LossTangent);
        Assert.Equal(stack.Layers[0].RelativePermittivity, split.Layers[1].RelativePermittivity);
        Assert.Equal(stack.Layers[0].LossTangent, split.Layers[1].LossTangent);
        Assert.Equal(stack.Layers[1].RelativePermittivity, split.Layers[2].RelativePermittivity);
        Assert.Equal(heights[^1], split.TotalThicknessMeters, 15);
    }

    public static IEnumerable<object[]> SplitHeights()
    {
        // One height inside each layer, plus two near (but not on) an interface.
        foreach (double z in new[] { 0.2e-3, 0.79e-3, 0.81e-3, 0.95e-3, 1.3e-3, 1.55e-3 })
            foreach (double kRhoOverK0 in new[] { 0.3, 0.95, 1.4, 4.0, 12.0 })
                yield return new object[] { z, kRhoOverK0 };
    }

    [Theory]
    [MemberData(nameof(SplitHeights))]
    public void SplittingALayer_LeavesTheBOUNDARYKernelsUnchanged(double z, double kRhoOverK0)
    {
        // The F1 split-slab identity, now against an arbitrary cut rather than a hand-built pair
        // of stackups: the source stays at the top plane, and the kernels there cannot know that
        // a layer below was described as two.
        var stack = Stack();
        var (split, _) = stack.SplitAt(z);
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = SpectralKernelsKz(k0 * k0, kRho);

        var (gaA, phiA) = TransmissionLineGreens.Evaluate(stack, k0, kRho, kz0);
        var (gaB, phiB) = TransmissionLineGreens.Evaluate(split, k0, kRho, kz0);
        AssertRel(gaA, gaB, 1e-12, $"G_A at k_rho/k0 = {kRhoOverK0}, split {z * 1e3:g4} mm");
        AssertRel(phiA, phiB, 1e-12, $"K_Phi at k_rho/k0 = {kRhoOverK0}, split {z * 1e3:g4} mm");
    }

    [Theory]
    [MemberData(nameof(SplitHeights))]
    public void SplittingALayer_LeavesThePerZFieldKernelsUnchanged(double z, double kRhoOverK0)
    {
        // The same claim one level up: the S9b per-z read-out, sampled at heights on BOTH sides of
        // the cut and in the air above, is invariant. This is the property the vertical kernels
        // will actually lean on, so it is gated where they will use it.
        var stack = Stack();
        var (split, _) = stack.SplitAt(z);
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = SpectralKernelsKz(k0 * k0, kRho);
        int mA = stack.Layers.Count - 1, mB = split.Layers.Count - 1;
        double top = stack.TotalThicknessMeters;

        foreach (double probe in new[] { 0.1e-3, z * 0.5, z, z * 1.01, 1.2e-3, top, top * 1.5 })
        {
            var a = TransmissionLineGreens.EvaluateField(stack, k0, kRho, kz0, mA, probe);
            var b = TransmissionLineGreens.EvaluateField(split, k0, kRho, kz0, mB, probe);
            string at = $"z = {probe * 1e3:g4} mm, split {z * 1e3:g4} mm, k_rho/k0 = {kRhoOverK0}";
            AssertRel(a.GA, b.GA, 1e-11, $"G_A at {at}");
            AssertRel(a.W, b.W, 1e-11, $"W at {at}");
            AssertRel(a.Phi, b.Phi, 1e-11, $"K_Phi at {at}");
            AssertRel(a.DzA, b.DzA, 1e-11, $"dz G_A at {at}");
            AssertRel(a.DzW, b.DzW, 1e-11, $"dz W at {at}");
            AssertRel(a.DzPhi, b.DzPhi, 1e-11, $"dz K_Phi at {at}");
        }
    }

    [Fact]
    public void SplittingRepeatedly_StillDescribesTheSameStack()
    {
        // Splits compose: cutting a stack four times in four different layers must still leave the
        // kernels alone. A cumulative error would show here and nowhere else.
        var stack = Stack();
        var split = stack;
        foreach (double z in new[] { 0.2e-3, 0.9e-3, 1.0e-3, 1.4e-3 })
            (split, _) = split.SplitAt(z);
        Assert.Equal(7, split.Layers.Count);

        double k0 = K0;
        foreach (double kRhoOverK0 in new[] { 0.5, 1.1, 3.0, 9.0 })
        {
            var kRho = new Complex(kRhoOverK0 * k0, 0);
            var kz0 = SpectralKernelsKz(k0 * k0, kRho);
            var (gaA, phiA) = TransmissionLineGreens.Evaluate(stack, k0, kRho, kz0);
            var (gaB, phiB) = TransmissionLineGreens.Evaluate(split, k0, kRho, kz0);
            AssertRel(gaA, gaB, 1e-11, $"G_A after four splits at k_rho/k0 = {kRhoOverK0}");
            AssertRel(phiA, phiB, 1e-11, $"K_Phi after four splits at k_rho/k0 = {kRhoOverK0}");
        }
    }

    [Fact]
    public void ASplitWithinRoundingOfAnInterface_SnapsToIt()
    {
        // The snap is a stated rounding decision, not a hidden tolerance: a hair below 1e-12 of
        // the stack height lands ON the interface, a hair above genuinely divides the layer.
        var stack = Stack();
        double h = stack.InterfaceHeights()[0], total = stack.TotalThicknessMeters;
        var (snapped, index) = stack.SplitAt(h + 0.4e-12 * total);
        Assert.Same(stack, snapped);
        Assert.Equal(0, index);

        var (divided, _) = stack.SplitAt(h + 1e-6 * total);
        Assert.Equal(4, divided.Layers.Count);
    }

    [Fact]
    public void OutOfRangeSplitsAreTypedFailures()
    {
        var stack = Stack();
        double total = stack.TotalThicknessMeters;
        Assert.Throws<ArgumentOutOfRangeException>(() => stack.SplitAt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => stack.SplitAt(-1e-4));
        Assert.Throws<ArgumentOutOfRangeException>(() => stack.SplitAt(total * 1.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => stack.SplitAt(double.NaN));
        // The top plane itself is a legal interface, not an over-run.
        var (same, index) = stack.SplitAt(total);
        Assert.Same(stack, same);
        Assert.Equal(stack.Layers.Count - 1, index);
    }

    /// <summary>k_z on the Im ≤ 0 branch — the same closed form the kernels take from the
    /// Sommerfeld contour, restated here so the gate does not borrow the code it tests.</summary>
    private static Complex SpectralKernelsKz(Complex kSq, Complex kRho)
    {
        var k = Complex.Sqrt(kSq - kRho * kRho);
        return k.Imaginary > 0 ? -k : k;
    }
}
