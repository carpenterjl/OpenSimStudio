using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using Xunit;

namespace OpenSim.Tests.Rf;

/// <summary>
/// Stage F collapse gates (F1) for the multi-layer transmission-line Green's function.
/// The general N-layer <see cref="TransmissionLineGreens"/> must (a) reproduce the pinned
/// single-slab closed form at N = 1 to 1e-12 (including deep up the evanescent tail where
/// only the reduced basis survives), (b) match an INDEPENDENT N-layer boundary-value solve
/// (full coupled dense system in a global-exponential basis — none of the production
/// reduced-referencing algebra), (c) satisfy the split-slab identity — cutting one slab
/// into K identical sub-layers changes nothing — and (d) collapse to the primary + PEC
/// ground image when every layer is air.
/// </summary>
public class MultiLayerKernelTests
{
    private static readonly double Mu0 = 4e-7 * Math.PI;
    private static readonly double Eps0 = 1.0 / (Mu0 * 299_792_458.0 * 299_792_458.0);
    private static double K0 => 2 * Math.PI * 10e9 / 299_792_458.0;

    private static Complex Kz(Complex kSq, Complex kRho)
    {
        var s = Complex.Sqrt(kSq - kRho * kRho);
        return s.Imaginary > 0 ? -s : s;
    }

    // ---- The independent reference: an N-layer spectral BVP solved as ONE coupled dense
    // system in the global e^{±j k_z z} basis. TE (A_x) and TM (A_z, factoring out −jk_x)
    // are solved together; the A_z shunt-source rows carry the A_x columns directly, so the
    // coupling is in the matrix, not a staged right-hand side. Valid at moderate k_ρ /
    // thin stacks (the growing exponential is bounded there) — its whole point is to be
    // algebraically unlike the production path.
    private static (Complex GA, Complex KPhi) SolveMultiLayerBvp(
        LayeredStackup stackup, double k0, Complex kRho)
    {
        int n = stackup.Layers.Count;
        double k0Sq = k0 * k0;
        var kz = new Complex[n];
        var eps = new Complex[n];
        var h = stackup.InterfaceHeights();      // top of each layer; h[n-1] = d
        double d = h[n - 1];
        for (int i = 0; i < n; i++)
        {
            eps[i] = stackup.Layers[i].ComplexPermittivity;
            kz[i] = Kz(eps[i] * k0Sq, kRho);
        }
        var j = Complex.ImaginaryOne;
        var kz0 = Kz(k0Sq, kRho);

        // Unknown layout: a_i,b_i (A_x, cols 2i,2i+1), C (2n), p_i,q_i (A_z, 2n+1+2i,+1), S (4n+1).
        int size = 4 * n + 2;
        int cIdx = 2 * n, sIdx = 4 * n + 1;
        int pBase = 2 * n + 1;
        var m = new ComplexDenseMatrix(size, size);
        var rhs = new Complex[size];
        Complex Ep(int i, double z) => Complex.Exp(-j * kz[i] * z);
        Complex Em(int i, double z) => Complex.Exp(j * kz[i] * z);
        int row = 0;

        // ===== TE: A_x =====
        // Ground A_x(0)=0.
        m[row, 0] = Ep(0, 0); m[row, 1] = Em(0, 0); row++;
        for (int i = 0; i < n - 1; i++)
        {
            double z = h[i];
            int a = 2 * i, b = 2 * (i + 1);
            // value
            m[row, a] = Ep(i, z); m[row, a + 1] = Em(i, z);
            m[row, b] = -Ep(i + 1, z); m[row, b + 1] = -Em(i + 1, z); row++;
            // derivative
            m[row, a] = -j * kz[i] * Ep(i, z); m[row, a + 1] = j * kz[i] * Em(i, z);
            m[row, b] = j * kz[i + 1] * Ep(i + 1, z); m[row, b + 1] = -j * kz[i + 1] * Em(i + 1, z); row++;
        }
        // Top value: A_x(d) = C.
        m[row, 2 * (n - 1)] = Ep(n - 1, d); m[row, 2 * (n - 1) + 1] = Em(n - 1, d);
        m[row, cIdx] = -1; row++;
        // Top jump: −jk_z0 C + jk_z,n-1 a Ep − jk_z,n-1 b Em = −2µ0.
        m[row, cIdx] = -j * kz0;
        m[row, 2 * (n - 1)] = j * kz[n - 1] * Ep(n - 1, d);
        m[row, 2 * (n - 1) + 1] = -j * kz[n - 1] * Em(n - 1, d);
        rhs[row] = -2 * Mu0; row++;

        // ===== TM: A_z (ã_z, with −jk_x factored out) =====
        // Ground ∂_z A_z(0)=0.
        m[row, pBase] = -j * kz[0] * Ep(0, 0); m[row, pBase + 1] = j * kz[0] * Em(0, 0); row++;
        for (int i = 0; i < n - 1; i++)
        {
            double z = h[i];
            int p = pBase + 2 * i, q = pBase + 2 * (i + 1);
            int a = 2 * i;            // A_x of layer i (below) supplies the source
            // A_z value continuity
            m[row, p] = Ep(i, z); m[row, p + 1] = Em(i, z);
            m[row, q] = -Ep(i + 1, z); m[row, q + 1] = -Em(i + 1, z); row++;
            // (1/ε)∂_z A_z jump = A_x(h_i)(1/ε_i − 1/ε_{i+1})
            Complex above = 1 / eps[i + 1], below = 1 / eps[i];
            m[row, q] = above * (-j * kz[i + 1]) * Ep(i + 1, z);
            m[row, q + 1] = above * (j * kz[i + 1]) * Em(i + 1, z);
            m[row, p] = -below * (-j * kz[i]) * Ep(i, z);
            m[row, p + 1] = -below * (j * kz[i]) * Em(i, z);
            Complex src = below - above;   // (1/ε_i − 1/ε_{i+1})
            m[row, a] += -src * Ep(i, z); m[row, a + 1] += -src * Em(i, z); row++;
        }
        // Top A_z value: A_z(d) = S.
        m[row, pBase + 2 * (n - 1)] = Ep(n - 1, d); m[row, pBase + 2 * (n - 1) + 1] = Em(n - 1, d);
        m[row, sIdx] = -1; row++;
        // Top jump: −jk_z0 S − (1/ε_top)∂_z A_z|below = C(1/ε_top − 1).
        Complex epsTop = eps[n - 1], invTop = 1 / epsTop;
        m[row, sIdx] = -j * kz0;
        m[row, pBase + 2 * (n - 1)] = -invTop * (-j * kz[n - 1]) * Ep(n - 1, d);
        m[row, pBase + 2 * (n - 1) + 1] = -invTop * (j * kz[n - 1]) * Em(n - 1, d);
        m[row, cIdx] += -(invTop - 1); row++;

        var sol = ComplexLu.Factor(m).Solve(rhs);
        Complex c = sol[cIdx], s = sol[sIdx];
        return (c, (c - j * kz0 * s) / (Mu0 * Eps0));
    }

    public static TheoryData<double, double, double> SingleSlabSweep => new()
    {
        { 2.2, 0, 0.3 }, { 2.2, 0, 0.95 }, { 2.2, 0, 1.2 }, { 2.2, 0, 3.0 }, { 2.2, 0, 10.0 },
        { 2.2, 0.02, 0.3 }, { 2.2, 0.02, 1.2 }, { 6.0, 0, 0.5 }, { 6.0, 0, 8.0 },
        { 2.2, 0, 200.0 }, { 6.0, 0, 600.0 },   // deep tail: only the reduced basis survives
    };

    [Theory]
    [MemberData(nameof(SingleSlabSweep))]
    public void GeneralPath_MatchesSingleSlabClosedForm(double epsR, double tanD, double kRhoOverK0)
    {
        var substrate = new SubstrateStackup(epsR, tanD, 1.588e-3);
        var stackup = LayeredStackup.FromSubstrate(substrate);
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var (gA, kPhi) = TransmissionLineGreens.Evaluate(stackup, k0, kRho, kz0);
        var (gARef, kPhiRef) = SpectralKernels.Evaluate(substrate, k0, kRho, kz0);
        Assert.True((gA - gARef).Magnitude < 1e-12 * gARef.Magnitude, $"G_A {gA} vs closed {gARef}");
        Assert.True((kPhi - kPhiRef).Magnitude < 1e-12 * kPhiRef.Magnitude, $"K_Φ {kPhi} vs closed {kPhiRef}");
    }

    public static TheoryData<double> ModerateKRho => new() { 0.3, 0.95, 1.2, 3.0, 8.0 };

    [Theory]
    [MemberData(nameof(ModerateKRho))]
    public void GeneralPath_MatchesIndependentMultiLayerBvp(double kRhoOverK0)
    {
        // A genuinely layered stack: FR4 / adhesive / Rogers, all distinct.
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(4.4, 0.02, 0.8e-3),
            new LayeredStackup.Layer(3.0, 0.005, 0.3e-3),
            new LayeredStackup.Layer(2.2, 0.0009, 0.5e-3),
        });
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var (gA, kPhi) = TransmissionLineGreens.Evaluate(stackup, k0, kRho, kz0);
        var (gARef, kPhiRef) = SolveMultiLayerBvp(stackup, k0, kRho);
        Assert.True((gA - gARef).Magnitude < 1e-12 * gARef.Magnitude, $"G_A {gA} vs BVP {gARef}");
        Assert.True((kPhi - kPhiRef).Magnitude < 1e-12 * kPhiRef.Magnitude, $"K_Φ {kPhi} vs BVP {kPhiRef}");
    }

    [Theory]
    [InlineData(2.2, 0.0, 0.5)]
    [InlineData(2.2, 0.02, 1.2)]
    [InlineData(6.0, 0.0, 3.0)]
    [InlineData(1.0, 0.0, 1.7)]
    public void SplitSlab_KIdenticalLayersEqualOneSlab(double epsR, double tanD, double kRhoOverK0)
    {
        var substrate = new SubstrateStackup(epsR, tanD, 1.5e-3);
        double k0 = K0;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var one = TransmissionLineGreens.Evaluate(LayeredStackup.FromSubstrate(substrate), k0, kRho, kz0);
        foreach (int k in new[] { 2, 3, 5 })
        {
            var parts = Enumerable.Range(0, k)
                .Select(_ => new LayeredStackup.Layer(epsR, tanD, substrate.ThicknessMeters / k))
                .ToArray();
            var split = TransmissionLineGreens.Evaluate(new LayeredStackup(parts), k0, kRho, kz0);
            Assert.True((split.GA - one.GA).Magnitude < 1e-12 * one.GA.Magnitude,
                $"split-{k} G_A {split.GA} vs one-slab {one.GA}");
            Assert.True((split.KPhi - one.KPhi).Magnitude < 1e-12 * one.KPhi.Magnitude,
                $"split-{k} K_Φ {split.KPhi} vs one-slab {one.KPhi}");
        }
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(1.7)]
    [InlineData(12.0)]
    public void AllAirLayers_ReduceToPrimaryPlusGroundImage(double kRhoOverK0)
    {
        // Three air layers of different thicknesses ⇒ total slab of air ⇒ exact PEC image.
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(1, 0, 0.9e-3),
            new LayeredStackup.Layer(1, 0, 0.6e-3),
            new LayeredStackup.Layer(1, 0, 1.0e-3),
        });
        double k0 = K0;
        double d = stackup.TotalThicknessMeters;
        var kRho = new Complex(kRhoOverK0 * k0, 0);
        var kz0 = Kz(k0 * k0, kRho);
        var j = Complex.ImaginaryOne;
        var closed = (1 - Complex.Exp(-2 * j * kz0 * d)) / (j * kz0);
        var (gA, kPhi) = TransmissionLineGreens.Evaluate(stackup, k0, kRho, kz0);
        Assert.True((gA - Mu0 * closed).Magnitude < 1e-12 * (Mu0 * closed).Magnitude,
            $"G_A {gA} vs primary+image {Mu0 * closed}");
        Assert.True((kPhi - closed / Eps0).Magnitude < 1e-12 * (closed / Eps0).Magnitude,
            $"K_Φ {kPhi} vs primary+image {closed / Eps0}");
    }
}
