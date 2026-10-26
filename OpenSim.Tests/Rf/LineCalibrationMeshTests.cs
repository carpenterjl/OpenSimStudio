using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Network;
using OpenSim.Rf.Si;
using OpenSim.Rf.Surface;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Rf;

/// <summary>
/// TC-1 (2026-10-25): the two-length calibration drifted with the line lengths because the
/// sheet-metal mesher graded each grid interval from one end and stretched to land, so the cell
/// beside the FAR port depended on the line's length and the two calibration lines carried
/// different port error boxes (the diagnosis and its data are in
/// <c>OpenSim.Tests/References/FU-15/README.md</c>, "TC-1: cause isolated"). The owner's remedy:
/// symmetric grading — the first three cells at each end of every interval are exactly the ramp's,
/// whatever the length — and a rule on the lengths, named in <see cref="LineCalibration.Warnings"/>.
/// </summary>
public class LineCalibrationMeshTests
{
    private const double C0 = 299792458.0;
    private const double H = 0.794e-3, Er = 2.2, W = 2.413e-3;   // the FU-15 filter line
    private readonly ITestOutputHelper _out;
    public LineCalibrationMeshTests(ITestOutputHelper output) => _out = output;

    private static SheetAssemblyResult Through(double length, double stub, double maxEdge, double minEdge, double growth)
    {
        double x = W / 2;
        return SheetMetalAssembly.Build(
            new[] { new SheetRectangle("line", new Vector3D(-x, 0, H), new Vector3D(x, length + 2 * stub, H)) },
            new[]
            {
                new SheetPortLine("p1", new Vector3D(-x, stub, H), new Vector3D(x, stub, H), new Vector3D(0, 1, 0)),
                new SheetPortLine("p2", new Vector3D(-x, stub + length, H), new Vector3D(x, stub + length, H), new Vector3D(0, -1, 0)),
            }, maxEdge, 20000, minEdge, growth);
    }

    /// <summary>The grid lines along y, from the mesh itself.</summary>
    private static List<double> GridY(SurfaceStructure s) =>
        s.Vertices.Select(v => Math.Round(v.Y * 1e9) / 1e9).Distinct().OrderBy(v => v).ToList();

    /// <summary>The <paramref name="count"/> cells walking from the grid line at <paramref name="y"/> in <paramref name="direction"/>.</summary>
    private static double[] CellsFrom(List<double> ys, double y, int count, int direction)
    {
        int i = ys.FindIndex(v => Math.Abs(v - y) < 1e-9);
        Assert.True(i >= 0, $"a grid line at y = {y * 1e3} mm");
        var cells = new double[count];
        for (int k = 0; k < count; k++) cells[k] = Math.Abs(ys[i + direction * (k + 1)] - ys[i + direction * k]);
        return cells;
    }

    [Theory]
    [InlineData(0.8e-3, 0.2e-3, 1.4, 2.4e-3)]
    [InlineData(0.8e-3, 0.2e-3, 1.5, 2.5e-3)]
    [InlineData(0.4e-3, 0.1e-3, 1.3, 2.5e-3)]
    public void EveryLength_CarriesTheSameCells_BesideBothPorts(double maxEdge, double minEdge, double growth, double stub)
    {
        // The ramp the grading promises at every interval end: hMin, then hMin + (growth − 1) × distance.
        double[] ramp = { minEdge, minEdge + (growth - 1) * minEdge, minEdge + (growth - 1) * (minEdge + minEdge + (growth - 1) * minEdge) };
        for (int k = 0; k < 3; k++) ramp[k] = Math.Min(maxEdge, ramp[k]);
        double[][] seen = Array.Empty<double[]>();
        foreach (double l in new[] { 6e-3, 6.4e-3, 8e-3, 10e-3, 11e-3, 12e-3, 12.8e-3, 16e-3, 18e-3 })
        {
            var ys = GridY(Through(l, stub, maxEdge, minEdge, growth).Structure);
            // Both ports, both sides: the three cells are the ramp's, to the metre's rounding.
            foreach (var (y, direction) in new[] { (stub, +1), (stub, -1), (stub + l, +1), (stub + l, -1) })
            {
                var cells = CellsFrom(ys, y, 3, direction);
                for (int k = 0; k < 3; k++) Assert.Equal(ramp[k], cells[k], 9);
            }
            // The whole interval: cells sum to the length, none exceeds the maximum by more than the
            // quarter the absorbed-remainder case allows, and the grid is symmetric about the middle.
            int i1 = ys.FindIndex(v => Math.Abs(v - stub) < 1e-9), i2 = ys.FindIndex(v => Math.Abs(v - stub - l) < 1e-9);
            var line = Enumerable.Range(i1, i2 - i1).Select(k => ys[k + 1] - ys[k]).ToList();
            Assert.Equal(l, line.Sum(), 9);
            Assert.All(line, c => Assert.True(c <= maxEdge * 1.25 + 1e-12, $"a {c * 1e6:F1} µm cell on a {maxEdge * 1e6:F0} µm maximum"));
            for (int k = 0; k < line.Count / 2; k++) Assert.Equal(line[k], line[^(k + 1)], 9);
            _out.WriteLine($"{maxEdge * 1e3:g2}/{minEdge * 1e3:g2}/{growth} L {l * 1e3:g4} mm: {line.Count} cells, max {line.Max() * 1e6:F1} µm");
        }
    }

    [Fact]
    public void TheCalibration_NoLongerDependsOnWhichLengthsWerePicked()
    {
        // The FU-15 gate's cells (0.8 / 0.2 / 1.4) at 5.55 GHz. Before the symmetric grading, pairs
        // of 6, 10, 12 and 18 mm lines read ε_eff anywhere from −1 % to +8.7 % of the 2D solve's,
        // by which far-port cell each length happened to get (calibration_drift_2026-10-25.txt:
        // 6+10 +0.77 %, 6+12 +3.42 %, 10+12 +8.73 %, 12+18 −2.01 %, 16+18 −7.18 %). Now every pair
        // within the rule (long/short ≤ 3, βΔl ≥ 0.5 rad — here Δl ≥ 4 mm is 0.64 rad) reads the
        // same ε_eff to a fraction of a per cent of each other, about 0.8–1 % above the quasi-static
        // 2D cross-section solve (the MoM's full-wave value; measured +0.80…+0.99 % on 2026-10-25).
        double f = 5.55e9, stub = 2.4e-3, maxEdge = 0.8e-3, minEdge = 0.2e-3, growth = 1.4;
        var table = new LayeredKernelTable(new SubstrateStackup(Er, 0, H), f, rhoMax: 0.035);
        var spec = ImpedanceCalculator.Solve(new LineSpec { WidthMeters = W, HeightMeters = H, ThicknessMeters = 1e-6, RelativePermittivity = Er, LossTangent = 0, FrequencyHz = f });
        double eeff2D = spec.Modes[0].EffectivePermittivity, beta2D = 2 * Math.PI * f / C0 * Math.Sqrt(eeff2D);
        var solver = new SurfaceMomSolver();
        var s = new Dictionary<double, Complex[,]>();
        foreach (double l in new[] { 6e-3, 10e-3, 14e-3, 18e-3 })
        {
            var m = Through(l, stub, maxEdge, minEdge, growth);
            s[l] = NetworkChecks.AdmittanceToScattering(solver.SolveMultiPort(m.Structure, table, m.Ports.Select(p => p.Port).ToArray()).Admittance, 50);
        }
        var eeffs = new List<double>();
        foreach (double ls in s.Keys)
            foreach (double ll in s.Keys)
            {
                if (ll <= ls) continue;
                var cal = LineFromTwoLengths.Calibrate(s[ls], s[ll], ls, ll, 50, beta2D, PortModel.ShuntAtTerminals);
                double eeff = Math.Pow(cal.Gamma.Imaginary * C0 / (2 * Math.PI * f), 2);
                _out.WriteLine($"{ls * 1e3:g3}+{ll * 1e3:g3} mm: ε_eff {eeff:F4} ({(eeff / eeff2D - 1) * 100:+0.00} % of 2D), Z0 {cal.CharacteristicOhms.Real:F2} Ω, warnings {cal.Warnings.Count}");
                Assert.Empty(cal.Warnings);                       // 6+18 is 3×, βΔl ≥ 0.64 rad throughout
                eeffs.Add(eeff);
            }
        // The pairs agree with each other within 0.5 % and sit within 1.5 % of the 2D solve.
        Assert.True(eeffs.Max() / eeffs.Min() - 1 < 0.005, $"pair spread {(eeffs.Max() / eeffs.Min() - 1) * 100:F2} %");
        Assert.All(eeffs, e => Assert.InRange(e / eeff2D - 1, -0.015, 0.015));
    }

    [Fact]
    public void TheRule_IsNamedInTheWarnings_AndTheCalibrationStillReturned()
    {
        // An ideal lossless 50 Ω line in a 50 Ω reference: S of a length is the pure delay.
        double f = 3e9, beta = 2 * Math.PI * f / C0 * Math.Sqrt(2);
        Complex[,] S(double l) => new[,] { { Complex.Zero, Complex.Exp(new Complex(0, -beta * l)) }, { Complex.Exp(new Complex(0, -beta * l)), Complex.Zero } };
        var fine = LineFromTwoLengths.Calibrate(S(10e-3), S(20e-3), 10e-3, 20e-3, 50, beta);
        Assert.Empty(fine.Warnings);
        Assert.Equal(beta, fine.Gamma.Imaginary, 6);

        var ratio = LineFromTwoLengths.Calibrate(S(5e-3), S(20e-3), 5e-3, 20e-3, 50, beta);  // 4× and βΔl = 1.33 rad
        Assert.Single(ratio.Warnings);
        Assert.Contains("4× the short one", ratio.Warnings[0]);
        Assert.Equal(beta, ratio.Gamma.Imaginary, 6);                                           // still the right answer here

        var close = LineFromTwoLengths.Calibrate(S(10e-3), S(12e-3), 10e-3, 12e-3, 50, beta);  // βΔl = 0.178 rad
        Assert.Single(close.Warnings);
        Assert.StartsWith("βΔl = 0.178 rad", close.Warnings[0]);
        Assert.Equal(beta, close.Gamma.Imaginary, 6);
    }
}
