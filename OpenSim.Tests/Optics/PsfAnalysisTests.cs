using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-38: the PSF engine's identities (the FFT is the pupil sum on its own grid; polychromatic
/// rescaling; the sampling rule; line and edge spread; determinism) and the six analyses.
/// Expected values are derived in the comments. The gate itself is <c>Gates/PsfGateTests</c>.
/// OpticStudio's behaviour is read from its 2025 R2 help pages saved under
/// plans/sources/optics/op-38-help/.
/// </summary>
public sealed class PsfAnalysisTests
{
    private const double Lambda = 0.55e-6;

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    private static SequentialSurface S(double radius, double thickness, double n = 1, double k = 0, bool stop = false, bool mirror = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = k, Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror,
    };

    private static SequentialLens Lens(double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance } }.Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double epd, double[] angleFields, params double[] wavelengths) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet
        {
            Type = FieldType.Angle,
            Points = angleFields.Length == 0 ? new[] { new FieldPoint() } : angleFields.Select(a => new FieldPoint { Y = a }).ToArray(),
        },
        Wavelengths = wavelengths.Length == 0 ? new WavelengthSet()
            : new WavelengthSet { Wavelengths = wavelengths.Select(w => new Wavelength { Micrometers = w }).ToArray() },
    };

    // A singlet near its paraxial focus (BFL ≈ 64.2 mm), as in WavefrontAnalysisTests.
    private static SequentialLens Singlet() => Lens(double.PositiveInfinity, S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064));

    private static SequentialLens Paraboloid(double defocus = 0) =>
        Lens(double.PositiveInfinity, S(-0.2, -0.1 + defocus, k: -1, stop: true, mirror: true));

    private static OpticsAnalysisContext Context(OpticalSystemData system) =>
        new("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });

    // ---- The engine ----

    /// <summary>The FFT of the zero-padded pupil is the pupil sum U(x) sampled at
    /// x_p = (p − M/2)·λ/(n·|a|·M·ΔP): it equals the matrix Fourier transform of the same samples
    /// at those points, in intensity and (after the FFT's phase origin is moved to the pupil
    /// centre) in complex amplitude, for either sign of the pupil scale.</summary>
    [Theory]
    [InlineData(0.04, 0.03)]
    [InlineData(-0.04, 0.05)]
    [InlineData(0.03, -0.02)]
    public void TheFft_IsThePupilSum_OnItsOwnGrid(double ax, double ay)
    {
        const int n = 24;
        var pupil = PupilFunction.FromFunctions(n, (x, y) => 1 - 0.3 * (x * x + y * y), (x, y) => 0.4 * x * (x * x + y * y) + 0.2 * y * y, ax, ay, Lambda, 1.5);
        var fft = PsfEngine.Fft(pupil);
        int m = 2 * n;
        Assert.Equal(m, fft.X.Count);
        double dp = 2.0 / (n - 1);
        Close(Lambda / (1.5 * Math.Abs(ax) * m * dp), fft.DeltaX, 1e-12 * fft.DeltaX, "x spacing");
        Close(Lambda / (1.5 * Math.Abs(ay) * m * dp), fft.DeltaY, 1e-12 * fft.DeltaY, "y spacing");
        Assert.Equal(0.0, fft.X[m / 2]);
        var direct = PsfEngine.Sample(pupil, fft.X, fft.Y);
        for (int r = 0; r < m; r++)
            for (int c = 0; c < m; c++)
            {
                Close(direct.Intensity[r, c], fft.Intensity[r, c], 1e-12, $"intensity [{r}, {c}]");
                Close(direct.Amplitude![r, c].Real, fft.Amplitude![r, c].Real, 1e-11, $"real amplitude [{r}, {c}]");
                Close(direct.Amplitude[r, c].Imaginary, fft.Amplitude[r, c].Imaginary, 1e-11, $"imaginary amplitude [{r}, {c}]");
            }
    }

    /// <summary>The FFT, the matrix transform and the Huygens sum are bitwise the same at any
    /// degree of parallelism (each output row is computed on its own).</summary>
    [Fact]
    public void TheEngine_IsBitwiseTheSame_AtAnyDegreeOfParallelism()
    {
        var lens = Singlet();
        var system = Sys(0.02, new[] { 0.0, 3.0 });
        var pupil = PsfEngine.BuildPupil(lens, system, 1, 0, 24);
        var huygens = PsfEngine.BuildHuygens(lens, system, 1, 0, 16);
        var (us, vs) = PsfEngine.Grid(20, 2e-6, 2e-6);
        foreach (int? dop in new int?[] { 2, 3, null })
        {
            Assert.Equal(PsfEngine.Fft(pupil, 2, 1).Intensity.Cast<double>(), PsfEngine.Fft(pupil, 2, dop).Intensity.Cast<double>());
            Assert.Equal(PsfEngine.Sample(pupil, us, vs, 1).Intensity.Cast<double>(), PsfEngine.Sample(pupil, us, vs, dop).Intensity.Cast<double>());
            Assert.Equal(PsfEngine.Huygens(huygens, us, vs, 1).Intensity.Cast<double>(), PsfEngine.Huygens(huygens, us, vs, dop).Intensity.Cast<double>());
        }
    }

    /// <summary>
    /// Polychromatic rescaling. Two pupils equal but for the wavelength (0.5 and 1.0 µm): the pupil
    /// sum depends on x only through n·a·x/λ, so the 1.0 µm PSF at 2x is the 0.5 µm PSF at x (to
    /// rounding). The polychromatic PSF lies on the shorter wavelength's FFT grid and is
    /// (w₁·I₁ + w₂·(λ₁/λ₂)²·I₂)/(w₁ + w₂·(λ₁/λ₂)²); its peak (the polychromatic Strehl) is 1 for
    /// an unaberrated pair.
    /// </summary>
    [Fact]
    public void ThePolychromaticPsf_RescalesEachWavelengthOntoTheShortestGrid_WithWeightTimesLambdaRatioSquared()
    {
        const int n = 32;
        const double a = 0.04;
        var p1 = PupilFunction.FromFunctions(n, (_, _) => 1, (_, _) => 0, a, a, 0.5e-6);
        var p2 = PupilFunction.FromFunctions(n, (_, _) => 1, (_, _) => 0, a, a, 1.0e-6);
        var xs = new[] { 0.0, 1.3e-6, 4.1e-6, -7.7e-6 };
        var at1 = PsfEngine.Sample(p1, xs, new[] { 0.0 });
        var at2 = PsfEngine.Sample(p2, xs.Select(x => 2 * x).ToArray(), new[] { 0.0 });
        for (int i = 0; i < xs.Length; i++) Close(at1.Intensity[0, i], at2.Intensity[0, i], 1e-12, $"scaled PSF at {xs[i]}");

        double w1 = 1, w2 = 3;
        var poly = PsfEngine.FftPolychromatic(new[] { p2, p1 }, new[] { w2, w1 });
        var native = PsfEngine.NativeSpacing(p1);
        Close(native.Dx, poly.DeltaX, 1e-12 * native.Dx, "the shortest wavelength's grid");
        Assert.Equal(2 * n, poly.X.Count);
        var i1 = PsfEngine.Sample(p1, poly.X, poly.Y);
        var i2 = PsfEngine.Sample(p2, poly.X, poly.Y);
        double f2 = w2 * 0.25;
        for (int r = 0; r < poly.Y.Count; r += 5)
            for (int c = 0; c < poly.X.Count; c += 3)
                Close((w1 * i1.Intensity[r, c] + f2 * i2.Intensity[r, c]) / (w1 + f2), poly.Intensity[r, c], 1e-13, $"polychromatic [{r}, {c}]");
        Close(1, poly.MaximumSample, 1e-12, "polychromatic Strehl of an unaberrated pair");
        Assert.Null(poly.Amplitude);
    }

    /// <summary>
    /// The sampling rule (PsfEngine.CheckSampling): the largest |ΔW| between neighbouring lit
    /// nodes, here for pure defocus W = D(2ρ² − 1) on 32 × 32 nodes, is found by brute force in
    /// the test; above ½ wave the result says ALIASED, between ¼ and ½ it cautions, below ¼ it is
    /// silent. The image period is λ/(n·|a|·ΔP).
    /// </summary>
    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1.5, 1)]
    [InlineData(4.0, 2)]
    public void TheSamplingCheck_WarnsAboveAQuarterWave_AndCallsHalfAWavePerSampleAliased(double defocus, int level)
    {
        const int n = 32;
        var pupil = PupilFunction.FromFunctions(n, (_, _) => 1, (x, y) => defocus * (2 * (x * x + y * y) - 1), 0.05, 0.05, Lambda);
        double expected = 0;
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                if (pupil.Amplitude[i, k] == 0) continue;
                if (k + 1 < n && pupil.Amplitude[i, k + 1] > 0) expected = Math.Max(expected, Math.Abs(pupil.Waves[i, k + 1] - pupil.Waves[i, k]));
                if (i + 1 < n && pupil.Amplitude[i + 1, k] > 0) expected = Math.Max(expected, Math.Abs(pupil.Waves[i + 1, k] - pupil.Waves[i, k]));
            }
        var check = PsfEngine.CheckSampling(pupil);
        Assert.Equal(expected, check.MaxStepWaves);
        Close(Lambda / (0.05 * 2.0 / (n - 1)), check.PeriodX, 1e-12 * check.PeriodX, "image period");
        switch (level)
        {
            case 0:
                Assert.InRange(expected, 0, PsfEngine.CautionStepWaves);
                Assert.Empty(check.Warnings);
                break;
            case 1:
                Assert.InRange(expected, PsfEngine.CautionStepWaves, PsfEngine.AliasStepWaves);
                Assert.Contains(check.Warnings, w => w.StartsWith("Near the sampling limit", StringComparison.Ordinal));
                break;
            default:
                Assert.True(expected > PsfEngine.AliasStepWaves);
                Assert.Contains(check.Warnings, w => w.StartsWith("ALIASED", StringComparison.Ordinal));
                break;
        }
        // An image wider than the period is aliased whatever the wavefront.
        Assert.Contains(PsfEngine.CheckSampling(pupil, 1.01 * check.PeriodX, check.PeriodY).Warnings, w => w.Contains("wider than the period", StringComparison.Ordinal));
    }

    /// <summary>The line spread is the PSF summed across the line, over its peak; the edge spread
    /// its running sum (half the current sample) over the total, so it rises from near 0 to near 1
    /// and never falls. For a centro-symmetric PSF the edge spread at the centre is ½ plus half the
    /// unpaired first sample's share (the FFT grid runs from −M/2 to M/2 − 1).</summary>
    [Fact]
    public void TheLineAndEdgeSpread_AreTheSummedPsfAndItsRunningSum()
    {
        var pupil = PupilFunction.FromFunctions(32, (_, _) => 1, (x, y) => 0.1 * (2 * (x * x + y * y) - 1), 0.05, 0.05, Lambda);
        var psf = PsfEngine.Fft(pupil);
        int m = psf.X.Count;
        var (coordinate, line, edge) = PsfEngine.LineEdgeSpread(psf, parallelToX: true);
        Assert.Same(psf.Y, coordinate);
        var sums = Enumerable.Range(0, m).Select(r => Enumerable.Range(0, m).Sum(c => psf.Intensity[r, c])).ToArray();
        double max = sums.Max(), total = sums.Sum();
        for (int r = 0; r < m; r++) Close(sums[r] / max, line[r], 1e-14, $"line spread [{r}]");
        for (int r = 1; r < m; r++) Assert.True(edge[r] >= edge[r - 1], "the edge spread never falls");
        Close(0.5 + sums[0] / (2 * total), edge[m / 2], 1e-12, "edge spread at the centre");
        Assert.InRange(edge[0], 0, 1e-3);
        Assert.InRange(edge[^1], 1 - 1e-3, 1);
        Assert.Equal(1.0, line.Max());
    }

    [Fact]
    public void ThePupilFunction_RefusesLightOutsideTheCircle_NegativeAmplitude_AndNoLight()
    {
        var a = new double[8, 8];
        var w = new double[8, 8];
        a[0, 0] = 1;                     // the corner node (−1, −1) lies outside the unit circle
        Assert.Throws<ArgumentException>(() => new PupilFunction(a, w, 0.1, 0.1, Lambda, 1));
        a[0, 0] = 0;
        Assert.Throws<ArgumentException>(() => new PupilFunction(a, w, 0.1, 0.1, Lambda, 1));   // no light
        a[4, 4] = -1;
        Assert.Throws<ArgumentException>(() => new PupilFunction(a, w, 0.1, 0.1, Lambda, 1));
        a[4, 4] = 1;
        Assert.Throws<ArgumentException>(() => new PupilFunction(a, w, 0, 0.1, Lambda, 1));     // no scale
        Assert.Equal(1, new PupilFunction(a, w, 0.1, 0.1, Lambda, 1).AmplitudeSum);
    }

    [Fact]
    public void BuildPupil_RefusesAfocalImageSpace_AndCountsRaysThatCarryNoLight()
    {
        var afocal = Sys(0.01, Array.Empty<double>()) with { Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01, AfocalImageSpace = true } };
        Assert.Throws<InvalidOperationException>(() => PsfEngine.BuildPupil(Paraboloid(), afocal, 0, 0, 16));
        Assert.Throws<InvalidOperationException>(() => PsfEngine.BuildHuygens(Paraboloid(), afocal, 0, 0, 16));

        // An aperture of half the beam on the mirror vignettes the outer rays: they carry no light
        // and are counted by reason.
        var lens = Paraboloid();
        var stopped = lens with
        {
            Surfaces = lens.Surfaces.Select((s, i) => i == 1 ? s with { Aperture = new CircularAperture(0, 0.0025) } : s).ToArray(),
        };
        var pupil = PsfEngine.BuildPupil(stopped, Sys(0.01, Array.Empty<double>()), 0, 0, 16);
        Assert.Contains(pupil.Warnings, w => w.Contains("carry no light", StringComparison.Ordinal));
        Assert.True(pupil.Transmission < 0.5, $"half the beam's diameter passes a quarter of its area ({pupil.Transmission:g4})");
    }

    // ---- The analyses ----

    [Fact]
    public void TheAnalyses_AreRegistered_AndReadTheProjectsSurfaces()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        var ids = new[] { FftPsfAnalysis.SavedId, FftPsfCrossSectionAnalysis.SavedId, FftLineEdgeSpreadAnalysis.SavedId, HuygensPsfAnalysis.SavedId, HuygensPsfCrossSectionAnalysis.SavedId, StrehlRatioAnalysis.SavedId };
        var empty = new OpticsAnalysisContext("P", new OpticsSettings());
        var saved = new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 40, Thickness = 6, Index = 1.5, IsStop = true },
            new() { Radius = -120, Thickness = 57.7 },   // near the paraxial focus (BFL ≈ 57.7 mm)
            new(),
        };
        var project = new OpticsAnalysisContext("Singlet", new OpticsSettings { Surfaces = saved });
        foreach (var id in ids)
        {
            Assert.True(registry.TryGet(id, out var analysis), id);
            Assert.Equal("PSF", analysis.Category);
            Assert.Equal(new[] { "No surfaces" }, analysis.Compute(empty, analysis.CreateDefaultSettings(), default).Sections.Select(s => s.Heading));
            var settings = analysis.CreateDefaultSettings() switch
            {
                FftPsfSettings s => s with { Sampling = 16 },
                FftPsfCrossSectionSettings s => s with { Sampling = 16 },
                FftLineEdgeSpreadSettings s => s with { Sampling = 16 },
                HuygensPsfSettings s => s with { PupilSampling = 8, ImageSampling = 16 },
                HuygensPsfCrossSectionSettings s => s with { PupilSampling = 8, ImageSampling = 32 },
                StrehlRatioSettings s => s with { Sampling = 16 },
                var other => other,
            };
            var result = analysis.Compute(project, settings, default);
            Assert.DoesNotContain("No surfaces", result.Sections.Select(s => s.Heading));
            Assert.NotEmpty(result.Plots);
            Assert.NotEmpty(result.Assumptions);
        }
    }

    /// <summary>The map is the engine's FFT PSF (centre crop with Display), the cross-section its
    /// centre row, and the Strehl ratio the engine's; normalising scales the peak to 1.</summary>
    [Fact]
    public void TheFftPsf_DrawsTheEnginesPsf_AndTheCrossSectionIsItsCentreRow()
    {
        var lens = Singlet();
        var system = Sys(0.01, new[] { 0.0, 2.0 });
        var context = Context(system);
        var map = new FftPsfAnalysis(_ => lens).Compute(context, new FftPsfSettings { Sampling = 32, Field = 2, Display = 16 }, default);
        var plot = Assert.IsType<AnalysisMapPlot>(Assert.Single(map.Plots));
        var pupil = PsfEngine.BuildPupil(lens, system, 1, 0, 32, new OpdOptions());
        var engine = PsfEngine.Fft(pupil);
        Assert.Equal(16, plot.Field.Rows);
        for (int r = 0; r < 16; r++)
            for (int c = 0; c < 16; c++) Assert.Equal(engine.Intensity[24 + r, 24 + c], plot.Field.Values[r, c]);
        Close(engine.X[24] * 1e6, plot.Field.X0, 1e-12, "map extent");
        string strehl = map.Sections.Single(s => s.Heading == "FFT PSF").Pairs.Single(p => p.Key == "Strehl ratio").Value;
        Assert.Equal(StudyReport.Number(PsfEngine.Strehl(pupil), "g6"), strehl);

        var section = new FftPsfCrossSectionAnalysis(_ => lens).Compute(context, new FftPsfCrossSectionSettings { Sampling = 32, Field = 2, Normalize = true }, default);
        var curve = Assert.IsType<LinePlot>(Assert.Single(section.Plots)).Curves[0];
        Assert.Equal(64, curve.X.Count);
        double max = engine.MaximumSample;
        for (int c = 0; c < 64; c++) Close(engine.Intensity[32, c] / max, curve.Y[c], 1e-15, $"section [{c}]");
    }

    /// <summary>A strongly defocused paraboloid on a coarse grid is aliased, and the analyses say
    /// so first; an image delta larger than the FFT's own is refused.</summary>
    [Fact]
    public void AnAliasedPsf_IsWarnedAbove_AndAnOversizedImageDeltaIsRefused()
    {
        var lens = Paraboloid(-0.5e-3);
        var system = Sys(0.04, Array.Empty<double>());
        var context = Context(system);
        var result = new FftPsfAnalysis(_ => lens).Compute(context, new FftPsfSettings { Sampling = 16 }, default);
        Assert.Equal("Sampling and validity warnings", result.Sections[0].Heading);
        Assert.Contains(result.Sections[0].Lines, l => l.StartsWith("ALIASED", StringComparison.Ordinal));
        Assert.Contains(result.Assumptions, a => a.StartsWith("Warning: ALIASED", StringComparison.Ordinal));

        var focused = new FftPsfAnalysis(_ => Paraboloid()).Compute(Context(Sys(0.01, Array.Empty<double>())), new FftPsfSettings { Sampling = 32 }, default);
        Assert.Equal("FFT PSF", focused.Sections[0].Heading);

        var pupil = PsfEngine.BuildPupil(Paraboloid(), Sys(0.01, Array.Empty<double>()), 0, 0, 32);
        double native = PsfEngine.NativeSpacing(pupil).Dx;
        Assert.Throws<ArgumentException>(() => new FftPsfAnalysis(_ => Paraboloid()).Compute(Context(Sys(0.01, Array.Empty<double>())),
            new FftPsfSettings { Sampling = 32, ImageDeltaMicrometers = 2 * native * 1e6 }, default));
        var finer = new FftPsfAnalysis(_ => Paraboloid()).Compute(Context(Sys(0.01, Array.Empty<double>())),
            new FftPsfSettings { Sampling = 32, ImageDeltaMicrometers = native * 1e6 / 2 }, default);
        var finerMap = Assert.IsType<AnalysisMapPlot>(Assert.Single(finer.Plots));
        Close(native * 1e6 / 2, finerMap.Field.DeltaX, 1e-9, "chosen image delta");
    }

    /// <summary>Polychromatic FFT and Huygens PSFs of three wavelengths run, and their Strehl ratio
    /// is reported; a phase display with every wavelength uses the primary (help, "FFT PSF").</summary>
    [Fact]
    public void ThePolychromaticAnalyses_Run_AndAPhaseDisplayUsesThePrimaryWavelength()
    {
        var lens = Singlet();
        var system = Sys(0.01, Array.Empty<double>(), 0.48, 0.55, 0.65);
        var context = Context(system);
        var fft = new FftPsfAnalysis(_ => lens).Compute(context, new FftPsfSettings { Sampling = 16 }, default);
        Assert.Contains("polychromatic", Assert.IsType<AnalysisMapPlot>(Assert.Single(fft.Plots)).Title);
        var phase = new FftPsfAnalysis(_ => lens).Compute(context, new FftPsfSettings { Sampling = 16, Type = PsfDisplayType.Phase }, default);
        Assert.Contains(phase.Assumptions, a => a.Contains("use the primary wavelength", StringComparison.Ordinal));
        Assert.Contains("0.48 µm", Assert.IsType<AnalysisMapPlot>(Assert.Single(phase.Plots)).Title);
        var huygens = new HuygensPsfAnalysis(_ => lens).Compute(context, new HuygensPsfSettings { PupilSampling = 8, ImageSampling = 16 }, default);
        Assert.Contains(huygens.Assumptions, a => a.StartsWith("Polychromatic", StringComparison.Ordinal));
        var strehl = new StrehlRatioAnalysis(_ => lens).Compute(context, new StrehlRatioSettings { Sampling = 16 }, default);
        Assert.Equal(4, strehl.Sections.Single(s => s.Heading == "Strehl ratio").TableRows.Count);   // three wavelengths and the polychromatic row
    }

    /// <summary>The Strehl analysis lists the engine's Strehl ratio, the amplitude-weighted RMS and
    /// the second-order estimate 1 − (2πσ)² for each field.</summary>
    [Fact]
    public void TheStrehlAnalysis_ListsTheEnginesRatio_TheRms_AndTheSecondOrderEstimate()
    {
        var lens = Singlet();
        var system = Sys(0.008, new[] { 0.0, 1.5 });
        var result = new StrehlRatioAnalysis(_ => lens).Compute(Context(system), new StrehlRatioSettings { Sampling = 32 }, default);
        var rows = result.Sections.Single(s => s.Heading == "Strehl ratio").TableRows;
        Assert.Equal(2, rows.Count);
        for (int f = 0; f < 2; f++)
        {
            var pupil = PsfEngine.BuildPupil(lens, system, f, 0, 32, new OpdOptions());
            double sigma = StrehlRatioAnalysis.WeightedRms(pupil);
            Assert.Equal(StudyReport.Number(PsfEngine.Strehl(pupil), "g6"), rows[f][2]);
            Assert.Equal(StudyReport.Number(sigma, "g6"), rows[f][3]);
            Assert.Equal(StudyReport.Number(1 - Math.Pow(2 * Math.PI * sigma, 2), "g6"), rows[f][4]);
        }
    }

    /// <summary>The Huygens PSF is centred on the chief ray or the centroid, with the help's default
    /// spacing λF/√n_p; its cross-section equals the map's centre row.</summary>
    [Fact]
    public void TheHuygensPsf_UsesTheHelpsDefaultSpacing_AndItsCrossSectionIsTheCentreRow()
    {
        var lens = Paraboloid();
        var system = Sys(0.01, Array.Empty<double>());
        var context = Context(system);
        var map = new HuygensPsfAnalysis(_ => lens).Compute(context, new HuygensPsfSettings { PupilSampling = 16, ImageSampling = 16 }, default);
        var field = Assert.IsType<AnalysisMapPlot>(Assert.Single(map.Plots)).Field;
        var pupil = PsfEngine.BuildHuygens(lens, system, 0, 0, 16);
        double delta = PsfEngine.DefaultHuygensDelta(pupil.Wavelength, pupil.FNumber, 16);
        Close(delta * 1e6, field.DeltaX, 1e-9, "default image delta");
        Close(-8 * delta * 1e6, field.X0, 1e-9, "the centre sample is m/2");
        var section = new HuygensPsfCrossSectionAnalysis(_ => lens).Compute(context,
            new HuygensPsfCrossSectionSettings { PupilSampling = 16, ImageSampling = 16 }, default);
        var curve = Assert.IsType<LinePlot>(Assert.Single(section.Plots)).Curves[0];
        for (int c = 0; c < 16; c++) Close(field.Values[8, c], curve.Y[c], 1e-14, $"section [{c}]");
        var centroid = new HuygensPsfAnalysis(_ => lens).Compute(context, new HuygensPsfSettings { PupilSampling = 16, ImageSampling = 16, UseCentroid = true }, default);
        Assert.Contains(centroid.Assumptions, a => a.Contains("the rays' centroid", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLineEdgeSpreadAnalysis_PlotsTheEnginesSpread()
    {
        var lens = Paraboloid();
        var system = Sys(0.01, Array.Empty<double>());
        var result = new FftLineEdgeSpreadAnalysis(_ => lens).Compute(Context(system), new FftLineEdgeSpreadSettings { Sampling = 16, Spread = SpreadFunction.Edge }, default);
        var curve = Assert.IsType<LinePlot>(Assert.Single(result.Plots)).Curves[0];
        var psf = PsfEngine.Fft(PsfEngine.BuildPupil(lens, system, 0, 0, 16, new OpdOptions()));
        var (_, _, edge) = PsfEngine.LineEdgeSpread(psf, true);
        Assert.Equal(edge, curve.Y);
    }
}
