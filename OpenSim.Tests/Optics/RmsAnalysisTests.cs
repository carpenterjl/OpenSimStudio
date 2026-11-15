using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-40: the pupil quadrature's arguments and the four RMS analyses (RMS vs field, wavelength
/// and focus, RMS field map). Expected values are derived in the comments or computed
/// independently with <see cref="OpdEngine"/>; OpticStudio's definitions are quoted from its
/// 2025 R2 help pages saved under plans/sources/optics/op-40-help/. The lenses are arbitrary test
/// inputs. The gate itself is <c>Gates/RmsGateTests</c>.
/// </summary>
public sealed class RmsAnalysisTests
{
    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false, SurfaceAperture? aperture = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, Aperture = aperture,
    };

    private static SequentialLens Lens(params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = double.PositiveInfinity } }.Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static SequentialLens Singlet(SurfaceAperture? aperture = null) =>
        Lens(S(0.05, 0.006, 1.6, stop: true, aperture: aperture), S(-0.2, 0.064));   // near its paraxial focus (BFL ≈ 64.2 mm)

    private static OpticalSystemData Sys(double epd, double[] angleFields, double[]? micrometers = null) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet { Type = FieldType.Angle, Points = angleFields.Select(a => new FieldPoint { Y = a }).ToArray() },
        Wavelengths = micrometers is null ? new WavelengthSet()
            : new WavelengthSet { Wavelengths = micrometers.Select(m => new Wavelength { Micrometers = m }).ToArray() },
    };

    private static OpticsAnalysisContext Context(OpticalSystemData system) =>
        new("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });

    private static LinePlot Plot(OpticsAnalysisResult result) => (LinePlot)result.Plots[0];

    // ---- The quadrature's arguments ----

    [Fact]
    public void TheQuadrature_RefusesRingsOutsideTheHelpsRange_AndTheGridMatchesTheSpotDiagramsSquare()
    {
        // The help ("RMS vs. Field"): for Gaussian quadrature "The maximum number is 20".
        Assert.Throws<ArgumentOutOfRangeException>(() => PupilQuadrature.Gaussian(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PupilQuadrature.Gaussian(21));
        Assert.Throws<ArgumentOutOfRangeException>(() => PupilQuadrature.Gaussian(3, obscuration: 1));
        var grid = PupilQuadrature.Rectangular(17);
        Assert.Equal(PupilSampling.Square(17), grid.Points);
        Assert.All(grid.Weights, w => Assert.Equal(1.0 / grid.Points.Count, w));
        // The first arm lies on +x at every ring.
        var rule = PupilQuadrature.Gaussian(3);
        for (int i = 0; i < 3; i++) Assert.Equal(0.0, rule.Points[i * rule.Arms].Py);
    }

    // ---- Registration and the empty project ----

    [Fact]
    public void TheFourAnalyses_AreRegistered_AndSayWhenThereAreNoSurfaces()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        foreach (var id in new[] { RmsVsFieldAnalysis.SavedId, RmsVsWavelengthAnalysis.SavedId, RmsVsFocusAnalysis.SavedId, RmsFieldMapAnalysis.SavedId })
        {
            Assert.True(registry.TryGet(id, out var analysis), id);
            var result = analysis.Compute(new OpticsAnalysisContext("P", new OpticsSettings()), analysis.CreateDefaultSettings(), default);
            Assert.Contains("No surfaces", result.Text);
        }
    }

    [Fact]
    public void TheAnalyses_RunOnALens_AndStateTheirAssumptions()
    {
        var lens = Singlet();
        var context = Context(Sys(0.02, new[] { 0.0, 5, 10 }, new[] { 0.5, 0.55, 0.6 }));
        IOpticsAnalysis[] analyses =
        {
            new RmsVsFieldAnalysis(_ => lens), new RmsVsWavelengthAnalysis(_ => lens), new RmsVsFocusAnalysis(_ => lens), new RmsFieldMapAnalysis(_ => lens),
        };
        foreach (var analysis in analyses)
        {
            var result = analysis.Compute(context, analysis.CreateDefaultSettings(), default);
            Assert.NotEmpty(result.Assumptions);
            Assert.Single(result.Plots);
            Assert.Contains("Every point has a value", result.Text);
        }
    }

    // ---- The values ----

    [Fact]
    public void TheRmsAtADefinedField_IsTheWeightedRmsOfTheOpdEnginesOpd_PistonRemoved()
    {
        // The scan's last point is the 10° field itself, traced as an appended field row; the
        // independent value is OpdEngine's OPD at the table's own row over the same rule, with the
        // weighted mean removed: √(Σw(W − W̄)²/Σw).
        var lens = Singlet();
        var system = Sys(0.02, new[] { 0.0, 10 });
        var result = new RmsVsFieldAnalysis(_ => lens).Compute(Context(system), new RmsVsFieldSettings { RayDensity = 5, FieldDensity = 2 }, default);
        double analysed = Plot(result).Curves[0].Y[1];

        var rule = PupilQuadrature.Gaussian(5);
        var opd = OpdEngine.Compute(lens, system, 1, 0, rule.Points);
        double mean = 0;
        for (int i = 0; i < rule.Points.Count; i++) mean += rule.Weights[i] * opd.Samples[i].Waves;
        double variance = 0;
        for (int i = 0; i < rule.Points.Count; i++) variance += rule.Weights[i] * Math.Pow(opd.Samples[i].Waves - mean, 2);
        double expected = Math.Sqrt(variance / rule.Weights.Sum());
        Assert.True(Math.Abs(analysed / expected - 1) <= 1e-12, $"analysis {analysed:R}, direct {expected:R}.");
    }

    [Theory]
    [InlineData(RmsData.WavefrontError)]
    [InlineData(RmsData.SpotRadius)]
    [InlineData(RmsData.SpotY)]
    public void TheCentroidReference_NeverExceedsTheChiefRay(RmsData data)
    {
        // Removing a least-squares tilt (wavefront) or measuring about the weighted mean (spot)
        // minimises the weighted mean square over that freedom, so it cannot exceed the chief-ray
        // value, which is one particular choice.
        var lens = Singlet();
        var context = Context(Sys(0.02, new[] { 0.0, 10 }));
        var analysis = new RmsVsFieldAnalysis(_ => lens);
        var chief = Plot(analysis.Compute(context, new RmsVsFieldSettings { Data = data, FieldDensity = 5 }, default)).Curves[0].Y;
        var centroid = Plot(analysis.Compute(context, new RmsVsFieldSettings { Data = data, FieldDensity = 5, Reference = RmsReference.Centroid }, default)).Curves[0].Y;
        for (int i = 0; i < chief.Count; i++)
            Assert.True(centroid[i] <= chief[i] * (1 + 1e-12), $"point {i}: centroid {centroid[i]:R}, chief {chief[i]:R}.");
        // Off axis there is coma, so the centroid sits off the chief ray and the values differ.
        Assert.True(centroid[^1] < chief[^1] * (1 - 1e-6));
    }

    [Fact]
    public void TheEvaluation_IsBitwiseTheSame_AtAnyDegreeOfParallelism()
    {
        var lens = Singlet();
        var context = Context(Sys(0.02, new[] { 0.0, 10 }, new[] { 0.5, 0.6 }));
        var settings = new RmsVsFieldSettings { FieldDensity = 7, RayDensity = 4 };
        var serial = new RmsVsFieldAnalysis(_ => lens) { MaxDegreeOfParallelism = 1 }.Compute(context, settings, default);
        var parallel = new RmsVsFieldAnalysis(_ => lens).Compute(context, settings, default);
        var a = Plot(serial).Curves;
        var b = Plot(parallel).Curves;
        Assert.Equal(a.Count, b.Count);
        for (int c = 0; c < a.Count; c++) Assert.Equal(a[c].Y, b[c].Y);
        Assert.Equal(serial.Text, parallel.Text);
    }

    [Fact]
    public void AVignettedPupil_HasNoGaussianQuadratureValue_ButTheRectangularArrayCountsTheRays()
    {
        // An aperture of radius 8 mm on the 10 mm-radius beam clips the pupil's edge. The help
        // ("Gaussian Quadrature"): "the algorithm is not accurate if some of the rays are clipped".
        var lens = Singlet(new CircularAperture(0, 0.008));
        var context = Context(Sys(0.02, new[] { 0.0, 10 }));
        var analysis = new RmsVsFieldAnalysis(_ => lens);
        var gq = analysis.Compute(context, new RmsVsFieldSettings { FieldDensity = 2 }, default);
        Assert.All(Plot(gq).Curves[0].Y, v => Assert.True(double.IsNaN(v)));
        Assert.Contains("Gaussian quadrature needs every ray", gq.Text);
        var ra = analysis.Compute(context, new RmsVsFieldSettings { FieldDensity = 2, Method = PupilIntegration.RectangularArray, RayDensity = 32 }, default);
        Assert.All(Plot(ra).Curves[0].Y, v => Assert.True(double.IsFinite(v) && v > 0));
        Assert.Contains("rays left out", ra.Text);
    }

    [Fact]
    public void TheWavelengthCurves_FollowTheSetting()
    {
        var lens = Singlet();
        var context = Context(Sys(0.02, new[] { 0.0, 10 }, new[] { 0.5, 0.55, 0.6 }));
        var analysis = new RmsVsFieldAnalysis(_ => lens);
        int Count(RmsVsFieldSettings s) => Plot(analysis.Compute(context, s with { FieldDensity = 2, RayDensity = 3 }, default)).Curves.Count;
        Assert.Equal(4, Count(new RmsVsFieldSettings()));
        Assert.Equal(1, Count(new RmsVsFieldSettings { Curves = RmsWavelengthCurves.PolychromaticOnly }));
        Assert.Equal(3, Count(new RmsVsFieldSettings { Curves = RmsWavelengthCurves.MonochromaticOnly }));
        Assert.Equal(1, Count(new RmsVsFieldSettings { Wavelength = 2 }));
        // The diffraction limit adds one line.
        Assert.Equal(2, Count(new RmsVsFieldSettings { Wavelength = 2, ShowDiffractionLimit = true }));
    }

    [Fact]
    public void ThePolychromaticRms_WithEqualIndexAtEveryWavelength_IsTheMonochromaticOneInPrimaryWaves()
    {
        // With a constant index every wavelength traces the same rays with the same paths; the
        // polychromatic RMS pools them (equal weights), measured in waves of the primary
        // wavelength, so it equals the primary wavelength's own RMS.
        var lens = Singlet();
        var context = Context(Sys(0.02, new[] { 0.0, 10 }, new[] { 0.55, 0.65 }));
        var curves = Plot(new RmsVsFieldAnalysis(_ => lens).Compute(context, new RmsVsFieldSettings { FieldDensity = 2, RayDensity = 4 }, default)).Curves;
        Assert.Equal("Polychromatic", curves[2].Name);
        for (int i = 0; i < 2; i++) Assert.True(Math.Abs(curves[2].Y[i] / curves[0].Y[i] - 1) <= 1e-10, $"point {i}");
    }

    [Fact]
    public void RmsVsWavelength_AtATableWavelength_EqualsRmsVsFieldThere()
    {
        // The wave scan's ends are the table's shortest and longest wavelengths, traced as an
        // appended row; RMS vs field at the same field and table row must agree.
        var lens = Singlet();
        var context = Context(Sys(0.02, new[] { 0.0, 10 }, new[] { 0.5, 0.6 }));
        var byWave = Plot(new RmsVsWavelengthAnalysis(_ => lens).Compute(context, new RmsVsWavelengthSettings { Field = 2, WaveDensity = 3, RayDensity = 4 }, default)).Curves[0];
        Assert.Equal(new[] { 0.5, 0.55, 0.6 }, byWave.X.Select(x => Math.Round(x, 12)));
        var byField = Plot(new RmsVsFieldAnalysis(_ => lens).Compute(context, new RmsVsFieldSettings { Wavelength = 2, FieldDensity = 2, RayDensity = 4 }, default)).Curves[0];
        Assert.True(Math.Abs(byWave.Y[2] / byField.Y[1] - 1) <= 1e-12, $"{byWave.Y[2]:R} against {byField.Y[1]:R}");
    }

    [Fact]
    public void TheFieldMap_IsAGridOfTheSampling_CentredOnTheReferenceField()
    {
        var lens = Singlet();
        var context = Context(Sys(0.02, new[] { 0.0, 10 }));
        var result = new RmsFieldMapAnalysis(_ => lens).Compute(context, new RmsFieldMapSettings
        {
            XFieldSampling = 5, YFieldSampling = 3, XFieldSize = 2, YFieldSize = 4, Field = 2, RayDensity = 3,
        }, default);
        var map = Assert.IsType<AnalysisMapPlot>(result.Plots[0]);
        Assert.Equal(3, map.Field.Rows);
        Assert.Equal(5, map.Field.Columns);
        Assert.Equal(-2, map.Field.X0, 12);
        Assert.Equal(2, map.Field.X1, 12);
        Assert.Equal(6, map.Field.Y0, 12);
        Assert.Equal(14, map.Field.Y1, 12);
        // Mirror symmetry of the lens about the yz plane: the map is even in x.
        for (int r = 0; r < 3; r++)
            Assert.True(Math.Abs(map.Field.Values[r, 0] / map.Field.Values[r, 4] - 1) <= 1e-9, $"row {r}");
    }

    [Fact]
    public void TheSettingsOutsideTheirRange_AreRefused()
    {
        var lens = Singlet();
        var context = Context(Sys(0.02, new[] { 0.0, 10 }));
        Assert.Throws<ArgumentException>(() => new RmsVsFieldAnalysis(_ => lens).Compute(context, new RmsVsFieldSettings { RayDensity = 21 }, default));
        Assert.Throws<ArgumentException>(() => new RmsVsFieldAnalysis(_ => lens).Compute(context, new RmsVsFieldSettings { FieldDensity = 101 }, default));
        Assert.Throws<ArgumentException>(() => new RmsVsFieldAnalysis(_ => lens).Compute(context, new RmsVsFieldSettings { Scan = FieldScan.PlusX }, default));
        Assert.Throws<ArgumentException>(() => new RmsVsFocusAnalysis(_ => lens).Compute(context, new RmsVsFocusSettings { MinFocus = 1, MaxFocus = 1 }, default));
        // The help offers the peak to valley with the rectangular array only.
        Assert.Throws<ArgumentException>(() => new RmsFieldMapAnalysis(_ => lens).Compute(context, new RmsFieldMapSettings { Data = RmsMapData.WavefrontPeakToValley }, default));
        var afocal = Context(Sys(0.02, new[] { 0.0, 10 }) with { Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.02, AfocalImageSpace = true } });
        Assert.Throws<InvalidOperationException>(() => new RmsVsFieldAnalysis(_ => lens).Compute(afocal, new RmsVsFieldSettings(), default));
    }

    // ---- The Strehl ratio (OP-38's PSF engine) ----

    // A paraboloid (k = −1, R < 0) on axis sends every axial ray through its focus at R/2 with
    // equal path (PsfAnalysisTests, RealRayTraceTests); the image surface is placed there.
    private static SequentialLens Paraboloid() => Lens(new SequentialSurface
    {
        Curvature = OpticsConventions.CurvatureFromRadius(-0.2), Conic = -1, Thickness = -0.1,
        Medium = ParaxialMedium.Constant(1), IsStop = true, IsMirror = true,
    });

    [Fact]
    public void TheStrehlRatioAgainstField_AtADefinedField_IsOp38sStrehl()
    {
        // The scan's last point is the 1.5° field itself, traced as an appended field row; OP-38's
        // engine at the table's own row (the default OPD options are the ones its Strehl analysis
        // uses for one wavelength) and its Strehl ratio analysis give the same pupil and the same
        // peak search. Expected bitwise; 1e-12 relative allows only the appended row's field
        // normalisation to round differently.
        var lens = Singlet();
        var system = Sys(0.008, new[] { 0.0, 1.5 });
        var context = Context(system);
        var result = new RmsVsFieldAnalysis(_ => lens).Compute(context,
            new RmsVsFieldSettings { Data = RmsData.StrehlRatio, PsfSampling = 32, FieldDensity = 2, Wavelength = 1 }, default);
        var curve = Plot(result).Curves[0];
        Assert.Equal("Strehl ratio", Plot(result).YLabel);

        var pupil = PsfEngine.BuildPupil(lens, system, 1, 0, 32, new OpdOptions());
        double engine = PsfEngine.Strehl(pupil);
        Assert.True(Math.Abs(curve.Y[1] / engine - 1) <= 1e-12, $"RMS vs field {curve.Y[1]:R}, engine {engine:R}.");
        var op38 = Plot(new StrehlRatioAnalysis(_ => lens).Compute(context, new StrehlRatioSettings { Sampling = 32, Wavelength = 1 }, default)).Curves[0];
        Assert.True(Math.Abs(curve.Y[1] / op38.Y[1] - 1) <= 1e-12, $"RMS vs field {curve.Y[1]:R}, Strehl analysis {op38.Y[1]:R}.");

        // The Maréchal expression 1 − (2πσ)² is a lower bound of the PSF-peak Strehl ratio, σ the
        // amplitude-weighted RMS of the same pupil (derived in PsfGateTests,
        // TheStrehlOfASmallZernikeTerm_LiesOnTheMarechalExpression_WithinItsDerivedBounds).
        foreach (int f in new[] { 0, 1 })
        {
            double sigma = StrehlRatioAnalysis.WeightedRms(PsfEngine.BuildPupil(lens, system, f, 0, 32, new OpdOptions()));
            double marechal = 1 - Math.Pow(2 * Math.PI * sigma, 2);
            Assert.True(curve.Y[f] >= marechal - 1e-12 && curve.Y[f] <= 1 + 1e-12, $"field {f}: Strehl {curve.Y[f]:R}, Maréchal bound {marechal:R}.");
        }
    }

    [Fact]
    public void TheStrehlRatio_OfAParaboloidOnAxis_IsOne()
    {
        // Zero OPD: the PSF is the unaberrated one, so its peak over the unaberrated peak is 1 to
        // the engine's rounding (the OP-38 gate allows 1e-12 for the same lens through PsfEngine).
        var lens = Paraboloid();
        var context = Context(Sys(0.01, new[] { 0.0, 0.1 }));
        var result = new RmsVsFieldAnalysis(_ => lens).Compute(context,
            new RmsVsFieldSettings { Data = RmsData.StrehlRatio, PsfSampling = 32, FieldDensity = 2 }, default);
        Assert.True(Math.Abs(Plot(result).Curves[0].Y[0] - 1) <= 1e-12, $"Strehl {Plot(result).Curves[0].Y[0]:R}.");
        Assert.Contains("Every point has a value.", result.Text);
    }

    [Fact]
    public void TheStrehlRatio_IsBitwiseTheSame_AtAnyDegreeOfParallelism()
    {
        var lens = Singlet();
        var context = Context(Sys(0.008, new[] { 0.0, 1.5 }, new[] { 0.5, 0.6 }));
        var settings = new RmsVsFieldSettings { Data = RmsData.StrehlRatio, PsfSampling = 16, FieldDensity = 3 };
        var serial = new RmsVsFieldAnalysis(_ => lens) { MaxDegreeOfParallelism = 1 }.Compute(context, settings, default);
        var parallel = new RmsVsFieldAnalysis(_ => lens).Compute(context, settings, default);
        var a = Plot(serial).Curves;
        var b = Plot(parallel).Curves;
        Assert.Equal(3, a.Count);   // two wavelengths and the polychromatic curve
        for (int c = 0; c < a.Count; c++) Assert.Equal(a[c].Y, b[c].Y);
        Assert.Equal(serial.Text, parallel.Text);

        var mapSettings = new RmsFieldMapSettings { Data = RmsMapData.StrehlRatio, PsfSampling = 16, XFieldSampling = 2, YFieldSampling = 2, Field = 2, XFieldSize = 0.5, YFieldSize = 0.5 };
        var mapSerial = new RmsFieldMapAnalysis(_ => lens) { MaxDegreeOfParallelism = 1 }.Compute(context, mapSettings, default);
        var mapParallel = new RmsFieldMapAnalysis(_ => lens).Compute(context, mapSettings, default);
        Assert.Equal(Assert.IsType<AnalysisMapPlot>(mapSerial.Plots[0]).Field.Values.Cast<double>(),
            Assert.IsType<AnalysisMapPlot>(mapParallel.Plots[0]).Field.Values.Cast<double>());
        Assert.All(Assert.IsType<AnalysisMapPlot>(mapSerial.Plots[0]).Field.Values.Cast<double>(), v => Assert.InRange(v, 0, 1 + 1e-12));
    }

    [Fact]
    public void TheStrehlRatiosSamplingWarnings_ReachTheResult_NamingThePoint()
    {
        // A paraboloid 0.5 mm out of focus on a 16 × 16 pupil is aliased (PsfAnalysisTests,
        // AnAliasedPsf_IsWarnedAbove_AndAnOversizedImageDeltaIsRefused, the same lens and
        // sampling); the warning carries the point's label with its focus shift.
        var lens = Paraboloid();
        var context = Context(Sys(0.04, new[] { 0.0, 0.1 }));
        var result = new RmsVsFocusAnalysis(_ => lens).Compute(context, new RmsVsFocusSettings
        {
            Data = RmsData.StrehlRatio, PsfSampling = 16, Field = 1, FocusDensity = 2, MinFocus = -0.5, MaxFocus = 0.5,
        }, default);
        var lines = result.Sections[0].Lines;
        Assert.Contains(lines, l => l.StartsWith("Warning: Field 1", StringComparison.Ordinal) && l.Contains("focus -0.5", StringComparison.Ordinal) && l.Contains("ALIASED", StringComparison.Ordinal));
        Assert.Contains(result.Assumptions, a => a.StartsWith("Warning: ", StringComparison.Ordinal) && a.Contains("ALIASED", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => new RmsVsFocusAnalysis(_ => lens).Compute(context, new RmsVsFocusSettings { Data = RmsData.StrehlRatio, PsfSampling = 8 }, default));
    }

    [Fact]
    public void ThePeakToValley_OnTheRectangularArray_IsAtLeastTheRms()
    {
        // Derived: the residual's mean lies between its min and max, so every |W − W̄| ≤ max − min
        // and the RMS, the root of the mean of their squares, is at most max − min.
        var lens = Singlet();
        var context = Context(Sys(0.02, new[] { 0.0, 10 }));
        var analysis = new RmsFieldMapAnalysis(_ => lens);
        var common = new RmsFieldMapSettings { Method = PupilIntegration.RectangularArray, RayDensity = 24, XFieldSampling = 2, YFieldSampling = 2, Wavelength = 1 };
        var ptv = Assert.IsType<AnalysisMapPlot>(analysis.Compute(context, common with { Data = RmsMapData.WavefrontPeakToValley }, default).Plots[0]).Field.Values;
        var rms = Assert.IsType<AnalysisMapPlot>(analysis.Compute(context, common, default).Plots[0]).Field.Values;
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 2; c++)
                Assert.True(ptv[r, c] >= rms[r, c], $"[{r}, {c}]: P-V {ptv[r, c]:R}, RMS {rms[r, c]:R}");
    }
}
