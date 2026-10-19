using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Network;
using OpenSim.Rf.Surface;
using OpenSim.Tests.Si;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// FU-14: Rautio's stripline standard (J. C. Rautio, "An ultra-high precision benchmark for
/// validation of planar electromagnetic analyses", IEEE Trans. MTT 42(11), pp. 2046–2050, Nov.
/// 1994), read from Sonnet's scanned copy of the paper, with Sonnet's Benchmarking pages ch3 and
/// ch7 for the reporting form and the zero-length through: a zero-thickness lossless strip centred
/// between two planes b = 1.0 mm apart in air, W = 1.4423896 mm (the paper's Table I width for
/// 50 Ω), L = 4.99654097 mm (a quarter wavelength at 15 GHz), analysed at 15 GHz. The exact answer
/// is a perfect 50 Ω quarter-wave line, |S11| = 0 and ∠S21 = −90°; the error metric (paper Sec. II,
/// to first order Sonnet's form E% = 100·|S11| + |90 + ∠S21|/0.9) adds the impedance and velocity
/// errors, and the paper bounds a rooftop analysis's residual error by
/// e_T ≤ 16/N_W + 2·(16/N_L)² % (N_W cells across the width, N_L per wavelength; N_W > 3,
/// N_L > 15). Material behind the numbers: OpenSim.Tests/References/FU-14.
/// </summary>
public class StriplineStandardTests
{
    private const double C0 = 299792458.0;
    private const double Mu0 = 4e-7 * Math.PI;
    private const double B = 1.0e-3, W = 1.4423896e-3, L = 4.99654097e-3, F = 15e9;
    private readonly ITestOutputHelper _output;
    public StriplineStandardTests(ITestOutputHelper output) => _output = output;

    private static LayeredStackup Air() =>
        new(new[] { new LayeredStackup.Layer(1, 0, B / 2), new LayeredStackup.Layer(1, 0, B / 2) });

    [Fact]
    public void TheStandardsDimensions_AreFiftyOhmsAndAQuarterWave_ToTheirPrintedDigits()
    {
        // Table I as printed, through the paper's own expression (ExactLines), η₀ = µ₀c: each width
        // is the exact one correctly rounded (the 40- and 80-digit check in References/FU-14 puts
        // the printed widths 3.6e-8, 9.0e-9 and 4.0e-9 mm off the exact ones).
        Assert.Equal(25, ExactLines.StriplineZ0(3.3260319e-3, B, 1), 1e-6);
        Assert.Equal(50, ExactLines.StriplineZ0(W, B, 1), 1e-6);
        Assert.Equal(100, ExactLines.StriplineZ0(0.50396767e-3, B, 1), 1e-6);
        // The often-quoted 30π for η₀/4 is 0.07 % off (the paper's own note): with it the 50 Ω
        // width reads 50.035 Ω.
        Assert.Equal(6.9229e-4, 30 * Math.PI / ExactLines.QuarterEta0 - 1, 1e-7);
        // The length is a quarter wave at 15 GHz to its printed digits.
        Assert.Equal(90, 360 * L * F / C0, 1e-6);
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(1.0)]
    public void TheAirFilledKernel_IsTheModeSumOfThePlatePair(double rhoOverSpacing)
    {
        // Air between the plates puts the TEM pole exactly at k₀, where the Sommerfeld contour's
        // two segments meet — the case every shielded test so far (εr ≥ 2.2) stepped round. The
        // table against the independent sum over the plate modes (all evanescent at 15 GHz in
        // 1 mm), as ShieldedKernelTests does for a dielectric fill: measured 1.4e-12 at 0.3·b,
        // 9.2e-11 at 1·b.
        double k0 = 2 * Math.PI * F / C0;
        var table = new MultiLayerKernelTable(Air(), F, rhoMax: 0.03, sourceInterface: 0, shielded: true);
        double rho = rhoOverSpacing * B;
        var (ga, kPhi) = table.EvaluateKernels(rho);
        Complex modeSum = Complex.Zero;
        for (int n = 1; n < 4000; n++)
        {
            double cut = n * Math.PI / B;
            double gamma = Math.Sqrt(cut * cut - k0 * k0);
            modeSum += 2 / B * Math.Pow(Math.Sin(cut * B / 2), 2) * ShieldedKernelTests.K0(gamma * rho) / (2 * Math.PI);
            if (gamma * rho > 60) break;
        }
        Assert.True((ga - Mu0 * modeSum).Magnitude < 1e-8 * (Mu0 * modeSum).Magnitude, $"G_A {ga} against {Mu0 * modeSum}");
        Complex phi = kPhi * OpenSim.Rf.RfConstants.Eps0;
        Assert.True((phi - modeSum).Magnitude < 1e-8 * modeSum.Magnitude, $"K_Φ·ε₀ {phi} against {modeSum}");
    }

    /// <summary>A flat strip with gap ports <paramref name="stub"/> in from each end,
    /// <paramref name="length"/> apart; the cells along are as near <paramref name="cellAlong"/> as
    /// divides each of the three stretches (stub, line, stub) exactly, so a port sits on a grid line
    /// whatever the lengths.</summary>
    private static (SurfaceStructure Structure, SurfacePort[] Ports) Strip(double length, double stub,
        double width, double cellAlong, int cellsAcross, bool graded)
    {
        var xs = new List<double> { 0 };
        foreach (double stretch in new[] { stub, length, stub })
        {
            int n = Math.Max(1, (int)Math.Round(stretch / cellAlong));
            double start = xs[^1];
            for (int i = 1; i <= n; i++) xs.Add(start + stretch * i / n);
        }
        int nx = xs.Count - 1, ny = cellsAcross;
        var vertices = new List<Vector3D>();
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= ny; j++)
                vertices.Add(new Vector3D(xs[i], graded ? -width / 2 * Math.Cos(Math.PI * j / ny) : -width / 2 + width * j / ny, 0));
        int At(int i, int j) => i * (ny + 1) + j;
        var triangles = new List<(int, int, int)>();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                triangles.Add((At(i, j), At(i + 1, j), At(i, j + 1)));
                triangles.Add((At(i + 1, j), At(i + 1, j + 1), At(i, j + 1)));
            }
        var structure = new SurfaceStructure(vertices, triangles, null);
        // Each port's current counts positive flowing INTO the line between them.
        SurfacePort Gap(double x, double direction) => new(
            Enumerable.Range(0, structure.BasisCount).Where(e => structure.Edges[e].MinusTriangle >= 0
                && Math.Abs(vertices[structure.Edges[e].V1].X - x) < 1e-12
                && Math.Abs(vertices[structure.Edges[e].V2].X - x) < 1e-12).ToList(),
            new Vector3D(direction, 0, 0));
        var ports = new[] { Gap(stub, 1), Gap(stub + length, -1) };
        Assert.All(ports, p => Assert.Equal(ny, p.EdgeBases.Count));
        return (structure, ports);
    }

    private static double FirstOrderError(Complex[,] s) =>
        100 * s[0, 0].Magnitude + Math.Abs(90 + s[1, 0].Phase * 180 / Math.PI) / 0.9;

    [Theory]
    [InlineData(2, true, 5.9955)]
    [InlineData(4, true, 2.2373)]
    [InlineData(4, false, 3.5384)]
    public void TheShieldedMoment_MeetsTheStandard_WithinRautiosResidualError(int across, bool graded, double measured)
    {
        // The strip between gap ports a quarter wave (5 mm) in from its open ends, so each port's
        // stub is a series short and the port is the lumped shunt the calibration models
        // (PortModel.ShuntAtTerminals; FU-30 measured what a short stub does). Two lines with ports
        // 7.5 and 12.5 mm apart (Δl a quarter wave: the calibration's best conditioning) take the
        // ports off; a third, the standard plus 2.5 mm each side, is then de-embedded to the
        // standard's own planes (the calibration's own Line(L) reads E within 0.001 % of it). Cells
        // along 0.25 mm: N_L = 80, so the paper's longitudinal term is 2·(16/80)² = 0.08 %.
        //
        // Measured E: 6.00 / 2.24 % with 2 / 4 cosine-graded cells across (0.60 % with 8, 262 s in
        // Release, not run here), and 3.54 % with 4 uniform cells (1.94 % with 8) — Sonnet 2.4's
        // own table for 4 and 8 uniform cells reads 3.53 and 1.96 %. Graded cells resolve the edge
        // current and converge faster; every value sits inside the paper's bound.
        double k0 = 2 * Math.PI * F / C0, stub = 5e-3, shift = 2.5e-3, reference = 50, along = 0.25e-3;
        var table = new MultiLayerKernelTable(Air(), F, rhoMax: 0.03, sourceInterface: 0, shielded: true);
        var solver = new SurfaceMomSolver();
        Complex[,] S(double length)
        {
            var (structure, ports) = Strip(length, stub, W, along, across, graded);
            return NetworkChecks.AdmittanceToScattering(solver.SolveMultiPort(structure, table, ports).Admittance, reference);
        }
        double l1 = 7.5e-3, l2 = 12.5e-3, l3 = L + 2 * shift;
        var calibration = LineFromTwoLengths.Calibrate(S(l1), S(l2), l1, l2, reference, k0, PortModel.ShuntAtTerminals);
        var s3 = S(l3);
        var check = NetworkChecks.Check(F, s3);
        var standard = LineFromTwoLengths.AbcdToScattering(calibration.DeEmbed(s3, reference, shift), reference);
        double error = FirstOrderError(standard);
        double eeff = Math.Pow(calibration.Gamma.Imaginary / k0, 2);
        double bound = 16.0 / across + 2 * Math.Pow(16 * along * F / C0, 2);
        // The paper's own metric, the impedance error plus the velocity error: the same reading to
        // first order (they part by 1.7 % of E at 6 %, 0.7 % at 2 %).
        double paper = 100 * Math.Abs(calibration.CharacteristicOhms.Real / ExactLines.StriplineZ0(W, B, 1) - 1)
                       + 100 * Math.Abs(1 / Math.Sqrt(eeff) - 1);
        var through = LineFromTwoLengths.AbcdToScattering(calibration.DeEmbed(s3, reference, l3 / 2), reference);
        _output.WriteLine($"{across} {(graded ? "graded" : "uniform")} cells across: Z0 {calibration.CharacteristicOhms.Real:f4} Ω, " +
                          $"εeff {eeff:f6}; |S11| {standard[0, 0].Magnitude:e3}, ∠S21 {standard[1, 0].Phase * 180 / Math.PI:f4}°, " +
                          $"E {error:f4} % (paper's metric {paper:f4} %, bound {bound:f2} %); reciprocity {check.ReciprocityError:e1}, " +
                          $"not returned {check.AbsorbedFraction[0]:e1}; zero-length through |S11| {through[0, 0].Magnitude:e3}, " +
                          $"|S21| − 1 {through[1, 0].Magnitude - 1:e2}, ∠S21 {through[1, 0].Phase * 180 / Math.PI:e3}°");
        Assert.InRange(paper / error, 0.97, 1.03);
        Assert.True(error <= bound, $"E {error} % above the paper's residual-error bound {bound} %");
        Assert.Equal(measured, error, 0.02);
        // Lossless, reciprocal, and in air nothing leaves between the plates.
        Assert.True(check.ReciprocityError < 1e-12);
        Assert.InRange(check.AbsorbedFraction[0], -1e-6, 1e-6);

        // Sonnet's ch7 zero-length through: the same line de-embedded to zero length must be a
        // perfect through (Sonnet: |S11| 8.1e-8, 5.7e-5° on its 16 × 128 cells). Measured here
        // |S11| 5.5–6.1e-6, |S21| − 1 under 1e-9 and 4.0–5.2e-4° — the port model, not the line, is
        // what it tests.
        Assert.True(through[0, 0].Magnitude < 2e-5, $"zero-length |S11| {through[0, 0].Magnitude:e2}");
        Assert.Equal(1, through[1, 0].Magnitude, 1e-6);
        Assert.True(Math.Abs(through[1, 0].Phase * 180 / Math.PI) < 2e-3, $"zero-length ∠S21 {through[1, 0].Phase * 180 / Math.PI:e2}°");
    }
}
