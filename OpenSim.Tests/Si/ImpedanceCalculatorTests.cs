using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;
using Xunit.Abstractions;

namespace OpenSim.Tests.Si;

/// <summary>
/// The stackup and impedance calculator against closed forms that share nothing with the
/// boundary-element solve: Cohn's even- and odd-mode impedances of the zero-thickness
/// edge-coupled stripline, the conformal map of three coplanar strips (the coplanar
/// waveguide with finite grounds), the TEM dielectric attenuation, and the algebra of
/// grounding one conductor of a pair.
/// </summary>
public class ImpedanceCalculatorTests
{
    private const double C0 = 299792458.0;
    private readonly ITestOutputHelper _output;

    public ImpedanceCalculatorTests(ITestOutputHelper output) => _output = output;

    /// <summary>K(k) by the AGM: π / (2·AGM(1, √(1 − k²))).</summary>
    private static double EllipticK(double k)
    {
        double a = 1, b = Math.Sqrt(1 - k * k);
        for (int i = 0; i < 60 && Math.Abs(a - b) > 1e-16 * a; i++)
            (a, b) = (0.5 * (a + b), Math.Sqrt(a * b));
        return Math.PI / (2 * a);
    }

    /// <summary>K(k′)/K(k).</summary>
    private static double ComplementRatio(double k) => EllipticK(Math.Sqrt(1 - k * k)) / EllipticK(k);

    private static readonly RlgcModel Lossless = RlgcModel.Kernel;

    // ------------------------------------------------------------------ Cohn, coupled stripline

    /// <summary>Cohn (1955), zero-thickness strips of width w, gap s, centred between planes
    /// b apart: k_e = tanh(πw/2b)·tanh(π(w+s)/2b), k_o = tanh(πw/2b)·coth(π(w+s)/2b),
    /// Z·√εr = 30π·K(k′)/K(k).</summary>
    [Theory]
    [InlineData(0.15e-3, 0.15e-3, 0.4e-3, 4.4)]
    [InlineData(0.10e-3, 0.10e-3, 0.4e-3, 3.5)]
    [InlineData(0.20e-3, 0.40e-3, 0.5e-3, 4.0)]
    [InlineData(0.30e-3, 0.08e-3, 0.3e-3, 2.2)]
    public void EdgeCoupledStripline_MatchesCohn(double w, double s, double b, double epsR)
    {
        var report = ImpedanceCalculator.Solve(new LineSpec
        {
            Structure = LineStructure.Stripline, WidthMeters = w, PairGapMeters = s,
            HeightMeters = b / 2, UpperHeightMeters = b / 2,
            RelativePermittivity = epsR, UpperRelativePermittivity = epsR,
            LossTangent = 0, UpperLossTangent = 0, Model = Lossless, FrequencyHz = 10e9
        }, panelsPerTrace: 64);

        double tw = Math.Tanh(Math.PI * w / (2 * b)), tws = Math.Tanh(Math.PI * (w + s) / (2 * b));
        double even = 30 * Math.PI / Math.Sqrt(epsR) * ComplementRatio(tw * tws);
        double odd = 30 * Math.PI / Math.Sqrt(epsR) * ComplementRatio(tw / tws);
        // The lossless mode impedances, from L and C alone.
        var l = report.Rlgc.InductanceHenriesPerMeter;
        var c = report.Rlgc.CapacitanceFaradsPerMeter;
        double zOdd = Math.Sqrt((l[0, 0] - l[0, 1]) / (c[0, 0] - c[0, 1]));
        double zEven = Math.Sqrt((l[0, 0] + l[0, 1]) / (c[0, 0] + c[0, 1]));
        _output.WriteLine($"w {w * 1e3} s {s * 1e3} b {b * 1e3}: odd {zOdd:g6} vs {odd:g6} ({(zOdd / odd - 1) * 100:f3} %), " +
                          $"even {zEven:g6} vs {even:g6} ({(zEven / even - 1) * 100:f3} %)");
        Assert.True(Math.Abs(zOdd - odd) <= 0.01 * odd, $"odd {zOdd} vs Cohn {odd}");
        Assert.True(Math.Abs(zEven - even) <= 0.01 * even, $"even {zEven} vs Cohn {even}");

        // What the report prints is the same thing (the kernel's R shifts Re Z far below 1 %).
        Assert.Equal(zOdd, report.Modes[0].ImpedanceOhms, 0.002 * zOdd);
        Assert.Equal(zEven, report.Modes[1].ImpedanceOhms, 0.002 * zEven);
        Assert.Equal(2 * report.Modes[0].ImpedanceOhms, report.DifferentialOhms!.Value, 1e-12);
        Assert.Equal(report.Modes[1].ImpedanceOhms / 2, report.CommonOhms!.Value, 1e-12);
        // One dielectric: both modes travel at c/√εr.
        Assert.Equal(epsR, report.Modes[0].EffectivePermittivity, 0.002 * epsR);
        Assert.Equal(epsR, report.Modes[1].EffectivePermittivity, 0.002 * epsR);
    }

    [Fact]
    public void FarEndCrosstalk_IsZeroInStripline_AndNegativeInMicrostrip()
    {
        var stripline = ImpedanceCalculator.Solve(new LineSpec
        {
            Structure = LineStructure.Stripline, WidthMeters = 0.15e-3, PairGapMeters = 0.15e-3,
            HeightMeters = 0.2e-3, UpperHeightMeters = 0.2e-3, Model = Lossless
        });
        var c = stripline.Rlgc.CapacitanceFaradsPerMeter;
        double capacitive = -c[0, 1] * Math.Sqrt(stripline.Rlgc.InductanceHenriesPerMeter[0, 0] / c[0, 0]);
        Assert.True(Math.Abs(stripline.FarEndCouplingSecondsPerMeter!.Value) < 1e-3 * capacitive,
            $"far-end {stripline.FarEndCouplingSecondsPerMeter} against its capacitive half {capacitive}");
        Assert.InRange(stripline.NearEndCoupling!.Value, 0.01, 0.25);

        // Over one plane with air above, the inductive coupling is the larger.
        var microstrip = ImpedanceCalculator.Solve(new LineSpec
        {
            WidthMeters = 0.3e-3, PairGapMeters = 0.2e-3, HeightMeters = 0.2e-3, Model = Lossless
        });
        Assert.True(microstrip.FarEndCouplingSecondsPerMeter < 0);
        Assert.True(microstrip.Modes[0].EffectivePermittivity < microstrip.Modes[1].EffectivePermittivity,
            "the odd mode has more of its field in the air");
        Assert.True(microstrip.Modes[0].ImpedanceOhms < microstrip.Modes[1].ImpedanceOhms);
    }

    // ------------------------------------------------------------------ asymmetric pairs

    private static (System.Numerics.Complex[,] Z, System.Numerics.Complex[,] Y) SeriesAndShunt(RlgcResult rlgc, double f, bool lossless)
    {
        double w = 2 * Math.PI * f;
        var r = rlgc.ResistanceMatrixOhmsPerMeter?.Invoke(f);
        var li = rlgc.InternalInductanceHenriesPerMeter?.Invoke(f);
        var c = rlgc.CapacitancePerMeter(f);
        var g = rlgc.ConductancePerMeter(f);
        var z = new System.Numerics.Complex[2, 2];
        var y = new System.Numerics.Complex[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                double re = lossless ? 0 : r is not null ? r[i, j] : i == j ? rlgc.ResistancePerMeter(i, f) : 0;
                double l = rlgc.InductanceHenriesPerMeter[i, j] + (lossless || li is null ? 0 : li[i, j]);
                z[i, j] = new System.Numerics.Complex(re, w * l);
                y[i, j] = new System.Numerics.Complex(lossless ? 0 : g[i, j], w * c[i, j]);
            }
        return (z, y);
    }

    [Fact]
    public void TheGeneralModes_OfASymmetricPair_AreTheEvenAndOddModes()
    {
        // The asymmetric pair's modal algebra, fed a symmetric pair's lossy Z and Y, must give
        // the even/odd split the symmetric path uses (and Cohn's gate stands behind).
        var spec = new LineSpec { WidthMeters = 0.3e-3, PairGapMeters = 0.2e-3, HeightMeters = 0.2e-3, FrequencyHz = 2e9 };
        var symmetric = ImpedanceCalculator.Solve(spec);
        var (z, y) = SeriesAndShunt(symmetric.Rlgc, spec.FrequencyHz, lossless: false);
        var (pi, c, zc) = ImpedanceCalculator.AsymmetricModes(z, y, 2 * Math.PI * spec.FrequencyHz);

        var odd = symmetric.Modes[0];
        var even = symmetric.Modes[1];
        foreach (var (general, split) in new[] { (pi, odd), (c, even) })
        {
            Assert.Equal(split.ImpedanceOhms, general.ImpedanceOhms, 1e-9 * split.ImpedanceOhms);
            Assert.Equal(split.EffectivePermittivity, general.EffectivePermittivity, 1e-9 * split.EffectivePermittivity);
            Assert.Equal(split.ConductorLossDbPerMeter, general.ConductorLossDbPerMeter, 1e-9 * split.ConductorLossDbPerMeter);
            Assert.Equal(split.DielectricLossDbPerMeter, general.DielectricLossDbPerMeter, 1e-9 * split.DielectricLossDbPerMeter);
        }
        double zDiff = zc[0, 0] + zc[1, 1] - 2 * zc[0, 1];
        _output.WriteLine(string.Join(Environment.NewLine, symmetric.Describe()));
        _output.WriteLine($"Zc {zc[0, 0]:g6} {zc[0, 1]:g6} {zc[1, 0]:g6} {zc[1, 1]:g6}; pi {pi.ImpedanceOhms:g6} c {c.ImpedanceOhms:g6}");
        Assert.Equal(symmetric.DifferentialOhms!.Value, zDiff, 1e-4 * zDiff);
    }

    [Fact]
    public void TheCharacteristicMatrix_OfALosslessAsymmetricPair_IsLAndCs()
    {
        // Z_c = (Z·Y)^−½·Z by the 2×2 root, against LineReadout's C⁻¹·(C·L)^½ by Cholesky and
        // Jacobi: two routes to the same matrix.
        var report = ImpedanceCalculator.Solve(new LineSpec
        {
            WidthMeters = 0.2e-3, SecondWidthMeters = 0.45e-3, PairGapMeters = 0.15e-3, HeightMeters = 0.2e-3
        });
        Assert.NotNull(report.CharacteristicImpedanceOhms);
        var (z, y) = SeriesAndShunt(report.Rlgc, 1e9, lossless: true);
        var (_, _, zc) = ImpedanceCalculator.AsymmetricModes(z, y, 2 * Math.PI * 1e9);
        var expected = LineReadout.CharacteristicImpedance(report.Rlgc.InductanceHenriesPerMeter, report.Rlgc.CapacitancePerMeter(1e9));
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.Equal(expected[i, j], zc[i, j], 1e-9 * expected[0, 0]);
        _output.WriteLine(string.Join(Environment.NewLine, report.Describe()));
    }

    [Fact]
    public void AnAsymmetricPairFarApart_IsTwoSingleTraces()
    {
        // Twenty substrate heights apart the traces barely couple: a differential wave sees the
        // two lines in series, a common one sees them in parallel.
        var single = new LineSpec { WidthMeters = 0.2e-3, HeightMeters = 0.2e-3, Model = Lossless };
        double z1 = ImpedanceCalculator.Solve(single).ImpedanceOhms;
        double z2 = ImpedanceCalculator.Solve(single with { WidthMeters = 0.5e-3 }).ImpedanceOhms;
        var pair = ImpedanceCalculator.Solve(single with { SecondWidthMeters = 0.5e-3, PairGapMeters = 4e-3 });
        _output.WriteLine($"Z1 {z1:g5}, Z2 {z2:g5}; Z_diff {pair.DifferentialOhms:g5} vs {z1 + z2:g5}, "
                          + $"Z_common {pair.CommonOhms:g5} vs {z1 * z2 / (z1 + z2):g5}");
        Assert.Equal(z1 + z2, pair.DifferentialOhms!.Value, 0.01 * (z1 + z2));
        Assert.Equal(z1 * z2 / (z1 + z2), pair.CommonOhms!.Value, 0.01 * z1 * z2 / (z1 + z2));
        Assert.Equal(z1, pair.ImpedanceOhms, 0.01 * z1);
    }

    [Fact]
    public void AnAsymmetricStripline_InOneDielectric_HasTemModes_AndNoFarEndCrosstalk()
    {
        const double epsR = 3.5;
        var report = ImpedanceCalculator.Solve(new LineSpec
        {
            Structure = LineStructure.Stripline, WidthMeters = 0.1e-3, SecondWidthMeters = 0.25e-3, PairGapMeters = 0.12e-3,
            HeightMeters = 0.2e-3, UpperHeightMeters = 0.2e-3, RelativePermittivity = epsR, UpperRelativePermittivity = epsR,
            LossTangent = 0, UpperLossTangent = 0, Model = Lossless, FrequencyHz = 10e9
        });
        Assert.Equal(new[] { "pi", "c" }, report.Modes.Select(m => m.Name));
        foreach (var mode in report.Modes)
            Assert.Equal(epsR, mode.EffectivePermittivity, 0.002 * epsR);
        double c = report.Rlgc.CapacitanceFaradsPerMeter[0, 1];
        double z0 = report.ImpedanceOhms;
        Assert.True(Math.Abs(report.FarEndCouplingSecondsPerMeter!.Value) < 1e-3 * Math.Abs(c) * z0);
    }

    // ------------------------------------------------------------------ coplanar

    /// <summary>
    /// A strip |x| &lt; a between grounds b &lt; |x| &lt; c, nothing else. t = x² maps the quarter
    /// plane to a half plane with the conductors on [0, a²] and [b², c²] and flux walls
    /// between; two such intervals have C = ε·K(k)/K(k′) with
    /// k = (a/b)·√((1 − b²/c²)/(1 − a²/c²)), four quarters make the line, and
    /// Z0 = (30π/√ε_eff)·K(k′)/K(k). With the pattern on the face of a thick dielectric the
    /// field is the same in both halves and ε_eff = (εr + 1)/2 exactly.
    /// The solver always has a plane under the dielectric; here it is fifty times further
    /// away than the pattern is wide.
    /// </summary>
    [Theory]
    [InlineData(0.20e-3, 0.10e-3, 1.0e-3, 1.0)]
    [InlineData(0.20e-3, 0.10e-3, 1.0e-3, 10.0)]
    [InlineData(0.50e-3, 0.05e-3, 4.0e-3, 4.4)]
    [InlineData(0.10e-3, 0.30e-3, 5.0e-3, 3.0)]
    public void CoplanarWaveguide_MatchesTheConformalMap(double w, double gap, double ground, double epsR)
    {
        double a = w / 2, b = a + gap, c = b + ground;
        var report = ImpedanceCalculator.Solve(new LineSpec
        {
            WidthMeters = w, CoplanarGapMeters = gap, CoplanarGroundWidthMeters = ground,
            HeightMeters = 50 * 2 * c, RelativePermittivity = epsR, LossTangent = 0, Model = Lossless
        }, panelsPerTrace: 64);

        double k = a / b * Math.Sqrt((1 - b * b / (c * c)) / (1 - a * a / (c * c)));
        double epsEff = (epsR + 1) / 2;
        double expected = 30 * Math.PI / Math.Sqrt(epsEff) * ComplementRatio(k);
        double l = report.Rlgc.InductanceHenriesPerMeter[0, 0], cap = report.Rlgc.CapacitanceFaradsPerMeter[0, 0];
        double z0 = Math.Sqrt(l / cap);
        double eps = cap / report.Rlgc.AirCapacitanceFaradsPerMeter[0, 0];
        _output.WriteLine($"w {w * 1e3} gap {gap * 1e3} ground {ground * 1e3} εr {epsR}: " +
                          $"Z0 {z0:g6} vs {expected:g6} ({(z0 / expected - 1) * 100:f3} %), ε_eff {eps:g5} vs {epsEff:g5}");
        Assert.True(Math.Abs(z0 - expected) <= 0.01 * expected, $"Z0 {z0} vs {expected}");
        Assert.True(Math.Abs(eps - epsEff) <= 0.01 * epsEff, $"ε_eff {eps} vs {epsEff}");
        Assert.Equal(1, report.Rlgc.ConductorCount);
    }

    [Fact]
    public void CoplanarGrounds_LowerTheImpedance_AndStopMatteringWhenFarAway()
    {
        var spec = new LineSpec { WidthMeters = 0.3e-3, HeightMeters = 0.2e-3, Model = Lossless };
        double alone = ImpedanceCalculator.Solve(spec).ImpedanceOhms;
        double near = ImpedanceCalculator.Solve(spec with { CoplanarGapMeters = 0.1e-3 }).ImpedanceOhms;
        double far = ImpedanceCalculator.Solve(spec with { CoplanarGapMeters = 2e-3 }).ImpedanceOhms;
        _output.WriteLine($"microstrip {alone:g5} Ω; grounds at 0.1 mm {near:g5} Ω; at 2 mm {far:g5} Ω");
        Assert.True(near < 0.95 * alone);
        Assert.Equal(alone, far, 0.01 * alone);
    }

    // ------------------------------------------------------------------ grounding a conductor

    [Fact]
    public void GroundingOneOfTwo_IsTheSchurComplement()
    {
        var section = new CoupledLineCrossSection(
            new LayeredStackup(new[] { new LayeredStackup.Layer(4.4, 0.02, 0.2e-3) }), 0,
            new[] { TraceCrossSection.Copper(-0.25e-3, 0.3e-3), TraceCrossSection.Copper(0.3e-3, 0.4e-3) });
        var full = RlgcExtractor.Extract(section);
        var reduced = RlgcReduction.GroundConductors(full, new[] { 1 });

        var l = full.InductanceHenriesPerMeter;
        Assert.Equal(1, reduced.ConductorCount);
        Assert.Equal(full.CapacitanceFaradsPerMeter[0, 0], reduced.CapacitanceFaradsPerMeter[0, 0], 1e-20);
        Assert.Equal(full.CapacitanceLossFaradsPerMeter[0, 0], reduced.CapacitanceLossFaradsPerMeter[0, 0], 1e-22);
        double expected = l[0, 0] - l[0, 1] * l[1, 0] / l[1, 1];
        Assert.Equal(expected, reduced.InductanceHenriesPerMeter[0, 0], 1e-9 * expected);

        // With loss: Z_red = Z11 − Z12·Z21/Z22 at one frequency, by hand.
        double f = 1e9, w = 2 * Math.PI * f;
        var z11 = new System.Numerics.Complex(full.ResistancePerMeter(0, f), w * l[0, 0]);
        var z22 = new System.Numerics.Complex(full.ResistancePerMeter(1, f), w * l[1, 1]);
        var z12 = new System.Numerics.Complex(0, w * l[0, 1]);
        var byHand = z11 - z12 * z12 / z22;
        double r = reduced.ResistanceMatrixOhmsPerMeter!(f)[0, 0];
        double lTotal = reduced.InductanceHenriesPerMeter[0, 0] + reduced.InternalInductanceHenriesPerMeter!(f)[0, 0];
        Assert.Equal(byHand.Real, r, 1e-9 * byHand.Real);
        Assert.Equal(byHand.Imaginary / w, lTotal, 1e-9 * lTotal);
        Assert.True(r > full.ResistancePerMeter(0, f), "the grounded neighbour carries return current and adds loss");

        // Nothing grounded is the same object; everything grounded is refused.
        Assert.Same(full, RlgcReduction.GroundConductors(full, Array.Empty<int>()));
        Assert.Throws<ArgumentException>(() => RlgcReduction.GroundConductors(full, new[] { 0, 1 }));
    }

    // ------------------------------------------------------------------ loss

    [Fact]
    public void DielectricLossInStripline_IsTheTemValue()
    {
        const double epsR = 4.0, tanD = 0.02, f = 2e9;
        var report = ImpedanceCalculator.Solve(new LineSpec
        {
            Structure = LineStructure.Stripline, WidthMeters = 0.15e-3,
            HeightMeters = 0.2e-3, UpperHeightMeters = 0.2e-3,
            RelativePermittivity = epsR, UpperRelativePermittivity = epsR,
            LossTangent = tanD, UpperLossTangent = tanD, Model = Lossless, FrequencyHz = f
        });
        // α_d = π·f·√εr·tan δ / c [Np/m].
        double expected = Math.PI * f * Math.Sqrt(epsR) * tanD / C0 * 8.685889638;
        var mode = report.Modes[0];
        Assert.Equal(expected, mode.DielectricLossDbPerMeter, 0.002 * expected);
        // α_c = R/(2·Z0) for a low-loss line.
        double conductor = mode.ResistanceOhmsPerMeter / (2 * mode.ImpedanceOhms) * 8.685889638;
        Assert.Equal(conductor, mode.ConductorLossDbPerMeter, 0.002 * conductor);
        Assert.Equal(mode.ConductorLossDbPerMeter + mode.DielectricLossDbPerMeter, mode.LossDbPerMeter, 1e-12);
    }

    [Fact]
    public void Roughness_HasItsLimits_AndRaisesTheConductorLoss()
    {
        const double sigma = 5.8e7;
        var hammerstad = SurfaceRoughness.Hammerstad(1e-6);
        Assert.Equal(1.0, hammerstad.Factor(0, sigma));
        Assert.Equal(1.0, hammerstad.Factor(1e3, sigma), 3);          // δ = 2 mm ≫ Δ
        Assert.Equal(2.0, hammerstad.Factor(1e14, sigma), 2);         // δ ≪ Δ
        // Δ = δ: 1 + (2/π)·atan(1.4).
        double fEqual = 1 / (Math.PI * 4e-7 * Math.PI * sigma * 1e-12);
        Assert.Equal(1 + 2 / Math.PI * Math.Atan(1.4), hammerstad.Factor(fEqual, sigma), 9);

        var huray = SurfaceRoughness.Huray(0.5e-6, 2.0);
        Assert.Equal(1.0, huray.Factor(1e3, sigma), 3);
        Assert.Equal(1 + 1.5 * 2.0, huray.Factor(1e16, sigma), 2);
        // δ = a: 1 + 1.5·S/2.5.
        double fRadius = 1 / (Math.PI * 4e-7 * Math.PI * sigma * 0.25e-12);
        Assert.Equal(1 + 1.5 * 2.0 / 2.5, huray.Factor(fRadius, sigma), 9);
        double previous = 1;
        foreach (double f in new[] { 1e6, 1e7, 1e8, 1e9, 1e10, 1e11 })
        {
            Assert.True(huray.Factor(f, sigma) >= previous);
            previous = huray.Factor(f, sigma);
        }

        var spec = new LineSpec { WidthMeters = 0.2e-3, HeightMeters = 0.1e-3, FrequencyHz = 10e9 };
        var smooth = ImpedanceCalculator.Solve(spec).Modes[0];
        var rough = ImpedanceCalculator.Solve(spec with
        {
            Model = RlgcModel.Board with { Roughness = hammerstad }
        }).Modes[0];
        double k = hammerstad.Factor(10e9, sigma);
        double ratio = rough.ConductorLossDbPerMeter / smooth.ConductorLossDbPerMeter;
        _output.WriteLine($"10 GHz, 1 µm RMS: factor {k:g4}, conductor loss {smooth.ConductorLossDbPerMeter:g4} → " +
                          $"{rough.ConductorLossDbPerMeter:g4} dB/m (×{ratio:g4})");
        Assert.InRange(ratio, 1 + 0.8 * (k - 1), k);
        Assert.Equal(smooth.DielectricLossDbPerMeter, rough.DielectricLossDbPerMeter, 0.01 * smooth.DielectricLossDbPerMeter);
    }

    // ------------------------------------------------------------------ the rest of the spec

    [Fact]
    public void AnEtchedTrace_IsTheRectangleOfItsMeanWidth()
    {
        var spec = new LineSpec { WidthMeters = 0.20e-3, TopWidthMeters = 0.17e-3, HeightMeters = 0.12e-3 };
        var etched = ImpedanceCalculator.Solve(spec);
        var mean = ImpedanceCalculator.Solve(spec with { WidthMeters = 0.185e-3, TopWidthMeters = null });
        var drawn = ImpedanceCalculator.Solve(spec with { TopWidthMeters = null });
        Assert.Equal(mean.ImpedanceOhms, etched.ImpedanceOhms, 1e-9 * mean.ImpedanceOhms);
        Assert.True(etched.ImpedanceOhms > drawn.ImpedanceOhms);
        Assert.Contains(etched.Assumptions, a => a.Contains("mean width"));

        // A pair keeps its pitch: the gap between the solved strips grows by the difference.
        var pair = ImpedanceCalculator.Solve(spec with { PairGapMeters = 0.15e-3 });
        var traces = pair.Section.Traces;
        Assert.Equal(0.35e-3, traces[1].CenterMeters - traces[0].CenterMeters, 1e-12);
        Assert.Equal(0.185e-3, traces[0].WidthMeters, 1e-12);
    }

    [Fact]
    public void ASolderMaskCover_LowersTheImpedance_AndTheBoardModelReportsLoss()
    {
        var bare = ImpedanceCalculator.Solve(new LineSpec { WidthMeters = 0.3e-3, HeightMeters = 0.2e-3 });
        var masked = ImpedanceCalculator.Solve(new LineSpec
        {
            Structure = LineStructure.EmbeddedMicrostrip, WidthMeters = 0.3e-3, HeightMeters = 0.2e-3,
            UpperHeightMeters = 60e-6, UpperRelativePermittivity = 3.5, UpperLossTangent = 0.025
        });
        _output.WriteLine(string.Join(Environment.NewLine, bare.Describe()));
        _output.WriteLine(string.Join(Environment.NewLine, masked.Describe()));
        Assert.InRange(masked.ImpedanceOhms, 0.9 * bare.ImpedanceOhms, 0.995 * bare.ImpedanceOhms);
        Assert.True(masked.Modes[0].EffectivePermittivity > bare.Modes[0].EffectivePermittivity);
        Assert.True(bare.Modes[0].ConductorLossDbPerMeter > 0 && bare.Modes[0].DielectricLossDbPerMeter > 0);
        Assert.Null(bare.DifferentialOhms);
        Assert.Null(bare.NearEndCoupling);
    }

    [Fact]
    public void WhatCannotBeSolved_IsRefusedByName()
    {
        Assert.Throws<ArgumentException>(() => ImpedanceCalculator.Solve(
            new LineSpec { WidthMeters = 0, HeightMeters = 0.2e-3 }));
        var stripline = Assert.Throws<ArgumentException>(() => ImpedanceCalculator.Solve(
            new LineSpec { Structure = LineStructure.Stripline, WidthMeters = 0.2e-3, HeightMeters = 0.2e-3 }));
        Assert.Contains("upper plane", stripline.Message);
        Assert.Throws<ArgumentException>(() => ImpedanceCalculator.Solve(
            new LineSpec { WidthMeters = 0.2e-3, HeightMeters = 0.2e-3, PairGapMeters = 0 }));
    }
}
