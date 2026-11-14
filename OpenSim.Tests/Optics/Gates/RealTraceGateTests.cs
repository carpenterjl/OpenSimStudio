using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-101 additions to the OP-20 and OP-18 gates for the real ray trace. Existing gates
/// (RealRayTraceTests) pin the conics; these add the two closed forms that exercise a SPHERE,
/// the surface every lens has: the aplanatic points of a refracting sphere (a stigmatic pair
/// for a sphere, so the trace must be exact) and the exact spherical aberration of a concave
/// spherical mirror (a known bias with a sign, held as a one-sided band as well as exactly).
/// And the real-trace half of OP-18's gate that ray aiming does not need: with aiming off the
/// launcher aims at the paraxial pupil, so equivalent field and aperture types must launch the
/// same real rays. Every expected value is derived in the comments from the test's own inputs;
/// radii, thicknesses and indices are arbitrary inputs, not reference data. Spencer and Murty,
/// Welford and Born and Wolf are not on hand; nothing is copied from them.
/// </summary>
public sealed class RealTraceGateTests
{
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;   // 2^-52

    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false, bool mirror = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror,
    };

    private static SequentialLens LensIn(double objectIndex, double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance, Medium = ParaxialMedium.Constant(objectIndex) } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    private static OpticalSystemData Sys(Aperture? aperture = null, FieldType type = FieldType.Angle, params FieldPoint[] fields) => new()
    {
        Aperture = aperture ?? new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.01 },
        Fields = new FieldSet { Type = type, Points = fields.Length == 0 ? new[] { new FieldPoint() } : fields },
    };

    private static void Close(double expected, double actual, double tolerance, string what)
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    // ---- OP-20: a refracting sphere's aplanatic points ----

    /// <summary>
    /// Derivation (the Weierstrass construction, worked here): a sphere of radius ρ and index n
    /// in air, centre C. A point P inside at |CP| = ρ/n and a point P′ at |CP′| = nρ on the same
    /// side of C, on the axis. For any point Q on the sphere the triangles CPQ and CQP′ share the
    /// angle at C and have |CP|/|CQ| = |CQ|/|CP′| = 1/n, so they are similar (C↔C, P↔Q, Q↔P′):
    /// |QP′| = n·|PQ| and ∠CQP′ = ∠CPQ. The angle of incidence at Q is I = ∠CQP, and by the sine
    /// rule in CPQ, sin I = |CP|·sin∠CPQ/|CQ| = sin∠CPQ / n. A refracted ray along P′Q (away from
    /// P′) makes the angle I′ = ∠CQP′ = ∠CPQ with the normal, so sin I′ = n·sin I, which is
    /// Snell's law into air, in the plane of incidence. So every ray from P leaves the sphere as
    /// if from the virtual point P′: a perfect, if virtual, image.
    /// The optical path to P′ along the ray is n·|PQ| − |QP′| = 0 for every ray.
    /// Layout: light travels +z; P is on the axis a distance ρ + ρ/n before the vertex (object
    /// space of index n), the surface's centre is toward −z (R = −ρ, Conventions §3), and the
    /// image surface sits at P′, a distance ρ + nρ before the vertex (thickness −ρ(1 + n), a
    /// virtual step back). No total internal reflection: n sin I = sin(angle at P) &lt; 1.
    /// </summary>
    [Fact]
    public void ARefractingSphere_ImagesItsAplanaticPoint_Perfectly()
    {
        foreach (double n in new[] { 1.5, 1.8, 2.4 })
        {
            double rho = 0.02;
            double d1 = rho + rho / n, d2 = rho + n * rho;
            var lens = LensIn(n, d1, S(-rho, -d2, n: 1, stop: true));
            var trace = SequentialTrace.Create(lens, Sys());
            // Every coordinate is a handful of roundings of lengths no larger than d2.
            double tolerance = 64 * Eps * d2;
            int traced = 0;
            for (int ring = 0; ring <= 8; ring++)
            {
                double a = 40.0 * ring / 8 * Math.PI / 180;
                for (int m = 0; m < (ring == 0 ? 1 : 8); m++)
                {
                    double b = m * Math.PI / 4 + 0.1;
                    var d = new Vector3D(Math.Sin(a) * Math.Cos(b), Math.Sin(a) * Math.Sin(b), Math.Cos(a));
                    var ray = trace.Trace(trace.NewRay(new Vector3D(0, 0, -d1), d));
                    Assert.True(ray.IsOk, $"n = {n}: ray failed: {ray.Status} at {ray.FailedAtSurface}");
                    Close(0, ray.Position.X, tolerance, $"n = {n}, x at P′");
                    Close(0, ray.Position.Y, tolerance, $"n = {n}, y at P′");
                    Close(0, ray.OpticalPath, tolerance, $"n = {n}, optical path P to P′");
                    traced++;
                }
            }
            Assert.Equal(65, traced);   // 1 + 8 rings · 8 azimuths

            // The aplanatic pair is also a paraxial conjugate pair: the paraxial image of P is at P′.
            var paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), Sys());
            // A ray from P of slope u is at height u·d1 on the vertex plane, reduced slope n·u.
            double u = 1e-3;
            var marginal = paraxial.Trace(u * d1, n * u);
            Close(0, marginal.Heights[lens.ImageIndex], 1e-15, $"n = {n}, paraxial height at P′");
        }
    }

    // ---- OP-20: a concave sphere's exact spherical aberration (a known bias) ----

    /// <summary>
    /// Derivation: a concave spherical mirror of radius R &lt; 0 (centre C at z = R), a ray parallel
    /// to the axis at height h. It meets the sphere at Q, z_Q = R + √(R² − h²), where the normal
    /// makes the angle θ with the axis, sin θ = h/|R|. The reflected ray makes θ with the normal
    /// on the other side, so the triangle C Q A (A where it crosses the axis) has equal angles at
    /// C and Q and |CA| = |QA| = (|R|/2)/cos θ: z_A = R + |R|/(2 cos θ). The paraxial focus is
    /// z = R/2. On the plane z = R/2 the ray's height is h·(z_A − R/2)/(z_A − z_Q), and its optical
    /// path from the launch plane z = 0 is z_Q (a virtual step) + |Q B|. Since cos θ &lt; 1, z_A lies
    /// between the paraxial focus and the mirror: every zone focuses short (spherical aberration
    /// undercorrected), so on the paraxial focal plane the ray has crossed the axis, y·h &lt; 0
    /// for every h ≠ 0 below 45° of θ. That sign is asserted as a one-sided band, and the height
    /// exactly.
    /// </summary>
    [Fact]
    public void AConcaveSphere_HasItsExactSphericalAberration_AlwaysUndercorrected()
    {
        double r = -0.2;
        var lens = LensIn(1, double.PositiveInfinity, S(r, r / 2, stop: true, mirror: true));
        var trace = SequentialTrace.Create(lens, Sys());
        double tolerance = 64 * Eps * Math.Abs(r);
        for (int i = 1; i <= 24; i++)
        {
            double h = 0.6 * Math.Abs(r) * i / 24;   // θ up to asin 0.6 ≈ 37°
            double cos = Math.Sqrt(r * r - h * h) / Math.Abs(r);
            double zQ = r + Math.Sqrt(r * r - h * h);
            double zA = r + Math.Abs(r) / (2 * cos);
            double yB = h * (zA - r / 2) / (zA - zQ);
            double path = zQ + Math.Sqrt((yB - h) * (yB - h) + (r / 2 - zQ) * (r / 2 - zQ));

            var ray = trace.Trace(trace.NewRay(new Vector3D(0, h, 0), Vector3D.UnitZ));
            Assert.True(ray.IsOk, $"h = {h}: {ray.Status}");
            Close(yB, ray.Position.Y, tolerance, $"h = {h:R}, height on the paraxial focal plane");
            Close(0, ray.Position.X, tolerance, $"h = {h:R}, x");
            Close(path, ray.OpticalPath, tolerance, $"h = {h:R}, optical path");
            Assert.True(ray.Position.Y * h < 0, $"h = {h:R}: y = {ray.Position.Y:R} has not crossed the axis (undercorrected sphere expected).");
            // The aberration grows with the zone: |y| increases with h.
            if (i > 1)
            {
                double hPrev = 0.6 * Math.Abs(r) * (i - 1) / 24;
                var prev = trace.Trace(trace.NewRay(new Vector3D(0, hPrev, 0), Vector3D.UnitZ));
                Assert.True(Math.Abs(ray.Position.Y) > Math.Abs(prev.Position.Y), $"|y| not increasing at h = {h:R}");
            }
        }
    }

    // ---- OP-18, the real-trace half: equivalent field and aperture types launch the same rays ----

    /// <summary>A singlet with the stop on its first surface, object at 0.3 m.</summary>
    private static SequentialLens StopOnFront(double objectDistance) =>
        LensIn(1, objectDistance, S(0.05, 0.005, 1.5, stop: true), S(-0.05, 0.09));

    /// <summary>
    /// With ray aiming off (OP-22 not built) the launcher aims each chief ray at the paraxial
    /// entrance-pupil centre. With the stop on surface 1 that pupil is the stop itself, so the real
    /// chief ray must meet surface 1 at its vertex, for every field type, and fields that are
    /// equivalent by construction (an angle θ; the object height h = −(z_E + t0)·tan θ that the
    /// same chief ray starts from, derived in ParaxialTests; the paraxial image height that chief
    /// ray reaches) must give the same real ray at the image surface.
    /// </summary>
    [Fact]
    public void EquivalentFieldTypes_LaunchTheSameRealChiefRay_ThroughTheStopCentre()
    {
        double t0 = 0.3, angle = 5;
        var lens = StopOnFront(t0);
        var byAngle = Sys(null, FieldType.Angle, new FieldPoint(), new FieldPoint { Y = angle });
        var launcherA = new RayLauncher(lens, byAngle);
        double zE = launcherA.Paraxial.EntrancePupilPosition!.Value;
        Close(0, zE, 1e-15, "entrance pupil at the stop on surface 1");
        double h = -(zE + t0) * Math.Tan(angle * Math.PI / 180);
        double hImage = launcherA.Paraxial.ImageSurfaceChiefHeight;
        var systems = new[]
        {
            byAngle,
            Sys(null, FieldType.ObjectHeight, new FieldPoint(), new FieldPoint { Y = h }),
            Sys(null, FieldType.ParaxialImageHeight, new FieldPoint(), new FieldPoint { Y = hImage }),
        };

        Ray? reference = null;
        foreach (var system in systems)
        {
            var trace = SequentialTrace.Create(lens, system);
            var launcher = new RayLauncher(lens, system);
            // The field point's own normalised coordinates: hy = +1 is +|largest y| (§7), which
            // for the object-height field is the opposite point.
            var (hx, hy) = system.Fields.Normalized(1);
            var path = trace.TracePath(launcher.Launch(trace, hx, hy, 0, 0));
            Assert.True(path.Final.IsOk, $"{system.Fields.Type}: {path.Status}");
            var stopHit = path.Hits.Single(x => x.Surface == 1);
            // The chief ray starts |h| ≈ 26 mm off axis and is aimed at the vertex; the hit is
            // the vertex to a few roundings of that start height.
            Close(0, stopHit.Position.Y, 16 * Eps * Math.Abs(h), $"{system.Fields.Type}: chief ray at the stop");
            Close(0, stopHit.Position.X, 16 * Eps * Math.Abs(h), $"{system.Fields.Type}: chief ray x at the stop");
            if (reference is null) { reference = path.Final; continue; }
            // The field values agree to the paraxial engine's 1e-14 (ParaxialTests); the real
            // trace carries that relative difference through a system of unit-order magnification.
            double scale = Math.Abs(h);
            Close(reference.Value.Position.Y, path.Final.Position.Y, 1e-13 * scale, $"{system.Fields.Type}: chief ray at the image");
            Close(reference.Value.Direction.Y, path.Final.Direction.Y, 1e-13, $"{system.Fields.Type}: chief direction at the image");
            Close(reference.Value.OpticalPath, path.Final.OpticalPath, 1e-13 * reference.Value.OpticalPath, $"{system.Fields.Type}: chief optical path");
        }
    }

    /// <summary>
    /// Aperture types set to the values the paraxial engine reports for an entrance-pupil
    /// diameter (image-space F/# = |f|/EPD, the paraxial working F/#) give the same pupil, so the
    /// real marginal and rim rays they launch must agree with the EPD system's. The values come
    /// from the reference system itself, not from any table.
    /// </summary>
    [Fact]
    public void EquivalentApertureTypes_LaunchTheSameRealMarginalRays()
    {
        var lens = StopOnFront(double.PositiveInfinity);
        var reference = Sys(new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.008 }, FieldType.Angle,
            new FieldPoint(), new FieldPoint { Y = 3 });
        var p = ParaxialSystem.Create(lens.ToParaxialLens(), reference);
        var alternatives = new[]
        {
            reference with { Aperture = new Aperture { Type = ApertureType.ImageSpaceFNumber, Value = p.ImageSpaceFNumber!.Value } },
            reference with { Aperture = new Aperture { Type = ApertureType.ParaxialWorkingFNumber, Value = p.WorkingFNumber!.Value } },
        };
        var pupil = new[] { (0.0, 1.0), (1.0, 0.0), (0.0, -1.0), (0.6, 0.6) };
        var expected = BatchTracer.Trace(lens, reference, new BatchTraceRequest { Pupil = pupil });
        foreach (var system in alternatives)
        {
            var actual = BatchTracer.Trace(lens, system, new BatchTraceRequest { Pupil = pupil });
            for (int i = 0; i < expected.Rays.Count; i++)
            {
                var a = expected.Rays[i].Ray;
                var b = actual.Rays[i].Ray;
                Assert.True(a.IsOk && b.IsOk);
                // The pupil diameters agree to 1e-13 relative (ParaxialTests); ray heights here are
                // below 20 mm, so the rays agree to 1e-13 of that.
                Close(a.Position.X, b.Position.X, 1e-13 * 0.02, $"{system.Aperture.Type} ray {i} x");
                Close(a.Position.Y, b.Position.Y, 1e-13 * 0.02, $"{system.Aperture.Type} ray {i} y");
                Close(a.Direction.Y, b.Direction.Y, 1e-13, $"{system.Aperture.Type} ray {i} direction");
            }
        }
    }
}
