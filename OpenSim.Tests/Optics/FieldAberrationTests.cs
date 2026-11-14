using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Tests.Optics;

/// <summary>Lenses and helpers shared by the OP-37 tests. The lenses are arbitrary test inputs,
/// not reference designs.</summary>
internal static class FieldAberrationFixtures
{
    public const double Mm = 1e-3;
    public static readonly double Inf = double.PositiveInfinity;

    public static SequentialSurface S(double radiusMm, double thicknessMm, double n = 1, bool stop = false, bool mirror = false,
        SurfaceShape? shape = null, ParaxialMedium? medium = null, TiltDecenter? before = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radiusMm * Mm), Thickness = thicknessMm * Mm,
        Medium = medium ?? ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror, Shape = shape, TiltBefore = before,
    };

    public static SequentialLens Lens(double objectDistanceMm, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistanceMm * Mm } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    /// <summary>The lens with the last surface's thickness replaced (meters).</summary>
    public static SequentialLens WithImageDistance(SequentialLens lens, double meters)
    {
        var s = lens.Surfaces.ToArray();
        s[^2] = s[^2] with { Thickness = meters };
        return lens with { Surfaces = s };
    }

    /// <summary>Fields in degrees (angle) or millimeters (heights).</summary>
    public static OpticalSystemData Sys(double epdMm, FieldType type, double[] fields, RayAimingMode aiming = RayAimingMode.Off,
        double[]? micrometers = null, int primary = 0) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epdMm * Mm },
        Fields = new FieldSet
        {
            Type = type,
            Points = fields.Select(f => new FieldPoint { Y = FieldTypes.IsAngle(type) ? f : f * Mm }).ToArray(),
        },
        Wavelengths = micrometers is null ? new WavelengthSet()
            : new WavelengthSet { Wavelengths = micrometers.Select(m => new Wavelength { Micrometers = m }).ToArray(), PrimaryIndex = primary },
        RayAiming = new RayAiming { Mode = aiming },
    };

    internal static FieldScanSetup Setup(SequentialLens lens, OpticalSystemData system, bool realImageHeightAsParaxial = true)
    {
        var notes = new List<string>();
        return FieldScanCommon.Prepare(lens, FieldScanCommon.ScanSystem(system, realImageHeightAsParaxial, notes), notes, default);
    }

    /// <summary>A synthetic dispersive glass (test data, not a real glass): n = 1.53 at 0.4 µm
    /// and 1.51 at 0.7 µm, tabulated.</summary>
    public static ParaxialMedium TestGlass() => ParaxialMedium.Of(new OpticalMaterial
    {
        Name = "TEST",
        Tabulated = new TabulatedIndex { Points = new[] { new TabulatedIndexPoint(0.4, 1.53, 0), new TabulatedIndexPoint(0.7, 1.51, 0) } },
    });

    public static void Close(double expected, double actual, double tolerance, string what)
    {
        Assert.True(Math.Abs(expected - actual) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:g3}, tolerance {tolerance:g3}).");
    }
}

/// <summary>
/// OP-37: Coddington foci against finite-difference real-ray foci, the distortion definitions,
/// longitudinal aberration and chromatic focal shift of a refracting sphere, the relative
/// illumination's effective F/#, refusals, and the six analyses through the registry. The gate
/// clauses are in <c>Gates/FieldAberrationGateTests</c>. Expected values are derived in the
/// comments from the test's own inputs; definitions quoted from OpticStudio's help are cited at
/// the code they test (OpenSim.Optics/Analysis/FieldAberrations).
/// </summary>
public sealed class FieldAberrationTests
{
    private const double Mm = FieldAberrationFixtures.Mm;
    private static readonly double Inf = FieldAberrationFixtures.Inf;

    // ---- Coddington against finite differences of real rays ----

    /// <summary>
    /// The finite-difference foci of the pencil about the chief ray of field (hx, hy): rays at
    /// pupil offsets ±δ in the tangential direction cross the chief ray (in the meridional
    /// plane) at a distance u along it from its image-surface point, p₀ + u·d₀ = p + v·d, so
    /// u = ((p − p₀) × d)/(d₀ × d) with a × b the 2-D cross product in that plane; rays at ±δ in
    /// the sagittal direction cross the meridional plane at q = p + v·d, and u = (q − p₀)·d₀. The
    /// mean of ±δ removes the odd terms of u(δ) = u₀ + a₁δ + a₂δ² + …, and Richardson's
    /// (4u(δ/2) − u(δ))/3 the δ² term, so the error is O(δ⁴) plus rounding of about
    /// 10⁻¹⁶·L/(δ·NA).
    /// </summary>
    private static (double T, double S) FiniteDifferenceFoci(SequentialTrace trace, RayLauncher launcher, double hx, double hy, bool alongX)
    {
        var chief = trace.Trace(launcher.Launch(trace, hx, hy, 0, 0));
        Assert.True(chief.IsOk);
        var p0 = chief.Position;
        var d0 = chief.Direction;
        (double T, double S) At(double delta)
        {
            double t = 0, s = 0;
            foreach (double sign in new[] { 1.0, -1.0 })
            {
                double e = sign * delta;
                var tr = trace.Trace(alongX ? launcher.Launch(trace, hx, hy, e, 0) : launcher.Launch(trace, hx, hy, 0, e));
                var sr = trace.Trace(alongX ? launcher.Launch(trace, hx, hy, 0, e) : launcher.Launch(trace, hx, hy, e, 0));
                Assert.True(tr.IsOk && sr.IsOk);
                double Cross(Vector3D a, Vector3D b) => alongX ? a.X * b.Z - a.Z * b.X : a.Y * b.Z - a.Z * b.Y;
                t += Cross(tr.Position - p0, tr.Direction) / Cross(d0, tr.Direction) / 2;
                double across = alongX ? sr.Position.Y : sr.Position.X, acrossD = alongX ? sr.Direction.Y : sr.Direction.X;
                var q = sr.Position + sr.Direction * (-across / acrossD);
                s += Vector3D.Dot(q - p0, d0) / 2;
            }
            return (t, s);
        }
        const double delta = 1e-3;
        var full = At(delta);
        var half = At(delta / 2);
        return ((4 * half.T - full.T) / 3, (4 * half.S - full.S) / 3);
    }

    private static void CheckCoddington(SequentialLens lens, OpticalSystemData system, double[] h, FieldScan scan)
    {
        var setup = FieldAberrationFixtures.Setup(lens, system);
        var trace = setup.Trace(system.Wavelengths.PrimaryIndex);
        var (dx, dy) = FieldScans.Direction(scan);
        foreach (double hh in h)
        {
            var path = trace.TracePath(setup.Launcher.Launch(trace, hh * dx, hh * dy, 0, 0));
            var foci = CoddingtonTrace.Trace(trace, path, FieldScans.Sagittal(scan), $"H = {hh}");
            var (t, s) = FiniteDifferenceFoci(trace, setup.Launcher, hh * dx, hh * dy, FieldScans.AlongX(scan));
            // Tolerance: 10⁻⁷ of the focal distance covers the O(δ⁴) remainder with room; the
            // rounding term is below 10⁻¹² m for these lenses.
            FieldAberrationFixtures.Close(t, foci.Tangential, 1e-7 * Math.Abs(t) + 1e-10, $"tangential focus at H = {hh}");
            FieldAberrationFixtures.Close(s, foci.Sagittal, 1e-7 * Math.Abs(s) + 1e-10, $"sagittal focus at H = {hh}");
        }
    }

    /// <summary>A singlet with a separate stop in front, an even asphere on its rear (so the
    /// conic and sag-shape curvature paths are both used), a finite object with height fields.</summary>
    private static SequentialLens AsphericSinglet() => FieldAberrationFixtures.Lens(500,
        FieldAberrationFixtures.S(Inf, 10, stop: true),
        FieldAberrationFixtures.S(40, 6, 1.6),
        FieldAberrationFixtures.S(-120, 50, shape: new EvenAsphere(new[] { 0.0, 2e3 })));

    [Fact]
    public void Coddington_MatchesFiniteDifferenceRealRayFoci_InAnAsphericSinglet_AlongY()
    {
        var system = FieldAberrationFixtures.Sys(10, FieldType.ObjectHeight, new[] { 0.0, 60.0 });
        CheckCoddington(AsphericSinglet(), system, new[] { 0.0, 0.5, 1.0 }, FieldScan.PlusY);
    }

    [Fact]
    public void Coddington_MatchesFiniteDifferenceRealRayFoci_InAnAsphericSinglet_AlongX()
    {
        var system = FieldAberrationFixtures.Sys(10, FieldType.ObjectHeight, new[] { 0.0, 60.0 });
        CheckCoddington(AsphericSinglet(), system, new[] { 0.7 }, FieldScan.PlusX);
    }

    [Fact]
    public void Coddington_MatchesFiniteDifferenceRealRayFoci_ThroughTwoMirrors()
    {
        // A coaxial Cassegrain (paraboloid primary, spherical secondary as the stop), off axis.
        var lens = FieldAberrationFixtures.Lens(Inf,
            FieldAberrationFixtures.S(-400, -150, mirror: true) with { Conic = -1 },
            FieldAberrationFixtures.S(-150, 200, stop: true, mirror: true));
        var system = FieldAberrationFixtures.Sys(80, FieldType.Angle, new[] { 0.0, 0.5 });
        CheckCoddington(lens, system, new[] { 0.5, 1.0 }, FieldScan.PlusY);
    }

    [Fact]
    public void Coddington_RefusesAPencilThatLeavesTheMeridionalPlane()
    {
        // The rear surface decentred in x: the y scan's chief ray is no longer in a plane of symmetry.
        var lens = FieldAberrationFixtures.Lens(500,
            FieldAberrationFixtures.S(Inf, 10, stop: true),
            FieldAberrationFixtures.S(40, 6, 1.6),
            FieldAberrationFixtures.S(-120, 50, before: new TiltDecenter(0.5 * Mm, 0, 0, 0, 0)));
        var system = FieldAberrationFixtures.Sys(10, FieldType.ObjectHeight, new[] { 0.0, 60.0 });
        var setup = FieldAberrationFixtures.Setup(lens, system);
        var trace = setup.Trace(0);
        var path = trace.TracePath(setup.Launcher.Launch(trace, 0, 1, 0, 0));
        var e = Assert.Throws<InvalidOperationException>(() => CoddingtonTrace.Trace(trace, path, Vector3D.UnitX, "test"));
        Assert.Contains("surface 3", e.Message);
    }

    // ---- Distortion definitions on an ideal lens ----

    /// <summary>
    /// An ideal (paraxial) lens of focal length f at the stop, object at infinity, image at f:
    /// the paraxial surface turns slopes by u′ = u − x·φ (OpticStudio's equations, quoted in
    /// <see cref="ParaxialShape"/>), so every ray of field angle θ meets the plane z = f at
    /// y = f·tan θ. Hence (derived) F-tan(θ) distortion is zero, F-θ distortion is
    /// 100·(f tan θ − f θ)/(f θ) = 100·(tan θ/θ − 1), calibrated F-tan(θ) fits f′ = f. The
    /// Coddington trace refuses the ideal lens (it turns rays by its own rule), and the analysis
    /// still gives the distortion, saying the field curvature was not computed.
    /// </summary>
    [Fact]
    public void AnIdealLens_HasNoFTanDistortion_AndItsFThetaDistortionIsTanThetaOverTheta()
    {
        double f = 0.1;
        var lens = FieldAberrationFixtures.Lens(Inf, FieldAberrationFixtures.S(Inf, f / Mm, stop: true, shape: new ParaxialShape(f)));
        var system = FieldAberrationFixtures.Sys(10, FieldType.Angle, new[] { 0.0, 30.0 });
        var setup = FieldAberrationFixtures.Setup(lens, system);
        var h = FieldScanCommon.Even(4);
        foreach (var reference in new[] { DistortionReference.FTanTheta, DistortionReference.FTheta, DistortionReference.CalibratedFTanTheta })
        {
            var c = FieldCurvatureDistortion.Compute(setup, new[] { 0 }, FieldScan.PlusY, h, reference, default).Single();
            Assert.NotNull(c.FieldCurvatureFailure);
            Assert.Contains("Paraxial", c.FieldCurvatureFailure);
            for (int i = 1; i < h.Length; i++)
            {
                double theta = h[i] * 30 * Math.PI / 180;
                double expected = reference == DistortionReference.FTheta ? 100 * (Math.Tan(theta) / theta - 1) : 0;
                // F-θ's small-field slope is a central difference in θ at 3·10⁻³ degrees: relative
                // error of order θ² ≈ 3·10⁻⁹, i.e. 3·10⁻⁷ % here.
                FieldAberrationFixtures.Close(expected, c.Points[i].DistortionPercent, 1e-6, $"{reference} at {h[i] * 30}°");
            }
            // F-θ's small-field slope is f·tan(θs)/θs, θs = 5.2·10⁻⁵ rad: relative excess θs²/3 ≈ 10⁻⁹.
            FieldAberrationFixtures.Close(f, c.ReferenceFocalLength, 1e-8 * f, $"{reference} reference focal length");
        }
    }

    [Fact]
    public void FThetaDistortion_IsRefusedForHeightFields()
    {
        var system = FieldAberrationFixtures.Sys(10, FieldType.ObjectHeight, new[] { 0.0, 60.0 });
        var setup = FieldAberrationFixtures.Setup(AsphericSinglet(), system);
        Assert.Throws<ArgumentException>(() => FieldCurvatureDistortion.Compute(setup, new[] { 0 }, FieldScan.PlusY,
            FieldScanCommon.Even(3), DistortionReference.FTheta, default));
    }

    // ---- Longitudinal aberration and chromatic focal shift of one refracting sphere ----

    /// <summary>
    /// A collimated ray at height h meets a refracting sphere (radius R, vertex at z = 0, centre
    /// at z = R, from air into index n) at incidence sin I = h/R, refracts to n sin I′ = sin I,
    /// and crosses the axis at F. In the triangle centre C, hit point P, F the angle at P is I′
    /// (between the normal line PC and the ray), the angle at F is I − I′ (the ray's angle to
    /// the axis), so by the sine rule CF = R·sin I′/sin(I − I′) and z_F = R + R·sin I′/sin(I − I′)
    /// (derived). As h → 0 this tends to R + R/(n − 1) = nR/(n − 1), the paraxial focus.
    /// </summary>
    private static double SphereCrossing(double r, double n, double h)
    {
        double sinI = h / r, sinIp = sinI / n;
        double i = Math.Asin(sinI), ip = Math.Asin(sinIp);
        return r + r * sinIp / Math.Sin(i - ip);
    }

    [Fact]
    public void LongitudinalAberration_OfARefractingSphere_IsItsExactAxisCrossing()
    {
        double r = 50 * Mm, n = 1.5, a = 10 * Mm, image = 150 * Mm;
        var lens = FieldAberrationFixtures.Lens(Inf, FieldAberrationFixtures.S(r / Mm, image / Mm, n, stop: true));
        var system = FieldAberrationFixtures.Sys(2 * a / Mm, FieldType.Angle, new[] { 0.0 });
        var setup = FieldAberrationFixtures.Setup(lens, system);
        var zones = FieldScanCommon.Even(5);
        var failures = new List<string>();
        var x = LongitudinalAberrationAnalysis.Crossings(setup, new[] { 0 }, zones, failures, default)[0];
        Assert.Empty(failures);
        FieldAberrationFixtures.Close(n * r / (n - 1) - image, x[0], 1e-14, "paraxial zone");
        for (int i = 1; i < zones.Length; i++)
            FieldAberrationFixtures.Close(SphereCrossing(r, n, zones[i] * a) - image, x[i], 1e-13, $"zone {zones[i]}");
    }

    [Fact]
    public void ChromaticFocalShift_OfARefractingSphere_FollowsItsIndex()
    {
        double r = 50 * Mm, a = 5 * Mm;
        var glass = FieldAberrationFixtures.TestGlass();
        var lens = FieldAberrationFixtures.Lens(Inf, FieldAberrationFixtures.S(r / Mm, 150, stop: true, medium: glass));
        var system = FieldAberrationFixtures.Sys(2 * a / Mm, FieldType.Angle, new[] { 0.0 }, micrometers: new[] { 0.45, 0.55, 0.65 }, primary: 1);
        var env = system.Environment;
        double nP = glass.IndexAt(0.55, env);
        double paraxialPrimary = nP * r / (nP - 1);
        var wavelengths = new[] { 0.45, 0.5, 0.6, 0.65 };
        var notes = new List<string>();
        var (paraxial, _, _) = ChromaticFocalShiftAnalysis.Shifts(lens, system, wavelengths, 0, notes, default);
        var (zonal, _, _) = ChromaticFocalShiftAnalysis.Shifts(lens, system, wavelengths, 0.5, notes, default);
        for (int i = 0; i < wavelengths.Length; i++)
        {
            double n = glass.IndexAt(wavelengths[i], env);
            FieldAberrationFixtures.Close(n * r / (n - 1) - paraxialPrimary, paraxial[i], 1e-14, $"paraxial shift at {wavelengths[i]} µm");
            FieldAberrationFixtures.Close(SphereCrossing(r, n, 0.5 * a) - paraxialPrimary, zonal[i], 1e-13, $"zone 0.5 shift at {wavelengths[i]} µm");
        }
    }

    // ---- Relative illumination: effective F/# on the axis ----

    /// <summary>
    /// On the axis of an ideal lens (focal length f, stop radius a at the lens, image at f) the
    /// arriving rays fill the cone from the image point to the stop disk: A = ∫ f²/(f² + r²)²·2πr dr
    /// over 0..a (the projected solid angle of a disk seen from a point on its axis, the integrand
    /// derived in <c>Gates/FieldAberrationGateTests</c>) = π·a²/(f² + a²), so the effective F/#
    /// √(π/(4A)) = √(f² + a²)/(2a) (derived).
    /// </summary>
    [Fact]
    public void TheEffectiveFNumber_OfAnIdealLens_IsTheClosedForm()
    {
        double f = 0.1, a = 5 * Mm;
        var lens = FieldAberrationFixtures.Lens(Inf, FieldAberrationFixtures.S(Inf, f / Mm, stop: true, shape: new ParaxialShape(f)));
        var system = FieldAberrationFixtures.Sys(2 * a / Mm, FieldType.Angle, new[] { 0.0, 10.0 });
        var setup = FieldAberrationFixtures.Setup(lens, system, false);
        var r = RelativeIllumination.Compute(setup, 0, FieldScan.PlusY, 20, new[] { 0.0, 1.0 }, null, default);
        FieldAberrationFixtures.Close(Math.PI * a * a / (f * f + a * a), r.ProjectedSolidAngle[0], 1e-8 * r.ProjectedSolidAngle[0], "A on the axis");
        FieldAberrationFixtures.Close(Math.Sqrt(f * f + a * a) / (2 * a), r.EffectiveFNumber[0], 1e-8 * r.EffectiveFNumber[0], "effective F/#");
    }

    // ---- The analyses ----

    [Fact]
    public void TheSixAnalyses_AreRegistered_AndSayWhenThereAreNoSurfaces()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        foreach (var id in new[]
        {
            FieldCurvatureDistortionAnalysis.SavedId, GridDistortionAnalysis.SavedId, LongitudinalAberrationAnalysis.SavedId,
            LateralColorAnalysis.SavedId, ChromaticFocalShiftAnalysis.SavedId, RelativeIlluminationAnalysis.SavedId,
        })
        {
            Assert.True(registry.TryGet(id, out var analysis), id);
            var result = analysis.Compute(new OpticsAnalysisContext("P", new OpticsSettings()), analysis.CreateDefaultSettings(), default);
            Assert.Contains("No surfaces", result.Text);
        }
    }

    [Fact]
    public void TheAnalyses_RunOnALens_AndStateTheirAssumptions()
    {
        var lens = AsphericSinglet();
        var system = FieldAberrationFixtures.Sys(10, FieldType.ObjectHeight, new[] { 0.0, 60.0 }, micrometers: new[] { 0.45, 0.55, 0.65 }, primary: 1);
        lens = lens with { Surfaces = lens.Surfaces.Select((s, j) => j == 2 ? s with { Medium = FieldAberrationFixtures.TestGlass() } : s).ToArray() };
        var context = new OpticsAnalysisContext("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });
        IOpticsAnalysis[] analyses =
        {
            new FieldCurvatureDistortionAnalysis(_ => lens), new GridDistortionAnalysis(_ => lens), new LongitudinalAberrationAnalysis(_ => lens),
            new LateralColorAnalysis(_ => lens), new ChromaticFocalShiftAnalysis(_ => lens), new RelativeIlluminationAnalysis(_ => lens),
        };
        foreach (var analysis in analyses)
        {
            var result = analysis.Compute(context, analysis.CreateDefaultSettings(), default);
            Assert.NotEmpty(result.Assumptions);
            Assert.NotEmpty(result.Plots);
            Assert.DoesNotContain("not computed", result.Text);
        }
    }

    [Fact]
    public void TheRayDensity_BelowTheHelpsMinimumOfFive_IsRefused()
    {
        var lens = AsphericSinglet();
        var system = FieldAberrationFixtures.Sys(10, FieldType.ObjectHeight, new[] { 0.0, 60.0 });
        var context = new OpticsAnalysisContext("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });
        Assert.Throws<ArgumentException>(() => new RelativeIlluminationAnalysis(_ => lens)
            .Compute(context, new RelativeIlluminationSettings { RayDensity = 4 }, default));
    }
}
