using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Optimization.Interactive;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-60: Quick Focus's field average and project-record path, and Quick Adjust (OpticStudio
/// 2025 R2 help pages "Quick Focus" and "Quick Adjust", saved under
/// plans/sources/optics/op-60-help/). Expected values are derived in the comments or computed
/// independently with OP-40's RMS vs Focus; the lenses are arbitrary test inputs. The gate is
/// <c>Gates/QuickFocusGateTests</c>.
/// </summary>
public sealed class InteractiveAdjustmentTests
{
    private static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop,
    };

    private static SequentialLens Lens(params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = double.PositiveInfinity } }.Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static SequentialLens Singlet() => Lens(S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064));

    private static OpticalSystemData Sys(double epd, (double Angle, double Weight)[] fields, double[]? micrometers = null) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet { Type = FieldType.Angle, Points = fields.Select(f => new FieldPoint { Y = f.Angle, Weight = f.Weight }).ToArray() },
        Wavelengths = micrometers is null ? new WavelengthSet()
            : new WavelengthSet { Wavelengths = micrometers.Select(m => new Wavelength { Micrometers = m }).ToArray() },
    };

    // ---- Quick Focus ----

    [Fact]
    public void QuickFocusCriterion_IsTheWeightedRmsOverFieldsOfRmsVsFocusCurves()
    {
        // The help: "a polychromatic average over the field, using the defined fields,
        // wavelengths, and weights". Here √(Σ w_f v_f²/Σ w_f) of RMS vs Focus's per-field values
        // at shift 0 (its first focus point), field weights 1, 2 and 0.5.
        var lens = Singlet();
        var system = Sys(0.02, new[] { (0.0, 1.0), (5.0, 2.0), (10.0, 0.5) }, new[] { 0.5, 0.55, 0.6 });
        var context = new OpticsAnalysisContext("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });
        var result = new RmsVsFocusAnalysis(_ => lens).Compute(context, new RmsVsFocusSettings
        {
            Data = RmsData.SpotRadius, Reference = RmsReference.Centroid, Field = 0, Wavelength = 0, MinFocus = 0, MaxFocus = 0.1, FocusDensity = 2,
        }, default);
        var curves = ((LinePlot)result.Plots[0]).Curves;
        double[] w = { 1, 2, 0.5 };
        double s = 0;
        for (int f = 0; f < 3; f++) s += w[f] * curves[f].Y[0] * curves[f].Y[0];
        double expected = Math.Sqrt(s / w.Sum());   // µm

        var (value, failure) = QuickFocus.Criterion(lens, system, new QuickFocusOptions { Criterion = QuickFocusCriterion.SpotRadius, UseCentroid = true }, 0);
        Assert.Null(failure);
        Assert.True(Math.Abs(value * 1e6 / expected - 1) <= 1e-12, $"Quick Focus {value * 1e6:R} µm, from the curves {expected:R} µm.");
    }

    [Fact]
    public void QuickFocusOnTheRecord_WritesTheThicknessBeforeTheImage_InLensUnits()
    {
        var optics = new OpticsSettings
        {
            SystemData = new OpticsSystemSettings { ApertureValue = 20 },
            Surfaces = new()
            {
                new() { ThicknessIsInfinite = true },
                new() { Radius = 50, Thickness = 6, Index = 1.6, IsStop = true },
                new() { Radius = -200, Thickness = 64 },
                new(),
            },
        };
        var options = new QuickFocusOptions { Criterion = QuickFocusCriterion.SpotRadius };
        var applied = QuickFocus.Run(optics, NoCatalogs, options);
        Assert.Equal(2, applied.Result.Surface);
        Assert.Equal(64 + OpticsConventions.FromMeters(applied.Result.Shift, LensUnit.Millimeter), applied.Settings.Surfaces![2].Thickness);
        Assert.Equal(optics.Surfaces[1], applied.Settings.Surfaces[1]);
        // The same search on the lens the record gives.
        var lens = ProjectLens.From(optics, NoCatalogs)!;
        var direct = QuickFocus.Find(lens, SystemDataSettings.FromSettings(optics.SystemData, new List<string>()), options);
        Assert.Equal(direct.Shift, applied.Result.Shift);
        // Focusing the focused record again moves it by no more than the stopping tolerances
        // (the criterion's minimum lies within each run's tolerance of its answer).
        var again = QuickFocus.Run(applied.Settings, NoCatalogs, options);
        Assert.True(Math.Abs(again.Result.Shift) <= again.Result.PositionTolerance + applied.Result.PositionTolerance + 1e-12,
            $"second shift {again.Result.Shift:R} m.");
    }

    [Fact]
    public void QuickFocus_RefusesASolvedThickness_NonSequentialMode_AndAfocalImageSpace()
    {
        // The fixture's back distance is a marginal-ray-height solve: Quick Focus would be overwritten.
        var solved = LocalOptimizationFixtures.Singlet();
        var e = Assert.Throws<InvalidOperationException>(() => QuickFocus.Run(solved, NoCatalogs, new QuickFocusOptions()));
        Assert.Contains("solve", e.Message);
        Assert.Contains("surface 2", e.Message, StringComparison.OrdinalIgnoreCase);

        var nsc = Assert.Throws<InvalidOperationException>(() => QuickFocus.Run(solved with { Mode = "NonSequential" }, NoCatalogs, new QuickFocusOptions()));
        Assert.Contains("NSC Quick Focus", nsc.Message);

        var system = Sys(0.02, new[] { (0.0, 1.0) });
        system = system with { Aperture = system.Aperture with { AfocalImageSpace = true } };
        var afocal = Assert.Throws<InvalidOperationException>(() => QuickFocus.Find(Singlet(), system, new QuickFocusOptions()));
        Assert.Contains("afocal", afocal.Message);
        Assert.Throws<ArgumentException>(() => QuickFocus.Find(Singlet(), Sys(0.02, new[] { (0.0, 1.0) }), new QuickFocusOptions { Rings = 21 }));
    }

    // ---- Quick Adjust ----

    [Fact]
    public void QuickAdjustAngular_CollimatesATelescope_AtTheSumOfTheFocalLengths()
    {
        // Two Paraxial surfaces (ideal thin lenses, f₁ = 50 mm, f₂ = 20 mm) refract by slopes:
        // u′ = u − h/f. Derived: a ray of slope u₀ at height h leaves the second lens with slope
        // u₀ − h/f₁ − (h + d·(u₀ − h/f₁))/f₂, which is independent of h exactly when d = f₁ + f₂
        // (it is then −u₀·f₁/f₂). So at d = 70 mm every ray of a field leaves parallel and the
        // angular RMS about the centroid is zero; elsewhere it is proportional to |d − 70 mm|.
        // The tolerance is Quick Adjust's stopping tolerance plus 1e-12 m for rounding of the
        // direction cosines (1e-16 over a slope gradient of order 1/m).
        var lens = Lens(
            new SequentialSurface { Thickness = 0.06, IsStop = true, Shape = new ParaxialShape(0.05) },
            new SequentialSurface { Thickness = 0.03, Shape = new ParaxialShape(0.02) });
        var system = Sys(0.01, new[] { (0.0, 1.0), (1.0, 1.0) });
        var result = QuickAdjust.Find(lens, system, new QuickAdjustOptions
        {
            Surface = 1, Parameter = QuickAdjustParameter.Thickness, Criterion = QuickAdjustCriterion.AngularRadius,
        });
        Assert.Equal(3, result.EvaluateSurface);
        Assert.True(Math.Abs(result.Value - 0.07) <= result.PositionTolerance + 1e-12,
            $"separation {result.Value:R} m, expected 0.07 m, allowed {result.PositionTolerance + 1e-12:R}.");
        // The criterion is proportional to |d − 70 mm| in slopes (above); in direction cosines the
        // factor varies by about u² ≲ 0.02 over these slopes (|u| ≲ 0.12: h/f₁ ≤ 5/50 plus the 1°
        // field), so 1.1 times the start's
        // ratio bounds what is left at the answer.
        double left = 1.1 * result.StartCriterion * (Math.Abs(result.Value - 0.07) + 1e-12) / 0.01 + 1e-15;
        Assert.True(result.CriterionValue <= left, $"angular RMS left {result.CriterionValue:R}, allowed {left:R}.");
    }

    [Fact]
    public void QuickAdjustSpotAtTheImage_IsQuickFocusAboutTheCentroid()
    {
        // Quick Adjust on the thickness before the image, spot radius at the image, uses OP-34's
        // ray sampler; Quick Focus with the centroid uses OP-40's engine. Both trace the same rays
        // to the same plane, so the criteria agree to rounding at any thickness, and both searches
        // land within their stopping tolerances of the one minimum (RMS² is a quadratic in the
        // thickness; see the gate).
        var lens = Singlet();
        var system = Sys(0.02, new[] { (0.0, 1.0), (7.0, 1.0) }, new[] { 0.5, 0.55, 0.6 });
        var adjust = new QuickAdjustOptions { Surface = 2, Parameter = QuickAdjustParameter.Thickness, Criterion = QuickAdjustCriterion.SpotRadius };
        var focus = new QuickFocusOptions { Criterion = QuickFocusCriterion.SpotRadius, UseCentroid = true };
        foreach (double t in new[] { 0.060, 0.0635, 0.066 })
        {
            double a = QuickAdjust.Criterion(lens, system, adjust, t).Value;
            double b = QuickFocus.Criterion(lens, system, focus, t - 0.064).Value;
            Assert.True(Math.Abs(a / b - 1) <= 1e-10, $"thickness {t}: Quick Adjust {a:R}, Quick Focus {b:R}.");
        }
        var qa = QuickAdjust.Find(lens, system, adjust);
        var qf = QuickFocus.Find(lens, system, focus);
        Assert.True(Math.Abs(qa.Value - qf.Thickness) <= qa.PositionTolerance + qf.PositionTolerance + 1e-12,
            $"Quick Adjust {qa.Value:R} m, Quick Focus {qf.Thickness:R} m.");
    }

    [Fact]
    public void QuickAdjustRadius_FindsALocalMinimumOfTheCriterion_AndWritesTheRadius()
    {
        // The back radius of the singlet at a fixed image distance: the answer must be a local
        // minimum of the criterion (a step of 1e-4 of the curvature either way is no better).
        var lens = Singlet();
        var system = Sys(0.02, new[] { (0.0, 1.0) });
        var options = new QuickAdjustOptions { Surface = 2, Parameter = QuickAdjustParameter.Radius, Criterion = QuickAdjustCriterion.SpotRadius };
        var r = QuickAdjust.Find(lens, system, options);
        Assert.True(r.CriterionValue < r.StartCriterion);
        double c = r.Value, dc = 1e-4 * Math.Abs(c);
        Assert.True(QuickAdjust.Criterion(lens, system, options, c - dc).Value >= r.CriterionValue);
        Assert.True(QuickAdjust.Criterion(lens, system, options, c + dc).Value >= r.CriterionValue);

        var optics = new OpticsSettings
        {
            SystemData = new OpticsSystemSettings { ApertureValue = 20 },
            Surfaces = new()
            {
                new() { ThicknessIsInfinite = true },
                new() { Radius = 50, Thickness = 6, Index = 1.6, IsStop = true },
                new() { Radius = -200, Thickness = 64 },
                new(),
            },
        };
        var applied = QuickAdjust.Run(optics, NoCatalogs, options);
        Assert.Equal(OpticsConventions.FromMeters(1 / applied.Result.Value, LensUnit.Millimeter), applied.Settings.Surfaces![2].Radius);
        Assert.Equal(64, applied.Settings.Surfaces[2].Thickness);
    }

    [Fact]
    public void QuickAdjust_RefusesAnEvaluationSurfaceNotAfterTheAdjustedOne()
    {
        var system = Sys(0.02, new[] { (0.0, 1.0) });
        Assert.Throws<ArgumentException>(() => QuickAdjust.Find(Singlet(), system, new QuickAdjustOptions { Surface = 2, EvaluateSurface = 2 }));
        Assert.Throws<ArgumentException>(() => QuickAdjust.Find(Singlet(), system, new QuickAdjustOptions { Surface = 0, Parameter = QuickAdjustParameter.Radius }));
        Assert.Throws<ArgumentException>(() => QuickAdjust.Find(Singlet(), system, new QuickAdjustOptions { Surface = 0, Parameter = QuickAdjustParameter.Thickness }));   // infinite object distance
    }
}
