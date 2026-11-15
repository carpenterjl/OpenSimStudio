using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis.EnclosedEnergy;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-41 gate: a perfect circular pupil's encircled energy is 1 − J₀(v)² − J₁(v)², v = π·r/(λF),
/// and the fraction inside the first dark ring is that expression at the first zero of J₁.
/// <para>
/// The closed form is derived in <see cref="AiryEnclosedEnergy"/> (from the Airy intensity
/// (2J₁(v)/v)² and DLMF 10.6.2's J₀′ = −J₁, J₁′ = J₀ − J₁/v); the test checks the derivative
/// numerically too. j₁,₁ is found here by Brent's method on <see cref="Bessel.J1"/>, not typed.
/// </para>
/// <para>
/// <b>What the sampled pupil can reach.</b> The enclosed energy is computed exactly for the
/// sampled pupil (no image grid: <see cref="DiffractionEnclosedEnergyKernel"/>), so the only
/// departure from the continuous Airy pattern is the pupil grid. Its main part is the area: the
/// N nodes inside the unit circle, each standing for ΔP² of pupil, make a pupil of area N·ΔP²,
/// i.e. a disk of radius s = √(N·ΔP²/π) ≠ 1, whose Airy pattern is E(s·v). To first order
/// E(s·v) − E(v) = E′(v)·v·(s − 1) = 2J₁(v)²·(s − 1), which is the bound the uncorrected curve is
/// held to. What remains after the area correction is the staircase's departure from a circle;
/// in a NumPy prototype of the same sum (written for this test, not a reference) it was
/// 5.5·10⁻⁵ at n = 64 and 1.2·10⁻⁵ at n = 128 over v ≤ 10, so 2·10⁻⁴ is allowed at n = 64 and the
/// test also asserts that doubling n shrinks it. At the first dark ring J₁ = 0, so E′(v) = 2J₁²/v
/// and E″(v) = 4J₁J₁′/v − 2J₁²/v² both vanish: the area error enters only at third order there,
/// and the fraction is held to 10⁻⁴ without the correction.
/// </para>
/// </summary>
public sealed class EnclosedEnergyGateTests
{
    private const double Lambda = 0.55e-6;
    private const double Scale = 0.05; // a: F = 1/(2a) = 10 in air

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    /// <summary>j₁,₁, the first zero of J₁: the first sign change of J₁ on a 0.1 scan from 1
    /// (J₁ > 0 just above 0), refined by Brent.</summary>
    private static double FirstZeroOfJ1()
    {
        double a = 1;
        while (Bessel.J1(a + 0.1) > 0) a += 0.1;
        return RootFinding.Brent(Bessel.J1, a, a + 0.1);
    }

    private static PupilFunction Clear(int n) => PupilFunction.FromFunctions(n, (_, _) => 1, (_, _) => 0, Scale, Scale, Lambda);

    /// <summary>The radius of a disk of the sampled pupil's area: √(N·ΔP²/π).</summary>
    private static double EffectiveRadius(PupilFunction p) => Math.Sqrt(p.CircleNodes * p.NodeSpacing * p.NodeSpacing / Math.PI);

    private static double MaxError(int n)
    {
        var p = Clear(n);
        var kernel = DiffractionEnclosedEnergyKernel.FromPupil(p);
        double s = EffectiveRadius(p), lambdaF = Lambda * p.FNumberX, worst = 0;
        for (double v = 0.25; v <= 10 + 1e-9; v += 0.25)
            worst = Math.Max(worst, Math.Abs(kernel.Fraction(EnclosedEnergyType.Encircled, v * lambdaF / Math.PI) - AiryEnclosedEnergy.Fraction(s * v)));
        return worst;
    }

    [Fact]
    public void TheClosedForm_HasTheDerivativeOfTheAiryEnergy_AndRunsFromZeroToOne()
    {
        // E′(v) must be 2J₁(v)²/v (the Airy intensity's ring integral, see AiryEnclosedEnergy);
        // central differences with h = 1e-5 are good to O(h²·E‴) ~ 1e-10.
        Assert.Equal(0, AiryEnclosedEnergy.Fraction(0));
        foreach (double v in new[] { 0.5, 1.7, 3.0, 5.2, 8.9, 14.5, 30.1 })
        {
            const double h = 1e-5;
            double numeric = (AiryEnclosedEnergy.Fraction(v + h) - AiryEnclosedEnergy.Fraction(v - h)) / (2 * h);
            double j1 = Bessel.J1(v);
            Close(2 * j1 * j1 / v, numeric, 1e-8, $"E′({v})");
        }
        // The energy outside v is J₀² + J₁²; with the leading Hankel term
        // J_ν(v) ≈ √(2/(πv))·cos(v − νπ/2 − π/4) (the DLMF 10.17 form Bessel.cs uses) the two
        // cosines are a quarter turn apart, so J₀² + J₁² → 2/(πv), about 0.003 at v = 200: the
        // enclosed fraction there lies between 0.99 and 1.
        Assert.InRange(AiryEnclosedEnergy.Fraction(200), 0.99, 1.0);
    }

    [Fact]
    public void ACircularPupil_HasTheAiryEnclosedEnergy_OfItsSampledArea()
    {
        const int n = 64;
        var p = Clear(n);
        var kernel = DiffractionEnclosedEnergyKernel.FromPupil(p);
        double s = EffectiveRadius(p), lambdaF = Lambda * p.FNumberX;
        Close(10, p.FNumberX, 1e-12, "F/# of the pupil scale");
        for (double v = 0.25; v <= 10 + 1e-9; v += 0.25)
        {
            double r = v * lambdaF / Math.PI;
            double e = kernel.Fraction(EnclosedEnergyType.Encircled, r);
            Close(AiryEnclosedEnergy.Fraction(s * v), e, 2e-4, $"E at v = {v} against the area-corrected Airy curve");
            double j1 = Bessel.J1(v);
            Close(AiryEnclosedEnergy.Fraction(v), e, 2 * j1 * j1 * Math.Abs(s - 1) + 2e-4, $"E at v = {v} against the Airy curve within the first-order area bound");
        }
        // The residual shrinks with the grid (prototype: about 4.5 times from 64 to 128).
        Assert.True(MaxError(128) < 0.5 * MaxError(64), "doubling the pupil sampling at least halves the residual");
    }

    [Fact]
    public void TheFractionInsideTheFirstDarkRing_IsOneMinusJ0SquaredMinusJ1Squared_AtTheFirstZeroOfJ1()
    {
        double z = FirstZeroOfJ1();
        Close(0, Bessel.J1(z), 1e-14, "J₁ at its zero");
        double expected = 1 - Bessel.J0(z) * Bessel.J0(z) - Bessel.J1(z) * Bessel.J1(z);
        foreach (int n in new[] { 64, 128 })
        {
            var p = Clear(n);
            var kernel = DiffractionEnclosedEnergyKernel.FromPupil(p);
            double r = z * Lambda * p.FNumberX / Math.PI;
            Close(expected, kernel.Fraction(EnclosedEnergyType.Encircled, r), 1e-4, $"inside the first dark ring, n = {n}");
        }
    }

    [Fact]
    public void AParaboloidOnAxis_EnclosesTheAiryFraction_InsideItsFirstDarkRing()
    {
        // A concave paraboloid (R = −0.2 m, focus 0.1 m) at F/20, image at its focus: zero OPD,
        // a clear pupil. The direction cosine of a ray from height h is (h/f)/(1 + h²/4f²)
        // (derived from the paraboloid's sag h²/4f), linear to 1.6e-4 at F/20, which the fitted
        // pupil scale absorbs; at the dark ring a scale error does not enter to second order (see
        // the class remarks), so the sampled pupil's 1e-4 holds with room.
        var lens = new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface
                {
                    Curvature = OpticsConventions.CurvatureFromRadius(-0.2), Conic = -1, Thickness = -0.1,
                    Medium = ParaxialMedium.Constant(1), IsStop = true, IsMirror = true,
                },
                new SequentialSurface(),
            },
        };
        var system = new OpticalSystemData
        {
            Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = 0.005 },
            Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint() } },
        };
        var setup = DiffractionEnclosedEnergy.Prepare(lens, system, 0, new[] { 0 }, 64, EnclosedEnergyReference.ChiefRay);
        var pupil = setup.Kernels[0].Pupil;
        Assert.InRange(pupil.FNumberX, 19.99, 20.01);
        double z = FirstZeroOfJ1();
        double r = z * pupil.Wavelength * pupil.FNumberX / Math.PI;
        double expected = 1 - Bessel.J0(z) * Bessel.J0(z);
        Close(expected, setup.Fraction(EnclosedEnergyType.Encircled, r), 1e-4, "paraboloid inside the first dark ring");
        Close(setup.Fraction(EnclosedEnergyType.Encircled, r),
            DiffractionEnclosedEnergy.Fraction(lens, system, 0, new[] { 0 }, 64, EnclosedEnergyType.Encircled, EnclosedEnergyReference.ChiefRay, r),
            0, "the criterion form is the same number");
        // On axis the centroid and the vertex are the chief ray's point (rotational symmetry).
        foreach (var reference in new[] { EnclosedEnergyReference.Centroid, EnclosedEnergyReference.Vertex })
            Close(setup.Fraction(EnclosedEnergyType.Encircled, r),
                DiffractionEnclosedEnergy.Prepare(lens, system, 0, new[] { 0 }, 64, reference).Fraction(EnclosedEnergyType.Encircled, r),
                1e-10, $"about the {reference} on axis");
    }
}
