using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.Freeform;
using OpenSim.Optics.Sequential.LensEditing;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-25: freeform and data-defined surfaces (docs/Optics-Conventions.md §55). Gate (plan): each
/// polynomial family set to a pure quadratic matches the paraboloid; a grid sag sampled from an
/// analytic asphere traces within the interpolation bound, which shrinks by 16 for each halving
/// of the grid (bicubic order); Zernike sag term by term against the closed-form polynomial.
/// <para>
/// The closed-form Zernike polynomials are copied from OpticStudio 2025 R2 help, "Zernike Fringe
/// Coefficients" (table Zernike Fringe Polynomials) and "Zernike Standard Coefficients" (table
/// Zernike Standard Polynomials), saved under plans/sources/optics/op-25-help/. The
/// Off-Axis Conic Freeform term order is copied from its help page's term table. Every other
/// expected value is derived in the test from its inputs (the derivation is beside it); the
/// prescriptions and coefficients are arbitrary test inputs, not reference data.
/// </para>
/// </summary>
public sealed class FreeformSurfaceTests
{
    private const double C = 20;          // paraboloid curvature, 1/m (R = 50 mm)

    private static SequentialSurface On(SurfaceShape shape, double c = 0, double k = 0) => new() { Shape = shape, Curvature = c, Conic = k };

    private static readonly (double X, double Y)[] Points =
    {
        (0, 0), (0.001, 0), (0, -0.002), (0.003, 0.002), (-0.0025, 0.0035), (-0.004, -0.001), (0.0015, -0.0037),
    };

    /// <summary>A grid file's data sampled from f and its derivatives on n × n points over ±half
    /// (meters, unit flag 3), in the file's order: row 0 is y = +half, each row from x = −half.</summary>
    internal static GridSagData Sampled(int n, double half, Func<double, double, (double Z, double Zx, double Zy, double Zxy)> f)
    {
        double h = 2 * half / (n - 1);
        var z = new double[n * n];
        var zx = new double[n * n];
        var zy = new double[n * n];
        var zxy = new double[n * n];
        for (int row = 0; row < n; row++)
            for (int i = 0; i < n; i++)
            {
                var v = f(-half + i * h, half - row * h);
                int k = row * n + i;
                (z[k], zx[k], zy[k], zxy[k]) = v;
            }
        return new GridSagData(n, n, h, h, 3, 0, 0, z, zx, zy, zxy);
    }

    /// <summary>z = (C/2)(x² + y²) and its derivatives (derived).</summary>
    private static (double, double, double, double) Paraboloid(double x, double y) => (C / 2 * (x * x + y * y), C * x, C * y, 0);

    /// <summary>Radial NURBS control points that draw z = a·r² on [0, Y] exactly (derived): on the
    /// cubic clamped knots (0,0,0,0,½,1,1,1,1) of 5 points, y(t) = Y·t has the Greville abscissae
    /// (0, 1/6, 1/2, 5/6, 1)·Y as controls (linear precision), and t² has the blossom
    /// (t₁t₂ + t₁t₃ + t₂t₃)/3 at the knot triples (0,0,0), (0,0,½), (0,½,1), (½,1,1), (1,1,1), that
    /// is 0, 0, 1/6, 2/3, 1; with unit weights the curve is (Y·t, a·Y²·t²), so z = a·y².</summary>
    private static RadialNurbsSurface NurbsParabola(double a, double Y) => new(
        new[] { Y / 6, Y / 2, 5 * Y / 6, Y },
        new[] { 0, a * Y * Y / 6, 2 * a * Y * Y / 3, a * Y * Y },
        new[] { 1.0, 1, 1, 1 });

    public static IEnumerable<object[]> QuadraticFamilies()
    {
        const double rn = 0.006;
        // Polynomial: γ1 x² + γ5 y² with γ1 = γ5 = C/2.
        yield return new object[] { "Polynomial", On(new PolynomialSurface(new[] { C / 2, 0, 0, 0, C / 2 })) };
        // Extended Polynomial on a plane: A3 u² + A5 v², u = x/R_n, with A3 = A5 = (C/2)R_n².
        yield return new object[] { "Extended Polynomial", On(new ExtendedPolynomialSurface(rn, new[] { 0, 0, C / 2 * rn * rn, 0, C / 2 * rn * rn })) };
        // Chebyshev on a plane, x₀ = y₀ = R_n: x₀y₀[c00 + c20(2u² − 1) + c02(2v² − 1)] with
        // c20 = c02 = C/4 and c00 = c20 + c02 is (C/2)(x² + y²).
        var cheb = new double[3, 3];
        cheb[0, 0] = C / 2;
        cheb[2, 0] = C / 4;
        cheb[0, 2] = C / 4;
        yield return new object[] { "Chebyshev Polynomial", On(new ChebyshevPolynomialSurface(rn, rn, cheb)) };
        // Fringe: A1·1 + A4(2ρ² − 1) with A1 = A4 = C·R_n²/4 is (C/2)r².
        double a4 = C * rn * rn / 4;
        yield return new object[] { "Zernike Fringe Sag", On(new ZernikeSagSurface(ZernikeOrdering.Fringe, Array.Empty<double>(), new[] { a4, 0, 0, a4 }, rn, false)) };
        // Standard: A1·1 + A4·√3(2ρ² − 1) with A4 = C·R_n²/(4√3), A1 = √3·A4.
        double s4 = C * rn * rn / (4 * Math.Sqrt(3));
        yield return new object[] { "Zernike Standard Sag", On(new ZernikeSagSurface(ZernikeOrdering.Standard, Array.Empty<double>(), new[] { Math.Sqrt(3) * s4, 0, 0, s4 }, rn, false)) };
        // Annular with ε = 0 is the Standard set.
        yield return new object[] { "Zernike Annular Standard Sag", On(new ZernikeSagSurface(ZernikeOrdering.Annular, Array.Empty<double>(), new[] { Math.Sqrt(3) * s4, 0, 0, s4 }, rn, false)) };
        // Off-Axis Conic Freeform with Y₀ = 0 is the surface's own conic, here the paraboloid.
        yield return new object[] { "Off-Axis Conic Freeform", On(new OffAxisConicFreeform(0, 0, Array.Empty<double>()), C, -1) };
        // Grid Sag on a plane, grid sampled from the paraboloid (bicubic reproduces a quadratic).
        yield return new object[] { "Grid Sag", On(new GridSagSurface(Sampled(11, 0.005, Paraboloid))) };
        // Radial NURBS drawing (C/2)r² exactly.
        yield return new object[] { "Radial NURBS", On(NurbsParabola(C / 2, 0.006)) };
    }

    [Theory]
    [MemberData(nameof(QuadraticFamilies))]
    public void Gate_EachFamilySetToAPureQuadratic_MatchesTheParaboloid(string type, SequentialSurface surface)
    {
        var shape = surface.Shape!;
        Assert.Equal(type, shape.TypeName);
        foreach (var (x, y) in Points)
        {
            double expected = C / 2 * (x * x + y * y);
            double z = shape.Sag(x, y, surface);
            Assert.True(Math.Abs(z - expected) <= 1e-12 * expected + 1e-18, $"{type} sag at ({x}, {y}): {z:R} vs {expected:R}");
            var n = shape.Normal(new Vector3D(x, y, z), surface);
            var m = new Vector3D(-C * x, -C * y, 1);
            m /= m.Length;
            Assert.True((n - m).Length <= 1e-12, $"{type} normal at ({x}, {y}): {n} vs {m}");
        }
        // A ray meets it on the paraboloid z = (C/2)(x² + y²).
        var dir = new Vector3D(0.04, -0.06, 1);
        dir /= dir.Length;
        Assert.True(shape.Intersect(0.002, 0.003, dir, surface, out var p, out _), $"{type}: the ray missed");
        Assert.True(Math.Abs(p.Z - C / 2 * (p.X * p.X + p.Y * p.Y)) <= 1e-12 * p.Z, $"{type}: hit {p}");
    }

    [Theory]
    [MemberData(nameof(QuadraticFamilies))]
    public void Gate_TheQuadraticFamilies_GiveTheParaboloidsParaxialCurvature(string type, SequentialSurface surface)
    {
        // The Zernike families reach z(0) = 0 only as A1 − A4·(value of Z4 at 0), which rounding
        // can leave a few 1e-21 m off zero; the paraxial model refuses any vertex sag (§23), so
        // they are not asked here (their vertex expansion is the OP-24 code's, tested there).
        if (type.StartsWith("Zernike", StringComparison.Ordinal)) return;
        double c = surface.Shape!.ParaxialEquivalent(surface).Curvature;
        Assert.True(Math.Abs(c - C) <= 1e-12 * C, $"{type}: paraxial curvature {c:R}, expected {C}");
    }

    // ------------------------------------------------------------------ Zernike, term by term

    /// <summary>"Zernike Fringe Coefficients", table Zernike Fringe Polynomials, terms 1–16 and 37.</summary>
    private static readonly Dictionary<int, Func<double, double, double>> Fringe = new()
    {
        [1] = (r, f) => 1,
        [2] = (r, f) => r * Math.Cos(f),
        [3] = (r, f) => r * Math.Sin(f),
        [4] = (r, f) => 2 * r * r - 1,
        [5] = (r, f) => r * r * Math.Cos(2 * f),
        [6] = (r, f) => r * r * Math.Sin(2 * f),
        [7] = (r, f) => (3 * r * r - 2) * r * Math.Cos(f),
        [8] = (r, f) => (3 * r * r - 2) * r * Math.Sin(f),
        [9] = (r, f) => 6 * Math.Pow(r, 4) - 6 * r * r + 1,
        [10] = (r, f) => Math.Pow(r, 3) * Math.Cos(3 * f),
        [11] = (r, f) => Math.Pow(r, 3) * Math.Sin(3 * f),
        [12] = (r, f) => (4 * r * r - 3) * r * r * Math.Cos(2 * f),
        [13] = (r, f) => (4 * r * r - 3) * r * r * Math.Sin(2 * f),
        [14] = (r, f) => (10 * Math.Pow(r, 4) - 12 * r * r + 3) * r * Math.Cos(f),
        [15] = (r, f) => (10 * Math.Pow(r, 4) - 12 * r * r + 3) * r * Math.Sin(f),
        [16] = (r, f) => 20 * Math.Pow(r, 6) - 30 * Math.Pow(r, 4) + 12 * r * r - 1,
        [37] = (r, f) => 924 * Math.Pow(r, 12) - 2772 * Math.Pow(r, 10) + 3150 * Math.Pow(r, 8) - 1680 * Math.Pow(r, 6) + 420 * Math.Pow(r, 4) - 42 * r * r + 1,
    };

    /// <summary>"Zernike Standard Coefficients", table Zernike Standard Polynomials, terms 1–15 and 22.</summary>
    private static readonly Dictionary<int, Func<double, double, double>> Standard = new()
    {
        [1] = (r, f) => 1,
        [2] = (r, f) => Math.Sqrt(4) * r * Math.Cos(f),
        [3] = (r, f) => Math.Sqrt(4) * r * Math.Sin(f),
        [4] = (r, f) => Math.Sqrt(3) * (2 * r * r - 1),
        [5] = (r, f) => Math.Sqrt(6) * r * r * Math.Sin(2 * f),
        [6] = (r, f) => Math.Sqrt(6) * r * r * Math.Cos(2 * f),
        [7] = (r, f) => Math.Sqrt(8) * (3 * Math.Pow(r, 3) - 2 * r) * Math.Sin(f),
        [8] = (r, f) => Math.Sqrt(8) * (3 * Math.Pow(r, 3) - 2 * r) * Math.Cos(f),
        [9] = (r, f) => Math.Sqrt(8) * Math.Pow(r, 3) * Math.Sin(3 * f),
        [10] = (r, f) => Math.Sqrt(8) * Math.Pow(r, 3) * Math.Cos(3 * f),
        [11] = (r, f) => Math.Sqrt(5) * (6 * Math.Pow(r, 4) - 6 * r * r + 1),
        [12] = (r, f) => Math.Sqrt(10) * (4 * Math.Pow(r, 4) - 3 * r * r) * Math.Cos(2 * f),
        [13] = (r, f) => Math.Sqrt(10) * (4 * Math.Pow(r, 4) - 3 * r * r) * Math.Sin(2 * f),
        [14] = (r, f) => Math.Sqrt(10) * Math.Pow(r, 4) * Math.Cos(4 * f),
        [15] = (r, f) => Math.Sqrt(10) * Math.Pow(r, 4) * Math.Sin(4 * f),
        [22] = (r, f) => Math.Sqrt(7) * (20 * Math.Pow(r, 6) - 30 * Math.Pow(r, 4) + 12 * r * r - 1),
    };

    [Fact]
    public void Gate_ZernikeSag_TermByTerm_EqualsTheClosedFormPolynomial()
    {
        // Tolerance: term 37's closed form sums values up to 3150ρ⁸ that cancel to order 1, so
        // its own rounding is a few 1e-13 of the coefficient; 1e-11 stays far below any wrong term.
        const double rn = 0.005, a = 2e-6;
        foreach (var (ordering, table) in new[] { (ZernikeOrdering.Fringe, Fringe), (ZernikeOrdering.Standard, Standard), (ZernikeOrdering.Annular, Standard) })
            foreach (var (term, z) in table)
            {
                var coefficients = new double[term];
                coefficients[term - 1] = a;
                var surface = On(new ZernikeSagSurface(ordering, Array.Empty<double>(), coefficients, rn, false));
                foreach (double rho in new[] { 0.0, 0.3, 0.71, 1.0 })
                    foreach (double phi in new[] { 0.0, 0.4, 2.2, -1.3 })
                    {
                        double x = rn * rho * Math.Cos(phi), y = rn * rho * Math.Sin(phi);
                        double expected = a * z(rho, phi);
                        double sag = surface.Shape!.Sag(x, y, surface);
                        Assert.True(Math.Abs(sag - expected) <= 1e-11 * a, $"{ordering} term {term} at ρ = {rho}, φ = {phi}: {sag:R} vs {expected:R}");
                    }
            }
    }

    [Fact]
    public void ZernikeSag_DecenterShiftsTheTerms_ExtrapolateAndObscurationBoundThem()
    {
        const double rn = 0.005, a = 1e-6, dx = 0.001, dy = -0.0005;
        var coefficients = new[] { 0, 0, 0, a };                    // Fringe Z4 = 2ρ² − 1
        var shifted = On(new ZernikeSagSurface(ZernikeOrdering.Fringe, Array.Empty<double>(), coefficients, rn, false, dx, dy));
        // ρ = |(x − dx, y − dy)|/R_n (parameters 9 and 10, "Zernike decenter").
        double x = 0.002, y = 0.001, rho2 = ((x - dx) * (x - dx) + (y - dy) * (y - dy)) / (rn * rn);
        Assert.Equal(a * (2 * rho2 - 1), shifted.Shape!.Sag(x, y, shifted), 1e-18);
        // Outside the normalization radius: ignored with extrapolate 0, used with 1.
        var bounded = On(new ZernikeSagSurface(ZernikeOrdering.Fringe, Array.Empty<double>(), coefficients, rn, false));
        var extrapolated = On(new ZernikeSagSurface(ZernikeOrdering.Fringe, Array.Empty<double>(), coefficients, rn, true));
        Assert.Equal(0, bounded.Shape!.Sag(0.006, 0, bounded));
        Assert.Equal(a * (2 * 0.006 * 0.006 / (rn * rn) - 1), extrapolated.Shape!.Sag(0.006, 0, extrapolated), 1e-18);
        // Annular: inside the obscuration the terms are always ignored.
        var annular = On(new ZernikeSagSurface(ZernikeOrdering.Annular, Array.Empty<double>(), new[] { a }, rn, true, obscuration: 0.4));
        Assert.Equal(0, annular.Shape!.Sag(0.001, 0, annular));
        Assert.NotEqual(0, annular.Shape!.Sag(0.003, 0, annular));
        // The help pages' limits.
        Assert.Throws<ArgumentException>(() => new ZernikeSagSurface(ZernikeOrdering.Fringe, Array.Empty<double>(), new double[38], rn, false));
        Assert.Throws<ArgumentException>(() => new ZernikeSagSurface(ZernikeOrdering.Annular, Array.Empty<double>(), new double[81], rn, false));
    }

    // ------------------------------------------------------------------ polynomial forms

    [Fact]
    public void PolynomialAndExtendedPolynomial_FollowTheirSagEquations()
    {
        // Polynomial: z = γ1x² + γ2x⁴ + γ3x⁶ + γ4x⁸ + γ5y² + γ6y⁴ + γ7y⁶ + γ8y⁸.
        var g = new[] { 3.0, -200, 1e5, 2e7, 7, 300, -4e4, 1e6 };
        var poly = On(new PolynomialSurface(g));
        double x = 0.004, y = -0.003;
        double expected = g[0] * Math.Pow(x, 2) + g[1] * Math.Pow(x, 4) + g[2] * Math.Pow(x, 6) + g[3] * Math.Pow(x, 8)
            + g[4] * Math.Pow(y, 2) + g[5] * Math.Pow(y, 4) + g[6] * Math.Pow(y, 6) + g[7] * Math.Pow(y, 8);
        Assert.Equal(expected, poly.Shape!.Sag(x, y, poly), 1e-17);
        // Extended Polynomial on a conic: term 12 is x²y² and term 13 is xy³ (the help page's
        // examples), each on (x/R_n), (y/R_n).
        const double rn = 0.005;
        var terms = new double[13];
        terms[11] = 2e-6;
        terms[12] = -1e-6;
        var ext = On(new ExtendedPolynomialSurface(rn, terms), 10, -0.5);
        double u = x / rn, v = y / rn;
        ConicSag.TryEvaluate(Math.Sqrt(x * x + y * y), 10, -0.5, out double conic, out _);
        Assert.Equal(conic + 2e-6 * u * u * v * v - 1e-6 * u * v * v * v, ext.Shape!.Sag(x, y, ext), 1e-17);
        // The Polynomial has no radius and no conic.
        Assert.False(SurfaceTypes.Find("Polynomial")!.UsesRadiusAndConic);
    }

    [Fact]
    public void Chebyshev_IsUndefinedOutsideItsNormalizationLengths_AndRefusesAConic()
    {
        var c = new double[2, 2];
        c[1, 1] = 3;
        var s = On(new ChebyshevPolynomialSurface(0.004, 0.003, c));
        // x₀y₀·c(1,1)·T1(x/x₀)·T1(y/y₀) = c(1,1)·x·y.
        Assert.Equal(3 * 0.002 * 0.001, s.Shape!.Sag(0.002, 0.001, s), 1e-18);
        Assert.True(double.IsNaN(s.Shape!.Sag(0.0041, 0, s)));
        Assert.True(double.IsNaN(s.Shape!.Sag(0, -0.0031, s)));
        var withConic = s with { Conic = -1 };
        Assert.True(double.IsNaN(withConic.Shape!.Sag(0.001, 0, withConic)));
        Assert.Contains("conic", Assert.Throws<ArgumentException>(() => withConic.Shape!.ParaxialEquivalent(withConic)).Message);
        Assert.Null(SurfaceTypes.Find("Chebyshev Polynomial"));    // its coefficient order is not on the help page
    }

    // ------------------------------------------------------------------ off-axis conic

    [Theory]
    [InlineData(0.05, -1.0, 0.015)]
    [InlineData(0.05, -0.5, 0.012)]
    [InlineData(0.05, 0.0, 0.01)]
    [InlineData(0.05, 0.7, 0.01)]
    [InlineData(-0.05, -0.5, 0.012)]
    [InlineData(-0.04, -3.0, -0.008)]
    [InlineData(0.06, -2.0, 0.015)]
    public void OffAxisConic_IsThePartOfTheParentConicAtY0_InItsTangentFrame(double radius, double conic, double y0)
    {
        double c = 1 / radius;
        var surface = On(new OffAxisConicFreeform(y0, 0, Array.Empty<double>()), c, conic);
        // The parent's sag and slope at Y₀ (ConicSag, the Standard surface), and the local frame:
        // y′ along the parent's tangent (cos θ, sin θ) in (Y, Z), z′ along its normal, θ = atan(sag′(Y₀)).
        Assert.True(ConicSag.TryEvaluate(Math.Abs(y0), c, conic, out double z0, out double slope));
        double theta = Math.Atan(Math.Sign(y0) * slope);
        foreach (var (x, y) in Points)
        {
            double z = surface.Shape!.Sag(x, y, surface);
            Assert.False(double.IsNaN(z));
            double Y = y0 + y * Math.Cos(theta) - z * Math.Sin(theta);
            double Z = z0 + y * Math.Sin(theta) + z * Math.Cos(theta);
            Assert.True(ConicSag.TryEvaluate(Math.Sqrt(x * x + Y * Y), c, conic, out double parent, out _));
            Assert.True(Math.Abs(Z - parent) <= 1e-16, $"R {radius}, k {conic}, Y0 {y0} at ({x}, {y}): {Z:R} vs parent {parent:R}");
        }
        // The vertex is on the surface with no slope (§23's paraxial rule then applies).
        Assert.Equal(0, surface.Shape!.Sag(0, 0, surface), 1e-18);
        var n0 = surface.Shape!.Normal(new Vector3D(0, 0, 0), surface);
        Assert.True(Math.Abs(n0.X) <= 1e-14 && Math.Abs(n0.Y) <= 1e-14, $"normal at the vertex {n0}");
    }

    [Fact]
    public void OffAxisConic_FreeformTerms_RunInTheHelpTablesOrder()
    {
        // Term table, "Off-Axis Conic Freeform": 1 A1,0 (x), 2 A0,1 (y), 3 A2,0 (x²), 4 A1,1 (xy),
        // 5 A0,2 (y²), 6 A3,0 (x³), 7 A2,1 (x²y), 8 A1,2 (xy²), 9 A0,3 (y³), 10 A4,0 (x⁴).
        (int Px, int Py)[] table = { (1, 0), (0, 1), (2, 0), (1, 1), (0, 2), (3, 0), (2, 1), (1, 2), (0, 3), (4, 0) };
        const double rn = 0.01;
        double x = 0.003, y = -0.007;
        for (int i = 0; i < table.Length; i++)
        {
            var terms = new double[i + 1];
            terms[i] = 1e-5;
            var s = On(new OffAxisConicFreeform(0.02, rn, terms));   // plane parent: only the term
            double expected = 1e-5 * Math.Pow(x / rn, table[i].Px) * Math.Pow(y / rn, table[i].Py);
            Assert.Equal(expected, s.Shape!.Sag(x, y, s), 1e-19);
        }
    }

    // ------------------------------------------------------------------ grid sag

    [Fact]
    public void Gate_GridSag_OfAQuartic_ErrsByTheHermiteRemainder_Exactly_AndBy16PerHalving()
    {
        // z = a·r⁴ = a(x⁴ + 2x²y² + y⁴). Derived: the tensor cubic Hermite interpolant reproduces
        // x²y² (degree ≤ 3 in each variable) and leaves on x⁴ the one-dimensional remainder
        // f⁗/4!·(x − x_i)²(x − x_{i+1})² = (x − x_i)²(x − x_{i+1})², so at local (t, s) in a cell
        // of side h the interpolant is z − a·h⁴[t²(1 − t)² + s²(1 − s)²]; at a cell centre a·h⁴/8.
        const double a = 1e4, half = 0.005;
        static (double, double, double, double) Quartic(double x, double y)
        {
            double r2 = x * x + y * y;
            return (a * r2 * r2, 4 * a * x * r2, 4 * a * y * r2, 8 * a * x * y);
        }
        double previous = double.NaN;
        foreach (int n in new[] { 11, 21, 41 })
        {
            var grid = new GridSagSurface(Sampled(n, half, Quartic));
            double h = 2 * half / (n - 1), centreError = 0;
            for (int i = 0; i < n - 1; i++)
                for (int j = 0; j < n - 1; j++)
                    foreach (var (t, s) in new[] { (0.5, 0.5), (0.25, 0.5), (0.8, 0.1) })
                    {
                        double x = -half + (i + t) * h, y = -half + (j + s) * h;
                        Assert.True(grid.TryGridSag(x, y, out double z, out _, out _));
                        double expected = a * h * h * h * h * (t * t * (1 - t) * (1 - t) + s * s * (1 - s) * (1 - s));
                        double error = Quartic(x, y).Item1 - z;
                        Assert.True(Math.Abs(error - expected) <= 1e-6 * expected, $"n = {n} at ({x}, {y}): error {error:R}, remainder {expected:R}");
                        if (t == 0.5 && s == 0.5) centreError = Math.Max(centreError, error);
                    }
            if (!double.IsNaN(previous)) Assert.Equal(16, previous / centreError, 6);
            previous = centreError;
        }
    }

    /// <summary>The asphere of the trace gate: z = a1r² + a2r⁴ + a3r⁶ (arbitrary test values).</summary>
    private const double A1 = 10, A2 = 1.6e4, A3 = 1.3e8, Half = 0.005;

    private static (double, double, double, double) Asphere(double x, double y)
    {
        double r2 = x * x + y * y;
        double q = A1 + 2 * A2 * r2 + 3 * A3 * r2 * r2;                // dz/d(r²)
        double dq = 2 * A2 + 6 * A3 * r2;                               // d²z/d(r²)²
        return (A1 * r2 + A2 * r2 * r2 + A3 * r2 * r2 * r2, 2 * x * q, 2 * y * q, 4 * x * y * dq);
    }

    /// <summary>
    /// The interpolation bound on ±Half for spacing h (derived). With f − IₓI_y f = (f − I_y f) +
    /// (I − Iₓ)(I_y f) and the one-dimensional cubic Hermite remainder ≤ h⁴/384·max|f⁗|
    /// (max of (x − x_i)²(x − x_{i+1})² is h⁴/16, over 4!), and ∂ₓ⁴ I_y f = I_y(∂ₓ⁴f) bounded by
    /// max|∂ₓ⁴f| + 2·(4/27)h·max|∂_y∂ₓ⁴f| (H0 + H1 = 1 with both ≥ 0; |G0|, |G1| ≤ 4h/27):
    /// B = h⁴/384·[M + M + (8/27)h·N], where for r⁴ = x⁴ + 2x²y² + y⁴ and r⁶ = x⁶ + 3x⁴y² + 3x²y⁴
    /// + y⁶, ∂ₓ⁴f = 24a2 + a3(360x² + 72y²), so M = 24a2 + 432·a3·Half² (at a corner) and
    /// N = max|144·a3·y| = 144·a3·Half; the y direction is the same by symmetry.
    /// </summary>
    private static double Bound(double h)
    {
        double m = 24 * A2 + 432 * A3 * Half * Half, n = 144 * A3 * Half;
        return h * h * h * h / 384 * (2 * m + 8.0 / 27 * h * n);
    }

    [Fact]
    public void Gate_GridSag_OfAnAsphere_IsWithinTheBound_AndConvergesAtFourthOrder()
    {
        double previous = double.NaN;
        foreach (int n in new[] { 21, 41, 81 })
        {
            var grid = new GridSagSurface(Sampled(n, Half, Asphere));
            double h = 2 * Half / (n - 1), worst = 0;
            for (int i = 0; i < n - 1; i++)
                for (int j = 0; j < n - 1; j++)
                    foreach (var (t, s) in new[] { (0.5, 0.5), (0.3, 0.7), (0.5, 0.2) })
                    {
                        double x = -Half + (i + t) * h, y = -Half + (j + s) * h;
                        Assert.True(grid.TryGridSag(x, y, out double z, out _, out _));
                        worst = Math.Max(worst, Math.Abs(z - Asphere(x, y).Item1));
                    }
            Assert.True(worst <= Bound(h), $"n = {n}: error {worst:R} above the bound {Bound(h):R}");
            // The ratio tends to 16 as h → 0 (fourth order); at these spacings the maximum's
            // position still moves by h/2 per halving, which keeps it a few percent below 16
            // (15.4 and 15.7 here).
            if (!double.IsNaN(previous))
            {
                double ratio = previous / worst;
                Assert.True(ratio > 15 && ratio < 16.5, $"n = {n}: error ratio {ratio:R} for a halving");
            }
            previous = worst;
        }
    }

    [Fact]
    public void Gate_RaysTracedOntoAGridSampledFromAnAsphere_LandWithinTheBound()
    {
        // The base is the conic (c = 20, k = −0.5), evaluated exactly; the grid carries the
        // asphere's polynomial part, so the surface's error is the grid's interpolation error.
        const double c = 20, k = -0.5;
        foreach (int n in new[] { 21, 41 })
        {
            var surface = On(new GridSagSurface(Sampled(n, Half, Asphere)), c, k);
            double bound = Bound(2 * Half / (n - 1));
            int traced = 0;
            foreach (var dir0 in new[] { new Vector3D(0, 0, 1), new Vector3D(0.05, -0.03, 1), new Vector3D(-0.08, 0.02, 1) })
            {
                var dir = dir0 / dir0.Length;
                for (double x = -0.004; x <= 0.00401; x += 0.001)
                    for (double y = -0.004; y <= 0.00401; y += 0.001)
                    {
                        Assert.True(surface.Shape!.Intersect(x, y, dir, surface, out var p, out _), $"ray from ({x}, {y}) missed");
                        ConicSag.TryEvaluate(Math.Sqrt(p.X * p.X + p.Y * p.Y), c, k, out double conic, out _);
                        double exact = conic + Asphere(p.X, p.Y).Item1;
                        Assert.True(Math.Abs(p.Z - exact) <= bound, $"n = {n}: hit {p} is {p.Z - exact:R} m off the asphere (bound {bound:R})");
                        traced++;
                    }
            }
            Assert.Equal(3 * 81, traced);
        }
    }

    [Fact]
    public void GridSagFile_ReadsTheHelpPagesLayout_AndWritesItBack()
    {
        // 5 × 5 points, spacing 2 mm, unit flag 0 (mm), decenter (1, −0.5) mm; "!" comments; the
        // first data line is the (−x, +y) corner, rows read left to right; point 7 has nodata 1.
        var text = new System.Text.StringBuilder("! a test grid\n5 5 2 -2 0 1 -0.5\n");
        for (int row = 0; row < 5; row++)
            for (int i = 0; i < 5; i++)
            {
                int k = row * 5 + i;
                text.Append(k == 6 ? "9 9 9 9 1\n" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{0.001 * (10 * row + i)} 0 0 0\n"));
            }
        var data = GridSagFile.Read(new StringReader(text.ToString()), "test.dat");
        Assert.Equal((5, 5, 2.0, 2.0, LensUnit.Millimeter), (data.Nx, data.Ny, data.DeltaX, data.DeltaY, data.Unit));
        Assert.Equal((1.0, -0.5), (data.DecenterX, data.DecenterY));
        Assert.Equal(0.001 * 22, data.Sag[12]);              // row 2, column 2
        Assert.Equal(0, data.Sag[6]);                       // nodata: the whole line replaced with zeros
        Assert.True(data.NoData![6]);
        var again = GridSagFile.Read(new StringReader(GridSagFile.Write(data)));
        Assert.Equal(data.Sag, again.Sag);
        Assert.Equal(data.NoData, again.NoData);

        // The grid's points: x = 1 mm + (i − 2)·2 mm, y = −0.5 mm + (2 − row)·2 mm. The (−x, +y)
        // corner (row 0, i = 0) is at (−3, 3.5) mm and holds 0; point (row 1, i = 3), at (3, 1.5) mm,
        // holds 0.013 mm. At a node the bicubic interpolant takes the node's value.
        var grid = new GridSagSurface(data);
        Assert.True(grid.DerivativesEstimated);
        Assert.True(grid.TryGridSag(-0.003, 0.0035, out double z0, out _, out _));
        Assert.Equal(0, z0, 1e-18);
        Assert.True(grid.TryGridSag(0.003, 0.0015, out double z13, out _, out _));
        Assert.Equal(0.013e-3, z13, 1e-18);
        Assert.False(grid.TryGridSag(0.0051 + 0.001, 0, out _, out _, out _));   // beyond x = 5 mm: a ray miss
        var surface = On(grid);
        Assert.True(double.IsNaN(surface.Shape!.Sag(-0.0031, 0, surface)));

        Assert.Contains("line 2", Assert.Throws<FormatException>(() => GridSagFile.Read(new StringReader("5 5 1 1 0 0 0\n1 2 x 4\n"))).Message);
        Assert.Contains("at least 5", Assert.Throws<FormatException>(() => GridSagFile.Read(new StringReader("4 5 1 1 0 0 0\n"))).Message);
        Assert.Contains("ends after", Assert.Throws<FormatException>(() => GridSagFile.Read(new StringReader("5 5 1 1 0 0 0\n1 0 0 0\n"))).Message);
    }

    [Fact]
    public void GridSag_EstimatedDerivatives_ReproduceAQuadratic_AndLinearInterpolationIsBilinear()
    {
        // z = 3x² − 2xy + 5y² + 0.01x: second-order differences are exact on a quadratic
        // (central and one-sided alike), so with every derivative zero in the file the estimated
        // ones are exact and the bicubic interpolant is the quadratic (derived).
        static double Q(double x, double y) => 3 * x * x - 2 * x * y + 5 * y * y + 0.01 * x;
        var file = Sampled(9, 0.004, (x, y) => (Q(x, y), 0, 0, 0));
        var grid = new GridSagSurface(file);
        Assert.True(grid.DerivativesEstimated);
        foreach (var (x, y) in Points)
        {
            Assert.True(grid.TryGridSag(x, y, out double z, out double zx, out double zy));
            Assert.Equal(Q(x, y), z, 1e-18);
            Assert.Equal(6 * x - 2 * y + 0.01, zx, 1e-15);
            Assert.Equal(-2 * x + 10 * y, zy, 1e-15);
        }
        // Linear: at a cell centre, the mean of its four corners (bilinear, derived).
        var linear = new GridSagSurface(file, GridSagInterpolation.Linear);
        double h = 0.001, cx = 0.0005, cy = -0.0015;
        double mean = (Q(cx - h / 2, cy - h / 2) + Q(cx + h / 2, cy - h / 2) + Q(cx - h / 2, cy + h / 2) + Q(cx + h / 2, cy + h / 2)) / 4;
        Assert.True(linear.TryGridSag(cx, cy, out double zl, out _, out _));
        Assert.Equal(mean, zl, 1e-18);
    }

    // ------------------------------------------------------------------ radial NURBS

    [Fact]
    public void RadialNurbs_DrawsTheParabolaOfItsControlPoints_AndEndsAtItsLastPoint()
    {
        const double a = 7, Y = 0.006;
        var surface = On(NurbsParabola(a, Y));
        foreach (double r in new[] { 0.0, 1e-4, 0.001, 0.0023, 0.004, 0.0059, Y })
        {
            double z = surface.Shape!.Sag(r, 0, surface);
            Assert.True(Math.Abs(z - a * r * r) <= 1e-14 * a * Y * Y, $"r = {r}: {z:R} vs {a * r * r:R}");
        }
        Assert.True(double.IsNaN(surface.Shape!.Sag(0.0061, 0, surface)));   // beyond the last point: a ray miss
        Assert.Equal(2 * a, surface.Shape!.ParaxialEquivalent(surface).Curvature, 1e-12);
        // A first point off z = 0 leaves the vertex with a slope (a cone point): no paraxial model.
        var cone = On(new RadialNurbsSurface(new[] { 0.001, 0.002, 0.003, 0.004 }, new[] { 1e-4, 2e-4, 3e-4, 4e-4 }, new[] { 1.0, 1, 1, 1 }));
        Assert.Contains("cone point", Assert.Throws<ArgumentException>(() => cone.Shape!.ParaxialEquivalent(cone)).Message);
        Assert.Throws<ArgumentException>(() => new RadialNurbsSurface(new[] { 0.001, 0.002, 0.002, 0.004 }, new double[4], new[] { 1.0, 1, 1, 1 }));
        Assert.Throws<ArgumentException>(() => new RadialNurbsSurface(new[] { 0.001, 0.002, 0.003 }, new double[3], new[] { 1.0, 1, 1 }));
        Assert.Throws<ArgumentException>(() => new RadialNurbsSurface(new[] { 0.001, 0.002, 0.003, 0.004 }, new double[4], new[] { 1.0, 0, 1, 1 }));
    }

    // ------------------------------------------------------------------ layouts and saving

    public static IEnumerable<object[]> SavedShapes()
    {
        yield return new object[] { "Polynomial", On(new PolynomialSurface(new[] { 5.0, -300, 0, 0, 8, 100 })) };
        yield return new object[] { "Extended Polynomial", On(new ExtendedPolynomialSurface(0.006, new[] { 0, 0, 1e-5, 2e-6, 3e-5, 0, 1e-7 }), 20, -0.4) };
        yield return new object[] { "Zernike Fringe Sag", On(new ZernikeSagSurface(ZernikeOrdering.Fringe, new[] { 0.5, 100 }, new[] { 0, 1e-6, 0, 2e-6, 0, 0, 0, 0, 3e-7 }, 0.006, true, 2e-4, -1e-4), 20) };
        yield return new object[] { "Zernike Standard Sag", On(new ZernikeSagSurface(ZernikeOrdering.Standard, new[] { 0.2 }, new[] { 0, 0, 0, 1e-6, 0, 3e-7 }, 0.006, false), 20, -0.3) };
        yield return new object[] { "Zernike Annular Standard Sag", On(new ZernikeSagSurface(ZernikeOrdering.Annular, Array.Empty<double>(), new[] { 0, 0, 0, 1e-6, 0, 0, 0, 0, 0, 0, 2e-7 }, 0.006, true, obscuration: 0.25), 20) };
        yield return new object[] { "Off-Axis Conic Freeform", On(new OffAxisConicFreeform(0.01, 0.006, new[] { 0, 0, 1e-6, 0, 2e-6, 5e-8 }), 20, -1) };
        yield return new object[] { "Grid Sag", On(new GridSagSurface(Sampled(11, 0.005, Asphere), GridSagInterpolation.Bicubic, new[] { 0.3 }, new[] { 0, 0, 0, 1e-7 }, 0.006, 1e-4, 0), 15, -0.2) };
        yield return new object[] { "Radial NURBS", On(NurbsParabola(8, 0.006)) };
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
        Assert.Equal(type == FreeformLayouts.GridSag, saved[2].GridSag is not null);

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

        // In millimeters each parameter is its SI value over 1e-3^power; the grid stays in its
        // own file's unit.
        var mm = SavedSurfaces.FromLens(lens, LensUnit.Millimeter);
        foreach (var (number, si) in layout.Read(surface.Shape!))
            Assert.Equal(si / SurfaceTypes.Factor(layout.Parameter(number)!.LengthPower, 1e-3), SavedSurfaces.Value(mm[2], number));
        var fromMm = layout.Read(SavedSurfaces.ToLens(mm, LensUnit.Millimeter, _ => null).Surfaces[2].Shape!);
        foreach (var (number, si) in layout.Read(surface.Shape!))
            Assert.True(Math.Abs(fromMm[number] - si) <= 4e-16 * Math.Abs(si), $"{type} parameter {number}: {fromMm[number]:R} vs {si:R}");
        if (saved[2].GridSag is { } g) Assert.Equal(JsonSerializer.Serialize(g), JsonSerializer.Serialize(mm[2].GridSag));
    }

    [Fact]
    public void GridSag_IsRefusedWithoutItsGrid_KeepsItInASolve_AndScalesWithTheLens()
    {
        var surface = On(new GridSagSurface(Sampled(11, 0.005, Asphere)));
        var saved = SavedSurfaces.FromLens(SavedLens(surface), LensUnit.Millimeter);
        var noGrid = saved.ToList();
        noGrid[2] = noGrid[2] with { GridSag = null };
        Assert.Contains("Surface 2: Grid Sag: no grid data has been imported", Assert.Throws<ArgumentException>(() => SavedSurfaces.ToLens(noGrid, LensUnit.Millimeter, _ => null)).Message);
        var wrongType = saved.ToList();
        wrongType[2] = wrongType[2] with { Type = "Even Asphere" };
        Assert.Contains("only a Grid Sag surface has grid data", Assert.Throws<ArgumentException>(() => SavedSurfaces.ToLens(wrongType, LensUnit.Millimeter, _ => null)).Message);

        // A solve on α1 keeps the grid.
        var solved = SurfaceTypes.WithParameter(surface, 1, 0.4);
        var g = Assert.IsType<GridSagSurface>(solved.Shape);
        Assert.Same(((GridSagSurface)surface.Shape!).Data, g.Data);
        Assert.Equal(0.4, g.Alpha[0]);

        // Scale Lens by 2: the grid's sag at (2x, 2y) is twice the old sag at (x, y) (derived:
        // spacings, decenters and sags double, slopes stay, the cross derivative halves).
        var scaled = ScaleLens.Apply(saved, 2).Surfaces;
        var big = SavedSurfaces.ToLens(scaled, LensUnit.Millimeter, _ => null).Surfaces[2];
        foreach (var (x, y) in Points)
            Assert.Equal(2 * surface.Shape!.Sag(x, y, surface), big.Shape!.Sag(2 * x, 2 * y, big), 1e-17);
        // A lens-unit change leaves the grid as it is (it is in its own file's unit).
        Assert.Equal(JsonSerializer.Serialize(saved[2].GridSag), JsonSerializer.Serialize(ScaleLens.ConvertUnits(saved, 1000).Surfaces[2].GridSag));
    }

    [Fact]
    public void TheLayouts_FollowTheHelpPages()
    {
        // "Parameter Definitions for … Surfaces" on each page.
        var poly = SurfaceTypes.Find("Polynomial")!;
        Assert.Equal(Enumerable.Range(1, 8), poly.Parameters.Select(p => p.Number));
        Assert.Equal(new[] { -1, -3, -5, -7, -1, -3, -5, -7 }, poly.Parameters.Select(p => p.LengthPower));
        var ext = SurfaceTypes.Find("Extended Polynomial")!;
        Assert.Equal((0, 230), ext.Parameter(13)!.Range);             // "Maximum term number (up to 230)"
        Assert.Equal(244, ext.LastNumber);                             // "15-244 | Polynomial terms"
        var fringe = SurfaceTypes.Find("Zernike Fringe Sag")!;
        Assert.Equal(51, fringe.LastNumber);                           // "15 - 51 | Coefficients on Zernike polynomials 1-37"
        Assert.Equal(1, fringe.Parameter(9)!.LengthPower);             // "Zernike decenter X in lens units"
        Assert.Equal(245, SurfaceTypes.Find("Zernike Standard Sag")!.LastNumber);   // "15 - 245"
        var annular = SurfaceTypes.Find("Zernike Annular Standard Sag")!;
        Assert.Equal(95, annular.LastNumber);                          // "16 - 95 | Coefficients on Zernike polynomials 1-80"
        Assert.Equal(0, annular.Parameter(13)!.LengthPower);           // "13 | Obscuration"
        Assert.Equal((0, 80), annular.Parameter(14)!.Range);
        var oacf = SurfaceTypes.Find("Off-Axis Conic Freeform")!;
        Assert.Equal(new[] { 13, 14, 15, 16 }, oacf.Parameters.Take(4).Select(p => p.Number));
        Assert.Equal(245, oacf.LastNumber);                            // "16-245 | X1Y0, X0Y1, etc."
        var grid = SurfaceTypes.Find("Grid Sag")!;
        Assert.Equal((0, 1), grid.Parameter(0)!.Range);                // "0 for bicubic spline, 1 for linear"
        Assert.Equal(245, grid.LastNumber);
        var nurbs = SurfaceTypes.Find("Radial NURBS")!;
        Assert.Equal((4, 60), nurbs.Parameter(13)!.Range);             // "At least 4 points … no more than 60"
        Assert.Equal(new[] { "y1", "z1", "w1" }, new[] { 14, 15, 16 }.Select(n => nurbs.Parameter(n)!.Name));
        Assert.Equal("y60", nurbs.Parameter(3 * 60 + 11)!.Name);       // "3n+11 | y coordinate for control point n"
        Assert.False(nurbs.UsesRadiusAndConic);
    }
}
