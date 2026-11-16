using System.Numerics;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Sources;
using OpenSim.Optics.Paraxial;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-77: non-sequential sources (docs/Optics-Conventions.md §72). Gate: each source's sampled
/// position and direction distributions converge to their analytic forms, at the 1/√N rate for
/// random numbers and faster for Sobol points; the emitted power equals the stated power exactly;
/// a run through <see cref="NscSourceRun"/> with a source object and OP-70's detecting stand-in
/// closes the energy ledger.
/// <para>
/// <b>How convergence is tested.</b> For a statistic g of one ray with exact mean μ and standard
/// deviation σ (both derived in each test from the distribution the help states, or computed by
/// quadrature independent of the sampler), the sample mean of N random rays has standard error
/// σ/√N. With a fixed seed it must lie within 5σ/√N of μ at N = 1024 and at N = 16384, which is
/// the 1/√N rate (a 5σ deviation of a near-normal mean has probability below 1e-6). Sobol points
/// must do better: at N = 16384 the error must be below σ/√N, one random standard error, i.e. a
/// fifth of the random tolerance; this is the gate's "faster than random" condition, checked only
/// where every coordinate comes from Sobol numbers through a monotone inversion. Rejection-sampled
/// parts (an annulus, the Gaussian far field of Source Ellipse) are random only, as in OpticStudio.
/// </para>
/// </summary>
public sealed class NonSequentialSourceTests
{
    private const double Mm = 1e-3;

    private static readonly NscSourceEmitOptions Mono = new() { SystemWavelengths = new[] { new NscSpectralLine(0.55, 1) } };

    private delegate NscSource Make(NscSourceCommon common, NscSourceProperties properties);

    private static NscScene Alone(NscSource source) => new() { Objects = new[] { new NscObject { Geometry = source } } };

    private static NscSourceEmissionSet EmitSet(Make make, int n, NscSourceSampling sampling, NscSourceProperties? properties = null,
        NscSourceEmitOptions? options = null, int? threads = null, double power = 2)
    {
        var source = make(new NscSourceCommon { AnalysisRays = n, Power = power }, (properties ?? new NscSourceProperties()) with { Sampling = sampling });
        return NscSourceEmitter.Emit(Alone(source), 1, new NscTraceSettings { Seed = 7, MaxDegreeOfParallelism = threads }, options ?? Mono);
    }

    /// <summary>The rays in ray-number order (one wavelength).</summary>
    private static List<NscLaunchRay> Emit(Make make, int n, NscSourceSampling sampling, NscSourceProperties? properties = null) =>
        EmitSet(make, n, sampling, properties).Emissions.Single().Rays.ToList();

    private static void Converges(string what, Make make, Func<NscLaunchRay, double> g, double mean, double sigma, bool sobol = true)
    {
        foreach (int n in new[] { 1024, 16384 })
        {
            double m = Emit(make, n, NscSourceSampling.Random).Average(g);
            double tolerance = 5 * sigma / Math.Sqrt(n);
            Assert.True(Math.Abs(m - mean) <= tolerance, $"{what}, {n} random rays: mean {m:R}, expected {mean:R} ± {tolerance:R} (5σ/√N).");
        }
        if (!sobol) return;
        const int N = 16384;
        double s = Emit(make, N, NscSourceSampling.Sobol).Average(g);
        double sobolTolerance = sigma / Math.Sqrt(N);
        Assert.True(Math.Abs(s - mean) <= sobolTolerance, $"{what}, {N} Sobol rays: mean {s:R}, expected {mean:R} ± {sobolTolerance:R} (σ/√N).");
    }

    private static double Deg(double radians) => radians * 180 / Math.PI;

    // ================================================================= Source Point and Source Ray

    /// <summary>Uniform in solid angle inside a 60° semi-cone (the reading in §72): cos θ uniform
    /// on [cos α, 1], so E[cos θ] = (1 + cos α)/2 = 3/4 and Var = (1 − cos α)²/12; E[l] = 0 with
    /// E[l²] = E[sin²θ]/2 = (1 − E[cos²θ])/2, E[cos²θ] = (1 + c + c²)/3 (derived).</summary>
    [Fact]
    public void SourcePoint_IsUniformInSolidAngleInsideItsCone()
    {
        Make make = (c, p) => new NscSourcePoint(60, c, p);
        Converges("cos θ", make, r => r.Direction.Z, 0.75, 0.5 / Math.Sqrt(12));
        double c2 = (1 + 0.5 + 0.25) / 3;
        Converges("l", make, r => r.Direction.X, 0, Math.Sqrt((1 - c2) / 2));
        Assert.All(Emit(make, 2000, NscSourceSampling.Random), r =>
        {
            Assert.Equal(Vector3D.Zero, r.Position);
            Assert.True(r.Direction.Z >= 0.5 - 1e-15);
            Assert.Equal(1, r.Direction.Length, 1e-15);
        });
    }

    [Fact]
    public void SourceRay_EmitsAlongItsNormalisedCosines_AfterPrePropagationAndReversal()
    {
        var rays = Emit((c, p) => new NscSourceRay(0, 3, 4, 0, c, p), 10, NscSourceSampling.Sobol,
            new NscSourceProperties { PrePropagation = 5 * Mm, ReverseRays = true });
        Assert.All(rays, r =>
        {
            // Pre-propagation moves the start 5 mm along (0, 0.6, 0.8), then the direction is reversed.
            Assert.Equal(0, r.Position.X, 1e-18);
            Assert.Equal(3 * Mm, r.Position.Y, 1e-15);
            Assert.Equal(4 * Mm, r.Position.Z, 1e-15);
            Assert.Equal(-0.6, r.Direction.Y, 1e-15);
            Assert.Equal(-0.8, r.Direction.Z, 1e-15);
        });
        Assert.Throws<ArgumentException>(() => new NscSourceRay(0, 0, 0));
    }

    // ================================================================= Source Ellipse / Rectangle

    /// <summary>Cosine mode, I ∝ cos^Cn θ per solid angle: μ = cos θ has density (Cn + 1)μ^Cn on
    /// [0, 1], so E[μ] = (Cn + 1)/(Cn + 2), E[μ²] = (Cn + 1)/(Cn + 3) (derived). Cn = 1 is
    /// Lambertian: E[μ] = 2/3, Var = 1/2 − 4/9 = 1/18. Start points uniform over the ellipse:
    /// ρ² = (x/a)² + (y/b)² is uniform on [0, 1], mean 1/2, Var 1/12.</summary>
    [Fact]
    public void SourceEllipse_CosineMode_FollowsCosToTheCn_AndFillsTheEllipse()
    {
        double a = 2 * Mm, b = 1 * Mm;
        Make lambert = (c, p) => new NscSourceEllipse(a, b, cosineExponent: 1, common: c, properties: p);
        Converges("Lambertian cos θ", lambert, r => r.Direction.Z, 2.0 / 3, Math.Sqrt(1.0 / 18));
        Converges("ellipse ρ²", lambert, r => Sq(r.Position.X / a) + Sq(r.Position.Y / b), 0.5, Math.Sqrt(1.0 / 12));
        Make cn3 = (c, p) => new NscSourceEllipse(a, b, cosineExponent: 3, common: c, properties: p);
        Converges("cos³ cos θ", cn3, r => r.Direction.Z, 4.0 / 5, Math.Sqrt(4.0 / 6 - 16.0 / 25));
    }

    /// <summary>Rectangle start points: x uniform on [−a, a], E[x²] = a²/3, Var = a⁴(1/5 − 1/9)
    /// (derived). Point mode: every ray lies on the line through the apparent point
    /// (Sx, Sy, −d), and travels toward +z for either sign of d; collimated mode: the direction is
    /// (Sx, Sy, √(1 − Sx² − Sy²)).</summary>
    [Fact]
    public void SourceRectangle_PointAndCollimatedModes_PassThroughTheApparentPoint()
    {
        double a = 3 * Mm, b = 2 * Mm;
        Make point = (c, p) => new NscSourceRectangle(a, b, sourceDistance: 20 * Mm, sourceX: 1 * Mm, sourceY: -2 * Mm, common: c, properties: p);
        Converges("rectangle x²", point, r => Sq(r.Position.X), a * a / 3, a * a * Math.Sqrt(1.0 / 5 - 1.0 / 9));
        foreach (double d in new[] { 20 * Mm, -15 * Mm })
        {
            var apparent = new Vector3D(1 * Mm, -2 * Mm, -d);
            var rays = Emit((c, p) => new NscSourceRectangle(a, b, d, sourceX: 1 * Mm, sourceY: -2 * Mm, common: c, properties: p), 500, NscSourceSampling.Sobol);
            Assert.All(rays, r =>
            {
                Assert.True(Math.Abs(r.Position.X) <= a && Math.Abs(r.Position.Y) <= b && r.Position.Z == 0);
                var off = Vector3D.Cross(apparent - r.Position, r.Direction).Length;
                Assert.True(off <= 1e-12 * (apparent - r.Position).Length, $"distance from the line {off:R}");
                Assert.True(r.Direction.Z > 0);
            });
        }
        var collimated = Emit((c, p) => new NscSourceRectangle(a, b, 0, sourceX: 0.3, sourceY: 0.4, common: c, properties: p), 50, NscSourceSampling.Sobol);
        Assert.All(collimated, r => { Assert.Equal(0.3, r.Direction.X, 1e-15); Assert.Equal(0.4, r.Direction.Y, 1e-15); Assert.Equal(Math.Sqrt(0.75), r.Direction.Z, 1e-15); });
        Assert.Throws<ArgumentException>(() => new NscSourceRectangle(a, b, 0, sourceX: 0.8, sourceY: 0.8));
    }

    /// <summary>The annular rectangle: uniform over [−a, a]×[−b, b] minus [−a0, a0]×[−b0, b0], so
    /// E[x²] = (a³b − a0³b0)/(3(ab − a0b0)) and E[x⁴] = (a⁵b − a0⁵b0)/(5(ab − a0b0)) (derived:
    /// ∫x² over a box 2p × 2q is 4p³q/3). Rejection sampling, so random only.</summary>
    [Fact]
    public void SourceRectangle_Annulus_LeavesTheHoleEmpty()
    {
        double a = 4, b = 3, a0 = 2, b0 = 1;   // in mm
        Make make = (c, p) => new NscSourceRectangle(a * Mm, b * Mm, cosineExponent: 1, minHalfWidthX: a0 * Mm, minHalfWidthY: b0 * Mm, common: c, properties: p);
        double area = a * b - a0 * b0;
        double m2 = (Math.Pow(a, 3) * b - Math.Pow(a0, 3) * b0) / (3 * area);
        double m4 = (Math.Pow(a, 5) * b - Math.Pow(a0, 5) * b0) / (5 * area);
        Converges("annulus x² (mm²)", make, r => Sq(r.Position.X / Mm), m2, Math.Sqrt(m4 - m2 * m2), sobol: false);
        Assert.All(Emit(make, 4000, NscSourceSampling.Sobol), r =>
            Assert.False(Math.Abs(r.Position.X) < a0 * Mm && Math.Abs(r.Position.Y) < b0 * Mm, $"a start point in the hole: {r.Position}"));
    }

    /// <summary>Gaussian mode, I(l, m) ∝ exp(−(Gx·l² + Gy·m²)) per solid angle over the forward
    /// hemisphere: E[l²] and E[l⁴] are ∫ g·e^(−Q) dΩ / ∫ e^(−Q) dΩ, computed here by a 96 × 192-point
    /// Gauss–Legendre product rule in (θ, φ) (an independent quadrature of a smooth integrand;
    /// its error is far below the Monte Carlo tolerance). Rejection sampling, so random only.</summary>
    [Fact]
    public void SourceEllipse_GaussianMode_MatchesTheFarFieldIntegral()
    {
        const double gx = 4, gy = 1;
        var (tn, tw) = GaussLegendre.Rule(96, 0, Math.PI / 2);
        var (pn, pw) = GaussLegendre.Rule(192, 0, 2 * Math.PI);
        double z = 0, s2 = 0, s4 = 0;
        for (int i = 0; i < tn.Length; i++)
            for (int j = 0; j < pn.Length; j++)
            {
                double l = Math.Sin(tn[i]) * Math.Cos(pn[j]), m = Math.Sin(tn[i]) * Math.Sin(pn[j]);
                double w = tw[i] * pw[j] * Math.Sin(tn[i]) * Math.Exp(-(gx * l * l + gy * m * m));
                z += w; s2 += w * l * l; s4 += w * l * l * l * l;
            }
        double mean = s2 / z, sigma = Math.Sqrt(s4 / z - mean * mean);
        Make make = (c, p) => new NscSourceEllipse(1 * Mm, 1 * Mm, gaussianX: gx, gaussianY: gy, common: c, properties: p);
        Converges("Gaussian far field l²", make, r => Sq(r.Direction.X), mean, sigma, sobol: false);
    }

    // ================================================================= Source Gaussian

    /// <summary>Start points normal with σ = w/2 in x (1/e² intensity radius w): E[x²] = w²/4,
    /// Var(x²) = 2σ⁴ (derived); every ray on the line through (0, 0, −Position).</summary>
    [Fact]
    public void SourceGaussian_HasTheOneOverESquaredRadius_AndItsApparentPoint()
    {
        double w = 2 * Mm;
        Make make = (c, p) => new NscSourceGaussian(w, 50 * Mm, c, p);
        Converges("Gaussian x²", make, r => Sq(r.Position.X), w * w / 4, Math.Sqrt(2) * w * w / 4);
        var apparent = new Vector3D(0, 0, -50 * Mm);
        Assert.All(Emit(make, 500, NscSourceSampling.Sobol), r =>
            Assert.True(Vector3D.Cross(apparent - r.Position, r.Direction).Length <= 1e-12 * (apparent - r.Position).Length));
        Assert.All(Emit((c, p) => new NscSourceGaussian(w, 0, c, p), 50, NscSourceSampling.Sobol), r => Assert.Equal(Vector3D.UnitZ, r.Direction));
    }

    // ================================================================= Source Diode

    /// <summary>θx = atan(l/n) has density ∝ exp(−2(θx/αx)^(2Gx)) on (−90°, 90°). With Gx = 1 and
    /// αx = 10° it is a normal law of σ = αx/2 = 5° truncated at 18σ (mass beyond e^(−162)):
    /// E[θx²] = 25 deg², Var = 2σ⁴ = 1250 deg⁴ (derived). θy with Gy = 3, αy = 20°: E[θy²] and
    /// E[θy⁴] by an independent composite Gauss–Legendre quadrature (180 panels × 20 nodes). The
    /// start points with Hx = 1, sx = 1 mm, Wx = 10 mm: normal with σ = 0.5 mm truncated at 20σ,
    /// E[x²] = 0.25 mm², Var = 2σ⁴; Wy = 0 puts y at 0.</summary>
    [Fact]
    public void SourceDiode_FarFieldAndNearField_FollowTheSuperGaussians()
    {
        Make make = (c, p) => new NscSourceDiode(0, 10, 1, 20, 3, 1, 1, 0, 0, 10 * Mm, 1 * Mm, 1, 0, 0, 1, c, p);
        Converges("diode θx²", make, r => Sq(Deg(Math.Atan2(r.Direction.X, r.Direction.Z))), 25, Math.Sqrt(1250));
        double z = 0, m2 = 0, m4 = 0;
        var (gn, gw) = GaussLegendre.Rule(20);
        for (int k = 0; k < 180; k++)
            for (int i = 0; i < gn.Length; i++)
            {
                double t = -90 + k + 0.5 * (gn[i] + 1), w = 0.5 * gw[i] * Math.Exp(-2 * Math.Pow(Math.Abs(t / 20), 6));
                z += w; m2 += w * t * t; m4 += w * t * t * t * t;
            }
        double mean = m2 / z;
        Converges("diode θy², super-Gaussian 3", make, r => Sq(Deg(Math.Atan2(r.Direction.Y, r.Direction.Z))), mean, Math.Sqrt(m4 / z - mean * mean));
        double sigma = 0.5 * Mm;
        Converges("diode x²", make, r => Sq(r.Position.X), sigma * sigma, Math.Sqrt(2) * sigma * sigma);
        Assert.All(Emit(make, 200, NscSourceSampling.Sobol), r => Assert.Equal(0, r.Position.Y));
    }

    [Fact]
    public void SourceDiode_Astigmatism_PutsStartsOnTheXLine_AndArraysShareThePower()
    {
        double astig = 2 * Mm;
        var rays = Emit((c, p) => new NscSourceDiode(astig, 10, 1, 5, 1, common: c, properties: p), 500, NscSourceSampling.Sobol);
        Assert.All(rays, r =>
        {
            Assert.Equal(0, r.Position.Y);
            Assert.Equal(astig * r.Direction.X / r.Direction.Z, r.Position.X, 1e-15);
        });
        // Three diodes 1 mm apart about the origin: x ∈ {−1, 0, 1} mm, a third of the rays each
        // (indicator of the first diode: mean 1/3, σ = √(2/9)).
        Make array = (c, p) => new NscSourceDiode(0, 10, 1, 10, 1, 3, 1, 1 * Mm, 0, common: c, properties: p);
        Assert.All(Emit(array, 300, NscSourceSampling.Sobol), r =>
            Assert.Contains(Math.Round(r.Position.X / Mm, 12), new[] { -1.0, 0.0, 1.0 }));
        Converges("first diode", array, r => r.Position.X < -0.5 * Mm ? 1 : 0, 1.0 / 3, Math.Sqrt(2.0 / 9));
    }

    // ================================================================= Source Two Angle

    /// <summary>Rectangular cone uniform in angle: θx uniform on [−αx, αx], E[θx²] = αx²/3,
    /// Var = αx⁴(1/5 − 1/9). Uniform on a distant plane: p = l/n uniform on [−tan αx, tan αx], the
    /// same moments in tan αx. Elliptical cone uniform in angle: (θx/αx)² + (θy/αy)² uniform on
    /// [0, 1]. (All derived from the reading in §72.)</summary>
    [Fact]
    public void SourceTwoAngle_IsUniformInAngleOrOnADistantPlane()
    {
        double ax = 20, ay = 10;
        double k = Math.Sqrt(1.0 / 5 - 1.0 / 9);
        Make angle = (c, p) => new NscSourceTwoAngle(1 * Mm, 1 * Mm, ax, ay, false, false, true, c, p);
        Converges("θx² uniform in angle", angle, r => Sq(Deg(Math.Atan2(r.Direction.X, r.Direction.Z))), ax * ax / 3, ax * ax * k);
        double t = Math.Tan(ax * Math.PI / 180);
        Make plane = (c, p) => new NscSourceTwoAngle(1 * Mm, 1 * Mm, ax, ay, false, false, false, c, p);
        Converges("(l/n)² uniform on a plane", plane, r => Sq(r.Direction.X / r.Direction.Z), t * t / 3, t * t * k);
        Make ellipse = (c, p) => new NscSourceTwoAngle(2 * Mm, 1 * Mm, ax, ay, true, true, true, c, p);
        Converges("elliptical cone ρ²", ellipse, r =>
            Sq(Deg(Math.Atan2(r.Direction.X, r.Direction.Z)) / ax) + Sq(Deg(Math.Atan2(r.Direction.Y, r.Direction.Z)) / ay), 0.5, Math.Sqrt(1.0 / 12));
        Converges("elliptical shape ρ²", ellipse, r => Sq(r.Position.X / (2 * Mm)) + Sq(r.Position.Y / (1 * Mm)), 0.5, Math.Sqrt(1.0 / 12));
    }

    // ================================================================= Source Radial

    /// <summary>The polar angle has density I(θ)·sin θ. Constant data (a natural spline through
    /// equal values is that constant): μ = cos θ uniform on [0, 1], mean 1/2, Var 1/12. Data
    /// linear in θ (the natural spline reproduces it exactly): density θ·sin θ on [0, π/2], with
    /// ∫θ sin θ dθ = 1, ∫θ sin θ cos θ dθ = π/8 and ∫θ sin θ cos²θ dθ = 2/9 (derived by parts), so
    /// E[μ] = π/8 and Var = 2/9 − π²/64.</summary>
    [Fact]
    public void SourceRadial_FollowsItsSplineTimesSinTheta()
    {
        Make flat = (c, p) => new NscSourceRadial(1 * Mm, 1 * Mm, 0, 90, new double[] { 1, 1, 1, 1, 1 }, c, p);
        Converges("flat radial cos θ", flat, r => r.Direction.Z, 0.5, Math.Sqrt(1.0 / 12));
        Make linear = (c, p) => new NscSourceRadial(-1 * Mm, 1 * Mm, 0, 90, new double[] { 0, 22.5, 45, 67.5, 90 }, c, p);
        Converges("linear radial cos θ", linear, r => r.Direction.Z, Math.PI / 8, Math.Sqrt(2.0 / 9 - Math.PI * Math.PI / 64));
        Assert.All(Emit(linear, 500, NscSourceSampling.Sobol), r => Assert.True(Sq(r.Position.X / Mm) + Sq(r.Position.Y / Mm) <= 1 + 1e-12));

        // Data (0, 0, 1, 0, 0): the natural spline on [0°, 22.5°] is (3/7)(t³ − t) < 0 in units of
        // the knot spacing (derived from the spline's moment equations), so a note says so.
        var bump = new NscSourceRadial(1 * Mm, 1 * Mm, 0, 90, new double[] { 0, 0, 1, 0, 0 });
        Assert.Contains(bump.Notes, n => n.Contains("negative"));
        Assert.Throws<ArgumentException>(() => new NscSourceRadial(1 * Mm, 1 * Mm, 0, 90, new double[] { 1, 1, 1, 1 }));
    }

    // ================================================================= filament, tube, volumes

    /// <summary>Isotropic directions: cos θ uniform on [−1, 1], E[n²] = 1/3, Var = 1/5 − 1/9.
    /// Filament: t = z/L uniform (mean 1/2, Var 1/12) on the helix (R cos 2πNt, R sin 2πNt, Lt).
    /// Tube: on the wall r = R, z/L uniform.</summary>
    [Fact]
    public void FilamentAndTube_StartOnTheirSurfaces_AndEmitIsotropically()
    {
        double l = 10 * Mm, r0 = 2 * Mm, turns = 3.5;
        Make filament = (c, p) => new NscSourceFilament(l, r0, turns, c, p);
        Converges("filament z/L", filament, r => r.Position.Z / l, 0.5, Math.Sqrt(1.0 / 12));
        Converges("isotropic n²", filament, r => Sq(r.Direction.Z), 1.0 / 3, Math.Sqrt(1.0 / 5 - 1.0 / 9));
        Assert.All(Emit(filament, 500, NscSourceSampling.Sobol), r =>
        {
            double phi = 2 * Math.PI * turns * r.Position.Z / l;
            Assert.Equal(r0 * Math.Cos(phi), r.Position.X, 1e-15);
            Assert.Equal(r0 * Math.Sin(phi), r.Position.Y, 1e-15);
        });
        Make tube = (c, p) => new NscSourceTube(l, r0, c, p);
        Converges("tube z/L", tube, r => r.Position.Z / l, 0.5, Math.Sqrt(1.0 / 12));
        Assert.All(Emit(tube, 500, NscSourceSampling.Sobol), r => Assert.Equal(r0, Math.Sqrt(Sq(r.Position.X) + Sq(r.Position.Y)), 1e-15));
    }

    /// <summary>Uniform in the volume: the ellipsoid's s = Σ(xᵢ/Wᵢ)² = r² with r³ uniform, so
    /// E[s] = 3/5, E[s²] = 3/7 (derived: E[r^k] = 3/(k + 3)); the cylinder's (x/Wx)² + (y/Wy)²
    /// uniform on [0, 1]; the box's (x/Wx)² with x/Wx uniform on [−1, 1], E = 1/3, Var = 1/5 − 1/9.</summary>
    [Fact]
    public void VolumeSources_FillTheirVolumesUniformly()
    {
        double wx = 3 * Mm, wy = 2 * Mm, wz = 1 * Mm;
        Make ellipsoid = (c, p) => new NscSourceVolume(NscVolumeShape.Ellipse, wx, wy, wz, c, p);
        Func<NscLaunchRay, double> s = r => Sq(r.Position.X / wx) + Sq(r.Position.Y / wy) + Sq(r.Position.Z / wz);
        Converges("ellipsoid s", ellipsoid, s, 3.0 / 5, Math.Sqrt(3.0 / 7 - 9.0 / 25));
        Assert.All(Emit(ellipsoid, 1000, NscSourceSampling.Sobol), r => Assert.True(s(r) <= 1 + 1e-12));
        Make cylinder = (c, p) => new NscSourceVolume(NscVolumeShape.Cylinder, wx, wy, wz, c, p);
        Converges("cylinder ρ²", cylinder, r => Sq(r.Position.X / wx) + Sq(r.Position.Y / wy), 0.5, Math.Sqrt(1.0 / 12));
        Converges("cylinder (z/Wz)²", cylinder, r => Sq(r.Position.Z / wz), 1.0 / 3, Math.Sqrt(1.0 / 5 - 1.0 / 9));
        Make box = (c, p) => new NscSourceVolume(NscVolumeShape.Rectangle, wx, wy, wz, c, p);
        Converges("box (x/Wx)²", box, r => Sq(r.Position.X / wx), 1.0 / 3, Math.Sqrt(1.0 / 5 - 1.0 / 9));
        Converges("box isotropic n²", box, r => Sq(r.Direction.Z), 1.0 / 3, Math.Sqrt(1.0 / 5 - 1.0 / 9));
    }

    // ================================================================= power, spectrum, polarization

    /// <summary>Each ray carries P/N (one rounding of at most 2⁻⁵³·P/N, the same for every ray) and
    /// the rays are summed with compensated summation, so the total is P to a few units in the last
    /// place; 1e-14·P leaves a factor of about 50.</summary>
    [Fact]
    public void TotalEmittedPower_IsTheStatedPower_AndArraysMultiplyIt()
    {
        foreach (int n in new[] { 1, 7, 1000, 12345 })
        {
            var set = EmitSet((c, p) => new NscSourcePoint(30, c, p), n, NscSourceSampling.Random, power: 2.7);
            Assert.Equal(n, set.Rays);
            Assert.True(Math.Abs(set.TracedPower - 2.7) <= 1e-14 * 2.7, $"{n} rays: {set.TracedPower:R}");
        }
        // A 3 × 2 rectangular array of 1 W sources gives 6 W ("a 3 x 3 array of 1 watt sources will
        // produce 9 watts"; help, "Sources"), each element at (i·Δx, j·Δy).
        var array = new NscSourceArray { Type = NscSourceArrayType.Rectangular, Count1 = 3, Count2 = 2, Size1 = 1 * Mm, Size2 = 2 * Mm };
        var arraySet = EmitSet((c, p) => new NscSourcePoint(10, c, p), 100, NscSourceSampling.Sobol, new NscSourceProperties { Array = array }, power: 1);
        Assert.Equal(600, arraySet.Rays);
        Assert.Equal(6, arraySet.StatedPower);
        Assert.True(Math.Abs(arraySet.TracedPower - 6) <= 1e-14 * 6);
        var rays = arraySet.Emissions.Single().Rays;
        for (int k = 0; k < 600; k++)
        {
            int e = k / 100;
            Assert.Equal(new Vector3D(e % 3 * 1 * Mm, e / 3 * 2 * Mm, 0), rays[k].Position);
        }
        var hex = new NscSourceArray { Type = NscSourceArrayType.Hexapolar, Count1 = 3, Size1 = 1 };
        Assert.Equal(1 + 6 + 12, hex.Offsets("test").Count);
        Assert.Equal(0, hex.Offsets("test")[1].X, 1e-15);   // ring 2 starts at +90°
        Assert.Equal(1, hex.Offsets("test")[1].Y, 1e-15);
        Assert.Throws<NotSupportedException>(() => new NscSourceArray { Type = NscSourceArrayType.Hexagonal }.Offsets("test"));
    }

    /// <summary>Wavenumber 0 draws system wavelengths by weight: with weights 1 and 3 the fraction
    /// at the second is a Bernoulli mean, 3/4 with σ = √(3/16); each wavelength's power is its
    /// ray count × P/N, and the total stays P.</summary>
    [Fact]
    public void SystemWavelengths_AreDrawnByWeight_AndThePowerSplitsWithTheRays()
    {
        var options = new NscSourceEmitOptions { SystemWavelengths = new[] { new NscSpectralLine(0.5, 1), new NscSpectralLine(0.6, 3) } };
        const int N = 16384;
        foreach (var sampling in new[] { NscSourceSampling.Random, NscSourceSampling.Sobol })
        {
            var set = EmitSet((c, p) => new NscSourcePoint(30, c, p), N, sampling, options: options);
            Assert.Equal(2, set.Emissions.Count);
            var second = set.Emissions.Single(e => e.WavelengthMicrometers == 0.6);
            double fraction = (double)second.Rays.Count / N, sigma = Math.Sqrt(3.0 / 16);
            double tolerance = (sampling == NscSourceSampling.Random ? 5 : 1) * sigma / Math.Sqrt(N);
            Assert.True(Math.Abs(fraction - 0.75) <= tolerance, $"{sampling}: fraction {fraction:R} ± {tolerance:R}");
            foreach (var e in set.Emissions)
                Assert.True(Math.Abs(e.Power - e.Rays.Count * (2.0 / N)) <= 1e-14 * 2);
            Assert.True(Math.Abs(set.Emissions.Sum(e => e.Power) - 2) <= 1e-14 * 2);
            Assert.Equal(N, set.Emissions.Sum(e => e.SourceRayNumbers!.Count));
        }
        var one = EmitSet((c, p) => new NscSourcePoint(30, c with { Wavenumber = 2 }, p), 100, NscSourceSampling.Sobol, options: options);
        Assert.Equal(0.6, one.Emissions.Single().WavelengthMicrometers);
        Assert.Throws<ArgumentException>(() => EmitSet((c, p) => new NscSourcePoint(30, c with { Wavenumber = 3 }, p), 10, NscSourceSampling.Sobol, options: options));
    }

    /// <summary>The black body's weights are Planck's B_λ. Checked against the ratio
    /// B(λ₁)/B(λ₂) = (λ₂/λ₁)⁵·(e^(c₂/λ₂T) − 1)/(e^(c₂/λ₁T) − 1) with the second radiation constant
    /// c₂ = 1.438 776 877 × 10⁻² m K (NIST, CODATA 2022, "second radiation constant",
    /// plans/sources/optics/op-77-help/nist/Value_c2.html; given to 10 digits, so the exponent is
    /// good to about 1e-9 relative and the ratio to 1e-7), and against Wien's law: the peak is at
    /// b/T with b = 2.897 771 955 × 10⁻³ m K (NIST, CODATA 2022, "Wien wavelength displacement law
    /// constant", Value_bwien.html), within one line spacing.</summary>
    [Fact]
    public void BlackBodySpectrum_FollowsPlancksLaw_AndPeaksAtWiensWavelength()
    {
        const double c2 = 1.438776877e-2, b = 2.897771955e-3, t = 5000;
        var spectrum = new NscSpectrum { Model = NscColorModel.BlackBodySpectrum, TemperatureKelvin = t, Count = 701, FromMicrometers = 0.3, ToMicrometers = 1.0 };
        var lines = spectrum.Lines(Array.Empty<NscSpectralLine>(), 0, x => x, "test");
        Assert.Equal(701, lines.Count);
        Assert.Equal(0.3, lines[0].Micrometers);
        Assert.Equal(1.0, lines[^1].Micrometers, 1e-15);
        double Ratio(double l1, double l2) => Math.Pow(l2 / l1, 5) * (Math.Exp(c2 / (l2 * 1e-6 * t)) - 1) / (Math.Exp(c2 / (l1 * 1e-6 * t)) - 1);
        for (int i = 0; i < 700; i += 50)
            Assert.Equal(1, lines[i].Weight / lines[700].Weight / Ratio(lines[i].Micrometers, lines[700].Micrometers), 1e-7);
        var peak = lines.MaxBy(l => l.Weight);
        Assert.True(Math.Abs(peak.Micrometers - b / t * 1e6) <= 0.001, $"peak {peak.Micrometers} µm, Wien {b / t * 1e6} µm");

        var uniform = new NscSpectrum { Model = NscColorModel.UniformPowerSpectrum, Count = 5, FromMicrometers = 0.4, ToMicrometers = 0.8 }
            .Lines(Array.Empty<NscSpectralLine>(), 0, x => x, "test");
        Assert.Equal(new[] { 0.4, 0.5, 0.6, 0.7, 0.8 }, uniform.Select(l => Math.Round(l.Micrometers, 12)));
        Assert.All(uniform, l => Assert.Equal(1, l.Weight));

        var e = Assert.Throws<ArgumentException>(() => new NscSpectrum { Model = NscColorModel.D65White }.Lines(Array.Empty<NscSpectralLine>(), 0, x => x, "Object 3"));
        Assert.Contains("OP-79", e.Message);
        var table = new NscSpectrum { Model = NscColorModel.SpectrumFile, Table = new[] { new NscSpectralLine(0.5, 1), new NscSpectralLine(0.4, 1), new NscSpectralLine(0.6, 1) } };
        Assert.Contains("ascending", Assert.Throws<ArgumentException>(() => table.Lines(Array.Empty<NscSpectralLine>(), 0, x => x, "test")).Message);
    }

    /// <summary>Lumens are converted with P_W = P_lm/(683·V̄) (683 lm/W from the SI candela; NIST,
    /// "Definitions of the SI base units"). V here is a test function (not CIE data): V = 0.5 at
    /// every wavelength gives 2 lm → 2/(683·0.5) W.</summary>
    [Fact]
    public void Lumens_NeedTheLuminousEfficiency_AndConvertToWatts()
    {
        var lumens = Mono with { Units = SourceUnit.Lumens };
        var e = Assert.Throws<NotSupportedException>(() => EmitSet((c, p) => new NscSourcePoint(30, c, p), 10, NscSourceSampling.Sobol, options: lumens));
        Assert.Contains("OP-79", e.Message);
        var set = EmitSet((c, p) => new NscSourcePoint(30, c, p), 1000, NscSourceSampling.Sobol, options: lumens with { LuminousEfficiency = _ => 0.5 });
        double watts = 2 / (683 * 0.5);
        Assert.True(Math.Abs(set.TracedPower - watts) <= 1e-14 * watts, $"{set.TracedPower:R} W, expected {watts:R}");
        Assert.Equal(2, set.StatedPower);
    }

    /// <summary>The X Axis Reference method: for K = ẑ, S = x̂ and P = ŷ, so (Jx, Jy) = (1, 1) with
    /// phases (0°, 90°) is (1, i, 0)/√2; any field is transverse and of unit norm.</summary>
    [Fact]
    public void JonesVectors_BecomeTransverseFields_ByTheXAxisReference()
    {
        var circular = NscSourceEmitter.Field(new NscSourcePolarization { Random = false, Jx = 1, Jy = 1, YPhaseDegrees = 90 }, Vector3D.UnitZ);
        Assert.Equal(1 / Math.Sqrt(2), circular.X.Real, 1e-15);
        Assert.Equal(0, circular.X.Imaginary, 1e-15);
        Assert.Equal(0, circular.Y.Real, 1e-15);
        Assert.Equal(1 / Math.Sqrt(2), circular.Y.Imaginary, 1e-15);
        Assert.Equal(Complex.Zero, circular.Z);

        var polarized = new NscSourceProperties { Polarization = new NscSourcePolarization { Random = false, Jx = 0.6, Jy = 0.8, XPhaseDegrees = 30 } };
        Assert.All(Emit((c, p) => new NscSourcePoint(60, c, p), 300, NscSourceSampling.Sobol, polarized), r =>
        {
            Assert.NotNull(r.Field);
            Assert.Equal(0, r.Field!.Value.Along(r.Direction).Magnitude, 1e-15);
            Assert.Equal(1, r.Field.Value.NormSquared, 1e-14);
        });
        Assert.All(Emit((c, p) => new NscSourcePoint(60, c, p), 10, NscSourceSampling.Sobol), r => Assert.Null(r.Field));
        var e = Assert.Throws<InvalidOperationException>(() => EmitSet((c, p) => new NscSourceRay(1, 0, 0, 0, c, p), 1, NscSourceSampling.Sobol, polarized));
        Assert.Contains("X Axis Reference", e.Message);
    }

    // ================================================================= reproducibility

    /// <summary>Ray k depends only on k: the first 1000 rays of a 2000-ray emission are the 1000
    /// rays of a 1000-ray one, wavelength included; any thread count gives the same rays.</summary>
    [Fact]
    public void RayK_DependsOnlyOnK_AndNotOnTheThreadCount()
    {
        var options = new NscSourceEmitOptions { SystemWavelengths = new[] { new NscSpectralLine(0.5, 1), new NscSpectralLine(0.6, 2) } };
        foreach (var sampling in new[] { NscSourceSampling.Sobol, NscSourceSampling.Random })
        {
            Make make = (c, p) => new NscSourceEllipse(2 * Mm, 1 * Mm, cosineExponent: 2, common: c, properties: p);
            static Dictionary<long, (double, NscLaunchRay)> ByNumber(NscSourceEmissionSet s) =>
                s.Emissions.SelectMany(e => e.Rays.Select((r, i) => (e.SourceRayNumbers![i], e.WavelengthMicrometers, r))).ToDictionary(t => t.Item1, t => (t.WavelengthMicrometers, t.r));
            var small = ByNumber(EmitSet(make, 1000, sampling, options: options));
            var large = ByNumber(EmitSet(make, 10000, sampling, options: options, threads: 1));
            var parallel = ByNumber(EmitSet(make, 10000, sampling, options: options, threads: 8));
            for (long k = 0; k < 1000; k++)
            {
                Assert.Equal(small[k].Item1, large[k].Item1);
                Assert.Equal(small[k].Item2.Position, large[k].Item2.Position);
                Assert.Equal(small[k].Item2.Direction, large[k].Item2.Direction);
            }
            for (long k = 0; k < 10000; k++)
            {
                Assert.Equal(large[k].Item1, parallel[k].Item1);
                Assert.Equal(large[k].Item2.Position, parallel[k].Item2.Position);
                Assert.Equal(large[k].Item2.Direction, parallel[k].Item2.Direction);
            }
        }
    }

    [Fact]
    public void TheInverseCdf_InvertsKnownDistributions()
    {
        var uniform = new NscInverseCdf(_ => 1, 0, 1, "uniform");
        var ramp = new NscInverseCdf(x => 2 * x, 0, 1, "ramp");     // F = x², F⁻¹(u) = √u
        foreach (double u in new[] { 0.0, 1e-9, 0.1, 0.37, 0.5, 0.9, 0.999999 })
        {
            Assert.Equal(u, uniform.Sample(u), 1e-14);
            Assert.Equal(Math.Sqrt(u), ramp.Sample(u), 1e-12);
            Assert.Equal(u, ramp.Cdf(ramp.Sample(u)), 1e-13);
        }
        Assert.Throws<ArgumentException>(() => new NscInverseCdf(_ => 0, 0, 1, "zero"));
    }

    // ================================================================= the run and the ledger

    private static NscScene PointAndDetector(int rays, double power) => new()
    {
        Objects = new[]
        {
            new NscObject { Geometry = new NscSourcePoint(30, new NscSourceCommon { AnalysisRays = rays, Power = power }) },
            new NscObject
            {
                Geometry = new NscRectangle(50 * Mm, 50 * Mm), Placement = new NscPlacement(Z: 10 * Mm), IsDetectingStandIn = true,
            },
        },
    };

    /// <summary>Gate: a source point inside a 30° cone with a detecting rectangle 10 mm away
    /// (half width 50 mm, far beyond the cone's 10·tan 30° = 5.8 mm): every ray is detected, so
    /// Detected = Emitted = the stated 2 W, split over two wavelengths, and the ledger closes; the
    /// result is the same to the bit at one thread and at many. Tolerance 1e-14·P: compensated
    /// sums of equal ray powers (see the power test).</summary>
    [Fact]
    public void SourceRun_ClosesTheLedger_WithTheDetectingStandIn()
    {
        var options = new NscSourceEmitOptions { SystemWavelengths = new[] { new NscSpectralLine(0.5, 1), new NscSpectralLine(0.6, 1) } };
        var scene = PointAndDetector(4096, 2);
        var result = NscSourceRun.Run(scene, new NscTraceSettings { MaxDegreeOfParallelism = 1 }, options);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal(4096, result.Runs.Sum(r => r.Rays));
        Assert.Equal("W", result.Unit);
        var ledger = result.Ledger;
        Assert.True(Math.Abs(ledger.Emitted - 2) <= 1e-14 * 2, $"emitted {ledger.Emitted:R}");
        Assert.True(Math.Abs(ledger.Detected - 2) <= 1e-14 * 2, $"detected {ledger.Detected:R}");
        Assert.True(Math.Abs(ledger.Residual) <= 1e-14 * 2);
        Assert.True(Math.Abs(ledger.DetectedByObject[2] - 2) <= 1e-14 * 2);
        var summary = Assert.Single(result.Sources);
        Assert.Equal(1, summary.ObjectNumber);
        Assert.True(Math.Abs(summary.TracedPower - 2) <= 1e-14 * 2);

        var parallel = NscSourceRun.Run(scene, new NscTraceSettings(), options);
        Assert.Equal(BitConverter.DoubleToInt64Bits(ledger.Detected), BitConverter.DoubleToInt64Bits(parallel.Ledger.Detected));
        Assert.Equal(BitConverter.DoubleToInt64Bits(ledger.Emitted), BitConverter.DoubleToInt64Bits(parallel.Ledger.Emitted));
        Assert.Equal(result.Runs.Select(r => r.Rays), parallel.Runs.Select(r => r.Rays));
    }

    /// <summary>A volume source inside a glass sphere, named by Inside Of: the ledger closes to
    /// rounding (escaped + not traced + ... = emitted, OP-70's compensated sums); without Inside Of
    /// the run is refused, naming the source object.</summary>
    [Fact]
    public void SourceRun_InsideAGlassSphere_NeedsInsideOf_AndClosesTheLedger()
    {
        NscScene Scene(int insideOf) => new()
        {
            Objects = new[]
            {
                new NscObject { Geometry = new NscSphere(10 * Mm), Material = NscMaterial.Of(ParaxialMedium.Constant(1.5)) },
                new NscObject
                {
                    Geometry = new NscSourceVolume(NscVolumeShape.Ellipse, 2 * Mm, 2 * Mm, 2 * Mm, new NscSourceCommon { AnalysisRays = 2000, Power = 3 }),
                    InsideOf = insideOf,
                },
            },
        };
        var result = NscSourceRun.Run(Scene(1), new NscTraceSettings(), Mono);
        Assert.True(Math.Abs(result.Ledger.Emitted - 3) <= 1e-14 * 3);
        Assert.True(Math.Abs(result.Ledger.Residual) <= 1e-13 * 3, $"residual {result.Ledger.Residual:R}");
        Assert.True(result.Ledger.Escaped > 2.5);   // most light leaves the sphere (normal-ish incidence, R ≈ 4 %)

        var e = Assert.Throws<ArgumentException>(() => NscSourceRun.Run(Scene(0), new NscTraceSettings(), Mono));
        Assert.Contains("Inside Of", e.Message);
        Assert.Contains("source object 2", e.Message);
    }

    [Fact]
    public void SourceRun_RefusesAScene_WithoutSources()
    {
        var scene = new NscScene { Objects = new[] { new NscObject { Geometry = new NscRectangle(1, 1) } } };
        Assert.Throws<ArgumentException>(() => NscSourceRun.Run(scene, new NscTraceSettings(), Mono));
        Assert.Throws<ArgumentException>(() => NscSourceRun.Run(PointAndDetector(10, 1), new NscTraceSettings(), Mono, new[] { 2 }));
    }

    // ================================================================= descriptors and the saved scene

    [Fact]
    public void Descriptors_ListEverySourceType_AndTheBuiltOnesCreateFromTheirDefaults()
    {
        Assert.Equal(21, NscSourceTypes.Descriptors.Count);
        Assert.Equal(13, NscSourceTypes.BuiltNames.Count);
        foreach (var d in NscSourceTypes.Descriptors)
        {
            Assert.Equal("# Analysis Rays", d.Parameters[1].Name);
            Assert.Equal(OpenSim.Optics.NonSequential.Editing.NscObjectCategory.Source, d.Category);
            // Source Radial's descriptor carries its intensity columns, so its defaults are complete.
            var values = d.Parameters.Select(p => p.Default).ToList();
            if (d.IsBuilt)
            {
                var s = NscSourceTypes.Create(d.TypeName, values, LensUnit.Millimeter);
                Assert.Equal(d.TypeName, s.TypeName);
                Assert.False(s.IsVolume);
            }
            else
            {
                var e = Assert.Throws<NotSupportedException>(() => NscSourceTypes.Create(d.TypeName, values, LensUnit.Millimeter));
                Assert.Contains("not built", e.Message);
            }
        }
        Assert.Null(NscSourceTypes.Find("Sphere"));
    }

    private static OpticsAnalysisContext Context(params OpticsNscObjectSettings[] rows) =>
        new("P", new OpticsSettings { NonSequential = new OpticsNonSequentialSettings { Objects = rows.ToList() } });

    [Fact]
    public void TheSavedScene_ReadsSourceRows_InLensUnits()
    {
        var context = Context(
            new OpticsNscObjectSettings
            {
                Type = "Source Ellipse",
                Parameters = new List<double> { 10, 500, 2, 0, 0, 3, 2, 0, 0, 0, 0, 0.1, 0.2, 0, 0 },
                Source = new OpticsNscSourceSettings { Sampling = "Random", ArrayType = "Circular", ArrayCount1 = 4, ArraySize1 = 5, PrePropagation = 1 },
            },
            new OpticsNscObjectSettings { Type = "Rectangle", Parameters = new List<double> { 50, 50 }, Z = 30, DetectingStandIn = true });
        var scene = ProjectNonSequential.Scene(context, LensUnit.Millimeter)!;
        var source = Assert.IsType<NscSourceEllipse>(scene.Objects[0].Geometry);
        Assert.Equal(3 * Mm, source.HalfWidthX, 1e-18);
        Assert.Equal(0.1, source.SourceX);                       // a direction cosine: source distance 0
        Assert.Equal(500, source.Common.AnalysisRays);
        Assert.Equal(NscSourceSampling.Random, source.Properties.Sampling);
        Assert.Equal(5 * Mm, source.Properties.Array.Size1, 1e-18);
        Assert.Equal(1 * Mm, source.Properties.PrePropagation, 1e-18);

        var options = ProjectNscSources.EmitOptions(context, NscEmissionPurpose.Layout);
        Assert.NotEmpty(options.SystemWavelengths);
        Assert.Equal(SourceUnit.Watts, options.Units);
        var set = NscSourceEmitter.Emit(scene, 1, new NscTraceSettings(), options);
        Assert.Equal(4 * 10, set.Rays);                          // the layout count, per array element

        var file = Assert.Throws<ArgumentException>(() => ProjectNonSequential.Scene(Context(new OpticsNscObjectSettings { Type = "Source File", Parameters = new List<double> { 1, 1, 1, 0, 0 } }), LensUnit.Millimeter));
        Assert.Contains("OP-78", file.Message);
        var dll = Assert.Throws<ArgumentException>(() => ProjectNonSequential.Scene(Context(new OpticsNscObjectSettings { Type = "Source DLL", Parameters = new List<double> { 1, 1, 1, 0, 0 } }), LensUnit.Millimeter));
        Assert.Contains("OP-100", dll.Message);
        var bad = Assert.Throws<ArgumentException>(() => ProjectNonSequential.Scene(Context(new OpticsNscObjectSettings
        {
            Type = "Source Point", Parameters = new List<double> { 1, 1, 1, 0, 0, 10 }, Source = new OpticsNscSourceSettings { Sampling = "sobol" },
        }), LensUnit.Millimeter));
        Assert.Contains("sampling method", bad.Message);
    }

    private static double Sq(double x) => x * x;
}
