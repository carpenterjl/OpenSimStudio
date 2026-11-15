using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-39: the MTF engine's identities (the autocorrelation is the transform of OP-38's FFT PSF; the
/// polychromatic weights; the geometric engine; the afocal pupil) and the fourteen analyses.
/// Expected values are derived in the comments; the gate itself is <c>Gates/MtfGateTests</c>.
/// OpticStudio's behaviour is read from its 2025 R2 help pages saved under
/// plans/sources/optics/op-39-help/.
/// </summary>
public sealed class MtfAnalysisTests
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

    // A singlet near its paraxial focus (BFL ≈ 64.2 mm), as in PsfAnalysisTests.
    private static SequentialLens Singlet() => Lens(double.PositiveInfinity, S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064));

    // A paraboloid at F/20 (EPD 5 mm) focusing at R/2 = −0.1 m: zero OPD on axis (OP-38's gate).
    private static SequentialLens Paraboloid(double defocus = 0) =>
        Lens(double.PositiveInfinity, S(-0.2, -0.1 + defocus, k: -1, stop: true, mirror: true));

    private static OpticsAnalysisContext Context(OpticalSystemData system) =>
        new("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });

    private static LinePlot Plot(OpticsAnalysisResult result) => Assert.IsType<LinePlot>(result.Plots[0]);

    private static OpdOptions Options() => new() { Reference = OpdReference.ExitPupil, ReferencePoint = OpdReferencePoint.ChiefRay };

    // ---- The engine ----

    /// <summary>
    /// The autocorrelation is the transform of OP-38's FFT PSF. That PSF samples one period
    /// T = 1/|step| of I(x) = Σ_P Σ_Q f(P)f*(Q)e^{−2πic(P−Q)x}, whose frequencies are j·step for
    /// |j| ≤ n − 1, at M = 2n points; the DFT of those samples at k·step (|k| ≤ n − 1) picks out
    /// j = k exactly, because |j − k| ≤ 2n − 2 &lt; M, so Σ I·e^{−2πik·step·x} / Σ I equals the
    /// autocorrelation at a k-node shift to rounding, for either sign of the pupil scale.
    /// </summary>
    [Theory]
    [InlineData(0.04, 0.03)]
    [InlineData(-0.04, 0.05)]
    [InlineData(0.03, -0.02)]
    public void TheOtf_IsTheTransformOfOp38sFftPsf(double ax, double ay)
    {
        const int n = 24;
        var pupil = PupilFunction.FromFunctions(n, (x, y) => 1 - 0.3 * (x * x + y * y), (x, y) => 0.4 * x * (x * x + y * y) + 0.2 * y * y + 0.1 * x * y, ax, ay, Lambda, 1.5);
        var otf = MtfPupil.FromPupil(pupil);
        var psf = PsfEngine.Fft(pupil);
        Close(1.5 * Math.Abs(ax) / Lambda * 2 * pupil.NodeSpacing * (n - 1) / 2, otf.CutoffX, 1e-9 * otf.CutoffX, "cutoff 2n|a|/λ");
        foreach (int k in new[] { 0, 1, 3, 7, 15, 23, -5 })
        {
            foreach (var o in new[] { MtfOrientation.Sagittal, MtfOrientation.Tangential })
            {
                double nu = k * (o == MtfOrientation.Sagittal ? otf.StepX : otf.StepY);
                var a = otf.Otf(nu, o);
                var b = MtfEngine.PsfOtf(psf, nu, o);
                Close(b.Real, a.Real, 1e-12, $"real OTF at {k} nodes, {o}");
                Close(b.Imaginary, a.Imaginary, 1e-12, $"imaginary OTF at {k} nodes, {o}");
            }
        }
        // Over the plane too, at whole-node shifts in both axes.
        var plane = MtfPupil.FromPupil(pupil, keepPlane: true);
        foreach (var (kx, ky) in new[] { (2, 3), (-4, 1), (5, -6), (0, 0) })
        {
            var a = plane.Otf(kx * plane.StepX, ky * plane.StepY);
            var b = MtfEngine.PsfOtf(psf, kx * plane.StepX, ky * plane.StepY);
            Close(b.Real, a.Real, 1e-12, $"real OTF at ({kx}, {ky})");
            Close(b.Imaginary, a.Imaginary, 1e-12, $"imaginary OTF at ({kx}, {ky})");
            var exact = plane.OtfAtLag(kx, ky);   // k·step/step may miss k by an ulp: the interpolation then moves by ~1e-16
            Close(exact.Real, a.Real, 1e-14, $"whole-lag real OTF at ({kx}, {ky})");
            Close(exact.Imaginary, a.Imaginary, 1e-14, $"whole-lag imaginary OTF at ({kx}, {ky})");
        }
        // The axis cuts of the plane are the axis cuts.
        var cut = otf.Otf(3.4 * otf.StepX, MtfOrientation.Sagittal);
        var onPlane = plane.Otf(3.4 * plane.StepX, 0.0);
        Close(cut.Real, onPlane.Real, 1e-15, "axis cut of the plane (real)");
        Close(cut.Imaginary, onPlane.Imaginary, 1e-15, "axis cut of the plane (imaginary)");
    }

    /// <summary>The PSF energy of a wavelength's term (<see cref="MtfPupil.PsfEnergy"/>) is the
    /// FFT PSF's Σ I·Δx·Δy (Parseval over one period, derived in the property's remarks).</summary>
    [Fact]
    public void ThePsfEnergy_IsTheFftPsfsIntegral()
    {
        var pupil = PupilFunction.FromFunctions(20, (x, y) => 1 - 0.2 * x, (x, y) => 0.3 * y * y, 0.03, -0.035, 0.6e-6, 1.2);
        var psf = PsfEngine.Fft(pupil);
        double sum = 0;
        foreach (var v in psf.Intensity) sum += v;
        double integral = sum * psf.DeltaX * psf.DeltaY;
        var otf = MtfPupil.FromPupil(pupil);
        Close(integral, otf.PsfEnergy, 1e-12 * integral, "PSF energy");
    }

    /// <summary>
    /// Two unaberrated, equal pupils at 0.5 and 1.0 µm: each PSF energy is Σ A²/((Σ A)²|step_x·step_y|)
    /// with step ∝ 1/λ, so w·(λ_min/λ)²·energy is w·λ_min²·(the same constant): the polychromatic
    /// OTF is the weight-mean of the two OTFs (derived). Weights must be finite and not all zero.
    /// </summary>
    [Fact]
    public void ThePolychromaticOtf_WeighsEachWavelengthByItsPsfsEnergy()
    {
        var p1 = MtfPupil.FromPupil(PupilFunction.FromFunctions(32, (_, _) => 1, (_, _) => 0, 0.04, 0.04, 0.5e-6));
        var p2 = MtfPupil.FromPupil(PupilFunction.FromFunctions(32, (_, _) => 1, (_, _) => 0, 0.04, 0.04, 1.0e-6));
        double w1 = 1, w2 = 3;
        foreach (double nu in new[] { 0.0, 2e4, 7e4, 1.1e5, 1.5e5 })
        {
            var expected = (w1 * p1.Otf(nu, MtfOrientation.Tangential) + w2 * p2.Otf(nu, MtfOrientation.Tangential)) / (w1 + w2);
            var poly = MtfEngine.PolychromaticOtf(new[] { p2, p1 }, new[] { w2, w1 }, nu, MtfOrientation.Tangential);
            Close(expected.Real, poly.Real, 1e-12, $"polychromatic at {nu}");
            Close(expected.Imaginary, poly.Imaginary, 1e-12, $"polychromatic at {nu} (imaginary)");
        }
        Assert.Throws<ArgumentException>(() => MtfEngine.PolychromaticOtf(new[] { p1, p2 }, new[] { 0.0, 0.0 }, 1e4, MtfOrientation.Sagittal));
        Assert.Throws<ArgumentException>(() => MtfEngine.PolychromaticOtf(new[] { p1, p2 }, new[] { 1.0 }, 1e4, MtfOrientation.Sagittal));
    }

    /// <summary>
    /// A paraboloid moved 0.5 mm off focus sends every ray of the pupil to a disk of radius
    /// R = Δ·h/f at F/20 (h/f = 0.025 at the rim; the paraboloid sends each ray exactly through the
    /// focus, so the landing radius is linear in the pupil radius to (h/2f)² ≈ 1.6·10⁻⁴): its
    /// geometric MTF is 2J₁(z)/z, z = 2πνR (derived in the gate's remarks). 129 × 129 rays: the
    /// lattice sum is within 1.2·10⁻³ of the closed form for z ≤ 12; 3·10⁻³ is allowed.
    /// </summary>
    [Fact]
    public void TheGeometricMtf_OfADefocusedParaboloid_Is2J1zOverz()
    {
        double delta = 0.0005;
        var lens = Paraboloid(delta);
        var system = Sys(0.005, Array.Empty<double>());
        var spot = MtfEngine.BuildGeometric(lens, system, 0, 0, 129);
        Assert.Empty(spot.Warnings);
        double radius = delta * 0.025;
        double rmax = spot.Rays.Max(r => Math.Sqrt(r.X * r.X + r.Y * r.Y));
        Close(radius, rmax, 1e-3 * radius, "the geometric spot's radius Δ·h/f");
        for (int i = 0; i <= 24; i++)
        {
            double z = i * 0.5;
            double nu = z / (2 * Math.PI * radius);
            double expected = Math.Abs(z == 0 ? 1 : 2 * Bessel.J1(z) / z);
            Close(expected, MtfEngine.GeometricOtf(spot.Rays, nu, MtfOrientation.Sagittal).Magnitude, 3e-3, $"geometric MTF at z = {z}");
        }
        // The diffraction factor is the unaberrated pupil's: the FFT engine's on the same lens.
        Assert.NotNull(spot.Diffraction);
        var fft = MtfEngine.BuildPupil(lens, system, 0, 0, 129, Options(), unaberrated: true);
        Close(fft.CutoffX, spot.CutoffX, 1e-9 * fft.CutoffX, "diffraction cutoff");
        Close(fft.Otf(0.3 * fft.CutoffX, MtfOrientation.Sagittal).Magnitude, spot.Diffraction!.Otf(0.3 * fft.CutoffX, MtfOrientation.Sagittal).Magnitude, 1e-9, "diffraction factor");
    }

    /// <summary>
    /// An afocal pupil: a paraboloid with the object at its focus (0.1 m in front of R = −0.2 m)
    /// reflects a collimated beam, exactly, so the OPD is zero and the MTF is the perfect pupil's in
    /// cycles per radian, with cutoff 2n·b/λ = D/λ (help, "FFT MTF": "the exit pupil diameter
    /// divided by the wavelength"), D the 5 mm beam. The beam's half-width per unit pupil coordinate
    /// departs from D/2 by the mirror's sag at the rim, (h²/2R)/0.1 ≈ 1.6·10⁻⁴; 10⁻³ is allowed. The
    /// MTF against s = ν/cutoff: 128 × 128 nodes, edge error 9·10⁻⁴; 3·10⁻³ is allowed.
    /// </summary>
    [Fact]
    public void AnAfocalPupil_HasCutoffDiameterOverLambda_InCyclesPerRadian()
    {
        var lens = Lens(0.1, S(-0.2, -0.05, k: -1, stop: true, mirror: true));
        var system = Sys(0.005, Array.Empty<double>()) with { Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.005, AfocalImageSpace = true } };
        var pupil = MtfEngine.BuildPupil(lens, system, 0, 0, 128);
        Assert.True(pupil.Afocal);
        double cutoff = 0.005 / pupil.Wavelength;
        Close(cutoff, pupil.CutoffX, 1e-3 * cutoff, "afocal cutoff D/λ (x)");
        Close(cutoff, pupil.CutoffY, 1e-3 * cutoff, "afocal cutoff D/λ (y)");
        for (int i = 0; i <= 10; i++)
        {
            double s = i / 10.0;
            Close(MtfEngine.DiffractionLimitedCircular(s), pupil.Otf(s * pupil.CutoffY, MtfOrientation.Tangential).Magnitude, 3e-3, $"afocal MTF at s = {s}");
        }
        // The analysis reports it in cycles per milliradian.
        var result = new FftMtfAnalysis(_ => lens).Compute(Context(system), new FftMtfSettings { Sampling = 32 }, default);
        Assert.Contains("cycles/mrad", Plot(result).XLabel);
        Close(cutoff / 1000, Plot(result).Curves[0].X[^1], 2e-3 * cutoff / 1000, "plotted up to the cutoff in cycles/mrad");
    }

    /// <summary>The public one-point call is the FFT pupil's OTF at the frequency in MTF units (× 1000
    /// to cycles per meter), and the square wave is offered with the FFT method only.</summary>
    [Fact]
    public void Evaluate_IsThePupilsOtf_AtTheFrequencyInMtfUnits()
    {
        var lens = Singlet();
        var system = Sys(0.02, new[] { 0.0, 3.0 });
        var pupil = MtfEngine.BuildPupil(lens, system, 1, 0, 32, Options());
        var value = MtfEngine.Evaluate(lens, system, 1, new[] { 0 }, 40, new MtfOptions { Sampling = 32 });
        Assert.Equal(pupil.Otf(40e3, MtfOrientation.Tangential).Magnitude, value.Tangential);
        Assert.Equal(pupil.Otf(40e3, MtfOrientation.Sagittal).Magnitude, value.Sagittal);
        Assert.Equal((value.Tangential + value.Sagittal) / 2, value.Average);
        var square = MtfEngine.Evaluate(lens, system, 1, new[] { 0 }, 40, new MtfOptions { Sampling = 32, SquareWave = true });
        Assert.Equal(MtfEngine.SquareWave(f => pupil.Otf(f, MtfOrientation.Tangential).Magnitude, 40e3, pupil.CutoffY), square.Tangential);
        Assert.Throws<ArgumentException>(() => MtfEngine.Evaluate(lens, system, 1, new[] { 0 }, 40, new MtfOptions { Method = MtfMethod.Geometric, SquareWave = true }));
        var geometric = MtfEngine.Evaluate(lens, system, 1, new[] { 0 }, 40, new MtfOptions { Method = MtfMethod.Geometric, Sampling = 32 });
        Assert.InRange(geometric.Tangential, 0, 1);
    }

    // ---- The analyses ----

    [Fact]
    public void TheAnalyses_AreRegistered_AndReadTheProjectsSurfaces()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        var ids = new[]
        {
            FftMtfAnalysis.SavedId, FftThroughFocusMtfAnalysis.SavedId, FftSurfaceMtfAnalysis.SavedId, FftMtfVsFieldAnalysis.SavedId, FftMtfMapAnalysis.SavedId,
            HuygensMtfAnalysis.SavedId, HuygensThroughFocusMtfAnalysis.SavedId, HuygensSurfaceMtfAnalysis.SavedId, HuygensMtfVsFieldAnalysis.SavedId,
            GeometricMtfAnalysis.SavedId, GeometricThroughFocusMtfAnalysis.SavedId, GeometricMtfVsFieldAnalysis.SavedId, GeometricMtfMapAnalysis.SavedId,
            ContrastLossMapAnalysis.SavedId,
        };
        Assert.Equal(ids.Length, ids.Distinct().Count());
        var empty = new OpticsAnalysisContext("P", new OpticsSettings());
        var saved = new List<OpticsSurfaceSettings>
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 40, Thickness = 6, Index = 1.5, IsStop = true },
            new() { Radius = -120, Thickness = 57.7 },   // near the paraxial focus (BFL ≈ 57.7 mm)
            new(),
        };
        var project = new OpticsAnalysisContext("Singlet", new OpticsSettings
        {
            Surfaces = saved, SystemData = SystemDataSettings.ToSettings(Sys(0.01, new[] { 0.0, 2.0 })),
        });
        foreach (var id in ids)
        {
            Assert.True(registry.TryGet(id, out var analysis), id);
            Assert.Equal("MTF", analysis.Category);
            Assert.Equal(new[] { "No surfaces" }, analysis.Compute(empty, analysis.CreateDefaultSettings(), default).Sections.Select(s => s.Heading));
            var settings = analysis.CreateDefaultSettings() switch
            {
                FftMtfSettings s => s with { Sampling = 16 },
                FftThroughFocusMtfSettings s => s with { Sampling = 16, Steps = 3 },
                FftSurfaceMtfSettings s => s with { Sampling = 16 },
                FftMtfVsFieldSettings s => s with { Sampling = 16, FieldDensity = 2 },
                FftMtfMapSettings s => s with { Sampling = 16, XPixels = 2, YPixels = 2 },
                HuygensMtfSettings s => s with { PupilSampling = 8, ImageSampling = 16 },
                HuygensThroughFocusMtfSettings s => s with { PupilSampling = 8, ImageSampling = 16, Steps = 3 },
                HuygensSurfaceMtfSettings s => s with { PupilSampling = 8, ImageSampling = 16 },
                HuygensMtfVsFieldSettings s => s with { PupilSampling = 8, ImageSampling = 16, FieldDensity = 2 },
                GeometricMtfSettings s => s with { Sampling = 16 },
                GeometricThroughFocusMtfSettings s => s with { Sampling = 16, Steps = 3 },
                GeometricMtfVsFieldSettings s => s with { Sampling = 16, FieldDensity = 2 },
                GeometricMtfMapSettings s => s with { Sampling = 16, XPixels = 2, YPixels = 2 },
                ContrastLossMapSettings s => s with { Sampling = 9 },
                var other => other,
            };
            var result = analysis.Compute(project, settings, default);
            Assert.DoesNotContain("No surfaces", result.Sections.Select(s => s.Heading));
            Assert.NotEmpty(result.Plots);
            Assert.NotEmpty(result.Assumptions);
            // Settings survive the project file.
            Assert.Equal(settings, OpticsAnalysisSettingsCodec.Clone(analysis, settings));
        }
    }

    /// <summary>
    /// The FFT MTF plots the engine's OTF at 101 frequencies up to the largest cutoff, tangential and
    /// sagittal per field, and the diffraction limit (the reference field (0, 0) with zero OPD). On a
    /// paraboloid on axis the OPD is zero (to rounding), so the field's curve is the diffraction limit;
    /// the square wave is (4/π)M above a third of the cutoff.
    /// </summary>
    [Fact]
    public void TheFftMtf_PlotsTheEnginesOtf_AndItsDiffractionLimit()
    {
        var lens = Paraboloid();
        var system = Sys(0.005, Array.Empty<double>());
        var context = Context(system);
        var result = new FftMtfAnalysis(_ => lens).Compute(context, new FftMtfSettings { Sampling = 32 }, default);
        var plot = Plot(result);
        Assert.Equal(new[] { "Field 1 (0, 0°) T", "Field 1 (0, 0°) S", "Diffraction limit" }, plot.Curves.Select(c => c.Name));
        var pupil = MtfEngine.BuildPupil(lens, system, 0, 0, 32, Options());
        var t = plot.Curves[0];
        Assert.Equal(MtfRuns.FrequencyPoints, t.X.Count);
        Close(Math.Max(pupil.CutoffX, pupil.CutoffY) / 1000, t.X[^1], 1e-12 * t.X[^1], "plotted to the cutoff");
        for (int i = 0; i < t.X.Count; i += 10)
        {
            Close(pupil.Otf(t.X[i] * 1000, MtfOrientation.Tangential).Magnitude, t.Y[i], 1e-12, $"the engine's OTF at {t.X[i]}");
            Close(plot.Curves[2].Y[i], t.Y[i], 1e-9, $"zero OPD: the diffraction limit at {t.X[i]}");
        }

        var square = Plot(new FftMtfAnalysis(_ => lens).Compute(context, new FftMtfSettings { Sampling = 32, Type = MtfDataType.SquareWave }, default));
        for (int i = 40; i < 101; i += 10)
            Close(4 / Math.PI * t.Y[i], square.Curves[0].Y[i], 1e-12, $"square wave above a third of the cutoff, {t.X[i]}");
        Assert.Equal(1.0, square.Curves[0].Y[0]);

        var phase = Plot(new FftMtfAnalysis(_ => lens).Compute(context, new FftMtfSettings { Sampling = 32, Type = MtfDataType.Phase }, default));
        Assert.Equal(2, phase.Curves.Count);   // no diffraction limit on a phase plot
    }

    [Fact]
    public void TheUnitsAndModes_TheEnginesDoNotCover_AreRefused()
    {
        var lens = Singlet();
        var mrad = Sys(0.02, Array.Empty<double>()) with { Units = new SystemUnits { MtfUnit = MtfUnit.CyclesPerMilliradian } };
        var ex = Assert.Throws<InvalidOperationException>(() => new FftMtfAnalysis(_ => lens).Compute(Context(mrad), new FftMtfSettings { Sampling = 16 }, default));
        Assert.Contains("cycles per milliradian", ex.Message);

        var afocalLens = Lens(0.1, S(-0.2, -0.05, k: -1, stop: true, mirror: true));
        var afocal = Sys(0.005, Array.Empty<double>()) with { Aperture = new Aperture { Value = 0.005, AfocalImageSpace = true } };
        Assert.Contains("diopters", Assert.Throws<InvalidOperationException>(() =>
            new FftThroughFocusMtfAnalysis(_ => afocalLens).Compute(Context(afocal), new FftThroughFocusMtfSettings { Sampling = 16 }, default)).Message);
        Assert.Contains("afocal", Assert.Throws<InvalidOperationException>(() =>
            new HuygensMtfAnalysis(_ => afocalLens).Compute(Context(afocal), new HuygensMtfSettings { PupilSampling = 8, ImageSampling = 16 }, default)).Message);

        var system = Context(Sys(0.02, new[] { 0.0, 3.0 }));
        Assert.Throws<ArgumentException>(() => new FftMtfAnalysis(_ => lens).Compute(system, new FftMtfSettings { Sampling = 8 }, default));
        Assert.Throws<ArgumentException>(() => new FftMtfVsFieldAnalysis(_ => lens).Compute(system, new FftMtfVsFieldSettings { Frequency1 = 0, Frequency2 = 0, Frequency3 = 0 }, default));
        Assert.Throws<ArgumentException>(() => new GeometricMtfMapAnalysis(_ => lens).Compute(system, new GeometricMtfMapSettings { Data = MtfMapData.Maximum }, default));
        Assert.Throws<ArgumentException>(() => new FftThroughFocusMtfAnalysis(_ => lens).Compute(system, new FftThroughFocusMtfSettings { Frequency = 0 }, default));
    }

    /// <summary>Through focus: the middle of three planes is the nominal focus, where the value is
    /// the FFT MTF's at the frequency (the same computation).</summary>
    [Fact]
    public void ThroughFocus_AtTheNominalFocus_IsTheMtfAtTheFrequency()
    {
        var lens = Singlet();
        var system = Sys(0.02, new[] { 0.0, 3.0 });
        var result = new FftThroughFocusMtfAnalysis(_ => lens).Compute(Context(system),
            new FftThroughFocusMtfSettings { Sampling = 32, Steps = 3, DeltaFocus = 0.2, Frequency = 25, Field = 2, Wavelength = 1 }, default);
        var plot = Plot(result);
        Assert.Equal(new[] { -0.2, 0.0, 0.2 }, plot.Curves[0].X);
        var value = MtfEngine.Evaluate(lens, system, 1, new[] { 0 }, 25, new MtfOptions { Sampling = 32 });
        Assert.Equal(value.Tangential, plot.Curves[0].Y[1]);
        Assert.Equal(value.Sagittal, plot.Curves[1].Y[1]);
        Assert.Contains("Every focus shift has a value.", result.Text);
    }

    /// <summary>MTF vs field at the axis and the map's centre pixel are the MTF of the on-axis
    /// field (a field (0, 0) appended to the table traces the same rays as field 1 at (0, 0)); the
    /// map's minimum, average and maximum bracket each other; both are bitwise the same at any
    /// degree of parallelism.</summary>
    [Fact]
    public void TheFieldScans_AtTheAxis_AreTheOnAxisMtf_AndDeterministic()
    {
        var lens = Singlet();
        var system = Sys(0.02, new[] { 0.0, 3.0 });
        var context = Context(system);
        var axis = MtfEngine.Evaluate(lens, system, 0, new[] { 0 }, 30, new MtfOptions { Sampling = 16 });

        var settings = new FftMtfVsFieldSettings { Sampling = 16, FieldDensity = 3, Frequency1 = 30, Frequency2 = 0, Frequency3 = 0, Wavelength = 1 };
        var serial = new FftMtfVsFieldAnalysis(_ => lens) { MaxDegreeOfParallelism = 1 }.Compute(context, settings, default);
        var parallel = new FftMtfVsFieldAnalysis(_ => lens).Compute(context, settings, default);
        Assert.Equal(serial.Text, parallel.Text);
        var plot = Plot(serial);
        Assert.Equal(new[] { 0.0, 1.5, 3.0 }, plot.Curves[0].X);
        Close(axis.Tangential, plot.Curves[0].Y[0], 1e-12, "MTF vs field at the axis, T");
        Close(axis.Sagittal, plot.Curves[1].Y[0], 1e-12, "MTF vs field at the axis, S");

        MtfMapSettingsCheck(lens, context, axis);
    }

    private static void MtfMapSettingsCheck(SequentialLens lens, OpticsAnalysisContext context, MtfValue axis)
    {
        double[,] Map(MtfMapData data, int? dop)
        {
            var s = new FftMtfMapSettings { Sampling = 16, XPixels = 3, YPixels = 3, XFieldWidth = 3, YFieldWidth = 3, Frequency = 30, Wavelength = 1, Data = data };
            var r = new FftMtfMapAnalysis(_ => lens) { MaxDegreeOfParallelism = dop }.Compute(context, s, default);
            return Assert.IsType<AnalysisMapPlot>(Assert.Single(r.Plots)).Field.Values;
        }
        var t = Map(MtfMapData.Tangential, 1);
        Assert.Equal(t.Cast<double>(), Map(MtfMapData.Tangential, null).Cast<double>());
        Close(axis.Tangential, t[1, 1], 1e-12, "the map's centre pixel");
        var min = Map(MtfMapData.Minimum, 1);
        var avg = Map(MtfMapData.Average, 1);
        var max = Map(MtfMapData.Maximum, 1);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                Assert.True(min[r, c] <= avg[r, c] && avg[r, c] <= max[r, c], $"pixel [{r}, {c}]");
    }

    /// <summary>The geometric MTF multiplied by the diffraction limit, on a paraboloid at focus (every
    /// ray at the chief ray to rounding, so the geometric factor is 1), is the diffraction-limited
    /// FFT MTF of the same pupil.</summary>
    [Fact]
    public void TheGeometricMtf_OfAPerfectLens_IsItsDiffractionLimit()
    {
        var lens = Paraboloid();
        var system = Sys(0.005, Array.Empty<double>());
        var result = new GeometricMtfAnalysis(_ => lens).Compute(Context(system), new GeometricMtfSettings { Sampling = 32 }, default);
        var plot = Plot(result);
        var pupil = MtfEngine.BuildPupil(lens, system, 0, 0, 32, Options(), unaberrated: true);
        for (int i = 0; i < plot.Curves[0].X.Count; i += 10)
            Close(pupil.Otf(plot.Curves[0].X[i] * 1000, MtfOrientation.Tangential).Magnitude, plot.Curves[0].Y[i], 1e-6, $"geometric × diffraction at {plot.Curves[0].X[i]}");
    }

    /// <summary>
    /// The contrast loss map: on a paraboloid at focus the OPD is zero, so every pair of rays is in
    /// phase and the loss is zero; the default frequency is 5 % of the cutoff, so the pupil shift
    /// λν/(n|a|) is 0.1 of the normalised pupil (derived: ν/ν_c = 0.05 and the shift is 2ν/ν_c).
    /// Defocused, the loss is 0.5(1 − cos 2πΔW) of the pair's OPD difference, checked at the centre.
    /// </summary>
    [Fact]
    public void TheContrastLossMap_IsZeroForAPerfectLens_AndThePairsPhaseDifferenceOtherwise()
    {
        var system = Sys(0.005, Array.Empty<double>());
        var perfect = new ContrastLossMapAnalysis(_ => Paraboloid()).Compute(Context(system), new ContrastLossMapSettings { Sampling = 9 }, default);
        Assert.Equal(2, perfect.Plots.Count);
        foreach (var plot in perfect.Plots.Cast<AnalysisMapPlot>())
            foreach (var v in plot.Field.Values)
                Assert.True(double.IsNaN(v) || v < 1e-9, $"contrast loss {v:R} of a perfect lens");
        Assert.Contains("0.1, 0.1", perfect.Text);

        var defocused = Paraboloid(0.002);
        var result = new ContrastLossMapAnalysis(_ => defocused).Compute(Context(system), new ContrastLossMapSettings { Sampling = 9, Frequency = 20 }, default);
        var sagittal = Assert.IsType<AnalysisMapPlot>(result.Plots[1]);
        var pupil = PsfEngine.BuildPupil(defocused, system, 0, 0, ContrastLossMapAnalysis.MtfSampling, Options());
        double dx = pupil.Wavelength * 20e3 / (pupil.ImageIndex * Math.Abs(pupil.ScaleX));
        // The centre point (node 4 of 9 is Px = Py = 0): rays at (±dx/2, 0).
        var opd = OpdEngine.Compute(defocused, system, 0, 0, new[] { (dx / 2, 0.0), (-dx / 2, 0.0) }, Options());
        double expected = 0.5 * (1 - Math.Cos(2 * Math.PI * (opd.Samples[0].Waves - opd.Samples[1].Waves)));
        Close(expected, sagittal.Field.Values[4, 4], 1e-12, "the centre's sagittal contrast loss");
        Assert.True(sagittal.Field.Values.Cast<double>().Where(double.IsFinite).Max() > 1e-3, "a defocused lens loses contrast");
        Assert.True(double.IsNaN(sagittal.Field.Values[4, 8]), "the pair at Px = 1 reaches outside the pupil");
    }

    /// <summary>
    /// The Huygens MTF of a paraboloid on axis is the perfect pupil's MTF: the Huygens PSF on 256 ×
    /// 256 points at the default λF/√64 holds the Airy pattern out to ±16λF, beyond which about 1.3 %
    /// of the energy lies (the Airy tail, 1 − EE ≈ 2/(π²·r/(λF)) for large r, derived from the
    /// asymptotic J₀² + J₁² ≈ 2/(πv)); losing it changes the low-frequency MTF by about that much.
    /// With the 64-node pupil's edge error (3·10⁻³) 0.03 is allowed.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void TheHuygensMtf_OfAPerfectLens_IsTheClosedForm()
    {
        var lens = Paraboloid();
        var system = Sys(0.005, Array.Empty<double>());
        var result = new HuygensMtfAnalysis(_ => lens).Compute(Context(system), new HuygensMtfSettings { PupilSampling = 64, ImageSampling = 256 }, default);
        var t = Plot(result).Curves[0];
        double cutoff = t.X[^1];
        for (int i = 0; i < t.X.Count; i += 5)
            Close(MtfEngine.DiffractionLimitedCircular(t.X[i] / cutoff), t.Y[i], 0.03, $"Huygens MTF at {t.X[i]}");
    }

    /// <summary>The surface MTF's axis cuts are the FFT MTF's (bilinear on the lag plane equals
    /// linear on the axis cut there).</summary>
    [Fact]
    public void TheSurfaceMtf_AlongTheAxes_IsTheMtf()
    {
        var lens = Singlet();
        var system = Sys(0.02, new[] { 0.0, 3.0 });
        var result = new FftSurfaceMtfAnalysis(_ => lens).Compute(Context(system), new FftSurfaceMtfSettings { Sampling = 16, Field = 2, Wavelength = 1 }, default);
        var map = Assert.IsType<AnalysisMapPlot>(Assert.Single(result.Plots)).Field;
        var pupil = MtfEngine.BuildPupil(lens, system, 1, 0, 16, Options());
        int g = MtfRuns.SurfacePoints, centre = g / 2;
        for (int c = 0; c < g; c += 16)
        {
            double fx = map.XAt(c) * 1000;
            Close(pupil.Otf(fx, MtfOrientation.Sagittal).Magnitude, map.Values[centre, c], 1e-12, $"sagittal cut at {fx}");
            double fy = map.YAt(c) * 1000;
            Close(pupil.Otf(fy, MtfOrientation.Tangential).Magnitude, map.Values[c, centre], 1e-12, $"tangential cut at {fy}");
        }
    }
}
