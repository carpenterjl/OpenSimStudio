using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Network;
using OpenSim.Rf.Si;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// FU-30: the layered kernels between two PEC planes — a stripline's strip. Each piece is gated
/// against something that does not share its arithmetic: the spectral kernels against the
/// closed-form Green's functions of the plate pair, the parallel-plate modes against their
/// closed-form wavenumbers, and the spatial table (images + Sommerfeld remainder + mode terms)
/// against the mode sum of the same Green's function, which converges where the image series
/// does not and so is an independent representation of it.
/// </summary>
public class ShieldedKernelTests
{
    private const double C0 = 299792458.0;
    private const double Mu0 = 4e-7 * Math.PI;
    private readonly ITestOutputHelper _output;
    public ShieldedKernelTests(ITestOutputHelper output) => _output = output;

    private static LayeredStackup Homogeneous(double epsR, double below, double above) =>
        new(new[] { new LayeredStackup.Layer(epsR, 0, below), new LayeredStackup.Layer(epsR, 0, above) });

    [Fact]
    public void TheSpectralKernel_IsThePlatePairsDirichletGreensFunction()
    {
        // One dielectric, the strip off-centre: G̃_A = 2µ₀·sin(k_z z)·sin(k_z(H − z))/(k_z sin k_zH)
        // (the 1D Dirichlet Green's function, ×2 for the Sommerfeld normalization the open stack
        // uses, where free space is µ₀/(jk_z)); and with no contrast there is no A_z, so
        // K̃_Φ·ε₀εr = G̃_A/µ₀. Propagating, evanescent and complex k_ρ alike.
        double epsR = 3.2, below = 0.5e-3, above = 1.1e-3, height = below + above;
        var stackup = Homogeneous(epsR, below, above);
        double k0 = 2 * Math.PI * 5e9 / C0, k = k0 * Math.Sqrt(epsR);
        foreach (Complex kRho in new Complex[] { 0.3 * k, 0.97 * k, 1.4 * k, 25 / height, new(0.8 * k, -0.2 * k) })
        {
            var (ga, kPhi) = TransmissionLineGreens.EvaluateShielded(stackup, k0, kRho, 0);
            Complex kz = Complex.Sqrt(k * k - kRho * kRho);
            Complex exact = 2 * Mu0 * Complex.Sin(kz * below) * Complex.Sin(kz * above) / (kz * Complex.Sin(kz * height));
            Assert.True((ga - exact).Magnitude < 1e-10 * exact.Magnitude, $"G̃_A at k_ρ = {kRho}: {ga} against {exact}");
            Complex phiScaled = kPhi * RfConstants.Eps0 * epsR;
            Assert.True((phiScaled - ga / Mu0).Magnitude < 1e-10 * (ga / Mu0).Magnitude, $"K̃_Φ at k_ρ = {kRho}");
        }
    }

    [Fact]
    public void BetweenTwoDielectrics_TheChargeKernel_IsTheElectrostaticOne()
    {
        // The TM part: two different dielectrics either side of the strip. At a frequency where
        // k ≪ k_ρ the charge kernel is the electrostatic one of a charge on the interface of two
        // grounded layers, K̃_Φ = 2/(ε₀ k_ρ (ε₁ coth k_ρh₁ + ε₂ coth k_ρh₂)) — a different
        // derivation (two capacitive half-lines in parallel) from the TE/TM boundary-value solve.
        double e1 = 4.4, e2 = 2.2, h1 = 0.4e-3, h2 = 0.9e-3;
        var stackup = new LayeredStackup(new[] { new LayeredStackup.Layer(e1, 0, h1), new LayeredStackup.Layer(e2, 0, h2) });
        double k0 = 2 * Math.PI * 1e5 / C0;
        foreach (double kRho in new[] { 0.2 / h1, 1 / h1, 5 / h1, 40 / h1 })
        {
            var (_, kPhi) = TransmissionLineGreens.EvaluateShielded(stackup, k0, kRho, 0);
            double exact = 2 / (RfConstants.Eps0 * kRho * (e1 / Math.Tanh(kRho * h1) + e2 / Math.Tanh(kRho * h2)));
            _output.WriteLine($"k_ρh₁ = {kRho * h1:g3}: K̃_Φ {kPhi.Real:e6} {kPhi.Imaginary:+0.0e0;-0.0e0}j against {exact:e6}");
            Assert.True((kPhi - exact).Magnitude < 1e-8 * exact, $"k_ρ = {kRho}");
        }
    }

    [Fact]
    public void TheModes_AreTheParallelPlateModes()
    {
        // One dielectric: TEM at k, then TE_n and TM_n together at √(k² − (nπ/H)²) — one pole
        // each, the TE/TM twins merged. 70 GHz in εr 9.8 over 1.6 mm: n = 1 and 2 propagate.
        double epsR = 9.8, height = 1.6e-3;
        var stackup = Homogeneous(epsR, 0.6e-3, 1.0e-3);
        double k0 = 2 * Math.PI * 70e9 / C0, k = k0 * Math.Sqrt(epsR);
        var poles = SurfaceWavePoles.FindShielded(stackup, k0, 0);
        var expected = new List<double> { k };
        for (int n = 1; n * Math.PI / height < k; n++) expected.Add(Math.Sqrt(k * k - Math.Pow(n * Math.PI / height, 2)));
        _output.WriteLine($"modes: {string.Join(", ", poles.Select(p => (p.KRho.Real / k).ToString("f6")))} k");
        Assert.Equal(expected.Count, poles.Count);
        foreach (double kp in expected)
            Assert.Contains(poles, p => Math.Abs(p.KRho.Real - kp) < 1e-9 * k && Math.Abs(p.KRho.Imaginary) < 1e-12 * k);
        // A horizontal current does not couple to the TEM mode of one dielectric (the mode has no
        // horizontal E): both residues vanish there. The others, from the contour, against the
        // direct limit (k_ρ − k_p)·G̃_A taken on either side of the pole.
        var tem = poles.Single(p => Math.Abs(p.KRho.Real - k) < 1e-9 * k);
        double largestA = poles.Max(p => p.ResidueA.Magnitude), largestPhi = poles.Max(p => p.ResiduePhi.Magnitude);
        Assert.True(tem.ResidueA.Magnitude < 1e-9 * largestA);
        Assert.True(tem.ResiduePhi.Magnitude < 1e-9 * largestPhi);
        foreach (var pole in poles.Where(p => p != tem))
        {
            // (k_ρ − k_p)·G̃_A at k_ρ = k_p(1 ± 10⁻⁷), averaged: the residue to O(10⁻¹⁴).
            Complex Limit(double side)
            {
                Complex at = pole.KRho * (1 + side * 1e-7);
                return (at - pole.KRho) * TransmissionLineGreens.EvaluateShielded(stackup, k0, at, 0).GA;
            }
            Complex direct = 0.5 * (Limit(1) + Limit(-1));
            Assert.True((pole.ResidueA - direct).Magnitude < 1e-6 * direct.Magnitude,
                $"residue at {pole.KRho.Real / k:f4} k: {pole.ResidueA} against {direct}");
        }
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(1.0)]
    [InlineData(4.0)]
    public void TheSpatialTable_IsTheModeSum(double rhoOverHeight)
    {
        // The table builds G_A and K_Φ from the two-plate images, the Sommerfeld remainder and the
        // TEM pole term. The same Green's function as a sum of plate modes,
        //   G_A(ρ) = µ₀ Σₙ (2/H) sin²(nπz/H) · (−j/4) H₀⁽²⁾(k_ρn ρ),  k_ρn = √(k² − (nπ/H)²),
        // with every mode evanescent here past the TEM — whose term is K₀(γₙρ)/2π, K₀ by its
        // integral K₀(x) = ∫₀^∞ e^{−x cosh t} dt — and K_Φ = G_A/(µ₀ε₀εr) in one dielectric.
        double epsR = 4.4, below = 0.7e-3, above = 0.9e-3, height = below + above;
        var stackup = Homogeneous(epsR, below, above);
        double f = 3e9, k0 = 2 * Math.PI * f / C0, k = k0 * Math.Sqrt(epsR);
        double rho = rhoOverHeight * height;
        var table = new MultiLayerKernelTable(stackup, f, rhoMax: 20 * height, sourceInterface: 0, shielded: true);
        var (ga, kPhi) = table.EvaluateKernels(rho);

        // n = 0 has sin² = 0 for A_x: the TEM mode is in K_Φ's charge, not in A_x — but A_x is
        // the Dirichlet function, and its modes are n ≥ 1 only.
        Complex modeSum = Complex.Zero;
        for (int n = 1; n < 4000; n++)
        {
            double cut = n * Math.PI / height;
            double weight = 2 / height * Math.Pow(Math.Sin(cut * below), 2);
            double gamma = Math.Sqrt(cut * cut - k * k);
            double term = weight * K0(gamma * rho) / (2 * Math.PI);
            modeSum += term;
            if (gamma * rho > 60) break;
        }
        Complex exactGa = Mu0 * modeSum;
        Complex exactPhi = modeSum / (RfConstants.Eps0 * epsR);
        _output.WriteLine($"ρ = {rhoOverHeight} H: G_A {ga.Real:e6} {ga.Imaginary:+0.000e0;-0.000e0}j against {exactGa.Real:e6}; " +
                          $"K_Φ {kPhi.Real:e6} {kPhi.Imaginary:+0.000e0;-0.000e0}j against {exactPhi.Real:e6}");
        Assert.True((ga - exactGa).Magnitude < 1e-4 * exactGa.Magnitude, "G_A");
        Assert.True((kPhi - exactPhi).Magnitude < 1e-4 * exactPhi.Magnitude, "K_Φ");
    }

    [Fact]
    public void FarFromTheStrip_NothingPropagates()
    {
        // In one dielectric a horizontal current excites no TEM, and every other mode is cut off
        // at 3 GHz in 1.6 mm: the kernels fall as e^{−πρ/H}. Fifteen and thirty plate spacings
        // out the mode sum is below 10⁻²⁰ of its value at one spacing; the table must be down at
        // its own noise, eight orders under, not carrying a spurious propagating wave.
        double epsR = 4.4, height = 1.6e-3;
        var table = new MultiLayerKernelTable(Homogeneous(epsR, 0.7e-3, 0.9e-3), 3e9, rhoMax: 40 * height,
            sourceInterface: 0, shielded: true);
        var (gaNear, phiNear) = table.EvaluateKernels(height);
        foreach (double far in new[] { 15.0, 30.0 })
        {
            var (ga, phi) = table.EvaluateKernels(far * height);
            _output.WriteLine($"ρ = {far} H: |G_A| {ga.Magnitude / gaNear.Magnitude:e2}, |K_Φ| {phi.Magnitude / phiNear.Magnitude:e2} of their values at H");
            Assert.True(ga.Magnitude < 1e-8 * gaNear.Magnitude);
            Assert.True(phi.Magnitude < 1e-8 * phiNear.Magnitude);
        }
    }

    /// <summary>A flat strip from x = 0 to <paramref name="length"/> + 2·<paramref name="stub"/>,
    /// with a gap port across it <paramref name="stub"/> in from each end — the ports
    /// <paramref name="length"/> apart.</summary>
    private static (SurfaceStructure Structure, SurfacePort[] Ports) Strip(double length, double stub,
        double width, double cellAlong, int cellsAcross, bool graded = false)
    {
        int nx = (int)Math.Round((length + 2 * stub) / cellAlong), ny = cellsAcross;
        double total = length + 2 * stub;
        var vertices = new List<Vector3D>();
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= ny; j++)
                vertices.Add(new Vector3D(total * i / nx,
                    graded ? -width / 2 * Math.Cos(Math.PI * j / ny) : -width / 2 + width * j / ny, 0));
        int At(int i, int j) => i * (ny + 1) + j;
        var triangles = new List<(int, int, int)>();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                triangles.Add((At(i, j), At(i + 1, j), At(i, j + 1)));
                triangles.Add((At(i + 1, j), At(i + 1, j + 1), At(i, j + 1)));
            }
        var structure = new SurfaceStructure(vertices, triangles, null);
        // Each port's current counts positive flowing INTO the line between them: +x at the
        // first, −x at the second.
        SurfacePort Gap(double x, double direction) => new(
            Enumerable.Range(0, structure.BasisCount).Where(e => structure.Edges[e].MinusTriangle >= 0
                && Math.Abs(vertices[structure.Edges[e].V1].X - x) < 1e-9
                && Math.Abs(vertices[structure.Edges[e].V2].X - x) < 1e-9).ToList(),
            new Vector3D(direction, 0, 0));
        var ports = new[] { Gap(stub, 1), Gap(stub + length, -1) };
        Assert.Equal(ny, ports[0].EdgeBases.Count);
        return (structure, ports);
    }

    [Theory]
    [InlineData(4.4, 4.4)]
    [InlineData(4.4, 2.5)]
    public void AStripBetweenTwoPlanes_HasTheImpedanceOfItsCrossSection(double epsBelow, double epsAbove)
    {
        // FU-30, end to end: a 0.5 mm strip midway between planes 1.6 mm apart, solved by the
        // MoM on the shielded kernel at 3 GHz between gap ports, two lengths apart; the
        // calibration takes the ports off and leaves the line, whose Z₀ and effective
        // permittivity the 2D cross-section solver gives independently. In one dielectric the
        // line is TEM and β = k₀√εr exactly; with two it is quasi-TEM, which at a board's
        // thickness against the wavelength the 2D (quasi-static) answer still is.
        //
        // The ports: a gap 3 mm in from each end. The calibration's port model is exact only for a
        // lumped L, and a short stub is a large series reactance (−1220 Ω at 0.25 mm) that multiplies
        // any departure from it — measured in one dielectric with four graded cells across:
        // Z₀ 73.8 Ω with a 0.25 mm stub, 64.5 at 0.5, 61.4 at 1.5, 61.3 at 3 (series −136 Ω), against
        // the exact zero-thickness 60.57 Ω (Cohn's conformal map) and the 2D solver's 60.48. The
        // remaining 1.3 % is the mesh across the strip, as εeff's 0.3 % is.
        double f = 3e9, k0 = 2 * Math.PI * f / C0, w = 0.5e-3, below = 0.8e-3, above = 0.8e-3, reference = 50;
        var stackup = new LayeredStackup(new[]
        {
            new LayeredStackup.Layer(epsBelow, 0, below), new LayeredStackup.Layer(epsAbove, 0, above)
        });
        var table = new MultiLayerKernelTable(stackup, f, rhoMax: 0.045, sourceInterface: 0, shielded: true);
        var solver = new SurfaceMomSolver();
        Complex[,] S(double length)
        {
            var (structure, ports) = Strip(length, 3e-3, w, 0.25e-3, 4, graded: true);
            return NetworkChecks.AdmittanceToScattering(solver.SolveMultiPort(structure, table, ports).Admittance, reference);
        }
        var sShort = S(20e-3);
        var sLong = S(30e-3);
        var check = NetworkChecks.Check(f, sShort);
        double line = ImpedanceCalculator.Solve(new LineSpec
        {
            Structure = LineStructure.Stripline, WidthMeters = w, ThicknessMeters = 1e-6,
            HeightMeters = below, RelativePermittivity = epsBelow, LossTangent = 0,
            UpperHeightMeters = above, UpperRelativePermittivity = epsAbove, UpperLossTangent = 0, FrequencyHz = f
        }) is var report ? report.ImpedanceOhms : 0;
        double effective = report.Modes[0].EffectivePermittivity;
        var calibration = LineFromTwoLengths.Calibrate(sShort, sLong, 20e-3, 30e-3, reference, k0 * Math.Sqrt(effective),
            PortModel.ShuntAtTerminals);
        double eeff = Math.Pow(calibration.Gamma.Imaginary / k0, 2);
        _output.WriteLine($"Z0 = {calibration.CharacteristicOhms.Real:f2} {calibration.CharacteristicOhms.Imaginary:+0.00;−0.00}j Ω " +
                          $"against the 2D {line:f2} Ω; εeff {eeff:f4} against {effective:f4}; α {calibration.Gamma.Real:e2} Np/m; " +
                          $"reciprocity {check.ReciprocityError:e1}, not returned {check.AbsorbedFraction[0]:e1}");
        // Between two planes nothing radiates. In one dielectric a horizontal current cannot launch
        // the parallel-plate mode either, so a lossless strip returns all the power. With two it
        // can: every discontinuity (the gaps, the open ends) sheds some into that mode, which
        // runs off between the planes — 2.9 % here, the strip's own mode staying bound (εeff 3.48
        // against the plate mode's 3.19). That coupling between the ports through the plate mode
        // is also what the two-length calibration cannot separate, and shows as its small α.
        Assert.True(check.ReciprocityError < 1e-8);
        if (epsBelow == epsAbove) Assert.InRange(check.AbsorbedFraction[0], -1e-3, 1e-3);
        else Assert.InRange(check.AbsorbedFraction[0], 0, 0.05);
        Assert.Equal(effective, eeff, 0.01 * effective);
        Assert.Equal(line, calibration.CharacteristicOhms.Real, 0.03 * line);
        if (epsBelow == epsAbove) Assert.Equal(epsBelow, eeff, 0.01 * epsBelow);
    }

    /// <summary>K₀(x) = ∫₀^∞ e^{−x cosh t} dt by the trapezoidal rule, which converges
    /// geometrically for this doubly-exponentially decaying integrand.</summary>
    private static double K0(double x)
    {
        double end = Math.Acosh(Math.Max(1, 700 / x)) + 1, h = end / 4000, sum = 0.5 * Math.Exp(-x);
        for (int i = 1; i <= 4000; i++) sum += Math.Exp(-x * Math.Cosh(i * h));
        return sum * h;
    }
}
