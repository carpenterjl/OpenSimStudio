using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;

namespace OpenSim.Tests.Si;

/// <summary>
/// The two-ground (stripline) static kernel of <see cref="RlgcExtractor"/>, against
/// references that share nothing with it: Cohn's exact conformal-mapping impedance of
/// the zero-thickness symmetric stripline, the TEM identity L·C = µ₀ε₀εr·I in a
/// homogeneous cross-section (which is what makes far-end crosstalk vanish), the
/// parallel-plate limit for an inhomogeneous asymmetric stack, and the open-top image
/// kernel in the limit where the upper plane is far away.
/// </summary>
public class StriplineRlgcTests
{
    private const double C0 = 299792458.0;
    private const double Epsilon0 = 8.8541878128e-12;
    private const double Mu0 = 4e-7 * Math.PI;

    private static CoupledLineCrossSection Stripline(double epsBelow, double hBelow,
        double epsAbove, double hAbove, params (double Center, double Width)[] traces) =>
        new(new LayeredStackup(new[]
            {
                new LayeredStackup.Layer(epsBelow, 0, hBelow),
                new LayeredStackup.Layer(epsAbove, 0, hAbove),
            }), 0,
            traces.Select(t => TraceCrossSection.Copper(t.Center, t.Width)).ToArray(),
            topGround: true);

    /// <summary>Complete elliptic integral of the first kind by the AGM, K = π / (2·AGM(1, k′)),
    /// taken as a function of the COMPLEMENTARY modulus k′ = √(1 − k²) — for a wide strip
    /// tanh(πw/2b) rounds to exactly 1 and forming k′ from it would lose everything.</summary>
    private static double EllipticKOfComplement(double complementaryModulus)
    {
        double a = 1, b = complementaryModulus;
        for (int i = 0; i < 60 && Math.Abs(a - b) > 1e-16 * a; i++)
            (a, b) = (0.5 * (a + b), Math.Sqrt(a * b));
        return Math.PI / (2 * a);
    }

    /// <summary>Cohn (1954): zero-thickness strip of width w centred between planes b apart,
    /// Z0·√εr = 30π·K(k)/K(k′), k = sech(πw/2b), k′ = tanh(πw/2b).</summary>
    private static double CohnZ0(double w, double b, double epsR)
    {
        double x = Math.PI * w / (2 * b);
        double sech = 1 / Math.Cosh(x), tanh = Math.Tanh(x);
        return 30 * Math.PI / Math.Sqrt(epsR) * EllipticKOfComplement(tanh) / EllipticKOfComplement(sech);
    }

    [Fact]
    public void CohnFormula_HasItsKnownLimits()
    {
        // k = k′ = 1/√2 at πw/2b = asinh(1): K/K′ = 1, so Z0√εr = 30π exactly.
        double b = 1e-3, w = 2 * b * Math.Asinh(1) / Math.PI;
        Assert.Equal(30 * Math.PI, CohnZ0(w, b, 1), 1e-9);
        // Wide strip → two parallel plates of height b/2 in parallel, plus fringing:
        // Z0 just below η₀·(b/2)/(2w).
        double wide = CohnZ0(20e-3, 1e-3, 1);
        Assert.InRange(wide, 0.9 * 376.73 / 80, 376.73 / 80);
    }

    [Theory]
    [InlineData(0.15e-3, 0.4e-3, 4.4)]     // the audit's case: about 45 Ω
    [InlineData(0.10e-3, 0.4e-3, 4.4)]     // narrow
    [InlineData(0.50e-3, 0.4e-3, 3.5)]     // wide
    [InlineData(1.50e-3, 0.3e-3, 2.2)]     // very wide, w/b = 5
    public void SymmetricStripline_MatchesCohn(double w, double b, double epsR)
    {
        var rlgc = RlgcExtractor.Extract(Stripline(epsR, b / 2, epsR, b / 2, (0, w)), panelsPerTrace: 64);

        double c = rlgc.CapacitanceFaradsPerMeter[0, 0], l = rlgc.InductanceHenriesPerMeter[0, 0];
        double z0 = Math.Sqrt(l / c);
        double reference = CohnZ0(w, b, epsR);
        Assert.True(Math.Abs(z0 - reference) <= 0.01 * reference,
            $"Z0 {z0:g6} Ω vs Cohn {reference:g6} Ω (w {w * 1e3} mm, b {b * 1e3} mm, εr {epsR})");

        // Homogeneous dielectric: ε_eff = εr, i.e. v = c/√εr.
        double epsEff = c / rlgc.AirCapacitanceFaradsPerMeter[0, 0];
        Assert.Equal(epsR, epsEff, 0.005 * epsR);
        Assert.Equal(C0 / Math.Sqrt(epsR), 1 / Math.Sqrt(l * c), 0.0025 * C0);
    }

    [Fact]
    public void CoupledPairInHomogeneousStripline_HasNoFarEndCrosstalk()
    {
        // 0.15 mm traces, 0.15 mm gap, εr 4.4, b = 0.4 mm. In a homogeneous TEM section
        // L·C = µ₀ε₀εr·I, so the inductive and capacitive coupling ratios are equal and
        // the forward (far-end) coupling coefficient ½(Lm/L − Cm/C) is zero.
        const double epsR = 4.4;
        var rlgc = RlgcExtractor.Extract(Stripline(epsR, 0.2e-3, epsR, 0.2e-3, (-0.15e-3, 0.15e-3), (0.15e-3, 0.15e-3)));
        var c = rlgc.CapacitanceFaradsPerMeter;
        var l = rlgc.InductanceHenriesPerMeter;

        Assert.True(c[0, 1] < 0, "Maxwell mutual capacitance is negative");
        Assert.True(l[0, 1] > 0);
        double kC = -c[0, 1] / c[0, 0], kL = l[0, 1] / l[0, 0];
        Assert.InRange(kC, 0.02, 0.5);                            // really coupled
        Assert.True(Math.Abs(kL - kC) < 1e-3 * kC, $"K_L {kL:g6} vs K_C {kC:g6}");

        double scale = Mu0 * Epsilon0 * epsR;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                double product = l[i, 0] * c[0, j] + l[i, 1] * c[1, j];
                Assert.Equal(i == j ? 1.0 : 0.0, product / scale, 2e-3);
            }

        // The same pair as surface microstrip is NOT homogeneous: K_L ≠ K_C there, which
        // is the far-end crosstalk a stripline does not have.
        var microstrip = RlgcExtractor.Extract(new CoupledLineCrossSection(
            new LayeredStackup(new[] { new LayeredStackup.Layer(epsR, 0, 0.2e-3) }), 0,
            new[] { TraceCrossSection.Copper(-0.15e-3, 0.15e-3), TraceCrossSection.Copper(0.15e-3, 0.15e-3) }));
        double mkC = -microstrip.CapacitanceFaradsPerMeter[0, 1] / microstrip.CapacitanceFaradsPerMeter[0, 0];
        double mkL = microstrip.InductanceHenriesPerMeter[0, 1] / microstrip.InductanceHenriesPerMeter[0, 0];
        Assert.True(mkL - mkC > 0.02, $"microstrip K_L {mkL:g4} − K_C {mkC:g4} should be clearly positive");
    }

    [Fact]
    public void AsymmetricInhomogeneousStripline_ApproachesTheParallelPlateSum()
    {
        // Very wide strip (w = 40·b): C′ → ε₀·w·(ε↓/h↓ + ε↑/h↑) from above, the fringing
        // being a few percent. This checks the two different dielectrics and heights
        // land on the right sides.
        const double w = 20e-3, hBelow = 0.1e-3, hAbove = 0.4e-3, epsBelow = 3.0, epsAbove = 4.5;
        var rlgc = RlgcExtractor.Extract(Stripline(epsBelow, hBelow, epsAbove, hAbove, (0, w)));

        double plates = Epsilon0 * w * (epsBelow / hBelow + epsAbove / hAbove);
        double c = rlgc.CapacitanceFaradsPerMeter[0, 0];
        Assert.InRange(c, plates, 1.05 * plates);

        double airPlates = Epsilon0 * w * (1 / hBelow + 1 / hAbove);
        Assert.InRange(rlgc.AirCapacitanceFaradsPerMeter[0, 0], airPlates, 1.05 * airPlates);
    }

    [Fact]
    public void FarUpperPlaneOverAir_RecoversTheMicrostripKernel()
    {
        // An air layer 60 substrate heights thick under the upper plane: the two-ground
        // spectral kernel must agree with the open-top image-series kernel it does not share
        // a line of code with.
        const double w = 0.3e-3, h = 0.2e-3, epsR = 4.4;
        var open = RlgcExtractor.Extract(new CoupledLineCrossSection(
            new LayeredStackup(new[] { new LayeredStackup.Layer(epsR, 0, h) }), 0,
            new[] { TraceCrossSection.Copper(0, w) }));
        var shielded = RlgcExtractor.Extract(Stripline(epsR, h, 1.0, 60 * h, (0, w)));

        double cOpen = open.CapacitanceFaradsPerMeter[0, 0], cShielded = shielded.CapacitanceFaradsPerMeter[0, 0];
        Assert.Equal(cOpen, cShielded, 0.005 * cOpen);
        Assert.Equal(open.AirCapacitanceFaradsPerMeter[0, 0], shielded.AirCapacitanceFaradsPerMeter[0, 0],
            0.01 * open.AirCapacitanceFaradsPerMeter[0, 0]);
    }

    [Fact]
    public void LossyDielectric_GivesTheLossTangentAsGOverOmegaC()
    {
        // Homogeneous lossy stripline: every field line is in the dielectric, so C″/C′ = tanδ.
        var section = new CoupledLineCrossSection(
            new LayeredStackup(new[]
            {
                new LayeredStackup.Layer(4.4, 0.02, 0.2e-3),
                new LayeredStackup.Layer(4.4, 0.02, 0.2e-3),
            }), 0, new[] { TraceCrossSection.Copper(0, 0.15e-3) }, topGround: true);
        var rlgc = RlgcExtractor.Extract(section);

        Assert.Equal(0.02, rlgc.CapacitanceLossFaradsPerMeter[0, 0] / rlgc.CapacitanceFaradsPerMeter[0, 0], 1e-6);
    }

    [Fact]
    public void TopGround_NeedsDielectricAboveTheMetal()
    {
        Assert.Throws<ArgumentException>(() => new CoupledLineCrossSection(
            new LayeredStackup(new[] { new LayeredStackup.Layer(4.4, 0, 0.2e-3) }), 0,
            new[] { TraceCrossSection.Copper(0, 0.15e-3) }, topGround: true));
    }

    [Fact]
    public void ProximityExtraction_RefusesATwoPlaneSection()
    {
        var ex = Assert.Throws<NotSupportedException>(() => ProximityExtractor.Extract(
            Stripline(4.4, 0.2e-3, 4.4, 0.2e-3, (0, 0.15e-3)), 1e3, 1e9));
        Assert.Contains("stripline", ex.Message);
    }
}
