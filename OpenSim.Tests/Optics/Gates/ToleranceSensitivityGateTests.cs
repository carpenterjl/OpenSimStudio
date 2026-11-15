using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-64: the singlet the tolerance-run tests share, in millimeters with a constant index (no
/// glass catalog), and its third-order spherical aberration in closed form. The radii, thickness,
/// index, aperture and wavelength are arbitrary test inputs, not a design from a source.
/// </summary>
internal static class ToleranceRunFixtures
{
    public static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    public const double Index = 1.5;
    public const double R1 = 100;
    public const double R2 = -100;
    public const double CentreThickness = 5;
    public const double EntrancePupil = 4;
    public const double Micrometers = 0.55;

    /// <summary>A number and its derivative (forward-mode automatic differentiation): the
    /// derivative of a closed form evaluated with it is exact to rounding.</summary>
    public readonly record struct Dual(double V, double D)
    {
        public static implicit operator Dual(double v) => new(v, 0);
        public static Dual operator +(Dual a, Dual b) => new(a.V + b.V, a.D + b.D);
        public static Dual operator -(Dual a, Dual b) => new(a.V - b.V, a.D - b.D);
        public static Dual operator -(Dual a) => new(-a.V, -a.D);
        public static Dual operator *(Dual a, Dual b) => new(a.V * b.V, a.D * b.V + a.V * b.D);
        public static Dual operator /(Dual a, Dual b) => new(a.V / b.V, (a.D * b.V - a.V * b.D) / (b.V * b.V));
    }

    /// <summary>
    /// The Seidel spherical-aberration sum S1 (mm) of the singlet with front radius
    /// <paramref name="r1"/>, object at infinity, stop on the front surface, and the paraxial back
    /// focal distance −y₂/u₂ (mm). Closed form, from the paraxial marginal ray (n′u′ = nu − yc(n′ − n),
    /// y₂ = y₁ + t·u₁) and the surface sum S1 = −Σ A² y Δ(u/n), A = n(u + yc), Δ(u/n) = u′/n′ − u/n:
    /// the formulas OpenSim.Optics' SeidelEngine documents from RayOptics 0.9.10
    /// (rayoptics/parax/thirdorder.py, compute_third_order_and_color), written out here for two
    /// surfaces.
    /// </summary>
    public static (Dual S1, double BackFocus) Seidel(Dual r1)
    {
        double n0 = 1, n1 = Index, n2 = 1, y1 = EntrancePupil / 2, u0 = 0;
        Dual c1 = 1 / r1;
        double c2 = 1 / R2;
        Dual u1 = (n0 * u0 - y1 * c1 * (n1 - n0)) / n1;
        Dual a1 = n0 * (u0 + y1 * c1);
        Dual s = -(a1 * a1) * y1 * (u1 / n1 - u0 / n0);
        Dual y2 = y1 + CentreThickness * u1;
        Dual u2 = (n1 * u1 - y2 * c2 * (n2 - n1)) / n2;
        Dual a2 = n1 * (u1 + y2 * c2);
        s = s - (a2 * a2) * y2 * (u2 / n2 - u1 / n1);
        return (s, -(y2.V / u2.V));
    }

    /// <summary>
    /// The RMS wavefront error (waves) of pure third-order spherical aberration W = W040ρ⁴ about
    /// the paraxial focus, piston and tilt removed, and its derivative in R1. Derived: over the
    /// unit disk ⟨ρ⁴⟩ = 1/3 and ⟨ρ⁸⟩ = 1/5, so the RMS is |W040|·√(1/5 − 1/9) = |W040|·2/(3√5); the
    /// tilt fit of a rotationally symmetric W is zero. W040 = S1/8 (OpticStudio 2025 R2 help,
    /// "Seidel Coefficients": "S1 = 8 W040", saved under plans/sources/optics/op-36-help/).
    /// </summary>
    public static Dual RmsWavefront(Dual r1)
    {
        var (s1, _) = Seidel(r1);
        double waveMm = Micrometers * 1e-3;
        double k = Math.Sign(s1.V) * 2 / (3 * Math.Sqrt(5)) / (8 * waveMm);
        return new Dual(k * s1.V, k * s1.D);
    }

    /// <summary>The singlet with its image at the closed-form paraxial focus (one on-axis field,
    /// one wavelength).</summary>
    public static OpticsSettings Singlet(double r1 = R1) => new()
    {
        SystemData = new OpticsSystemSettings
        {
            ApertureValue = EntrancePupil,
            Fields = new() { new OpticsFieldSettings() },
            Wavelengths = new() { new OpticsWavelengthSettings { Micrometers = Micrometers } },
        },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = r1, Thickness = CentreThickness, Index = Index, IsStop = true },
            new() { Radius = R2, Thickness = Seidel(r1).BackFocus },
            new(),
        },
    };

    public static ToleranceOperandRow Row(string code, int i1 = 0, int i2 = 0, double min = 0, double max = 0) =>
        new() { Code = code, Int1 = i1, Int2 = i2, Min = min, Max = max };

    public static ToleranceTable Table(params ToleranceOperandRow[] rows) => new(rows);

    /// <summary>RMS wavefront, paraxial focus, the system's one field, 6 rays per arm.</summary>
    public static ToleranceRunOptions Wavefront => new()
    {
        Criterion = ToleranceCriterion.RmsWavefront, Compensation = ToleranceCompensation.ParaxialFocus,
        Fields = ToleranceFieldSet.UserDefined, Sampling = 6,
    };
}

/// <summary>
/// OP-64's gate (plan): "On a singlet with a back-focus compensator, a radius tolerance's
/// sensitivity equals the analytic derivative of spherical aberration with bending; inverse limits
/// reproduce the requested criterion change."
/// </summary>
public sealed class ToleranceSensitivityGateTests
{
    private static double Relative(double expected, double actual) => Math.Abs(actual - expected) / Math.Abs(expected);

    /// <summary>
    /// A TRAD tolerance of ±0.5 mm on the front radius of an equiconvex singlet, criterion RMS
    /// wavefront, paraxial focus compensator. The run's sensitivity (P(+δ) − P(−δ))/2δ must equal
    /// the derivative in R1 of the closed-form third-order RMS (<see cref="ToleranceRunFixtures.RmsWavefront"/>).
    /// <para>
    /// Why the paraxial focus compensator makes this the derivative of spherical aberration alone:
    /// it holds the image at each perturbed lens's paraxial focus, so the defocus term is zero and
    /// the on-axis wavefront is W040ρ⁴ plus higher orders.
    /// </para>
    /// <para>
    /// Tolerances (1 % relative on the nominal and the slope). The aperture is F/25 (y/f ≈ 0.02),
    /// so fifth-order spherical aberration is of order (y/f)² ≈ 4e-4 of the third, a few times that
    /// with its coefficient. The quadrature (6 rays per arm, 12 arms) integrates ρ⁴ and ρ⁶ terms
    /// and their squares exactly. The central difference over ±0.5 % of R1 errs by about
    /// (δ/R1)² ≈ 2.5e-5 relative. The criterion is about 1e-9 m and its change over the
    /// tolerance about 1e-11 m, against path rounding of order 1e-16 m.
    /// </para>
    /// </summary>
    [Fact]
    public void ARadiusTolerancesSensitivity_IsTheDerivativeOfSphericalAberrationWithBending()
    {
        const double delta = 0.5;
        var result = ToleranceAnalysis.Run(ToleranceRunFixtures.Singlet(), ToleranceRunFixtures.Table(ToleranceRunFixtures.Row("TRAD", 1, 0, -delta, delta)),
            ToleranceRunFixtures.NoCatalogs, ToleranceRunFixtures.Wavefront);
        var analytic = ToleranceRunFixtures.RmsWavefront(new ToleranceRunFixtures.Dual(ToleranceRunFixtures.R1, 1));
        Assert.True(Relative(analytic.V, result.Nominal) < 0.01, $"nominal {result.Nominal:R} waves against third order {analytic.V:R}");

        var row = Assert.Single(result.Rows);
        Assert.True(row.AtMin.Succeeded, row.AtMin.Failure);
        Assert.True(row.AtMax.Succeeded, row.AtMax.Failure);
        double slope = (row.AtMax.Criterion - row.AtMin.Criterion) / (2 * delta);
        Assert.True(Relative(analytic.D, slope) < 0.01, $"sensitivity {slope:R} waves/mm against the derivative {analytic.D:R}");
        Assert.Equal(row.AtMax.Criterion - result.Nominal, row.AtMax.Change);   // linear difference
        Assert.Single(result.Compensators);
    }

    /// <summary>
    /// Inverse Increment and Inverse Limit on a ±2 mm TRAD: the increment is half the larger
    /// change the starting limits make, so that side must be tightened and, run again in Sensitivity
    /// mode at the new limit, change the criterion by the increment; a side whose change is already
    /// within it is kept. Inverse Limit with Limit = N + increment must give the same limits.
    /// <para>
    /// Tolerances. Brent's method brackets the limit to 1e-7 of its start, which moves the
    /// change by about 1e-7 of itself times the slope's logarithmic order (≈ 2); the criterion's
    /// rounding is about 1e-5 of the change (see the sensitivity test). The test allows 1e-3 for the
    /// change and 1e-5 between the two inverse modes, which search the same function to the same
    /// target (only the target's rounding differs).
    /// </para>
    /// </summary>
    [Fact]
    public void InverseLimits_ReproduceTheRequestedCriterionChange()
    {
        var lens = ToleranceRunFixtures.Singlet();
        var table = ToleranceRunFixtures.Table(ToleranceRunFixtures.Row("TRAD", 1, 0, -2, 2));
        var o = ToleranceRunFixtures.Wavefront;
        var sensitivity = ToleranceAnalysis.Run(lens, table, ToleranceRunFixtures.NoCatalogs, o);
        var start = sensitivity.Rows[0];
        double larger = Math.Max(start.AtMin.Change, start.AtMax.Change);
        Assert.True(larger > 0, "one side of a radius change away from the best bending must worsen the wavefront");
        double increment = larger / 2;

        var inverse = ToleranceAnalysis.Run(lens, table, ToleranceRunFixtures.NoCatalogs, o with { Mode = ToleranceMode.InverseIncrement, Increment = increment });
        var r = inverse.Rows[0];
        Assert.True(r.Adjusted);
        var check = ToleranceAnalysis.Run(lens, inverse.Table, ToleranceRunFixtures.NoCatalogs, o).Rows[0];
        foreach (var (side, before, after, limit) in new[] { (ToleranceSide.Min, start.AtMin, check.AtMin, r.Min), (ToleranceSide.Max, start.AtMax, check.AtMax, r.Max) })
        {
            double startLimit = side == ToleranceSide.Min ? -2 : 2;
            if (before.Change > increment)
            {
                Assert.True(Math.Abs(limit) < Math.Abs(startLimit) && Math.Sign(limit) == Math.Sign(startLimit), $"{side}: {limit:R} must be tightened from {startLimit}");
                Assert.True(Relative(increment, after.Change) < 1e-3, $"{side}: change {after.Change:R} at the new limit against the increment {increment:R}");
            }
            else Assert.Equal(startLimit, limit);   // never loosened, not tightened when within
        }

        var byLimit = ToleranceAnalysis.Run(lens, table, ToleranceRunFixtures.NoCatalogs,
            o with { Mode = ToleranceMode.InverseLimit, Limit = sensitivity.Nominal + increment });
        Assert.True(Relative(r.Min, byLimit.Rows[0].Min) < 1e-5 || r.Min == byLimit.Rows[0].Min);
        Assert.True(Relative(r.Max, byLimit.Rows[0].Max) < 1e-5 || r.Max == byLimit.Rows[0].Max);
    }
}
