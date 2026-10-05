using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf;
using OpenSim.Rf.Network;
using OpenSim.Rf.Si;
using OpenSim.Rf.Surface;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// S-parameter results of the sheet solver: where the input power goes when the metal is
/// lossy, a two-port line in air (reciprocity, passivity, and the propagation constant with
/// the ports taken out, which for a TEM line in air is exactly ω/c), the adaptive sweep on a
/// response with a resonance a coarse sweep steps over, and the checks themselves.
/// </summary>
public class NetworkResultsTests
{
    private const double C0 = 299792458.0;
    private readonly ITestOutputHelper _output;
    public NetworkResultsTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------ loss

    [Fact]
    public void CopperSheet_IsTheDcSheetAtLowFrequency_AndTheSkinImpedanceAtHigh()
    {
        double sigma = 5.8e7, t = 35e-6;
        Assert.Equal(1 / (sigma * t), SheetLoss.CopperSheet(1, sigma, t, bothFaces: false).Real, 1e-9);
        Assert.Equal(1 / (sigma * t), SheetLoss.CopperSheet(1, sigma, t, bothFaces: true).Real, 1e-9);
        double f = 10e9, delta = 1 / Math.Sqrt(Math.PI * f * RfConstants.Mu0 * sigma);
        var one = SheetLoss.CopperSheet(f, sigma, t, bothFaces: false);
        var two = SheetLoss.CopperSheet(f, sigma, t, bothFaces: true);
        Assert.Equal(1 / (sigma * delta), one.Real, 1e-6 / (sigma * delta));
        Assert.Equal(one.Real, one.Imaginary, 1e-6 * one.Real);
        Assert.Equal(one.Real / 2, two.Real, 1e-6 * one.Real);
    }

    [Fact]
    public void ALossyStripDipole_PutsItsInputPowerIntoRadiationAndHeat()
    {
        double f = 300e6, lambda = C0 / f, width = lambda / 100, length = lambda / 2;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(width, length, lambda / 40);
        var structure = grid.Structure!;
        Complex sheet = 0.5;                                        // Ω per square, a poor conductor
        var lossless = new SurfaceMomSolver().Solve(structure, f, grid.Port!);
        var lossy = new SurfaceMomSolver { SheetImpedance = _ => sheet }.Solve(structure, f, grid.Port!);

        double input = 0.5 * (Complex.One / lossy.InputImpedance).Real;
        double radiated = SurfaceFarFieldEvaluator.Compute(structure, lossy).TotalRadiatedPowerWatts;
        double heat = SheetLoss.OhmicPower(structure, lossy.EdgeCurrents, sheet);
        double efficiency = radiated / input;
        _output.WriteLine($"Zin {lossless.InputImpedance.Real:f2} + j{lossless.InputImpedance.Imaginary:f2} Ω lossless, " +
                          $"{lossy.InputImpedance.Real:f2} + j{lossy.InputImpedance.Imaginary:f2} Ω at 0.5 Ω/sq");
        _output.WriteLine($"radiated {radiated / input:p2}, heat {heat / input:p2}, together {(radiated + heat) / input:p2}");
        Assert.InRange((radiated + heat) / input, 0.98, 1.02);

        // A sinusoidal current spread evenly over the width would lose R = Z_s·L/(2w) in
        // series with the radiation resistance. The solved current crowds toward the strip's
        // edges, so the loss is at least that.
        double uniform = sheet.Real * length / (2 * width);
        double addedResistance = lossy.InputImpedance.Real - lossless.InputImpedance.Real;
        _output.WriteLine($"resistance added at the feed {addedResistance:f2} Ω; even-current estimate {uniform:f2} Ω");
        Assert.InRange(addedResistance, 0.95 * uniform, 1.6 * uniform);
        Assert.InRange(efficiency, 0.6, 1 / (1 + 0.9 * uniform / lossless.InputImpedance.Real));
    }

    [Fact]
    public void APatchOnALosslessSlabWithLossyMetal_ClosesItsLedgerWithTheConductorHeat()
    {
        // 1 − |S11|² of what is sent arrives; all of that is radiated, launched as surface
        // wave, or turned to heat in the patch. With perfect metal the first two close the
        // ledger on this patch; with a resistive sheet the heat has to make up the rest.
        const double f = 10e9, slab = 1.588e-3;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(1.186e-2, 0.906e-2, 1.4e-3, z: slab, portFraction: 0);
        var structure = grid.Structure!;
        var table = new OpenSim.Rf.Layered.LayeredKernelTable(new OpenSim.Rf.Layered.SubstrateStackup(2.2, 0.0, slab), f, 0.03);
        Complex sheet = 0.2;
        var perfect = new SurfaceMomSolver().Solve(structure, table, grid.Port!);
        var lossy = new SurfaceMomSolver { SheetImpedance = _ => sheet }.Solve(structure, table, grid.Port!);

        double Ledger(SurfaceMomSolution s, double heat, out double input)
        {
            input = 0.5 * (Complex.One / s.InputImpedance).Real;
            double radiated = OpenSim.Rf.Layered.LayeredFarField.Compute(structure, table, s).TotalRadiatedPowerWatts;
            double surfaceWave = OpenSim.Rf.Layered.LayeredFarField.SurfaceWavePowerWatts(structure, table, s);
            return (radiated + surfaceWave + heat) / input;
        }
        double closedPerfect = Ledger(perfect, 0, out _);
        double heatWatts = SheetLoss.OhmicPower(structure, lossy.EdgeCurrents, sheet);
        double closedLossy = Ledger(lossy, heatWatts, out double inputLossy);
        double withoutHeat = Ledger(lossy, 0, out _);
        _output.WriteLine($"perfect metal: P_rad + P_sw = {closedPerfect:p2} of the input; 0.2 Ω/sq: {withoutHeat:p2} without the heat, " +
                          $"{closedLossy:p2} with it (heat {heatWatts / inputLossy:p2})");
        Assert.InRange(closedPerfect, 0.97, 1.03);
        Assert.True(heatWatts / inputLossy > 0.02, "the sheet should be lossy enough to matter in this test");
        // The heat closes the lossy ledger as well as the perfect one closed.
        Assert.Equal(closedPerfect, closedLossy, 0.01);
        Assert.True(withoutHeat < closedPerfect - 0.015);
    }

    [Fact]
    public void OnePortThroughTheMultiPortSolve_IsTheSameImpedance()
    {
        double f = 300e6, lambda = C0 / f;
        var grid = SurfaceMeshBuilder.BuildRectangularPlate(lambda / 100, lambda / 2, lambda / 30);
        var solver = new SurfaceMomSolver();
        var single = solver.Solve(grid.Structure!, f, grid.Port!);
        var multi = solver.SolveMultiPort(grid.Structure!, f, new[] { grid.Port! });
        Assert.True((1 / multi.Admittance[0, 0] - single.InputImpedance).Magnitude < 1e-9 * single.InputImpedance.Magnitude);
    }

    // ------------------------------------------------------------------ a two-port line

    /// <summary>
    /// A strip of width w at height h over the ground plane, in air, with a vertical tab
    /// down to the plane at each end. Each tab's foot is a port (a gap between tab and
    /// plane). Built as one folded sheet so the triangles keep one orientation round the
    /// two bends.
    /// </summary>
    private static (SurfaceStructure Structure, SurfacePort[] Ports) AirLine(double length, double w, double h, double cell)
    {
        int nTab = Math.Max(1, (int)Math.Round(h / cell)), nLine = (int)Math.Round(length / cell), ny = Math.Max(1, (int)Math.Round(w / cell));
        int rows = nTab + nLine + nTab;
        var vertices = new List<Vector3D>();
        for (int i = 0; i <= rows; i++)
            for (int j = 0; j <= ny; j++)
            {
                double y = -w / 2 + w * j / ny;
                Vector3D p;
                if (i == 0) p = new Vector3D(0, y, 0);
                else if (i < nTab) p = new Vector3D(0, y, h * i / nTab);
                else if (i <= nTab + nLine) p = new Vector3D(length * (i - nTab) / nLine, y, h);
                else if (i < rows) p = new Vector3D(length, y, h * (rows - i) / nTab);
                else p = new Vector3D(length, y, 0);
                vertices.Add(p);
            }
        int At(int i, int j) => i * (ny + 1) + j;
        var triangles = new List<(int, int, int)>();
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < ny; j++)
            {
                triangles.Add((At(i, j), At(i + 1, j), At(i, j + 1)));
                triangles.Add((At(i + 1, j), At(i + 1, j + 1), At(i, j + 1)));
            }
        var structure = new SurfaceStructure(vertices, triangles, new GroundPlane(0));
        SurfacePort Foot(double x) => new(
            Enumerable.Range(0, structure.Edges.Count).Where(e => structure.Edges[e].MinusTriangle < 0
                && Math.Abs(vertices[structure.Edges[e].V1].X - x) < 1e-12).ToList(),
            new Vector3D(0, 0, -1));
        var ports = new[] { Foot(0), Foot(length) };
        Assert.Equal(ny, ports[0].EdgeBases.Count);
        Assert.Equal(ny, ports[1].EdgeBases.Count);
        return (structure, ports);
    }

    private static Complex[,] LineS(double length, double f, double reference, Func<double, Complex>? sheet,
        out SurfaceStructure structure, double cell = 1e-3)
    {
        var (s, ports) = AirLine(length, 2e-3, 1e-3, cell);
        structure = s;
        var solution = new SurfaceMomSolver { SheetImpedance = sheet }.SolveMultiPort(s, f, ports);
        return NetworkChecks.AdmittanceToScattering(solution.Admittance, reference);
    }

    [Fact]
    public void ALineInAir_IsReciprocalAndPassive_AndItsWaveTravelsAtTheSpeedOfLight()
    {
        double f = 3e9, k0 = 2 * Math.PI * f / C0, reference = 90;
        var sShort = LineS(30e-3, f, reference, null, out var structure);
        var sLong = LineS(40e-3, f, reference, null, out _);
        var check = NetworkChecks.Check(f, sShort);
        _output.WriteLine($"{structure.BasisCount} unknowns; |S11| {sShort[0, 0].Magnitude:f4}, |S21| {sShort[1, 0].Magnitude:f4}; " +
                          $"reciprocity {check.ReciprocityError:e2}, largest singular value {check.LargestSingularValue:f5}, " +
                          $"not returned {check.AbsorbedFraction[0]:p2}");
        Assert.True(check.ReciprocityError < 1e-9);
        Assert.True(check.IsPassive(1e-6));
        // What does not come back out of a port was radiated: a little, from the two bends.
        Assert.InRange(check.AbsorbedFraction[0], 0, 0.15);
        Assert.Equal(check.AbsorbedFraction[0], check.AbsorbedFraction[1], 1e-6);
        foreach (string line in NetworkChecks.Describe(new[] { check })) _output.WriteLine(line);

        // Two lengths between the same ports: the ports drop out, and the line is TEM in air.
        Complex gamma = LineFromTwoLengths.PropagationConstant(sShort, sLong, 10e-3, k0);
        _output.WriteLine($"1 mm cells: β = {gamma.Imaginary:f3} rad/m against ω/c = {k0:f3} ({gamma.Imaginary / k0 - 1:p2}); α = {gamma.Real:f4} Np/m");
        Assert.Equal(k0, gamma.Imaginary, 0.015 * k0);
        Assert.InRange(gamma.Real, -0.005 * k0, 0.02 * k0);

        // Half the cell size: closer.
        Complex fine = LineFromTwoLengths.PropagationConstant(
            LineS(30e-3, f, reference, null, out var fineStructure, 0.5e-3),
            LineS(40e-3, f, reference, null, out _, 0.5e-3), 10e-3, k0);
        _output.WriteLine($"0.5 mm cells ({fineStructure.BasisCount} unknowns): β = {fine.Imaginary:f3} rad/m ({fine.Imaginary / k0 - 1:p2}); α = {fine.Real:f4} Np/m");
        Assert.True(Math.Abs(fine.Imaginary - k0) < Math.Abs(gamma.Imaginary - k0));
        Assert.Equal(k0, fine.Imaginary, 0.007 * k0);
    }

    [Fact]
    public void WithLossyMetal_TheLineAttenuates_AndStaysPassive()
    {
        double f = 3e9, k0 = 2 * Math.PI * f / C0, reference = 90;
        Func<double, Complex> sheet = _ => 0.05;                    // Ω per square
        var sShort = LineS(30e-3, f, reference, sheet, out _);
        var sLong = LineS(40e-3, f, reference, sheet, out _);
        var perfectShort = LineS(30e-3, f, reference, null, out _);
        var perfectLong = LineS(40e-3, f, reference, null, out _);
        Complex gamma = LineFromTwoLengths.PropagationConstant(sShort, sLong, 10e-3, k0);
        Complex perfect = LineFromTwoLengths.PropagationConstant(perfectShort, perfectLong, 10e-3, k0);
        // Even current over the 2 mm strip on a 90 Ω line: α = R′/(2·Z0) with R′ = Z_s/w.
        double even = 0.05 / 2e-3 / (2 * 90);
        double added = gamma.Real - perfect.Real;
        _output.WriteLine($"α {gamma.Real:f4} Np/m with loss, {perfect.Real:f4} without: added {added:f4}; even-current estimate {even:f4}");
        Assert.True(NetworkChecks.Check(f, sShort).IsPassive(1e-6));
        Assert.True(sShort[1, 0].Magnitude < perfectShort[1, 0].Magnitude);
        // An estimate on both counts (the meshed line is not exactly 90 Ω, two cells across
        // the strip cannot crowd the current): the right size, no more.
        Assert.InRange(added, 0.8 * even, 2.0 * even);
    }

    // ------------------------------------------------------------------ adaptive sweep

    [Fact]
    public void TheAdaptiveSweep_FindsAResonanceThatASevenPointSweepStepsOver()
    {
        // A broad response with a Q = 300 series resonance at 1.37 GHz on it.
        double f0 = 1.37e9, q = 300, r = 2;
        Complex[] Response(double f)
        {
            double x = f / f0 - f0 / f;
            Complex resonant = 1 / (r * new Complex(1, q * x));
            Complex broad = 1 / new Complex(50, 2 * Math.PI * f * 4e-9);
            return new[] { resonant + broad };
        }
        double peak = Response(f0)[0].Magnitude;

        double coarse = 0;
        for (int k = 0; k < 7; k++) coarse = Math.Max(coarse, Response(1e9 * Math.Pow(2, k / 6.0))[0].Magnitude);
        Assert.True(coarse < 0.2 * peak, "the test only means something if the fixed sweep misses the peak");

        int calls = 0;
        var sweep = RationalSweep.Run(f => { calls++; return Response(f); }, 1e9, 2e9, tolerance: 1e-4);
        double best = 0, at = 0;
        for (double f = 1e9; f <= 2e9; f += 0.05e6)
        {
            double m = sweep.At(f)[0].Magnitude;
            if (m > best) { best = m; at = f; }
        }
        _output.WriteLine($"{calls} solver runs; peak {best:f5} at {at / 1e6:f2} MHz (true {peak:f5} at {f0 / 1e6:f2} MHz); " +
                          $"fixed 7-point sweep saw {coarse:f5}; estimated error {sweep.EstimatedError:e1}");
        Assert.True(sweep.Converged);
        Assert.InRange(calls, 5, 25);
        Assert.Equal(calls, sweep.FrequenciesHz.Count);
        Assert.Equal(f0, at, 0.1e6);
        Assert.Equal(peak, best, 0.01 * peak);
        // And between the samples it is the response, not only at the peak.
        foreach (double f in new[] { 1.05e9, 1.3e9, 1.365e9, 1.38e9, 1.9e9 })
            Assert.True((sweep.At(f)[0] - Response(f)[0]).Magnitude < 2e-3 * peak, $"off at {f / 1e6} MHz");
    }

    [Fact]
    public void RationalInterpolation_ReproducesARationalFunction()
    {
        // (2 + 3x)/(1 + x²) has degree 2 over 2: five points determine it.
        Complex F(double x) => (2 + 3 * x) / new Complex(1 + x * x, 0.5 * x);
        var xs = new[] { 0.0, 0.5, 1.0, 1.7, 2.5 };
        var ys = xs.Select(F).ToArray();
        foreach (double x in new[] { 0.2, 0.9, 2.2, 3.0 })
            Assert.True((RationalSweep.Interpolate(xs, ys, x) - F(x)).Magnitude < 1e-10);
        Assert.Equal(ys[2], RationalSweep.Interpolate(xs, ys, 1.0));
    }

    // ------------------------------------------------------------------ checks

    [Fact]
    public void TheChecks_TellAPassiveReciprocalNetworkFromOneThatIsNot()
    {
        // A matched lossless line: |S21| = 1.
        var line = new[,] { { Complex.Zero, Complex.FromPolarCoordinates(1, -1.1) }, { Complex.FromPolarCoordinates(1, -1.1), Complex.Zero } };
        var good = NetworkChecks.Check(1e9, line);
        Assert.True(good.IsPassive() && good.IsReciprocal());
        Assert.Equal(1.0, good.LargestSingularValue, 1e-12);
        Assert.Equal(0.0, good.AbsorbedFraction[0], 1e-12);

        var gain = new[,] { { Complex.Zero, new Complex(1.1, 0) }, { new Complex(1.1, 0), Complex.Zero } };
        Assert.False(NetworkChecks.Check(1e9, gain).IsPassive());
        Assert.Contains("NOT PASSIVE", NetworkChecks.Describe(new[] { NetworkChecks.Check(1e9, gain) })[0]);

        var isolator = new[,] { { Complex.Zero, Complex.Zero }, { Complex.One, Complex.Zero } };
        var oneWay = NetworkChecks.Check(1e9, isolator);
        Assert.True(oneWay.IsPassive());
        Assert.False(oneWay.IsReciprocal());
        Assert.Equal(1.0, oneWay.ReciprocityError, 1e-12);

        // A singular value the row and column sums do not show: [[0.8, 0.8], [0.8, −0.8]]/…
        var s = new[,] { { new Complex(0.8, 0), new Complex(0.8, 0) }, { new Complex(0.8, 0), new Complex(-0.8, 0) } };
        Assert.Equal(0.8 * Math.Sqrt(2), NetworkChecks.LargestSingularValue(s), 1e-12);

        // Y → S agrees with Z → S.
        var z = new[,] { { new Complex(30, 12), new Complex(5, -3) }, { new Complex(5, -3), new Complex(70, -20) } };
        var fromZ = NetworkParameters.ImpedanceToScattering(z, 50);
        var fromY = NetworkChecks.AdmittanceToScattering(NetworkParameters.Invert(z), 50);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.True((fromZ[i, j] - fromY[i, j]).Magnitude < 1e-12);
    }

    [Fact]
    public void TwoLengthsOfALine_GiveItsPropagationConstant_WhateverThePortsAre()
    {
        // Lines of 20 and 35 mm with γ = 3 + j70 /m and Z0 = 63 Ω, each between the same two
        // lumped port networks (a series inductor and a shunt capacitor), in ABCD form.
        Complex gamma = new(3, 70);
        double z0 = 63, f = 2e9, omega = 2 * Math.PI * f;
        Complex[,] Abcd(double length)
        {
            Complex[,] Mul(Complex[,] a, Complex[,] b) => NetworkParameters.Multiply(a, b);
            var series = new[,] { { Complex.One, new Complex(0, omega * 1.5e-9) }, { Complex.Zero, Complex.One } };
            var shunt = new[,] { { Complex.One, Complex.Zero }, { new Complex(0, omega * 0.4e-12), Complex.One } };
            var line = new[,]
            {
                { Complex.Cosh(gamma * length), z0 * Complex.Sinh(gamma * length) },
                { Complex.Sinh(gamma * length) / z0, Complex.Cosh(gamma * length) }
            };
            // Port 2's network is port 1's mirrored.
            return Mul(Mul(Mul(Mul(series, shunt), line), shunt), series);
        }
        Complex[,] ToS(Complex[,] m)
        {
            Complex a = m[0, 0], b = m[0, 1], c = m[1, 0], d = m[1, 1];
            Complex denominator = a + b / 50 + c * 50 + d;
            return new[,]
            {
                { (a + b / 50 - c * 50 - d) / denominator, 2 * (a * d - b * c) / denominator },
                { 2 / denominator, (-a + b / 50 - c * 50 + d) / denominator }
            };
        }
        Complex found = LineFromTwoLengths.PropagationConstant(ToS(Abcd(20e-3)), ToS(Abcd(35e-3)), 15e-3, 65);
        Assert.True((found - gamma).Magnitude < 1e-8 * gamma.Magnitude, $"{found} against {gamma}");
    }
}
