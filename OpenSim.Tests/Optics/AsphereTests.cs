using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-23: the asphere forms (docs/Optics-Conventions.md §17). Gates: each form set to
/// reproduce a conic traces as the Standard surface to 1e-12; normals equal finite-difference
/// sag gradients; Even Asphere → Q-type → Even Asphere keeps the sag to 1e-12 of the aperture.
/// Reference values are Forbes's, copied from the open-access papers and cited at each use
/// (transcribed in plans/sources/optics/forbes/Forbes-equations-read-2026-11-14.txt). Lens
/// prescriptions and coefficients are arbitrary test inputs, not reference data.
/// </summary>
public sealed class AsphereTests
{
    // ---- Builders ----

    private const double R1 = 0.05, K1 = -0.6;

    private static SequentialSurface Front(SurfaceShape? shape, double k = K1) => new()
    {
        Curvature = 1 / R1, Conic = k, Thickness = 0.006, Medium = ParaxialMedium.Constant(1.5168),
        IsStop = true, Shape = shape,
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

    // ---- Gate 1: forms set to a conic trace as the Standard surface ----

    /// <summary>Each entry: a form whose coefficients make it the conic of the front surface
    /// (or, for the superconic sphere, the same sphere written differently), and the conic
    /// constant it needs.</summary>
    public static IEnumerable<object[]> ConicForms()
    {
        yield return new object[] { "Even Asphere, zero terms", K1 };
        yield return new object[] { "Odd Asphere, zero terms", K1 };
        yield return new object[] { "Extended Asphere, zero terms", K1 };
        yield return new object[] { "Extended Odd Asphere, zero terms", K1 };
        yield return new object[] { "Qcon, zero terms", K1 };
        yield return new object[] { "Qbfs, zero terms", K1 };
        yield return new object[] { "Superconic, no terms", K1 };
        yield return new object[] { "Superconic, zero terms", K1 };
        yield return new object[] { "Superconic sphere with U1 = λ, V1 = λc", 0.0 };
        yield return new object[] { "Periodic, zero frequencies", K1 };
        yield return new object[] { "Periodic, zero amplitude", K1 };
        yield return new object[] { "Odd Cosine, R = 0 switches the cosine terms off", K1 };
    }

    private static SurfaceShape ConicForm(string name) => name switch
    {
        "Even Asphere, zero terms" => new EvenAsphere(Zeros(8)),
        "Odd Asphere, zero terms" => new OddAsphere(Zeros(8)),
        "Extended Asphere, zero terms" => new ExtendedAsphere(0.011, Zeros(12)),
        "Extended Odd Asphere, zero terms" => new ExtendedOddAsphere(0.011, Zeros(12)),
        "Qcon, zero terms" => new QTypeAsphere(QTypeKind.Qcon, 0.011, Zeros(6)),
        "Qbfs, zero terms" => new QTypeAsphere(QTypeKind.Qbfs, 0.011, Zeros(6)),
        "Superconic, no terms" => new SuperconicSurface(Array.Empty<double>(), Array.Empty<double>()),
        "Superconic, zero terms" => new SuperconicSurface(Zeros(3), Zeros(3)),
        // With k = 0 the conic is F_c = c·s² − 2z. The superconic F = c s² − 2Bz + C with
        // B = 1 + λs², C = c s² + λc s⁴ is (1 + λs²)·F_c (derived), so it has the same zeros and,
        // on F_c = 0, the same normal direction.
        "Superconic sphere with U1 = λ, V1 = λc" => new SuperconicSurface(new[] { 300.0 }, new[] { 300.0 / R1 }),
        // ¼(1 + 1)(1 + 1) − 1 = 0 for every amplitude when α = β = 0.
        "Periodic, zero frequencies" => new PeriodicSurface(2e-4, 0, 0),
        "Periodic, zero amplitude" => new PeriodicSurface(0, 370, 110),
        // "If R is zero or negative, the cosine terms are ignored" (OpticStudio help, Odd Cosine).
        "Odd Cosine, R = 0 switches the cosine terms off" => new OddCosineSurface(Zeros(16), 0,
            new[] { new OddCosineTerm(1e-4, 2, 3, 0.5), new OddCosineTerm(-2e-5, 1, 1, 0) }),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(ConicForms))]
    public void FormSetToAConic_TracesAsTheStandardSurface(string name, double k)
    {
        var system = Sys();
        var request = new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(11) };
        var standard = BatchTracer.Trace(Lens(Front(null, k)), system, request);
        var shaped = BatchTracer.Trace(Lens(Front(ConicForm(name), k)), system, request);
        Assert.Equal(standard.Rays.Count, shaped.Rays.Count);
        int ok = 0;
        for (int i = 0; i < standard.Rays.Count; i++)
        {
            var a = standard.Rays[i].Ray;
            var b = shaped.Rays[i].Ray;
            Assert.Equal(a.Status, b.Status);
            if (!a.IsOk) continue;
            ok++;
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

    [Fact]
    public void AlternateForms_MeetTheConicsOtherRoot_AndTheOrdinaryFormsTheVertexRoot()
    {
        // Sphere of radius R = 1 m, centre (0, 0, R). A ray from (x0, 0, 0) with direction
        // (L, 0, N) meets it where (x0 + sL)² + (sN − R)² = R², i.e. s² + 2(x0L − RN)s + x0² = 0
        // (L² + N² = 1), roots s = −(x0L − RN) ∓ √((x0L − RN)² − x0²) (derived). For this grazing
        // ray both points lie below the centre (z < R), on the sheet that holds the vertex.
        double radius = 1, x0 = -0.9, L = 0.95, N = Math.Sqrt(1 - L * L);
        var surface = new SequentialSurface { Curvature = 1 / radius };
        double half = x0 * L - radius * N;
        double root = Math.Sqrt(half * half - x0 * x0);
        double near = -half - root, far = -half + root;
        Assert.True(far * N < radius && near > 0);
        var d = new Vector3D(L, 0, N);
        foreach (var (shape, expected) in new (SurfaceShape, double)[]
        {
            (new EvenAsphere(Zeros(8)), near), (new OddAsphere(Zeros(8)), near),
            (new AlternateEvenAsphere(Zeros(8)), far), (new AlternateOddAsphere(Zeros(8)), far),
        })
        {
            Assert.True(shape.Intersect(x0, 0, d, surface, out var point, out double s), shape.TypeName);
            Close(expected, s, 1e-12, shape.TypeName);
            Close(expected * N, point.Z, 1e-12, shape.TypeName + " z");
        }
    }

    [Fact]
    public void AlternateEven_OnAnOrdinaryLens_MissesBecauseTheOtherRootIsOnTheFarHemisphere()
    {
        // For rays crossing a lens surface the quadratic's other root is on the far half of the
        // sphere, where the sag is not defined, so the trace flags a miss at surface 1.
        var result = BatchTracer.Trace(Lens(Front(new AlternateEvenAsphere(Zeros(8)), 0)), Sys(),
            new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(5) });
        Assert.All(result.Rays, r =>
        {
            Assert.Equal(RayStatus.MissedSurface, r.Ray.Status);
            Assert.Equal(1, r.Ray.FailedAtSurface);
        });
    }

    // ---- Gate 2: normals equal finite-difference sag gradients ----

    public static IEnumerable<object[]> RealForms() => RealFormNames().Select(n => new object[] { n });

    private static IEnumerable<string> RealFormNames() => new[]
    {
        "Even", "Odd", "Extended", "ExtendedOdd", "Qcon", "Qbfs", "Superconic", "Periodic", "OddCosine",
        "AlternateEven",
    };

    private static SurfaceShape RealForm(string name) => name switch
    {
        "Even" => new EvenAsphere(new[] { 0.5, 800, -2e5, 1e8 }),
        "Odd" => new OddAsphere(new[] { 1e-3, 0.2, 50, -1e4 }),
        "Extended" => new ExtendedAsphere(0.012, new[] { 1e-4, -3e-5, 2e-5, -1e-5, 5e-6 }),
        "ExtendedOdd" => new ExtendedOddAsphere(0.012, new[] { 2e-5, 1e-4, -3e-5, 1e-5 }),
        "Qcon" => new QTypeAsphere(QTypeKind.Qcon, 0.012, new[] { 2e-5, -1e-5, 5e-6, -2e-6 }),
        "Qbfs" => new QTypeAsphere(QTypeKind.Qbfs, 0.012, new[] { 3e-5, -1e-5, 4e-6, -2e-6, 1e-6 }),
        "Superconic" => new SuperconicSurface(new[] { 50.0, -1e3 }, new[] { 2e3, -1e5 }),
        "Periodic" => new PeriodicSurface(2e-6, 150, 90),
        "OddCosine" => new OddCosineSurface(new[] { 1e-3, 0.2, 10 }, 0.01,
            new[] { new OddCosineTerm(2e-6, 2, 3, 0.4), new OddCosineTerm(1e-6, 3.5, 1, -0.2) }),
        "AlternateEven" => new AlternateEvenAsphere(new[] { 0.5, 800, -2e5, 1e8 }),
        _ => throw new ArgumentException(name),
    };

    private static readonly (double X, double Y)[] Points =
        { (0.003, -0.002), (-0.006, 0.0045), (0.0071, 0.0012), (0, 0.008), (-0.0005, -0.0002) };

    [Theory]
    [MemberData(nameof(RealForms))]
    public void Normal_EqualsTheFiniteDifferenceSagGradient(string name)
    {
        var shape = RealForm(name);
        var surface = Front(shape);
        // Central differences with h = 1 µm: truncation h²·z‴/6 stays below 1e-9 for these
        // surfaces (the stiffest, the periodic one, has z‴ ≈ (2π·150/m)³·2 µm ≈ 2e3 m⁻²), and
        // rounding ε·|z|/h is about 1e-13.
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
        if (name == "AlternateEven") return;   // its other root is off the vertex sheet for these rays
        var shape = RealForm(name);
        var surface = Front(shape);
        int met = 0;
        foreach (var (x, y) in Points)
            foreach (var d in new[] { new Vector3D(0, 0, 1), new Vector3D(0.2, -0.1, 1), new Vector3D(-0.35, 0.25, 1), new Vector3D(0.1, 0.3, -1) })
            {
                var u = d / d.Length;
                Assert.True(shape.Intersect(x, y, u, surface, out var p, out double s), $"{name}: ({x}, {y}) {u} missed");
                met++;
                // On the ray by construction. On the surface: Newton stops when its step is a
                // few ulps of the 10 mm coordinates (about 1e-17 m) or, at the rounding floor,
                // within 64 times that, so 1e-14 m is a sharp bound with margin.
                Close(x + s * u.X, p.X, 1e-18, $"{name} on ray x");
                Close(shape.Sag(p.X, p.Y, surface), p.Z, 1e-14, $"{name} on surface");
            }
        Assert.Equal(Points.Length * 4, met);
    }

    [Theory]
    [MemberData(nameof(RealForms))]
    public void LensWithTheForm_TracesEveryRay(string name)
    {
        if (name == "AlternateEven") return;
        var result = BatchTracer.Trace(Lens(Front(RealForm(name))), Sys(), new BatchTraceRequest { Pupil = BatchTracer.SquareGrid(9) });
        Assert.All(result.Rays, r => Assert.True(r.Ray.IsOk, $"{name}: {r.Ray.Status} at {r.Ray.FailedAtSurface}"));
    }

    [Fact]
    public void RayBeyondTheConicsSquareRoot_IsAMiss()
    {
        // c = 20/m, k = 0: the sag exists only for r < 1/c = 50 mm. A ray parallel to the axis at
        // r = 60 mm meets nothing.
        var shape = new EvenAsphere(new[] { 0.0, 10 });
        var surface = new SequentialSurface { Curvature = 20, Shape = shape };
        Assert.False(shape.Intersect(0.06, 0, Vector3D.UnitZ, surface, out _, out _));
        Assert.True(double.IsNaN(shape.Sag(0.06, 0, surface)));
    }

    // ---- Q polynomials against Forbes's closed forms ----

    private static readonly double[] Xs = { 0, 0.13, 0.5, 0.77, 1 };

    /// <summary>Forbes, Opt. Express 15, 5218 (2007), Eq. (9): Q^con_0 … Q^con_5.</summary>
    private static double QconClosed(int m, double x) => m switch
    {
        0 => 1,
        1 => -(5 - 6 * x),
        2 => 15 - 14 * x * (3 - 2 * x),
        3 => -(35 - 12 * x * (14 - x * (21 - 10 * x))),
        4 => 70 - 3 * x * (168 - 5 * x * (84 - 11 * x * (8 - 3 * x))),
        5 => -(126 - x * (1260 - 11 * x * (420 - x * (720 - 13 * x * (45 - 14 * x))))),
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    /// <summary>Forbes, Opt. Express 15, 5218 (2007), Eq. (14): Q^bfs_0 … Q^bfs_3.</summary>
    private static double QbfsClosed(int m, double x) => m switch
    {
        0 => 1,
        1 => (13 - 16 * x) / Math.Sqrt(19),
        2 => Math.Sqrt(2.0 / 95) * (29 - 4 * x * (25 - 19 * x)),
        3 => Math.Sqrt(2.0 / 2545) * (207 - 4 * x * (315 - x * (577 - 320 * x))),
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    [Fact]
    public void QconRecurrence_MatchesForbes2007Equation9()
    {
        foreach (double x in Xs)
        {
            var q = QPolynomials.ConBasis(5, x);
            for (int m = 0; m <= 5; m++) Close(QconClosed(m, x), q[m], 1e-12, $"Q^con_{m}({x})");
        }
    }

    [Fact]
    public void QbfsRecurrence_MatchesForbes2007Equation14()
    {
        foreach (double x in Xs)
        {
            var q = QPolynomials.BfsBasis(3, x);
            for (int m = 0; m <= 3; m++) Close(QbfsClosed(m, x), q[m], 1e-13, $"Q^bfs_{m}({x})");
        }
    }

    [Fact]
    public void QbfsBand_MatchesForbes2010Equation_A17()
    {
        // Forbes, Opt. Express 18, 19700 (2010), Eq. (A.17), the initial sub-block of the
        // change-of-basis matrix.
        var (f, g, h) = QPolynomials.BfsBand(5);
        Close(2, f[0], 1e-15, "f0");
        Close(Math.Sqrt(19.0 / 4), f[1], 1e-15, "f1");
        Close(4 * Math.Sqrt(10.0 / 19), f[2], 1e-15, "f2");
        Close(0.5 * Math.Sqrt(509.0 / 10), f[3], 1e-14, "f3");
        Close(6 * Math.Sqrt(259.0 / 509), f[4], 1e-14, "f4");
        Close(0.5 * Math.Sqrt(25607.0 / 259), f[5], 1e-14, "f5");
        Close(-0.5, g[0], 1e-15, "g0");
        Close(-5 / (2 * Math.Sqrt(19)), g[1], 1e-15, "g1");
        Close(-17 / (2 * Math.Sqrt(190)), g[2], 1e-15, "g2");
        Close(-91 / (2 * Math.Sqrt(5090)), g[3], 1e-15, "g3");
        Close(-473 / (2 * Math.Sqrt(131831)), g[4], 1e-15, "g4");
        Close(-0.5, h[0], 1e-15, "h0");
        Close(-6 / Math.Sqrt(19), h[1], 1e-15, "h1");
        Close(-1.5 * Math.Sqrt(19.0 / 10), h[2], 1e-14, "h2");
        Close(-20 * Math.Sqrt(10.0 / 509), h[3], 1e-14, "h3");
    }

    [Fact]
    public void ClenshawSumsAndDerivatives_MatchTheClosedForms()
    {
        var a = new[] { 0.7, -1.3, 0.45, 2.1, -0.6, 0.25 };
        var aBfs = a.Take(4).ToArray();
        var b = QPolynomials.BfsToAuxiliary(aBfs);
        var aBack = QPolynomials.BfsFromAuxiliary(b);
        for (int m = 0; m < aBfs.Length; m++) Close(aBfs[m], aBack[m], 1e-14, $"a{m} through b and back");
        const double h = 1e-6;
        foreach (double x in Xs.Where(x => x > 0 && x < 1))
        {
            double Con(double t) => Enumerable.Range(0, 6).Sum(m => a[m] * QconClosed(m, t));
            double Bfs(double t) => Enumerable.Range(0, 4).Sum(m => aBfs[m] * QbfsClosed(m, t));
            QPolynomials.ConSum(a, x, out double s, out double ds);
            Close(Con(x), s, 1e-11, $"Σ Q^con at {x}");
            Close((Con(x + h) - Con(x - h)) / (2 * h), ds, 1e-6, $"Σ Q^con′ at {x}");
            QPolynomials.BfsSumAuxiliary(b, x, out double sb, out double dsb);
            Close(Bfs(x), sb, 1e-12, $"Σ Q^bfs at {x}");
            Close((Bfs(x + h) - Bfs(x - h)) / (2 * h), dsb, 1e-7, $"Σ Q^bfs′ at {x}");
        }
    }

    [Fact]
    public void QbfsSlope_WithZeroConic_IsForbesEquation3_14()
    {
        // Forbes, Opt. Express 18, 19700 (2010), Eq. (3.14): with φ = √(1 − c²ρ²),
        // z′ = cρ/φ + u[1 + φ² − u²(1 + 3φ²)]/(ρmax φ³)·S + 2u³(1 − u²)/(ρmax φ)·S′.
        double rhoMax = 0.012, c = 1 / 0.03;
        var coefficients = new[] { 3e-5, -1e-5, 4e-6, -2e-6, 1e-6 };
        var shape = new QTypeAsphere(QTypeKind.Qbfs, rhoMax, coefficients);
        var surface = new SequentialSurface { Curvature = c, Shape = shape };
        var b = QPolynomials.BfsToAuxiliary(coefficients);
        foreach (double r in new[] { 0.001, 0.004, 0.0083, 0.0119 })
        {
            double u = r / rhoMax, x = u * u, phi = Math.Sqrt(1 - c * c * r * r);
            QPolynomials.BfsSumAuxiliary(b, x, out double s, out double ds);
            double expected = c * r / phi + u * (1 + phi * phi - x * (1 + 3 * phi * phi)) / (rhoMax * phi * phi * phi) * s
                + 2 * u * x * (1 - x) / (rhoMax * phi) * ds;
            Assert.True(shape.TryRadialSag(r, surface, out _, out double slope));
            Close(expected, slope, 1e-13 * Math.Abs(expected) + 1e-16, $"z′({r})");
        }
    }

    [Fact]
    public void CosineTransformFit_ReproducesForbesWorkedExample()
    {
        // Forbes, Opt. Express 18, 19700 (2010), Sec. 4: a parabola of axial radius 20 mm over
        // ρmax = 20 mm has best-fit radius 25 mm (Eq. 4.2), and Eq. (4.5) with N = 32 gives the
        // b of Eq. (4.7), in nm. The paper notes that for N ≥ 16 the last few digits of the
        // smaller members vary, and b0 is printed to 1e-5 nm, so the tolerance is 1e-5 nm.
        double axialRadius = 0.020, rhoMax = 0.020;
        var b = QPolynomials.BfsFitByCosineTransform(r => r * r / (2 * axialRadius), rhoMax, 32, out double c);
        Close(25e-3, 1 / c, 1e-15, "best-fit radius");
        double[] expectedNm =
        {
            1009010.04959, 2770.64974485, -4739.30847163, 1172.09704743,
            -257.270488293, 55.4172061289, -11.966650385, 2.60463667585,
        };
        for (int m = 0; m < expectedNm.Length; m++) Close(expectedNm[m], b[m] * 1e9, 1e-5, $"b{m} (nm)");

        // Eq. (4.8): the seven retained b converted to a by Eqs. (3.2)–(3.3), printed rounded
        // to the nanometre, so the tolerance is half a nanometre.
        var a = QPolynomials.BfsFromAuxiliary(b.Take(7).ToArray());
        double[] aNm = { 2019004, 7143, -13944, 4190, -1095, 283, -68 };
        for (int m = 0; m < aNm.Length; m++) Close(aNm[m], a[m] * 1e9, 0.5, $"a{m} (nm)");
    }

    // ---- Gate 3: Even Asphere → Q-type → Even Asphere ----

    private static readonly double[] EvenAlpha = { 0, 50, 1e5, 1e9, -5e12, 1e16, -1e20, 1e23 };

    [Fact]
    public void EvenToQconAndBack_KeepsTheSag()
    {
        // The Even Asphere's departure α2r⁴ + … + α8r¹⁶ is u⁴ times a polynomial of order 6 in
        // u², which seven Q^con terms represent exactly; the way back represents it exactly with
        // α1 … α8. So both fits are exact to rounding.
        double aperture = 0.01;
        var even = new SequentialSurface { Curvature = 25, Conic = -1.2, Shape = new EvenAsphere(EvenAlpha) };
        var toQ = AsphereConversion.ToQType(even, QTypeKind.Qcon, 7, aperture);
        var back = AsphereConversion.ToEvenAsphere(toQ.Surface, aperture);
        double tol = 1e-12 * aperture;
        Assert.True(toQ.MaxResidual <= tol, $"to Qcon: max residual {toQ.MaxResidual:R}");
        Assert.True(back.MaxResidual <= tol, $"back: max residual {back.MaxResidual:R}");
        for (int i = 0; i <= 200; i++)
        {
            double r = aperture * i / 200;
            Close(AsphereConversion.RadialSag(even, r), AsphereConversion.RadialSag(back.Surface, r), tol, $"sag at r = {r}");
            Close(AsphereConversion.RadialSag(even, r), AsphereConversion.RadialSag(toQ.Surface, r), tol, $"Qcon sag at r = {r}");
        }
        Assert.Equal(25, toQ.Surface.Curvature);
        Assert.Equal(-1.2, toQ.Surface.Conic);
    }

    [Fact]
    public void EvenWithAnR2Term_ToQcon_ReportsTheResidual()
    {
        // Q^con departures start at u⁴, so an α1·r² term cannot be represented; the fit says so
        // through its residual instead of hiding it.
        var even = new SequentialSurface { Curvature = 25, Shape = new EvenAsphere(new[] { 0.5, 50 }) };
        var fit = AsphereConversion.ToQType(even, QTypeKind.Qcon, 8, 0.01);
        Assert.True(fit.MaxResidual > 1e-8, $"residual {fit.MaxResidual:R}");
    }

    [Fact]
    public void QbfsSurface_FittedToQbfs_RecoversItsCurvatureAndCoefficients()
    {
        // A Qbfs departure vanishes at the edge, so the conic through the vertex and the edge
        // point is the surface's own conic, and the fit recovers the coefficients.
        double rho = 0.01;
        var a = new[] { 2e-5, -8e-6, 3e-6, -1e-6 };
        var source = new SequentialSurface { Curvature = 1 / 0.03, Conic = -0.4, Shape = new QTypeAsphere(QTypeKind.Qbfs, rho, a) };
        var fit = AsphereConversion.ToQType(source, QTypeKind.Qbfs, 4, rho);
        Close(source.Curvature, fit.Surface.Curvature, 1e-12 * source.Curvature, "curvature");
        var q = Assert.IsType<QTypeAsphere>(fit.Surface.Shape);
        for (int m = 0; m < a.Length; m++) Close(a[m], q.Coefficients[m], 1e-16, $"a{m}");
        Assert.True(fit.MaxResidual <= 1e-12 * rho, $"residual {fit.MaxResidual:R}");
    }

    [Fact]
    public void Conversion_RefusesANonRotationalShape()
    {
        var periodic = new SequentialSurface { Curvature = 10, Shape = new PeriodicSurface(1e-6, 100, 100) };
        var e = Assert.Throws<ArgumentException>(() => AsphereConversion.ToEvenAsphere(periodic, 0.01));
        Assert.Contains("Periodic", e.Message);
    }

    [Fact]
    public void Constructors_RefuseBadData()
    {
        Assert.Throws<ArgumentException>(() => new EvenAsphere(Zeros(9)));
        Assert.Throws<ArgumentException>(() => new ExtendedAsphere(0, Zeros(3)));
        Assert.Throws<ArgumentException>(() => new QTypeAsphere(QTypeKind.Qcon, -1, Zeros(3)));
        Assert.Throws<ArgumentException>(() => new QTypeAsphere(QTypeKind.Qcon, 1, Zeros(101)));
        Assert.Throws<ArgumentException>(() => new SuperconicSurface(Zeros(2), Zeros(3)));
        Assert.Throws<ArgumentException>(() => new OddCosineSurface(Zeros(16), 1, Enumerable.Repeat(new OddCosineTerm(1, 1, 1, 1), 7).ToArray()));
        Assert.Throws<ArgumentException>(() => new OddAsphere(new[] { double.NaN }));
    }
}
