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
/// FU-15: two microstrip circuits from D. M. Sheen, "Numerical modeling of microstrip circuits and
/// antennas", PhD thesis, MIT, 1991, on the layered MoM, against the thesis's measured curves and its
/// FD-TD:
///  • the low-pass filter of Sec. 3.4 (Fig. 3.25, p. 108): a 20.32 × 2.54 mm patch on εr 2.2,
///    h 0.794 mm, fed by 2.413 mm lines on opposite long sides whose near edges are 5.65 mm from the
///    nearer patch end; |S11| and |S21| measured and by FD-TD in Figs. 3.33 and 3.34 (pp. 115–116);
///  • the line-fed patch of Sec. 4.3 (Fig. 4.11, p. 159): 40.2 × 40.2 mm on εr 2.55, h 1.59 mm, a
///    4.48 mm line at the centre of one edge; Zin at the patch edge measured by Deshpande and Bailey
///    (IEEE Trans. AP-30(4), 1982) and by FD-TD in Fig. 4.14 (p. 162).
/// Perfect conductors of zero thickness and a lossless substrate, as in the FD-TD. The filter board's
/// coaxial transitions were not de-embedded (p. 136); for the patch the thesis states neither connectors
/// nor calibration, and plots the measured Zin at the patch edge; metal and board or ground sizes are
/// unstated for both. Curves read off the thesis's figures (verified re-read); material behind the
/// numbers: References/FU-15.
/// </summary>
public class SheenCircuitsTests
{
    private const double C0 = 299792458.0;
    private readonly ITestOutputHelper _output;
    public SheenCircuitsTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------ the low-pass filter

    private const double H = 0.794e-3, Er = 2.2, Line = 2.413e-3, PatchHalfX = 10.16e-3, PatchHalfY = 1.27e-3;
    private const double LineX0 = -PatchHalfX + 5.65e-3, LineX1 = LineX0 + Line;
    // The FD-TD's reference planes: 10 Δy = 4.232 mm from the patch's long sides (p. 112).
    private const double RefOffset = 4.232e-3;
    // Cells up to 0.8 mm, graded from 0.2 mm at every part edge and port line by 1.4 (the mesher grades
    // each interval from both ends and stretches the steps to fit), the ports 2.4 mm beyond the reference
    // planes with 2.4 mm of stub past each gap, calibration lines of 6.4 and 12.8 mm. With these the
    // calibration's ε_eff is within 0.9 % of the 2D solve's at 3 and 5.55 GHz. Other choices drifted,
    // for a cause not isolated: 6 and 12 mm lines with a 2 mm shift and 2.5 mm stubs on 0.8/0.2/1.5 cells
    // read ε_eff 4.3 % high at 5.55 GHz, and an 11 mm line on 0.4/0.1/1.3 cells 7.7 % low at 3 GHz. All
    // five features below are within 0.06 % of 0.6/0.15/1.4 cells, which are in turn within 0.02 dB
    // of 0.4/0.1/1.3 cells at the corner. About 3 min per feature in Debug.
    public sealed record FilterMesh(double MaxEdge, double MinEdge, double Growth, double Shift, double Stub, double Short, double Long);
    private static readonly FilterMesh Gate = new(0.8e-3, 0.2e-3, 1.4, 2.4e-3, 2.4e-3, 6.4e-3, 12.8e-3);

    private static (SurfaceStructure Structure, SurfacePort[] Ports) Built(SheetRectangle[] parts, SheetPortLine[] ports,
        double maxEdge, double minEdge, double growth)
    {
        var built = SheetMetalAssembly.Build(parts, ports, maxEdge, 20000, minEdge, growth);
        return (built.Structure, built.Ports.Select(p => p.Port).ToArray());
    }

    private static (SurfaceStructure, SurfacePort[]) Filter(FilterMesh m)
    {
        double yPort = PatchHalfY + RefOffset + m.Shift, yEnd = yPort + m.Stub;
        return Built(new[]
        {
            new SheetRectangle("patch", new Vector3D(-PatchHalfX, -PatchHalfY, H), new Vector3D(PatchHalfX, PatchHalfY, H)),
            new SheetRectangle("line1", new Vector3D(LineX0, -yEnd, H), new Vector3D(LineX1, -PatchHalfY, H)),
            new SheetRectangle("line2", new Vector3D(-LineX1, PatchHalfY, H), new Vector3D(-LineX0, yEnd, H)),
        }, new[]
        {
            new SheetPortLine("p1", new Vector3D(LineX0, -yPort, H), new Vector3D(LineX1, -yPort, H), new Vector3D(0, 1, 0)),
            new SheetPortLine("p2", new Vector3D(-LineX1, yPort, H), new Vector3D(-LineX0, yPort, H), new Vector3D(0, -1, 0)),
        }, m.MaxEdge, m.MinEdge, m.Growth);
    }

    /// <summary>A straight line between two gap ports <paramref name="length"/> apart, each with
    /// <paramref name="stub"/> of line beyond it.</summary>
    private static (SurfaceStructure, SurfacePort[]) Through(double width, double height, double length, double stub,
        double maxEdge, double minEdge, double growth)
    {
        double x = width / 2;
        return Built(new[] { new SheetRectangle("line", new Vector3D(-x, 0, height), new Vector3D(x, length + 2 * stub, height)) }, new[]
        {
            new SheetPortLine("p1", new Vector3D(-x, stub, height), new Vector3D(x, stub, height), new Vector3D(0, 1, 0)),
            new SheetPortLine("p2", new Vector3D(-x, stub + length, height), new Vector3D(x, stub + length, height), new Vector3D(0, -1, 0)),
        }, maxEdge, minEdge, growth);
    }

    /// <summary>The two lengths' calibration of a line at <paramref name="f"/>: the 2D solve's β only
    /// picks the branch.</summary>
    private static LineCalibration Calibrate(SurfaceMomSolver solver, LayeredKernelTable table, double f, double width,
        double height, double er, double stub, double shortLength, double longLength, double maxEdge, double minEdge, double growth)
    {
        var spec = ImpedanceCalculator.Solve(new LineSpec
        {
            WidthMeters = width, HeightMeters = height, ThicknessMeters = 1e-6, RelativePermittivity = er, LossTangent = 0, FrequencyHz = f
        });
        double beta = 2 * Math.PI * f / C0 * Math.Sqrt(spec.Modes[0].EffectivePermittivity);
        Complex[,] S((SurfaceStructure s, SurfacePort[] p) m) =>
            NetworkChecks.AdmittanceToScattering(solver.SolveMultiPort(m.s, table, m.p).Admittance, 50);
        return LineFromTwoLengths.Calibrate(S(Through(width, height, shortLength, stub, maxEdge, minEdge, growth)),
            S(Through(width, height, longLength, stub, maxEdge, minEdge, growth)), shortLength, longLength, 50, beta,
            PortModel.ShuntAtTerminals);
    }

    /// <summary>The filter between the FD-TD's reference planes, in the line's own impedance (as the
    /// FD-TD reports it: incident and reflected waves on the line).</summary>
    private static Complex[,] FilterAt(double f, FilterMesh m)
    {
        var solver = new SurfaceMomSolver();
        var table = new LayeredKernelTable(new SubstrateStackup(Er, 0, H), f, rhoMax: 0.035);
        var cal = Calibrate(solver, table, f, Line, H, Er, m.Stub, m.Short, m.Long, m.MaxEdge, m.MinEdge, m.Growth);
        var (structure, ports) = Filter(m);
        var raw = NetworkChecks.AdmittanceToScattering(solver.SolveMultiPort(structure, table, ports).Admittance, 50);
        var check = NetworkChecks.Check(f, raw);
        Assert.True(check.ReciprocityError < 1e-9, $"reciprocity {check.ReciprocityError:e2}");
        Assert.True(check.IsPassive(1e-6), $"largest singular value {check.LargestSingularValue}");
        return cal.DeEmbedToLine(raw, 50, m.Shift);
    }

    private static double Db(Complex x) => 20 * Math.Log10(x.Magnitude);

    public enum Feature { Corner, ReflectionNull, TransmissionNull }

    private static (double Found, double Depth) FeatureOf(Feature feature, double f1GHz, double f2GHz, FilterMesh m)
    {
        var a = FilterAt(f1GHz * 1e9, m);
        var b = FilterAt(f2GHz * 1e9, m);
        if (feature == Feature.Corner)
        {
            double da = Db(a[1, 0]), db = Db(b[1, 0]);
            Assert.True(da > -3 && db < -3, $"|S21| {da:f2} and {db:f2} dB do not straddle −3 dB");
            return (f1GHz + (f2GHz - f1GHz) * (-3 - da) / (db - da), -3);
        }
        Complex sa = feature == Feature.ReflectionNull ? a[0, 0] : a[1, 0];
        Complex sb = feature == Feature.ReflectionNull ? b[0, 0] : b[1, 0];
        Complex chord = sb - sa;
        double t = -(Complex.Conjugate(sa) * chord).Real / (chord.Magnitude * chord.Magnitude);
        Assert.InRange(t, 0, 1);
        return (f1GHz + t * (f2GHz - f1GHz), Db(sa + t * chord));
    }

    // Each feature from two frequencies astride it: the −3 dB corner of |S21| by interpolation in dB;
    // a null as the complex response's closest approach to zero on the chord between the two (against
    // a quadratic through three points 0.1 GHz apart, within 0.012 GHz). The feature must fall between
    // the two, and a null must be deep (the gate run's depth less 5 dB): those bind. The brackets came
    // from OpenSim's own sweep and are narrower than most of the thesis bands, so the thesis bands bind
    // only for the 5 GHz null and the corner against the FD-TD; elsewhere they state the agreement.
    // Thesis values off the verified digitisation of Figs. 3.33 and 3.34, except the measured |S11|
    // null near 5 GHz, where Fig. 3.33's measured line is hidden: a parabola through a Fig. 3.27 point
    // (its depth a lower bound); and the FD-TD |S21| null near 8 GHz, below the plot: the centre of its
    // −40 dB crossings.
    [Theory]
    [InlineData(Feature.Corner, 5.55, 5.60, 5.551, 5.600, 0.01, 0.005, double.NaN)]
    [InlineData(Feature.ReflectionNull, 4.95, 5.05, 4.977, 5.004, 0.01, 0.005, -26.5)]
    [InlineData(Feature.TransmissionNull, 7.0, 7.1, 6.939, double.NaN, 0.025, double.NaN, -29.6)]
    [InlineData(Feature.TransmissionNull, 8.2, 8.3, 7.951, 8.012, 0.05, 0.04, -33.7)]
    [InlineData(Feature.ReflectionNull, 10.9, 11.0, 10.802, 10.993, 0.02, 0.01, -17.4)]
    public void TheLowPassFilter_HasTheMeasuredFeatures(Feature feature, double f1GHz, double f2GHz,
        double measuredGHz, double fdtdGHz, double measuredBand, double fdtdBand, double depthBoundDb)
    {
        var (found, depth) = FeatureOf(feature, f1GHz, f2GHz, Gate);
        _output.WriteLine($"{feature} between {f1GHz} and {f2GHz} GHz: {found:f4} GHz ({depth:f1} dB); measured {measuredGHz} GHz " +
                          $"({(found / measuredGHz - 1) * 100:+0.00;-0.00} %), FD-TD {fdtdGHz} GHz ({(found / fdtdGHz - 1) * 100:+0.00;-0.00} %)");
        Assert.InRange(found / measuredGHz - 1, -measuredBand, measuredBand);
        if (!double.IsNaN(fdtdGHz)) Assert.InRange(found / fdtdGHz - 1, -fdtdBand, fdtdBand);
        if (!double.IsNaN(depthBoundDb)) Assert.True(depth < depthBoundDb, $"the null is only {depth:f1} dB deep");
    }

    // ------------------------------------------------------------------ the line-fed patch

    private const double PH = 1.59e-3, PEr = 2.55, PSide = 40.2e-3, Feed = 4.48e-3;
    // The port at the FD-TD's own reference plane, 16.75 mm (15 cells) from the patch (p. 159), and a
    // quarter wave of stub past the gap (23 mm at 2.27 GHz), which leaves the port almost no series
    // element; 2.5 mm cells graded from 0.6 mm.
    private const double PMaxEdge = 2.5e-3, PMinEdge = 0.6e-3, PGrowth = 1.4;
    private const double PPort = 16.75e-3, PStub = 23e-3, PShort = 10e-3, PLong = 20e-3;

    private static (SurfaceStructure, SurfacePort[]) Patch()
    {
        double half = PSide / 2, x = Feed / 2;
        return Built(new[]
        {
            new SheetRectangle("patch", new Vector3D(-half, 0, PH), new Vector3D(half, PSide, PH)),
            new SheetRectangle("feed", new Vector3D(-x, -PPort - PStub, PH), new Vector3D(x, 0, PH)),
        }, new[] { new SheetPortLine("p1", new Vector3D(-x, -PPort, PH), new Vector3D(x, -PPort, PH), new Vector3D(0, 1, 0)) },
            PMaxEdge, PMinEdge, PGrowth);
    }

    /// <summary>The load beyond <paramref name="lengthMeters"/> of calibrated line, from the
    /// impedance at the calibrated port: the port's own L and the line taken off.</summary>
    private static Complex Load(LineCalibration cal, Complex zPort, double lengthMeters)
    {
        Complex zs = cal.PortSeriesOhms, ys = cal.PortShuntSiemens;
        var port = cal.Model == PortModel.ShuntAtTerminals
            ? new[,] { { Complex.One, zs }, { ys, 1 + zs * ys } }
            : new[,] { { 1 + zs * ys, zs }, { ys, Complex.One } };
        var n = NetworkParameters.Multiply(port, cal.Line(lengthMeters));
        return (n[1, 1] * zPort - n[0, 1]) / (n[0, 0] - n[1, 0] * zPort);
    }

    private static Complex PatchEdgeAt(double f)
    {
        var solver = new SurfaceMomSolver();
        var table = new LayeredKernelTable(new SubstrateStackup(PEr, 0, PH), f, rhoMax: 0.11);
        var cal = Calibrate(solver, table, f, Feed, PH, PEr, PStub, PShort, PLong, PMaxEdge, PMinEdge, PGrowth);
        var (structure, ports) = Patch();
        return Load(cal, 1 / solver.SolveMultiPort(structure, table, ports).Admittance[0, 0], PPort);
    }

    /// <summary>The parallel resonance between two frequencies astride it: where the admittance's
    /// susceptance crosses zero, the conductance there, and Q = (f₀/2)·R₀·dB/df.</summary>
    private static (double F0, double R0, double Q) Resonance(double f1, Complex z1, double f2, Complex z2)
    {
        Complex y1 = 1 / z1, y2 = 1 / z2;
        Assert.True(y1.Imaginary < 0 && y2.Imaginary > 0, $"B {y1.Imaginary:e3} and {y2.Imaginary:e3} S do not straddle zero");
        double t = -y1.Imaginary / (y2.Imaginary - y1.Imaginary), f0 = f1 + t * (f2 - f1);
        double r0 = 1 / (y1.Real + t * (y2.Real - y1.Real));
        return (f0, r0, f0 / 2 * r0 * (y2.Imaginary - y1.Imaginary) / (f2 - f1));
    }

    [Fact]
    public void TheLineFedPatch_ResonatesWhereItWasMeasured()
    {
        // Fig. 4.14's measured markers at 2.25 and 2.30 GHz (117.284 + j192.547 and 118.378 − j180.379 Ω,
        // ±1.2 Ω in R, ±1.0 and ±1.8 Ω in X) give f₀ 2.2747 GHz, R₀ 412.6 Ω, Q 71.9 by this rule — a five-parameter fit to all
        // seven markers gives 2.2749 GHz, 411.7 Ω, 70.8 — and its FD-TD curve 2.2856 GHz, 397.0 Ω, 77.6.
        var measured = Resonance(2.25, new Complex(117.284, 192.547), 2.30, new Complex(118.378, -180.379));
        var fdtd = Resonance(2.25, new Complex(50.436, 147.115), 2.30, new Complex(201.943, -184.794));
        var ours = Resonance(2.25, PatchEdgeAt(2.25e9), 2.30, PatchEdgeAt(2.30e9));
        _output.WriteLine($"f0 {ours.F0:f5} GHz, R0 {ours.R0:f1} Ω, Q {ours.Q:f1}; measured {measured.F0:f5} / {measured.R0:f1} / {measured.Q:f1}; " +
                          $"FD-TD {fdtd.F0:f5} / {fdtd.R0:f1} / {fdtd.Q:f1}");
        Assert.Equal(2.2747, measured.F0, 1e-4);
        Assert.Equal(2.2856, fdtd.F0, 1e-4);
        // The resonance frequency hardly depends on how the feed is taken off: 2.2648–2.2657 GHz with
        // the port 10 or 16.75 mm out and 5 to 23 mm stubs on these cells; on 2.0 mm cells 2.2643 GHz
        // with the 23 mm stub and 2.2616 GHz with the 5 mm one (whose calibration read ε_eff 4 % low).
        Assert.InRange(ours.F0 / measured.F0 - 1, -0.01, 0.01);
        Assert.InRange(ours.F0 / fdtd.F0 - 1, -0.013, 0.013);
        // R₀ and Q at the edge do: the patch's near field reaches along the feed, and a load near
        // 8·Z₀ magnifies any error in the line taken off — 347 to 385 Ω and Q 65.5 to 73.6 over those
        // ports and stubs. With the 23 mm stub and the port at 10 mm, 2.0 against 2.5 mm cells moves
        // them under 0.4 % (3.75 % and 1.26 % with the 5 mm stub); the 16.75 mm port ran on 2.5 mm cells
        // only. The measured board also had metal loss and a ground of unstated size.
        Assert.InRange(ours.R0 / measured.R0 - 1, -0.12, 0.12);
        Assert.InRange(ours.Q / measured.Q - 1, -0.12, 0.12);
        Assert.InRange(ours.R0 / fdtd.R0 - 1, -0.12, 0.12);
        Assert.InRange(ours.Q / fdtd.Q - 1, -0.12, 0.12);
    }
}
