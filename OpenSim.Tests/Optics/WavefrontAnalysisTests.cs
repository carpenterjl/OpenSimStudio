using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-35: the OPD engine's reference choices, tilt and focus removal, the Foucault shadowgram,
/// pupil aberration and the five analyses. Expected values are closed forms derived in the
/// comments or independent traces; OpticStudio's definitions are quoted from its 2025 R2 help
/// pages saved under plans/sources/optics/op-35-help/. The lenses are arbitrary test inputs.
/// The gate itself is <c>Gates/OpdGateTests</c>.
/// </summary>
public sealed class WavefrontAnalysisTests
{
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;

    private static SequentialSurface S(double radius, double thickness, double n = 1, double k = 0, bool stop = false,
        bool mirror = false, SurfaceShape? shape = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = k, Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror, Shape = shape,
    };

    private static SequentialLens Lens(double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance } }.Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double epd, params double[] angleFields) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet
        {
            Type = FieldType.Angle,
            Points = angleFields.Length == 0 ? new[] { new FieldPoint() } : angleFields.Select(a => new FieldPoint { Y = a }).ToArray(),
        },
    };

    private static SequentialLens Singlet() => Lens(double.PositiveInfinity, S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064));   // near its paraxial focus (BFL ≈ 64.2 mm)

    private static SequentialLens DefocusedParaboloid(double defocus) =>
        Lens(double.PositiveInfinity, S(-0.2, -0.1 + defocus, k: -1, stop: true, mirror: true));

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    // ---- Statistics and removal ----

    [Fact]
    public void Statistics_ArePeakToValleyAndTheRmsAboutTheMean()
    {
        // Samples 1, 2, 3, 6 (and a NaN, left out): mean 3, deviations −2, −1, 0, 3, mean square
        // 14/4, P-V 5.
        var s = WavefrontTools.Statistics(new[] { 1.0, 2, double.NaN, 3, 6 });
        Assert.Equal(5, s.PeakToValley);
        Assert.Equal(3, s.Mean);
        Close(Math.Sqrt(14.0 / 4), s.Rms, 4 * Eps, "RMS");
        Assert.Equal(4, s.Points);
        Assert.Throws<ArgumentException>(() => WavefrontTools.Statistics(new[] { double.NaN }));
    }

    [Fact]
    public void Removal_TakesOutExactlyTheTiltAndFocusOfAWavefrontInTheirSpan()
    {
        var nodes = OpdEngine.PupilNodes(17);
        var px = nodes.Select(p => p.Px).ToArray();
        var py = nodes.Select(p => p.Py).ToArray();
        // W = 0.3 + 0.7x − 0.2y + 1.1(2ρ² − 1) + 0.05·x³ (the cubic is outside the span).
        double Cubic(int i) => 0.05 * px[i] * px[i] * px[i];
        var w = nodes.Select((p, i) => 0.3 + 0.7 * p.Px - 0.2 * p.Py + 1.1 * (2 * (p.Px * p.Px + p.Py * p.Py) - 1)).ToArray();
        var both = WavefrontTools.Remove(px, py, w, true, true, out var removed);
        Close(0.7, removed.TiltX, 1e-13, "x tilt");
        Close(-0.2, removed.TiltY, 1e-13, "y tilt");
        Close(1.1, removed.Focus, 1e-13, "focus");
        foreach (double v in both) Close(0.3, v, 1e-13, "piston left alone");

        var tiltOnly = WavefrontTools.Remove(px, py, w, true, false, out _);
        for (int i = 0; i < w.Length; i++) Close(w[i] - 0.7 * px[i] + 0.2 * py[i], tiltOnly[i], 1e-13, "tilt only");

        // With a term outside the span, what remains is still free of tilt: its own fit has none.
        var withCubic = w.Select((v, i) => v + Cubic(i)).ToArray();
        var rest = WavefrontTools.Remove(px, py, withCubic, true, true, out _);
        WavefrontTools.Remove(px, py, rest, true, true, out var again);
        Close(0, again.TiltX, 1e-13, "residual x tilt");
        Close(0, again.Focus, 1e-13, "residual focus");
    }

    // ---- Reference point, wavelength and limitations ----

    [Fact]
    public void TheCentroidReference_IsCentredOnTheMeanImagePoint_AndDiffersFromTheChiefRayByATilt()
    {
        var lens = Singlet();
        var system = Sys(0.02, 0, 10);
        var pupil = OpdEngine.PupilNodes(15);
        var chief = OpdEngine.Compute(lens, system, 1, 0, pupil);
        var centroid = OpdEngine.Compute(lens, system, 1, 0, pupil, new OpdOptions { ReferencePoint = OpdReferencePoint.Centroid, CentroidSampling = 21 });

        // The centre is the mean of an independent batch trace of the same 21 × 21 node grid.
        var grid = BatchTracer.Trace(lens, system, new BatchTraceRequest { Fields = new[] { 1 }, Wavelengths = new[] { 0 }, Pupil = OpdEngine.PupilNodes(21) });
        var ok = grid.Rays.Where(r => r.Ray.IsOk).Select(r => r.Ray.Position).ToArray();
        var mean = ok.Aggregate(Vector3D.Zero, (a, b) => a + b) / ok.Length;
        Assert.Equal(mean, centroid.Geometry.Point);
        Assert.NotEqual(chief.Geometry.Point, centroid.Geometry.Point);

        // Moving the sphere's centre by δ adds n·Δd·δ to the OPD (Δd the ray's direction minus
        // the chief ray's), to first order a tilt across the pupil: the difference is fitted by a
        // plane with a residual far below the tilt itself.
        var px = pupil.Select(p => p.Px).ToArray();
        var py = pupil.Select(p => p.Py).ToArray();
        var difference = pupil.Select((_, i) => centroid.Samples[i].Waves - chief.Samples[i].Waves).ToArray();
        Assert.Equal(0, difference[pupil.ToList().IndexOf((0.0, 0.0))]);   // the chief ray's OPD is zero in both
        var flat = WavefrontTools.Remove(px, py, difference, true, false, out var tilt);
        double residual = WavefrontTools.Statistics(flat).PeakToValley;
        Assert.True(Math.Abs(tilt.TiltY) > 1e-3, $"the centroid should shift the reference (tilt {tilt.TiltY:g4} waves)");
        Assert.True(residual < 0.05 * Math.Abs(tilt.TiltY), $"the difference is not a tilt: residual {residual:g4} against tilt {tilt.TiltY:g4}.");
    }

    [Fact]
    public void ReferenceToPrimary_CentresTheSphereOnThePrimaryChiefRay()
    {
        var lens = Lens(double.PositiveInfinity, S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064));   // near its paraxial focus (BFL ≈ 64.2 mm)
        var system = Sys(0.02, 0, 10) with
        {
            Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 0.55 }, new Wavelength { Micrometers = 0.48 } } },
        };
        var pupil = OpdEngine.PupilNodes(5);
        var own = OpdEngine.Compute(lens, system, 1, 1, pupil);
        var primary = OpdEngine.Compute(lens, system, 1, 1, pupil, new OpdOptions { ReferenceToPrimary = true });
        var primaryChief = OpdEngine.Compute(lens, system, 1, 0, pupil).ChiefRay;
        Assert.Equal(primaryChief.Position, primary.Geometry.Point);
        Assert.Equal(own.ChiefRay.Position, own.Geometry.Point);
        // The chief ray of the traced wavelength still has zero OPD (no piston between wavelengths).
        int centre = pupil.ToList().IndexOf((0.0, 0.0));
        Assert.Equal(0, primary.Samples[centre].Waves);
    }

    [Fact]
    public void ASurfaceThatLimitsTheOpd_IsRefusedUnlessAllowed()
    {
        var lens = Lens(double.PositiveInfinity, S(double.PositiveInfinity, 0.1, stop: true, shape: new ParaxialXYShape(10, 12)));
        var system = Sys(0.01);
        var ex = Assert.Throws<InvalidOperationException>(() => OpdEngine.Compute(lens, system, 0, 0, OpdEngine.PupilNodes(5)));
        Assert.Contains("Surface 1:", ex.Message);
        Assert.Contains("Allow OPD limitations", ex.Message);
        var allowed = OpdEngine.Compute(lens, system, 0, 0, OpdEngine.PupilNodes(5), new OpdOptions { AllowOpdLimitations = true });
        Assert.Single(allowed.Limitations);
    }

    [Fact]
    public void AfocalImageSpace_MeasuresAgainstAPlane_AndACollimatedBeamIsFlat()
    {
        // A point at the focus of a paraboloid (f = 0.1 m) leaves collimated: every ray parallel
        // to the axis, with equal paths to any plane normal to it (Fermat; the reverse of
        // RealRayTraceTests' paraboloid).
        double r = -0.2, f = 0.1;
        var lens = Lens(f, S(r, -0.05, k: -1, stop: true, mirror: true));
        var system = Sys(0.03) with { Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.03, AfocalImageSpace = true } };
        var pupil = OpdEngine.PupilNodes(11);
        foreach (var reference in new[] { OpdReference.ExitPupil, OpdReference.Infinity, OpdReference.Absolute, OpdReference.Absolute2 })
        {
            var result = OpdEngine.Compute(lens, system, 0, 0, pupil, new OpdOptions { Reference = reference });
            Assert.True(result.Afocal);
            Assert.Equal(reference == OpdReference.Absolute2 ? OpdReferenceShape.ImageSurface : OpdReferenceShape.Plane, result.Geometry.Shape);
            double wave = result.AirMicrometers * 1e-6;
            foreach (var s in result.Samples)
            {
                Assert.True(s.IsValid, s.Failure);
                Close(0, s.Waves, 64 * Eps * f / wave, $"{reference} OPD at ({s.Px}, {s.Py})");
            }
        }
        Assert.Throws<ArgumentException>(() => OpdEngine.Compute(lens, system, 0, 0, pupil, new OpdOptions { ReferenceRadius = 1 }));
    }

    [Fact]
    public void AFailedRay_IsKeptWithItsReason()
    {
        var lens = Lens(double.PositiveInfinity, S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.08) with { SemiDiameter = 0.005, Aperture = new CircularAperture(0, 0.005) });
        var result = OpdEngine.Compute(lens, Sys(0.02), 0, 0, OpdEngine.PupilNodes(9));
        Assert.Contains(result.Samples, s => !s.IsValid && s.Failure!.Contains("surface 2"));
        Assert.All(result.Samples.Where(s => !s.IsValid), s => Assert.True(double.IsNaN(s.Waves)));
        Assert.Contains(result.Samples, s => s.IsValid);
    }

    // ---- Interferogram ----

    [Fact]
    public void TheInterferogram_IsDarkWhereThePhaseIsAWholeNumberOfFringes()
    {
        Close(0, InterferogramAnalysis.Intensity(0, 1, 0, 0, 0.3, 0.4), 1e-15, "zero OPD, no tilt");
        Close(1, InterferogramAnalysis.Intensity(0.5, 1, 0, 0, 0, 0), 1e-15, "half a wave");
        Close(0, InterferogramAnalysis.Intensity(0.5, 2, 0, 0, 0, 0), 1e-15, "half a wave, double pass");
        // Two fringes of x tilt across the diameter: from Px = −1 to 1 the phase runs through 2.
        Close(1, InterferogramAnalysis.Intensity(0, 1, 2, 0, 0.5, 0), 1e-15, "a quarter of the way across");
        Close(0, InterferogramAnalysis.Intensity(0, 1, 2, 0, 1, 0), 1e-15, "at the edge");
    }

    // ---- Foucault ----

    [Fact]
    public void TheShadowgramOfAStronglyDefocusedBeam_IsTheGeometricShadowOfTheKnife()
    {
        // In the geometric limit a pupil point is dark when its ray lands on the blocked side of
        // the knife and bright otherwise. The landing points come from the trace itself (the OPD
        // samples' image intercepts), independently of the diffraction calculation and its signs.
        var lens = DefocusedParaboloid(-2.2e-3);
        var system = Sys(0.02);
        var result = FoucaultShadowgram.Compute(lens, system, 0, 0, 48, KnifeEdge.VerticalLeft, 0);
        double reach = result.Opd.Samples.Where(s => s.IsValid).Max(s => Math.Abs(s.Ray.Position.X - result.Opd.ChiefRay.Position.X));
        Assert.True(reach > 1e-4, $"the defocus should spread the rays (reach {reach:g3} m).");
        int dark = 0, bright = 0;
        var nodes = Enumerable.Range(0, 48).Select(k => OpdEngine.Node(k, 48)).ToArray();
        foreach (var s in result.Opd.Samples)
        {
            if (!s.IsValid || s.Px * s.Px + s.Py * s.Py > 0.85 * 0.85) continue;
            double x = s.Ray.Position.X - result.Opd.ChiefRay.Position.X;
            if (Math.Abs(x) < 0.25 * reach) continue;
            int column = Array.IndexOf(nodes, s.Px), row = Array.IndexOf(nodes, s.Py);
            double intensity = result.Intensity.Values[row, column];
            if (x <= 0) { Assert.True(intensity < 0.1, $"({s.Px:g3}, {s.Py:g3}) lands at x = {x:g3} m behind the knife, intensity {intensity:g3}."); dark++; }
            else { Assert.True(intensity > 0.6, $"({s.Px:g3}, {s.Py:g3}) lands at x = {x:g3} m past the knife, intensity {intensity:g3}."); bright++; }
        }
        Assert.True(dark > 200 && bright > 200, $"{dark} dark and {bright} bright points checked.");
        Close(0.5, result.PassedFraction, 0.05, "half the light passes a knife through the centre of a symmetric spot");

        // A knife outside the window blocks nothing: the intensity's mean is 1 by its normalisation.
        var clear = FoucaultShadowgram.Compute(lens, system, 0, 0, 48, KnifeEdge.VerticalLeft, -1);
        Assert.Equal(1, clear.PassedFraction);
        var values = clear.Intensity.Values.Cast<double>().Where(double.IsFinite).ToArray();
        Close(1, values.Average(), 1e-12, "mean unblocked intensity");
    }

    [Fact]
    public void Foucault_RefusesAfocalImageSpace()
    {
        var system = Sys(0.02) with { Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.02, AfocalImageSpace = true } };
        Assert.Throws<InvalidOperationException>(() => FoucaultShadowgram.Compute(DefocusedParaboloid(0), system, 0, 0, 16, KnifeEdge.VerticalLeft, 0));
    }

    // ---- Pupil aberration ----

    [Fact]
    public void PupilAberration_IsZeroWithTheStopFirst_AndVanishesWithRayAiming()
    {
        var line = Enumerable.Range(-5, 11).Select(i => i / 5.0).ToArray();
        // Stop on surface 1 and the object on axis at infinity: the rays launched on the
        // paraxial entrance pupil (the stop's vertex plane) are parallel to the axis, so they
        // meet the stop at exactly P × the paraxial stop radius.
        var first = PupilAberration.Fan(Singlet(), Sys(0.02), 0, 0, line, true);
        Assert.All(first, p => Close(0, p.Percent, 1e-10, $"stop-first pupil aberration at {p.Pupil}"));

        // A stop 20 mm behind a singlet (RayAimingTests' pupil-aberrated singlet): without
        // aiming the 20° rays miss the scaled stop points; aimed (paraxial stop radius) they
        // land on them to the aiming tolerance.
        var lens = Lens(double.PositiveInfinity, S(0.06, 0.006, 1.6), S(-0.2, 0.02), S(double.PositiveInfinity, 0.06, stop: true));
        var off = Sys(0.01, 0, 20);
        var on = off with { RayAiming = new RayAiming { Mode = RayAimingMode.Paraxial } };
        var unaimed = PupilAberration.Fan(lens, off, 1, 0, line, true);
        var aimed = PupilAberration.Fan(lens, on, 1, 0, line, true);
        Assert.True(unaimed.Max(p => Math.Abs(p.Percent)) > 1, "the unaimed fan should show pupil aberration");
        Assert.All(aimed, p => Assert.True(Math.Abs(p.Percent) < 1e-6, $"aimed ray at {p.Pupil}: {p.Percent:g3} %"));

        // The value is the definition, checked against an independent trace of one ray.
        var trace = SequentialTrace.Create(lens, off);
        var launcher = new RayLauncher(lens, off);
        var (hx, hy) = off.Fields.Normalized(1);
        var hit = trace.TracePath(launcher.Launch(trace, hx, hy, 0, 0.6), new TraceOptions { IgnoreApertures = true }).Hits.First(h => h.Surface == 3);
        double radius = Math.Abs(launcher.Paraxial.MarginalRay.Heights[3]);
        Close(100 * (hit.Position.Y - 0.6 * radius) / radius, unaimed[8].Percent, 1e-12, "pupil aberration at Py = 0.6");
    }

    // ---- The analyses ----

    [Fact]
    public void TheAnalyses_AreRegistered_AndReadTheProjectsSurfaces()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        var ids = new[] { OpdFanAnalysis.SavedId, WavefrontMapAnalysis.SavedId, InterferogramAnalysis.SavedId, FoucaultAnalysis.SavedId, PupilAberrationAnalysis.SavedId };
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
            Assert.Equal(new[] { "No surfaces" }, analysis.Compute(empty, analysis.CreateDefaultSettings(), default).Sections.Select(s => s.Heading));
            var settings = analysis.CreateDefaultSettings() switch
            {
                WavefrontMapSettings m => m with { Sampling = 17 },
                InterferogramSettings i => i with { Sampling = 17 },
                FoucaultSettings f => f with { Sampling = 16 },
                var other => other,
            };
            var result = analysis.Compute(project, settings, default);
            Assert.DoesNotContain("No surfaces", result.Sections.Select(s => s.Heading));
            Assert.NotEmpty(result.Plots);
            Assert.NotEmpty(result.Assumptions);
        }
    }

    [Fact]
    public void TheWavefrontMap_GivesPeakToValleyAndRms_AndFocusRemovalFlattensADefocusedPerfectLens()
    {
        var lens = DefocusedParaboloid(-0.5e-3);
        var system = Sys(0.04);
        var context = new OpticsAnalysisContext("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });
        var analysis = new WavefrontMapAnalysis(_ => lens);
        var raw = analysis.Compute(context, new WavefrontMapSettings { Sampling = 33 }, default);
        var map = Assert.IsType<AnalysisMapPlot>(Assert.Single(raw.Plots));
        Assert.Equal(33, map.Field.Rows);
        Assert.True(double.IsNaN(map.Field.Values[0, 0]), "the corner lies outside the pupil");
        // The map's values and the engine's agree node for node, and the listed statistics are theirs.
        var engine = OpdEngine.Compute(lens, system, 0, 0, OpdEngine.PupilNodes(33));
        var stats = WavefrontTools.Statistics(engine.Samples.Select(s => s.Waves).ToArray());
        Assert.Contains(("Peak to valley (waves)", StudyReport.Number(stats.PeakToValley, "g6")), raw.Sections[0].Pairs);
        Assert.Contains(("RMS, mean removed (waves)", StudyReport.Number(stats.Rms, "g6")), raw.Sections[0].Pairs);

        // The two-sphere OPD is nearly pure focus (2ρ² − 1) with a little higher-order spherical
        // term; removing focus leaves a small fraction of the RMS.
        var flat = analysis.Compute(context, new WavefrontMapSettings { Sampling = 33, RemoveFocus = true, RemoveTilt = true }, default);
        double flatRms = double.Parse(flat.Sections[0].Pairs.First(p => p.Key.StartsWith("RMS")).Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(stats.Rms > 1, $"the defocus should give more than a wave RMS ({stats.Rms:g4}).");
        Assert.True(flatRms < 0.02 * stats.Rms, $"focus removal left {flatRms:g4} of {stats.Rms:g4} waves RMS.");
    }

    [Fact]
    public void TheOpdFan_PlotsTangentialAndSagittalFansPerField()
    {
        var lens = Singlet();
        var system = Sys(0.02, 0, 10);
        var context = new OpticsAnalysisContext("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });
        var result = new OpdFanAnalysis(_ => lens).Compute(context, new OpdFanSettings { NumberOfRays = 4 }, default);
        Assert.Equal(4, result.Plots.Count);
        var tangential = Assert.IsType<LinePlot>(result.Plots[2]);
        Assert.Equal(new[] { -1, -0.75, -0.5, -0.25, 0, 0.25, 0.5, 0.75, 1 }, tangential.Curves[0].X);
        var engine = OpdEngine.Compute(lens, system, 1, 0, tangential.Curves[0].X.Select(p => (0.0, p)).ToArray(),
            new OpdOptions { ReferenceToPrimary = false });
        for (int i = 0; i < 9; i++) Close(engine.Samples[i].Waves, tangential.Curves[0].Y[i], 0, $"tangential OPD at {tangential.Curves[0].X[i]}");
        Assert.Equal(0, tangential.Curves[0].Y[4]);
    }
}
