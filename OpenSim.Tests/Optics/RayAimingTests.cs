using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-22: ray aiming, vignetting factors, Set Vignetting, Replace Vignetting With Apertures, the
/// Vignetting Plot and real-image-height fields. Every expected value is either the defining
/// condition itself (the aimed ray lands where it was aimed, checked by an independent trace) or
/// a closed form derived in the test's comments. The vignetting-factor equations are copied from
/// the Ansys OpticStudio 2025 R2 help, Conventions and Definitions, "Vignetting Factors"
/// (ansyshelp.ansys.com, OpticStudio_Help/topics/Vignetting_Factors.html), its four equations.
/// The lenses are arbitrary test inputs, not reference designs.
/// </summary>
public sealed class RayAimingTests
{
    private const double Mm = 1e-3;
    private static readonly double Inf = double.PositiveInfinity;

    private static SequentialSurface S(double radiusMm, double thicknessMm, double n = 1, double k = 0, bool stop = false,
        bool mirror = false, SurfaceAperture? aperture = null, double? semiDiameterMm = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radiusMm * Mm), Conic = k, Thickness = thicknessMm * Mm,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror, Aperture = aperture,
        SemiDiameter = semiDiameterMm * Mm,
    };

    private static SequentialLens Lens(double objectDistanceMm, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistanceMm * Mm } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double epdMm, RayAimingMode mode, bool robust, params double[] angleFields) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epdMm * Mm },
        Fields = new FieldSet { Type = FieldType.Angle, Points = angleFields.Select(a => new FieldPoint { Y = a }).ToArray() },
        RayAiming = new RayAiming { Mode = mode, Robust = robust },
    };

    private static readonly TraceOptions NoApertures = new() { IgnoreApertures = true };

    // ---- The test systems ----

    /// <summary>A singlet with the stop 20 mm behind it: the entrance pupil is the stop seen
    /// through the lens, so its image is aberrated; at 20° the paraxial chief ray misses the
    /// stop centre by a third of the stop radius.</summary>
    private static SequentialLens PupilAberratedSinglet() =>
        Lens(Inf, S(60, 6, 1.6), S(-200, 20), S(Inf, 60, stop: true));

    /// <summary>A wide-angle pair: a strong negative meniscus in front of the stop, a positive
    /// lens behind. At 75° the ray launched at the paraxial pupil does not even reach the stop.</summary>
    private static SequentialLens WideAngle() =>
        Lens(Inf, S(40, 3, 1.6), S(12, 20), S(Inf, 2, stop: true), S(30, 5, 1.7), S(-30, 30));

    /// <summary>A coaxial Cassegrain (paraboloid primary, spherical secondary as the stop) used
    /// off axis through field-biased fields; tilted mirrors are OP-21's.</summary>
    private static SequentialLens Cassegrain() =>
        Lens(Inf, S(-400, -150, k: -1, mirror: true), S(-150, 200, stop: true, mirror: true));

    /// <summary>A coaxial three-mirror system (stop on the secondary), field biased.</summary>
    private static SequentialLens ThreeMirror() =>
        Lens(Inf, S(-400, -150, k: -1, mirror: true), S(-150, 150, stop: true, mirror: true), S(-200, -100, k: -0.5, mirror: true));

    /// <summary>The gate systems by name: the lens, the entrance-pupil diameter (mm) and the
    /// angle fields (degrees).</summary>
    private static (SequentialLens Lens, double Epd, double[] Fields) GateSystem(string name) => name switch
    {
        "singlet" => (PupilAberratedSinglet(), 10, new double[] { 0, 10, 20 }),
        "wide" => (WideAngle(), 2, new double[] { 0, 40, 60, 75 }),
        "cassegrain" => (Cassegrain(), 80, new double[] { 1, 2, 3 }),
        "three-mirror" => (ThreeMirror(), 60, new double[] { 1, 2, 3 }),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static IEnumerable<object[]> GateSystems()
    {
        foreach (var name in new[] { "singlet", "wide", "cassegrain", "three-mirror" })
            foreach (var mode in new[] { RayAimingMode.Paraxial, RayAimingMode.Real })
                foreach (bool robust in new[] { false, true })
                    yield return new object[] { name, mode, robust };
    }

    // ---- The gate ----

    /// <summary>
    /// The gate: with aiming on, the chief ray of every field crosses the stop centre, and every
    /// pupil point lands at (Px·R, Py·R), within 1e-10 of the stop radius R. The landing point is
    /// read from an independent trace of the launched ray (apertures ignored), not from the
    /// solver's own residual.
    /// </summary>
    [Theory]
    [MemberData(nameof(GateSystems))]
    public void WithAimingOn_EveryRayLandsWhereItWasAimed_OnTheStop(string name, RayAimingMode mode, bool robust)
    {
        var (lens, epd, fields) = GateSystem(name);
        var system = Sys(epd, mode, robust, fields);
        var launcher = new RayLauncher(lens, system);
        double radius = launcher.StopRadius;
        Assert.True(radius > 0, name);
        Assert.Empty(launcher.Notes);
        {
            var trace = SequentialTrace.Create(lens, system);
            for (int f = 0; f < fields.Length; f++)
            {
                var (hx, hy) = system.Fields.Normalized(f);
                foreach (var (px, py) in new[] { (0.0, 0.0), (0.0, 1.0), (0.0, -1.0), (1.0, 0.0), (-1.0, 0.0), (0.7, 0.7), (-0.3, 0.5) })
                {
                    var aimed = launcher.LaunchAimed(trace, hx, hy, px, py);
                    Assert.True(aimed.Converged, $"{name}, {mode}, field {f + 1}, pupil ({px}, {py}): {aimed.Failure}");
                    var path = trace.TracePath(aimed.Ray, NoApertures);
                    var p = path.Hits[lens.StopIndex].Position;
                    Assert.Equal(lens.StopIndex, path.Hits[lens.StopIndex].Surface);
                    double miss = Math.Sqrt((p.X - px * radius) * (p.X - px * radius) + (p.Y - py * radius) * (p.Y - py * radius));
                    Assert.True(miss <= 1e-10 * radius,
                        $"{name}, {mode}, field {f + 1}, pupil ({px}, {py}): misses its stop point by {miss / radius:g3} of the stop radius.");
                }
            }
        }
    }

    /// <summary>The systems need aiming: without it the 20° chief ray of the singlet misses the
    /// stop centre by more than a tenth of the stop radius, and the 75° chief ray of the
    /// wide-angle pair does not reach the stop at all.</summary>
    [Fact]
    public void WithoutAiming_TheTestSystemsMissTheStopCentre()
    {
        var singlet = PupilAberratedSinglet();
        var system = Sys(10, RayAimingMode.Off, false, 0, 20);
        var trace = SequentialTrace.Create(singlet, system);
        var off = new RayLauncher(singlet, system).Launch(trace, 0, 1, 0, 0);
        double radius = new RayLauncher(singlet, system with { RayAiming = new RayAiming { Mode = RayAimingMode.Paraxial } }).StopRadius;
        var landing = trace.TracePath(off, NoApertures).Hits[singlet.StopIndex].Position;
        Assert.True(Math.Abs(landing.Y) > 0.1 * radius);

        var wide = WideAngle();
        var wideSystem = Sys(2, RayAimingMode.Off, false, 0, 75);
        var wideTrace = SequentialTrace.Create(wide, wideSystem);
        var wideRay = new RayLauncher(wide, wideSystem).Launch(wideTrace, 0, 1, 0, 0);
        Assert.True(trace.TracePath(off).Hits.Count > singlet.StopIndex);
        Assert.True(wideTrace.TracePath(wideRay, NoApertures).Hits.Count <= wide.StopIndex);
    }

    // ---- Aiming off is the paraxial-pupil launch, bit for bit ----

    /// <summary>
    /// With aiming off (and no vignetting factors) the launch is OP-20's paraxial-pupil launch,
    /// written out here independently from its definition: object at infinity, the field's
    /// direction (ūx, ūy, 1) normalised through (Px·r, Py·r, z_E), starting on the plane through
    /// surface 1's vertex at right angles to it; finite object, from (x̄, ȳ, −t0) to that pupil
    /// point; telecentric, along (Px·u, Py·u, 1). Compared bit for bit.
    /// </summary>
    [Fact]
    public void WithAimingOff_TheLaunchIsTheParaxialPupilLaunch_BitForBit()
    {
        var atInfinity = PupilAberratedSinglet();
        var finite = Lens(150, S(60, 6, 1.6), S(-200, 20), S(Inf, 60, stop: true));
        var cases = new (SequentialLens Lens, OpticalSystemData System)[]
        {
            (atInfinity, Sys(10, RayAimingMode.Off, false, 0, 7, 20)),
            (finite, new OpticalSystemData
            {
                Aperture = new Aperture { Type = ApertureType.ObjectSpaceNumericalAperture, Value = 0.05 },
                Fields = new FieldSet { Type = FieldType.ObjectHeight, Points = new[] { new FieldPoint(), new FieldPoint { X = 2 * Mm, Y = 9 * Mm } } },
            }),
            (finite, new OpticalSystemData
            {
                Aperture = new Aperture { Type = ApertureType.ObjectSpaceNumericalAperture, Value = 0.05, TelecentricObjectSpace = true },
                Fields = new FieldSet { Type = FieldType.ObjectHeight, Points = new[] { new FieldPoint(), new FieldPoint { Y = 9 * Mm } } },
            }),
        };
        foreach (var (lens, system) in cases)
        {
            var launcher = new RayLauncher(lens, system);
            var paraxial = launcher.Paraxial;
            var trace = SequentialTrace.Create(lens, system);
            foreach (var (hx, hy) in new[] { (0.0, 0.0), (0.0, 1.0), (0.3, -0.6) })
                foreach (var (px, py) in new[] { (0.0, 0.0), (0.0, 1.0), (-0.4, 0.8) })
                {
                    var (fx, fy) = OpticsConventions.DenormalizeField((hx, hy), system.Fields.Scale);
                    var (cx, cy) = paraxial.TraceChiefRay(new FieldPoint { X = fx, Y = fy });
                    Vector3D start, direction;
                    if (lens.ObjectAtInfinity)
                    {
                        direction = new Vector3D(cx.Slopes[0], cy.Slopes[0], 1);
                        direction /= direction.Length;
                        double r = paraxial.EntrancePupilDiameter!.Value / 2;
                        var pupil = new Vector3D(px * r, py * r, paraxial.EntrancePupilPosition!.Value);
                        start = pupil - direction * Vector3D.Dot(pupil, direction);
                    }
                    else
                    {
                        start = new Vector3D(cx.Heights[0], cy.Heights[0], -lens.Surfaces[0].Thickness);
                        if (paraxial.EntrancePupilAtInfinity)
                        {
                            double u = Math.Abs(paraxial.MarginalRay.Slopes[0]);
                            direction = new Vector3D(px * u, py * u, 1);
                        }
                        else
                        {
                            double r = paraxial.EntrancePupilDiameter!.Value / 2;
                            direction = new Vector3D(px * r, py * r, paraxial.EntrancePupilPosition!.Value) - start;
                            if (direction.Z < 0) direction = -direction;
                        }
                    }
                    var expected = trace.NewRay(start, direction);
                    var actual = launcher.Launch(trace, hx, hy, px, py);
                    Assert.Equal(expected, actual);
                }
        }
    }

    // ---- Stop radius ----

    /// <summary>
    /// Real aiming takes the stop radius from the real marginal ray (from the centre of the
    /// object, through the rim of the paraxial pupil); so aimed at (0, 1) on axis it is that
    /// same ray: here the parallel ray at height r. Paraxial aiming takes the paraxial marginal
    /// height. Float by stop size takes the stop's semi-diameter in both modes (OpticStudio help,
    /// "Ray Aiming": with float by stop size "both real and paraxial rays will be aimed to the
    /// real stop exactly").
    /// </summary>
    [Fact]
    public void TheStopRadius_IsTheMarginalRaysHeight_OrTheStopSize()
    {
        var lens = PupilAberratedSinglet();
        var real = Sys(10, RayAimingMode.Real, false, 0, 10);
        var launcher = new RayLauncher(lens, real);
        var trace = SequentialTrace.Create(lens, real);
        var aimed = launcher.Launch(trace, 0, 0, 0, 1);
        var unaimed = new RayLauncher(lens, real with { RayAiming = new RayAiming() }).Launch(trace, 0, 0, 0, 1);
        Assert.Equal(unaimed.Direction, aimed.Direction);
        Assert.True((aimed.Position - unaimed.Position).Length <= 1e-9 * 5 * Mm);

        var paraxial = new RayLauncher(lens, real with { RayAiming = new RayAiming { Mode = RayAimingMode.Paraxial } });
        Assert.Equal(Math.Abs(paraxial.Paraxial.MarginalRay.Heights[lens.StopIndex]), paraxial.StopRadius);
        Assert.NotEqual(paraxial.StopRadius, launcher.StopRadius);    // the real ray has spherical aberration

        var floating = Lens(Inf, S(60, 6, 1.6), S(-200, 20), S(Inf, 60, stop: true, semiDiameterMm: 3));
        foreach (var mode in new[] { RayAimingMode.Paraxial, RayAimingMode.Real })
        {
            var system = Sys(10, mode, false, 0, 10) with { Aperture = new Aperture { Type = ApertureType.FloatByStopSize } };
            Assert.Equal(3 * Mm, new RayLauncher(floating, system).StopRadius);
        }
        Assert.True(double.IsNaN(new RayLauncher(lens, Sys(10, RayAimingMode.Off, false, 0)).StopRadius));
    }

    // ---- Pupil shift and compression only seed the aim ----

    [Fact]
    public void PupilShiftAndCompress_ChangeOnlyTheFirstGuess()
    {
        var lens = PupilAberratedSinglet();
        var plain = Sys(10, RayAimingMode.Paraxial, false, 0, 10, 20);
        var assisted = plain with
        {
            RayAiming = plain.RayAiming with { PupilShiftY = 0.4 * Mm, PupilShiftZ = -3 * Mm, PupilCompressX = 0.05, PupilCompressY = 0.1, ScalePupilShiftByField = true }
        };
        var a = new RayLauncher(lens, plain);
        var b = new RayLauncher(lens, assisted);
        var trace = SequentialTrace.Create(lens, plain);
        foreach (var (px, py) in new[] { (0.0, 0.0), (0.0, 1.0), (0.6, -0.6) })
        {
            var ra = trace.TracePath(a.Launch(trace, 0, 1, px, py), NoApertures).Hits[lens.StopIndex].Position;
            var rb = trace.TracePath(b.Launch(trace, 0, 1, px, py), NoApertures).Hits[lens.StopIndex].Position;
            Assert.True((ra - rb).Length <= 2e-10 * a.StopRadius, $"pupil ({px}, {py})");
        }
    }

    // ---- Vignetting factors ----

    /// <summary>The remapping is the help's two transformations: P′ = VD + P(1 − VC), then P″ is
    /// P′ turned by θ. With aiming off a vignetted field's ray is the unvignetted system's ray at
    /// P″, bit for bit.</summary>
    [Fact]
    public void VignettingFactors_RemapThePupil_ByTheHelpsEquations()
    {
        var lens = PupilAberratedSinglet();
        var factors = new FieldPoint { Y = 20, DecenterX = 0.05, DecenterY = -0.2, CompressionX = 0.1, CompressionY = 0.3, TiltDegrees = 30 };
        var vignetted = Sys(10, RayAimingMode.Off, false, 0) with { Fields = new FieldSet { Points = new[] { new FieldPoint(), factors } } };
        var clear = VignettingTools.Clear(vignetted);
        var trace = SequentialTrace.Create(lens, vignetted);
        var withFactors = new RayLauncher(lens, vignetted);
        var without = new RayLauncher(lens, clear);
        double px = 0.6, py = -0.8;
        double x1 = 0.05 + px * (1 - 0.1), y1 = -0.2 + py * (1 - 0.3);
        double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);   // θ = 30°; compared to 14 digits below
        var (ex, ey) = (x1 * c - y1 * s, x1 * s + y1 * c);
        var (ax, ay) = withFactors.VignettedPupil(0, 1, px, py);
        Assert.Equal(ex, ax, 14);
        Assert.Equal(ey, ay, 14);
        Assert.Equal(without.Launch(trace, 0, 1, ax, ay), withFactors.Launch(trace, 0, 1, px, py));
        // The unvignetted field is untouched, bit for bit.
        Assert.Equal(without.Launch(trace, 0, 0, px, py), withFactors.Launch(trace, 0, 0, px, py));
        Assert.Equal((px, py), PupilVignetting.Apply(default, px, py));
    }

    /// <summary>Between fields on the y axis the factors are interpolated linearly in Hy and held
    /// beyond the ends; off the axis the nearest field's are used.</summary>
    [Fact]
    public void VignettingFactors_AreInterpolatedAlongY_AndOtherwiseTakenFromTheNearestField()
    {
        var fields = new FieldSet
        {
            Points = new[] { new FieldPoint(), new FieldPoint { Y = 10, DecenterY = 0.2, CompressionY = 0.4 }, new FieldPoint { Y = 20, DecenterY = 0.4, CompressionY = 0.5 } }
        };
        var v = new PupilVignetting(fields);
        static void Near(VignettingFactors expected, VignettingFactors actual)
        {
            foreach (var (e, a) in new[] { (expected.DecenterX, actual.DecenterX), (expected.DecenterY, actual.DecenterY),
                (expected.CompressionX, actual.CompressionX), (expected.CompressionY, actual.CompressionY), (expected.TiltDegrees, actual.TiltDegrees) })
                Assert.True(Math.Abs(e - a) <= 1e-15, $"{actual} is not {expected}");
        }
        // The fields normalise to Hy = 0, 0.5 and 1; Hy = 0.25 and 0.75 are the midpoints.
        Near(new VignettingFactors(0, 0.1, 0, 0.2, 0), v.At(0, 0.25));
        Near(new VignettingFactors(0, 0.3, 0, 0.45, 0), v.At(0, 0.75));
        Assert.Equal(VignettingFactors.Of(fields.Points[2]), v.At(0, 1));
        Assert.Equal(VignettingFactors.Of(fields.Points[0]), v.At(0, -0.5));       // held at the end
        Assert.Equal(VignettingFactors.Of(fields.Points[1]), v.At(0.1, 0.45));     // off the axis: nearest

        var general = new PupilVignetting(new FieldSet { Points = new[] { new FieldPoint(), new FieldPoint { X = 10, DecenterX = 0.2 } } });
        Assert.Equal(VignettingFactors.Of(new FieldPoint { X = 10, DecenterX = 0.2 }), general.At(0.6, 0));
        Assert.True(general.At(0.4, 0).IsZero);
    }

    /// <summary>
    /// A vignetting case with a closed form. Object at infinity; a plane dummy surface carries a
    /// circular aperture of radius a = r (the pupil radius) a distance D in front of the stop,
    /// with nothing but air between. A ray of field angle θ through the stop at (x, y) meets the
    /// dummy at (x, y − D·tan θ), so in normalised pupil coordinates the open region is the unit
    /// circle intersected with the unit circle centred at (0, s), s = D·tan θ / r. Its vertical
    /// edges through x = 0 are at 1 and s − 1, so VDY = s/2 and VCY = 1 − (1 − (s − 1))/2 = s/2;
    /// the two circles cross at y = s/2, where the half-width is √(1 − s²/4), so VDX = 0 and
    /// VCX = 1 − √(1 − s²/4). The open fraction of the pupil is the lens area of two unit
    /// circles d = s apart over π: (2·acos(s/2) − (s/2)·√(4 − s²))/π. Ray aiming changes nothing
    /// here (no optics before the stop, so the paraxial pupil is the stop).
    /// </summary>
    private static (SequentialLens Lens, double RadiusMm, double DistanceMm) VignettingBench()
    {
        double r = 5, d = 20;
        return (Lens(Inf, S(Inf, d, aperture: new CircularAperture(0, r * Mm)), S(Inf, 2, stop: true), S(50, 5, 1.5), S(Inf, 90)), r, d);
    }

    [Theory]
    [InlineData(RayAimingMode.Off)]
    [InlineData(RayAimingMode.Paraxial)]
    public void SetVignetting_FindsTheClosedFormFactors(RayAimingMode mode)
    {
        var (lens, r, d) = VignettingBench();
        double[] angles = { 0, 5, 10 };
        var system = Sys(2 * r, mode, false, angles);
        var result = VignettingTools.SetVignetting(lens, system);
        Assert.Empty(result.Notes);
        for (int f = 0; f < angles.Length; f++)
        {
            var p = result.System.Fields.Points[f];
            double s = d * Math.Tan(angles[f] * Math.PI / 180) / r;
            // Edges are bisected to 1e-10 of the pupil; the factors are their sums and differences.
            Assert.True(Math.Abs(p.DecenterX) <= 1e-8, $"VDX {p.DecenterX}");
            Assert.True(Math.Abs(p.DecenterY - s / 2) <= 1e-8, $"VDY {p.DecenterY} vs {s / 2}");
            Assert.True(Math.Abs(p.CompressionY - s / 2) <= 1e-8, $"VCY {p.CompressionY} vs {s / 2}");
            Assert.True(Math.Abs(p.CompressionX - (1 - Math.Sqrt(1 - s * s / 4))) <= 1e-8, $"VCX {p.CompressionX}");
            Assert.Equal(0, p.TiltDegrees);
            Assert.Equal(system.Fields.Points[f].Y, p.Y);
        }
        Assert.True(result.System.Fields.Points[0].IsVignetted == false);

        // The four remapped marginal rays now pass (just inside the edges).
        var launcher = new RayLauncher(lens, result.System);
        var trace = SequentialTrace.Create(lens, result.System);
        foreach (var (px, py) in new[] { (0.0, 0.999), (0.0, -0.999), (0.999, 0.0), (-0.999, 0.0) })
            Assert.True(trace.Trace(launcher.Launch(trace, 0, 1, px, py)).IsOk, $"({px}, {py})");
        Assert.False(trace.Trace(launcher.Launch(trace, 0, 1, 0, -1.001)).IsOk);
    }

    [Fact]
    public void TheVignettingCurve_IsTheOpenFractionOfThePupil()
    {
        var (lens, r, d) = VignettingBench();
        var system = Sys(2 * r, RayAimingMode.Off, false, 0, 10);
        var curve = VignettingTools.Curve(lens, system, 40, 5);
        Assert.Equal(new[] { 0, 0.25, 0.5, 0.75, 1 }, curve.Hy);
        Assert.Equal(1, curve.Fraction[0]);
        for (int i = 0; i < curve.Hy.Count; i++)
        {
            double s = d * Math.Tan(curve.Hy[i] * 10 * Math.PI / 180) / r;
            double open = (2 * Math.Acos(s / 2) - s / 2 * Math.Sqrt(4 - s * s)) / Math.PI;
            // A grid of spacing 1/40 counts the area to about the boundary's length times the
            // spacing over the area, ~1e-2 at worst; the edge errors largely cancel, 5e-3 holds.
            Assert.True(Math.Abs(curve.Fraction[i] - open) <= 5e-3, $"Hy {curve.Hy[i]}: {curve.Fraction[i]} vs {open}");
        }
    }

    [Fact]
    public void ReplaceVignettingWithApertures_ClearsTheFactors_AndKeepsTheVignettedRays()
    {
        var (lens, r, _) = VignettingBench();
        var set = VignettingTools.SetVignetting(lens, Sys(2 * r, RayAimingMode.Off, false, 0, 5, 10)).System;
        var replaced = VignettingTools.ReplaceWithApertures(lens, set);
        Assert.All(replaced.System.Fields.Points, p => Assert.False(p.IsVignetted));
        Assert.Same(lens.Surfaces[1].Aperture, replaced.Lens.Surfaces[1].Aperture);       // its own aperture kept
        for (int j = 2; j < lens.ImageIndex; j++)
            Assert.IsType<CircularAperture>(replaced.Lens.Surfaces[j].Aperture);
        Assert.Null(replaced.Lens.Surfaces[lens.ImageIndex].Aperture);

        var launcher = new RayLauncher(replaced.Lens, replaced.System);
        var vignetted = new RayLauncher(lens, set);
        var trace = SequentialTrace.Create(replaced.Lens, replaced.System);
        var original = SequentialTrace.Create(lens, set);
        for (int f = 0; f < set.Fields.Points.Count; f++)
        {
            var (hx, hy) = set.Fields.Normalized(f);
            foreach (var (px, py) in new[] { (0.0, 0.999), (0.0, -0.999), (0.999, 0.0), (-0.999, 0.0), (0.0, 0.0) })
            {
                var (qx, qy) = vignetted.VignettedPupil(hx, hy, px, py);
                Assert.True(trace.Trace(launcher.Launch(trace, hx, hy, qx, qy)).IsOk, $"field {f + 1}, ({px}, {py})");
                Assert.True(original.Trace(vignetted.Launch(original, hx, hy, px, py)).IsOk);
            }
        }
        var none = VignettingTools.ReplaceWithApertures(lens, VignettingTools.Clear(set));
        Assert.Same(lens, none.Lens);
    }

    // ---- Real image height (closes OP-18's deferred gate) ----

    /// <summary>
    /// A real-image-height field launches the chief ray of the angle field whose real chief ray
    /// meets the image at that height: trace the 20° chief ray to the image, read its height h,
    /// and the real-image-height field h gives the same ray (to the solve's tolerance, 1e-13 of
    /// the field), with aiming off and on. The paraxial engine still refuses the type.
    /// </summary>
    [Theory]
    [InlineData(RayAimingMode.Off)]
    [InlineData(RayAimingMode.Real)]
    public void ARealImageHeightField_LaunchesTheChiefRayThatReachesThatHeight(RayAimingMode mode)
    {
        var lens = PupilAberratedSinglet();
        var byAngle = Sys(10, mode, false, 0, 20);
        var launcher = new RayLauncher(lens, byAngle);
        var trace = SequentialTrace.Create(lens, byAngle);
        var chief = launcher.Launch(trace, 0, 1, 0, 0);
        double height = trace.Trace(chief).Position.Y;
        Assert.True(height > 0.01);

        var byImage = byAngle with
        {
            Fields = new FieldSet { Type = FieldType.RealImageHeight, Points = new[] { new FieldPoint(), new FieldPoint { Y = height } } }
        };
        var imageLauncher = new RayLauncher(lens, byImage);
        var imageTrace = SequentialTrace.Create(lens, byImage);
        var solved = imageLauncher.Launch(imageTrace, 0, 1, 0, 0);
        Assert.True((solved.Direction - chief.Direction).Length <= 1e-12, $"directions differ by {(solved.Direction - chief.Direction).Length:g3}");
        Assert.True(Math.Abs(imageTrace.Trace(solved).Position.Y - height) <= 1e-11 * height);
        var marginal = imageLauncher.Launch(imageTrace, 0, 1, 0, 1);
        Assert.True((marginal.Direction - launcher.Launch(trace, 0, 1, 0, 1).Direction).Length <= 1e-12);
        Assert.Equal((0.0, 0.0), imageLauncher.ParaxialField(0, 0));

        Assert.Throws<NotSupportedException>(() => ParaxialSystem.Create(lens.ToParaxialLens(), byImage));
        var tooFar = byImage with { Fields = new FieldSet { Type = FieldType.RealImageHeight, Points = new[] { new FieldPoint { Y = 10.0 } } } };
        var ex = Assert.Throws<InvalidOperationException>(() => new RayLauncher(lens, tooFar).Launch(SequentialTrace.Create(lens, tooFar), 0, 1, 0, 0));
        Assert.Contains("Real image height", ex.Message);
    }

    // ---- Parallel batches ----

    [Fact]
    public void AnAimedBatch_InParallel_IsBitwiseTheSequentialOne()
    {
        var lens = WideAngle();
        var system = Sys(2, RayAimingMode.Real, false, 0, 30, 60, 75) with
        {
            Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 0.5 }, new Wavelength { Micrometers = 0.6 } } }
        };
        var request = new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(7) };
        var one = BatchTracer.Trace(lens, system, request, maxDegreeOfParallelism: 1);
        var many = BatchTracer.Trace(lens, system, request, maxDegreeOfParallelism: 4);
        Assert.Equal(one.Rays, many.Rays);
        Assert.Equal(one.ChiefRays, many.ChiefRays);
        Assert.All(one.ChiefRays, c => Assert.True(c.IsOk, $"{c.Status} at {c.FailedAtSurface}"));
    }

    // ---- A ray that cannot be aimed ----

    /// <summary>
    /// A stop point no real ray reaches comes back failed with NoConvergence at the stop, never
    /// as a plausible ray landing elsewhere. A strong singlet with the stop 12 mm behind it, at
    /// 15°: the stop lies in a caustic of the field's rays, so the meridional rays' landing
    /// height on the stop has a minimum above zero. The test scans the meridional rays (the
    /// field's direction through every height of the entrance-pupil plane within ±8 mm) and
    /// checks that none lands within 0.3 mm of the centre before asking for the chief ray.
    /// </summary>
    [Fact]
    public void AStopPointNoRayReaches_IsAFailedRay_AtTheStop()
    {
        var lens = Lens(Inf, S(30, 8, 1.7), S(-80, 12), S(Inf, 40, stop: true));
        var system = Sys(16, RayAimingMode.Paraxial, false, 0, 15);
        var launcher = new RayLauncher(lens, system);
        var trace = SequentialTrace.Create(lens, system);
        double zE = launcher.Paraxial.EntrancePupilPosition!.Value;
        var d = new Vector3D(0, Math.Tan(15 * Math.PI / 180), 1);
        d /= d.Length;
        for (int i = -400; i <= 400; i++)
        {
            var pupil = new Vector3D(0, i * 0.02 * Mm, zE);
            var path = trace.TracePath(trace.NewRay(pupil - d * Vector3D.Dot(pupil, d), d), NoApertures);
            if (path.Hits.Count > lens.StopIndex)
                Assert.True(Math.Abs(path.Hits[lens.StopIndex].Position.Y) > 0.3 * Mm, $"height {i * 0.02} mm lands at {path.Hits[lens.StopIndex].Position.Y}");
        }
        var aimed = launcher.LaunchAimed(trace, 0, 1, 0, 0);
        Assert.False(aimed.Converged);
        Assert.Equal(RayStatus.NoConvergence, aimed.Ray.Status);
        Assert.Equal(lens.StopIndex, aimed.Ray.FailedAtSurface);
        Assert.False(string.IsNullOrEmpty(aimed.Failure));
    }

    // ---- Settings ----

    [Fact]
    public void RayAimingSettings_AreChecked_AndNamed()
    {
        static List<string> Check(RayAiming a) { var p = new List<string>(); a.Validate(p); return p; }
        Assert.Empty(Check(new RayAiming()));
        Assert.NotEmpty(Check(new RayAiming { PupilCompressX = 1 }));
        Assert.NotEmpty(Check(new RayAiming { PupilShiftZ = double.NaN }));
        Assert.NotEmpty(new OpticalSystemData { RayAiming = new RayAiming { PupilCompressY = 2 } }.Validate());
        Assert.Equal(new[] { "Off", "Paraxial", "Real" }, RayAimingModes.Names.All.Select(RayAimingModes.Names.SavedName));
        Assert.Equal(RayAimingMode.Off, OpticalSystemData.Default.RayAiming.Mode);
    }

    [Fact]
    public void TheRayAimingGroup_RoundTripsThroughTheFileForm_AndAnOlderFileOpensWithAimingOff()
    {
        var data = OpticalSystemData.Default with
        {
            Units = new SystemUnits { LensUnit = LensUnit.Inch },
            RayAiming = new RayAiming
            {
                Mode = RayAimingMode.Real, Robust = true, PupilShiftX = 0.00254, PupilShiftY = -0.0127, PupilShiftZ = 0.0508,
                PupilCompressX = 0.1, PupilCompressY = -0.2, ScalePupilShiftByField = true
            }
        };
        var settings = SystemDataSettings.ToSettings(data);
        Assert.Equal("Real", settings.RayAiming);
        Assert.Equal(0.1, settings.PupilShiftX!.Value, 14);        // 0.00254 m in inches
        var notes = new List<string>();
        var back = SystemDataSettings.FromSettings(settings, notes).RayAiming;
        Assert.Empty(notes);
        Assert.Equal(data.RayAiming with { PupilShiftX = 0, PupilShiftY = 0, PupilShiftZ = 0 }, back with { PupilShiftX = 0, PupilShiftY = 0, PupilShiftZ = 0 });
        foreach (var (a, b) in new[] { (data.RayAiming.PupilShiftX, back.PupilShiftX), (data.RayAiming.PupilShiftY, back.PupilShiftY), (data.RayAiming.PupilShiftZ, back.PupilShiftZ) })
            Assert.True(Math.Abs(a - b) <= 2 * (Math.BitIncrement(Math.Abs(a)) - Math.Abs(a)));

        Assert.Equal(new RayAiming(), SystemDataSettings.FromSettings(new OpticsSystemSettings(), notes).RayAiming);
        Assert.Empty(notes);
        var unknown = SystemDataSettings.FromSettings(new OpticsSystemSettings { RayAiming = "Telepathic" }, notes);
        Assert.Equal(RayAimingMode.Off, unknown.RayAiming.Mode);
        Assert.Contains(notes, n => n.Contains("'Telepathic'"));
    }

    // ---- The Vignetting Plot analysis ----

    [Fact]
    public void TheVignettingPlot_IsRegistered_AndPlotsTheFraction()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.True(registry.TryGet(VignettingPlotAnalysis.SavedId, out var analysis));
        var empty = analysis.Compute(new OpticsAnalysisContext("P", new OpticsSettings()), analysis.CreateDefaultSettings(), default);
        Assert.Equal(new[] { "No surfaces" }, empty.Sections.Select(s => s.Heading));

        var (lens, r, _) = VignettingBench();
        var system = Sys(2 * r, RayAimingMode.Off, false, 0, 10);
        var context = new OpticsAnalysisContext("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });
        var result = new VignettingPlotAnalysis(_ => lens).Compute(context, new VignettingPlotSettings { RayDensity = 10, FieldDensity = 3 }, default);
        var plot = Assert.IsType<LinePlot>(Assert.Single(result.Plots));
        Assert.Equal(new[] { 0.0, 5, 10 }, plot.Curves[0].X);
        Assert.Equal(1, plot.Curves[0].Y[0]);
        Assert.True(plot.Curves[0].Y[2] < 0.7);
        Assert.Throws<ArgumentException>(() => new VignettingPlotAnalysis(_ => lens).Compute(context, new VignettingPlotSettings { RayDensity = 4 }, default));
    }
}
