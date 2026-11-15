using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.UniversalPlot;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-45's gate (plan): "Focal length against one radius reproduces the lensmaker's curve." Plus
/// the brief's 2D check (EFFL over two radii against the thick-lens formula) and a failing sweep
/// point reported by its parameter value.
/// <para>
/// The lens is a test input, not a design from a source: a singlet of constant index n = 1.5
/// (relative to air), centre thickness t = 5 mm, back radius R₂ = −100 mm, stop on its front
/// surface, entrance pupil 2 mm, object at infinity, image 50 mm behind it.
/// </para>
/// <para>
/// The thick-lens focal length, derived here (no source open): trace the paraxial ray y₁ = 1,
/// u₁ = 0 with the refraction n′u′ = nu − yφ and the transfer y′ = y + t·u′. Surface 1 has power
/// φ₁ = (n − 1)/R₁, so u₁′ = −φ₁/n; at surface 2 the height is y₂ = 1 − tφ₁/n; surface 2 has power
/// φ₂ = (1 − n)/R₂, so the emerging slope is u₂′ = n·u₁′ − y₂φ₂ = −(φ₁ + φ₂ − (t/n)φ₁φ₂). The focal
/// length is f = −y₁/u₂′ = 1/φ with φ = φ₁ + φ₂ − (t/n)φ₁φ₂: the lensmaker's equation for a thick
/// lens, 1/f = (n − 1)[1/R₁ − 1/R₂ + (n − 1)t/(n R₁ R₂)].
/// </para>
/// <para>
/// Tolerance. EFFL is the paraxial engine's focal length, the same ray algebra in a few dozen
/// floating-point operations on numbers of order 1, converted from meters to millimeters; the
/// error is a few units of 1e-16 relative. The test allows 1e-12.
/// </para>
/// </summary>
public sealed class UniversalPlotGateTests
{
    private const double N = 1.5, T = 5, R2 = -100;

    internal static OpticsSettings Singlet(double r1 = 50, double r2 = R2) => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 2 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = r1, Thickness = T, Index = N, IsStop = true },
            new() { Radius = r2, Thickness = 50 },
            new(),
        },
    };

    /// <summary>The thick-lens focal length, derived in the class comment.</summary>
    internal static double ThickLensFocalLength(double r1, double r2, double n = N, double t = T)
    {
        double phi1 = (n - 1) / r1, phi2 = (1 - n) / r2;
        return 1 / (phi1 + phi2 - t / n * phi1 * phi2);
    }

    private static void Close(double expected, double actual, string what) =>
        Assert.True(Math.Abs(actual - expected) <= 1e-12 * Math.Abs(expected), $"{what}: {actual:R} against {expected:R}");

    [Fact]
    public void FocalLengthAgainstOneRadius_ReproducesTheLensmakersCurve()
    {
        var settings = new UniversalPlot1DSettings
        {
            Category = UniversalPlotCategory.Surface, Parameter = UniversalPlotParameter.Radius, Surface = 1,
            Start = 20, Stop = 200, Steps = 10, Operand = "EFFL", Int2 = 1,
        };
        var result = new UniversalPlot1DAnalysis().Compute(new OpticsAnalysisContext("Singlet", Singlet()), settings, default);
        var curve = Assert.IsType<LinePlot>(Assert.Single(result.Plots)).Curves.Single();
        Assert.Equal(10, curve.X.Count);
        Assert.Equal(20, curve.X[0]);
        Assert.Equal(200, curve.X[^1]);
        for (int i = 0; i < curve.X.Count; i++)
        {
            Close(20 + 20.0 * i, curve.X[i], $"radius {i}");
            Close(ThickLensFocalLength(curve.X[i], R2), curve.Y[i], $"EFFL at R1 = {curve.X[i]}");
        }
        Assert.Contains("Surface 1 radius (mm)", result.Text);
        // The analysis always carries a note explaining how failed points are shown, so check the
        // failed-points section and the table, not the whole text.
        Assert.DoesNotContain(result.Sections, s => s.Heading == "Failed points");
        var values = Assert.Single(result.Sections, s => s.Heading == "Values");
        Assert.DoesNotContain(values.TableRows!, r => r.Contains("failed"));
    }

    /// <summary>EFFL over both radii, x = R₁ rising and y = R₂ swept downwards (so the map must
    /// turn its rows round), against the thick-lens formula at every node.</summary>
    [Fact]
    public void FocalLengthOverTwoRadii_IsTheThickLensFormulaAtEveryNode()
    {
        var settings = new UniversalPlot2DSettings
        {
            XCategory = UniversalPlotCategory.Surface, XParameter = UniversalPlotParameter.Radius, XSurface = 1, XStart = 30, XStop = 120, XSteps = 4,
            YCategory = UniversalPlotCategory.Surface, YParameter = UniversalPlotParameter.Radius, YSurface = 2, YStart = -40, YStop = -150, YSteps = 5,
            Operand = "EFFL", Int2 = 1,
        };
        var result = new UniversalPlot2DAnalysis().Compute(new OpticsAnalysisContext("Singlet", Singlet()), settings, default);
        var map = Assert.IsType<AnalysisMapPlot>(Assert.Single(result.Plots));
        var field = map.Field;
        Assert.Equal((4, 5), (field.Columns, field.Rows));
        Assert.Equal((30.0, 120.0, -150.0, -40.0), (field.X0, field.X1, field.Y0, field.Y1));
        for (int r = 0; r < field.Rows; r++)
            for (int c = 0; c < field.Columns; c++)
            {
                double r1 = 30 + 30.0 * c, r2 = -150 + 27.5 * r;   // the nodes in increasing x and y
                Close(ThickLensFocalLength(r1, r2), field.Values[r, c], $"EFFL at R1 = {r1}, R2 = {r2}");
            }
        Assert.Equal(AnalysisMapStyle.Image, map.Style);

        var contour = new UniversalPlot2DAnalysis().Compute(new OpticsAnalysisContext("Singlet", Singlet()),
            settings with { ShowAs = UniversalPlotDisplay.Contour }, default);
        Assert.Equal(AnalysisMapStyle.Contour, Assert.IsType<AnalysisMapPlot>(contour.Plots[0]).Style);
    }

    /// <summary>
    /// A real ray at the pupil edge (REAY, Py = 1, height 1 mm on surface 1) while R₁ runs from
    /// 0.8 to 8 mm: a sphere of radius 0.8 mm never reaches a height of 1 mm, so the ray misses it
    /// and that point must fail, reported by its radius; the other four (2.6, 4.4, 6.2 and 8 mm)
    /// trace. The failed point is a gap (NaN) in the curve and "failed" in the table, never a number.
    /// </summary>
    [Fact]
    public void AFailingPoint_IsReportedByItsParameterValue_AndIsAGap()
    {
        var settings = new UniversalPlot1DSettings
        {
            Category = UniversalPlotCategory.Surface, Parameter = UniversalPlotParameter.Radius, Surface = 1,
            Start = 0.8, Stop = 8, Steps = 5, Operand = "REAY", Int1 = 3, Int2 = 1, Data4 = 1,
        };
        var result = new UniversalPlot1DAnalysis().Compute(new OpticsAnalysisContext("Singlet", Singlet()), settings, default);
        var curve = Assert.IsType<LinePlot>(result.Plots[0]).Curves.Single();
        Assert.True(double.IsNaN(curve.Y[0]), $"the missed point plotted as {curve.Y[0]:R}");
        for (int i = 1; i < 5; i++) Assert.True(double.IsFinite(curve.Y[i]), $"point {i + 1} is {curve.Y[i]:R}");
        var failed = Assert.Single(result.Sections, s => s.Heading == "Failed points");
        Assert.Contains("Point 1, Surface 1 radius = 0.8 mm", Assert.Single(failed.Lines));
        var values = Assert.Single(result.Sections, s => s.Heading == "Values");
        Assert.Equal("failed", values.TableRows[0][2]);
        Assert.Equal(1, values.TableRows.Count(r => r[2] == "failed"));
    }
}
