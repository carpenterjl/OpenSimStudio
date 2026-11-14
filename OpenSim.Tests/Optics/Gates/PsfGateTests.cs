using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Wavefront;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// The OP-38 gate (docs/Optics-Gates.md): the Airy pattern of a circular pupil against
/// (2J₁(v)/v)² with its first dark ring at (j₁,₁/π)·λF; the annular pupil's closed form; the Strehl
/// ratio of a small pure Zernike term against the Maréchal expression within its validity; the FFT
/// and Huygens PSFs agree on an untilted image; the PSF's energy sums to the pupil transmission.
/// Goodman and Born and Wolf were not on hand: every closed form is derived in the comments, and
/// the only numbers are the Bessel function J₁ (<see cref="Bessel.J1"/>, gated by OP-3) and its
/// first zero found by root-finding.
/// <para>
/// <b>The continuous PSF.</b> A pupil of amplitude A(P) and OPD W(P) (waves) over the unit disk of
/// normalised coordinates P, with pupil scale a (direction cosine per unit P) and image index n,
/// gives U(x) = ∫ A·e^{2πi(W − n·a·P·x/λ)} d²P (the kernel of <see cref="PupilFunction"/>). For
/// A = 1, W = 0 on a disk of radius R, with v = 2π·n·a·|x|/λ and polar P = (ρ, θ):
/// U = ∫₀^R ∫₀^{2π} e^{−i(v/1)ρ cos θ} dθ ρ dρ = 2π ∫₀^R J₀(vρ) ρ dρ = 2π R J₁(vR)/v, using
/// J₀(z) = (1/2π)∫₀^{2π} e^{−iz cos θ} dθ and d[zJ₁(z)]/dz = z·J₀(z). So for the unit disk
/// U(v)/U(0) = 2J₁(v)/v (U(0) = π), the Airy amplitude, and the intensity is its square. Its first
/// zero is the first zero j₁,₁ of J₁: x₀ = j₁,₁·λ/(2π·n·a), and with the working F/#
/// F = 1/(2n·a) (the image-space numerical aperture n·a), x₀ = (j₁,₁/π)·λF; j₁,₁/π rounds to the
/// familiar 1.22. For an annulus ε ≤ ρ ≤ 1 subtract the disk of radius ε:
/// U ∝ 2J₁(v)/v − ε²·2J₁(εv)/(εv), divided by its value 1 − ε² at v = 0.
/// </para>
/// </summary>
public sealed class PsfGateTests
{
    private const double Lambda = 0.55e-6;

    private static double Airy(double v) => v == 0 ? 1 : 2 * Bessel.J1(v) / v;

    private static double Annulus(double v, double eps) =>
        v == 0 ? 1 : (Airy(v) - eps * eps * Airy(eps * v)) / (1 - eps * eps);

    /// <summary>j₁,₁, the first zero of J₁ (between 3 and 5: J₁(3) &gt; 0 &gt; J₁(5) is checked by Brent).</summary>
    private static double FirstZeroOfJ1() => RootFinding.Brent(Bessel.J1, 3, 5);

    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    // ---- The Airy pattern ----

    /// <summary>
    /// A clear circular pupil sampled 1025 × 1025 (radius 512 node spacings). Discretisation: the
    /// node sum replaces the disk integral; its error is the jagged edge, a strip of about
    /// 2π·512 ≈ 3200 nodes out of about 823 000 whose in/out decisions fall at random offsets, so
    /// the amplitude error is of order √3200/2/823 000 ≈ 3·10⁻⁵ of the peak and the intensity
    /// error about twice that. The tolerance 10⁻³ of the peak leaves a factor of ten.
    /// </summary>
    [Fact]
    public void ACircularPupil_GivesTheAiryPattern_WithItsFirstZeroAtJ11OverPiTimesLambdaF()
    {
        const int n = 1025;
        const double a = 0.025;          // F/20 in air
        var pupil = PupilFunction.FromFunctions(n, (_, _) => 1, (_, _) => 0, a, a, Lambda);
        double f = 1 / (2 * a);
        Close(f, pupil.FNumberX, 1e-12 * f, "working F/#");

        // v = 2π·a·x/λ along x through the centre, out past the third ring.
        var vs = Enumerable.Range(0, 81).Select(i => i * 0.125).ToArray();
        var xs = vs.Select(v => v * Lambda / (2 * Math.PI * a)).ToArray();
        var image = PsfEngine.Sample(pupil, xs, new[] { 0.0 });
        for (int i = 0; i < vs.Length; i++)
            Close(Airy(vs[i]) * Airy(vs[i]), image.Intensity[0, i], 1e-3, $"Airy intensity at v = {vs[i]}");
        Assert.Equal(1.0, image.Intensity[0, 0], 12);             // the unaberrated peak is 1 by definition

        // The first dark ring: the engine's amplitude (real for a symmetric pupil) changes sign at
        // x₀ = (j₁,₁/π)·λF. Amplitude error δ ≈ 3·10⁻⁵ against a slope |d(2J₁/v)/dv| = 2|J₀(j₁,₁)|/j₁,₁
        // ≈ 0.2 (derived: d(2J₁(v)/v)/dv = −2J₂(v)/v, and J₂ = −J₀ at a zero of J₁) moves the zero
        // by ~1.5·10⁻⁴ in v, 4·10⁻⁵ relative; 5·10⁻⁴ is allowed.
        double j11 = FirstZeroOfJ1();
        double x0 = j11 / Math.PI * Lambda * f;
        double U(double x) => PsfEngine.Sample(pupil, new[] { x }, new[] { 0.0 }).Amplitude![0, 0].Real;
        double zero = RootFinding.Brent(U, 0.9 * x0, 1.1 * x0, 1e-12 * x0);
        Close(x0, zero, 5e-4 * x0, "first dark ring");
        Assert.InRange(PsfEngine.Sample(pupil, new[] { x0 }, new[] { 0.0 }).Intensity[0, 0], 0, 1e-6);
    }

    /// <summary>The same through a lens: a paraboloid on axis (zero OPD, OpdGateTests) at F/20,
    /// traced 128 × 128. The edge strip is now about 400 of 12 700 nodes (amplitude error of order
    /// √400/2/12 700 ≈ 8·10⁻⁴), and the paraboloid's directions depart from the linear pupil
    /// scale by (h/2f)² ≈ 1.6·10⁻⁴ at the rim; 10⁻² of the peak is allowed. The F/# is the one
    /// the fitted pupil scale stands for, which must be the paraboloid's f/D to 10⁻³.</summary>
    [Fact]
    public void AParaboloid_HasTheAiryPattern_OfItsWorkingFNumber()
    {
        double r = -0.2, epd = 0.005;
        var lens = Paraboloid(r, epd, out var system);
        var pupil = PsfEngine.BuildPupil(lens, system, 0, 0, 128);
        Assert.Empty(pupil.Warnings);
        double fNumber = (-r / 2) / epd;
        Close(fNumber, pupil.FNumberX, 1e-3 * fNumber, "working F/# from the fitted scale (x)");
        Close(fNumber, pupil.FNumberY, 1e-3 * fNumber, "working F/# from the fitted scale (y)");
        double a = Math.Abs(pupil.ScaleX);
        var vs = Enumerable.Range(0, 41).Select(i => i * 0.25).ToArray();
        var xs = vs.Select(v => v * pupil.Wavelength / (2 * Math.PI * pupil.ImageIndex * a)).ToArray();
        var image = PsfEngine.Sample(pupil, xs, new[] { 0.0 });
        for (int i = 0; i < vs.Length; i++)
            Close(Airy(vs[i]) * Airy(vs[i]), image.Intensity[0, i], 1e-2, $"Airy intensity at v = {vs[i]}");
        Close(1, PsfEngine.Strehl(pupil), 1e-12, "Strehl ratio of a perfect lens");
    }

    // ---- The annular pupil ----

    /// <summary>An annulus 0.4 ≤ ρ ≤ 1 sampled 1025 × 1025: two jagged edges instead of one, the
    /// same error estimate, the same 10⁻³ tolerance. Its first zero is where J₁(v) = ε·J₁(εv),
    /// found on the closed form and on the engine.</summary>
    [Fact]
    public void AnAnnularPupil_GivesTheClosedForm_AndItsFirstZeroMovesIn()
    {
        const int n = 1025;
        const double a = 0.025, eps = 0.4;
        var pupil = PupilFunction.FromFunctions(n, (x, y) => x * x + y * y >= eps * eps ? 1 : 0, (_, _) => 0, a, a, Lambda);
        var vs = Enumerable.Range(0, 81).Select(i => i * 0.125).ToArray();
        var xs = vs.Select(v => v * Lambda / (2 * Math.PI * a)).ToArray();
        var image = PsfEngine.Sample(pupil, xs, new[] { 0.0 });
        for (int i = 0; i < vs.Length; i++)
            Close(Annulus(vs[i], eps) * Annulus(vs[i], eps), image.Intensity[0, i], 1e-3, $"annular intensity at v = {vs[i]}");

        // g(v) = 1 − ε² at 0 and −ε²·2J₁(ε·j₁,₁)/(ε·j₁,₁)/(1 − ε²) < 0 at j₁,₁, so the first zero lies
        // in between, inside the Airy pattern's.
        double j11 = FirstZeroOfJ1();
        double v0 = RootFinding.Brent(v => Annulus(v, eps), 0.5, j11);
        Assert.True(v0 < j11, $"the annulus's first zero {v0} lies inside the disk's {j11}");
        double x0 = v0 * Lambda / (2 * Math.PI * a);
        double zero = RootFinding.Brent(x => PsfEngine.Sample(pupil, new[] { x }, new[] { 0.0 }).Amplitude![0, 0].Real, 0.9 * x0, 1.1 * x0, 1e-12 * x0);
        Close(x0, zero, 5e-4 * x0, "annular first zero");
    }

    // ---- Strehl against Maréchal ----

    /// <summary>
    /// A small pure Zernike term (Fringe Z9, balanced spherical 6ρ⁴ − 6ρ² + 1, or Z5, astigmatism
    /// ρ² cos 2θ), symmetric under x → −x and y → −y, so the PSF's peak is at the reference point
    /// (the gradient vanishes there by symmetry and the Hessian is the Airy pattern's to first
    /// order). There S = |⟨e^{iφ}⟩|², φ = 2πW, ⟨·⟩ the mean over the lit nodes.
    /// <para>
    /// <b>Derivation.</b> With φ̃ = φ − ⟨φ⟩ (|⟨e^{iφ}⟩| does not change), cos x ≥ 1 − x²/2 gives
    /// ⟨cos φ̃⟩ ≥ 1 − m₂/2 (m_k = ⟨φ̃^k⟩), so S ≥ (1 − m₂/2)² ≥ 1 − m₂: the Maréchal expression
    /// 1 − (2πσ)² is a lower bound. cos x ≤ 1 − x²/2 + x⁴/24 and |sin x − x + x³/6| ≤ |x|⁵/120 with
    /// ⟨φ̃⟩ = 0 give S ≤ (1 − m₂/2 + m₄/24)² + (|m₃|/6 + ⟨|φ̃|⁵⟩/120)², so
    /// S − (1 − m₂) = O(m₂²): the expression holds while (2πσ)² is small. Both bounds are computed
    /// from the same pupil samples the engine sums.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(9, 0.045)]
    [InlineData(5, 0.04)]
    public void TheStrehlOfASmallZernikeTerm_LiesOnTheMarechalExpression_WithinItsDerivedBounds(int term, double coefficient)
    {
        Func<double, double, double> z = term == 9
            ? (x, y) => { double r2 = x * x + y * y; return 6 * r2 * r2 - 6 * r2 + 1; }
            : (x, y) => x * x - y * y;
        var pupil = PupilFunction.FromFunctions(128, (_, _) => 1, (x, y) => coefficient * z(x, y), 0.05, 0.05, Lambda);
        double strehl = PsfEngine.Strehl(pupil);

        var phases = new List<double>();
        for (int i = 0; i < pupil.Sampling; i++)
            for (int k = 0; k < pupil.Sampling; k++)
                if (pupil.Amplitude[i, k] > 0) phases.Add(2 * Math.PI * pupil.Waves[i, k]);
        double mean = phases.Average();
        double M(int p) => phases.Average(x => Math.Pow(x - mean, p));
        double m2 = M(2), m3 = M(3), m4 = M(4), m5abs = phases.Average(x => Math.Pow(Math.Abs(x - mean), 5));
        double sigma = Math.Sqrt(m2) / (2 * Math.PI);
        double marechal = 1 - Math.Pow(2 * Math.PI * sigma, 2);
        double lower = Math.Pow(1 - m2 / 2, 2);
        double upper = Math.Pow(1 - m2 / 2 + m4 / 24, 2) + Math.Pow(Math.Abs(m3) / 6 + m5abs / 120, 2);

        // The peak search must find the reference point's value (the bounds are for it).
        Close(PsfEngine.Sample(pupil, new[] { 0.0 }, new[] { 0.0 }).Intensity[0, 0], strehl, 1e-12, "Strehl is the value at the reference point");
        Assert.InRange(strehl, lower - 1e-12, upper + 1e-12);
        Assert.True(strehl >= marechal - 1e-12, $"Strehl {strehl} below the Maréchal bound {marechal}");
        // Within its validity: the two differ by O(m₂²); with (2πσ)² ≈ 0.016 here, by less than a
        // tenth of the aberration's own effect 1 − S.
        Assert.InRange((2 * Math.PI * sigma) * (2 * Math.PI * sigma), 0.005, 0.03);
        Assert.True(strehl - marechal <= 0.1 * (1 - strehl), $"Strehl {strehl} departs from Maréchal {marechal} by more than a tenth of 1 − S");
    }

    // ---- FFT against Huygens ----

    /// <summary>
    /// Rays whose transverse direction cosines are exactly a·P, meeting the image plane at its
    /// centre with path W: the Huygens sum is then term for term the pupil-function sum the FFT
    /// and the matrix Fourier transform evaluate, so the two agree to rounding (each value is a
    /// sum of ~800 unit-size terms whose phases carry ~10⁻¹⁴ turns of error: 10⁻¹⁰ allowed).
    /// The wavefront carries coma so the PSF is not symmetric.
    /// </summary>
    [Fact]
    public void TheFftAndHuygensSums_AreTheSameSum_ForRaysOnTheLinearPupilScale()
    {
        const int n = 32;
        const double a = 0.04;
        Func<double, double, double> coma = (x, y) => 0.3 * (3 * (x * x + y * y) - 2) * y;
        var pupil = PupilFunction.FromFunctions(n, (x, y) => Math.Exp(-0.5 * (x * x + y * y)), coma, a, a, Lambda);
        var fft = PsfEngine.Fft(pupil);

        var rays = new List<HuygensRay>();
        for (int i = 0; i < n; i++)
            for (int k = 0; k < n; k++)
            {
                if (pupil.Amplitude[i, k] == 0) continue;
                double px = pupil.Nodes[k], py = pupil.Nodes[i];
                double tx = a * px, ty = a * py;
                rays.Add(new HuygensRay(Vector3D.Zero, new Vector3D(tx, ty, Math.Sqrt(1 - tx * tx - ty * ty)), pupil.Waves[i, k], pupil.Amplitude[i, k]));
            }
        var huygensPupil = new HuygensPupil
        {
            Rays = rays, Wavelength = Lambda, ImageIndex = 1, Centre = Vector3D.Zero,
            AxisX = new Vector3D(1, 0, 0), AxisY = new Vector3D(0, 1, 0),
        };
        var huygens = PsfEngine.Huygens(huygensPupil, fft.X, fft.Y);
        for (int r = 0; r < fft.Y.Count; r++)
            for (int c = 0; c < fft.X.Count; c++)
                Close(fft.Intensity[r, c], huygens.Intensity[r, c], 1e-10, $"intensity at [{r}, {c}]");
    }

    /// <summary>
    /// An untilted image of a real lens: a paraboloid on axis at F/20 (zero OPD, the image plane
    /// normal to the chief ray, so the FFT's plane and the Huygens plane are the same). The FFT
    /// uses the fitted linear pupil scale and equal weights; the Huygens sum uses each ray's real
    /// direction and the √Jacobian weight. They differ by the paraboloid's departure from a linear
    /// direction map, (h/2f)² ≈ 1.6·10⁻⁴ at the rim, in scale and in amplitude: an intensity
    /// difference of order 10⁻⁴ of the peak. 2·10⁻³ is allowed. Both Strehl ratios are 1.
    /// </summary>
    [Fact]
    public void TheFftAndHuygensPsfs_AgreeOnAnUntiltedImage()
    {
        var lens = Paraboloid(-0.2, 0.005, out var system);
        const int n = 64;
        var pupil = PsfEngine.BuildPupil(lens, system, 0, 0, n);
        var fft = PsfEngine.Fft(pupil);
        var huygensPupil = PsfEngine.BuildHuygens(lens, system, 0, 0, n);
        Assert.Equal(pupil.Amplitude.Cast<double>().Count(v => v > 0), huygensPupil.Rays.Count);

        int m = fft.X.Count, centre = m / 2;
        double reach = 3 * 1.3 * pupil.Wavelength * pupil.FNumberX;     // past the second dark ring
        var columns = Enumerable.Range(0, m).Where(c => Math.Abs(fft.X[c]) <= reach).ToArray();
        var xs = columns.Select(c => fft.X[c]).ToArray();
        var row = PsfEngine.Huygens(huygensPupil, xs, new[] { 0.0 });
        var column = PsfEngine.Huygens(huygensPupil, new[] { 0.0 }, xs.Select((_, i) => fft.Y[columns[i]]).ToArray());
        Assert.True(columns.Length >= 9, $"the comparison covers {columns.Length} samples");
        for (int i = 0; i < columns.Length; i++)
        {
            Close(fft.Intensity[centre, columns[i]], row.Intensity[0, i], 2e-3, $"x section at {fft.X[columns[i]]:g4} m");
            Close(fft.Intensity[columns[i], centre], column.Intensity[i, 0], 2e-3, $"y section at {fft.Y[columns[i]]:g4} m");
        }
        Close(1, PsfEngine.Strehl(pupil), 1e-12, "FFT Strehl");
        Close(1, PsfEngine.HuygensStrehl(huygensPupil, row), 1e-9, "Huygens Strehl");
    }

    // ---- Energy ----

    /// <summary>The FFT window is one period of the PSF, so by Parseval its energy is exactly the
    /// pupil's Σ A² over a clear pupil's, the transmission, whatever the aberration, apodization
    /// or obscuration (to rounding: 10⁻¹² relative).</summary>
    [Theory]
    [InlineData(0.0, 0.0, 0.0)]
    [InlineData(0.3, 0.0, 0.0)]
    [InlineData(0.0, 1.0, 0.8)]
    [InlineData(0.25, 0.5, 2.0)]
    public void TheFftPsfsEnergy_SumsToThePupilTransmission(double obscuration, double gaussian, double defocusWaves)
    {
        var pupil = PupilFunction.FromFunctions(48,
            (x, y) => x * x + y * y >= obscuration * obscuration ? Math.Exp(-gaussian * (x * x + y * y)) : 0,
            (x, y) => defocusWaves * (2 * (x * x + y * y) - 1), 0.03, -0.035, Lambda, 1.3);
        var image = PsfEngine.Fft(pupil);
        Close(pupil.Transmission, image.EnergyFraction, 1e-12 * pupil.Transmission, "energy against transmission");
        if (obscuration == 0 && gaussian == 0) Close(1, pupil.Transmission, 0, "a clear pupil transmits everything");
        else Assert.True(pupil.Transmission < 1);
    }

    /// <summary>The same through a lens with Gaussian apodization: the transmission is the mean of
    /// e^{−2Gρ²} over the traced nodes, and the FFT's energy is it.</summary>
    [Fact]
    public void ThroughALens_WithGaussianApodization_TheEnergyIsTheTransmission()
    {
        var lens = Paraboloid(-0.2, 0.01, out var clear);
        var system = clear with { Aperture = clear.Aperture with { Apodization = ApodizationType.Gaussian, ApodizationFactor = 1 } };
        var pupil = PsfEngine.BuildPupil(lens, system, 0, 0, 40);
        double sum = 0;
        int count = 0;
        for (int i = 0; i < 40; i++)
            for (int k = 0; k < 40; k++)
            {
                double px = OpdEngine.Node(k, 40), py = OpdEngine.Node(i, 40), r2 = px * px + py * py;
                if (r2 > 1) continue;
                sum += Math.Exp(-2 * r2);
                count++;
            }
        Close(sum / count, pupil.Transmission, 1e-13, "transmission");
        Close(pupil.Transmission, PsfEngine.Fft(pupil).EnergyFraction, 1e-12, "energy");
    }

    // ---- Helpers ----

    private static SequentialLens Paraboloid(double radius, double epd, out OpticalSystemData system)
    {
        // RealRayTraceTests: a paraboloid (k = −1, R < 0) sends every axial ray through its focus
        // at R/2 with equal path; the image surface is placed there.
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
