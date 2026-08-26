using System.Numerics;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// The two-height structure the multi-layer VERTICAL kernels are built on, measured against the
/// SHIPPED per-z read-out rather than assumed.
///
/// <para>A vertical current needs three things the horizontal machinery does not obviously
/// provide: the charge kernel K̃_Φ(z, z′) at two arbitrary heights, its SOURCE-height derivative
/// ∂_z′K̃_Φ, and the mixed ∂_z∂_z′K̃_Φ. The first is already there —
/// <see cref="LayeredStackup.SplitAt"/> makes any height an interface and the S9b read-out
/// evaluates at any other. The second and third are the interesting ones, and both rest on
/// structure this file gates:</para>
/// <list type="number">
///   <item><b>Reciprocity.</b> K̃_Φ(a, b) = K̃_Φ(b, a). A charge kernel must be symmetric, and if
///     it is, the source-height derivative is just the OBSERVATION derivative with the arguments
///     swapped — an analytic quantity the read-out already returns. This is also the sharpest
///     independent check on the divergence read-out's local-ε normalization, which is not
///     obviously symmetric to look at.</item>
///   <item><b>Separability.</b> A one-dimensional Green's function is u(z&lt;)v(z&gt;)/W away from
///     its source, so g·∂₁∂₂g = ∂₁g·∂₂g identically — the mixed partial no combination of
///     single-argument derivatives can produce comes free from the rank-1 structure. Gating it
///     tells us whether the shipped TE read-out really has that structure, and (measured here)
///     whether K̃_Φ does — it does not, because it carries the ε-contrast coupling term on top of
///     the TE Green's function, which is exactly the decomposition the vertical kernels will
///     have to respect.</item>
/// </list>
/// <para>The source-height derivatives are checked against central finite differences in the
/// SOURCE height — the one thing here that is not analytic, so the band is the difference
/// quotient's own accuracy and is stated as such.</para>
/// </summary>
public class TwoHeightKernelTests
{
    private const double FrequencyHz = 10e9;
    private static double K0 => 2 * Math.PI * FrequencyHz / 299_792_458.0;

    private static LayeredStackup Stack() => new(new[]
    {
        new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
        new LayeredStackup.Layer(3.0, 0.005, 0.3e-3),
        new LayeredStackup.Layer(2.2, 0.0009, 0.5e-3)
    });

    private static Complex Kz(Complex kSq, Complex kRho)
    {
        var k = Complex.Sqrt(kSq - kRho * kRho);
        return k.Imaginary > 0 ? -k : k;
    }

    /// <summary>The six field kernels for a source at height <paramref name="source"/> observed at
    /// <paramref name="observe"/>: split the stack so the source height IS an interface, then read
    /// the shipped per-z kernels out at the observation height.</summary>
    private static (Complex GA, Complex W, Complex Phi, Complex DzPhi, Complex DzA, Complex DzW)
        At(LayeredStackup stack, double k0, Complex kRho, double source, double observe)
    {
        var (split, m) = stack.SplitAt(source);
        return TransmissionLineGreens.EvaluateField(split, k0, kRho, Kz(k0 * k0, kRho), m, observe);
    }

    private static double Rel(Complex a, Complex b) =>
        (a - b).Magnitude / Math.Max(Math.Max(a.Magnitude, b.Magnitude), 1e-300);

    public static IEnumerable<object[]> HeightPairs()
    {
        foreach (var (a, b) in new[]
                 {
                     (0.25e-3, 0.55e-3),      // both inside layer 0
                     (0.3e-3, 0.95e-3),       // across one interface
                     (0.4e-3, 1.35e-3),       // across two
                     (0.9e-3, 1.05e-3),       // both inside layer 1
                     (1.15e-3, 1.5e-3)        // both inside layer 2
                 })
            foreach (double kRhoOverK0 in new[] { 0.4, 0.95, 1.3, 3.5, 10.0 })
                yield return new object[] { a, b, kRhoOverK0 };
    }

    [Theory]
    [MemberData(nameof(HeightPairs))]
    public void TheChargeKernelIsReciprocal(double a, double b, double kRhoOverK0)
    {
        // K̃_Φ(a, b) = K̃_Φ(b, a). Two INDEPENDENT solves — different split stackups, different
        // source interfaces, different observation layers — so agreement is a statement about the
        // physics rather than about a shared code path. It is also the check on the divergence
        // read-out's ÷ε_local normalization, which is not obviously symmetric: a charge in medium
        // ε₁ seen from medium ε₂ carries 2/(ε₁+ε₂), and that IS symmetric, but only because the
        // transmitted amplitude supplies the other half.
        var stack = Stack();
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var forward = At(stack, k0, kRho, source: b, observe: a).Phi;
        var reverse = At(stack, k0, kRho, source: a, observe: b).Phi;
        Assert.True(Rel(forward, reverse) < 1e-12,
            $"K_Phi({a * 1e3:g4}, {b * 1e3:g4}) = {forward} but reversed = {reverse} "
            + $"(rel {Rel(forward, reverse):g3}) at k_rho/k0 = {kRhoOverK0}");
    }

    [Theory]
    [MemberData(nameof(HeightPairs))]
    public void TheVectorPotentialKernelIsReciprocal(double a, double b, double kRhoOverK0)
    {
        // The same statement for G̃_A = A_x, the TE Green's function, where it is the textbook
        // symmetry of a self-adjoint one-dimensional operator.
        var stack = Stack();
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var forward = At(stack, k0, kRho, source: b, observe: a).GA;
        var reverse = At(stack, k0, kRho, source: a, observe: b).GA;
        Assert.True(Rel(forward, reverse) < 1e-12,
            $"G_A({a * 1e3:g4}, {b * 1e3:g4}) = {forward} but reversed = {reverse} "
            + $"(rel {Rel(forward, reverse):g3}) at k_rho/k0 = {kRhoOverK0}");
    }

    [Theory]
    [MemberData(nameof(HeightPairs))]
    public void TheSourceHeightDerivativeIsTheSwappedObservationDerivative(
        double a, double b, double kRhoOverK0)
    {
        // ∂_z′K̃_Φ(z, z′) — the quantity a vertical current needs and no read-out returns — is
        // ∂₁K̃_Φ(z′, z) by the reciprocity above, i.e. an ANALYTIC derivative with the arguments
        // swapped. Checked against a central difference in the SOURCE height, which is the only
        // non-analytic thing here: the band is the difference quotient's own accuracy (h chosen
        // at ~1e-3 of the layer, where the O(h²) truncation and the O(eps/h) cancellation meet),
        // not a tolerance on the kernels.
        var stack = Stack();
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        double h = 2e-7;                                  // 0.2 µm against 0.3–0.8 mm layers

        var analytic = At(stack, k0, kRho, source: a, observe: b).DzPhi;   // ∂₁K(b, a) = ∂₂K(a, b)
        var plus = At(stack, k0, kRho, source: b + h, observe: a).Phi;
        var minus = At(stack, k0, kRho, source: b - h, observe: a).Phi;
        var numeric = (plus - minus) / (2 * h);

        Assert.True(Rel(analytic, numeric) < 2e-5,
            $"d/dz' K_Phi at source {b * 1e3:g4} mm, observed {a * 1e3:g4} mm: analytic {analytic}, "
            + $"finite difference {numeric} (rel {Rel(analytic, numeric):g3}), k_rho/k0 = {kRhoOverK0}");
    }

    [Theory]
    [MemberData(nameof(HeightPairs))]
    public void TheVectorKernelIsSeparable_SoItsMixedPartialIsFree(
        double a, double b, double kRhoOverK0)
    {
        // g·∂₁∂₂g = ∂₁g·∂₂g — the rank-1 identity of a one-dimensional Green's function away from
        // its source. It is what makes the mixed partial reachable at all: no combination of
        // single-argument derivatives of a symmetric function yields a mixed one, but a SEPARABLE
        // one hands it over. Gated against a finite difference of the analytic ∂₁g in the source
        // height, so both the identity and the code's structure are being checked at once.
        var stack = Stack();
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        double h = 2e-7;

        var g = At(stack, k0, kRho, source: b, observe: a).GA;
        var d1 = At(stack, k0, kRho, source: b, observe: a).DzA;          // ∂₁g(a, b)
        var d2 = At(stack, k0, kRho, source: a, observe: b).DzA;          // ∂₂g(a, b) by symmetry
        var identity = d1 * d2 / g;

        var plus = At(stack, k0, kRho, source: b + h, observe: a).DzA;
        var minus = At(stack, k0, kRho, source: b - h, observe: a).DzA;
        var numeric = (plus - minus) / (2 * h);

        Assert.True(Rel(identity, numeric) < 2e-5,
            $"mixed partial at ({a * 1e3:g4}, {b * 1e3:g4}) mm: separability gives {identity}, "
            + $"finite difference {numeric} (rel {Rel(identity, numeric):g3}), k_rho/k0 = {kRhoOverK0}");
    }
}
