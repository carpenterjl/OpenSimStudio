using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// The OP-35 gate (docs/Optics-Gates.md): a paraboloid on axis has zero OPD; a defocused perfect
/// lens has the OPD of the gap between two spheres along each ray, a closed form derived below;
/// moving the reference sphere to infinity reproduces the plane (telecentric) reference
/// continuously. Every expected value is derived in the test; the lenses are arbitrary inputs.
/// </summary>
public sealed class OpdGateTests
{
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;

    private static SequentialSurface S(double radius, double thickness, double n = 1, double k = 0, bool stop = false, bool mirror = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = k, Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror,
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

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    // ---- Zero OPD of a paraboloid on axis ----

    [Theory]
    [InlineData(OpdReference.ExitPupil)]
    [InlineData(OpdReference.Infinity)]
    [InlineData(OpdReference.Absolute)]
    public void AParaboloidOnAxis_HasZeroOpd(OpdReference reference)
    {
        // RealRayTraceTests derives that a paraboloid (k = −1, R < 0 facing +z) sends every ray
        // parallel to the axis through its focus at R/2 with equal path. The stop is the mirror,
        // so the exit pupil is the mirror's vertex, a focal length from the image point.
        double r = -0.2, f = -r / 2;
        var lens = Lens(double.PositiveInfinity, S(r, r / 2, k: -1, stop: true, mirror: true));
        var system = Sys(0.08);
        var pupil = OpdEngine.PupilNodes(21);
        var result = OpdEngine.Compute(lens, system, 0, 0, pupil, new OpdOptions { Reference = reference });

        double wave = result.AirMicrometers * 1e-6;
        // Each OPD is a few roundings of lengths no larger than f; 64 ulps of f bounds it.
        double tolerance = 64 * Eps * f / wave;
        Assert.Equal(pupil.Count, result.Samples.Count);
        foreach (var s in result.Samples)
        {
            Assert.True(s.IsValid, s.Failure);
            Close(0, s.Waves, tolerance, $"OPD at ({s.Px}, {s.Py})");
        }
        if (reference == OpdReference.ExitPupil)
        {
            Assert.Equal(OpdReferenceShape.Sphere, result.Geometry.Shape);
            Close(f, result.Geometry.Radius, 8 * Eps * f, "reference sphere radius (mirror vertex to focus)");
            Assert.Equal(-1, result.Geometry.Sheet);
        }

        // The centroid of a symmetric pupil on axis is the chief ray's point: the same OPD.
        var centroid = OpdEngine.Compute(lens, system, 0, 0, pupil, new OpdOptions { Reference = reference, ReferencePoint = OpdReferencePoint.Centroid });
        foreach (var s in centroid.Samples) Close(0, s.Waves, tolerance, $"centroid-referenced OPD at ({s.Px}, {s.Py})");
    }

    // ---- A defocused perfect lens: the gap between two spheres ----

    [Theory]
    [InlineData(-0.5e-3)]   // image plane 0.5 mm beyond the focus
    [InlineData(0.5e-3)]    // 0.5 mm inside it
    [InlineData(-3e-3)]
    public void ADefocusedParaboloid_HasTheClosedFormOpdOfTwoSpheres(double defocus)
    {
        // Derivation. The paraboloid of vertex radius R (< 0) sends the ray at height h to the
        // mirror point M = (0, h, h²/(2R)) and on through the focus F = (0, 0, R/2), every ray
        // with the same path P0 to F (RealRayTraceTests). The image plane is moved to
        // z_I = R/2 + Δ. The chief ray meets it at C = (0, 0, z_I); the stop is the mirror, so
        // the exit pupil E is the vertex and the reference sphere has centre C, radius ρ = |z_I|,
        // on the mirror's side. A ray with unit direction d = (F − M)/|F − M| is the line F + t·d,
        // whose path to the point F + t·d is P0 + t (index 1). It meets the sphere where
        // |g + t·d|² = ρ², g = F − C = (0, 0, −Δ): t = −g·d − √((g·d)² − |g|² + ρ²) (the root on
        // the mirror's side). The chief ray (d = −z) gives t_c = −Δ − ρ, the point E. OPD = chief
        // path − ray path = t_c − t_r: the distance between
        // the wavefront (the sphere about F) and the reference sphere (about C) along the ray.
        double r = -0.2, epd = 0.04;
        var lens = Lens(double.PositiveInfinity, S(r, r / 2 + defocus, k: -1, stop: true, mirror: true));
        var system = Sys(epd);
        var pupil = Enumerable.Range(0, 21).Select(i => (0.0, -1 + i / 10.0)).Concat(new[] { (0.6, 0.6), (-0.3, 0.8) }).ToArray();
        var result = OpdEngine.Compute(lens, system, 0, 0, pupil);

        double zi = r / 2 + defocus, rho = Math.Abs(zi);
        var focus = new Vector3D(0, 0, r / 2);
        var g = new Vector3D(0, 0, -defocus);
        double T(Vector3D d)
        {
            double gd = Vector3D.Dot(g, d);
            return -gd - Math.Sqrt(gd * gd - g.LengthSquared + rho * rho);
        }
        double tc = T(new Vector3D(0, 0, -1));
        double wave = result.AirMicrometers * 1e-6;
        Close(rho, result.Geometry.Radius, 8 * Eps * rho, "reference radius");
        double largest = 0;
        foreach (var s in result.Samples)
        {
            Assert.True(s.IsValid, s.Failure);
            double hx = s.Px * epd / 2, hy = s.Py * epd / 2;
            var mirror = new Vector3D(hx, hy, (hx * hx + hy * hy) / (2 * r));
            var d = (focus - mirror).Normalized();
            double expected = (tc - T(d)) / wave;
            largest = Math.Max(largest, Math.Abs(expected));
            // The traced and closed-form directions agree to rounding (~1e-16); the OPD is a
            // difference of lengths ~0.1 m, so 1e-14 m (≈2e-8 waves) bounds both.
            Close(expected, s.Waves, 1e-14 / wave, $"OPD at ({s.Px}, {s.Py})");
        }
        Assert.True(largest > 1, $"the defocus should give more than a wave of OPD (largest {largest:g4}).");
    }

    // ---- The reference sphere at infinity ----

    private static SequentialLens Singlet() => Lens(double.PositiveInfinity,
        S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064));   // near its paraxial focus (BFL ≈ 64.2 mm)

    [Fact]
    public void TheReferenceSphere_TendsToThePlaneReference_AsItsRadiusGrows()
    {
        // OpdEngine's sphere correction differs from the plane-per-ray one by
        // n·(p_c − p_r)/(√(R² + p_c) + √(R² + p_r)), p = −|w⊥|² (the class comment). With the
        // chief ray as reference p_c = 0, so the gap is at most n·|w_r|²/(R + √(R² − |w_r|²)) ≤
        // n·|w_r|²/R, |w_r| the ray's distance from the chief ray's image point.
        var lens = Singlet();
        var system = Sys(0.02, 0, 10);
        var pupil = OpdEngine.PupilNodes(9);
        var plane = OpdEngine.Compute(lens, system, 1, 0, pupil, new OpdOptions { ReferenceRadius = double.PositiveInfinity });
        var infinity = OpdEngine.Compute(lens, system, 1, 0, pupil, new OpdOptions { Reference = OpdReference.Infinity });
        Assert.Equal(OpdReferenceShape.PlaneNormalToEachRay, plane.Geometry.Shape);
        double wave = plane.AirMicrometers * 1e-6;
        for (int i = 0; i < pupil.Count; i++)
            Assert.Equal(infinity.Samples[i].Waves, plane.Samples[i].Waves);

        double previous = double.NaN;
        foreach (double radius in new[] { 0.1, 1, 10, 100, 1e4, 1e8 })
        {
            var sphere = OpdEngine.Compute(lens, system, 1, 0, pupil, new OpdOptions { ReferenceRadius = radius });
            double gap = 0;
            for (int i = 0; i < pupil.Count; i++)
            {
                var s = sphere.Samples[i];
                Assert.True(s.IsValid, s.Failure);
                double w2 = (s.Ray.Position - sphere.ChiefRay.Position).LengthSquared;
                double bound = sphere.ImageIndex * w2 / radius / wave + 1e-14 / wave;
                double difference = Math.Abs(s.Waves - plane.Samples[i].Waves);
                Assert.True(difference <= bound, $"R = {radius}: |sphere − plane| = {difference:g4} waves exceeds the bound {bound:g4}.");
                gap = Math.Max(gap, difference);
            }
            // The gap falls as 1/R until rounding (1e-8 waves) takes over.
            if (double.IsFinite(previous) && previous > 1e-6)
                Assert.True(gap < previous / 5, $"R = {radius}: the gap {gap:g4} did not fall from {previous:g4}.");
            previous = gap;
        }
        Assert.True(previous < 1e-7, $"at R = 1e8 m the sphere and plane still differ by {previous:g4} waves.");
    }

    /// <summary>A paraboloid with the stop in its front focal plane: chief rays leave parallel to
    /// the axis, so the exit pupil is at infinity (telecentric image space). The stop moved by ε
    /// puts the exit pupil at a finite distance ~f²/ε, on either side.</summary>
    private static SequentialLens Telecentric(double stopShift) =>
        Lens(double.PositiveInfinity, S(double.PositiveInfinity, 0.1 + stopShift, stop: true), S(-0.2, -0.1, k: -1, mirror: true));

    [Fact]
    public void ATelecentricLens_UsesThePlaneLimit_AndNearbyLensesApproachItContinuously()
    {
        var system = Sys(0.02, 0, 3);
        var pupil = OpdEngine.PupilNodes(7);
        var telecentric = Telecentric(0);
        Assert.True(new RayLauncher(telecentric, system).Paraxial.ExitPupilAtInfinity, "precondition: the exit pupil is at infinity");
        var limit = OpdEngine.Compute(telecentric, system, 1, 0, pupil);
        Assert.Equal(OpdReferenceShape.PlaneNormalToEachRay, limit.Geometry.Shape);
        Assert.True(limit.Samples.Max(s => Math.Abs(s.Waves)) > 1e-3, "the off-axis field should have some aberration");
        // The plane limit is the Infinity reference's definition.
        var infinity = OpdEngine.Compute(telecentric, system, 1, 0, pupil, new OpdOptions { Reference = OpdReference.Infinity });
        for (int i = 0; i < pupil.Count; i++) Assert.Equal(infinity.Samples[i].Waves, limit.Samples[i].Waves);

        foreach (int side in new[] { 1, -1 })
        {
            double previous = double.NaN;
            int sheet = 0;
            foreach (double eps in new[] { 1e-3, 1e-4, 1e-5, 1e-6 })
            {
                var near = OpdEngine.Compute(Telecentric(side * eps), system, 1, 0, pupil);
                Assert.Equal(OpdReferenceShape.Sphere, near.Geometry.Shape);
                if (sheet == 0) sheet = near.Geometry.Sheet;
                Assert.Equal(sheet, near.Geometry.Sheet);
                double gap = pupil.Select((_, i) => Math.Abs(near.Samples[i].Waves - limit.Samples[i].Waves)).Max();
                // Both the beam's footprint and the sphere move by O(ε), so the gap is O(ε).
                if (double.IsFinite(previous) && previous > 1e-7)
                    Assert.True(gap < previous / 5, $"stop shift {side * eps}: the gap {gap:g4} waves did not fall from {previous:g4}.");
                previous = gap;
            }
            Assert.True(previous < 1e-3, $"stop shift {side * 1e-6}: still {previous:g4} waves from the telecentric limit.");
        }
        // The exit pupil jumps from one side of the image to the other as the stop crosses the
        // focal plane, and the sphere's sheet with it; the OPD does not jump.
        Assert.NotEqual(OpdEngine.Compute(Telecentric(1e-4), system, 1, 0, pupil).Geometry.Sheet,
            OpdEngine.Compute(Telecentric(-1e-4), system, 1, 0, pupil).Geometry.Sheet);
    }
}
