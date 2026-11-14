using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using Xunit.Abstractions;

namespace OpenSim.Tests.Optics;

/// <summary>
/// TC-6: the asphere forms' first-order models (<see cref="SurfaceShape.ParaxialEquivalent"/>,
/// docs/Optics-Conventions.md §17). Every expected value is derived here or in the class under
/// test from the sag equations OP-23 cites; the coefficients and lenses are arbitrary test
/// inputs, not reference data.
/// </summary>
public sealed class AsphereParaxialTests
{
    private static readonly double Eps = Math.BitIncrement(1.0) - 1.0;   // 2^-52
    private const double C0 = 1 / 0.05, K0 = -0.6, N = 1.5168;
    private readonly ITestOutputHelper _out;

    public AsphereParaxialTests(ITestOutputHelper output) => _out = output;

    // ---- Builders ----

    private static SequentialSurface Front(SurfaceShape? shape, double c = C0, double k = K0) => new()
    {
        Curvature = c, Conic = k, Thickness = 0.006, Medium = ParaxialMedium.Constant(N),
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
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.004 },
        Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint(), new FieldPoint { Y = 2 } } },
    };

    private static void Close(double expected, double actual, double tolerance, string what = "")
    {
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what} expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");
    }

    public static IEnumerable<object[]> Forms() => new[]
    {
        "Even", "Odd", "Extended", "ExtendedOdd", "Qcon", "Qbfs", "Superconic", "Periodic", "OddCosine",
        "AlternateEven", "AlternateOdd",
    }.Select(n => new object[] { n });

    private const double Rn = 0.012, Rc = 0.01;

    private static SurfaceShape Form(string name) => name switch
    {
        "Even" => new EvenAsphere(new[] { 0.5, 800, -2e5 }),
        "Odd" => new OddAsphere(new[] { 1e-3, 0.2, 50, -1e3 }),
        "Extended" => new ExtendedAsphere(Rn, new[] { 1e-4, -3e-5, 2e-5 }),
        "ExtendedOdd" => new ExtendedOddAsphere(Rn, new[] { 2e-5, 1e-4, -3e-5, 1e-5 }),
        "Qcon" => new QTypeAsphere(QTypeKind.Qcon, Rn, new[] { 2e-5, -1e-5, 5e-6 }),
        "Qbfs" => new QTypeAsphere(QTypeKind.Qbfs, Rn, new[] { 3e-5, -1e-5, 4e-6, -2e-6 }),
        "Superconic" => new SuperconicSurface(new[] { 50.0, -1e3 }, new[] { 2e3, -1e5 }),
        "Periodic" => new PeriodicSurface(2e-6, 150, 90),
        "OddCosine" => new OddCosineSurface(new[] { 1e-3, 0.2, 10 }, Rc, new[]
        {
            new OddCosineTerm(2e-6, 2, 2, 0.4),     // even B: the same on both halves of the y meridian
            new OddCosineTerm(3e-6, 2, 3, 0.4),     // odd B: opposite halves, mean zero
            new OddCosineTerm(1e-7, 3.5, 1, -0.2),  // P > 2: no second-order part
            new OddCosineTerm(1e-6, 1, 0, 0),       // P = 1: a cone, left out
        }),
        "AlternateEven" => new AlternateEvenAsphere(new[] { 0.5, 800, -2e5 }),
        "AlternateOdd" => new AlternateOddAsphere(new[] { 1e-3, 0.2, 50, -1e3 }),
        _ => throw new ArgumentException(name),
    };

    /// <summary>The curvature each form's sag equation gives at the vertex, derived by hand
    /// from the coefficients of <see cref="Form"/> (see the overrides' comments).</summary>
    private static double ExpectedCurvature(string name) => name switch
    {
        "Even" or "AlternateEven" => C0 + 2 * 0.5,
        "Odd" or "AlternateOdd" => C0 + 2 * 0.2,
        "Extended" => C0 + 2 * 1e-4 / (Rn * Rn),
        "ExtendedOdd" => C0 + 2 * 1e-4 / (Rn * Rn),
        "Qcon" or "Superconic" => C0,
        // S(0) = Σ a_m Q^bfs_m(0), here from the basis recurrence ([F2010b] Eq. 2.7), not from
        // the Clenshaw sum on the auxiliary coefficients that the shape uses.
        "Qbfs" => C0 + 2 * QbfsAtZero(new[] { 3e-5, -1e-5, 4e-6, -2e-6 }) / (Rn * Rn),
        "Periodic" => C0 + 2 * Math.PI * Math.PI * 2e-6 * 90 * 90,
        // β2 = 0.2; the B = 2 term gives 2A cos(π + 0.4)/R² = −2A cos(0.4)/R²; the B = 3 term's
        // halves are cos(3π/2 + 0.4) = sin 0.4 and cos(−3π/2 + 0.4) = −sin 0.4, mean 0.
        "OddCosine" => C0 + 2 * 0.2 - 2 * 2e-6 * Math.Cos(0.4) / (Rc * Rc),
        _ => throw new ArgumentException(name),
    };

    private static double QbfsAtZero(double[] a)
    {
        var q = QPolynomials.BfsBasis(a.Length - 1, 0);
        double s = 0;
        for (int m = 0; m < a.Length; m++) s += a[m] * q[m];
        return s;
    }

    // ---- The curvature against the sag equation and against the sag itself ----

    [Theory]
    [MemberData(nameof(Forms))]
    public void Curvature_IsTheDerivedVertexCurvature(string name)
    {
        var shape = Form(name);
        double expected = ExpectedCurvature(name);
        double actual = shape.ParaxialEquivalent(Front(shape)).Curvature;
        // The two sides add the same few terms in different orders (and Qbfs's S(0) by two
        // recurrences); 64 ulps of the largest term bounds their rounding.
        Close(expected, actual, 64 * Eps * Math.Max(Math.Abs(expected), C0), $"{name} curvature");
    }

    /// <summary>
    /// The paraxial curvature equals a finite-difference second derivative of the shape's own
    /// <see cref="SurfaceShape.Sag"/> along the y meridian through the vertex.
    /// <para>
    /// D(h) = [z(2h) − 2z(h) + 2z(0) − 2z(−h) + z(−2h)]/(2h²) is the mean of the forward and
    /// backward second differences. It cancels terms linear in |y| exactly (β1 r, a cone,
    /// which the paraxial curvature leaves out), and for the rest
    /// D(h) = z″(0) + 6β₃h + O(h²) (derived: an |y|³ term gives (8 − 2)β₃h³/h² on each side);
    /// R = 2D(h/2) − D(h) removes the h term. The h² remainder is (7/24)h²·z⁗(0) (Taylor:
    /// (7/12)h² for D, then the extrapolation). The stiffest sag here is the Qcon's, whose
    /// x²S(x) term has z⁗ = 24·S(0)/ρmax⁴ ≈ 1.7e5 m⁻³ (S(0) ≈ 1.5e-4 m for these coefficients,
    /// which also matches the 5e-8 m⁻¹ error seen at h = 1 µm); the periodic one has
    /// A(2πβ)⁴/2 ≈ 5e4 m⁻³ and the Odd Asphere 24|β4| + 3|1 + k|c³ ≈ 3e4 m⁻³. With h = 0.5 µm the
    /// remainder is below 1.3e-8 m⁻¹; the Odd Cosine's |y|^3.5 term adds O(A/R^3.5·h^1.5) ≈ 1e-9.
    /// Rounding: each sag is good to a few ulps of its largest term (≤ |β1|·2h + 30·(2h)² m),
    /// and R weighs the sags by 36/h² in all, which gives about 1e-10 m⁻¹; the superconic's
    /// Newton stops within 8 ulps of r, at most 36·8ε·2h/h² ≈ 3e-8 m⁻¹ in the worst case.
    /// Tolerance 1e-7 m⁻¹, against the curvature changes of 0.3 m⁻¹ and more that the test is
    /// there to see.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Forms))]
    public void Curvature_EqualsTheSagsSecondDifferenceAtTheVertex(string name)
    {
        var shape = Form(name);
        var surface = Front(shape);
        double Z(double y) => shape.Sag(0, y, surface);
        double D(double h) => (Z(2 * h) - 2 * Z(h) + 2 * Z(0) - 2 * Z(-h) + Z(-2 * h)) / (2 * h * h);
        const double h = 5e-7, tol = 1e-7;
        double fd = 2 * D(h / 2) - D(h);
        double actual = shape.ParaxialEquivalent(surface).Curvature;
        _out.WriteLine($"{name}: paraxial {actual:R}, difference {fd:R}, error {actual - fd:E3}");
        Close(fd, actual, tol, $"{name} curvature against the sag");
    }

    // ---- First-order data ----

    [Fact]
    public void EvenAsphereWithOnlyAlpha1_HasTheParaxialDataOfTheSphereOfRadiusOneOverTwoAlpha1()
    {
        // On a plane base the sag is exactly α1 r²; the sphere of radius 1/(2α1) has
        // z = c r²/2 + O(r⁴) = α1 r² + O(r⁴) with c = 2α1, the same vertex curvature, so every
        // first-order quantity agrees. Curvature 2α1 enters both lenses as the same
        // double, so the agreement is exact.
        const double alpha1 = 7.5;
        var asphere = ParaxialSystem.Create(Lens(Front(new EvenAsphere(new[] { alpha1 }), c: 0, k: 0)).ToParaxialLens(), Sys());
        var sphere = ParaxialSystem.Create(Lens(Front(null, c: 2 * alpha1, k: 0)).ToParaxialLens(), Sys());
        Assert.NotNull(sphere.EffectiveFocalLength);
        Assert.Equal(sphere.EffectiveFocalLength, asphere.EffectiveFocalLength);
        Assert.Equal(sphere.BackFocalLength, asphere.BackFocalLength);
        Assert.Equal(sphere.ExitPupilPosition, asphere.ExitPupilPosition);
        Assert.Equal(sphere.ParaxialImageHeight, asphere.ParaxialImageHeight);
        // And on a curved base the asphere is the sphere of curvature c + 2α1.
        var curved = ParaxialSystem.Create(Lens(Front(new EvenAsphere(new[] { alpha1, 300 }), c: C0, k: K0)).ToParaxialLens(), Sys());
        var curvedSphere = ParaxialSystem.Create(Lens(Front(null, c: C0 + 2 * alpha1, k: 0)).ToParaxialLens(), Sys());
        Assert.Equal(curvedSphere.EffectiveFocalLength, curved.EffectiveFocalLength);
    }

    public static IEnumerable<object[]> FocusingForms() => new[]
    {
        "Even", "Odd", "Extended", "ExtendedOdd", "Qbfs", "Periodic", "OddCosine",
    }.Select(n => new object[] { n });

    /// <summary>A singlet whose front surface gets a large part of its power from the form's
    /// second-order term (no cone terms, so real rays have a focus).</summary>
    private static SurfaceShape FocusingForm(string name) => name switch
    {
        "Even" => new EvenAsphere(new[] { 6.0, 800 }),
        "Odd" => new OddAsphere(new[] { 0, 6.0, 50 }),
        "Extended" => new ExtendedAsphere(Rn, new[] { 6.0 * Rn * Rn, -3e-5 }),
        "ExtendedOdd" => new ExtendedOddAsphere(Rn, new[] { 0, 6.0 * Rn * Rn, -3e-5 }),
        "Qbfs" => new QTypeAsphere(QTypeKind.Qbfs, Rn, new[] { 4e-4, -1e-5, 4e-6 }),
        "Periodic" => new PeriodicSurface(2e-5, 0, 300),
        "OddCosine" => new OddCosineSurface(new[] { 0, 6.0 }, Rc, new[] { new OddCosineTerm(1e-4, 2, 2, 0.4) }),
        _ => throw new ArgumentException(name),
    };

    /// <summary>
    /// Real marginal rays in the y meridian cross the axis at the paraxial focus in the limit
    /// of small height. With L(h) the real crossing behind the last surface minus the
    /// paraxial back focal length, L → 0 as h → 0 (as h² for even terms, as h for an r³ term,
    /// whose slope 3β3h² adds an angle ∝ h² and so a longitudinal shift ∝ h). Halving h must
    /// therefore shrink |L| by at least a factor 1/0.6 each time; a paraxial curvature that
    /// missed the form's second-order term (several m⁻¹ here) would leave L at millimetres.
    /// </summary>
    [Theory]
    [MemberData(nameof(FocusingForms))]
    public void RealMarginalRaysAtSmallHeight_ConvergeToTheParaxialFocus(string name)
    {
        var shape = FocusingForm(name);
        var lens = Lens(Front(shape, c: C0, k: 0));
        var paraxial = ParaxialSystem.Create(lens.ToParaxialLens(), Sys());
        double bfl = paraxial.BackFocalLength!.Value;
        double baseCurvature = shape.ParaxialEquivalent(lens.Surfaces[1]).Curvature;
        Assert.True(baseCurvature - C0 > 3, $"{name}: the form must add real power ({baseCurvature - C0} m⁻¹)");
        var trace = SequentialTrace.Create(lens, Sys());
        double previous = double.NaN;
        var ls = new List<double>();
        for (int k = 0; k < 8; k++)
        {
            double h = 2e-4 / Math.Pow(2, k);
            var path = trace.TracePath(trace.NewRay(new Vector3D(0, h, 0), Vector3D.UnitZ));
            Assert.True(path.Final.IsOk, $"{name}: ray at {h} m: {path.Final.Status}");
            var last = path.Hits[2];
            double crossing = last.Position.Z - last.Position.Y * last.Direction.Z / last.Direction.Y;
            double l = crossing - bfl;
            _out.WriteLine($"{name}: h = {h:E2} m, L = {l:E3} m");
            if (k > 0) Assert.True(Math.Abs(l) <= 0.6 * Math.Abs(previous), $"{name}: L({h}) = {l} after {previous}");
            previous = l;
            ls.Add(l);
        }
        // Aitken's Δ² on the last three heights, L0 = (L₅L₇ − L₆²)/(L₅ + L₇ − 2L₆), is exactly
        // the limit of a sequence L0 + a·qᵏ. Here L = a·h + b·h² (r³ term) or a·h² + b·h⁴, and
        // the second term leaves a residue of order b·h² at h ≈ 1.6–6 µm: about 1e-10 m for the
        // r³ forms (a residue of 1.7e-9 m with the last height four times larger, falling as
        // h²), far less for the others. A paraxial curvature off by δc moves the focus by about
        // (n − 1)f²·δc ≈ 2.5e-3 m²·δc, so the 2e-9 m bound stands for δc ≈ 1e-6 m⁻¹.
        double l5 = ls[5], l6 = ls[6], l7 = ls[7];
        double limit = (l5 * l7 - l6 * l6) / (l5 + l7 - 2 * l6);
        _out.WriteLine($"{name}: extrapolated L0 = {limit:E3} m");
        Assert.True(Math.Abs(limit) <= 2e-9, $"{name}: extrapolated focus error {limit} m");
    }

    /// <summary>A form without a second-order term gives the paraxial engine exactly what the
    /// Standard surface gives it.</summary>
    [Fact]
    public void LensWithoutAnR2Term_IsUnchangedBitForBit()
    {
        var shapes = new SurfaceShape[]
        {
            new EvenAsphere(new[] { 0, 800, -2e5 }),
            new EvenAsphere(Array.Empty<double>()),
            new OddAsphere(new[] { 1e-3, 0, 50 }),
            new ExtendedAsphere(Rn, new[] { 0, -3e-5 }),
            new ExtendedOddAsphere(Rn, new[] { 2e-5, 0, -3e-5 }),
            new QTypeAsphere(QTypeKind.Qcon, Rn, new[] { 2e-5, -1e-5 }),
            new QTypeAsphere(QTypeKind.Qbfs, Rn, Array.Empty<double>()),
            new SuperconicSurface(new[] { 50.0 }, new[] { 2e3 }),
            new PeriodicSurface(2e-6, 150, 0),
            new OddCosineSurface(new[] { 1e-3, 0, 10 }, Rc, new[] { new OddCosineTerm(1e-7, 3.5, 1, -0.2) }),
            new AlternateEvenAsphere(new[] { 0, 800.0 }),
        };
        foreach (double c in new[] { C0, -C0, 0.0, -0.0 })
        {
            var standard = Lens(Front(null, c: c)).ToParaxialLens();
            var reference = ParaxialSystem.Create(standard, Sys());
            foreach (var shape in shapes)
            {
                var paraxial = Lens(Front(shape, c: c)).ToParaxialLens();
                Assert.Equal(BitConverter.DoubleToInt64Bits(standard.Surfaces[1].Curvature),
                    BitConverter.DoubleToInt64Bits(paraxial.Surfaces[1].Curvature));
                var model = ParaxialSystem.Create(paraxial, Sys());
                Assert.Equal(reference.EffectiveFocalLength, model.EffectiveFocalLength);
                Assert.Equal(reference.BackFocalLength, model.BackFocalLength);
                Assert.Equal(reference.EntrancePupilPosition, model.EntrancePupilPosition);
            }
        }
    }

    [Fact]
    public void OddCosineTermWithoutAVertexCurvature_IsRefusedNamingTheSurfaceAndTheTerm()
    {
        var shape = new OddCosineSurface(new[] { 0.0 }, Rc, new[] { new OddCosineTerm(0, 1.5, 1, 0), new OddCosineTerm(1e-6, 1.5, 2, 0) });
        var e = Assert.Throws<ArgumentException>(() => Lens(Front(shape)).ToParaxialLens());
        Assert.Contains("Surface 1", e.Message);
        Assert.Contains("cosine term 2", e.Message);
        // The same term with R ≤ 0 is switched off ("If R is zero or negative, the cosine terms
        // are ignored", OpticStudio help, "Odd Cosine") and is accepted.
        var off = new OddCosineSurface(new[] { 0.0 }, 0, new[] { new OddCosineTerm(1e-6, 1.5, 2, 0) });
        Assert.Equal(C0, off.ParaxialEquivalent(Front(off)).Curvature);
    }
}
