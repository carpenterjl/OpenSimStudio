using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// The OP-39 gate (docs/Optics-Gates.md): a perfect circular pupil's MTF against
/// (2/π)(acos s − s√(1 − s²)) with cutoff 1/(λF); a defocused perfect lens against the exact
/// defocus integral; the geometric MTF approaching the diffraction MTF as the aberration grows;
/// the square wave by Coltman's series. Goodman (Introduction to Fourier Optics) was not on hand,
/// so every closed form is derived here; Hopkins' defocus series and O'Neill's annular closed form
/// were not available, so the defocus check uses the exact integral derived below and the annular
/// pupil is not gated. The only library numbers are J₁ (<see cref="Bessel.J1"/>, gated by OP-3)
/// and Gauss–Legendre nodes (<see cref="GaussLegendre"/>).
/// <para>
/// <b>The perfect pupil.</b> The OTF at a pupil shift t (normalised pupil units, the shift ν/c of
/// <see cref="MtfPupil"/>) is the overlap of the unit disk with itself shifted by t, over its area
/// π. Two unit circles a distance t = 2s apart overlap in a lens of area
/// 2(acos s − s√(1 − s²)) (two circular segments of half-angle acos s, each acos s − s√(1 − s²)),
/// so MTF(s) = (2/π)(acos s − s√(1 − s²)), zero from s = 1, the shift 2, which is the frequency
/// 2c = 2n·a/λ = 1/(λF) with F = 1/(2n·a).
/// </para>
/// <para>
/// <b>Defocus.</b> With W = W₂₀·ρ² waves, f(P)·f*(P + t) has the phase 2πW₂₀(|P|² − |P + t|²);
/// centring the shift (P → P − t/2) makes it −2πW₂₀·2t·Px, linear in Px, so with t = 2s
/// OTF(s) = (1/π)∫∫_lens e^{−i·8πW₂₀·s·x} dx dy over the lens |P ± s·x̂| ≤ 1, whose half-height at x
/// is √(1 − (|x| + s)²). The integrand is even in x:
/// OTF(s) = (4/π)∫₀^{1−s} √(1 − (x + s)²)·cos(8πW₂₀·s·x) dx, and with x + s = sin θ,
/// OTF(s) = (4/π)∫_{asin s}^{π/2} cos²θ·cos(8πW₂₀·s·(sin θ − s)) dθ, smooth for Gauss–Legendre. At
/// W₂₀ = 0 it is (4/π)[π/4 − (asin s + s√(1 − s²))/2], the perfect pupil's MTF, since
/// π/2 − asin s = acos s. This is the integral Hopkins' series evaluates.
/// </para>
/// <para>
/// <b>Geometric defocus.</b> Stationary phase of OP-38's kernel puts the ray from P at
/// x = (λ/(n·a))·∂W/∂P = 2W₂₀·λ·P/(n·a): a uniform disk of radius R = 2W₂₀λ/(n·a). Its OTF is
/// (1/π)∫_disk e^{−2πiνR·Px} dP = 2J₁(z)/z with z = 2πνR (the same integral as the Airy amplitude,
/// OP-38's gate), and in s = ν/ν_c, z = 8πW₂₀·s.
/// </para>
/// </summary>
public sealed class MtfGateTests
{
    private const double Lambda = 0.55e-6;
    private const double A = 0.025;   // pupil scale: F/20 in air

    private static double Perfect(double s) => s >= 1 ? 0 : 2 / Math.PI * (Math.Acos(s) - s * Math.Sqrt(1 - s * s));

    private static double DefocusIntegral(double s, double w20)
    {
        if (s >= 1) return 0;
        var (x, w) = GaussLegendre.Rule(200);
        double a = Math.Asin(s), b = Math.PI / 2, sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double t = (b - a) / 2 * x[i] + (b + a) / 2;
            sum += w[i] * Math.Cos(t) * Math.Cos(t) * Math.Cos(8 * Math.PI * w20 * s * (Math.Sin(t) - s));
        }
        return 4 / Math.PI * sum * (b - a) / 2;
    }

    private static double GeometricDefocus(double z) => z == 0 ? 1 : 2 * Bessel.J1(z) / z;

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    private static MtfPupil Pupil(int n, Func<double, double, double> waves) =>
        MtfPupil.FromPupil(PupilFunction.FromFunctions(n, (_, _) => 1, waves, A, A, Lambda));

    // ---- The perfect circular pupil ----

    /// <summary>
    /// 129 × 129 nodes. The node sum replaces the area integral: the error is the jagged edge of
    /// the two sampled disks, which a direct count of the overlapping nodes puts at 6.7·10⁻⁴ of the
    /// MTF at most for this grid (and 9·10⁻⁴ at 128); linear interpolation between whole-node
    /// shifts adds h²/8·|M''| ≈ 10⁻⁵. 2·10⁻³ is allowed. The cutoff is 2n·a/λ = 1/(λF) to rounding,
    /// and beyond the last whole-node shift that overlaps the MTF is exactly zero.
    /// </summary>
    [Fact]
    public void APerfectCircularPupil_HasTheClosedFormMtf_WithCutoffOneOverLambdaF()
    {
        var pupil = Pupil(129, (_, _) => 0);
        double f = 1 / (2 * A);
        double cutoff = 1 / (Lambda * f);
        Close(cutoff, pupil.CutoffX, 1e-12 * cutoff, "sagittal cutoff 1/(λF)");
        Close(cutoff, pupil.CutoffY, 1e-12 * cutoff, "tangential cutoff 1/(λF)");
        for (int i = 0; i <= 50; i++)
        {
            double s = i / 50.0;
            double nu = s * cutoff;
            Close(Perfect(s), pupil.Otf(nu, MtfOrientation.Tangential).Magnitude, 2e-3, $"tangential MTF at s = {s}");
            Close(Perfect(s), pupil.Otf(nu, MtfOrientation.Sagittal).Magnitude, 2e-3, $"sagittal MTF at s = {s}");
            Close(MtfEngine.DiffractionLimitedCircular(s), Perfect(s), 1e-15, $"the engine's closed form at s = {s}");
        }
        Assert.Equal(1.0, pupil.Otf(0, MtfOrientation.Tangential).Real, 14);
        Assert.Equal(0.0, pupil.Otf(1.01 * cutoff, MtfOrientation.Tangential).Magnitude);
        Assert.Equal(0.0, pupil.Otf(-1.01 * cutoff, MtfOrientation.Sagittal).Magnitude);
    }

    /// <summary>The same through a lens: a paraboloid on axis (zero OPD, OP-38's gate) at F/20 traced
    /// 128 × 128. The cutoff the fitted pupil scale stands for must be 1/(λF) with F = f/D to 10⁻³
    /// (OP-38's tolerance for the same lens's F/#), and the MTF against s = ν/cutoff the closed form
    /// within the 128-node edge error (9·10⁻⁴) plus the paraboloid's departure of its directions
    /// from the linear pupil scale, (h/2f)² ≈ 1.6·10⁻⁴ at the rim: 3·10⁻³ is allowed.</summary>
    [Fact]
    public void AParaboloid_HasTheClosedFormMtf_OfItsWorkingFNumber()
    {
        double r = -0.2, epd = 0.005;
        var lens = Paraboloid(r, epd, out var system);
        var pupil = MtfEngine.BuildPupil(lens, system, 0, 0, 128);
        Assert.Empty(pupil.Warnings);
        double fNumber = (-r / 2) / epd;
        double cutoff = 1 / (pupil.Wavelength * fNumber);
        Close(cutoff, pupil.CutoffX, 1e-3 * cutoff, "sagittal cutoff");
        Close(cutoff, pupil.CutoffY, 1e-3 * cutoff, "tangential cutoff");
        for (int i = 0; i <= 25; i++)
        {
            double s = i / 25.0;
            Close(Perfect(s), pupil.Otf(s * pupil.CutoffY, MtfOrientation.Tangential).Magnitude, 3e-3, $"tangential MTF at s = {s}");
            Close(Perfect(s), pupil.Otf(s * pupil.CutoffX, MtfOrientation.Sagittal).Magnitude, 3e-3, $"sagittal MTF at s = {s}");
        }
    }

    // ---- Defocus ----

    /// <summary>
    /// A perfect pupil with W₂₀ = 0.25, 0.5 and 1 wave of defocus on 129 × 129 nodes against the
    /// exact defocus integral (derived in the class remarks; 200 Gauss–Legendre nodes on a smooth
    /// integrand of at most a few oscillations, so the quadrature is exact to rounding). The node
    /// sum's error, by a direct evaluation of the same sampled autocorrelation, is at most 9.7·10⁻⁴
    /// for these defocuses; 2·10⁻³ is allowed.
    /// </summary>
    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void ADefocusedPerfectLens_FollowsTheExactDefocusIntegral(double w20)
    {
        var pupil = Pupil(129, (x, y) => w20 * (x * x + y * y));
        Close(Perfect(0.3), DefocusIntegral(0.3, 0), 1e-13, "the integral without defocus is the perfect MTF");
        for (int i = 0; i <= 40; i++)
        {
            double s = i / 40.0;
            Close(Math.Abs(DefocusIntegral(s, w20)), pupil.Otf(s * pupil.CutoffX, MtfOrientation.Sagittal).Magnitude, 2e-3, $"W20 = {w20}, s = {s}");
            Close(Math.Abs(DefocusIntegral(s, w20)), pupil.Otf(s * pupil.CutoffY, MtfOrientation.Tangential).Magnitude, 2e-3, $"W20 = {w20}, s = {s} (tangential)");
        }
    }

    // ---- Geometric against diffraction ----

    /// <summary>The geometric engine on a uniform disk of rays: 2J₁(z)/z (derived in the class
    /// remarks). 129 × 129 rays: a direct evaluation of the same lattice sum differs from the
    /// closed form by at most 1.2·10⁻³ for z ≤ 12; 3·10⁻³ is allowed.</summary>
    [Fact]
    public void TheGeometricEngine_OnAUniformDiskOfRays_Is2J1zOverz()
    {
        const int n = 129;
        double radius = 20e-6;
        var rays = new List<GeometricRay>();
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                double px = OpdEngine.Node(k, n), py = OpdEngine.Node(i, n);
                if (px * px + py * py <= 1) rays.Add(new GeometricRay(radius * px, radius * py, 1));
            }
        for (int i = 0; i <= 48; i++)
        {
            double z = i * 0.25;
            double nu = z / (2 * Math.PI * radius);
            Close(Math.Abs(GeometricDefocus(z)), MtfEngine.GeometricOtf(rays, nu, MtfOrientation.Sagittal).Magnitude, 3e-3, $"sagittal at z = {z}");
            Close(Math.Abs(GeometricDefocus(z)), MtfEngine.GeometricOtf(rays, nu, MtfOrientation.Tangential).Magnitude, 3e-3, $"tangential at z = {z}");
        }
    }

    /// <summary>
    /// The gate's last line: the geometric MTF tends to the diffraction MTF as the aberration grows.
    /// For W₂₀ = 4 and 16 waves of defocus on 257 × 257 nodes, the largest difference between the
    /// FFT MTF and the geometric MTF of the rays the same defocus sends (x = 2W₂₀λP/(n·a), the
    /// stationary-phase landing point derived in the class remarks), over z = 8πW₂₀s ≤ 12, falls
    /// as 1/W₂₀ (a direct evaluation of both lattice sums gives 0.041 and 0.0107). The test asks it
    /// to fall by more than half and to be below 0.015 at 16 waves. The FFT pupil is sampled finely
    /// enough: the integrand's phase advances 8πW₂₀·s·ΔP ≤ 12·2/256 ≈ 0.1 rad per node here.
    /// </summary>
    [Fact]
    public void TheGeometricMtf_TendsToTheDiffractionMtf_AsTheDefocusGrows()
    {
        const int n = 257;
        double Difference(double w20)
        {
            var diffraction = Pupil(n, (x, y) => w20 * (x * x + y * y));
            double radius = 2 * w20 * Lambda / A;
            var rays = new List<GeometricRay>();
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++)
                {
                    double px = OpdEngine.Node(k, n), py = OpdEngine.Node(i, n);
                    if (px * px + py * py <= 1) rays.Add(new GeometricRay(radius * px, radius * py, 1));
                }
            double worst = 0;
            for (int i = 0; i <= 60; i++)
            {
                double z = i * 0.2;
                double s = z / (8 * Math.PI * w20);
                double nu = s * diffraction.CutoffX;
                Close(z / (2 * Math.PI * radius), nu, 1e-9 * nu + 1e-9, "the frequency of z (2πνR = z)");
                double d = diffraction.Otf(nu, MtfOrientation.Sagittal).Magnitude;
                double g = MtfEngine.GeometricOtf(rays, nu, MtfOrientation.Sagittal).Magnitude;
                worst = Math.Max(worst, Math.Abs(d - g));
            }
            return worst;
        }
        double four = Difference(4), sixteen = Difference(16);
        Assert.True(sixteen < 0.5 * four, $"the difference must fall as the defocus grows: {four:R} at 4 waves, {sixteen:R} at 16.");
        Assert.True(sixteen < 0.015, $"at 16 waves the geometric and diffraction MTFs differ by {sixteen:R}.");
    }

    // ---- Square wave ----

    /// <summary>
    /// Coltman's series on the perfect pupil (derived in <see cref="MtfEngine.SquareWave"/>): above
    /// s = 1/3 only the fundamental is below the cutoff, so S = (4/π)M; between 1/5 and 1/3,
    /// S = (4/π)(M(s) − M(3s)/3); S(0) = 1. Against the closed form on 129 × 129 nodes, the MTF
    /// tolerance 2·10⁻³ times the series' weights (4/π)(1 + 1/3 + …) for the harmonics used.
    /// </summary>
    [Fact]
    public void TheSquareWave_IsColtmansSeriesOfTheSineMtf()
    {
        var pupil = Pupil(129, (_, _) => 0);
        double c = pupil.CutoffX;
        double Engine(double s) => MtfEngine.SquareWave(f => pupil.Otf(f, MtfOrientation.Sagittal).Magnitude, s * c, c);
        Assert.Equal(1.0, Engine(0));
        foreach (double s in new[] { 0.34, 0.5, 0.75, 0.95 })
            Close(4 / Math.PI * Perfect(s), Engine(s), 4 / Math.PI * 2e-3, $"square wave at s = {s}");
        foreach (double s in new[] { 0.21, 0.25, 0.3 })
            Close(4 / Math.PI * (Perfect(s) - Perfect(3 * s) / 3), Engine(s), 4 / Math.PI * (1 + 1.0 / 3) * 2e-3, $"square wave at s = {s}");
        // The closed form itself: at small s the series sums many harmonics and tends to 1.
        double Closed(double s) => MtfEngine.SquareWave(Perfect, s, 1);
        Assert.True(Closed(0.01) > Perfect(0.01), "the square wave responds more than the sine wave at low frequency");
        Assert.InRange(Closed(0.001), 0.99, 1.0);
    }

    private static SequentialLens Paraboloid(double radius, double epd, out OpticalSystemData system)
    {
        // OP-38's gate: a paraboloid (k = −1, R < 0) sends every axial ray through its focus at R/2
        // with equal path; the image surface is placed there.
        system = new OpticalSystemData
        {
            Aperture = new Aperture { Type = ApertureType.EntrancePupilDiameter, Value = epd },
            Fields = new FieldSet { Type = FieldType.Angle, Points = new[] { new FieldPoint() } },
        };
        return new SequentialLens
        {
            Surfaces = new[]
            {
                new SequentialSurface { Thickness = double.PositiveInfinity },
                new SequentialSurface
                {
                    Curvature = OpticsConventions.CurvatureFromRadius(radius), Conic = -1, Thickness = radius / 2,
                    Medium = ParaxialMedium.Constant(1), IsStop = true, IsMirror = true,
                },
                new SequentialSurface(),
            },
        };
    }
}
