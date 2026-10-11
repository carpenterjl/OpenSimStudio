using System.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using Xunit;
using Xunit.Abstractions;
using Layer = OpenSim.Rf.Layered.LayeredStackup.Layer;

namespace OpenSim.Tests.Rf;

/// <summary>
/// FU-37: the lossy ground as an impedance (Leontovich) boundary inside the layered Green's
/// function. The oracle is the same stack with the ground as what it physically is — a metal layer
/// (ε = 1 − jσ/ωε₀) twenty skin depths thick on the PEC — solved by the unchanged transmission-line
/// machinery, which knows nothing of surface impedances. The impedance ground must reproduce the
/// metal layer's departure from the perfect ground. Leontovich's own error is the
/// k_ρ-dependence of the metal's vertical wavenumber it drops: (k_ρ/k_c)² = (k_ρδ)²/2, 4.5e-4 at
/// the largest k_ρ asked here (20 k₀ at 2 GHz with δ = 36 µm), and the metal's e^{−20} leak to
/// the PEC under it is 2e-9.
/// </summary>
public class GroundLossKernelTests
{
    private const double Sigma = 1e5, F = 2e9;
    private static readonly double Omega = 2 * Math.PI * F;
    private static readonly double K0 = Omega / RfConstants.SpeedOfLight;
    private static readonly double SkinDepth = 1 / Math.Sqrt(Math.PI * F * RfConstants.Mu0 * Sigma);
    private static readonly double MetalThickness = 20 * SkinDepth;

    /// <summary>(k_ρ δ)²/2 at 20 k₀ is 4.5e-4; the band is four times that.</summary>
    private const double Tolerance = 2e-3;

    private readonly ITestOutputHelper _out;
    public GroundLossKernelTests(ITestOutputHelper output) => _out = output;

    private static LayeredStackup Pec(params Layer[] layers) => new(layers);
    private static LayeredStackup Impedance(params Layer[] layers) => new(layers) { GroundConductivitySiemensPerMeter = Sigma };
    private static LayeredStackup Metal(params Layer[] layers) =>
        new(new[] { new Layer(1, Sigma / (Omega * RfConstants.Eps0), MetalThickness) }.Concat(layers).ToArray());

    private static readonly Complex[] Points =
    {
        0.3 * K0, 0.9 * K0, 1.2 * K0, 1.45 * K0, 2 * K0, 5 * K0, 20 * K0,
        new Complex(1.2, -0.05) * K0, new Complex(3, -0.5) * K0,
    };

    /// <summary>The departures from the PEC agree: |Δ_impedance − Δ_metal| / |Δ_metal|, and the
    /// departure is not lost in rounding (|Δ_metal| / |PEC| above 1e-6).</summary>
    private void Agree(string what, Complex pec, Complex impedance, Complex metal)
    {
        Complex dI = impedance - pec, dM = metal - pec;
        double error = (dI - dM).Magnitude / dM.Magnitude;
        _out.WriteLine($"{what}: departure {dM.Magnitude / pec.Magnitude:e2} of the PEC value, impedance vs metal {error:e2}");
        Assert.True(dM.Magnitude > 1e-6 * pec.Magnitude, $"{what}: the ground's effect is lost in rounding");
        Assert.InRange(error, 0, Tolerance);
    }

    [Fact]
    public void TopSourceKernels_MatchAMetalGroundLayer()
    {
        var layers = new[] { new Layer(2.2, 0, 1e-3) };
        foreach (var kRho in Points)
        {
            var pec = TransmissionLineGreens.Evaluate(Pec(layers), K0, kRho);
            var imp = TransmissionLineGreens.Evaluate(Impedance(layers), K0, kRho);
            var metal = TransmissionLineGreens.Evaluate(Metal(layers), K0, kRho);
            Agree($"G_A at {kRho / K0:f2} k0", pec.GA, imp.GA, metal.GA);
            if (kRho.Magnitude <= PhiReach) Agree($"K_Phi at {kRho / K0:f2} k0", pec.KPhi, imp.KPhi, metal.KPhi);
        }
    }

    /// <summary>Beyond 5 k₀, K_Φ's departure from the PEC is the near-cancelling difference of its TE
    /// and TM parts (6e-6 of the kernel at 20 k₀) and the comparison measures Leontovich's own
    /// (k_ρδ)² error magnified by that cancellation, not the rows.</summary>
    private static readonly double PhiReach = 5.01 * K0;

    [Fact]
    public void CoveredSourceKernels_MatchAMetalGroundLayer()
    {
        var layers = new[] { new Layer(2.2, 0, 1e-3), new Layer(3.0, 0, 0.5e-3) };
        foreach (var kRho in Points)
        {
            var kz0 = SpectralKernels.Kz(K0 * K0, kRho);
            var pec = TransmissionLineGreens.EvaluateInterior(Pec(layers), K0, kRho, kz0, 0);
            var imp = TransmissionLineGreens.EvaluateInterior(Impedance(layers), K0, kRho, kz0, 0);
            var metal = TransmissionLineGreens.EvaluateInterior(Metal(layers), K0, kRho, kz0, 1);
            Agree($"G_A at {kRho / K0:f2} k0", pec.GA, imp.GA, metal.GA);
            if (kRho.Magnitude <= PhiReach) Agree($"K_Phi at {kRho / K0:f2} k0", pec.KPhi, imp.KPhi, metal.KPhi);
        }
    }

    [Theory]
    [InlineData(0.7e-3, 0.3e-3)]
    [InlineData(0.3e-3, 0.3e-3)]
    [InlineData(0.6e-3, 0.05e-3)]
    [InlineData(0.05e-3, 0.8e-3)]
    public void VerticalKernels_MatchAMetalGroundLayer(double z, double zPrime)
    {
        // A probe's kernels: the vertical TM solve, the horizontal auxiliary problem and the
        // source-height derivative, whose ground shunt is the separability term.
        var layers = new[] { new Layer(2.2, 0, 1e-3) };
        foreach (var kRho in Points.Where(k => k.Imaginary == 0 && k.Real < 10 * K0))
        {
            var kz0 = SpectralKernels.Kz(K0 * K0, kRho);
            var pec = TransmissionLineGreens.EvaluateVertical(Pec(layers), K0, kRho, kz0, z, zPrime);
            var imp = TransmissionLineGreens.EvaluateVertical(Impedance(layers), K0, kRho, kz0, z, zPrime);
            var metal = TransmissionLineGreens.EvaluateVertical(Metal(layers), K0, kRho, kz0,
                z + MetalThickness, zPrime + MetalThickness);
            Agree($"G_zz at {kRho.Real / K0:f2} k0", pec.GAzz, imp.GAzz, metal.GAzz);
            Agree($"G_xz at {kRho.Real / K0:f2} k0", pec.GAxz, imp.GAxz, metal.GAxz);
            Agree($"K_Phi at {kRho.Real / K0:f2} k0", pec.KPhi, imp.KPhi, metal.KPhi);
        }
    }

    [Fact]
    public void ThePoles_LeaveTheAxisWhereTheMetalLayerPutsThem()
    {
        // Thick enough for TE1 as well as TM0 (k₀h√(εr − 1) = 1.9 > π/2): a mode of each family.
        var layers = new[] { new Layer(10.2, 0, 15e-3) };
        var pecPoles = SurfaceWavePoles.Find(Pec(layers), K0);
        var poles = SurfaceWavePoles.Find(Impedance(layers), K0);
        Assert.Equal(pecPoles.Count, poles.Count);
        Assert.Contains(poles, p => p.IsTm);
        Assert.Contains(poles, p => !p.IsTm);
        var metal = Metal(layers);
        foreach (var pole in poles)
        {
            var pec = pecPoles.Where(p => p.IsTm == pole.IsTm).MinBy(p => (p.KRho - pole.KRho).Magnitude);
            // Newton on the metal stack's own characteristic function, from the impedance pole.
            Complex kp = pole.KRho;
            for (int i = 0; i < 40; i++)
            {
                Complex h = 1e-7 * K0;
                Complex d = SurfaceWaveDispersion.Dispersion(metal, K0, kp, pole.IsTm);
                Complex slope = (SurfaceWaveDispersion.Dispersion(metal, K0, kp + h, pole.IsTm)
                    - SurfaceWaveDispersion.Dispersion(metal, K0, kp - h, pole.IsTm)) / (2 * h);
                kp -= d / slope;
            }
            double shift = (pole.KRho - pec.KRho).Magnitude;
            double apart = (pole.KRho - kp).Magnitude;
            _out.WriteLine($"{(pole.IsTm ? "TM" : "TE")}: PEC {pec.KRho / K0:f8}, impedance {pole.KRho / K0:f8}, " +
                           $"metal {kp / K0:f8} (k0); the loss moved it {shift / K0:e2} k0, the two grounds differ by {apart / shift:e2} of that");
            Assert.True(pole.KRho.Imaginary < -1e-9 * K0, "the pole must leave the real axis into the decaying half-plane");
            Assert.InRange(apart / shift, 0, Tolerance);

            var (resA, resPhi) = TransmissionLineGreens.PoleResidues(metal, K0, kp, pole.IsTm);
            double residueError = ((pole.IsTm ? pole.ResiduePhi : pole.ResidueA) - (pole.IsTm ? resPhi : resA)).Magnitude
                                  / (pole.IsTm ? resPhi : resA).Magnitude;
            _out.WriteLine($"  residue {(pole.IsTm ? "K_Phi" : "G_A")}: impedance vs metal {residueError:e2}");
            Assert.InRange(residueError, 0, Tolerance);
        }
    }

    [Fact]
    public void AVeryGoodConductor_IsThePecGround()
    {
        // Z_s ∝ σ^(−1/2): at σ = 1e40 S/m the rows differ from the PEC's in the 1e-20s.
        var layers = new[] { new Layer(4.4, 0.02, 1.6e-3), new Layer(3.0, 0, 0.2e-3) };
        var pec = Pec(layers);
        var nearly = new LayeredStackup(layers) { GroundConductivitySiemensPerMeter = 1e40 };
        foreach (var kRho in Points)
        {
            var a = TransmissionLineGreens.Evaluate(pec, K0, kRho);
            var b = TransmissionLineGreens.Evaluate(nearly, K0, kRho);
            Assert.True((a.GA - b.GA).Magnitude <= 1e-12 * a.GA.Magnitude);
            Assert.True((a.KPhi - b.KPhi).Magnitude <= 1e-12 * a.KPhi.Magnitude);
        }
    }

    [Fact]
    public void TheGround_TravelsWithEveryDerivedStackup()
    {
        var stack = new LayeredStackup(new[] { new Layer(4.4, 0, 1.6e-3) })
        {
            GroundConductivitySiemensPerMeter = 5.8e7, GroundThicknessMeters = 35e-6
        };
        var (split, _) = stack.SplitAt(0.5e-3);
        Assert.Equal(stack.GroundConductivitySiemensPerMeter, split.GroundConductivitySiemensPerMeter);
        Assert.Equal(stack.GroundThicknessMeters, split.GroundThicknessMeters);
        Assert.Equal(stack.GroundSurfaceImpedance(Omega), split.GroundSurfaceImpedance(Omega));
        var half = SurfaceWaveDispersion.WithLossFraction(stack, 0.5);
        Assert.Equal(0.5 * stack.GroundSurfaceImpedance(Omega), half.GroundSurfaceImpedance(Omega));
        Assert.False(SurfaceWaveDispersion.WithLossFraction(stack, 0).HasLossyGround);
        Assert.False(stack.IsSingleSlab);
        // A thin ground at low frequency is its DC sheet resistance.
        double dc = 1 / (5.8e7 * 35e-6);
        Assert.InRange(stack.GroundSurfaceImpedance(2 * Math.PI * 1e3).Real / dc, 0.999, 1.001);
    }
}
