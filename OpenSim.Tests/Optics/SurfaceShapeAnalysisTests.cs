using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using Xunit;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-43 surface shape analyses (docs/Optics-Conventions.md §28). Every expected value is a
/// closed form derived here: the sphere's sag z = R − sign(R)·√(R² − y²) and slope
/// dz/dy = sign(R)·y/√(R² − y²) (the circle of radius R through the vertex), and for an Even
/// Asphere z = c·r²/(1 + √(1 − c²r²)) + α₂r⁴ (OpticStudio's "Even Asphere" with α₁ = 0, saved
/// under plans/sources/optics/ansyshelp-op27/Even_Asphere.html) with z′ = c·r/φ + 4α₂r³,
/// z″ = c/φ³ + 12α₂r², φ = √(1 − c²r²), and the curvatures of a surface of revolution
/// κ_t = z″/(1 + z′²)^{3/2}, κ_s = z′/(r·√(1 + z′²)) (Meusnier, derived from the normal-curvature
/// formula in <see cref="SurfaceProbe.Curvature"/>).
/// </summary>
public class SurfaceShapeAnalysisTests
{
    private const double Sd = 0.01;          // clear semi-diameter, m (10 mm)
    private const double R = 0.05;           // vertex radius, m (50 mm)
    private const double Alpha2 = 2000;      // m^-3 (2e-6 mm^-3): 20 µm of r⁴ at the rim

    private static SequentialSurface Surf(double radius, SurfaceShape? shape = null, double n = 1.5) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = 0.005, Medium = ParaxialMedium.Constant(n),
        IsStop = true, SemiDiameter = Sd, Shape = shape,
    };

    private static SequentialLens Lens(SequentialSurface first) => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            first,
            new SequentialSurface { Curvature = 0, Thickness = 0.05, SemiDiameter = Sd },
            new SequentialSurface(),
        }
    };

    private static OpticsAnalysisContext Context() => new("P", new OpticsSettings());

    private static OpticalSystemData System() => SystemDataSettings.FromSettings(null, new List<string>());

    private static double SphereSag(double radius, double y) => radius - Math.Sign(radius) * Math.Sqrt(radius * radius - y * y);
    private static double SphereSlope(double radius, double y) => Math.Sign(radius) * y / Math.Sqrt(radius * radius - y * y);

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"{what}: {actual:R} differs from {expected:R} by more than {tolerance:G3}.");

    private static SagTableAnalysis.Table Table(SequentialLens lens, int surface, SagTableSettings settings) =>
        SagTableAnalysis.Compute(lens, System(), new SurfaceProbe(lens.Surfaces[surface], 1e-3 * Sd), surface, Sd, settings, LensUnit.Millimeter);

    // ---- Gate: the sag table of a sphere is the closed form, and its best-fit sphere is itself ----

    [Theory]
    [InlineData(R, BestFitSphereCriterion.MinimumVolume)]
    [InlineData(R, BestFitSphereCriterion.MinimumRmsNoOffset)]
    [InlineData(R, BestFitSphereCriterion.MinimumRmsWithOffset)]
    [InlineData(-R, BestFitSphereCriterion.MinimumVolume)]
    [InlineData(-R, BestFitSphereCriterion.MinimumRmsNoOffset)]
    [InlineData(-R, BestFitSphereCriterion.MinimumRmsWithOffset)]
    public void TheSagTableOfASphere_IsTheClosedForm_AndItsBestFitSphereIsItself(double radius, BestFitSphereCriterion criterion)
    {
        var table = Table(Lens(Surf(radius)), 1, new SagTableSettings { Criterion = criterion, StepSize = 0.25 });
        Assert.Equal(41, table.Rows.Count); // 0 to 10 mm in 0.25 mm
        foreach (var row in table.Rows)
        {
            // Sag and slope against the closed forms; both evaluate to about 1e-17 m and 1e-16.
            Close(SphereSag(radius, row.Y), row.Sag, 1e-15, $"sag at y = {row.Y}");
            Close(SphereSlope(radius, row.Y), row.Slope, 1e-14, $"slope at y = {row.Y}");
            // The sphere fits itself: the Brent stop (1e-10 relative in c) bounds what is left.
            Close(0, row.Deviation, 1e-12, $"deviation at y = {row.Y}");
            Close(0, row.Remove, 1e-12, $"remove at y = {row.Y}");
            Close(0, row.SlopeDelta, 1e-10, $"slope delta at y = {row.Y}");
        }
        Close(radius, table.Fit.Radius, 1e-8 * R, "best-fit radius");
        Close(0, table.Fit.VertexOffset, 1e-12, "vertex offset");
        Assert.True(table.Fit.MaxSlopeDeviation < 1e-10);
    }

    [Fact]
    public void TheBestFitSphereOfASphere_IsItself_AlsoThroughTheDifferencedShapePath_AndAnAnnulus()
    {
        // A zero Even Asphere is the sphere; the fit reads its sag through SurfaceShape.Sag.
        var lens = Lens(Surf(R, new EvenAsphere(new double[] { 0, 0 })));
        foreach (var criterion in Enum.GetValues<BestFitSphereCriterion>())
        {
            var table = Table(lens, 1, new SagTableSettings { Criterion = criterion, MinimumRadius = 2, MaximumRadius = 9 });
            Close(R, table.Fit.Radius, 1e-8 * R, $"{criterion} radius");
            Close(0, table.Fit.VertexOffset, 1e-12, $"{criterion} vertex offset");
            Close(0.002, table.Fit.MinRadius, 1e-15, "fit from 2 mm");
            Close(0.009, table.Fit.MaxRadius, 1e-15, "fit to 9 mm");
        }
    }

    // ---- An Even Asphere: the sag table against the derived sag, and the fit's optimality ----

    [Fact]
    public void TheSagTableOfAnEvenAsphere_IsTheDerivedSag()
    {
        double c = 1 / R;
        var lens = Lens(Surf(R, new EvenAsphere(new double[] { 0, Alpha2 })));
        var table = Table(lens, 1, new SagTableSettings { StepSize = 0.5 });
        Assert.Equal(21, table.Rows.Count);
        foreach (var row in table.Rows)
        {
            double y = row.Y, phi = Math.Sqrt(1 - c * c * y * y);
            Close(c * y * y / (1 + phi) + Alpha2 * Math.Pow(y, 4), row.Sag, 1e-15, $"sag at y = {y}");
            Close(c * y / phi + 4 * Alpha2 * Math.Pow(y, 3), row.Slope, 1e-14, $"slope at y = {y}");
            Close(row.BfsSag - row.Sag, row.Deviation, 0, "deviation is BFS sag − sag");
            Close(row.Slope - row.BfsSlope, row.SlopeDelta, 0, "slope delta");
            // Minimum volume: the sphere lies on the air side, so nothing is to be added.
            Assert.True(row.Remove >= -1e-12, $"remove at y = {y} is {row.Remove}");
        }
        Assert.True(table.Fit.MaxDepthToRemove > 1e-6, "an r⁴ term of 20 µm leaves material to remove");
        Assert.True(table.Fit.VolumeToRemove > 0);
    }

    [Theory]
    [InlineData(BestFitSphereCriterion.MinimumVolume)]
    [InlineData(BestFitSphereCriterion.MinimumRmsNoOffset)]
    [InlineData(BestFitSphereCriterion.MinimumRmsWithOffset)]
    public void TheBestFitSphereOfAnAsphere_IsAMinimumOfItsCriterion(BestFitSphereCriterion criterion)
    {
        var probe = new SurfaceProbe(Surf(R, new EvenAsphere(new double[] { 0, Alpha2 })), 1e-3 * Sd);
        var fit = BestFitSphere.Fit(probe, 0, Sd, criterion, +1, "test");
        var profile = BestFitSphere.Sample(probe, 0, Sd, "test");
        Close(fit.Objective, BestFitSphere.Evaluate(profile, criterion, +1, fit.Curvature).Objective, 0, "objective");
        foreach (double factor in new[] { 1 - 1e-5, 1 + 1e-5, 1 - 1e-3, 1 + 1e-3 })
            Assert.True(BestFitSphere.Evaluate(profile, criterion, +1, fit.Curvature * factor).Objective >= fit.Objective,
                $"{criterion}: c × {factor} gives a lower objective than the fit");
        if (criterion == BestFitSphereCriterion.MinimumRmsNoOffset) Assert.Equal(0, fit.VertexOffset);
    }

    [Fact]
    public void TheAirSide_IsTheLowerIndexSide_AndReverseTakesTheOther()
    {
        var lens = Lens(Surf(R));
        var system = System();
        Assert.Equal(+1, BestFitSphere.AirSign(lens, system, 1, false, out _)); // air before, glass after: +z from air to glass
        Assert.Equal(-1, BestFitSphere.AirSign(lens, system, 2, false, out _)); // glass before, air after
        Assert.Equal(-1, BestFitSphere.AirSign(lens, system, 1, true, out var note));
        Assert.Contains("Reverse direction", note);
        Assert.Equal(+1, BestFitSphere.AirSign(lens, system, 3, false, out var same)); // image: air on both sides
        Assert.Contains("cannot be told", same);
    }

    // ---- Gate: the curvature map of a sphere is constant ----

    public static TheoryData<bool, SurfaceCurvatureData> CurvatureCases()
    {
        var data = new TheoryData<bool, SurfaceCurvatureData>();
        foreach (bool differenced in new[] { false, true })
            foreach (var d in Enum.GetValues<SurfaceCurvatureData>()) data.Add(differenced, d);
        return data;
    }

    [Theory]
    [MemberData(nameof(CurvatureCases))]
    public void TheCurvatureMapOfASphere_IsConstant(bool differenced, SurfaceCurvatureData data)
    {
        var lens = Lens(Surf(R, differenced ? new EvenAsphere(new double[] { 0 }) : null));
        var result = new SurfaceCurvatureAnalysis(_ => lens).Compute(Context(), new SurfaceCurvatureSettings { Data = data, Sampling = 33 }, default);
        var map = Assert.IsType<AnalysisMapPlot>(Assert.Single(result.Plots));
        Assert.Equal("1/mm", map.ValueUnit);
        double expected = 1 / (R * 1000); // 1/mm
        // Analytic: the conic's second derivatives, to rounding. Differenced: Richardson's h⁴
        // term vanishes to 1e-15 for a sphere of 50 mm at h = 10 µm; rounding of the slopes,
        // about 3·ε·|slope|/h ≈ 1e-11 /m, is 5e-13 of c.
        double tolerance = (differenced ? 1e-9 : 1e-13) * expected;
        int inside = 0;
        var f = map.Field;
        for (int row = 0; row < f.Rows; row++)
            for (int col = 0; col < f.Columns; col++)
            {
                double x = f.XAt(col), y = f.YAt(row);
                double v = f.Values[row, col];
                if (x * x + y * y > 100 * (1 + 1e-12)) { Assert.True(double.IsNaN(v), $"({x}, {y}) is outside the clear aperture"); continue; }
                inside++;
                Close(expected, v, tolerance, $"{data} curvature at ({x}, {y})");
            }
        Assert.True(inside > 800);
        Assert.DoesNotContain("could not be evaluated", result.Text);
        Assert.Contains(differenced ? "Richardson" : "analytic second derivatives", result.Text);
    }

    // ---- Slopes and cross-sections against the closed forms ----

    [Fact]
    public void TheSlopeMapOfASphere_IsTheClosedForm()
    {
        var lens = Lens(Surf(R));
        foreach (var data in Enum.GetValues<SurfaceSlopeData>())
        {
            var map = (AnalysisMapPlot)new SurfaceSlopeAnalysis(_ => lens)
                .Compute(Context(), new SurfaceSlopeSettings { Data = data, Sampling = 21 }, default).Plots[0];
            var f = map.Field;
            for (int row = 0; row < f.Rows; row++)
                for (int col = 0; col < f.Columns; col++)
                {
                    double x = f.XAt(col) / 1000, y = f.YAt(row) / 1000, r = Math.Sqrt(x * x + y * y);
                    if (r > Sd * (1 + 1e-12)) continue;
                    double root = Math.Sqrt(R * R - r * r);
                    double expected = data switch
                    {
                        SurfaceSlopeData.Tangential or SurfaceSlopeData.Modulus => r / root,
                        SurfaceSlopeData.Sagittal => 0,
                        SurfaceSlopeData.X => x / root,
                        _ => y / root,
                    };
                    Close(expected, f.Values[row, col], 1e-14, $"{data} slope at ({x}, {y})");
                }
        }
    }

    [Fact]
    public void TheCrossSectionsOfAnEvenAsphere_AreTheDerivedSagAndCurvatures()
    {
        double c = 1 / R;
        var lens = Lens(Surf(R, new EvenAsphere(new double[] { 0, Alpha2 })));
        foreach (double angle in new[] { 0.0, 30.0, 90.0 })
        {
            var sag = (LinePlot)new SurfaceSagCrossSectionAnalysis(_ => lens)
                .Compute(Context(), new SurfaceSagCrossSectionSettings { Angle = angle }, default).Plots[0];
            var tangential = (LinePlot)new SurfaceCurvatureCrossSectionAnalysis(_ => lens)
                .Compute(Context(), new SurfaceCurvatureCrossSectionSettings { Angle = angle, Data = SurfaceCurvatureData.Tangential }, default).Plots[0];
            var sagittal = (LinePlot)new SurfaceCurvatureCrossSectionAnalysis(_ => lens)
                .Compute(Context(), new SurfaceCurvatureCrossSectionSettings { Angle = angle, Data = SurfaceCurvatureData.Sagittal }, default).Plots[0];
            var t = sag.Curves[0].X;
            Assert.Equal(65, t.Count);
            Close(-10, t[0], 1e-12, "first position");
            Close(10, t[^1], 1e-12, "last position");
            for (int i = 0; i < t.Count; i++)
            {
                double r = Math.Abs(t[i]) / 1000, phi = Math.Sqrt(1 - c * c * r * r);
                double z = c * r * r / (1 + phi) + Alpha2 * Math.Pow(r, 4);
                double z1 = c * r / phi + 4 * Alpha2 * Math.Pow(r, 3);
                double z2 = c / (phi * phi * phi) + 12 * Alpha2 * r * r;
                double kt = z2 / Math.Pow(1 + z1 * z1, 1.5);
                double ks = r > 0 ? z1 / (r * Math.Sqrt(1 + z1 * z1)) : z2;
                Close(z * 1000, sag.Curves[0].Y[i], 1e-12, $"sag at {t[i]} mm, {angle}°");
                // Differenced curvatures: |error| ≲ 1e-11 /m against κ ≈ 20 /m (see the map test).
                Close(kt / 1000, tangential.Curves[0].Y[i], 1e-9 * c / 1000, $"tangential curvature at {t[i]} mm, {angle}°");
                Close(ks / 1000, sagittal.Curves[0].Y[i], 1e-9 * c / 1000, $"sagittal curvature at {t[i]} mm, {angle}°");
            }
        }
    }

    // ---- Remove ----

    [Fact]
    public void RemovingTheSphere_LeavesNothingOfASphere_AndTheAsphericTermOfAnAsphere()
    {
        var sphere = Lens(Surf(R));
        foreach (var remove in new[] { SurfaceRemove.BaseRadius, SurfaceRemove.BestFitSphere })
        {
            var map = (AnalysisMapPlot)new SurfaceSagAnalysis(_ => sphere)
                .Compute(Context(), new SurfaceSagSettings { Remove = remove, Sampling = 17 }, default).Plots[0];
            foreach (double v in map.Field.Values)
                if (!double.IsNaN(v)) Close(0, v, 1e-9, $"{remove}: sphere less itself (mm)");
        }

        // Base radius removed from the Even Asphere leaves α₂r⁴ exactly (in mm).
        var asphere = Lens(Surf(R, new EvenAsphere(new double[] { 0, Alpha2 })));
        var section = (LinePlot)new SurfaceSagCrossSectionAnalysis(_ => asphere)
            .Compute(Context(), new SurfaceSagCrossSectionSettings { Remove = SurfaceRemove.BaseRadius, Angle = 0 }, default).Plots[0];
        for (int i = 0; i < section.Curves[0].X.Count; i++)
        {
            double r = section.Curves[0].X[i] / 1000;
            Close(Alpha2 * Math.Pow(r, 4) * 1000, section.Curves[0].Y[i], 1e-14, $"α₂r⁴ at {r} m");
        }
    }

    // ---- Settings, contours, refusals, registration ----

    [Fact]
    public void TheContourFormat_IsTheIntervalOrTheListOfLevels()
    {
        var notes = new List<string>();
        Assert.Null(SurfaceSagAnalysis.ContourLevels("t", "", -1, 1, notes));
        Assert.Equal(new[] { -1.0, -0.5, 0, 0.5, 1, 1.5 }, SurfaceSagAnalysis.ContourLevels("t", "0.5", -1.2, 1.7, notes));
        Assert.Equal(new[] { 0.2, 0.5, 0.8 }, SurfaceSagAnalysis.ContourLevels("t", "0.8 0.5 0.2", -1, 1, notes));
        Assert.Null(SurfaceSagAnalysis.ContourLevels("t", "1e-6", -1, 1, notes));
        Assert.Contains("more than 100 contours", Assert.Single(notes));
        Assert.Throws<ArgumentException>(() => SurfaceSagAnalysis.ContourLevels("t", "0.5 x", -1, 1, notes));
        Assert.Throws<ArgumentException>(() => SurfaceSagAnalysis.ContourLevels("t", "-2", -1, 1, notes));
    }

    [Fact]
    public void BadSurfacesAndSampling_AreRefused_NamingTheSurface()
    {
        var lens = Lens(Surf(R));
        var analysis = new SurfaceSagAnalysis(_ => lens);
        Assert.Contains("1 to 3", Assert.Throws<ArgumentException>(() => analysis.Compute(Context(), new SurfaceSagSettings { Surface = 0 }, default)).Message);
        Assert.Throws<ArgumentException>(() => analysis.Compute(Context(), new SurfaceSagSettings { Surface = 4 }, default));
        Assert.Throws<ArgumentException>(() => analysis.Compute(Context(), new SurfaceSagSettings { Sampling = 64 }, default));

        var withBreak = new SequentialLens
        {
            Surfaces = new[] { lens.Surfaces[0], lens.Surfaces[1], new SequentialSurface { CoordinateBreak = new CoordinateBreak() }, lens.Surfaces[2], lens.Surfaces[3] }
        };
        var ex = Assert.Throws<ArgumentException>(() => new SagTableAnalysis(_ => withBreak).Compute(Context(), new SagTableSettings { Surface = 2 }, default));
        Assert.Contains("surface 2 is a coordinate break", ex.Message);
    }

    [Fact]
    public void TheSurfaceAnalyses_AreRegistered_SayWhenThereAreNoSurfaces_AndKeepTheirSettings()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        var ids = new[]
        {
            SagTableAnalysis.SavedId, SurfaceSagAnalysis.SavedId, SurfaceCurvatureAnalysis.SavedId, SurfaceSlopeAnalysis.SavedId,
            SurfaceSagCrossSectionAnalysis.SavedId, SurfaceCurvatureCrossSectionAnalysis.SavedId, SurfaceSlopeCrossSectionAnalysis.SavedId,
        };
        var edited = new Dictionary<string, object>
        {
            [SagTableAnalysis.SavedId] = new SagTableSettings { Surface = 2, StepSize = 0.5, Criterion = BestFitSphereCriterion.MinimumRmsWithOffset, ReverseDirection = true, MinimumRadius = 1, MaximumRadius = 4 },
            [SurfaceSagAnalysis.SavedId] = new SurfaceSagSettings { Surface = 2, Sampling = 33, ShowAs = SurfaceMapShowAs.Contour, ContourFormat = "0.1", Remove = SurfaceRemove.BestFitSphere, Criterion = BestFitSphereCriterion.MinimumRmsNoOffset, ReverseDirection = true },
            [SurfaceCurvatureAnalysis.SavedId] = new SurfaceCurvatureSettings { Data = SurfaceCurvatureData.Modulus, Remove = SurfaceRemove.BaseRadius },
            [SurfaceSlopeAnalysis.SavedId] = new SurfaceSlopeSettings { Data = SurfaceSlopeData.Sagittal, Sampling = 129 },
            [SurfaceSagCrossSectionAnalysis.SavedId] = new SurfaceSagCrossSectionSettings { Angle = 45, Sampling = 101 },
            [SurfaceCurvatureCrossSectionAnalysis.SavedId] = new SurfaceCurvatureCrossSectionSettings { Data = SurfaceCurvatureData.Y, Angle = 10 },
            [SurfaceSlopeCrossSectionAnalysis.SavedId] = new SurfaceSlopeCrossSectionSettings { Data = SurfaceSlopeData.X, Remove = SurfaceRemove.BestFitSphere },
        };
        foreach (var id in ids)
        {
            Assert.True(registry.TryGet(id, out var analysis), id);
            Assert.Equal("Surface", analysis.Category);
            var result = analysis.Compute(new OpticsAnalysisContext("P", new OpticsSettings()), analysis.CreateDefaultSettings(), default);
            Assert.Equal(new[] { "No surfaces" }, result.Sections.Select(s => s.Heading));
            Assert.Equal(edited[id], OpticsAnalysisSettingsCodec.Clone(analysis, edited[id]));
        }
        // Surface Phase and Phase Slope need a surface carrying a phase, which no type has yet.
        Assert.DoesNotContain(registry.All, a => a.Id.Contains("Phase", StringComparison.Ordinal) && a.Category == "Surface");
    }

    [Fact]
    public void EveryAnalysis_RunsOnTheLens_WithEveryRemoveOption()
    {
        var lens = Lens(Surf(R, new EvenAsphere(new double[] { 0, Alpha2 })));
        foreach (var remove in Enum.GetValues<SurfaceRemove>())
        {
            var analyses = new IOpticsAnalysis[]
            {
                new SurfaceSagAnalysis(_ => lens), new SurfaceCurvatureAnalysis(_ => lens), new SurfaceSlopeAnalysis(_ => lens),
                new SurfaceSagCrossSectionAnalysis(_ => lens), new SurfaceCurvatureCrossSectionAnalysis(_ => lens), new SurfaceSlopeCrossSectionAnalysis(_ => lens),
            };
            foreach (var analysis in analyses)
            {
                var settings = (SurfaceShapeSettingsBase)analysis.CreateDefaultSettings();
                object withRemove = settings with { Remove = remove };
                var result = analysis.Compute(Context(), withRemove, default);
                Assert.NotEmpty(result.Plots);
                Assert.DoesNotContain("could not be evaluated", result.Text);
                Assert.NotEmpty(result.Assumptions);
            }
        }
        var table = new SagTableAnalysis(_ => lens).Compute(Context(), new SagTableSettings(), default);
        Assert.Contains("Best fit sphere radius", table.Text);
        Assert.Contains("Volume to remove", table.Text);
    }
}
