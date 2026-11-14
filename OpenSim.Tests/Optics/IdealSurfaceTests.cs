using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-27: the idealised and special sequential surfaces against closed forms derived in each
/// test's comments, and against the surfaces they stand for (a Fresnel facet against its parent
/// conic or asphere, an ABCD block against the thin-lens train its matrix is built from). Each
/// surface's definition is its OpticStudio 2025 R2 help page, cited on the class under test.
/// Focal lengths, distances, indices and coefficients are arbitrary test inputs, not reference
/// data.
/// </summary>
public sealed class IdealSurfaceTests
{
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;   // 2^-52

    // ---- Builders ----

    private static SequentialSurface Plane(double thickness, double n = 1, bool stop = false, bool mirror = false,
        SurfaceShape? shape = null, double radius = double.PositiveInfinity, double k = 0) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = k, Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror, Shape = shape,
    };

    private static SequentialLens Lens(double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(double epd, FieldType type = FieldType.Angle, params double[] fields) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet
        {
            Type = type,
            Points = fields.Length == 0 ? new[] { new FieldPoint() } : fields.Select(f => new FieldPoint { Y = f }).ToArray()
        },
    };

    private static void Close(double expected, double actual, double tolerance, string what = "")
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    private static void CloseVector(Vector3D expected, Vector3D actual, double tolerance, string what)
    {
        Close(expected.X, actual.X, tolerance, what + " x");
        Close(expected.Y, actual.Y, tolerance, what + " y");
        Close(expected.Z, actual.Z, tolerance, what + " z");
    }

    // ---- Gate: a Paraxial surface gives zero OPD for an on-axis object at any aperture ----

    [Fact]
    public void Paraxial_FiniteOnAxisObject_ZeroPathDifferenceAtAnyAperture()
    {
        // Derivation. The surface's rule n′u′ = n·u − x·φ (u = L/N) is linear in the slopes. A ray
        // from the axial object at z = −a (u = x/a) is at x + z·(u − x·φ) = x·(1 + z/a − z·φ) at
        // z after the surface (n = n′ = 1), which is zero for every x at z_i = 1/(φ − 1/a), the
        // Gaussian image. With a = 0.2 m and f = 0.05 m, z_i = 1/(20 − 5) = 1/15 m. Every ray
        // ends on the axial image point, so its path to the image plane is its path to the
        // image point, and the optical-path term of mode 3 (conjugates from the paraxial
        // marginal ray) makes those equal: PathFromChief is zero. The entrance pupil is the
        // surface itself, 0.2 m across: rays leave the object up to atan(0.5) = 26.6° from the
        // axis and reach the image up to atan(1.5) = 56° from it.
        double a = 0.2, f = 0.05, zi = 1.0 / 15;
        var lens = Lens(a, Plane(zi, stop: true, shape: new ParaxialShape(f)));
        var system = Sys(0.2);
        var resolved = ParaxialShape.ResolveConjugates(lens, system);
        Close(-a, ((ParaxialShape)resolved.Surfaces[1].Shape!).ObjectDistance, 4 * Eps * a, "resolved object distance");

        var result = BatchTracer.Trace(resolved, system, new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(15) });
        double tolerance = 256 * Eps * (a + zi);
        Assert.True(result.Rays.Count > 150);
        foreach (var traced in result.Rays)
        {
            Assert.True(traced.Ray.IsOk, $"ray failed: {traced.Ray.Status}");
            Close(0, traced.Ray.Position.X, tolerance, "x at image");
            Close(0, traced.Ray.Position.Y, tolerance, "y at image");
            Close(0, traced.PathFromChief, tolerance, "path from chief");
        }

        // The term matters: at infinite conjugates (mode 2) the same rays do not agree.
        var unresolved = BatchTracer.Trace(lens, system, new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(15) });
        Assert.True(unresolved.Rays.Max(r => Math.Abs(r.PathFromChief)) > 1e-6,
            "the infinite-conjugate term should not fit a finite object");
    }

    [Fact]
    public void Paraxial_ObjectAtInfinity_ZeroPathDifferenceAtEveryFieldAndAperture()
    {
        // Derivation. A collimated beam of slope u (n = 1) leaves at u′ = u − x·φ and reaches the
        // back focal plane z = 1/φ = f at x + f·(u − x/f) = f·u, the same point for every x, so
        // the image of a field angle θ is at f·tan θ. Mode 2's term (infinite conjugates) makes
        // the paths from the incoming plane wavefront to that point equal. F/1 at f = 0.1 m.
        double f = 0.1;
        var lens = Lens(double.PositiveInfinity, Plane(f, stop: true, shape: new ParaxialShape(f)));
        var system = Sys(0.1, FieldType.Angle, 0, 5, 20);
        var result = BatchTracer.Trace(lens, system, new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(13) });
        double tolerance = 256 * Eps * f;
        for (int field = 0; field < 3; field++)
        {
            double theta = new[] { 0.0, 5, 20 }[field] * Math.PI / 180;
            for (int p = 0; p < result.Pupil.Count; p++)
            {
                var traced = result.At(field, 0, p);
                Assert.True(traced.Ray.IsOk, $"ray failed: {traced.Ray.Status}");
                Close(f * Math.Tan(theta), traced.Ray.Position.Y, tolerance, $"y at focus, field {field}");
                Close(0, traced.Ray.Position.X, tolerance, "x at focus");
                Close(0, traced.PathFromChief, tolerance, $"path from chief, field {field}");
            }
        }
    }

    [Fact]
    public void Paraxial_ImagingIntoGlass_FocusesAtNPrimeOverPower()
    {
        // n′u′ = −x·φ for a collimated axial beam, which reaches the axis at z = x/(−u′) = n′/φ: the
        // focal length in air f gives the image-space focal distance n′·f (the help: the focal
        // length "is that which would be measured in air ... although the paraxial model will
        // support imaging into a non-unity index medium").
        double f = 0.05, nPrime = 1.5;
        var lens = Lens(double.PositiveInfinity, Plane(nPrime * f, n: nPrime, stop: true, shape: new ParaxialShape(f)));
        var result = BatchTracer.Trace(lens, Sys(0.08), new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(11) });
        double tolerance = 256 * Eps * nPrime * f;
        foreach (var traced in result.Rays)
        {
            Assert.True(traced.Ray.IsOk);
            Close(0, traced.Ray.Position.Y, tolerance, "y at focus");
            Close(0, traced.PathFromChief, tolerance, "path from chief");
        }
    }

    [Fact]
    public void Paraxial_ResolvedConjugates_ZeroPathDifferenceOffAxis()
    {
        // As the first test with object heights: a ray from (0, y0, −a) with u = (x − y0)/a
        // reaches z_i at x·(1 + z_i/a − z_i·φ) − z_i·y0/a = −y0·z_i/a = −y0/3 for every x, so each
        // object point images to one point, and mode 3's term places P on the object plane, which
        // is where every ray of that point crosses it.
        double a = 0.2, f = 0.05, zi = 1.0 / 15;
        var lens = Lens(a, Plane(zi, stop: true, shape: new ParaxialShape(f)));
        var system = Sys(0.1, FieldType.ObjectHeight, 0, 0.01, 0.03);
        var resolved = ParaxialShape.ResolveConjugates(lens, system);
        var result = BatchTracer.Trace(resolved, system, new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(11) });
        double tolerance = 256 * Eps * (a + zi);
        for (int field = 0; field < 3; field++)
        {
            double y0 = new[] { 0.0, 0.01, 0.03 }[field];
            for (int p = 0; p < result.Pupil.Count; p++)
            {
                var traced = result.At(field, 0, p);
                Assert.True(traced.Ray.IsOk);
                Close(-y0 / 3, traced.Ray.Position.Y, tolerance, $"image height, field {field}");
                Close(0, traced.PathFromChief, tolerance, $"path from chief, field {field}");
            }
        }
    }

    [Fact]
    public void Paraxial_ParaxialEngineSeesItsPower()
    {
        double f = 0.08;
        var lens = Lens(double.PositiveInfinity, Plane(f, stop: true, shape: new ParaxialShape(f)));
        var model = ParaxialSystem.Create(lens.ToParaxialLens(), Sys(0.01));
        Close(1 / f, model.Powers[1], 4 * Eps / f, "power");
        Close(f, model.EffectiveFocalLength!.Value, 4 * Eps * f, "EFL");
        Close(f, model.ParaxialImageDistance!.Value, 4 * Eps * f, "image distance");
    }

    [Fact]
    public void ParaxialXY_TurnsXAndYWithTheirOwnPowers()
    {
        // n′u′ = −x·φx and −y·φy for a ray parallel to the axis in air (help: "Paraxial XY").
        double px = 5, py = 12;
        var lens = Lens(double.PositiveInfinity, Plane(0.1, stop: true, shape: new ParaxialXYShape(px, py)));
        var trace = SequentialTrace.Create(lens, Sys(0.01));
        double x = 0.003, y = -0.002;
        var path = trace.TracePath(trace.NewRay(new Vector3D(x, y, 0), Vector3D.UnitZ));
        var d = path.Hits[1].Direction;
        Close(-x * px, d.X / d.Z, 8 * Eps, "x slope");
        Close(-y * py, d.Y / d.Z, 8 * Eps, "y slope");
        var model = ParaxialSystem.Create(lens.ToParaxialLens(), Sys(0.01));
        Close(1 / py, model.EffectiveFocalLength!.Value, 4 * Eps / py, "paraxial EFL is the y one");
    }

    // ---- Gate: an ABCD surface equals the equivalent thin-lens train ----

    [Fact]
    public void Abcd_EqualsTheThinLensTrainItsMatrixIsBuiltFrom()
    {
        // In air the Paraxial surface maps (x, u = L/N) by [[1, 0], [−φ, 1]] and a gap d by
        // [[1, d], [0, 1]] (a straight line advances x by d·tan), so lens f1, gap d, lens f2 is
        // M = L(φ2)·T(d)·L(φ1) exactly, at any ray height and angle, not only paraxially. The ABCD
        // surface with that matrix in x and y, followed by the train's last gap, must put every
        // ray at the same image point with the same direction. (ParaxialMatrix's Refraction and
        // Transfer are these matrices when n = 1.)
        double f1 = 0.1, f2 = -0.06, d = 0.03, t = 0.07;
        var m = ParaxialMatrix.Refraction(1 / f2) * ParaxialMatrix.Transfer(d) * ParaxialMatrix.Refraction(1 / f1);
        var train = Lens(double.PositiveInfinity,
            Plane(0, stop: true),
            Plane(d, shape: new ParaxialShape(f1)),
            Plane(t, shape: new ParaxialShape(f2)));
        var block = Lens(double.PositiveInfinity,
            Plane(0, stop: true),
            Plane(0, shape: new AbcdShape(m, m)),
            Plane(t));
        var system = Sys(0.04);
        var trainTrace = SequentialTrace.Create(train, system);
        var blockTrace = SequentialTrace.Create(block, system);
        double tolerance = 64 * Eps * 0.1;
        int count = 0;
        foreach (double x in new[] { 0.0, 0.004, -0.015 })
        foreach (double y in new[] { 0.0, 0.01, -0.02 })
        foreach (double angle in new[] { 0.0, 3, -10 })
        {
            var direction = new Vector3D(Math.Sin(angle * Math.PI / 180) * 0.6, Math.Sin(angle * Math.PI / 180) * 0.8, 1);
            direction /= direction.Length;
            var a = trainTrace.Trace(trainTrace.NewRay(new Vector3D(x, y, 0), direction));
            var b = blockTrace.Trace(blockTrace.NewRay(new Vector3D(x, y, 0), direction));
            Assert.True(a.IsOk && b.IsOk);
            CloseVector(a.Position, b.Position, tolerance, "image point");
            CloseVector(a.Direction, b.Direction, tolerance, "direction");
            count++;
        }
        Assert.Equal(27, count);

        // Paraxially: the same focal length and back focal distance.
        var pa = ParaxialSystem.Create(train.ToParaxialLens(), system);
        var pb = ParaxialSystem.Create(block.ToParaxialLens(), system);
        Close(pa.EffectiveFocalLength!.Value, pb.EffectiveFocalLength!.Value, 1e-12 * Math.Abs(pa.EffectiveFocalLength.Value), "EFL");
        Close(pa.BackFocalLength!.Value, pb.BackFocalLength!.Value, 1e-12 * Math.Abs(pa.BackFocalLength.Value), "BFL");
        Close(pa.ParaxialImageDistance!.Value, pb.ParaxialImageDistance!.Value, 1e-12 * Math.Abs(pa.ParaxialImageDistance.Value), "image distance");
    }

    [Fact]
    public void Abcd_MovingTheRayAtTheStop_IsRefusedWithAReason()
    {
        var m = new ParaxialMatrix(1, 0.01, 0, 1);
        var lens = Lens(double.PositiveInfinity, Plane(0.05, stop: true, shape: new AbcdShape(m, m)), Plane(0.05));
        var problems = lens.Validate();
        Assert.Contains(problems, p => p.Contains("cannot be surface 1, the stop or the last surface"));
        Assert.Throws<ArgumentException>(() => new AbcdShape(new ParaxialMatrix(0, 1, -1, 0), ParaxialMatrix.Identity));
    }

    // ---- Gate: a Fresnel surface's rays equal the parent conic's or asphere's in direction ----

    [Fact]
    public void Fresnel_DirectionsEqualTheParentConic()
    {
        // A ray parallel to the axis meets the Fresnel plane and the parent conic at the same
        // (x, y); the Fresnel surface refracts with the conic's normal there (help: "Fresnel"),
        // so the two leave in the same direction. The normals agree analytically:
        // (−c·x, −c·y, 1 − c(1 + k)z) ∝ (−z_x, −z_y, 1) because 1 − c(1 + k)z = √(1 − (1 + k)c²r²).
        double radius = 0.05, k = -0.6, n = 1.5;
        var fresnel = Lens(double.PositiveInfinity, Plane(0.1, n, stop: true, shape: new FresnelShape(), radius: radius, k: k));
        var parent = Lens(double.PositiveInfinity, Plane(0.1, n, stop: true, radius: radius, k: k));
        var tf = SequentialTrace.Create(fresnel, Sys(0.02));
        var tp = SequentialTrace.Create(parent, Sys(0.02));
        foreach (var (x, y) in new[] { (0.0, 0.0), (0.004, 0.0), (0.003, -0.007), (-0.012, 0.009) })
        {
            var a = tf.TracePath(tf.NewRay(new Vector3D(x, y, 0), Vector3D.UnitZ));
            var b = tp.TracePath(tp.NewRay(new Vector3D(x, y, 0), Vector3D.UnitZ));
            Assert.True(a.Status == RayStatus.Ok && b.Status == RayStatus.Ok);
            Close(0, a.Hits[1].Position.Z, 0, "the Fresnel intercept is on the plane");
            CloseVector(b.Hits[1].Direction, a.Hits[1].Direction, 16 * Eps, $"direction at ({x}, {y})");
        }
        var model = ParaxialSystem.Create(fresnel.ToParaxialLens(), Sys(0.02));
        Close((n - 1) / radius, model.Powers[1], 4 * Eps * n / radius, "paraxial power of the facet");
    }

    [Fact]
    public void Fresnel_WithPolynomialTerms_DirectionsEqualTheParentAsphere()
    {
        // The parent even asphere z = c r²/(1 + √(1 − (1 + k)c²r²)) + α1 r² + α2 r⁴ has
        // dz/dr = c·r/√(1 − (1 + k)c²r²) + 2α1·r + 4α2·r³ (differentiated by hand), normal
        // (−z′·x/r, −z′·y/r, 1)/|…|; the refracted direction follows from Snell's law with it.
        double c = 1 / 0.08, k = 0.3, a1 = 0.7, a2 = -150, n = 1.6;
        var shape = new FresnelShape(new[] { a1, a2 });
        var lens = Lens(double.PositiveInfinity, Plane(0.1, n, stop: true, shape: shape, radius: 1 / c, k: k));
        var trace = SequentialTrace.Create(lens, Sys(0.02));
        foreach (var (x, y) in new[] { (0.005, 0.0), (0.002, -0.006), (-0.009, 0.004) })
        {
            double r = Math.Sqrt(x * x + y * y);
            double dz = c * r / Math.Sqrt(1 - (1 + k) * c * c * r * r) + 2 * a1 * r + 4 * a2 * r * r * r;
            var normal = new Vector3D(-dz * x / r, -dz * y / r, 1);
            normal /= normal.Length;
            Assert.True(ConicIntersection.Refract(Vector3D.UnitZ, normal, 1, n, out var expected));
            var path = trace.TracePath(trace.NewRay(new Vector3D(x, y, 0), Vector3D.UnitZ));
            CloseVector(expected, path.Hits[1].Direction, 32 * Eps, $"direction at ({x}, {y})");
        }
        var model = ParaxialSystem.Create(lens.ToParaxialLens(), Sys(0.02));
        Close((n - 1) * (c + 2 * a1), model.Powers[1], 8 * Eps * n * c, "paraxial power c + 2α1");
    }

    [Fact]
    public void ExtendedFresnel_SubstrateOnly_EqualsTheStandardSurface_AndPatternOnly_EqualsFresnel()
    {
        double radius = 0.06, k = -1.2, n = 1.5, cf = 1 / 0.04;
        var system = Sys(0.02);
        // Substrate only: intercept and refraction are the substrate's, the Standard conic's.
        var substrate = SequentialTrace.Create(Lens(double.PositiveInfinity,
            Plane(0.1, n, stop: true, shape: new ExtendedFresnelShape(null, 0, 0, null), radius: radius, k: k)), system);
        var standard = SequentialTrace.Create(Lens(double.PositiveInfinity, Plane(0.1, n, stop: true, radius: radius, k: k)), system);
        // Pattern only on a plane substrate: the Fresnel surface of the pattern's curvature.
        var pattern = SequentialTrace.Create(Lens(double.PositiveInfinity,
            Plane(0.1, n, stop: true, shape: new ExtendedFresnelShape(null, cf, 0, null))), system);
        var fresnel = SequentialTrace.Create(Lens(double.PositiveInfinity,
            Plane(0.1, n, stop: true, shape: new FresnelShape(), radius: 1 / cf)), system);
        foreach (var (x, y) in new[] { (0.004, 0.001), (-0.006, 0.008) })
        {
            var start = new Vector3D(x, y, -0.01);
            var d = new Vector3D(0.02, -0.05, 1) / new Vector3D(0.02, -0.05, 1).Length;
            var a = substrate.TracePath(substrate.NewRay(start, d));
            var b = standard.TracePath(standard.NewRay(start, d));
            CloseVector(b.Hits[1].Position, a.Hits[1].Position, 64 * Eps * 0.01, "substrate intercept");
            CloseVector(b.Hits[1].Direction, a.Hits[1].Direction, 64 * Eps, "substrate direction");
            var e = pattern.TracePath(pattern.NewRay(start, d));
            var g = fresnel.TracePath(fresnel.NewRay(start, d));
            CloseVector(g.Hits[1].Direction, e.Hits[1].Direction, 16 * Eps, "pattern direction");
        }
    }

    [Fact]
    public void GeneralizedFresnel_LinearYTerm_RefractsLikeATiltedPlane()
    {
        // Help ("Generalized Fresnel"): a flat substrate with a linear tilt term refracts "as
        // though the surface was tilted". Term 2 is y, so the facet is f = A2·y/R with slope
        // tan θ = A2/R, normal (0, −sin θ, cos θ). An axial ray meets it at incidence θ, leaves at
        // θ′ with sin θ′ = sin θ/n, on the far side of the normal: the direction is
        // cos θ′·n̂ + sin θ′·(0, cos θ, sin θ) = (0, sin(θ′ − θ), cos(θ′ − θ)) (derived).
        double a2 = 0.002, r = 0.01, n = 1.5;
        var shape = new GeneralizedFresnelShape(null, r, new[] { 0.0, a2 });
        var trace = SequentialTrace.Create(Lens(double.PositiveInfinity, Plane(0.1, n, stop: true, shape: shape)), Sys(0.02));
        double theta = Math.Atan(a2 / r), thetaPrime = Math.Asin(Math.Sin(theta) / n);
        var expected = new Vector3D(0, Math.Sin(thetaPrime - theta), Math.Cos(thetaPrime - theta));
        foreach (var (x, y) in new[] { (0.0, 0.0), (0.005, -0.003) })
        {
            var path = trace.TracePath(trace.NewRay(new Vector3D(x, y, 0), Vector3D.UnitZ));
            CloseVector(expected, path.Hits[1].Direction, 16 * Eps, "tilted-plane direction");
        }
    }

    [Fact]
    public void ExtendedPolynomialTermOrder_MatchesTheHelpExamples()
    {
        // OpticStudio 2025 R2 help, "Extended Polynomial": "the 12th term ... is the coefficient
        // on the term in x2y2" and "xy3 ... is term number 13"; order 20 ends at term 230.
        Assert.Equal((1, 0), ExtendedPolynomialTerms.Exponents(1));
        Assert.Equal((0, 1), ExtendedPolynomialTerms.Exponents(2));
        Assert.Equal((2, 2), ExtendedPolynomialTerms.Exponents(12));
        Assert.Equal((1, 3), ExtendedPolynomialTerms.Exponents(13));
        Assert.Equal((0, 20), ExtendedPolynomialTerms.Exponents(230));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExtendedPolynomialTerms.Exponents(231));
    }

    [Fact]
    public void CylinderFresnel_RefractsOnlyInY_LikeTheSphereOfItsFacetCurvature()
    {
        // The facet is a function of y alone, so a ray parallel to the axis at (x, y) leaves as
        // a ray through a Standard sphere of the facet's curvature at (0, y), with no x
        // component. On a curved cylindrical substrate the intercept is at the substrate's sag
        // c_s·y²/(1 + √(1 − c_s²y²)) (k = 0) and the direction is unchanged, the help's "the
        // refraction ... accounts for only the Fresnel sag".
        double cf = 1 / 0.05, cs = 1 / 0.2, n = 1.5;
        var system = Sys(0.02);
        var flat = SequentialTrace.Create(Lens(double.PositiveInfinity,
            Plane(0.1, n, stop: true, shape: new CylinderFresnelShape(null, cf, 0, null))), system);
        var curved = SequentialTrace.Create(Lens(double.PositiveInfinity,
            Plane(0.1, n, stop: true, shape: new CylinderFresnelShape(null, cf, 0, null), radius: 1 / cs)), system);
        var sphere = SequentialTrace.Create(Lens(double.PositiveInfinity, Plane(0.1, n, stop: true, radius: 1 / cf)), system);
        foreach (var (x, y) in new[] { (0.006, 0.004), (-0.003, -0.008) })
        {
            var reference = sphere.TracePath(sphere.NewRay(new Vector3D(0, y, 0), Vector3D.UnitZ)).Hits[1].Direction;
            var a = flat.TracePath(flat.NewRay(new Vector3D(x, y, 0), Vector3D.UnitZ)).Hits[1];
            var b = curved.TracePath(curved.NewRay(new Vector3D(x, y, 0), Vector3D.UnitZ)).Hits[1];
            CloseVector(reference, a.Direction, 16 * Eps, "flat substrate direction");
            CloseVector(reference, b.Direction, 16 * Eps, "curved substrate direction");
            Close(cs * y * y / (1 + Math.Sqrt(1 - cs * cs * y * y)), b.Position.Z, 16 * Eps * 0.001, "substrate sag");
        }
    }

    // ---- Retro Reflect ----

    [Fact]
    public void RetroReflect_SendsEveryRayBackAlongItself()
    {
        // Help ("Retro Reflect"): on a MIRROR the rays are "undeviated", so they "follow the
        // incident path" back. A plane 0.05 m before the retroreflector and the image surface
        // 0.05 m back from it (negative after the mirror) are the same plane, so the ray returns
        // to its launch point with the opposite direction, after a path of twice the
        // forward one.
        var lens = Lens(double.PositiveInfinity,
            Plane(0.05, stop: true),
            Plane(-0.05, mirror: true, shape: new RetroReflectShape()));
        var trace = SequentialTrace.Create(lens, Sys(0.02));
        foreach (var d0 in new[] { new Vector3D(0, 0, 1), new Vector3D(0.1, -0.2, 1), new Vector3D(-0.3, 0.05, 1) })
        {
            var d = d0 / d0.Length;
            var start = new Vector3D(0.003, -0.002, 0);
            var ray = trace.Trace(trace.NewRay(start, d));
            Assert.True(ray.IsOk);
            CloseVector(new Vector3D(start.X, start.Y, 0), ray.Position, 16 * Eps * 0.05, "return point");
            CloseVector(-d, ray.Direction, 4 * Eps, "return direction");
            Close(2 * 0.05 / d.Z, ray.OpticalPath, 16 * Eps * 0.1, "path out and back");
        }
    }

    // ---- Conjugate ----

    [Fact]
    public void Conjugate_RealPair_MirrorImagesOnePointOntoTheOther()
    {
        // Help ("Conjugate"): with z1, z2 of one sign, |P − P1| + |P − P2| is constant on the
        // surface, |P1| + |P2| since it passes through the vertex; a mirror then sends every ray
        // from P1 to P2 with that total path. Light travels +z, so both points are at negative z.
        var p1 = new Vector3D(0, 0.002, -0.08);
        var p2 = new Vector3D(0.001, -0.003, -0.12);
        var shape = new ConjugateShape(p1, p2);
        var lens = Lens(-p1.Z, Plane(p2.Z, stop: true, mirror: true, shape: shape));
        var trace = SequentialTrace.Create(lens, Sys(0.02));
        double tolerance = 256 * Eps * 0.2;
        foreach (double tx in new[] { -0.01, 0, 0.007 })
        foreach (double ty in new[] { -0.012, 0, 0.004 })
        {
            var d = new Vector3D(tx, ty, 0) - p1;
            var ray = trace.Trace(trace.NewRay(p1, d / d.Length));
            Assert.True(ray.IsOk, $"ray failed: {ray.Status}");
            Close(p2.X, ray.Position.X, tolerance, "x at P2");
            Close(p2.Y, ray.Position.Y, tolerance, "y at P2");
            Close(p1.Length + p2.Length, ray.OpticalPath, tolerance, "path P1 to P2");
        }
    }

    [Fact]
    public void Conjugate_VirtualPair_ReflectsAsIfFromTheSecondPoint()
    {
        // Opposite signs: |P − P1| − |P − P2| = |P1| − |P2| on the surface. Rays from P1 leave
        // the mirror as if from P2 behind it: traced back (a virtual step) they pass through P2,
        // and the path |X − P1| − |X − P2| is that constant.
        var p1 = new Vector3D(0, 0.001, -0.1);
        var p2 = new Vector3D(0, 0.002, 0.05);
        var lens = Lens(-p1.Z, Plane(p2.Z, stop: true, mirror: true, shape: new ConjugateShape(p1, p2)));
        var trace = SequentialTrace.Create(lens, Sys(0.02));
        double tolerance = 256 * Eps * 0.2;
        foreach (double ty in new[] { -0.01, 0, 0.006 })
        {
            var d = new Vector3D(0.003, ty, 0) - p1;
            var ray = trace.Trace(trace.NewRay(p1, d / d.Length));
            Assert.True(ray.IsOk, $"ray failed: {ray.Status}");
            Close(p2.X, ray.Position.X, tolerance, "x at P2");
            Close(p2.Y, ray.Position.Y, tolerance, "y at P2");
            Close(p1.Length - p2.Length, ray.OpticalPath, tolerance, "virtual path");
        }
    }

    [Fact]
    public void Conjugate_EqualPointsOnTheAxis_IsTheSphereOfThatRadius()
    {
        // Help: "a sphere can be formed by setting the x and y values to zero, and the two z
        // values each to the radius". Then 2|P − (0, 0, R)| = 2R: the sphere of radius R through
        // the vertex, sag R − √(R² − r²), vertex curvature 1/R.
        double r = 0.05;
        var shape = new ConjugateShape(new Vector3D(0, 0, r), new Vector3D(0, 0, r));
        var surface = Plane(0.1, shape: shape);
        foreach (double h in new[] { 0.0, 0.01, 0.03 })
            Close(r - Math.Sqrt(r * r - h * h), shape.Sag(h * 0.6, h * 0.8, surface), 16 * Eps * r, $"sag at {h}");
        Close(1 / r, shape.ParaxialEquivalent(surface).Curvature, 8 * Eps / r, "paraxial curvature");
        Assert.Throws<ArgumentException>(() => new ConjugateShape(new Vector3D(0, 0, 0), new Vector3D(0, 0, 1)));
    }

    // ---- Lenslet array ----

    [Fact]
    public void LensletArray_EachCellIsTheLensletAboutItsCentre()
    {
        // A ray at (i·W + dx, j·H + dy) meets cell (i, j)'s lenslet as a ray at (dx, dy) meets
        // the single Standard surface: same sag, same direction. Outside the nx × ny cells the
        // ray misses the surface.
        double w = 0.002, h = 0.003, radius = 0.01, n = 1.5;
        var array = SequentialTrace.Create(Lens(double.PositiveInfinity,
            Plane(0.05, n, stop: true, shape: new LensletArrayShape(3, 3, w, h), radius: radius)), Sys(0.01));
        var single = SequentialTrace.Create(Lens(double.PositiveInfinity, Plane(0.05, n, stop: true, radius: radius)), Sys(0.01));
        foreach (var (i, j, dx, dy) in new[] { (1, -1, 0.0003, 0.0004), (0, 1, -0.0006, -0.0011), (-1, 0, 0.0, 0.0) })
        {
            var a = array.TracePath(array.NewRay(new Vector3D(i * w + dx, j * h + dy, 0), Vector3D.UnitZ)).Hits[1];
            var b = single.TracePath(single.NewRay(new Vector3D(dx, dy, 0), Vector3D.UnitZ)).Hits[1];
            Close(b.Position.Z, a.Position.Z, 16 * Eps * 0.001, "lenslet sag");
            CloseVector(b.Direction, a.Direction, 16 * Eps, "lenslet direction");
        }
        var outside = array.Trace(array.NewRay(new Vector3D(2 * w, 0, 0), Vector3D.UnitZ));
        Assert.Equal(RayStatus.MissedSurface, outside.Status);
        Assert.Throws<ArgumentException>(() => new LensletArrayShape(2, 3, w, h));
    }

    // ---- OPD limitations are stated ----

    [Fact]
    public void OpdLimitation_IsStatedWhereOpticStudioRefusesOpd()
    {
        var plane = Plane(0.1);
        var curved = Plane(0.1, radius: 0.1);
        Assert.Null(new ParaxialShape(0.1).OpdLimitation(plane));
        Assert.NotNull(new AbcdShape(ParaxialMatrix.Identity, ParaxialMatrix.Identity).OpdLimitation(plane));
        Assert.Null(new ExtendedFresnelShape(null, 1, 0, null).OpdLimitation(plane));
        Assert.NotNull(new ExtendedFresnelShape(null, 1, 0, null).OpdLimitation(curved));
        Assert.Null(new FresnelShape().OpdLimitation(curved));
    }
}
