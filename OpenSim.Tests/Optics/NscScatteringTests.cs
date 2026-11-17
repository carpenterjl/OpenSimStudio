using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Coatings;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.Scattering;
using OpenSim.Optics.NonSequential.Sources;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-80: surface scattering and importance sampling (docs/Optics-Conventions.md §74). Gate:
/// each model's total integrated scatter equals its stated fraction; a Lambertian surface in a
/// sphere reproduces the integrating-sphere closed form; importance-sampled and brute-force
/// estimates agree within their error bars.
/// <para>
/// <b>Sources and closed forms.</b> The BSDF formulas are OpticStudio's help ("Available Scatter
/// Models": Lambertian 1/π, Gaussian A·exp(−|x|²/σ²), ABg A/(B + |x|^g)); the TIS is the help's
/// integral ∫∫ BSDF cos θ sin θ dθ dφ ("Bi-Directional Scatter Distribution Function"), which with
/// ρ = sin θ is ∫∫ BSDF ρ dρ dφ over the unit disk of projected directions (derived). Every other
/// expected value here is derived in the test where it is used. The ABg values A = 0.002,
/// B = 0.0005, g = 2 are the help's example curve ("A typical ABg BSDF curve is shown below for the
/// parameters A = 0.002, B = 0.0005, g = 2.0"); other parameters are arbitrary test inputs.
/// Stover's and Freniere's ABg papers were not available; nothing here is taken from them.
/// </para>
/// <para>
/// <b>Monte Carlo tolerances.</b> All runs use fixed seeds (SplitMix64 streams). A count of N
/// independent trials with probability p has standard deviation √(N p (1 − p)); a mean of N
/// independent per-ray values has standard error s/√N. Every Monte Carlo comparison allows 5
/// standard errors (two-sided normal tail 5.7e-7), stated at each assertion.
/// </para>
/// </summary>
public sealed class NscScatteringTests
{
    private static void Within(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    /// <summary>∫ g(β) dβ over the unit disk in polar coordinates about its centre: Gauss–Legendre
    /// in ρ ∈ [0, 1] (weight ρ), trapezoid in ψ (periodic). Independent of the program's
    /// <see cref="NscScatterMath.DiskIntegral"/>, which works about the specular point.</summary>
    private static double OverDisk(Func<double, double, double> g, int radial, int angular)
    {
        var (nodes, weights) = GaussLegendre.Rule(radial, 0, 1);
        double sum = 0;
        for (int i = 0; i < nodes.Length; i++)
            for (int k = 0; k < angular; k++)
            {
                double psi = 2 * Math.PI * k / angular;
                sum += weights[i] * nodes[i] * g(nodes[i] * Math.Cos(psi), nodes[i] * Math.Sin(psi));
            }
        return sum * 2 * Math.PI / angular;
    }

    /// <summary>∫₀^R f(r) r dr by 20-point Gauss–Legendre on panels growing by 1.25 from 1e-9
    /// (plus [0, 1e-9]): for peaked f near r = 0.</summary>
    private static double Radial(Func<double, double> f, double upper)
    {
        double sum = 0, a = 0, b = Math.Min(1e-9, upper);
        while (a < upper)
        {
            var (nodes, weights) = GaussLegendre.Rule(20, a, b);
            for (int j = 0; j < nodes.Length; j++) sum += weights[j] * nodes[j] * f(nodes[j]);
            a = b;
            b = Math.Min(upper, b * 1.25);
        }
        return sum;
    }

    // ============================================================ gate: TIS equals the fraction

    /// <summary>Lambertian and Gaussian: the help normalises every model but ABg "to yield a TIS
    /// equal to the fraction to scatter". The help's TIS integral of the program's BSDF, taken by
    /// 96 × 256-point quadrature over the disk about its centre (an analytic integrand, so the
    /// rule is converged far below 1e-8), equals the fraction. Specular directions at 0°, 35° and
    /// 70° (azimuth 20°), σ = 0.3 and 2.</summary>
    [Fact]
    public void Gate_LambertianAndGaussianTis_EqualTheFraction()
    {
        var lambert = new NscLambertianScatter(0.37);
        foreach (double angle in new[] { 0.0, 35, 70 })
        {
            double b = Math.Sin(angle * Math.PI / 180);
            var inc = new NscScatterIncidence(b * Math.Cos(20 * Math.PI / 180), b * Math.Sin(20 * Math.PI / 180), angle);
            Assert.Equal(0.37, lambert.Tis(inc));
            Within(0.37, OverDisk((x, y) => lambert.Bsdf(inc, x, y), 96, 256), 1e-12, $"Lambertian TIS integral at {angle}°");
            foreach (double sigma in new[] { 0.3, 2.0 })
            {
                var gauss = new NscGaussianScatter(0.6, sigma);
                Assert.Equal(0.6, gauss.Tis(inc));
                Within(0.6, OverDisk((x, y) => gauss.Bsdf(inc, x, y), 96, 256), 1e-8, $"Gaussian σ = {sigma} TIS integral at {angle}°");
            }
        }
    }

    /// <summary>ABg: its fraction is its TIS. At normal incidence with g = 2 the TIS is
    /// 2πA∫₀¹ r/(B + r²) dr = πA ln((B + 1)/B) (derived), for the help's example A = 0.002,
    /// B = 0.0005; with g = 1.5 (the tabulated F) it is compared with direct quadrature
    /// (<see cref="Radial"/>), at normal incidence and, by the help's TIS integral about the disk's
    /// centre, at 40°. The table's interpolation is fourth order in a 0.01 step of ln r; 1e-7
    /// relative is far above its error and far below any physical effect.</summary>
    [Fact]
    public void Gate_AbgTis_MatchesClosedFormsAndQuadrature()
    {
        var help = new NscAbgKernel(0.002, 0.0005, 2);
        double closed = Math.PI * 0.002 * Math.Log((0.0005 + 1) / 0.0005);
        Within(closed, help.Tis(0), 1e-13 * closed, "TIS of the help's example ABg curve at normal incidence (closed form)");

        var k = new NscAbgKernel(0.01, 0.01, 1.5);
        double normal = 2 * Math.PI * 0.01 * Radial(r => 1 / (0.01 + Math.Pow(r, 1.5)), 1);
        Within(normal, k.Tis(0), 1e-7 * normal, "ABg g = 1.5 TIS at normal incidence");
        foreach (double r in new[] { 1e-5, 0.003, 0.05, 0.7, 1.9 })
        {
            double direct = Radial(s => 1 / (0.01 + Math.Pow(s, 1.5)), r);
            Within(direct, k.F(r), 1e-7 * direct, $"F({r}) from the table");
        }

        // Off normal (40°). g = 2: the help's integral about the disk's centre (the integrand is
        // analytic). g = 1.5: |x|^1.5 has a cusp at the specular point, which a grid about the
        // centre resolves only algebraically, so the integral is taken in polar coordinates about
        // the specular point instead: ∫₀^{2π} ∫₀^{ρ(ψ)} f(r) r dr dψ, ρ(ψ) = −b cos ψ + √(1 − b² sin² ψ)
        // (derived), r by direct Gauss–Legendre (<see cref="Radial"/>), ψ by a 1024-point trapezoid
        // (periodic and analytic in ψ).
        double b = Math.Sin(40 * Math.PI / 180);
        var g2 = new NscAbgKernel(0.01, 0.05, 2);
        double centred = OverDisk((x, y) => g2.Bsdf(Math.Sqrt((x - b) * (x - b) + y * y)), 128, 512);
        Within(centred, g2.Tis(b), 1e-7 * centred, "ABg g = 2 TIS at 40° against the help's integral");
        var g15 = new NscAbgKernel(0.01, 0.05, 1.5);
        double polar = 0;
        for (int q = 0; q < 1024; q++)
        {
            double psi = 2 * Math.PI * q / 1024;
            double rho = -b * Math.Cos(psi) + Math.Sqrt(1 - b * b * Math.Sin(psi) * Math.Sin(psi));
            polar += Radial(g15.Bsdf, rho);
        }
        polar *= 2 * Math.PI / 1024;
        Within(polar, g15.Tis(b), 1e-7 * polar, "ABg g = 1.5 TIS at 40° by quadrature about the specular point");

        // The profile's fraction is its TIS, and a TIS of 1 or more is refused.
        var profile = new NscAbgProfile { Name = "P", Rows = new[] { new NscAbgRow(0, 0.002, 0.0005, 2) } };
        var abg = new NscAbgScatter(profile, 0.55);
        Within(closed, abg.Fraction(new NscScatterIncidence(0, 0, 0)), 1e-13 * closed, "ABg profile fraction = TIS");
        var heavy = new NscAbgScatter(new NscAbgProfile { Name = "H", Rows = new[] { new NscAbgRow(0, 1, 0.01, 1) } }, 0.55);
        var e = Assert.Throws<InvalidOperationException>(() => heavy.Fraction(new NscScatterIncidence(0, 0, 0)));
        Assert.Contains("less than 1", e.Message);
    }

    /// <summary>Rows at two angles are interpolated linearly in the cosine of incidence ("linearly
    /// interpolated in cosine space"): at the incidence whose cosine is midway the TIS is the mean
    /// of the rows' TIS; outside the rows the end rows are used. Wavelength scaling follows the
    /// help's A′ = A(λ/λ_ref)^(g−4), B′ = B(λ/λ_ref)^g.</summary>
    [Fact]
    public void AbgRows_InterpolateInCosine_AndScaleWithWavelength()
    {
        var profile = new NscAbgProfile { Name = "TWO", Rows = new[] { new NscAbgRow(60, 0.004, 0.002, 2), new NscAbgRow(0, 0.002, 0.001, 2) } };
        var abg = new NscAbgScatter(profile, 0.55);
        double c = (1 + 0.5) / 2;
        double mid = Math.Acos(c) * 180 / Math.PI;
        double b = 0.3;
        double t0 = new NscAbgKernel(0.002, 0.001, 2).Tis(b), t60 = new NscAbgKernel(0.004, 0.002, 2).Tis(b);
        Within(0.5 * (t0 + t60), abg.Fraction(new NscScatterIncidence(b, 0, mid)), 1e-13, "TIS midway in cosine");
        Within(t60, abg.Fraction(new NscScatterIncidence(b, 0, 80)), 1e-15, "beyond the last row");

        var scaled = new NscAbgProfile { Name = "W", WavelengthMicrometers = 0.5, Rows = new[] { new NscAbgRow(0, 0.003, 0.0004, 1.7) } }.RowsAt(0.6);
        Within(0.003 * Math.Pow(1.2, 1.7 - 4), scaled[0].A, 1e-18, "A′ = A(λ/λref)^(g−4)");
        Within(0.0004 * Math.Pow(1.2, 1.7), scaled[0].B, 1e-18, "B′ = B(λ/λref)^g");
    }

    // ============================================================ samplers

    /// <summary>The draws follow the stated densities: |x| = |β − β₀| binned in rings, each ring's
    /// probability ∫ f(r) r·arc(r) dr / ∫ f(r) r·arc(r) dr, arc(r) the angle of the circle |x| = r
    /// inside the unit disk (derived by the law of cosines; <see cref="NscScatterMath.ArcInDisk"/>
    /// is checked against a direct count of the circle's points below). 100 000 draws; 5σ binomial.</summary>
    [Fact]
    public void Samplers_FollowTheirDensities()
    {
        // ArcInDisk against counting 100 000 points of the circle.
        foreach (var (r, bb) in new[] { (0.3, 0.5), (1.2, 0.5), (0.9, 0.95) })
        {
            int inside = 0;
            for (int k = 0; k < 100_000; k++)
            {
                double p = 2 * Math.PI * (k + 0.5) / 100_000;
                double x = bb + r * Math.Cos(p), y = r * Math.Sin(p);
                if (x * x + y * y <= 1) inside++;
            }
            Within(2 * Math.PI * inside / 100_000, NscScatterMath.ArcInDisk(r, bb), 2 * Math.PI * 2e-5, $"arc in disk r = {r}, b = {bb}");
        }

        var cases = new (string Name, NscScatterDistribution D, Func<double, double> F, double Angle)[]
        {
            ("Gaussian σ 0.3", new NscGaussianScatter(1, 0.3), r => Math.Exp(-r * r / 0.09), 35),
            ("ABg g 1.5", new NscAbgScatter(new NscAbgProfile { Name = "S", Rows = new[] { new NscAbgRow(0, 0.01, 0.01, 1.5) } }, 0.55), r => 1 / (0.01 + Math.Pow(r, 1.5)), 40),
            ("Lambertian", new NscLambertianScatter(1), r => 1, 50),
        };
        double[] edges = { 0, 0.01, 0.03, 0.1, 0.2, 0.35, 0.5, 0.8, 1.2, 1.6, 2 };
        const int n = 100_000;
        foreach (var (name, d, f, angle) in cases)
        {
            double b = Math.Sin(angle * Math.PI / 180);
            var inc = new NscScatterIncidence(b, 0, angle);
            var rng = new SplitMix64(11);
            var counts = new int[edges.Length - 1];
            for (int k = 0; k < n; k++)
            {
                var (x, y) = d.Sample(inc, rng);
                Assert.True(x * x + y * y <= 1, $"{name}: a draw outside the unit disk");
                double r = Math.Sqrt((x - b) * (x - b) + y * y);
                for (int i = 0; i < counts.Length; i++) if (r >= edges[i] && r < edges[i + 1]) { counts[i]++; break; }
            }
            double Ring(double a, double c)
            {
                double sum = 0;
                var (nodes, weights) = GaussLegendre.Rule(40, a, c);
                for (int j = 0; j < nodes.Length; j++) sum += weights[j] * nodes[j] * f(nodes[j]) * NscScatterMath.ArcInDisk(nodes[j], b);
                return sum;
            }
            var mass = Enumerable.Range(0, counts.Length).Select(i => Ring(edges[i], edges[i + 1])).ToArray();
            // The ABg ring [0, 0.01] holds the peak at r ~ B^(1/g) = 0.046... below it; 40 points
            // integrate it to far better than the binomial error.
            double total = mass.Sum();
            for (int i = 0; i < counts.Length; i++)
            {
                double p = mass[i] / total;
                Within(n * p, counts[i], 5 * Math.Sqrt(n * p * (1 - p)) + 1e-9, $"{name}: ring {edges[i]}–{edges[i + 1]}");
            }
        }
    }

    // ============================================================ traced fractions and energies

    private static NscScene Mirror(NscFaceScatter scatter, NscScatterTo? to = null, string? coating = null, params NscObject[] more) => new()
    {
        Objects = new[]
        {
            new NscObject
            {
                Geometry = new NscRectangle(0.02, 0.02), Material = NscMaterial.Mirror,
                FaceProperties = new Dictionary<int, NscFace> { [0] = new NscFace { Scatter = scatter, Coating = coating } },
                ScatterTo = to,
            },
        }.Concat(more).ToArray(),
    };

    /// <summary>A ray onto the origin at 30° in the X–Z plane, from above.</summary>
    private static NscLaunchRay Incoming(double power = 1) => new()
    {
        Position = new Vector3D(-0.05 * Math.Tan(Math.PI / 6), 0, 0.05),
        Direction = new Vector3D(Math.Sin(Math.PI / 6), 0, -Math.Cos(Math.PI / 6)),
        Intensity = power,
    };

    /// <summary>Without ray splitting a ray scatters with probability f (help, "Fraction to Scatter
    /// and Number of Scatter Rays"), and for ABg with probability TIS: the count of scattered rays
    /// out of N = 20 000 is within 5√(N f (1 − f)) of N f.</summary>
    [Fact]
    public void Gate_WithoutSplitting_RaysScatterWithTheStatedFraction()
    {
        var settings = new NscTraceSettings { UsePolarization = false, ScatterRays = true, Seed = 3 };
        var abgProfile = new NscAbgProfile { Name = "M", Rows = new[] { new NscAbgRow(0, 0.05, 0.01, 1.5) } };
        double abgTis = new NscAbgScatter(abgProfile, 0.55).Fraction(new NscScatterIncidence(0.5, 0, 30));
        Assert.InRange(abgTis, 0.05, 0.95);
        foreach (var (scatter, f) in new[]
        {
            (new NscFaceScatter { Model = NscScatterModel.Lambertian, Fraction = 0.3 }, 0.3),
            (new NscFaceScatter { Model = NscScatterModel.ABg, AbgReflect = abgProfile }, abgTis),
        })
        {
            var tracer = new NscTracer(Mirror(scatter), 0.55, settings);
            const int n = 20_000;
            int scattered = 0;
            for (int k = 0; k < n; k++)
            {
                var tree = tracer.TraceSingle(Incoming(), k);
                if (tree.Segments[1].Events.HasFlag(NscEvents.Scattered))
                {
                    scattered++;
                    Assert.Contains(tree.Segments, s => s.Events.HasFlag(NscEvents.ScatteredFromParent));
                }
                Assert.True(Math.Abs(tree.Ledger.Residual) <= 1e-15, "the ledger closes");
            }
            Within(n * f, scattered, 5 * Math.Sqrt(n * f * (1 - f)), $"{scatter.Model} rays scattered");
        }
    }

    /// <summary>With ray splitting the help's own example: "if the fraction to scatter is 0.25 and
    /// the number of scatter rays is 5, then the specular ray will receive a relative energy of 0.75,
    /// and each of the 5 scattered rays will have a relative energy of 0.05". The mirror carries an
    /// ideal coating with R = 1, so the reflected power is the incident power to rounding.</summary>
    [Fact]
    public void Splitting_GivesTheHelpsExampleEnergies()
    {
        var settings = new NscTraceSettings
        {
            Splitting = NscSplitting.Split, ScatterRays = true,
            Coatings = new CoatingCatalog { Coatings = new Coating[] { new IdealCoating { Name = "R1", Reflection = 1 } } },
        };
        var scene = Mirror(new NscFaceScatter { Model = NscScatterModel.Lambertian, Fraction = 0.25, NumberOfRays = 5 }, coating: "R1");
        var tree = new NscTracer(scene, 0.55, settings).TraceSingle(Incoming());
        Assert.True(tree.Segments[1].Events.HasFlag(NscEvents.Scattered));
        var children = tree.Segments.Where(s => s.Parent == 1).ToList();
        var scattered = children.Where(s => s.Events.HasFlag(NscEvents.ScatteredFromParent)).ToList();
        Assert.Equal(5, scattered.Count);
        foreach (var s in scattered) Within(0.05, s.Intensity, 1e-15, "a scattered ray's energy");
        var specular = Assert.Single(children, s => !s.Events.HasFlag(NscEvents.ScatteredFromParent));
        Within(0.75, specular.Intensity, 1e-15, "the specular ray's energy");
        Within(0, tree.Ledger.Residual, 1e-15, "ledger");

        // Zero scatter rays: "the fraction to scatter is ignored and no scattering occurs".
        var none = Mirror(new NscFaceScatter { Model = NscScatterModel.Lambertian, Fraction = 0.25, NumberOfRays = 0 }, coating: "R1");
        var plain = new NscTracer(none, 0.55, settings).TraceSingle(Incoming());
        Assert.False(plain.Segments[1].Events.HasFlag(NscEvents.Scattered));
    }

    /// <summary>Scatter NSC Rays off: a scene with scatter models traces exactly as the same scene
    /// without them (bitwise ledger), and a run is bitwise the same at one thread and at many.</summary>
    [Fact]
    public void ScatterOff_IsTheOldTrace_AndRunsAreBitwiseAtAnyThreadCount()
    {
        var scatter = new NscFaceScatter { Model = NscScatterModel.Gaussian, Fraction = 0.5, Sigma = 0.2, NumberOfRays = 3 };
        var detector = new NscObject { Geometry = new NscRectangle(0.05, 0.05), Placement = new NscPlacement(Z: 0.1), IsDetectingStandIn = true };
        var rays = Enumerable.Repeat(Incoming(), 3000).ToArray();
        var off = new NscTracer(Mirror(scatter, more: new[] { detector }), 0.55, new NscTraceSettings { UsePolarization = false }).Run(rays).Ledger;
        var bareScene = new NscScene { Objects = new[] { new NscObject { Geometry = new NscRectangle(0.02, 0.02), Material = NscMaterial.Mirror }, detector } };
        var bare = new NscTracer(bareScene, 0.55, new NscTraceSettings { UsePolarization = false }).Run(rays).Ledger;
        Assert.Equal(bare, off with { DetectedByObject = bare.DetectedByObject });
        Assert.Equal(bare.DetectedByObject.ToArray(), off.DetectedByObject.ToArray());

        foreach (var split in new[] { NscSplitting.None, NscSplitting.Split })
        {
            var one = new NscTracer(Mirror(scatter, more: new[] { detector }), 0.55, new NscTraceSettings { Splitting = split, ScatterRays = true, MaxDegreeOfParallelism = 1 }).Run(rays).Ledger;
            var many = new NscTracer(Mirror(scatter, more: new[] { detector }), 0.55, new NscTraceSettings { Splitting = split, ScatterRays = true }).Run(rays).Ledger;
            Assert.Equal(one.Detected, many.Detected);
            Assert.Equal(one.Escaped, many.Escaped);
            Assert.Equal(one.Absorbed, many.Absorbed);
            Assert.True(one.Detected > 0 && one.Detected < one.Emitted);
            Assert.True(Math.Abs(one.Residual) <= 1e-12 * one.Emitted, $"ledger residual {one.Residual:R}");
        }
    }

    // ============================================================ gate: importance sampling

    /// <summary>
    /// A ray scattered at the origin of a mirror (plane z = 0) toward a detecting square of half
    /// width h centred at (x₀, 0, D), parallel to the mirror. The fraction of the scattered power it
    /// receives is ∫∫ p(β)·cos θ dΩ = ∫∫ p(β(x, y))·D²/s⁴ dx dy over the square, s² = x² + y² + D²
    /// (derived: cos θ = D/s, dΩ = D dx dy/s³), p the normalised density per projected solid angle
    /// (1/π Lambertian; the Gaussian's normalised by <see cref="OverDisk"/>), by 32 × 32-point
    /// Gauss–Legendre.
    /// <para>
    /// Brute force: N_b = 400 000 rays, the detected power is Binomial(N_b, p)/N_b, standard error
    /// √(p(1 − p)/N_b). Importance sampling (target sphere on the square's centre, radius its
    /// half diagonal): 20 000 rays traced one by one, each detecting E′ = BSDF·cos θ·Ω/TIS or
    /// nothing; standard error s/√N. Each estimate is within 5 standard errors of the closed form
    /// and the two within 5√(σ_b² + σ_IS²) of each other; importance sampling's error is the
    /// smaller.
    /// </para>
    /// </summary>
    [Fact]
    public void Gate_ImportanceSampling_AgreesWithBruteForce()
    {
        const double h = 0.004, x0 = 0.03, D = 0.08;
        var detector = new NscObject { Geometry = new NscRectangle(h, h), Placement = new NscPlacement(X: x0, Z: D), IsDetectingStandIn = true };
        var settings = new NscTraceSettings { UsePolarization = false, ScatterRays = true, Seed = 17 };
        double b0 = Math.Sin(Math.PI / 6);
        double sigma = 0.5;
        double gaussNorm = OverDisk((x, y) => Math.Exp(-((x - b0) * (x - b0) + y * y) / (sigma * sigma)), 96, 256);
        var cases = new (string Name, NscFaceScatter Scatter, Func<double, double, double> P)[]
        {
            ("Lambertian", new NscFaceScatter { Model = NscScatterModel.Lambertian, Fraction = 1 }, (bx, by) => 1 / Math.PI),
            ("Gaussian", new NscFaceScatter { Model = NscScatterModel.Gaussian, Fraction = 1, Sigma = sigma },
                (bx, by) => Math.Exp(-((bx - b0) * (bx - b0) + by * by) / (sigma * sigma)) / gaussNorm),
        };
        var (u, w) = GaussLegendre.Rule(32, -h, h);
        foreach (var (name, scatter, p) in cases)
        {
            double expected = 0;
            for (int i = 0; i < u.Length; i++)
                for (int j = 0; j < u.Length; j++)
                {
                    double x = x0 + u[i], y = u[j], s2 = x * x + y * y + D * D, s = Math.Sqrt(s2);
                    expected += w[i] * w[j] * p(x / s, y / s) * D * D / (s2 * s2);
                }

            const int nb = 400_000;
            var brute = new NscTracer(Mirror(scatter, more: new[] { detector }), 0.55, settings).Run(Enumerable.Repeat(Incoming(), nb).ToArray()).Ledger;
            double bruteMean = brute.DetectedByObject.GetValueOrDefault(2) / nb;
            double sigmaB = Math.Sqrt(expected * (1 - expected) / nb);

            var to = new NscScatterTo { Method = NscScatterToMethod.ImportanceSampling, Targets = new[] { new NscScatterTarget(2, h * Math.Sqrt(2) * 1.000001, 1) } };
            var tracer = new NscTracer(Mirror(scatter, to, more: new[] { detector }), 0.55, settings);
            const int ni = 20_000;
            double sum = 0, squares = 0;
            for (int k = 0; k < ni; k++)
            {
                var tree = tracer.TraceSingle(Incoming(), k);
                double e = tree.Ledger.DetectedByObject.GetValueOrDefault(2);
                sum += e;
                squares += e * e;
                Assert.True(Math.Abs(tree.Ledger.Residual) <= 1e-14, $"{name}: ledger residual {tree.Ledger.Residual:R} (importance sampling's correction is in the ledger)");
            }
            double isMean = sum / ni;
            double sigmaIs = Math.Sqrt(Math.Max(0, squares / ni - isMean * isMean) / (ni - 1));
            Within(expected, bruteMean, 5 * sigmaB, $"{name}: brute force against the closed form");
            Within(expected, isMean, 5 * sigmaIs, $"{name}: importance sampling against the closed form");
            Within(bruteMean, isMean, 5 * Math.Sqrt(sigmaB * sigmaB + sigmaIs * sigmaIs), $"{name}: importance sampling against brute force");
            Assert.True(sigmaIs < sigmaB, $"{name}: importance sampling's error {sigmaIs:R} is below brute force's {sigmaB:R}");
        }
    }

    /// <summary>The Scatter To list keeps only scattered rays that meet the listed objects (help,
    /// "The Scatter To List"): with the detector listed, the detected power is the brute-force run's
    /// to the bit (the same draws), and what brute force lets escape is "scattered, not traced".</summary>
    [Fact]
    public void ScatterToList_KeepsOnlyRaysTowardTheListedObjects()
    {
        var detector = new NscObject { Geometry = new NscRectangle(0.004, 0.004), Placement = new NscPlacement(X: 0.03, Z: 0.08), IsDetectingStandIn = true };
        var scatter = new NscFaceScatter { Model = NscScatterModel.Lambertian, Fraction = 1 };
        var settings = new NscTraceSettings { UsePolarization = false, ScatterRays = true, Seed = 5 };
        var rays = Enumerable.Repeat(Incoming(), 20_000).ToArray();
        var brute = new NscTracer(Mirror(scatter, more: new[] { detector }), 0.55, settings).Run(rays).Ledger;
        var listed = new NscTracer(Mirror(scatter, new NscScatterTo { Method = NscScatterToMethod.List, Objects = new[] { 2 } }, more: new[] { detector }), 0.55, settings).Run(rays).Ledger;
        Assert.Equal(brute.DetectedByObject[2], listed.DetectedByObject[2]);
        Assert.Equal(brute.Escaped, listed.ScatterNotTraced);
        Assert.Equal(0.0, listed.Escaped);
        Assert.True(Math.Abs(listed.Residual) <= 1e-12 * listed.Emitted);
    }

    // ============================================================ gate: integrating sphere

    /// <summary>
    /// A Lambertian point source of flux Φ at the south pole of a hollow sphere of radius R whose
    /// wall is a Lambertian reflector of reflectance ρ (MIRROR with an ideal coating R = ρ, T = 0,
    /// and Lambertian scatter with fraction 1). The source alone gives every wall point Φ/(4πR²)
    /// (OP-79's gate, NscDetectorTests: chord s = 2R cos θ₁ = 2R cos θ₂); a Lambertian wall element
    /// of flux dΦ does the same, so each reflection adds a uniform ρⁿΦ/(4πR²) and the wall's total
    /// irradiance is E = Φ/(4πR²(1 − ρ)) (derived: the geometric series). The wall's exitance ρE is
    /// uniform, so the radiance seen from the centre is ρE/π in every direction, and a flat detector
    /// at the centre receives πL = ρE on each side: the integrating-sphere closed form
    /// Φρ/(4πR²(1 − ρ)) per unit area (derived). The detector (a transparent Detector Rectangle,
    /// both sides counted) lies in the plane x = 0, which holds the source, so no direct ray
    /// reaches it. Source: OP-77's Source Ellipse with zero half widths and cosine exponent 1
    /// (I ∝ cos θ), random sampling, 1 W, 1e-7 m inside the wall (a 2e-6 relative change, far
    /// below the statistics). Rays below 1e-10 of their start are dropped (ρ^k ≤ 1e-10 relative
    /// bias). Sixteen batches of 20 000 rays with different seeds; the mean of the batch fluxes is
    /// within 5 standard errors (s/√16; Student t with 15 degrees of freedom beyond 5 has
    /// probability 1.6e-4) of the closed form.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void Gate_LambertianWallInASphere_ReproducesTheIntegratingSphereClosedForm()
    {
        const double R = 0.05, h = 0.01, rho = 0.75;
        var scene = new NscScene
        {
            Objects = new NscObject[]
            {
                new()
                {
                    Geometry = new NscSphere(R, isVolume: false), Material = NscMaterial.Mirror,
                    FaceProperties = new Dictionary<int, NscFace> { [0] = new NscFace { Coating = "WALL", Scatter = new NscFaceScatter { Model = NscScatterModel.Lambertian, Fraction = 1 } } },
                },
                new()
                {
                    Geometry = new NscSourceEllipse(0, 0, cosineExponent: 1, common: new NscSourceCommon { AnalysisRays = 20_000, Power = 1 },
                        properties: new NscSourceProperties { Sampling = NscSourceSampling.Random }),
                    Placement = new NscPlacement(Z: -R + 1e-7),
                },
                new() { Geometry = new NscDetectorRectangle(h, h, 1, 1), Placement = new NscPlacement(TiltYDegrees: 90) },
            },
        };
        var options = new NscSourceEmitOptions { SystemWavelengths = new[] { new NscSpectralLine(0.55, 1) } };
        var batches = new List<double>();
        for (int batch = 0; batch < 16; batch++)
        {
            var settings = new NscTraceSettings
            {
                ScatterRays = true, Seed = 100 + batch, MinimumRelativeIntensity = 1e-10,
                Coatings = new CoatingCatalog { Coatings = new Coating[] { new IdealCoating { Name = "WALL", Reflection = rho } } },
            };
            var run = NscSourceRun.Run(scene, settings, options);
            Assert.True(Math.Abs(run.Ledger.Residual) <= 1e-12, $"ledger residual {run.Ledger.Residual:R}");
            var data = Assert.IsType<NscFlatDetectorData>(Assert.Single(run.Detectors!, d => d.ObjectNumber == 3));
            batches.Add(data.Flux[0]);
        }
        double expected = 2 * (2 * h) * (2 * h) * rho / (4 * Math.PI * R * R * (1 - rho));
        double mean = batches.Average();
        double sd = Math.Sqrt(batches.Sum(v => (v - mean) * (v - mean)) / (batches.Count - 1));
        Assert.True(sd > 0);
        Within(expected, mean, 5 * sd / Math.Sqrt(batches.Count), "detector flux at the centre (integrating-sphere closed form)");
    }

    // ============================================================ files and catalogs

    [Fact]
    public void AbgCatalog_RoundTripsAsText_AndRefusesBadProfiles()
    {
        var catalog = new NscAbgCatalog
        {
            Profiles = new[]
            {
                new NscAbgProfile { Name = "ABG-EXAMPLE", Rows = new[] { new NscAbgRow(0, 0.01, 0.015, 2), new NscAbgRow(45, 0.01, 0.00552, 2), new NscAbgRow(70, 0.01, 0.001, 2) } },
                new NscAbgProfile { Name = "BLACK", WavelengthMicrometers = 0.55, Rows = new[] { new NscAbgRow(0, 0.1, 0, 0) } },
            },
        };
        Assert.Empty(catalog.Validate());
        var back = NscAbgCatalog.Parse(catalog.Format());
        Assert.Equal(catalog.Profiles.Select(p => (p.Name, p.WavelengthMicrometers)), back.Profiles.Select(p => (p.Name, p.WavelengthMicrometers)));
        Assert.Equal(catalog.Profiles.SelectMany(p => p.Rows), back.Profiles.SelectMany(p => p.Rows));

        Assert.Contains(new NscAbgCatalog { Profiles = new[] { new NscAbgProfile { Name = "lower", Rows = new[] { new NscAbgRow(0, 0.1, 0.1, 2) } } } }.Validate(), p => p.Contains("upper case"));
        Assert.Contains(new NscAbgCatalog { Profiles = new[] { new NscAbgProfile { Name = "B0", Rows = new[] { new NscAbgRow(0, 0.1, 0, 2) } } } }.Validate(), p => p.Contains("1e-12"));
        Assert.Contains(new NscAbgCatalog { Profiles = new[] { new NscAbgProfile { Name = "MANY", Rows = Enumerable.Range(0, 11).Select(i => new NscAbgRow(i, 0.1, 0.1, 2)).ToArray() } } }.Validate(), p => p.Contains("Up to 10"));
        Assert.Throws<FormatException>(() => NscAbgCatalog.Parse("A 0 0 1 2"));
    }

    /// <summary>ABGF files (help, "ABg File Scattering"): relative weights are divided by their sum,
    /// absolute ones are not; names must be upper case and in the catalog.</summary>
    [Fact]
    public void AbgFile_WeightsAreRelativeOrAbsolute()
    {
        var catalog = new NscAbgCatalog
        {
            Profiles = new[]
            {
                new NscAbgProfile { Name = "ONE", Rows = new[] { new NscAbgRow(0, 0.002, 0.0005, 2) } },
                new NscAbgProfile { Name = "TWO", Rows = new[] { new NscAbgRow(0, 0.001, 0.01, 1.5) } },
            },
        };
        var relative = NscAbgSum.Parse("2\nONE 1\nTWO 3\n", "R.ABGF", catalog);
        Assert.False(relative.Absolute);
        Assert.Equal(new[] { 0.25, 0.75 }, relative.Coefficients());
        var absolute = NscAbgSum.Parse("2 1\nONE 1\nTWO 3\n", "A.ABGF", catalog);
        Assert.Equal(new[] { 1.0, 3.0 }, absolute.Coefficients());
        var inc = new NscScatterIncidence(0, 0, 0);
        double t1 = new NscAbgKernel(0.002, 0.0005, 2).Tis(0), t2 = new NscAbgKernel(0.001, 0.01, 1.5).Tis(0);
        Within(0.25 * t1 + 0.75 * t2, new NscAbgSumScatter(relative, 0.55).Fraction(inc), 1e-15, "relative sum's TIS");
        Within(t1 + 3 * t2, new NscAbgSumScatter(absolute, 0.55).Fraction(inc), 1e-15, "absolute sum's TIS");

        Assert.Contains("upper case", Assert.Throws<FormatException>(() => NscAbgSum.Parse("1\none 1\n", "L.ABGF", catalog)).Message);
        Assert.Contains("not in the ABg catalog", Assert.Throws<FormatException>(() => NscAbgSum.Parse("1\nTHREE 1\n", "M.ABGF", catalog)).Message);
        Assert.Contains("20", Assert.Throws<FormatException>(() => NscAbgSum.Parse("21\n", "X.ABGF", catalog)).Message);
    }

    /// <summary>A plane-symmetric monochrome BSDF file written here in the layout of the Knowledge
    /// Base article "Zemax Tabular BSDF data file format" (values arbitrary). Its scattered rays
    /// carry fraction × the file's TIS for the nearest angle of incidence (0.5 at 0°, 0.4 at 30°),
    /// never leave the tabulated radial range, and an XYZ file is read from its Y group.</summary>
    [Fact]
    public void BsdfFile_IsRead_AndScattersWithTheFilesTis()
    {
        string Table(double value, double tis0, double tis30)
        {
            var sb = new System.Text.StringBuilder();
            foreach (double tis in new[] { tis0, tis30 })
            {
                sb.AppendLine(FormattableString.Invariant($"TIS {tis}"));
                for (int a = 0; a < 7; a++) sb.AppendLine(string.Join("\t", Enumerable.Repeat(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), 5)));
            }
            return sb.ToString();
        }
        string head = "# test file\nSource Measured\nSymmetry PlaneSymmetrical\nSpectralContent {0}\nScatterType BRDF\nSampleRotation 1\n0\nAngleOfIncidence 2\n0\t30\nScatterAzimuth 7\n0\t30\t60\t90\t120\t150\t180\nScatterRadial 5\n0\t5\t10\t15\t20\n\n";
        string mono = string.Format(head, "Monochrome") + "Monochrome\nDataBegin\n" + Table(1, 0.5, 0.4) + "DataEnd\n";
        var table = NscBsdfTable.Parse(mono, "T.BSDF");
        Assert.Equal(NscBsdfSymmetry.PlaneSymmetrical, table.Symmetry);
        Assert.Equal(0.5, table.Tis[0, 0]);
        Assert.Equal(0.4, table.Tis[0, 1]);
        string xyz = string.Format(head, "XYZ") + "TristimulusX\nDataBegin\n" + Table(1, 0.9, 0.9) + "DataEnd\nTristimulusY\nDataBegin\n" + Table(1, 0.3, 0.2) + "DataEnd\nTristimulusZ\nDataBegin\n" + Table(1, 0.8, 0.8) + "DataEnd\n";
        Assert.Equal(0.3, NscBsdfTable.Parse(xyz, "C.BSDF").Tis[0, 0]);
        Assert.Throws<FormatException>(() => NscBsdfTable.Parse(mono.Replace("PlaneSymmetrical", "Round"), "B.BSDF"));
        Assert.Throws<FormatException>(() => NscBsdfTable.Parse(mono.Replace("TIS 0.5", "0.5"), "B.BSDF"));

        var dist = new NscTableScatter(table, 1, 0);
        Assert.Equal(0.5, dist.Weight(new NscScatterIncidence(0, 0, 0)));
        Assert.Equal(0.4, dist.Weight(new NscScatterIncidence(Math.Sin(25 * Math.PI / 180), 0, 25)));
        var inc = new NscScatterIncidence(0.5, 0, 30);
        var rng = new SplitMix64(4);
        for (int k = 0; k < 20_000; k++)
        {
            var (x, y) = dist.Sample(inc, rng);
            var w = new Vector3D(x, y, Math.Sqrt(1 - x * x - y * y));
            var r = new Vector3D(0.5, 0, Math.Sqrt(0.75));
            Assert.True(Math.Acos(Math.Min(1, Vector3D.Dot(w, r))) * 180 / Math.PI <= 20 + 1e-9, "no scatter beyond the last radial angle");
        }

        // Traced: one scatter ray of power fraction × TIS(30°) = 0.4 with splitting.
        var scatter = new NscFaceScatter { Model = NscScatterModel.Bsdf, Fraction = 1, NumberOfRays = 1, BsdfReflect = table };
        var settings = new NscTraceSettings
        {
            Splitting = NscSplitting.Split, ScatterRays = true,
            Coatings = new CoatingCatalog { Coatings = new Coating[] { new IdealCoating { Name = "R1", Reflection = 1 } } },
        };
        var tree = new NscTracer(Mirror(scatter, coating: "R1"), 0.55, settings).TraceSingle(Incoming());
        var child = Assert.Single(tree.Segments, s => s.Events.HasFlag(NscEvents.ScatteredFromParent));
        Within(0.4, child.Intensity, 1e-15, "scattered power = fraction × the file's TIS");
        Within(0, tree.Ledger.Residual, 1e-15, "ledger (the rest of the fraction is absorbed)");
        Within(0.6, tree.Ledger.Absorbed, 1e-15, "absorbed = fraction × (1 − TIS)");
    }

    // ============================================================ project, refusals, analyses

    private static OpticsAnalysisContext Context(OpticsNonSequentialSettings ns) => new("P", new OpticsSettings { NonSequential = ns });

    private static OpticsNonSequentialSettings Saved() => new()
    {
        AbgCatalog = new() { new OpticsAbgProfileSettings { Name = "POLISH", Wavelength = 0, Rows = new() { new OpticsAbgRowSettings { Angle = 0, A = 0.002, B = 0.0005, G = 2 } } } },
        Objects = new()
        {
            new OpticsNscObjectSettings
            {
                Type = "Rectangle", Parameters = new() { 20, 20 }, Material = "MIRROR",
                Faces = new() { new OpticsNscFaceSettings { Face = 0, ScatterModel = "ABg", ScatterRays = 4, ScatterReflect = "POLISH", ThinWindowScattering = true } },
                ScatterToMethod = "Importance Sampling",
                ScatterTargets = new() { new OpticsNscScatterTargetSettings { Object = 2, Size = 5, Limit = 0.5 } },
            },
            new OpticsNscObjectSettings { Type = "Rectangle", Parameters = new() { 5, 5 }, Z = 100, DetectingStandIn = true },
        },
    };

    [Fact]
    public void SavedScattering_BecomesTheScene_AndRoundTripsThroughTheProjectFile()
    {
        var scene = ProjectNonSequential.Scene(Context(Saved()), LensUnit.Millimeter)!;
        var face = scene.Objects[0].Face(0).Scatter!;
        Assert.Equal(NscScatterModel.ABg, face.Model);
        Assert.Equal(4, face.NumberOfRays);
        Assert.True(face.ThinWindow);
        Assert.Equal("POLISH", face.AbgReflect!.Name);
        var to = scene.Objects[0].ScatterTo!;
        Assert.Equal(NscScatterToMethod.ImportanceSampling, to.Method);
        var target = Assert.Single(to.Targets);
        Assert.Equal((2, 0.5), (target.Object, target.Limit));
        Within(0.005, target.Radius, 1e-18, "target size 5 mm in meters");

        var project = new SimProject { Name = "S", AnalysisType = "Optics", Optics = new OpticsSettings { NonSequential = Saved() } };
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = serializer.Load(path).Optics!.NonSequential!;
            var original = Saved();
            Assert.Equal(original.AbgCatalog![0] with { Rows = null }, loaded.AbgCatalog![0] with { Rows = null });
            Assert.Equal(original.AbgCatalog[0].Rows, loaded.AbgCatalog[0].Rows);
            Assert.Equal(original.Objects![0].Faces, loaded.Objects![0].Faces);
            Assert.Equal(original.Objects[0].ScatterTargets, loaded.Objects[0].ScatterTargets);
            Assert.Equal(original.Objects[0] with { Parameters = null, Faces = null, ScatterTargets = null },
                loaded.Objects[0] with { Parameters = null, Faces = null, ScatterTargets = null });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UserDefinedAndUnknownModels_AreRefusedByName()
    {
        foreach (var (model, words) in new[] { ("User Defined", "User Defined scattering"), ("Fractal", "is not one of") })
        {
            var saved = Saved() with
            {
                Objects = new() { new OpticsNscObjectSettings { Type = "Rectangle", Parameters = new() { 1, 1 }, Faces = new() { new OpticsNscFaceSettings { Face = 0, ScatterModel = model } } } },
            };
            var e = Assert.Throws<ArgumentException>(() => ProjectNonSequential.Scene(Context(saved), LensUnit.Millimeter));
            Assert.Contains(words, e.Message);
        }
        var problems = new List<string>();
        new NscFaceScatter { Model = NscScatterModel.Gaussian, Fraction = 1, Sigma = 6 }.Validate("f", problems);
        Assert.Contains(problems, p => p.Contains("5.0"));
        problems.Clear();
        new NscFaceScatter { Model = NscScatterModel.Lambertian, Fraction = 1, NumberOfRays = 301 }.Validate("f", problems);
        Assert.Contains(problems, p => p.Contains("300"));
    }

    /// <summary>An ABg TIS of 1 or more is a trace error naming the profile, not a silent energy gain.</summary>
    [Fact]
    public void AnAbgTisOfOneOrMore_IsATraceError()
    {
        var heavy = new NscAbgProfile { Name = "HEAVY", Rows = new[] { new NscAbgRow(0, 1, 0.01, 1) } };
        var tree = new NscTracer(Mirror(new NscFaceScatter { Model = NscScatterModel.ABg, AbgReflect = heavy }), 0.55,
            new NscTraceSettings { UsePolarization = false, ScatterRays = true }).TraceSingle(Incoming());
        Assert.Contains(tree.Errors, e => e.Contains("HEAVY") && e.Contains("less than 1"));
        Assert.True(tree.Ledger.Errors > 0);
    }

    /// <summary>Scatter Function Viewer and Scatter Polar Plot: Lambertian is flat at f/π ("The
    /// resulting BSDF 'curve' is a flat line with a value of 1/π", times the fraction); the ABg
    /// viewer curve is A/(B + |x|^g) at every plotted |x|; both list the TIS.</summary>
    [Fact]
    public void ScatterPlots_ShowTheModelsBsdf()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.Contains(registry.All, a => a.Id == NscScatterFunctionViewerAnalysis.SavedId);
        Assert.Contains(registry.All, a => a.Id == NscScatterPolarPlotAnalysis.SavedId);
        var context = Context(Saved());

        var flat = new NscScatterFunctionViewerAnalysis().Compute(context, new NscScatterFunctionViewerSettings { Model = NscScatterModel.Lambertian, Fraction = 0.5 }, default);
        var curve = Assert.IsType<LinePlot>(Assert.Single(flat.Plots)).Curves.Single();
        Assert.NotEmpty(curve.Y);
        foreach (double y in curve.Y) Within(0.5 / Math.PI, y, 1e-16, "Lambertian BSDF");

        var abg = new NscScatterFunctionViewerAnalysis().Compute(context, new NscScatterFunctionViewerSettings { Model = NscScatterModel.ABg, FileName = "POLISH", YMinimum = -8, YDecades = 12 }, default);
        var c = Assert.IsType<LinePlot>(Assert.Single(abg.Plots)).Curves.Single();
        Assert.True(c.X.Count > 100);
        for (int i = 0; i < c.X.Count; i++)
            Within(0.002 / (0.0005 + c.X[i] * c.X[i]), c.Y[i], 1e-12 * c.Y[i], $"ABg BSDF at |x| = {c.X[i]}");
        Assert.Contains("TIS", abg.Text);

        var polar = new NscScatterPolarPlotAnalysis().Compute(context, new NscScatterPolarPlotSettings { Model = NscScatterModel.Lambertian, Fraction = 0.8, RadialBins = 30, AzimuthalBins = 36 }, default);
        var map = Assert.IsType<AnalysisMapPlot>(Assert.Single(polar.Plots));
        foreach (double v in map.Field.Values)
            if (!double.IsNaN(v)) Within(0.8 / Math.PI, v, 1e-16, "Lambertian polar BSDF");
        Assert.Contains(map.Field.Values.Cast<double>(), double.IsNaN);

        Assert.Throws<NotSupportedException>(() => new NscScatterPolarPlotAnalysis().Compute(context, new NscScatterPolarPlotSettings { Model = NscScatterModel.UserDefined }, default));
    }
}
