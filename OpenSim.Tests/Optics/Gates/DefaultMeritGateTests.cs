using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.Sampling;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// The OP-56 gate (docs/Optics-Gates.md): the Optimization Wizard's rows, evaluated by the merit
/// engine, give (1) the quadrature RMS, equal to a dense-grid RMS on an unvignetted lens and exact
/// to the rule's order on a polynomial aberration, and (2) OP-40's RMS analysis value, by the
/// relation MF² = Σ_f w_f Σ_λ w_λ Σ_i w_i e² / Σ_f w_f Σ_λ w_λ (DefaultMeritWizard). Every
/// expected value is derived in the comments or computed by OP-40's RMS core; the lenses are
/// arbitrary inputs.
/// </summary>
public sealed class DefaultMeritGateTests
{
    private static SequentialSurface S(double radius, double thickness, double n = 1, bool stop = false) => new()
    {
        Curvature = OpticsConventions.CurvatureFromRadius(radius), Thickness = thickness,
        Medium = ParaxialMedium.Constant(n), IsStop = stop,
    };

    private static SequentialLens Lens(params SequentialSurface[] surfaces) => new()
    {
        Surfaces = new[] { new SequentialSurface { Thickness = double.PositiveInfinity } }.Concat(surfaces).Append(new SequentialSurface()).ToArray()
    };

    /// <summary>OP-40's gate singlet: F/3.2, no apertures (BFL ≈ 64.2 mm).</summary>
    internal static SequentialLens Singlet() => Lens(S(0.05, 0.006, 1.6, stop: true), S(-0.2, 0.064));

    internal static OpticalSystemData Sys(double epd, double[] angles, double[]? fieldWeights = null, double[]? micrometers = null, double[]? waveWeights = null) => new()
    {
        Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
        Fields = new FieldSet
        {
            Type = FieldType.Angle,
            Points = angles.Select((a, i) => new FieldPoint { Y = a, Weight = fieldWeights?[i] ?? 1 }).ToArray(),
        },
        Wavelengths = micrometers is null ? new WavelengthSet()
            : new WavelengthSet
            {
                Wavelengths = micrometers.Select((m, i) => new Wavelength { Micrometers = m, Weight = waveWeights?[i] ?? 1 }).ToArray(),
                PrimaryIndex = (micrometers.Length - 1) / 2,
            },
    };

    /// <summary>The merit value of the wizard's rows on one configuration.</summary>
    internal static double Merit(SequentialLens lens, OpticalSystemData system, DefaultMeritOptions options, out DefaultMeritResult result)
    {
        var configurations = new SingleMeritConfiguration(lens, system);
        result = DefaultMeritWizard.Generate(configurations, 1, options);
        var evaluation = MeritEvaluator.Evaluate(new MeritFunction(result.Rows), configurations);
        Assert.False(evaluation.HasFailures, string.Join(Environment.NewLine, evaluation.Errors));
        return evaluation.Value;
    }

    /// <summary>OP-40's RMS core at one field (degrees) over <paramref name="rule"/>: meters for a
    /// spot, waves for the wavefront.</summary>
    internal static double Rms(SequentialLens lens, OpticalSystemData system, double fieldDegrees, int[] rows, bool polychromatic,
        RmsQuantity quantity, RmsReference reference, PupilQuadratureRule rule)
    {
        var setup = RmsCore.Prepare(lens, system, false, "gate", new List<string>(), default);
        var point = new RmsPoint { FieldX = 0, FieldY = fieldDegrees, Rows = rows, Polychromatic = polychromatic, Label = "gate" };
        var v = RmsCore.Evaluate(setup, point, quantity, reference, rule, default);
        Assert.True(v.Succeeded, v.Failure);
        return v.Value;
    }

    private static DefaultMeritOptions Options(MeritWizardCriterion criterion, MeritWizardReference reference, int rings = 3, int arms = 12) => new()
    {
        Criterion = criterion, Reference = reference, Rings = rings, Arms = arms, AssumeAxialSymmetry = false,
    };

    private static (RmsQuantity Quantity, RmsReference Reference) RmsOf(MeritWizardCriterion c, MeritWizardReference r) => (
        c switch
        {
            MeritWizardCriterion.Wavefront => RmsQuantity.WavefrontRms,
            MeritWizardCriterion.SpotX => RmsQuantity.SpotX,
            MeritWizardCriterion.SpotY => RmsQuantity.SpotY,
            _ => RmsQuantity.SpotRadius,
        },
        r == MeritWizardReference.Centroid ? RmsReference.Centroid : RmsReference.ChiefRay);

    /// <summary>A spot RMS in the lens unit (mm), as the rows give it; waves as they are.</summary>
    private static double InRowUnits(RmsQuantity q, double value, OpticalSystemData system) =>
        RmsCore.IsSpot(q) ? OpticsConventions.FromMeters(value, system.Units.LensUnit) : value;

    private static void Relative(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual / expected - 1) <= tolerance, $"{what}: expected {expected:R}, got {actual:R} (relative {actual / expected - 1:E2}).");

    // ---- (2) The rows reproduce OP-40's RMS ----

    [Theory]
    [InlineData(MeritWizardCriterion.SpotRadius, MeritWizardReference.Centroid, 0.0)]
    [InlineData(MeritWizardCriterion.SpotRadius, MeritWizardReference.Centroid, 10.0)]
    [InlineData(MeritWizardCriterion.SpotRadius, MeritWizardReference.ChiefRay, 10.0)]
    [InlineData(MeritWizardCriterion.SpotX, MeritWizardReference.Centroid, 10.0)]
    [InlineData(MeritWizardCriterion.SpotY, MeritWizardReference.ChiefRay, 10.0)]
    [InlineData(MeritWizardCriterion.Wavefront, MeritWizardReference.Centroid, 10.0)]
    [InlineData(MeritWizardCriterion.Wavefront, MeritWizardReference.ChiefRay, 10.0)]
    [InlineData(MeritWizardCriterion.Wavefront, MeritWizardReference.ChiefRay, 0.0)]
    public void OneFieldOneWavelength_TheMeritValueIsTheRmsAnalysisValue(MeritWizardCriterion criterion, MeritWizardReference reference, double field)
    {
        // One field and one wavelength, so MF² = Σ wᵢ eᵢ² / Σ wᵢ with the rule's weights (summing
        // to 1): the RMS itself. OP-40's core over the same rule (3 rings × 12 arms, its default
        // 4n arms) is the independent value; both trace the same rays, so they agree to the
        // rounding of the sums.
        var lens = Singlet();
        var system = Sys(0.02, new[] { field }, micrometers: new[] { 0.55 });
        double merit = Merit(lens, system, Options(criterion, reference), out var result);
        Assert.Equal(PupilIntegration.GaussianQuadrature, result.Integration);
        var (q, r) = RmsOf(criterion, reference);
        double rms = InRowUnits(q, Rms(lens, system, field, new[] { 0 }, false, q, r, PupilQuadrature.Gaussian(3, 12)), system);
        Relative(rms, merit, 1e-9, $"{criterion}, {reference}, {field}°");
    }

    [Fact]
    public void FieldAndWavelengthWeights_GiveTheWeightedMeanSquareOfThePolychromaticRms()
    {
        // MF² = Σ_f w_f Σ_λ w_λ Σᵢ wᵢ rᵢ² / (Σ_f w_f · Σ_λ w_λ). With one centroid per field over
        // every wavelength (lateral colour counted), Σ_λ w_λ Σᵢ wᵢ rᵢ² / Σ_λ w_λ is OP-40's
        // polychromatic RMS² of that field, so MF² = Σ_f w_f R_f² / Σ_f w_f.
        var lens = Singlet();
        double[] fields = { 0, 7, 10 }, fieldWeights = { 1, 3, 2 };
        var system = Sys(0.02, fields, fieldWeights, new[] { 0.486, 0.588, 0.656 }, new[] { 1.0, 2, 1 });
        double merit = Merit(lens, system, Options(MeritWizardCriterion.SpotRadius, MeritWizardReference.Centroid), out _);
        var rule = PupilQuadrature.Gaussian(3, 12);
        double sum = 0;
        for (int f = 0; f < fields.Length; f++)
        {
            double r = InRowUnits(RmsQuantity.SpotRadius, Rms(lens, system, fields[f], new[] { 0, 1, 2 }, true, RmsQuantity.SpotRadius, RmsReference.Centroid, rule), system);
            sum += fieldWeights[f] * r * r;
        }
        Relative(Math.Sqrt(sum / fieldWeights.Sum()), merit, 1e-9, "weighted polychromatic RMS");
    }

    [Fact]
    public void IgnoringLateralColour_GivesTheWeightedMeanSquareOfTheMonochromaticRms()
    {
        // One centroid per field and wavelength: MF² = Σ_f Σ_λ w_f w_λ R²(f, λ) / (Σ_f w_f · Σ_λ w_λ),
        // R(f, λ) OP-40's monochromatic RMS.
        var lens = Singlet();
        double[] fields = { 0, 10 }, fieldWeights = { 1, 2 }, waveWeights = { 1, 3 };
        var system = Sys(0.02, fields, fieldWeights, new[] { 0.5, 0.6 }, waveWeights);
        double merit = Merit(lens, system, Options(MeritWizardCriterion.SpotRadius, MeritWizardReference.Centroid) with { IgnoreLateralColor = true }, out _);
        var rule = PupilQuadrature.Gaussian(3, 12);
        double sum = 0;
        for (int f = 0; f < 2; f++)
            for (int w = 0; w < 2; w++)
            {
                double r = InRowUnits(RmsQuantity.SpotRadius, Rms(lens, system, fields[f], new[] { w }, false, RmsQuantity.SpotRadius, RmsReference.Centroid, rule), system);
                sum += fieldWeights[f] * waveWeights[w] * r * r;
            }
        Relative(Math.Sqrt(sum / (fieldWeights.Sum() * waveWeights.Sum())), merit, 1e-9, "per-wavelength RMS");
    }

    [Fact]
    public void SpotXPlusY_WithEqualRelativeWeights_IsTheRadialRmsOverRootTwo()
    {
        // x and y rows of equal weight: MF² = Σ wᵢ(xᵢ² + yᵢ²) / (2 Σ wᵢ) = R²/2.
        var lens = Singlet();
        var system = Sys(0.02, new[] { 10.0 }, micrometers: new[] { 0.55 });
        double xy = Merit(lens, system, Options(MeritWizardCriterion.SpotXY, MeritWizardReference.Centroid), out _);
        double radius = Merit(lens, system, Options(MeritWizardCriterion.SpotRadius, MeritWizardReference.Centroid), out _);
        Relative(radius / Math.Sqrt(2), xy, 1e-12, "x + y");
    }

    [Theory]
    [InlineData(MeritWizardCriterion.SpotRadius)]
    [InlineData(MeritWizardCriterion.SpotX)]
    [InlineData(MeritWizardCriterion.Wavefront)]
    public void AssumingAxialSymmetry_HalvesTheRows_AndKeepsTheValue(MeritWizardCriterion criterion)
    {
        // On a rotationally symmetric lens a field on the y axis is mirror-symmetric in x, so the
        // points with Px > 0 at twice their weight stand for their mirror images; on axis every
        // point of a ring has the same radial value. The value changes only by rounding.
        var lens = Singlet();
        var system = Sys(0.02, new[] { 0.0, 10 }, micrometers: new[] { 0.55 });
        var options = Options(criterion, MeritWizardReference.Centroid);
        double full = Merit(lens, system, options, out var all);
        double half = Merit(lens, system, options with { AssumeAxialSymmetry = true }, out var halved);
        Relative(full, half, 1e-9, $"{criterion} symmetric");
        int Count(DefaultMeritResult r) => r.Rows.Count(row => row.Code != "BLNK");
        Assert.Equal(2 * 36, Count(all));                        // 2 fields × 3 rings × 12 arms
        // 12 arms from +x every 30°: Px > 0 for 0°, ±30°, ±60° (5 arms), Px = 0 for ±90° (2);
        // on axis the radial criteria keep one point per ring.
        int axis = criterion == MeritWizardCriterion.SpotX ? 3 * 7 : 3;
        Assert.Equal(axis + 3 * 7, Count(halved));
    }

    // ---- (1) The quadrature RMS against a dense grid, and exact to its order ----

    [Theory]
    [InlineData(MeritWizardCriterion.SpotRadius, MeritWizardReference.Centroid)]
    [InlineData(MeritWizardCriterion.SpotRadius, MeritWizardReference.ChiefRay)]
    [InlineData(MeritWizardCriterion.Wavefront, MeritWizardReference.Centroid)]
    [InlineData(MeritWizardCriterion.Wavefront, MeritWizardReference.ChiefRay)]
    public void TheQuadratureRms_EqualsADenseGridRms_OnAnUnvignettedLens(MeritWizardCriterion criterion, MeritWizardReference reference)
    {
        // OP-40's gate singlet, unvignetted, on axis and at 10°: the wizard's rows (3 rings ×
        // 12 arms, exact to degree 11) against OP-40's RMS analysis on a 301 × 301 rectangular
        // grid. The grid's own error bound is OP-40's (RmsGateTests: 2·10⁻⁴ to 4·10⁻⁴ at 301 across
        // on smooth model aberrations; 2·10⁻³ allows five times that), and OP-40's gate shows the
        // quadrature has converged to 10⁻⁹ by 10 rings; three rings leave the seventh-order and
        // higher terms of this F/3.2 singlet, far below the grid's error.
        var lens = Singlet();
        var (q, r) = RmsOf(criterion, reference);
        foreach (double field in new[] { 0.0, 10 })
        {
            var system = Sys(0.02, new[] { field }, micrometers: new[] { 0.55 });
            double merit = Merit(lens, system, Options(criterion, reference), out var result);
            Assert.Equal(PupilIntegration.GaussianQuadrature, result.Integration);
            double grid = InRowUnits(q, Rms(lens, system, field, new[] { 0 }, false, q, r, PupilQuadrature.Rectangular(301)), system);
            Relative(grid, merit, 2e-3, $"{criterion}, {reference}, {field}°: quadrature against the 301 × 301 grid");
        }
    }

    [Fact]
    public void OnAPolynomialAberration_OneRingIsExact_AndTheDenseGridOnlyApproaches()
    {
        // An ideal (Paraxial) lens of focal length f at infinite conjugates, the stop on it, the
        // image plane δ past the focus. The help's refraction n′u′ = nu − yφ makes every ray leave
        // with slope tanθ − y/f, so at the image plane y′ = y + (f + δ)(tanθ − y/f) and the error
        // against the chief ray (y = 0) is −δ·(x, y)/f: linear in the pupil, (x, y) = (D/2)(Px, Py)
        // with the stop on the lens. Its centroid is the chief ray, and the RMS radius is
        // (δ/f)(D/2)·√⟨ρ²⟩ = (δ/f)(D/2)/√2 at every field. The squared error has degree 2, within
        // one ring × 6 arms' exact degree min(4·1 − 1, 6 − 1) = 3 (PupilQuadrature), so the rows
        // give it to rounding; the 301 grid only within its discretisation error.
        double f = 0.1, delta = 0.002, epd = 0.02;
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface { Thickness = f + delta, IsStop = true, Shape = new ParaxialShape(f) },
                new SequentialSurface(),
            },
        };
        var system = Sys(epd, new[] { 0.0, 10 }, micrometers: new[] { 0.55 });
        double exact = OpticsConventions.FromMeters(delta / f * (epd / 2) / Math.Sqrt(2), system.Units.LensUnit);
        foreach (var reference in new[] { MeritWizardReference.Centroid, MeritWizardReference.ChiefRay })
        {
            double merit = Merit(lens, system, Options(MeritWizardCriterion.SpotRadius, reference, rings: 1, arms: 6), out _);
            Relative(exact, merit, 1e-10, $"one ring, {reference}");
        }
        double grid = InRowUnits(RmsQuantity.SpotRadius,
            Rms(lens, system, 10, new[] { 0 }, false, RmsQuantity.SpotRadius, RmsReference.Centroid, PupilQuadrature.Rectangular(301)), system);
        Relative(exact, grid, 2e-3, "301 grid");
    }
}
