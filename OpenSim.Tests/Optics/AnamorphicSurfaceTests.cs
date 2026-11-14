using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Anamorphic;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-24: Toroidal, Biconic, Biconic Zernike and Irregular surfaces (docs/Optics-Conventions.md
/// §23). Gate (plan): a biconic with equal x and y terms equals the Standard surface ray for
/// ray; a cylinder lens focuses to a line at the paraxial focal distance (derived below); an
/// Irregular surface with zero terms equals Standard. Also normals against finite differences of
/// the sag, the saved layouts, and the paraxial model. Every expected value is derived in the
/// test from its inputs; the prescriptions and coefficients are arbitrary test inputs.
/// </summary>
public sealed class AnamorphicSurfaceTests
{
    private const double R1 = 0.05, K1 = -0.6;

    private static SequentialSurface Front(SurfaceShape? shape, double k = K1, double c = 1 / R1, double? semiDiameter = null) => new()
    {
        Curvature = c, Conic = k, Thickness = 0.006, Medium = ParaxialMedium.Constant(1.5168),
        IsStop = true, Shape = shape, SemiDiameter = semiDiameter,
    };

    private static SequentialLens Lens(SequentialSurface front) => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            front,
            new SequentialSurface { Curvature = -1 / 0.08, Thickness = 0.09 },
            new SequentialSurface(),
        },
    };

    private static OpticalSystemData Sys() => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.02 },
        Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint(), new FieldPoint { Y = 5 }, new FieldPoint { X = -3, Y = 2 } } },
    };

    private static void Close(double expected, double actual, double tolerance, string what = "")
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    private static double[] Zeros(int n) => new double[n];

    // ---- Gate: forms set to the Standard surface trace as it, ray for ray ----

    public static IEnumerable<object[]> StandardForms() => new[]
    {
        new object[] { "Biconic, Rx = Ry, kx = ky", K1 },
        new object[] { "Biconic Zernike, Rx = Ry, kx = ky, zero terms", K1 },
        new object[] { "Irregular, zero terms", K1 },
        new object[] { "Irregular, zero terms, fixed semi-diameter", K1 },
        new object[] { "Toroidal sphere, R = Ry", 0.0 },
    };

    private static SequentialSurface StandardForm(string name, double k) => name switch
    {
        "Biconic, Rx = Ry, kx = ky" => Front(new BiconicSurface(R1, K1), k),
        "Biconic Zernike, Rx = Ry, kx = ky, zero terms" =>
            Front(new BiconicZernikeSurface(R1, K1, Zeros(16), Zeros(16), Zeros(10), 0.011, extrapolate: true), k),
        "Irregular, zero terms" => Front(new IrregularSurface(0, 0, 0, 0, 0, 0, 0, 0), k),
        "Irregular, zero terms, fixed semi-diameter" => Front(new IrregularSurface(0, 0, 0, 0, 0, 0, 0, 25), k, semiDiameter: 0.012),
        // The y–z circle of radius R1 turned about the axis through its own centre (z = R1) is the
        // sphere of radius R1 (derived from the help's construction).
        "Toroidal sphere, R = Ry" => Front(new ToroidalSurface(R1, Zeros(7)), k),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(StandardForms))]
    public void FormSetToTheStandardSurface_TracesAsIt(string name, double k)
    {
        var system = Sys();
        var request = new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(11) };
        var standard = BatchTracer.Trace(Lens(Front(null, k, semiDiameter: name.Contains("fixed") ? 0.012 : null)), system, request);
        var shaped = BatchTracer.Trace(Lens(StandardForm(name, k)), system, request);
        Assert.Equal(standard.Rays.Count, shaped.Rays.Count);
        int ok = 0;
        for (int i = 0; i < standard.Rays.Count; i++)
        {
            var a = standard.Rays[i].Ray;
            var b = shaped.Rays[i].Ray;
            Assert.Equal(a.Status, b.Status);
            if (!a.IsOk) continue;
            ok++;
            // The same root found by the same Newton iteration from the conic's own start; the
            // sag formulas differ only in rounding (AsphereTests uses the same bound).
            const double tol = 1e-12;
            Close(a.Position.X, b.Position.X, tol, $"{name}: x");
            Close(a.Position.Y, b.Position.Y, tol, $"{name}: y");
            Close(a.Direction.X, b.Direction.X, tol, $"{name}: L");
            Close(a.Direction.Y, b.Direction.Y, tol, $"{name}: M");
            Close(a.Direction.Z, b.Direction.Z, tol, $"{name}: N");
            Close(a.OpticalPath, b.OpticalPath, tol, $"{name}: optical path");
        }
        Assert.True(ok > 200, $"{name}: only {ok} rays traced");
    }

    // ---- Gate: a cylinder lens focuses to a line at the paraxial focal distance ----

    private const double Rc = 0.05, NGlass = 1.5, TGlass = 0.004;

    /// <summary>
    /// Back focal distance of a plano-convex lens, curved face first, in air (derived): the first
    /// surface images infinity at n·Rc/(n − 1) inside the glass; that point is n·Rc/(n − 1) − t
    /// beyond the plane face, which a plane interface from glass to air images at that distance
    /// divided by n. So BFD = Rc/(n − 1) − t/n.
    /// </summary>
    private static double Bfd => Rc / (NGlass - 1) - TGlass / NGlass;

    private static SequentialLens CylinderLens(double curvatureY, double radiusOfRotation) => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            new SequentialSurface
            {
                Curvature = curvatureY, Thickness = TGlass, Medium = ParaxialMedium.Constant(NGlass), IsStop = true,
                Shape = new ToroidalSurface(radiusOfRotation, Array.Empty<double>()),
            },
            new SequentialSurface { Thickness = Bfd },
            new SequentialSurface(),
        },
    };

    private static OpticalSystemData CylinderSys() => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.004 },
        Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint() } },
    };

    /// <summary>Where a collimated ray launched at (x, y) crosses the plane coordinate = 0
    /// (y = 0 when <paramref name="inY"/>, else x = 0), along z from the image plane.</summary>
    private static double Crossing(SequentialTrace trace, double x, double y, bool inY)
    {
        var r = trace.Trace(trace.NewRay(new Vector3D(x, y, 0), Vector3D.UnitZ));
        Assert.True(r.IsOk, $"ray ({x}, {y}): {r.Status}");
        return inY ? -r.Position.Y * r.Direction.Z / r.Direction.Y : -r.Position.X * r.Direction.Z / r.Direction.X;
    }

    [Fact]
    public void CylinderLens_FocusesToALineAtTheParaxialFocalDistance()
    {
        // A Toroidal surface with radius of rotation 0 ("infinite") is a cylinder with power in y
        // only. Rays at heights h and 2h cross the axis at BFD + a·h² + O(h⁴) (spherical
        // aberration is even in h); Richardson's (4·z(h) − z(2h))/3 removes the h² term. With
        // h = 0.1 mm the O(h⁴) residue is of order h⁴/f³ ≈ 1e-13 m and the rounding of a crossing
        // about 1e-17 m over a slope of 1e-3, so 1e-11 m is sharp with margin.
        var lens = CylinderLens(1 / Rc, 0);
        var trace = SequentialTrace.Create(lens, CylinderSys());
        const double h = 1e-4;
        double z1 = Crossing(trace, 0, h, true), z2 = Crossing(trace, 0, 2 * h, true);
        Close(0, (4 * z1 - z2) / 3, 1e-11, "y focus from the image plane at the derived BFD");
        // The aberration really is there (so the extrapolation is not trivially exact).
        Assert.True(Math.Abs(z1) > 1e-9);

        // Off the y section the surface is the same curve, so a ray at x ≠ 0 crosses y = 0 at the
        // same place, and keeps its x: the image of a point at infinity is a line along x.
        Close(z1, Crossing(trace, 0.0015, h, true), 1e-14, "focus independent of x");
        var flat = trace.Trace(trace.NewRay(new Vector3D(0.0015, 0, 0), Vector3D.UnitZ));
        Assert.True(flat.IsOk);
        Assert.Equal(0.0015, flat.Position.X);
        Assert.Equal(0.0, flat.Direction.X);
        Assert.Equal(0.0, flat.Direction.Y);

        // The paraxial engine traces the y section and puts the focus at the same distance.
        var paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), CylinderSys());
        Close(Bfd, paraxial.BackFocalLength!.Value, 1e-15, "paraxial BFL");
    }

    [Fact]
    public void CylinderTurnedAboutY_FocusesXRays_AndTheParaxialEngineSeesNoPower()
    {
        // Ry infinite (c = 0) and R = Rc: the x section is the circle of radius Rc, the y section
        // a straight line, so x rays focus at the same BFD and y rays pass undeviated.
        var lens = CylinderLens(0, Rc);
        var trace = SequentialTrace.Create(lens, CylinderSys());
        const double h = 1e-4;
        double z1 = Crossing(trace, h, 0, false), z2 = Crossing(trace, 2 * h, 0, false);
        Close(0, (4 * z1 - z2) / 3, 1e-11, "x focus at the derived BFD");
        var y = trace.Trace(trace.NewRay(new Vector3D(0, 0.0015, 0), Vector3D.UnitZ));
        Assert.Equal(0.0015, y.Position.Y);
        Assert.Equal(0.0, y.Direction.Y);
        // The rotationally symmetric engine takes the y–z section (§23), which has no power.
        var paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), CylinderSys());
        Assert.Null(paraxial.EffectiveFocalLength);
    }

    // ---- Normals against finite differences of the sag; intersections on the surface ----

    public static IEnumerable<object[]> RealForms() => new[]
    {
        "Toroidal", "ToroidalNegativeR", "Cylinder", "Biconic", "BiconicZernike", "Irregular", "IrregularPlaced",
    }.Select(n => new object[] { n });

    private static SequentialSurface RealForm(string name) => name switch
    {
        "Toroidal" => Front(new ToroidalSurface(0.03, new[] { 0.5, 800 }, new[] { 0, 1e-6, -2e-6, 3e-6, 1e-6, -1e-6, 5e-7, 2e-7 }, 0.012, extrapolate: true)),
        "ToroidalNegativeR" => Front(new ToroidalSurface(-0.04, new[] { 0.2, -300, 1e5 }), k: 0.3),
        "Cylinder" => Front(new ToroidalSurface(0, new[] { 0.5 })),
        "Biconic" => Front(new BiconicSurface(0.04, -2)),
        "BiconicZernike" => Front(new BiconicZernikeSurface(-0.07, 0.3, new[] { 1e-3, 0.5, 30 }, new[] { -2e-3, 0.2, -20, 1e3 },
            new[] { 1e-7, 2e-6, -1e-6, 3e-6, 1e-6, 2e-6, -5e-7, 4e-7, 1e-7, -2e-7, 3e-7 }, 0.012, extrapolate: true)),
        "Irregular" => Front(new IrregularSurface(0, 0, 0, 0, 2e-6, -1e-6, 5e-7, 30), semiDiameter: 0.01),
        "IrregularPlaced" => Front(new IrregularSurface(3e-4, -2e-4, 1.5, -2, 2e-6, -1e-6, 5e-7, 30), semiDiameter: 0.01),
        _ => throw new ArgumentException(name),
    };

    private static readonly (double X, double Y)[] Points =
        { (0.003, -0.002), (-0.006, 0.0045), (0.0071, 0.0012), (0, 0.008), (-0.0005, -0.0002) };

    [Theory]
    [MemberData(nameof(RealForms))]
    public void Normal_EqualsTheFiniteDifferenceSagGradient(string name)
    {
        var surface = RealForm(name);
        var shape = surface.Shape!;
        // Central differences with h = 1 µm: truncation h²·z‴/6 is below 1e-9 for these surfaces
        // (the stiffest terms, Zernike terms of degree up to 4 over a 12 mm radius at a few µm,
        // and the 30/m² x³ term, have z‴ below about 1e3 m⁻²); rounding ε·|z|/h is about 1e-13,
        // and for the placed Irregular surface, whose sag is itself a Newton root converged to a
        // few ulps of 10 mm, about 1e-11.
        const double h = 1e-6, tol = 1e-8;
        foreach (var (x, y) in Points)
        {
            double zx = (shape.Sag(x + h, y, surface) - shape.Sag(x - h, y, surface)) / (2 * h);
            double zy = (shape.Sag(x, y + h, surface) - shape.Sag(x, y - h, surface)) / (2 * h);
            var expected = new Vector3D(-zx, -zy, 1);
            expected /= expected.Length;
            var point = new Vector3D(x, y, shape.Sag(x, y, surface));
            var normal = shape.Normal(point, surface);
            Close(expected.X, normal.X, tol, $"{name} n_x at ({x}, {y})");
            Close(expected.Y, normal.Y, tol, $"{name} n_y at ({x}, {y})");
            Close(expected.Z, normal.Z, tol, $"{name} n_z at ({x}, {y})");
        }
    }

    [Theory]
    [MemberData(nameof(RealForms))]
    public void Intersection_LiesOnTheSurfaceAndOnTheRay(string name)
    {
        var surface = RealForm(name);
        var shape = surface.Shape!;
        foreach (var (x, y) in Points)
            foreach (var d in new[] { new Vector3D(0, 0, 1), new Vector3D(0.2, -0.1, 1), new Vector3D(-0.35, 0.25, 1), new Vector3D(0.1, 0.3, -1) })
            {
                var u = d / d.Length;
                Assert.True(shape.Intersect(x, y, u, surface, out var p, out double s), $"{name}: ({x}, {y}) {u} missed");
                Close(x + s * u.X, p.X, 1e-18, $"{name} on ray x");
                Close(y + s * u.Y, p.Y, 1e-18, $"{name} on ray y");
                // Newton stops at a few ulps of the 10 mm coordinates (AsphereTests' bound).
                Close(shape.Sag(p.X, p.Y, surface), p.Z, 1e-14, $"{name} on surface");
            }
    }

    [Theory]
    [MemberData(nameof(RealForms))]
    public void LensWithTheForm_TracesEveryRay(string name)
    {
        // Traced directly (not through the launcher): several of these forms have no paraxial
        // model by design (§23), and the launcher needs one.
        var trace = SequentialTrace.Create(Lens(RealForm(name)), Sys());
        int count = 0;
        foreach (double angle in new[] { 0.0, 0.05 })
            for (double x = -0.008; x <= 0.0081; x += 0.002)
                for (double y = -0.008; y <= 0.0081; y += 0.002)
                {
                    if (x * x + y * y > 0.009 * 0.009) continue;
                    var r = trace.Trace(trace.NewRay(new Vector3D(x, y, 0), new Vector3D(0, Math.Sin(angle), Math.Cos(angle))));
                    Assert.True(r.IsOk, $"{name}: ({x}, {y}) at {angle}: {r.Status} at {r.FailedAtSurface}");
                    count++;
                }
        Assert.True(count > 100);
    }

    // ---- The sag formulas against the help's construction ----

    [Fact]
    public void Toroidal_IsTheYCurveTurnedAboutTheAxisAtDistanceR()
    {
        // Help, "Toroidal": the point of the y–z curve at height y is R − z_y from the axis of
        // rotation (z = R, parallel to y); turning it keeps that distance, so
        // (R − z)² + x² = (R − z_y)², z = R − sign(R)·√((R − z_y)² − x²) on the vertex sheet.
        double c = 20, k = -0.4;
        var alpha = new[] { 0.3, -200.0 };
        foreach (double radius in new[] { 0.03, -0.045 })
        {
            var surface = new SequentialSurface { Curvature = c, Conic = k, Shape = new ToroidalSurface(radius, alpha) };
            foreach (var (x, y) in Points)
            {
                double zy = c * y * y / (1 + Math.Sqrt(1 - (1 + k) * c * c * y * y)) + alpha[0] * y * y + alpha[1] * Math.Pow(y, 4);
                double expected = radius - Math.Sign(radius) * Math.Sqrt((radius - zy) * (radius - zy) - x * x);
                // The direct form loses digits to cancellation (R ≈ 0.03 m against z ≈ 1e-3 m).
                Close(expected, surface.Shape!.Sag(x, y, surface), 1e-16, $"R = {radius}, ({x}, {y})");
            }
        }
    }

    [Fact]
    public void Biconic_SectionsAreTheXAndYConics()
    {
        // Help, "Biconic": on y = 0 the sag is cx·x²/(1 + √(1 − (1 + kx)cx²x²)), the conic of
        // cx, kx; on x = 0 that of cy, ky. Rx = 0 is an infinite radius (a flat x section).
        var surface = new SequentialSurface { Curvature = 1 / 0.06, Conic = 0.4, Shape = new BiconicSurface(-0.03, -1.5) };
        foreach (double t in new[] { -0.008, 0.002, 0.007 })
        {
            Assert.True(ConicSag.TryEvaluate(t, 1 / -0.03, -1.5, out double zx, out _));
            Assert.True(ConicSag.TryEvaluate(t, 1 / 0.06, 0.4, out double zy, out _));
            Close(zx, surface.Shape!.Sag(t, 0, surface), 1e-17, "x section");
            Close(zy, surface.Shape!.Sag(0, t, surface), 1e-17, "y section");
        }
        var flatX = surface with { Shape = new BiconicSurface(0, 5) };
        Assert.Equal(0.0, flatX.Shape!.Sag(0.006, 0, flatX));
    }

    [Fact]
    public void ZernikeTerms_WithoutExtrapolation_StopAtTheNormalizationRadius()
    {
        // Help, "Toroidal": "The extrapolate flag may be set to zero to ignore the Zernike terms
        // for rays that land outside the normalization radius."
        var zernike = new[] { 1e-6, 2e-6, -1e-6, 3e-6 };
        var off = new SequentialSurface { Curvature = 15, Shape = new ToroidalSurface(0.05, new[] { 0.1 }, zernike, 0.005, extrapolate: false) };
        var on = off with { Shape = new ToroidalSurface(0.05, new[] { 0.1 }, zernike, 0.005, extrapolate: true) };
        var bare = off with { Shape = new ToroidalSurface(0.05, new[] { 0.1 }) };
        Assert.Equal(bare.Shape!.Sag(0.004, 0.004, bare), off.Shape!.Sag(0.004, 0.004, off));    // ρ = 1.13
        Assert.NotEqual(bare.Shape!.Sag(0.004, 0.004, bare), on.Shape!.Sag(0.004, 0.004, on));
        Assert.Equal(on.Shape!.Sag(0.002, -0.003, on), off.Shape!.Sag(0.002, -0.003, off));     // ρ = 0.72
        Assert.NotEqual(bare.Shape!.Sag(0.002, -0.003, bare), off.Shape!.Sag(0.002, -0.003, off));
    }

    [Fact]
    public void Irregular_AddsItsTermsAtTheClearSemiDiameter()
    {
        // Help, "Irregular": Zs·ρ⁴ + Za·ρ′y² + Zc·ρ²·ρ′y with ρ = r/r_max and
        // ρ′y = ρy·cos θ − ρx·sin θ. At the rim point along θ from the y axis (ρx = −sin θ,
        // ρy = cos θ, so ρ′y = 1 and ρ = 1) the terms add Zs + Za + Zc to the plane's sag.
        double rmax = 0.01, theta = 30;
        var surface = new SequentialSurface { SemiDiameter = rmax, Shape = new IrregularSurface(0, 0, 0, 0, 2e-6, -1e-6, 5e-7, theta) };
        var (s, c) = OpticsConventions.SinCosDegrees(theta);
        Close(2e-6 - 1e-6 + 5e-7, surface.Shape!.Sag(-s * rmax, c * rmax, surface), 4e-21, "rim along θ");
        // A decenter moves the whole surface, base sphere included: the decentered sphere's sag
        // at (dx, dy) + p is the centred sphere's at p.
        var sphere = new SequentialSurface { Curvature = 30, SemiDiameter = rmax, Shape = new IrregularSurface(0, 0, 0, 0, 2e-6, 0, 0, 0) };
        var moved = sphere with { Shape = new IrregularSurface(0.001, -0.0005, 0, 0, 2e-6, 0, 0, 0) };
        Close(sphere.Shape!.Sag(0.002, 0.003, sphere), moved.Shape!.Sag(0.003, 0.0025, moved), 1e-16, "decentered");
    }

    [Fact]
    public void Irregular_WithTermsButAnAutomaticSemiDiameter_FailsByName()
    {
        var surface = new SequentialSurface { Curvature = 10, Shape = new IrregularSurface(0, 0, 0, 0, 0, 1e-6, 0, 0) };
        var ex = Assert.Throws<InvalidOperationException>(() => surface.Shape!.Sag(0.001, 0.001, surface));
        Assert.Contains("clear semi-diameter", ex.Message);
        Assert.Contains("automatic", ex.Message);
        // Without terms r_max is not needed.
        var bare = surface with { Shape = new IrregularSurface(0, 0, 0, 0, 0, 0, 0, 0) };
        Assert.True(double.IsFinite(bare.Shape!.Sag(0.001, 0.001, bare)));
    }

    // ---- The paraxial model (§23): the y–z vertex curvature, or a refusal by name ----

    [Fact]
    public void ParaxialEquivalent_IsTheYZVertexCurvature()
    {
        var on = (SurfaceShape shape, double? sd) => new SequentialSurface { Curvature = 20, Shape = shape, SemiDiameter = sd };
        // Toroidal: z(0, y) = z_y = (c/2 + α1)y² + O(y⁴), so c + 2α1.
        var t = on(new ToroidalSurface(0.03, new[] { 0.5, 100 }), null);
        Assert.Equal(21.0, t.Shape!.ParaxialEquivalent(t).Curvature);
        // Biconic: cy, whatever Rx.
        var b = on(new BiconicSurface(0.01, 3), null);
        Assert.Equal(20.0, b.Shape!.ParaxialEquivalent(b).Curvature);
        // Biconic Zernike: cy + 2β2; α2 (x curvature) is not passed on.
        var bz = on(new BiconicZernikeSurface(0.01, 0, new[] { 0, 7.0 }, new[] { 0, 3.0, 50 }), null);
        Assert.Equal(26.0, bz.Shape!.ParaxialEquivalent(bz).Curvature);
        // Irregular, θ = 0: Za·(y/r_max)² adds 2Za/r_max²; θ = 90°: the astigmatism is along x.
        var ir = on(new IrregularSurface(0, 0, 0, 0, 5e-6, 2e-6, 1e-6, 0), 0.01);
        Close(20 + 2 * 2e-6 / (0.01 * 0.01), ir.Shape!.ParaxialEquivalent(ir).Curvature, 1e-14, "Irregular θ = 0");
        var ir90 = on(new IrregularSurface(0, 0, 0, 0, 5e-6, 2e-6, 1e-6, 90), 0.01);
        Assert.Equal(20.0, ir90.Shape!.ParaxialEquivalent(ir90).Curvature);
        // A Zernike cos 2θ term (n = 2, m = 2) is N·ρ²·cos 2θ = N·(x² − y²)/R_n², N its
        // normalisation: it adds −2N·A/R_n² in y.
        int astig = ZernikeIndex.ToNoll(new ZernikeTerm(2, 2));
        var a = new double[astig];
        a[^1] = 1e-6;
        var tz = on(new ToroidalSurface(0, Array.Empty<double>(), a, 0.01), null);
        double n22 = ZernikeIndex.Normalization(ZernikeOrdering.Standard, new ZernikeTerm(2, 2));
        Close(20 - 2 * n22 * 1e-6 / (0.01 * 0.01), tz.Shape!.ParaxialEquivalent(tz).Curvature, 1e-13, "Zernike astigmatism");
    }

    [Fact]
    public void ParaxialEquivalent_RefusesAVertexOffTheAxisTiltedOrTwisted()
    {
        SequentialSurface On(SurfaceShape shape, double? sd = null) => new() { Curvature = 20, Shape = shape, SemiDiameter = sd };
        double[] Only(int n, int m)
        {
            var a = new double[ZernikeIndex.ToNoll(new ZernikeTerm(n, m))];
            a[^1] = 1e-6;
            return a;
        }
        void Refused(SequentialSurface s, string text) =>
            Assert.Contains(text, Assert.Throws<ArgumentException>(() => s.Shape!.ParaxialEquivalent(s)).Message);

        Refused(On(new ToroidalSurface(0, Array.Empty<double>(), Only(2, 0), 0.01)), "sag at the vertex");   // defocus: R(0) ≠ 0
        Refused(On(new ToroidalSurface(0, Array.Empty<double>(), Only(1, -1), 0.01)), "slope");              // y tilt
        Refused(On(new ToroidalSurface(0, Array.Empty<double>(), Only(2, -2), 0.01)), "∂²z/∂x∂y");          // 45° astigmatism
        Refused(On(new BiconicZernikeSurface(0, 0, new[] { 1e-3 }, Array.Empty<double>())), "slope");      // α1·x
        Refused(On(new IrregularSurface(0, 0, 0, 0, 0, 2e-6, 0, 30), 0.01), "∂²z/∂x∂y");
        Refused(On(new IrregularSurface(1e-4, 0, 0, 0, 0, 0, 0, 0)), "decentered");
        Refused(On(new IrregularSurface(0, 0, 0.5, 0, 0, 0, 0, 0)), "tilted");
        // A decentered plane is still a plane on the axis.
        var plane = new SequentialSurface { Shape = new IrregularSurface(1e-4, 2e-4, 0, 0, 0, 0, 0, 0) };
        Assert.Equal(0.0, plane.Shape!.ParaxialEquivalent(plane).Curvature);
        // The lens names the surface.
        var lens = Lens(Front(new IrregularSurface(0, 0, 1, 0, 0, 0, 0, 0)));
        Assert.Contains("Surface 1: Irregular", Assert.Throws<ArgumentException>(() => lens.ToParaxialLens()).Message);
    }

    [Fact]
    public void ZernikeVertexExpansion_MatchesTheBasisAndItsDifferencedGradient()
    {
        // ZernikeSagTerms.AtVertex against ZernikeBasis itself: value and slope at the origin
        // exactly as the basis evaluates them, second derivatives by central differences of the
        // basis's exact gradient (h = 1e-5 of the normalised radius: truncation h²·∂⁴Z/6 below
        // 1e-7 for terms up to n = 6, rounding ε·|∇Z|/h about 1e-10).
        var rng = new Random(24);
        var a = Enumerable.Range(0, 28).Select(_ => rng.NextDouble() - 0.5).ToArray();
        var terms = new ZernikeSagTerms("test", a, 1.0, extrapolate: true, maximum: 231);
        var basis = new ZernikeBasis(ZernikeOrdering.Standard, a.Length);
        var v = terms.AtVertex();
        double value = 0, gx = 0, gy = 0;
        for (int j = 1; j <= a.Length; j++)
        {
            var (z, dx, dy) = basis.EvaluateWithGradient(j, 0, 0);
            value += a[j - 1] * z;
            gx += a[j - 1] * dx;
            gy += a[j - 1] * dy;
        }
        Close(value, v.Value, 1e-14, "value");
        Close(gx, v.Gx, 1e-14, "∂/∂x");
        Close(gy, v.Gy, 1e-14, "∂/∂y");
        const double h = 1e-5;
        (double X, double Y) Grad(double x, double y)
        {
            double sx = 0, sy = 0;
            for (int j = 1; j <= a.Length; j++)
            {
                var (_, dx, dy) = basis.EvaluateWithGradient(j, x, y);
                sx += a[j - 1] * dx;
                sy += a[j - 1] * dy;
            }
            return (sx, sy);
        }
        var (pxx, pxy) = Grad(h, 0);
        var (mxx, mxy) = Grad(-h, 0);
        var (pyx, pyy) = Grad(0, h);
        var (myx, myy) = Grad(0, -h);
        Close((pxx - mxx) / (2 * h), v.Hxx, 1e-6, "∂²/∂x²");
        Close((pyy - myy) / (2 * h), v.Hyy, 1e-6, "∂²/∂y²");
        Close((pxy - mxy) / (2 * h), v.Hxy, 1e-6, "∂²/∂x∂y");
        Close((pyx - myx) / (2 * h), v.Hxy, 1e-6, "∂²/∂y∂x");
    }

    // ---- Saved layouts (SavedSurfaceTypesTests' round trip for the OP-24 types) ----

    public static IEnumerable<object[]> SavedShapes()
    {
        SequentialSurface On(SurfaceShape shape, double c = 20, double k = 0, double? sd = null) =>
            new() { Shape = shape, Curvature = c, Conic = k, SemiDiameter = sd };
        yield return new object[] { "Toroidal", On(new ToroidalSurface(0.04, new[] { 0.5, 100, -2e5 }, new[] { 0, 1e-6, -2e-6, 3e-7 }, 0.006, true), k: -0.3) };
        yield return new object[] { "Toroidal", On(new ToroidalSurface(0, new[] { 0.2 })) };
        yield return new object[] { "Biconic", On(new BiconicSurface(0.03, -0.5), k: 0.2) };
        yield return new object[] { "Biconic Zernike", On(new BiconicZernikeSurface(0.03, -0.5, new[] { 0, 0.4, 20 }, new[] { 0, -0.3, 0, 1e3 }, new[] { 0, 1e-6, 0, 2e-6 }, 0.006, false)) };
        yield return new object[] { "Irregular", On(new IrregularSurface(1e-4, -2e-4, 0.5, -0.3, 1e-6, -2e-6, 5e-7, 25), sd: 0.005) };
    }

    private static SequentialLens SavedLens(SequentialSurface test) => new()
    {
        Surfaces = new[]
        {
            new SequentialSurface { Thickness = double.PositiveInfinity },
            new SequentialSurface { Thickness = 0.002, IsStop = true },
            test with { Thickness = 0.005, Medium = ParaxialMedium.Constant(1.5) },
            new SequentialSurface { Thickness = 0.03 },
            new SequentialSurface(),
        },
    };

    [Theory]
    [MemberData(nameof(SavedShapes))]
    public void EachType_RoundTripsThroughTheSavedSurfaces_AndTracesBitForBit(string type, SequentialSurface surface)
    {
        var lens = SavedLens(surface);
        var saved = SavedSurfaces.FromLens(lens, LensUnit.Meter);
        Assert.Equal(type, saved[2].Type);
        var back = SavedSurfaces.ToLens(saved, LensUnit.Meter, _ => null);
        Assert.Equal(type, back.Surfaces[2].Shape!.TypeName);
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(SavedSurfaces.FromLens(back, LensUnit.Meter)));
        var layout = SurfaceTypes.Find(type)!;
        Assert.Equal(JsonSerializer.Serialize(layout.Read(surface.Shape!)), JsonSerializer.Serialize(layout.Read(back.Surfaces[2].Shape!)));
        Assert.All(saved[2].Parameters ?? new(), p => Assert.True(p.Number < SurfaceTypes.FirstExtraData));
        Assert.All(saved[2].ExtraData ?? new(), p => Assert.True(p.Number >= SurfaceTypes.FirstExtraData));

        var sys = new OpticalSystemData
        {
            Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.008 },
            Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint() } },
        };
        var a = SequentialTrace.Create(lens, sys);
        var b = SequentialTrace.Create(back, sys);
        int ok = 0;
        foreach (double angle in new[] { 0.0, 0.03 })
            for (double x = -0.003; x <= 0.0031; x += 0.0015)
                for (double y = -0.003; y <= 0.0031; y += 0.0015)
                {
                    var dir = new Vector3D(0, Math.Sin(angle), Math.Cos(angle));
                    var ra = a.Trace(a.NewRay(new Vector3D(x, y, 0), dir));
                    var rb = b.Trace(b.NewRay(new Vector3D(x, y, 0), dir));
                    Assert.Equal(ra.Status, rb.Status);
                    foreach (var (p, q) in new[] { (ra.Position.X, rb.Position.X), (ra.Position.Y, rb.Position.Y), (ra.Position.Z, rb.Position.Z),
                                 (ra.Direction.X, rb.Direction.X), (ra.Direction.Y, rb.Direction.Y), (ra.Direction.Z, rb.Direction.Z), (ra.OpticalPath, rb.OpticalPath) })
                        Assert.Equal(BitConverter.DoubleToInt64Bits(p), BitConverter.DoubleToInt64Bits(q));
                    if (ra.Status == RayStatus.Ok) ok++;
                }
        Assert.True(ok > 0, "no ray reached the image");

        // In millimeters each value is its SI value over 1e-3^power (the column's power).
        var mm = SavedSurfaces.FromLens(lens, LensUnit.Millimeter);
        foreach (var (number, si) in layout.Read(surface.Shape!))
        {
            var info = layout.Parameter(number)!;
            Assert.Equal(si / SurfaceTypes.Factor(info.LengthPower, 1e-3), SavedSurfaces.Value(mm[2], number));
        }
        var fromMm = layout.Read(SavedSurfaces.ToLens(mm, LensUnit.Millimeter, _ => null).Surfaces[2].Shape!);
        foreach (var (number, si) in layout.Read(surface.Shape!))
            Assert.True(Math.Abs(fromMm[number] - si) <= 4e-16 * Math.Abs(si), $"{type} parameter {number}: {fromMm[number]:R} vs {si:R}");
    }

    [Fact]
    public void TheLayouts_FollowTheHelpPages()
    {
        // Parameter numbers and units from the four help pages (table "Parameter Definitions
        // for … Surfaces" on each).
        var toroidal = SurfaceTypes.Find("Toroidal")!;
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 13, 14 }, toroidal.Parameters.Take(11).Select(p => p.Number));
        Assert.Equal(245, toroidal.LastNumber);                       // "15-245 | Coefficients on Zernike polynomials 1 - 231"
        Assert.Equal(new[] { -1, -3, -5, -7, -9, -11, -13 }, Enumerable.Range(2, 7).Select(n => toroidal.Parameter(n)!.LengthPower));
        var bz = SurfaceTypes.Find("Biconic Zernike")!;
        Assert.Equal((0, 210), bz.Parameter(3)!.Range);               // "this value may be between 0 and 210, inclusive"
        Assert.Equal(0, bz.Parameter(13)!.LengthPower);               // x¹: lens units^0
        Assert.Equal(-15, bz.Parameter(28)!.LengthPower);             // x¹⁶
        Assert.Equal(0, bz.Parameter(29)!.LengthPower);               // y¹
        Assert.Equal(254, bz.LastNumber);                             // "45-254"
        var irregular = SurfaceTypes.Find("Irregular")!;
        Assert.True(irregular.Parameter(3)!.IsAngle && irregular.Parameter(4)!.IsAngle && irregular.Parameter(8)!.IsAngle);
        Assert.Equal(1, irregular.Parameter(6)!.LengthPower);
        Assert.Equal(new[] { 1, 2 }, SurfaceTypes.Find("Biconic")!.Parameters.Select(p => p.Number));

        // Too many Zernike terms or a missing normalization radius are refused by name.
        Assert.Contains("parameter 3 (Number of terms) is 211",
            Assert.Throws<ArgumentException>(() => bz.Build(n => n == 3 ? 211 : 0)).Message);
        Assert.Contains("normalization radius",
            Assert.Throws<ArgumentException>(() => toroidal.Build(n => n == 13 ? 4 : 0)).Message);
    }
}
