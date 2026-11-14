using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>Lenses and helpers of the OP-36 tests, in meters. The lenses are arbitrary test inputs,
/// not reference designs.</summary>
internal static class SeidelFixtures
{
    public static SequentialSurface S(double radius, double thickness, double n = 1, double k = 0, bool stop = false,
        bool mirror = false, SurfaceShape? shape = null, ParaxialMedium? medium = null) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = k, Thickness = thickness,
        Medium = medium ?? ParaxialMedium.Constant(n), IsStop = stop, IsMirror = mirror, Shape = shape,
    };

    public static SequentialLens Lens(double objectDistance, params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = objectDistance } }
            .Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    /// <summary>Angle fields in degrees along +y.</summary>
    public static OpticalSystemData Sys(double epd, double[] fields, double[]? micrometers = null, int primary = 0) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet { Type = FieldType.Angle, Points = fields.Select(f => new FieldPoint { Y = f }).ToArray() },
        Wavelengths = micrometers is null ? new WavelengthSet()
            : new WavelengthSet { Wavelengths = micrometers.Select(m => new Wavelength { Micrometers = m }).ToArray(), PrimaryIndex = primary },
    };

    /// <summary>The lens with its last thickness set to the paraxial back focal length, so the
    /// image surface is the paraxial image of an object at infinity.</summary>
    public static SequentialLens AtParaxialFocus(SequentialLens lens, OpticalSystemData system)
    {
        var p = ParaxialSystem.Create(lens.ToParaxialLens(), system);
        var s = lens.Surfaces.ToArray();
        s[^2] = s[^2] with { Thickness = p.BackFocalLength!.Value };
        return lens with { Surfaces = s };
    }

    /// <summary>A singlet in air (R1 = 50 mm, 5 mm of n = 1.5, R2 = −200 mm, front conic −0.7),
    /// stop on the front surface, image at the paraxial focus.</summary>
    public static SequentialLens Singlet(OpticalSystemData system, double frontConic = -0.7) =>
        AtParaxialFocus(Lens(double.PositiveInfinity, S(0.05, 0.005, 1.5, frontConic, stop: true), S(-0.2, 0.1)), system);

    public static OpticsAnalysisContext Context(OpticalSystemData system) =>
        new("P", new OpticsSettings { SystemData = SystemDataSettings.ToSettings(system) });

    public static void Close(double expected, double actual, double tolerance, string what)
    {
        Assert.True(Math.Abs(expected - actual) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:g3}, tolerance {tolerance:g3}).");
    }

    /// <summary>Least-squares e = a₁P + a₃P³ + a₅P⁵ + a₇P⁷ by the normal equations.</summary>
    public static double[] FitOdd(IReadOnlyList<double> p, IReadOnlyList<double> e)
    {
        const int k = 4;
        var m = new double[k, k];
        var b = new double[k];
        for (int i = 0; i < p.Count; i++)
        {
            var phi = Enumerable.Range(0, k).Select(j => Math.Pow(p[i], 2 * j + 1)).ToArray();
            for (int j = 0; j < k; j++)
            {
                b[j] += phi[j] * e[i];
                for (int q = 0; q < k; q++) m[j, q] += phi[j] * phi[q];
            }
        }
        for (int c = 0; c < k; c++)
            for (int r = c + 1; r < k; r++)
            {
                double f = m[r, c] / m[c, c];
                for (int q = c; q < k; q++) m[r, q] -= f * m[c, q];
                b[r] -= f * b[c];
            }
        var x = new double[k];
        for (int c = k - 1; c >= 0; c--)
        {
            double s = b[c];
            for (int q = c + 1; q < k; q++) s -= m[c, q] * x[q];
            x[c] = s / m[c, c];
        }
        return x;
    }
}

/// <summary>
/// OP-36's gate: "Thin-lens closed forms: minimum spherical aberration at the bending given by the
/// Coddington factors; a stop at the centre of curvature of a spherical mirror has zero coma and
/// astigmatism; a Ritchey-Chretien built from its closed-form conics has zero third-order
/// spherical and coma; Seidel spherical equals the cubic coefficient fitted to real-ray fans as
/// aperture tends to zero." Expected values are derived in the comments from the tests' own
/// inputs, except the Ritchey-Chrétien conics, which are RayOptics' closed forms (cited there).
/// </summary>
public sealed class SeidelGateTests
{
    private static SeidelResult Seidel(SequentialLens lens, OpticalSystemData system) => SeidelEngine.Compute(lens, system);

    // ---- Coddington bending ----

    /// <summary>
    /// A zero-thickness lens of index n in air with its stop on it, power φ, marginal ray of height
    /// y and object-space slope u₀. Derivation (from the surface sums of SeidelEngine, y = 1):
    /// A₁ = u₀ + c₁, Δ₁ = (u₀ − (n − 1)c₁)/n² − u₀; A₂ = u₀ + c₁ − nφ/(n − 1); Δ₁ + Δ₂ = −φ; so
    /// −S1 = Δ₁(A₁² − A₂²) − φA₂², in which the c₁³ terms cancel: S1 is a quadratic in c₁. Setting
    /// dS1/dc₁ = 0 gives c₁ = n(2n + 1)φ/(2(n − 1)(n + 2)) − 2(n + 1)u₀/(n + 2). With the shape
    /// factor X = (c₁ + c₂)/(c₁ − c₂) and the position factor Y = (u₀ + u₂)/(u₀ − u₂)
    /// (u₂ = u₀ − φ), this is X = −2(n² − 1)Y/(n + 2), Coddington's minimum-spherical bending.
    /// The test finds the vertex of S1(c₁) from three bendings (exact for a quadratic) and
    /// compares it with that X.
    /// </summary>
    [Theory]
    [InlineData(double.PositiveInfinity, 1.5)]
    [InlineData(0.3, 1.5)]
    [InlineData(-0.25, 1.8)]
    public void AThinLens_HasItsLeastSphericalAberration_AtTheCoddingtonBending(double objectDistance, double n)
    {
        const double phi = 10, epd = 0.01;   // f = 100 mm
        var system = SeidelFixtures.Sys(epd, new[] { 0.0, 1.0 });
        SequentialLens Bent(double c1)
        {
            double c2 = c1 - phi / (n - 1);
            return SeidelFixtures.Lens(objectDistance,
                new SequentialSurface { Curvature = c1, Thickness = 0, Medium = ParaxialMedium.Constant(n), IsStop = true },
                new SequentialSurface { Curvature = c2, Thickness = 0.1 });
        }
        double S1(double c1) => Seidel(Bent(c1), system).Sum.S1;

        var marginal = ParaxialSystem.Create(Bent(phi).ToParaxialLens(), system).MarginalRay;
        double y = marginal.Heights[1], u0 = marginal.Slopes[0], u2 = marginal.Slopes[2];
        SeidelFixtures.Close(u0 - y * phi, u2, 1e-15, "u₂ = u₀ − yφ");
        double c0 = n * (2 * n + 1) * phi / (2 * (n - 1) * (n + 2)) - 2 * (n + 1) * (u0 / y) / (n + 2);

        double d = phi / 2;
        double sm = S1(c0 - d), s0 = S1(c0), sp = S1(c0 + d);
        double vertex = c0 - d * (sp - sm) / (2 * (sp - 2 * s0 + sm));
        Assert.True(sp - 2 * s0 + sm > 0, "S1 is a quadratic with a minimum in c₁.");
        // Rounding: the S1 values carry ~1e-15 relative error, and the vertex formula divides
        // their difference by the second difference, so the vertex is good to ~1e-14·d.
        SeidelFixtures.Close(c0, vertex, 1e-11 * phi, "c₁ of the least S1");

        double cv2 = vertex - phi / (n - 1);
        double x = (vertex + cv2) / (vertex - cv2), yFactor = (u0 + u2) / (u0 - u2);
        SeidelFixtures.Close(-2 * (n * n - 1) * yFactor / (n + 2), x, 1e-11, "shape factor X = −2(n² − 1)Y/(n + 2)");
    }

    // ---- Spherical mirror with its stop at the centre ----

    /// <summary>
    /// A concave sphere (R = −0.2 m) with a plane stop at its centre of curvature: every chief ray
    /// passes through the centre, so it meets the mirror along a normal, Ā = n(ū + ȳc) = 0, and
    /// S2 = S3 = 0 (and S5 = 0). S1 and S4 do not involve the chief ray's angle of incidence
    /// (S1 = −A²yΔ(u/n), S4 = −H²P with the same H), so they equal those of the same mirror with
    /// its stop on it, whose S2 is not zero.
    /// </summary>
    [Fact]
    public void ASphericalMirror_WithItsStopAtTheCentre_HasNoComaOrAstigmatism()
    {
        const double r = -0.2;
        var system = SeidelFixtures.Sys(0.02, new[] { 0.0, 3.0 });
        var centre = SeidelFixtures.Lens(double.PositiveInfinity,
            SeidelFixtures.S(double.PositiveInfinity, -r, stop: true), SeidelFixtures.S(r, r / 2, mirror: true));
        var onMirror = SeidelFixtures.Lens(double.PositiveInfinity, SeidelFixtures.S(r, r / 2, stop: true, mirror: true));
        var a = Seidel(centre, system).Sum;
        var b = Seidel(onMirror, system).Sum;

        Assert.True(Math.Abs(b.S2) > 1e-9, "the stop on the mirror gives coma");
        SeidelFixtures.Close(0, a.S2, 1e-12 * Math.Abs(b.S2), "S2 with the stop at the centre");
        SeidelFixtures.Close(0, a.S3, 1e-12 * Math.Abs(b.S2), "S3 with the stop at the centre");
        SeidelFixtures.Close(0, a.S5, 1e-12 * Math.Abs(b.S2), "S5 with the stop at the centre");
        SeidelFixtures.Close(b.S1, a.S1, 1e-13 * Math.Abs(b.S1), "S1 does not depend on the stop");
        SeidelFixtures.Close(b.S4, a.S4, 1e-13 * Math.Abs(b.S4), "S4 does not depend on the stop");
    }

    // ---- Ritchey-Chrétien ----

    /// <summary>
    /// Two mirrors, stop on the primary, object at infinity. The conics are RayOptics 0.9.10's
    /// closed forms (Michael J. Hayford, BSD 3-Clause; <c>rayoptics/seq/twoconicmirrors.py</c>,
    /// <c>ritchey_chretien</c>, <c>_mag</c> and <c>_separation_ratio</c>, saved under
    /// plans/sources/optics/pyref-venv): m = ν₁/ν₂ (the reduced slopes after the primary and after
    /// the secondary), s = m·y₂/(y₁ − y₂), k₁ = −2s/m³ − 1, k₂ = −(4m(m − 1) + 2(m + s))/(m − 1)³ − 1.
    /// Those are independent of this program's surface sums, so S1 = S2 = 0 tests the sums and
    /// their aspheric terms on mirrors. Each mirror alone has S1 and S2 far from zero.
    /// </summary>
    [Theory]
    [InlineData(-0.4, -0.15, -0.2)]
    [InlineData(-1.0, -0.35, -0.4)]
    public void ARitcheyChretien_FromItsClosedFormConics_HasNoThirdOrderSphericalOrComa(double r1, double d, double r2)
    {
        var system = SeidelFixtures.Sys(0.04, new[] { 0.0, 0.3 });
        SequentialLens Build(double k1, double k2) => SeidelFixtures.Lens(double.PositiveInfinity,
            SeidelFixtures.S(r1, d, k: k1, stop: true, mirror: true), SeidelFixtures.S(r2, 0.3, k: k2, mirror: true));
        var ray = ParaxialSystem.Create(Build(0, 0).ToParaxialLens(), system).MarginalRay;
        double m = ray.ReducedSlopes[1] / ray.ReducedSlopes[2];
        double s = m * ray.Heights[2] / (ray.Heights[1] - ray.Heights[2]);
        double k1 = -2 * s / (m * m * m) - 1;
        double k2 = -(4 * m * (m - 1) + 2 * (m + s)) / Math.Pow(m - 1, 3) - 1;

        var r = Seidel(Build(k1, k2), system);
        double scale1 = r.Surfaces.Max(x => Math.Abs(x.Terms.S1)), scale2 = r.Surfaces.Max(x => Math.Abs(x.Terms.S2));
        Assert.True(scale1 > 0 && scale2 > 0);
        SeidelFixtures.Close(0, r.Sum.S1, 1e-12 * scale1, "S1 of the Ritchey-Chrétien");
        SeidelFixtures.Close(0, r.Sum.S2, 1e-12 * scale2, "S2 of the Ritchey-Chrétien");
        Assert.True(Math.Abs(r.Sum.S3) > 1e-6 * scale2, "astigmatism remains (an RC corrects only S1 and S2)");
    }

    // ---- Real-ray fans ----

    /// <summary>
    /// TSPH = S1/(2n′u′) is the image-plane error of the marginal ray to third order, so it is the
    /// cubic Taylor coefficient of OP-34's tangential ray fan EY(Py) when the image is at the
    /// paraxial focus. The fan (41 rays) is fitted with odd terms to P⁷; the fit's a₃ differs from
    /// the Taylor coefficient by the neglected a₉, ~a₃·(y/R)⁶ ≲ 1e-10·a₃ for these apertures, and
    /// the trace noise (~1e-17 m) is ~1e-10 of a₃. The aperture is small (EPD 2 mm on f ≈ 80 mm,
    /// and 4 mm on the mirror of radius 200 mm) so the series converges fast.
    /// </summary>
    [Fact]
    public void SeidelSpherical_IsTheCubicCoefficientOfTheRealRayFan()
    {
        // A refracting singlet with a conic front.
        var system = SeidelFixtures.Sys(0.002, new[] { 0.0 });
        var singlet = SeidelFixtures.Singlet(system);
        CheckFan(singlet, system, out _);

        // OP-34's concave spherical mirror (RayFanSpotTests): its fan's a₃ was derived there as
        // −(D/2)³/(2ρ²); TSPH must agree with it too.
        const double r = -0.2, rho = 0.2, epd = 0.004;
        var mirrorSystem = SeidelFixtures.Sys(epd, new[] { 0.0 });
        var mirror = SeidelFixtures.Lens(double.PositiveInfinity, SeidelFixtures.S(r, r / 2, stop: true, mirror: true));
        CheckFan(mirror, mirrorSystem, out double tsph);
        double derived = -Math.Pow(epd / 2, 3) / (2 * rho * rho);
        SeidelFixtures.Close(derived, tsph, 1e-12 * Math.Abs(derived), "TSPH of the mirror against OP-34's derived a₃");
    }

    private static void CheckFan(SequentialLens lens, OpticalSystemData system, out double tsph)
    {
        var r = SeidelEngine.Compute(lens, system);
        tsph = r.Transverse(r.Sum).Spherical;
        var result = new RayAberrationAnalysis(_ => lens).Compute(SeidelFixtures.Context(system),
            new RayAberrationSettings { RaysEachSide = 20 }, default);
        var curve = Assert.Single(Assert.IsType<LinePlot>(result.Plots[0]).Curves);
        var ey = curve.Y.Select(v => v * 1e-6).ToArray();   // µm to m
        var a = SeidelFixtures.FitOdd(curve.X, ey);
        SeidelFixtures.Close(tsph, a[1], 1e-7 * Math.Abs(tsph), "cubic coefficient of EY(Py) against TSPH");
        SeidelFixtures.Close(0, a[0], 1e-7 * Math.Abs(tsph), "linear term (image at the paraxial focus)");
    }
}
