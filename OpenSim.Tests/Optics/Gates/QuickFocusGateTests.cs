using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Optimization.Interactive;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// The OP-60 gate, library half (docs/Optics-Gates.md): Quick Focus lands on the minimum that
/// OP-40's RMS vs Focus analysis reports for the same lens and sampling. The other half (a slider
/// drag updates an open spot diagram within the update budget) is
/// <c>OpenSim.App.Tests/ViewModels/OpticsInteractiveToolsTests</c>. The lens is an arbitrary test
/// input (the singlet of <c>RmsAnalysisTests</c>); every expected value comes from the analysis or
/// is derived in the comments.
/// </summary>
public sealed class QuickFocusGateTests
{
    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop,
    };

    private static SequentialLens Singlet() => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064), new SequentialSurface(),
        },
    };

    private static OpticalSystemData Sys(double field, params double[] micrometers) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.02 },
        Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint { Y = field } } },
        Wavelengths = new WavelengthSet { Wavelengths = micrometers.Select(m => new Wavelength { Micrometers = m }).ToArray() },
    };

    private static OpticsAnalysisContext Context(OpticalSystemData system) =>
        new("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });

    /// <summary>RMS vs Focus over [min, max] mm (n points) for field row 1, every wavelength; the
    /// focus values (mm) and the plotted curve.</summary>
    private static (double[] Z, double[] Y) RmsVsFocus(SequentialLens lens, OpticalSystemData system, RmsData data,
        RmsReference reference, double min, double max, int n)
    {
        var result = new RmsVsFocusAnalysis(_ => lens).Compute(Context(system), new RmsVsFocusSettings
        {
            Data = data, Reference = reference, RayDensity = 6, Field = 1, Wavelength = 0, MinFocus = min, MaxFocus = max, FocusDensity = n,
        }, default);
        var curve = ((LinePlot)result.Plots[0]).Curves[0];
        return (curve.X.ToArray(), curve.Y.ToArray());
    }

    [Fact]
    public void QuickFocusOnTheSpotRadius_LandsOnTheVertexOfRmsVsFocus()
    {
        // Same lens, same sampling (Gaussian quadrature, 6 rings, 24 arms: both defaults), one
        // field (so Quick Focus's field average is that field's RMS, unchanged), three wavelengths
        // of a constant-index glass, about the chief ray.
        //
        // Derived: every ray, the chief ray included, reaches the plane image at x(z) = x₀ + z·L/N,
        // linear in the focus shift z, so RMS²(z) = Σw|r(z) − r_chief(z)|²/Σw is exactly a
        // quadratic in z. The vertex of the parabola through RMS vs Focus's three samples around
        // its smallest value is therefore the analysis's minimum to rounding (relative 1e-15 of
        // RMS² over a second difference of ~10² µm² here: below 1e-12 m).
        //
        // Quick Focus's stopping rule (Brent's localmin, InteractiveSearch.Tolerance) puts the
        // minimum within result.PositionTolerance of its answer; the tolerance is that plus 1e-9 m
        // for the vertex's rounding (a thousand times its bound).
        var lens = Singlet();
        var system = Sys(5, 0.5, 0.55, 0.6);
        var qf = QuickFocus.Find(lens, system, new QuickFocusOptions { Criterion = QuickFocusCriterion.SpotRadius, Rings = 6 });
        double zStar = qf.Shift * 1e3;   // mm

        // A grid not centred on the answer: Δ = 0.05 mm, the answer 0.37 mm from its first point.
        var (z, y) = RmsVsFocus(lens, system, RmsData.SpotRadius, RmsReference.ChiefRay, zStar - 0.37, zStar + 0.63, 21);
        int i = Array.IndexOf(y, y.Min());
        Assert.InRange(i, 1, z.Length - 2);
        double vertex = Vertex(z[i - 1], z[i], z[i + 1], y[i - 1] * y[i - 1], y[i] * y[i], y[i + 1] * y[i + 1]);   // mm

        double tolerance = qf.PositionTolerance + 1e-9;
        Assert.True(Math.Abs(vertex * 1e-3 - qf.Shift) <= tolerance,
            $"Quick Focus shift {qf.Shift:R} m, RMS vs Focus vertex {vertex * 1e-3:R} m, allowed {tolerance:R} m.");
        // The value Quick Focus reports is the curve's at that point (the same engine).
        double atVertex = Math.Sqrt(Parabola(z[i - 1], z[i], z[i + 1], y[i - 1] * y[i - 1], y[i] * y[i], y[i + 1] * y[i + 1], vertex));
        Assert.True(Math.Abs(qf.Value * 1e6 / atVertex - 1) <= 1e-9, $"Quick Focus {qf.Value * 1e6:R} µm, curve {atVertex:R} µm.");
        Assert.True(qf.Value < qf.StartValue);
    }

    [Fact]
    public void QuickFocusOnTheWavefront_LandsOnRmsVsFocusSmallestSample()
    {
        // Wavefront about the centroid, polychromatic. The RMS wavefront is not exactly quadratic
        // in focus, so the comparison is with the grid: on a grid of spacing Δ = 2 µm, far below
        // the depth-of-focus scale λ/NA² ≈ 26 µm of this lens (NA ≈ 0.145, derived from EPD 20 mm and
        // f ≈ 69 mm), the curve is a parabola within ±Δ of its minimum, so its smallest sample is
        // the one nearest the true minimum: within Δ/2 of it. The grid puts the answer 0.3 Δ from a
        // sample. Tolerance: Δ/2 plus Quick Focus's stopping tolerance.
        var lens = Singlet();
        var system = Sys(0, 0.5, 0.55, 0.6);
        var qf = QuickFocus.Find(lens, system, new QuickFocusOptions { Criterion = QuickFocusCriterion.Wavefront, UseCentroid = true });
        double zStar = qf.Shift * 1e3, delta = 0.002;
        var (z, y) = RmsVsFocus(lens, system, RmsData.WavefrontError, RmsReference.Centroid, zStar - 10.3 * delta, zStar + 9.7 * delta, 21);
        int i = Array.IndexOf(y, y.Min());
        Assert.InRange(i, 1, z.Length - 2);
        double tolerance = delta / 2 * 1e-3 + qf.PositionTolerance;
        Assert.True(Math.Abs(z[i] * 1e-3 - qf.Shift) <= tolerance,
            $"Quick Focus shift {qf.Shift:R} m, RMS vs Focus smallest sample {z[i] * 1e-3:R} m, allowed {tolerance:R} m.");
        Assert.True(qf.Value <= y[i] * (1 + 1e-12), $"Quick Focus {qf.Value:R} waves is above the grid's smallest {y[i]:R}.");
    }

    /// <summary>The abscissa of the vertex of the parabola through three points (Lagrange form).</summary>
    private static double Vertex(double x0, double x1, double x2, double f0, double f1, double f2)
    {
        double num = (x1 - x0) * (x1 - x0) * (f1 - f2) - (x1 - x2) * (x1 - x2) * (f1 - f0);
        double den = (x1 - x0) * (f1 - f2) - (x1 - x2) * (f1 - f0);
        return x1 - 0.5 * num / den;
    }

    /// <summary>The parabola through three points, at x.</summary>
    private static double Parabola(double x0, double x1, double x2, double f0, double f1, double f2, double x) =>
        f0 * (x - x1) * (x - x2) / ((x0 - x1) * (x0 - x2))
        + f1 * (x - x0) * (x - x2) / ((x1 - x0) * (x1 - x2))
        + f2 * (x - x0) * (x - x1) / ((x2 - x0) * (x2 - x1));
}
