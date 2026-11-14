using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Layout;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-20: the real ray trace against closed forms derived in the comments of each test (conic
/// foci, Fermat's principle for the hyperbolic plano-convex lens, Snell's law as conservation
/// of the tangential component, the paraxial engine as the small-ray limit). Spencer and Murty
/// and Welford were not on hand; nothing is copied from them. Radii, thicknesses and indices are
/// arbitrary test inputs, not reference data.
/// </summary>
public sealed class RealRayTraceTests
{
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;   // 2^-52

    // ---- Builders ----

    private static SequentialSurface S(double radius, double thickness, double n = 1, double k = 0, bool stop = false,
        bool mirror = false, SurfaceAperture? aperture = null, double? semiDiameter = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = k, Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror, Aperture = aperture,
        SemiDiameter = semiDiameter,
    };

    private static SequentialLens Lens(double objectDistance, params SequentialSurface[] surfaces) =>
        LensIn(1, objectDistance, surfaces);

    private static SequentialLens LensIn(double objectIndex, double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance, Medium = ParaxialMedium.Constant(objectIndex) } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double epd = 0.01, params double[] angleFields) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet
        {
            Type = FieldType.Angle,
            Points = angleFields.Length == 0 ? new[] { new FieldPoint() } : angleFields.Select(a => new FieldPoint { Y = a }).ToArray()
        },
    };

    private static void Close(double expected, double actual, double tolerance, string what = "")
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    /// <summary>Rays from a point (in surface 1's frame) in a cone of half-angle
    /// <paramref name="maxDegrees"/> about +z, rings of 8 directions.</summary>
    private static IEnumerable<Vector3D> Cone(double maxDegrees, int rings)
    {
        yield return Vector3D.UnitZ;
        for (int i = 1; i <= rings; i++)
        {
            double a = maxDegrees * i / rings * Math.PI / 180;
            for (int m = 0; m < 8; m++)
            {
                double b = m * Math.PI / 4;
                yield return new Vector3D(Math.Sin(a) * Math.Cos(b), Math.Sin(a) * Math.Sin(b), Math.Cos(a));
            }
        }
    }

    // ---- Gate: conic mirrors image their foci perfectly ----

    [Fact]
    public void ParabolicMirror_IsStigmaticOnAxis_ToMachinePrecision()
    {
        // A concave mirror facing light travelling +z has its centre of curvature toward −z, so
        // R < 0 (Conventions §3). The paraboloid (k = −1) is h² = 2R·z, with its focus at
        // z = R/2, so the image surface is a thickness R/2 (negative after the mirror) away.
        // Derivation of the path: a ray parallel to the axis at height h, launched on the plane
        // z = 0, meets the mirror at z = h²/(2R) < 0 (a virtual step of length z), then runs to
        // the focus a distance √(h² + (R/2 − z)²) = √(2Rz + R²/4 − Rz + z²) = |R/2 + z| = −R/2 − z.
        // The two add to −R/2 = f for every h, and every ray ends on the focus.
        double r = -0.2, f = -r / 2;
        var lens = Lens(double.PositiveInfinity, S(r, r / 2, k: -1, stop: true, mirror: true));
        var system = Sys(epd: 0.16);   // F/0.625: rays up to 0.8·f from the axis
        var result = BatchTracer.Trace(lens, system, new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(21) });

        // Machine precision: every coordinate is a handful of roundings of numbers no larger
        // than f, so 32 ulps of f bounds the error with room to spare.
        double tolerance = 32 * Eps * f;
        Assert.True(result.Rays.Count > 300);
        foreach (var traced in result.Rays)
        {
            var ray = traced.Ray;
            Assert.True(ray.IsOk, $"ray failed: {ray.Status} at {ray.FailedAtSurface}");
            Close(0, ray.Position.X, tolerance, "x at focus");
            Close(0, ray.Position.Y, tolerance, "y at focus");
            Close(f, ray.OpticalPath, tolerance, "optical path");
            Close(0, traced.PathFromChief, tolerance, "path from chief");
        }
    }

    [Fact]
    public void EllipsoidalMirror_ImagesItsNearFocusOntoItsFarFocus()
    {
        // The conic r² − 2R·z + (1 + k)·z² = 0 with −1 < k < 0 is an ellipse of semi-axis
        // a = |R|/(1 + k) along z, centred at z = R/(1 + k), with b² = R²/(1 + k). Its eccentricity
        // is e = √(−k), its foci c_f = a·e from the centre, so from the vertex at
        // a − c_f = |R|/(1 + e) (near) and a + c_f = |R|/(1 − e) (far). Every ray from one focus
        // reflects through the other, with total path 2a = d1 + d2 (the axial ray's).
        double r = -0.1, k = -0.36, e = Math.Sqrt(-k);
        double d1 = -r / (1 + e), d2 = -r / (1 - e);
        var lens = Lens(d1, S(r, -d2, k: k, stop: true, mirror: true));
        var trace = SequentialTrace.Create(lens, Sys());
        double tolerance = 64 * Eps * d2;
        int traced = 0;
        foreach (var d in Cone(30, 6))
        {
            var ray = trace.Trace(trace.NewRay(new Vector3D(0, 0, -d1), d));
            Assert.True(ray.IsOk, $"ray failed: {ray.Status}");
            Close(0, ray.Position.X, tolerance, "x");
            Close(0, ray.Position.Y, tolerance, "y");
            Close(d1 + d2, ray.OpticalPath, tolerance, "optical path");
            traced++;
        }
        Assert.Equal(49, traced);
    }

    [Fact]
    public void HyperbolicMirror_ImagesOneFocusAtTheOther()
    {
        // With k < −1 the same conic is a hyperbola: a = |R|/|1 + k|, centre at z = R/(1 + k)
        // (behind the vertex for R < 0), e = √(−k), foci a·e from the centre: one in front of
        // the vertex at |R|/(1 + e), the other behind it at |R|/(e − 1). A ray from the front
        // focus reflects as if from the rear focus, so traced back (a virtual step) every ray
        // passes through it, with path |PF1| − |PF2| = −2a, which is d1 − d2 for the axial ray.
        double r = -0.1, k = -2.25, e = Math.Sqrt(-k);
        double d1 = -r / (1 + e), d2 = -r / (e - 1);
        // The image is behind the mirror: +d2 along z after the reflection (a virtual image).
        var lens = Lens(d1, S(r, d2, k: k, stop: true, mirror: true));
        var trace = SequentialTrace.Create(lens, Sys());
        double tolerance = 64 * Eps * d2;
        foreach (var d in Cone(30, 6))
        {
            var ray = trace.Trace(trace.NewRay(new Vector3D(0, 0, -d1), d));
            Assert.True(ray.IsOk, $"ray failed: {ray.Status}");
            Close(0, ray.Position.X, tolerance, "x");
            Close(0, ray.Position.Y, tolerance, "y");
            Close(d1 - d2, ray.OpticalPath, tolerance, "optical path");
        }
    }

    // ---- Gate: the hyperbolic plano-convex lens ----

    [Fact]
    public void HyperbolicPlanoConvexLens_WithConicMinusNSquared_FocusesACollimatedBeamPerfectly()
    {
        // Derivation (Fermat): a plane wave in glass of index n meets the surface z(r) and must
        // reach the focus F = (0, f) with the same optical path as the axial ray, n·z + √(r² +
        // (f − z)²) = f. Squaring, r² + f² − 2fz + z² = f² − 2nfz + n²z², i.e.
        // r² − 2f(1 − n)·z + (1 − n²)·z² = 0. That is the conic r² − 2R·z + (1 + k)·z² = 0 with
        // k = −n² and R = f(1 − n): the focus is f = R/(1 − n) behind the vertex, which is also
        // the paraxial back focal length (power (1 − n)/R for a surface from glass to air).
        double n = 1.5, r2 = -0.05, t = 0.01;
        double f = r2 / (1 - n);
        var lens = Lens(double.PositiveInfinity, S(double.PositiveInfinity, t, n, stop: true), S(r2, f, k: -n * n));
        var system = Sys(epd: 0.06);
        var paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), system);
        Close(f, paraxial.BackFocalLength!.Value, 1e-15, "paraxial BFL");

        var result = BatchTracer.Trace(lens, system, new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(21) });
        double tolerance = 64 * Eps * f;
        foreach (var traced in result.Rays)
        {
            Assert.True(traced.Ray.IsOk, $"ray failed: {traced.Ray.Status} at {traced.Ray.FailedAtSurface}");
            Close(0, traced.Ray.Position.X, tolerance, "x at focus");
            Close(0, traced.Ray.Position.Y, tolerance, "y at focus");
            // From the launch plane z = 0: n·t through the glass, then the constant f.
            Close(n * t + f, traced.Ray.OpticalPath, tolerance, "optical path");
        }
    }

    // ---- Gate: Snell's law at every refraction; points on the surface ----

    /// <summary>Three elements with a prolate, an oblate and a hyperbolic surface.</summary>
    private static SequentialLens ConicTriplet() => Lens(double.PositiveInfinity,
        S(0.05, 0.008, 1.6, k: -0.5, stop: true),
        S(-0.08, 0.002),
        S(0.1, 0.005, 1.75, k: 2.0),
        S(0.06, 0.003),
        S(0.08, 0.006, 1.5, k: -3),
        S(-0.06, 0.06));

    [Fact]
    public void SnellsLaw_HoldsAtEveryRefraction_AndEveryHitIsOnItsSurface()
    {
        var lens = ConicTriplet();
        var system = Sys(0.02, 0, 5, 10);
        var trace = SequentialTrace.Create(lens, system);
        var launcher = new RayLauncher(lens, system);
        int checks = 0;
        for (int fi = 0; fi < system.Fields.Points.Count; fi++)
        {
            var (hx, hy) = system.Fields.Normalized(fi);
            foreach (var (px, py) in BatchTracer.SquareGrid(11))
            {
                var path = trace.TracePath(launcher.Launch(trace, hx, hy, px, py));
                foreach (var hit in path.Hits.Where(h => h.Surface > 0))
                {
                    var s = lens.Surfaces[hit.Surface];
                    // The point is on the conic: its z is the sag (Layout's independent formula).
                    double sag = new ConicSag(s.Curvature, s.Conic).Sag(hit.Position.X, hit.Position.Y);
                    Close(sag, hit.Position.Z, 1e-16, $"sag at surface {hit.Surface}");
                    Close(1, hit.Direction.Length, 4 * Eps, "unit direction");
                    if (hit.Surface == lens.ImageIndex || hit.Status != RayStatus.Ok || hit.IndexBefore == hit.IndexAfter) continue;
                    // Snell's law: n·(d × n̂) = n′·(d′ × n̂), the tangential component conserved,
                    // which holds the sines in ratio and the three vectors in one plane.
                    var before = Vector3D.Cross(hit.IncomingDirection, hit.Normal) * hit.IndexBefore;
                    var after = Vector3D.Cross(hit.Direction, hit.Normal) * hit.IndexAfter;
                    Close(before.X, after.X, 1e-14, "Snell x");
                    Close(before.Y, after.Y, 1e-14, "Snell y");
                    Close(before.Z, after.Z, 1e-14, "Snell z");
                    double sinI = Math.Sin(hit.AngleOfIncidenceDegrees * Math.PI / 180);
                    double sinT = Math.Sin(hit.AngleOfExitDegrees * Math.PI / 180);
                    Close(hit.IndexBefore * sinI, hit.IndexAfter * sinT, 1e-14, "n sin I");
                    checks++;
                }
            }
        }
        Assert.True(checks > 1000, $"only {checks} refractions checked");
    }

    // ---- Gate: the paraxial limit ----

    [Fact]
    public void ARealRay_InTheParaxialLimit_EqualsTheParaxialTrace_ThroughALensAndAMirror()
    {
        // A singlet and a concave mirror (thickness negative after it): the real ray's height
        // where it crosses each vertex plane and its slope dy/dz after each surface tend to the
        // paraxial ray's as the ray shrinks; the difference is of relative order (c·y)² and u²,
        // about 1e-12 at these sizes.
        var lens = Lens(double.PositiveInfinity,
            S(0.06, 0.005, 1.5168, k: -0.7),
            S(-0.2, 0.05),
            S(-0.3, -0.12, k: 0.4, stop: true, mirror: true));
        var system = Sys();
        var paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), system);
        var trace = SequentialTrace.Create(lens, system);

        foreach (var (h, u) in new[] { (1e-7, 0.0), (0.0, 1e-7), (3e-8, -2e-8) })
        {
            var expected = paraxial.Trace(h, paraxial.SignedIndices[0] * u);
            var direction = new Vector3D(0, u, 1);
            var path = trace.TracePath(trace.NewRay(new Vector3D(0, h, 0), direction));
            Assert.True(path.Final.IsOk);
            double scale = Math.Max(expected.Heights.Skip(1).Max(Math.Abs), 1e-300);
            double slopeScale = Math.Max(expected.Slopes.Max(Math.Abs), 1e-300);
            foreach (var hit in path.Hits.Where(x => x.Surface > 0))
            {
                int j = hit.Surface;
                // Height on the vertex plane: back along the incoming ray from the hit by z/N.
                double yVertex = hit.Position.Y - hit.Position.Z * hit.IncomingDirection.Y / hit.IncomingDirection.Z;
                Close(expected.Heights[j], yVertex, 1e-9 * scale, $"height at surface {j}");
                Close(expected.Slopes[j], hit.Direction.Y / hit.Direction.Z, 1e-9 * slopeScale, $"slope after surface {j}");
            }
        }
    }

    // ---- Failures are flagged, never dropped ----

    [Fact]
    public void AnAperture_StopsRaysOutsideIt_AndNamesTheSurface()
    {
        var singlet = new[] { S(0.05, 0.005, 1.5, stop: true), S(-0.05, 0.09, aperture: new CircularAperture(0, 0.003)) };
        var lens = Lens(double.PositiveInfinity, singlet);
        var system = Sys();
        var pupil = new[] { (0.0, 0.0), (0.0, 0.2), (0.0, 1.0), (1.0, 0.0) };
        var result = BatchTracer.Trace(lens, system, new BatchTraceRequest { Pupil = pupil });
        Assert.True(result.At(0, 0, 0).Ray.IsOk);
        Assert.True(result.At(0, 0, 1).Ray.IsOk);
        foreach (int p in new[] { 2, 3 })
        {
            var ray = result.At(0, 0, p).Ray;
            Assert.Equal(RayStatus.Vignetted, ray.Status);
            Assert.Equal(2, ray.FailedAtSurface);
            Assert.True(double.IsNaN(result.At(0, 0, p).PathFromChief));
        }
        Assert.Equal(pupil.Length, result.Rays.Count);

        var ignoring = BatchTracer.Trace(lens, system, new BatchTraceRequest { Pupil = pupil, Options = new TraceOptions { IgnoreApertures = true } });
        Assert.All(ignoring.Rays, r => Assert.True(r.Ray.IsOk));

        // An obscuration on surface 1 stops the chief ray there; the path keeps the hit.
        var obscured = Lens(double.PositiveInfinity,
            S(0.05, 0.005, 1.5, stop: true, aperture: new CircularAperture(0, 0.001) { IsObscuration = true }), S(-0.05, 0.09));
        var trace = SequentialTrace.Create(obscured, system);
        var launcher = new RayLauncher(obscured, system);
        var chief = trace.TracePath(launcher.Launch(trace, 0, 0, 0, 0));
        Assert.Equal(RayStatus.Vignetted, chief.Status);
        Assert.Equal(1, chief.FailedAtSurface);
        Assert.Equal(RayStatus.Vignetted, chief.Hits[^1].Status);
        Assert.True(trace.Trace(launcher.Launch(trace, 0, 0, 0, 0.5)).IsOk);

        // A spider's arms block the centre and the +x arm; a ray between arms passes.
        var spider = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, stop: true, aperture: new SpiderAperture(0.0004, 4)), S(-0.05, 0.09));
        var spiderTrace = SequentialTrace.Create(spider, system);
        var spiderLauncher = new RayLauncher(spider, system);
        Assert.Equal(RayStatus.Vignetted, spiderTrace.Trace(spiderLauncher.Launch(spiderTrace, 0, 0, 0.5, 0)).Status);
        Assert.True(spiderTrace.Trace(spiderLauncher.Launch(spiderTrace, 0, 0, 0.5, 0.5)).IsOk);
    }

    [Fact]
    public void TotalInternalReflection_IsFlaggedAtTheSurface()
    {
        // From glass (n = 1.5) to air at a plane: sin I > 1/n reflects totally. 60° has
        // 1.5·sin 60° ≈ 1.3 > 1; 30° has 0.75 and refracts to asin(0.75).
        var lens = LensIn(1.5, 0.01, S(double.PositiveInfinity, 0.05, stop: true));
        var trace = SequentialTrace.Create(lens, Sys());
        var start = new Vector3D(0, 0, -0.01);
        var steep = trace.TracePath(trace.NewRay(start, new Vector3D(Math.Sin(Math.PI / 3), 0, Math.Cos(Math.PI / 3))));
        Assert.Equal(RayStatus.TotalInternalReflection, steep.Status);
        Assert.Equal(1, steep.FailedAtSurface);
        Assert.Equal(RayStatus.TotalInternalReflection, steep.Hits[^1].Status);

        var gentle = trace.Trace(trace.NewRay(start, new Vector3D(Math.Sin(Math.PI / 6), 0, Math.Cos(Math.PI / 6))));
        Assert.True(gentle.IsOk);
        Close(1.5 * 0.5, gentle.Direction.X, 1e-15, "sine after refraction");
    }

    [Fact]
    public void ARayThatMissesASurface_IsFlaggedAtThatSurface()
    {
        // A sphere of radius 10 mm cannot be met by a ray parallel to the axis 20 mm from it.
        var lens = Lens(double.PositiveInfinity, S(0.01, 0.005, 1.5, stop: true), S(-0.05, 0.05));
        var trace = SequentialTrace.Create(lens, Sys());
        var path = trace.TracePath(trace.NewRay(new Vector3D(0, 0.02, 0), Vector3D.UnitZ));
        Assert.Equal(RayStatus.MissedSurface, path.Status);
        Assert.Equal(1, path.FailedAtSurface);
        Assert.Single(path.Hits);   // the launch point only

        // A ray parallel to a vertex plane never reaches it.
        Assert.Equal(RayStatus.MissedSurface, trace.Trace(trace.NewRay(new Vector3D(0, 0, -0.01), Vector3D.UnitX)).Status);
    }

    // ---- Parallel equals sequential ----

    [Fact]
    public void TheBatchTrace_InParallel_IsBitwiseTheSequentialOne()
    {
        var lens = ConicTriplet();
        var system = Sys(0.02, 0, 5, 10) with
        {
            Wavelengths = new WavelengthSet { Wavelengths = new[] { new Wavelength { Micrometers = 0.486 }, new Wavelength { Micrometers = 0.588 }, new Wavelength { Micrometers = 0.656 } } }
        };
        var request = new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(13) };
        var serial = BatchTracer.Trace(lens, system, request, maxDegreeOfParallelism: 1);
        var parallel = BatchTracer.Trace(lens, system, request, maxDegreeOfParallelism: null);
        Assert.Equal(serial.Rays.Count, parallel.Rays.Count);
        static long B(double v) => BitConverter.DoubleToInt64Bits(v);
        for (int i = 0; i < serial.Rays.Count; i++)
        {
            var a = serial.Rays[i];
            var b = parallel.Rays[i];
            Assert.Equal(a.Ray.Status, b.Ray.Status);
            Assert.Equal(a.Ray.FailedAtSurface, b.Ray.FailedAtSurface);
            Assert.Equal(B(a.Ray.Position.X), B(b.Ray.Position.X));
            Assert.Equal(B(a.Ray.Position.Y), B(b.Ray.Position.Y));
            Assert.Equal(B(a.Ray.Position.Z), B(b.Ray.Position.Z));
            Assert.Equal(B(a.Ray.Direction.X), B(b.Ray.Direction.X));
            Assert.Equal(B(a.Ray.Direction.Y), B(b.Ray.Direction.Y));
            Assert.Equal(B(a.Ray.Direction.Z), B(b.Ray.Direction.Z));
            Assert.Equal(B(a.Ray.OpticalPath), B(b.Ray.OpticalPath));
            Assert.Equal(B(a.PathFromChief), B(b.PathFromChief));
            Assert.Equal(B(a.Ray.Wavelength), B(b.Ray.Wavelength));
        }
    }

    // ---- Apertures by shape ----

    [Fact]
    public void ApertureShapes_PassAndBlockWhereTheirDefinitionsSay()
    {
        var annulus = new CircularAperture(0.001, 0.002);
        Assert.True(annulus.Passes(0.0015, 0, double.NaN));
        Assert.False(annulus.Passes(0.0005, 0, double.NaN));
        Assert.False(annulus.Passes(0.0025, 0, double.NaN));
        Assert.True((annulus with { IsObscuration = true }).Passes(0.0005, 0, double.NaN));

        var rectangle = new RectangularAperture(0.002, 0.001) { DecenterX = 0.001 };
        Assert.True(rectangle.Passes(0.0029, 0.0009, double.NaN));
        Assert.False(rectangle.Passes(-0.0011, 0, double.NaN));
        Assert.False(rectangle.Passes(0.001, 0.0011, double.NaN));

        var ellipse = new EllipticalAperture(0.002, 0.001);
        Assert.True(ellipse.Passes(0.0019, 0, double.NaN));
        Assert.False(ellipse.Passes(0, 0.0011, double.NaN));
        Assert.False(ellipse.Passes(0.0015, 0.0008, double.NaN));   // (0.75)² + (0.8)² > 1

        // Three arms at 0°, 120°, 240°; full width 0.2 mm.
        var spider = new SpiderAperture(0.0002, 3);
        Assert.False(spider.Passes(0.005, 0.00005, double.NaN));            // on the +x arm
        Assert.True(spider.Passes(-0.005, 0.00005, double.NaN));            // −x has no arm
        Assert.False(spider.Passes(-0.0025, 0.0025 * Math.Sqrt(3), double.NaN)); // on the 120° arm
        Assert.True(spider.Passes(0, 0.005, double.NaN));

        // An L-shaped outline: the notch at the top right is outside.
        var l = new PolygonAperture(new[] { (0.0, 0.0), (2.0, 0.0), (2.0, 1.0), (1.0, 1.0), (1.0, 2.0), (0.0, 2.0) });
        Assert.True(l.Passes(0.5, 1.5, double.NaN));
        Assert.True(l.Passes(1.5, 0.5, double.NaN));
        Assert.False(l.Passes(1.5, 1.5, double.NaN));
        Assert.False(l.Passes(-0.5, 0.5, double.NaN));

        var floating = new FloatingAperture();
        Assert.True(floating.Passes(0.003, 0.004, 0.005));
        Assert.False(floating.Passes(0.003, 0.0041, 0.005));
        Assert.Throws<InvalidOperationException>(() => floating.Passes(0, 0, double.NaN));

        var problems = new List<string>();
        new CircularAperture(0.002, 0.001).Validate("Surface 3", problems);
        new PolygonAperture(new[] { (0.0, 0.0), (1.0, 1.0) }).Validate("Surface 4", problems);
        new SpiderAperture(0.001, 0).Validate("Surface 5", problems);
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("Surface 3"));
    }

    // ---- Semi-diameters, ignored surfaces, the lens model ----

    [Fact]
    public void AutomaticSemiDiameters_AreTheLargestRayHeights_WithTheMargins()
    {
        // Object at infinity, axial field, stop on surface 1: the rim rays enter parallel to the
        // axis at the pupil radius and meet surface 1 at exactly that height.
        var lens = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, stop: true), S(-0.05, 0.09, semiDiameter: 0.007) with { ChipZone = 0.001 });
        var plain = SemiDiameterSizing.Compute(lens, Sys());
        Assert.Equal(0.005, plain.Clear[1]);
        Assert.True(plain.Automatic[1]);
        Assert.Equal(0.007, plain.Clear[2]);
        Assert.False(plain.Automatic[2]);
        Assert.Equal(0.008, plain.Mechanical[2], 15);
        Assert.Equal(0.005, plain.Mechanical[1]);
        Assert.Equal(0, plain.Clear[0]);
        Assert.Empty(plain.Notes);

        var withMargins = SemiDiameterSizing.Compute(lens, Sys() with
        {
            Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01, SemiDiameterMargin = 0.001, SemiDiameterMarginPercent = 10 }
        });
        Close((0.005 + 0.001) * 1.1, withMargins.Clear[1], 1e-17, "margins");
        Assert.Equal(0.007, withMargins.Clear[2]);

        // A floating aperture on a fixed 3 mm semi-diameter stops the rim of a 10 mm beam.
        var floating = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, stop: true, aperture: new FloatingAperture(), semiDiameter: 0.003), S(-0.05, 0.09));
        var result = BatchTracer.Trace(floating, Sys(), new BatchTraceRequest { Pupil = new[] { (0.0, 0.5), (0.0, 1.0) } });
        Assert.True(result.At(0, 0, 0).Ray.IsOk);
        Assert.Equal(RayStatus.Vignetted, result.At(0, 0, 1).Ray.Status);
    }

    [Fact]
    public void AnIgnoredSurface_TracesAsADummyPlane_AndConvertsSoToTheParaxialLens()
    {
        var withDummy = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, stop: true), S(-0.05, 0.03), S(double.PositiveInfinity, 0.06));
        var ignored = withDummy with
        {
            Surfaces = withDummy.Surfaces.Select((s, j) => j == 3
                ? s with { Curvature = 10, Conic = -2, Medium = ParaxialMedium.Constant(1.7), Aperture = new CircularAperture(0, 1e-6), IsIgnored = true }
                : s).ToArray()
        };
        var system = Sys(0.01, 0, 3);
        var a = BatchTracer.Trace(withDummy, system, new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(5) });
        var b = BatchTracer.Trace(ignored, system, new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(5) });
        for (int i = 0; i < a.Rays.Count; i++)
        {
            Assert.Equal(a.Rays[i].Ray.Position, b.Rays[i].Ray.Position);
            Assert.Equal(a.Rays[i].Ray.Direction, b.Rays[i].Ray.Direction);
            Assert.Equal(a.Rays[i].Ray.OpticalPath, b.Rays[i].Ray.OpticalPath);
        }
        var p = ignored.ToParaxialLens();
        Assert.Equal(0, p.Surfaces[3].Curvature);
        Assert.Equal(1.0, p.Surfaces[3].Medium.Index);
        Assert.Equal(ParaxialSystem.Create(withDummy.ToParaxialLens(), system).EffectiveFocalLength,
            ParaxialSystem.Create(p, system).EffectiveFocalLength);
    }

    [Fact]
    public void TheLens_ConvertsToTheParaxialLens_AndValidatesWhatOnlyARealTraceNeeds()
    {
        var lens = Lens(0.2, S(0.05, 0.005, 1.5, k: -1, stop: true, semiDiameter: 0.004), S(-0.05, 0.09, mirror: true));
        var p = lens.ToParaxialLens();
        Assert.Equal(lens.Surfaces.Count, p.Surfaces.Count);
        Assert.Equal(20, p.Surfaces[1].Curvature, 12);
        Assert.Equal(0.004, p.Surfaces[1].SemiDiameter);
        Assert.True(p.Surfaces[2].IsMirror);
        Assert.Equal(1, p.StopIndex);
        Assert.Empty(lens.Validate());

        var bad = lens with
        {
            Surfaces = new[]
            {
                lens.Surfaces[0] with { Aperture = new CircularAperture(0, 1) },
                lens.Surfaces[1] with { IsIgnored = true, Conic = double.NaN },
                lens.Surfaces[2] with { IsIgnored = true, ChipZone = -1 },
                lens.Surfaces[3],
            }
        };
        var problems = bad.Validate();
        Assert.Contains(problems, x => x.StartsWith("Object surface") && x.Contains("aperture"));
        Assert.Contains(problems, x => x.StartsWith("Surface 1") && x.Contains("stop cannot be ignored"));
        Assert.Contains(problems, x => x.StartsWith("Surface 1") && x.Contains("conic"));
        Assert.Contains(problems, x => x.StartsWith("Surface 2") && x.Contains("mirror cannot be ignored"));
        Assert.Contains(problems, x => x.StartsWith("Surface 2") && x.Contains("chip zone"));
        Assert.Throws<ArgumentException>(() => SequentialTrace.Create(bad, Sys()));
    }

    [Fact]
    public void SkippedSurfaces_AreLeftOutOfTheDrawnPath_Only()
    {
        var lens = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, stop: true), S(-0.05, 0.03), S(double.PositiveInfinity, 0.06) with { SkipRaysTo = true });
        var trace = SequentialTrace.Create(lens, Sys());
        var path = trace.TracePath(trace.NewRay(new Vector3D(0, 0.002, 0), Vector3D.UnitZ));
        Assert.Equal(5, path.Hits.Count);
        Assert.Equal(new[] { 0, 1, 2, 4 }, path.DrawnHits(lens).Select(h => h.Surface));
    }

    [Fact]
    public void FiniteObjectsAndGlobalPositions_FollowTheThicknesses()
    {
        // An object 0.1 m before surface 1: the axial object point is the launch point and the
        // global position of each vertex is the sum of the thicknesses before it.
        var lens = Lens(0.1, S(0.05, 0.005, 1.5, stop: true), S(-0.05, 0.09));
        var system = Sys() with { Aperture = new Aperture { Type = ApertureType.ObjectSpaceNumericalAperture, Value = 0.02 } };
        var trace = SequentialTrace.Create(lens, system);
        var launcher = new RayLauncher(lens, system);
        var chief = trace.TracePath(launcher.Launch(trace, 0, 0, 0, 0));
        Assert.True(chief.Final.IsOk);
        Assert.Equal(new Vector3D(0, 0, -0.1), chief.Hits[0].GlobalPosition);
        Assert.Equal(Vector3D.Zero, chief.Hits[0].Position);
        Assert.Equal(0.095, chief.Hits[3].GlobalPosition.Z, 15);
        Close(0.1 + 0.005 * 1.5 + 0.09, chief.Final.OpticalPath, 1e-15, "axial optical path");

        // The marginal ray leaves the object at asin(NA), as the paraxial engine's slope
        // tan(asin NA) defines it.
        var marginal = trace.TracePath(launcher.Launch(trace, 0, 0, 0, 1));
        Close(0.02, marginal.Hits[0].Direction.Y, 1e-15, "sine of the marginal ray");
    }

    // ---- The analyses ----

    [Fact]
    public void TheSingleRayTrace_IsRegistered_AndSaysSoWhenThereAreNoSurfaces()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.True(registry.TryGet(SingleRayTraceAnalysis.SavedId, out var analysis));
        var result = analysis.Compute(new OpticsAnalysisContext("P", new OpticsSettings()), analysis.CreateDefaultSettings(), default);
        Assert.Equal(new[] { "No surfaces" }, result.Sections.Select(s => s.Heading));
    }

    [Fact]
    public void TheSingleRayTrace_ListsEverySurface_AndNamesAFailure()
    {
        var lens = Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, stop: true), S(-0.05, 0.09, aperture: new CircularAperture(0, 0.003)));
        var analysis = new SingleRayTraceAnalysis(_ => lens);
        var context = new OpticsAnalysisContext("P", new OpticsSettings());
        var result = analysis.Compute(context, new SingleRayTraceSettings { Py = 0.2 }, default);
        Assert.Equal(new[] { "Ray", "Surfaces" }, result.Sections.Select(s => s.Heading));
        Assert.Equal(lens.Surfaces.Count, result.Sections[1].TableRows.Count);
        Assert.Contains("Reached the image surface", result.Text);

        var failed = analysis.Compute(context, new SingleRayTraceSettings { Py = 1, GlobalCoordinates = true }, default);
        Assert.Contains("Stopped by an aperture at surface 2", failed.Text);
        Assert.Equal(3, failed.Sections[1].TableRows.Count);
        Assert.Throws<ArgumentException>(() => analysis.Compute(context, new SingleRayTraceSettings { Wavelength = 2 }, default));

        var paraxial = ParaxialDataAnalysis.FromSequential(_ => lens).Compute(context, new ParaxialDataSettings(), default);
        Assert.Contains("Effective focal length", paraxial.Text);
    }
}
