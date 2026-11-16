using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.Photometry;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-79: detector objects, photometry and the Detector Viewer (docs/Optics-Conventions.md §73).
/// Gate: a point source at distance d gives inverse-square irradiance with the cosine factor; a
/// Lambertian emitter's radiant intensity follows the cosine law and its radiance is constant; the
/// direct irradiance a Lambertian element on an integrating sphere's wall gives the rest of the
/// wall is uniform, Φ/(4πR²) (the multiple wall reflections need Lambertian scattering, OP-80, not
/// built); a 555 nm watt reads 683 lumens.
/// <para>
/// Monte Carlo tolerances: the sources here are pseudo-random (SplitMix64, fixed seeds), so a
/// pixel that a ray reaches with probability p collects n ~ Binomial(N, p) rays of power P/N; its
/// flux has standard deviation σ = (P/N)·√(N·p·(1 − p)) (derived). Every comparison allows 5σ, so
/// a correct engine fails one of the ≈ 300 comparisons below with probability under 3 × 10⁻⁴
/// (two-sided normal tail 5.7 × 10⁻⁷ each); with the fixed seeds the outcome is deterministic.
/// </para>
/// </summary>
public sealed class NscDetectorTests
{
    private static NscTraceSettings Plain(int? threads = null) => new() { UsePolarization = false, MaxDegreeOfParallelism = threads };

    private static NscRunResult Run(NscScene scene, IReadOnlyList<NscLaunchRay> rays, double micrometers = 0.55, NscTraceSettings? settings = null) =>
        new NscTracer(scene, micrometers, settings ?? Plain()).Run(rays);

    private static T Data<T>(NscRunResult run, int objectNumber) where T : NscDetectorData =>
        Assert.IsType<T>(Assert.Single(run.Detectors!, d => d.ObjectNumber == objectNumber));

    private static void Within(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    /// <summary>Cosine-weighted (Lambertian) directions about +z from a point or a disk: sin θ = √u₁,
    /// φ = 2πu₂ (derived: the pdf cos θ sin θ/π over the hemisphere has CDF sin²θ in θ), and points
    /// uniform on the disk r = a√u₃, ψ = 2πu₄; SplitMix64 stream k for ray k.</summary>
    private static NscLaunchRay[] Lambertian(Vector3D center, double radius, int count, double power, long seed)
    {
        var rays = new NscLaunchRay[count];
        for (int k = 0; k < count; k++)
        {
            var rng = SplitMix64.ForStream(seed, k);
            double s = Math.Sqrt(rng.NextDouble()), phi = 2 * Math.PI * rng.NextDouble();
            double r = radius * Math.Sqrt(rng.NextDouble()), psi = 2 * Math.PI * rng.NextDouble();
            double c = Math.Sqrt(Math.Max(0, 1 - s * s));
            rays[k] = new NscLaunchRay
            {
                Position = center + new Vector3D(r * Math.Cos(psi), r * Math.Sin(psi), 0),
                Direction = new Vector3D(s * Math.Cos(phi), s * Math.Sin(phi), c),
                Intensity = power / count,
            };
        }
        return rays;
    }

    private static double Sigma(double power, int n, double p) => power / n * Math.Sqrt(n * p * (1 - p));

    // ================================================================= gate: point source

    /// <summary>
    /// The solid angle a rectangle [x₁, x₂] × [y₁, y₂] in the plane z = d subtends at the origin:
    /// Ω = ∫∫ d/(x² + y² + d²)^{3/2} dx dy = F(x₂, y₂) − F(x₁, y₂) − F(x₂, y₁) + F(x₁, y₁) with
    /// F(x, y) = atan(xy/(d·√(x² + y² + d²))) (derived: ∂²F/∂x∂y = d/(x² + y² + d²)^{3/2}; the test
    /// below also checks it against Gauss–Legendre quadrature).
    /// </summary>
    private static double RectangleSolidAngle(double x1, double x2, double y1, double y2, double d)
    {
        double F(double x, double y) => Math.Atan(x * y / (d * Math.Sqrt(x * x + y * y + d * d)));
        return F(x2, y2) - F(x1, y2) - F(x2, y1) + F(x1, y1);
    }

    /// <summary>
    /// A point source of power P emitting uniformly into the +z hemisphere has radiant intensity
    /// I = P/(2π) (derived), and the irradiance it gives a plane at distance d is
    /// E = I cos θ/r² = I·d/r³ (inverse square with the cosine factor). A pixel's expected flux is
    /// I·Ω_pixel. Two transparent Detector Rectangles at d and 2d, the second twice as wide, subtend
    /// the same pixel solid angles, so the same rays land in corresponding pixels and the
    /// irradiance of the far one is a quarter of the near one's (inverse square), to rounding.
    /// The detector's own noise estimate agrees with σ to its sampling error, ≈ 1/(2√(Np)) relative
    /// (derived: the estimate is ∝ √p̂), allowed 5 times.
    /// </summary>
    [Fact]
    public void Gate_APointSource_GivesInverseSquareIrradiance_WithTheCosineFactor()
    {
        const double d = 0.1, h = 0.1, power = 1;
        const int n = 200_000, pixels = 10;
        var near = new NscDetectorRectangle(h, h, pixels, pixels);
        var far = new NscDetectorRectangle(2 * h, 2 * h, pixels, pixels);
        var scene = new NscScene
        {
            Objects = new[]
            {
                new NscObject { Geometry = near, Placement = new NscPlacement(Z: d) },
                new NscObject { Geometry = far, Placement = new NscPlacement(Z: 2 * d) },
            }
        };
        var rays = NscSourceStandIn.PointCone(Vector3D.Zero, Vector3D.UnitZ, 90, n, power, NscSampling.Random, seed: 7);
        var run = Run(scene, rays);
        var a = Data<NscFlatDetectorData>(run, 1);
        var b = Data<NscFlatDetectorData>(run, 2);
        Assert.Equal(0.0, run.Ledger.Detected);   // transparent detectors take nothing
        Within(power, run.Ledger.Escaped, 1e-12, "everything passes the transparent detectors and escapes");

        double intensity = power / (2 * Math.PI);
        var (gx, gw) = GaussLegendre.Rule(24, -h, -h + a.PixelWidthX);
        for (int iy = 0; iy < pixels; iy++)
            for (int ix = 0; ix < pixels; ix++)
            {
                int k = a.Index(ix, iy);
                var (cx, cy) = a.PixelCenter(ix, iy);
                double x1 = cx - a.PixelWidthX / 2, x2 = cx + a.PixelWidthX / 2, y1 = cy - a.PixelWidthY / 2, y2 = cy + a.PixelWidthY / 2;
                double omega = RectangleSolidAngle(x1, x2, y1, y2, d);
                if (ix == 0 && iy == 0)
                {
                    // The closed form against quadrature of d/r³ over the corner pixel.
                    double q = 0;
                    for (int i = 0; i < gx.Length; i++)
                        for (int j = 0; j < gx.Length; j++)
                        {
                            double x = gx[i], y = gx[j];   // the corner pixel spans [−h, −h + width] in both
                            q += gw[i] * gw[j] * d / Math.Pow(x * x + y * y + d * d, 1.5);
                        }
                    Within(q, omega, 1e-12 * omega, "rectangle solid angle against quadrature");
                }
                double p = omega / (2 * Math.PI);
                double expected = intensity * omega;      // ∫∫ I cos θ / r² dA over the pixel
                double sigma = Sigma(power, n, p);
                Within(expected, a.Flux[k], 5 * sigma, $"near detector pixel ({ix}, {iy}) flux");
                Within(a.Flux[k] / a.PixelArea, a.Irradiance(k), 0, "irradiance = flux / pixel area");
                Within(sigma, a.FluxStandardError(k), 5 / (2 * Math.Sqrt(n * p)) * sigma, $"noise estimate of pixel ({ix}, {iy})");
                // Inverse square: the same rays, four times the pixel area.
                Within(a.Flux[k], b.Flux[k], 1e-12 * a.Flux[k], $"far detector pixel ({ix}, {iy}) flux");
                Within(a.Irradiance(k) / 4, b.Irradiance(k), 1e-12 * a.Irradiance(k), $"far detector pixel ({ix}, {iy}) irradiance");
            }
    }

    // ================================================================= gate: Lambertian emitter

    /// <summary>
    /// A small Lambertian disk (radius a, power P, emitting into +z) has radiant intensity
    /// I(θ) = (P/π)·cos θ and radiance L = I/(A cos θ) = P/(πA), A = πa² (derived:
    /// ∫ I₀ cos θ dΩ over the hemisphere = πI₀ = P). A Detector Polar about it (radius 1 m ≫ a,
    /// binning by direction) collects in polar bin [θ₁, θ₂] of one azimuth arm
    /// (P/π)·Δφ·(sin²θ₂ − sin²θ₁)/2 (derived: ∫ cos θ sin θ dθ = sin²θ/2). The cosine law is
    /// checked pixel by pixel, the constant radiance ring by ring: L_ring = Φ_ring/(A·∫cos θ dΩ).
    /// </summary>
    [Fact]
    public void Gate_ALambertianEmitter_FollowsTheCosineLaw_AndHasConstantRadiance()
    {
        const double a = 1e-3, power = 1;
        const int n = 200_000;
        var polar = new NscDetectorPolar(90, 1.0, 10, 12);
        var scene = new NscScene { Objects = new[] { new NscObject { Geometry = polar, Material = NscMaterial.Absorb } } };
        var run = Run(scene, Lambertian(Vector3D.Zero, a, n, power, seed: 11));
        var d = Data<NscPolarDetectorData>(run, 1);
        Within(power, run.Ledger.Detected, 1e-12, "the absorbing polar detector counts everything as detected");
        double dphi = 2 * Math.PI / d.AzimuthPixels, area = Math.PI * a * a, radiance = power / (Math.PI * area);
        for (int j = 0; j < d.PolarPixels; j++)
        {
            var (from, to) = d.PolarBounds(j);
            double s1 = Math.Sin(from * Math.PI / 180), s2 = Math.Sin(to * Math.PI / 180);
            double pPixel = dphi * (s2 * s2 - s1 * s1) / 2 / Math.PI;
            double ringFlux = 0;
            for (int arm = 0; arm < d.AzimuthPixels; arm++)
            {
                int k = d.Index(j, arm);
                double expected = power * pPixel, sigma = Sigma(power, n, pPixel);
                Within(expected, d.Flux[k], 5 * sigma, $"polar pixel ({j}, {arm}) flux");
                Within(expected / d.SolidAngle(j), d.RadiantIntensity(j, arm), 5 * sigma / d.SolidAngle(j), $"polar pixel ({j}, {arm}) intensity, the cosine law");
                ringFlux += d.Flux[k];
            }
            double pRing = pPixel * d.AzimuthPixels;
            double projected = area * 2 * Math.PI * (s2 * s2 - s1 * s1) / 2;    // A·∫cos θ dΩ over the ring
            Within(radiance, ringFlux / projected, 5 * Sigma(power, n, pRing) / projected, $"radiance of ring {j}");
        }
    }

    // ================================================================= gate: integrating sphere (direct part)

    /// <summary>
    /// A Lambertian point element of flux Φ at the south pole S of a sphere of radius R, facing
    /// in, gives every wall point Q irradiance E = (Φ/π)·cos θ₁·cos θ₂/s², and on a sphere the chord
    /// s = |Q − S| = 2R cos θ₁ = 2R cos θ₂, so E = Φ/(4πR²) everywhere (derived; the classic
    /// integrating-sphere property). Small Detector Rectangles tangent to the wall at four points
    /// collect the integral of E over their (flat) area, computed here by 24 × 24-point
    /// Gauss–Legendre; at each tangent point the closed form gives Φ/(4πR²) to rounding. The wall's
    /// own Lambertian reflections (the rest of the integrating-sphere formula) need OP-80.
    /// </summary>
    [Fact]
    public void Gate_TheDirectIrradianceOfALambertianWallElement_IsUniformOverTheSphere()
    {
        const double R = 0.1, h = 0.015, power = 1;
        const int n = 400_000;
        var source = new Vector3D(0, 0, -R);
        double[] alphas = { 60, 90, 120, 180 };
        var scene = new NscScene
        {
            Objects = alphas.Select(al => new NscObject
            {
                Geometry = new NscDetectorRectangle(h, h, 1, 1),
                Placement = new NscPlacement(X: R * Math.Sin(al * Math.PI / 180), Z: -R * Math.Cos(al * Math.PI / 180), TiltYDegrees: 180 - al),
            }).ToArray(),
        };
        var tracer = new NscTracer(scene, 0.55, Plain());
        var run = tracer.Run(Lambertian(source, 0, n, power, seed: 5));
        double uniform = power / (4 * Math.PI * R * R);
        var (u, w) = GaussLegendre.Rule(24, -h, h);
        for (int i = 0; i < alphas.Length; i++)
        {
            var t = tracer.GlobalPlacement(i + 1);
            var normal = t.ApplyDirection(Vector3D.UnitZ);
            var q = t.Apply(Vector3D.Zero);
            Within(1, Math.Abs(Vector3D.Dot(normal, q / q.Length)), 1e-12, $"detector {i + 1} is tangent to the wall");
            Within(R, q.Length, 1e-15, $"detector {i + 1} touches the wall");
            double E(Vector3D p)
            {
                var v = p - source;
                double s = v.Length;
                return power / Math.PI * (v.Z / s) * Math.Abs(Vector3D.Dot(v, normal) / s) / (s * s);
            }
            Within(uniform, E(q), 1e-13 * uniform, $"closed-form irradiance at tangent point {i + 1}");
            double expected = 0;
            for (int a = 0; a < u.Length; a++)
                for (int b = 0; b < u.Length; b++)
                    expected += w[a] * w[b] * E(t.Apply(new Vector3D(u[a], u[b], 0)));
            var data = Data<NscFlatDetectorData>(run, i + 1);
            double p = expected / power;
            Within(expected, data.Flux[0], 5 * Sigma(power, n, p), $"wall detector at {alphas[i]}° flux");
            Within(uniform, data.Irradiance(0), 5 * Sigma(power, n, p) / data.PixelArea + Math.Abs(expected / data.PixelArea - uniform),
                $"wall detector at {alphas[i]}° irradiance against Φ/(4πR²)");
        }
    }

    // ================================================================= gate: photometry

    /// <summary>
    /// One watt at 555 nm reads 683 lm: V(555 nm) is exactly 1.0000000000000 in the CIE table
    /// (CIE_sle_photopic.csv, row 555) and K_cd = 683 lm/W (SI Brochure, 9th ed., §2.3.1). The SI
    /// fixes 683 lm/W at 540 × 10¹² Hz, which is c/f = 555.1712… nm in vacuum (c = 299 792 458 m/s,
    /// SI Brochure §2.2; derived); the CIE table is in standard air, where that radiation is a
    /// little shorter, between the two. At the vacuum wavelength this program reads
    /// 683·V(555.1712 nm), V interpolated linearly between the 555 nm row (1.0000000000000) and the
    /// 556 nm row (0.9998567000000), so every reading between 555 nm and the vacuum wavelength lies
    /// in [683·V(555.1712 nm), 683] lm.
    /// </summary>
    [Fact]
    public void Gate_A555NanometerWatt_Reads683Lumens()
    {
        Assert.Equal(1.0, CieTables.PhotopicEfficiency(555));
        Within(683.0, Photometric.LumensPerWatt(0.555), 1e-12, "lumens per watt at 0.555 µm (0.555·1000 is 555 to a rounding)");
        double vacuumNm = Photometric.SpeedOfLight / Photometric.CandelaFrequencyHz * 1e9;
        Assert.InRange(vacuumNm, 555.171, 555.172);
        double v = 1.0 + (vacuumNm - 555) * (0.9998567000000 - 1.0);
        Within(683 * v, Photometric.LumensPerWatt(vacuumNm / 1000), 1e-12, "683·V at 540 THz (vacuum wavelength)");
        Assert.True(683 * v < 683 && 683 * v > 682.98);

        // Through a Detector Color: 1 W of 555 nm rays gives Y = 683 lm and X, Z from the 555 nm row
        // of CIE_xyz_1931_2deg.csv (0.512050100000, 1.0000000000000, 0.005749999000).
        var color = new NscDetectorColor(0.01, 0.01, 2, 2, spectral: (10, 0.5, 0.6));
        var scene = new NscScene { Objects = new[] { new NscObject { Geometry = color, Placement = new NscPlacement(Z: 0.05), Material = NscMaterial.Absorb } } };
        var run = Run(scene, NscSourceStandIn.CollimatedDisk(Vector3D.Zero, Vector3D.UnitZ, 0.005, 1000, 1.0), 0.555);
        var d = Data<NscFlatDetectorData>(run, 1);
        Within(683, d.Y!.Sum(), 1e-10, "Y (lumens)");
        Within(683, d.Luminous!.Sum(), 1e-10, "luminous flux");
        Within(683 * 0.512050100000, d.X!.Sum(), 1e-10, "X");
        Within(683 * 0.005749999000, d.Z!.Sum(), 1e-12, "Z");
        Within(1, d.Spectrum![5], 1e-12, "the 0.55–0.56 µm bin holds the whole watt");
        Within(1, d.Spectrum.Sum(), 1e-12, "the spectrum's total");
    }

    /// <summary>The tables as shipped, against the validations their CIE metadata files publish
    /// ("sumOfColumns" and "sampleRow" in CIE_xyz_1931_2deg.csv_metadata.json and
    /// CIE_sle_photopic.csv_metadata.json); linear interpolation and zero outside 360–830 nm, as
    /// the metadata's "interpolationMethod" and "extrapolationMethod" say.</summary>
    [Fact]
    public void TheCieTables_MatchTheirPublishedValidations()
    {
        var xyz = CieTables.ColorMatchingRows;
        var v = CieTables.PhotopicRows;
        Assert.Equal(471, xyz.Count);
        Assert.Equal(471, v.Count);
        // The metadata prints the sums to 12 decimals (±5e-13) and a sum of 471 values below 107
        // differs by summation order by at most 471 half-ulps of 107 (3.3e-12): 1e-11 covers both.
        Within(106.865469489595, xyz.Sum(r => r[0]), 1e-11, "Σ x̄");
        Within(106.8569171011719, xyz.Sum(r => r[1]), 1e-11, "Σ ȳ");
        Within(106.892251278636, xyz.Sum(r => r[2]), 1e-11, "Σ z̄");
        Within(106.8569171011719, v.Sum(r => r[0]), 1e-11, "Σ V");
        Assert.Equal((0.104297900000, 0.1334528000000, 0.856619300000), CieTables.ColorMatching(479));
        Assert.Equal(0.1334528000000, CieTables.PhotopicEfficiency(479));
        Assert.Equal(0.0000004846123, CieTables.PhotopicEfficiency(829));
        Assert.Equal(0.0000055321360, CieTables.PhotopicEfficiency(363));
        Within((1.0 + 0.9998567000000) / 2, CieTables.PhotopicEfficiency(555.5), 1e-15, "linear interpolation");
        Assert.Equal(0.0, CieTables.PhotopicEfficiency(359.9));
        Assert.Equal(0.0, CieTables.PhotopicEfficiency(830.1));
        Assert.Equal((0.0, 0.0, 0.0), CieTables.ColorMatching(1000));
    }

    /// <summary>Chromaticity and sRGB. An equal-energy spectrum's chromaticity is the ratio of the
    /// published column sums; the two forms of u′, v′ in e-ILV 17-23-073 agree; the sRGB primaries
    /// and white of Stokes et al., "A Standard Default Color Space for the Internet – sRGB" (1996),
    /// Table 0.2 (red 0.6400, 0.3300; green 0.3000, 0.6000; blue 0.1500, 0.0600; D65 0.3127,
    /// 0.3290) come back from CSS Color 4's lin_sRGB_to_XYZ to the table's half unit in the last
    /// digit, and the two matrices are inverse (their quotients are exact inverses, checked in
    /// rational arithmetic while writing this test, so the round trip is rounding only).</summary>
    [Fact]
    public void Chromaticity_AndSrgb_FollowTheirDefinitions()
    {
        double sx = 106.865469489595, sy = 106.8569171011719, sz = 106.892251278636;
        var xyz = CieTables.ColorMatchingRows;
        var (x, y) = Photometric.Chromaticity(xyz.Sum(r => r[0]), xyz.Sum(r => r[1]), xyz.Sum(r => r[2]));
        Within(sx / (sx + sy + sz), x, 1e-12, "equal-energy x");
        Within(sy / (sx + sy + sz), y, 1e-12, "equal-energy y");
        var (u1, v1) = Photometric.Ucs1976(0.3, 0.5, 0.2);
        var (cx, cy) = Photometric.Chromaticity(0.3, 0.5, 0.2);
        Within(4 * cx / (-2 * cx + 12 * cy + 3), u1, 1e-15, "u′ both forms");
        Within(9 * cy / (-2 * cx + 12 * cy + 3), v1, 1e-15, "v′ both forms");

        void Primary(double r, double g, double b, double ex, double ey, string name)
        {
            var (X, Y, Z) = Photometric.XyzOfLinearSrgb(r, g, b);
            var (px, py) = Photometric.Chromaticity(X, Y, Z);
            Within(ex, px, 5e-5, name + " x");
            Within(ey, py, 5e-5, name + " y");
            var (rr, gg, bb) = Photometric.LinearSrgb(X, Y, Z);
            Within(r, rr, 1e-12, name + " round trip R");
            Within(g, gg, 1e-12, name + " round trip G");
            Within(b, bb, 1e-12, name + " round trip B");
        }
        Primary(1, 0, 0, 0.6400, 0.3300, "red");
        Primary(0, 1, 0, 0.3000, 0.6000, "green");
        Primary(0, 0, 1, 0.1500, 0.0600, "blue");
        Primary(1, 1, 1, 0.3127, 0.3290, "white");
        Within(1, Photometric.SrgbEncode(1), 1e-15, "sRGB of 1");
        Assert.Equal(0.0, Photometric.SrgbEncode(0));
        Within(12.92 * 0.003, Photometric.SrgbEncode(0.003), 1e-17, "the linear segment");
    }

    // ================================================================= coherent data

    /// <summary>
    /// Two plane waves at ±α (sin α = 0.05) on a Detector Rectangle of 40 × 2 pixels 1 µm wide.
    /// Each beam's rays land on a 4 × 4 grid in every pixel and start on a plane wavefront, so
    /// their optical path at the hit point p is L + d·p and the phase carried to the pixel centre
    /// c is k(L + d·c), the same for every ray of a beam (derived). Each pixel's coherent sum is then
    /// 16(e^{iφ_A} + e^{iφ_B}), |…|² = 256(2 + 2 cos(k·2 sin α·c_x)) (derived), with k = 2π/λ.
    /// Normalized: scaled so the total equals the incoherent flux; not normalized:
    /// I_inc·|ΣE|²/(ΣA)² = I_inc(2 + 2 cos Δφ)/4. Phases of order k·L ≈ 1.1 × 10⁴ rad carry
    /// rounding of ≈ 1e-12 rad, so 1e-9 of the largest value is generous.
    /// </summary>
    [Fact]
    public void TwoPlaneWaves_GiveCosineFringes_OnTheCoherentIrradiance()
    {
        const double lambdaUm = 0.55, sinA = 0.05, baseLength = 1e-3;
        const int nx = 40, ny = 2;
        double hx = 20e-6, hy = 1e-6, k = 2 * Math.PI / (lambdaUm * 1e-6);
        double cosA = Math.Sqrt(1 - sinA * sinA);
        var dA = new Vector3D(sinA, 0, cosA);
        var dB = new Vector3D(-sinA, 0, cosA);
        var rays = new List<NscLaunchRay>();
        foreach (var dir in new[] { dA, dB })
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                        {
                            var p = new Vector3D(-hx + (ix + (sx + 0.5) / 4) * 1e-6, -hy + (iy + (sy + 0.5) / 4) * 1e-6, 0);
                            double t = baseLength + Vector3D.Dot(dir, p);
                            rays.Add(new NscLaunchRay { Position = p - dir * t, Direction = dir, Intensity = 1 });
                        }
        foreach (bool normalize in new[] { true, false })
        {
            var scene = new NscScene { Objects = new[] { new NscObject { Geometry = new NscDetectorRectangle(hx, hy, nx, ny, normalizeCoherentPower: normalize), Material = NscMaterial.Absorb } } };
            var d = Data<NscFlatDetectorData>(Run(scene, rays, lambdaUm), 1);
            var coherent = d.CoherentIrradiance();
            double totalIncoherent = d.Flux.Sum();
            Within(rays.Count, totalIncoherent, 1e-9, "incoherent total");
            double sumC = 0;
            var c = new double[d.PixelCount];
            for (int iy = 0; iy < ny; iy++)
                for (int ix = 0; ix < nx; ix++)
                {
                    double cx = d.PixelCenter(ix, iy).X;
                    c[d.Index(ix, iy)] = 256 * (2 + 2 * Math.Cos(k * 2 * sinA * cx));
                    sumC += c[d.Index(ix, iy)];
                }
            double max = coherent.Max();
            for (int i = 0; i < d.PixelCount; i++)
            {
                double expected = normalize ? c[i] * totalIncoherent / sumC / d.PixelArea : d.Irradiance(i) * c[i] / (32.0 * 32.0);
                Within(expected, coherent[i], 1e-9 * max, $"coherent irradiance of pixel {i + 1} (normalize {normalize})");
            }
            if (normalize) Within(totalIncoherent, coherent.Sum() * d.PixelArea, 1e-9 * totalIncoherent, "normalized coherent total");
        }
    }

    // ================================================================= binning and numbering

    /// <summary>The polar bins of the help's figure "Detector Polar sampling" (maximum angle 10°,
    /// 10 radial pixels, 12 angular pixels): increment 1.11°, pixel 1 [0.000°, 0.556°], pixel 2
    /// ]0.556°, 1.667°], pixel 10 ]9.444°, 10.00°] (to the figure's three decimals); arms 30° wide
    /// centred on multiples of 30°.</summary>
    [Fact]
    public void PolarBins_AreTheHelpFigures()
    {
        var d = new NscDetectorPolar(10, 1, 10, 12).Layout(1);
        Within(1.111, d.Increment, 5e-4, "increment");
        Within(0.556, d.PolarBounds(0).To, 5e-4, "pixel 1 upper");
        Within(0.556, d.PolarBounds(1).From, 5e-4, "pixel 2 lower");
        Within(1.667, d.PolarBounds(1).To, 5e-4, "pixel 2 upper");
        Within(9.444, d.PolarBounds(9).From, 5e-4, "pixel 10 lower");
        Assert.Equal(10.0, d.PolarBounds(9).To);
        Assert.Equal(0, d.PolarIndex(0.5));
        Assert.Equal(1, d.PolarIndex(0.6));
        Assert.Equal(9, d.PolarIndex(9.5));
        Assert.Equal(-1, d.PolarIndex(10.01));
        Assert.Equal(0, d.AzimuthIndex(14.9));
        Assert.Equal(1, d.AzimuthIndex(15.1));
        Assert.Equal(0, d.AzimuthIndex(-14.9));
        Assert.Equal(0, d.AzimuthIndex(359));
        Assert.Equal(11, d.AzimuthIndex(330));
        Within(2 * Math.PI, Enumerable.Range(0, 10).Sum(j => d.SolidAngle(j)) * 12 / (1 - Math.Cos(10 * Math.PI / 180)), 1e-12, "the bins tile the cap");
    }

    /// <summary>A ray lands in the angle pixel the help's formula gives: x angle = |cos⁻¹ n|·l/√(1 − n²),
    /// y angle = |cos⁻¹ n|·m/√(1 − n²) ("Comments on Detector Rectangle angular binning"); position
    /// pixel 1 is the (−x, −y) corner and x changes fastest ("pixel numbering").</summary>
    [Fact]
    public void Pixels_AreNumberedAndBinnedAsTheHelpSays()
    {
        var det = new NscDetectorRectangle(1, 1, 9, 9);
        var scene = new NscScene { Objects = new[] { new NscObject { Geometry = det, Material = NscMaterial.Absorb } } };
        var dir = new Vector3D(0.3, -0.2, Math.Sqrt(1 - 0.13));
        var hit = new Vector3D(-0.95, -0.95, 0);
        var run = Run(scene, new[] { new NscLaunchRay { Position = hit - dir * 0.5, Direction = dir }, new NscLaunchRay { Position = new Vector3D(0.95, -0.95, -0.5), Direction = Vector3D.UnitZ } });
        var d = Data<NscFlatDetectorData>(run, 1);
        Assert.Equal(1.0, d.Flux[0], 12);         // pixel 1: (−x, −y)
        Assert.Equal(1.0, d.Flux[8], 12);         // pixel 9: (+x, −y), the end of the bottom row
        double theta = Math.Acos(dir.Z) * 180 / Math.PI, s = Math.Sqrt(1 - dir.Z * dir.Z);
        double ax = theta * dir.X / s, ay = theta * dir.Y / s;
        int jx = (int)Math.Floor((ax + 90) / 20), jy = (int)Math.Floor((ay + 90) / 20);
        Assert.Equal(1.0, d.AngleFlux[jy * 9 + jx], 12);
        Assert.Equal(1.0, d.AngleFlux[4 * 9 + 4], 12);   // the normal ray, angle (0, 0)
    }

    // ================================================================= materials, ledger, flags

    [Fact]
    public void DetectorMaterials_PassReflectOrAbsorb_AndTheLedgerCloses()
    {
        var rays = NscSourceStandIn.CollimatedDisk(Vector3D.Zero, Vector3D.UnitZ, 0.004, 500, 2.0);
        NscScene Scene(NscMaterial m) => new()
        {
            Objects = new[]
            {
                new NscObject { Geometry = new NscDetectorRectangle(0.01, 0.01, 4, 4), Placement = new NscPlacement(Z: 0.05), Material = m },
                new NscObject { Geometry = new NscRectangle(0.02, 0.02), Placement = new NscPlacement(Z: 0.1), IsDetectingStandIn = true },
            }
        };
        var transparent = Run(Scene(NscMaterial.Blank), rays);
        Within(2, Data<NscFlatDetectorData>(transparent, 1).Flux.Sum(), 1e-12, "transparent: recorded");
        Within(2, transparent.Ledger.DetectedByObject[2], 1e-12, "transparent: passed to the stand-in behind");
        Assert.False(transparent.Ledger.DetectedByObject.ContainsKey(1));

        var absorbing = Run(Scene(NscMaterial.Absorb), rays);
        Within(2, absorbing.Ledger.DetectedByObject[1], 1e-12, "absorbing: counted as detected");
        Within(2, Data<NscFlatDetectorData>(absorbing, 1).Flux.Sum(), 1e-12, "absorbing: recorded");
        Within(0, absorbing.Ledger.Residual, 1e-13, "ledger residual");

        var mirror = Run(Scene(NscMaterial.Mirror), rays);
        Within(2, Data<NscFlatDetectorData>(mirror, 1).Flux.Sum(), 1e-12, "mirror: recorded");
        Within(2, mirror.Ledger.Escaped, 1e-12, "mirror: reflected back and escaped");
        Within(0, mirror.Ledger.Residual, 1e-13, "ledger residual");
    }

    [Fact]
    public void FrontOnly_IgnoresRaysFromTheBack_AndMirroring_SplitsTheRay()
    {
        var back = new[] { new NscLaunchRay { Position = new Vector3D(0.5, 0.5, 1), Direction = -Vector3D.UnitZ } };
        var front = new[] { new NscLaunchRay { Position = new Vector3D(0.5, 0.5, -1), Direction = Vector3D.UnitZ } };
        NscScene Scene(NscGeometry g) => new() { Objects = new[] { new NscObject { Geometry = g, Material = NscMaterial.Absorb } } };
        var frontOnly = new NscDetectorRectangle(1, 1, 2, 2, frontOnly: true);
        var r1 = Run(Scene(frontOnly), back);
        Assert.Equal(0.0, Data<NscFlatDetectorData>(r1, 1).Flux.Sum());
        Within(1, r1.Ledger.Escaped, 0, "a ray from the back misses");
        Assert.Equal(1.0, Data<NscFlatDetectorData>(Run(Scene(frontOnly), front), 1).Flux[3]);

        // Mirroring 1 (left-right): half the ray in its pixel and half in the mirror pixel; 4: both whole.
        var half = Data<NscFlatDetectorData>(Run(Scene(new NscDetectorRectangle(1, 1, 2, 2, mirroring: 1)), front), 1);
        Assert.Equal(new[] { 0, 0, 0.5, 0.5 }, half.Flux);
        var whole = Data<NscFlatDetectorData>(Run(Scene(new NscDetectorRectangle(1, 1, 2, 2, mirroring: 4)), front), 1);
        Assert.Equal(new[] { 0, 0, 1.0, 1.0 }, whole.Flux);
    }

    /// <summary>Pixel interpolation shares a ray between the pixel centres around it: a ray at the
    /// corner of four pixels gives each a quarter; a ray at a pixel centre stays whole (help,
    /// "Detector Settings", Use Pixel Interpolation).</summary>
    [Fact]
    public void PixelInterpolation_SharesTheRayBetweenNeighbours()
    {
        NscScene Scene() => new() { Objects = new[] { new NscObject { Geometry = new NscDetectorRectangle(1, 1, 4, 4, pixelInterpolation: true), Material = NscMaterial.Absorb } } };
        var corner = Data<NscFlatDetectorData>(Run(Scene(), new[] { new NscLaunchRay { Position = new Vector3D(0, 0, -1), Direction = Vector3D.UnitZ } }), 1);
        foreach (int k in new[] { 5, 6, 9, 10 }) Assert.Equal(0.25, corner.Flux[k], 15);
        var centre = Data<NscFlatDetectorData>(Run(Scene(), new[] { new NscLaunchRay { Position = new Vector3D(0.25, 0.25, -1), Direction = Vector3D.UnitZ } }), 1);
        Assert.Equal(1.0, centre.Flux[10], 15);
    }

    [Fact]
    public void TheDetectorRun_IsBitwiseTheSame_AtAnyThreadCount()
    {
        var scene = new NscScene
        {
            Objects = new NscObject[]
            {
                new() { Geometry = new NscDetectorRectangle(0.05, 0.05, 16, 16), Placement = new NscPlacement(Z: 0.05) },
                new() { Geometry = new NscDetectorColor(0.05, 0.05, 8, 8, mirroring: 3), Placement = new NscPlacement(Z: 0.06) },
                new() { Geometry = new NscDetectorPolar(180, 0.5, 20, 24) },
            }
        };
        var rays = NscSourceStandIn.PointCone(Vector3D.Zero, Vector3D.UnitZ, 60, 5000, 1, NscSampling.Random, 3);
        var one = Run(scene, rays, settings: Plain(1));
        foreach (int? threads in new int?[] { 3, null })
        {
            var other = Run(scene, rays, settings: Plain(threads));
            for (int i = 0; i < 3; i++)
            {
                switch (one.Detectors![i], other.Detectors![i])
                {
                    case (NscFlatDetectorData a, NscFlatDetectorData b):
                        Assert.Equal(a.Flux, b.Flux); Assert.Equal(a.FluxSquares, b.FluxSquares); Assert.Equal(a.AngleFlux, b.AngleFlux);
                        if (a.CoherentReal is not null) { Assert.Equal(a.CoherentReal, b.CoherentReal); Assert.Equal(a.CoherentImaginary, b.CoherentImaginary); }
                        if (a.X is not null) Assert.Equal(a.X, b.X);
                        break;
                    case (NscPolarDetectorData a, NscPolarDetectorData b):
                        Assert.Equal(a.Flux, b.Flux); Assert.Equal(a.FluxSquares, b.FluxSquares);
                        break;
                    default:
                        Assert.Fail("detector data types differ");
                        break;
                }
            }
        }
        Within(1, ((NscPolarDetectorData)one.Detectors![2]).Flux.Sum(), 1e-12, "the full-sphere polar detector sees every ray");
    }

    [Fact]
    public void RunsAtTwoWavelengths_Combine()
    {
        var scene = new NscScene { Objects = new[] { new NscObject { Geometry = new NscDetectorRectangle(0.01, 0.01, 2, 2), Placement = new NscPlacement(Z: 0.01) } } };
        var rays = NscSourceStandIn.CollimatedDisk(Vector3D.Zero, Vector3D.UnitZ, 0.005, 100, 1);
        var a = Run(scene, rays, 0.5);
        var b = Run(scene, rays, 0.6);
        var both = Assert.IsType<NscFlatDetectorData>(Assert.Single(NscDetectorResults.Combine(new[] { a, b })!));
        Assert.Equal(200, both.LaunchedRays);
        Within(2, both.Flux.Sum(), 1e-12, "flux of both runs");
        var la = Data<NscFlatDetectorData>(a, 1).Luminous!.Sum();
        var lb = Data<NscFlatDetectorData>(b, 1).Luminous!.Sum();
        Within(la + lb, both.Luminous!.Sum(), 1e-12, "luminous flux of both runs");
        Within(683 * CieTables.PhotopicEfficiency(500), la, 1e-10, "luminous flux at 500 nm");
    }

    [Fact]
    public void UnbuiltOptions_AreRefusedByName()
    {
        Assert.Contains("PSF mode", Assert.Throws<ArgumentException>(() => new NscDetectorRectangle(1, 1, 2, 2, psfWavenumber: 1)).Message);
        Assert.Contains("polarization flag 1", Assert.Throws<ArgumentException>(() => new NscDetectorRectangle(1, 1, 2, 2, polarization: 1)).Message);
        Assert.Contains("multiple of 4", Assert.Throws<ArgumentException>(() => new NscDetectorPolar(90, 1, 10, 13)).Message);
        Assert.Contains("10 to 721", Assert.Throws<ArgumentException>(() => new NscDetectorPolar(90, 1, 9, 12)).Message);
    }

    // ================================================================= saved scene

    private static OpticsAnalysisContext Context(params OpticsNscObjectSettings[] rows) =>
        new("P", new OpticsSettings { NonSequential = new OpticsNonSequentialSettings { Objects = rows.ToList() } });

    [Fact]
    public void SavedDetectorRows_BecomeDetectors_AndUnbuiltTypesAreRefusedByName()
    {
        var scene = ProjectNonSequential.Scene(Context(
            new OpticsNscObjectSettings { Type = "Detector Rectangle", Parameters = new() { 5, 4, 10, 8 }, Z = 30, DetectorPixelInterpolation = true, DetectorNormalizeCoherentPower = false },
            new OpticsNscObjectSettings { Type = "Detector Color", Parameters = new() { 5, 5, 4, 4, 0, 0, 0, 0, 0, 1, 0, -30, 30, -20, 20, 0, 1 }, DetectorSpectralBins = 20, DetectorSpectralMinMicrometers = 0.4, DetectorSpectralMaxMicrometers = 0.7 },
            new OpticsNscObjectSettings { Type = "Detector Polar", Parameters = new() { 90, 50, 10, 12, 0 }, Material = "ABSORB" }), LensUnit.Millimeter)!;
        var rect = Assert.IsType<NscDetectorRectangle>(scene.Objects[0].Geometry);
        Assert.Equal(5e-3, rect.HalfWidthX, 15);
        Assert.Equal(4e-3, rect.HalfWidthY, 15);
        Assert.Equal((10, 8), (rect.PixelsX, rect.PixelsY));
        Assert.Equal((-90.0, 90.0, -90.0, 90.0), (rect.AngleXMin, rect.AngleXMax, rect.AngleYMin, rect.AngleYMax));
        Assert.True(rect.PixelInterpolation);
        Assert.False(rect.NormalizeCoherentPower);
        var color = Assert.IsType<NscDetectorColor>(scene.Objects[1].Geometry);
        Assert.True(color.FrontOnly);
        Assert.Equal((-30.0, 30.0, -20.0, 20.0), (color.AngleXMin, color.AngleXMax, color.AngleYMin, color.AngleYMax));
        Assert.Equal(1, color.Mirroring);
        Assert.Equal((20, 0.4, 0.7), color.Spectral);
        var polar = Assert.IsType<NscDetectorPolar>(scene.Objects[2].Geometry);
        Assert.Equal(0.05, polar.RadialSize, 15);

        foreach (var (type, words) in new[] { ("Detector Volume", "Detector Volume is not built"), ("Detector Surface", "Detector Surface is not built") })
        {
            var e = Assert.Throws<ArgumentException>(() => ProjectNonSequential.Scene(Context(new OpticsNscObjectSettings { Type = type, Parameters = new() { 1, 1, 1, 2, 2, 2 } }), LensUnit.Millimeter));
            Assert.Contains(words, e.Message);
        }
        Assert.Contains(NscDetectorTypes.Descriptors, d => d.TypeName == "Detector Rectangle" && d.Parameters.Count == 17 && d.IsBuilt);
        Assert.Contains(NscDetectorTypes.Descriptors, d => d.TypeName == "Detector Volume" && !d.IsBuilt);
    }

    [Fact]
    public void DetectorProperties_RoundTripThroughTheProjectFile()
    {
        var row = new OpticsNscObjectSettings
        {
            Type = "Detector Color", Parameters = new() { 1, 1, 2, 2 }, DetectorPixelInterpolation = true, DetectorNormalizeCoherentPower = false,
            DetectorSpectralBins = 7, DetectorSpectralMinMicrometers = 0.45, DetectorSpectralMaxMicrometers = 0.65,
        };
        var project = new SimProject { Name = "D", AnalysisType = "Optics", Optics = new OpticsSettings { NonSequential = new OpticsNonSequentialSettings { Objects = new() { row } } } };
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = Assert.Single(serializer.Load(path).Optics!.NonSequential!.Objects!);
            Assert.Equal(row with { Parameters = loaded.Parameters, Faces = loaded.Faces }, loaded);
            Assert.Equal(row.Parameters, loaded.Parameters);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ================================================================= files

    private static NscFlatDetectorData SampleRectangle(bool color)
    {
        var scene = new NscScene
        {
            Objects = new[]
            {
                new NscObject { Geometry = color ? new NscDetectorColor(0.01, 0.02, 3, 2) : new NscDetectorRectangle(0.01, 0.02, 3, 2, angleXMin: -40, angleXMax: 40), Placement = new NscPlacement(Z: 0.02) },
            }
        };
        return Data<NscFlatDetectorData>(Run(scene, NscSourceStandIn.PointCone(Vector3D.Zero, Vector3D.UnitZ, 40, 500, 1, NscSampling.Random, 2)), 1);
    }

    /// <summary>The DDR, DDC and DDP layouts of the help ("Saving and Loading Detector Data"):
    /// version 1004 first, the type 1/2/3, lens units, a 4-byte gap so d_data starts at byte 224,
    /// the ray-trace-method integer at byte 624, then 5 (DDR) or 8 (DDC, DDP) doubles per pixel.</summary>
    [Fact]
    public void DetectorData_SavesAndLoads_InTheHelpsBinaryLayout()
    {
        foreach (bool color in new[] { false, true })
        {
            var data = SampleRectangle(color);
            using var ms = new MemoryStream();
            NscDetectorFile.Write(ms, data, LensUnit.Millimeter);
            var bytes = ms.ToArray();
            Assert.Equal(624 + 4 + data.PixelCount * (color ? 8 : 5) * 8, bytes.Length);
            Assert.Equal(1004, BitConverter.ToInt32(bytes, 0));
            Assert.Equal(color ? 2 : 1, BitConverter.ToInt32(bytes, 4));
            Assert.Equal(0, BitConverter.ToInt32(bytes, 8));                          // mm
            Assert.Equal(3, BitConverter.ToInt32(bytes, 20));                         // i_data[0]: x pixels
            Assert.Equal(10.0, BitConverter.ToDouble(bytes, 224), 12);                // d_data[0]: x half width, mm
            Assert.Equal(2, BitConverter.ToInt32(bytes, 624));                        // NSC ray trace
            var back = Assert.IsType<NscFlatDetectorData>(NscDetectorFile.Read(bytes, "sample"));
            Assert.Equal(data.Flux, back.Flux);
            Assert.Equal(data.AngleFlux, back.AngleFlux);
            Assert.Equal(data.HalfWidthY, back.HalfWidthY, 15);
            Assert.Equal(data.AngleXMin, back.AngleXMin);
            Assert.Equal(data.SpatialHits, back.SpatialHits);
            if (color) { Assert.Equal(data.Y, back.Y); Assert.Equal(data.AngleZ, back.AngleZ); }
            else { Assert.Equal(data.CoherentReal, back.CoherentReal); Assert.Equal(data.CoherentAmplitude, back.CoherentAmplitude); }
            Assert.True(double.IsNaN(back.FluxStandardError(0)));
        }

        var polar = Data<NscPolarDetectorData>(Run(new NscScene { Objects = new[] { new NscObject { Geometry = new NscDetectorPolar(90, 0.1, 10, 12) } } },
            NscSourceStandIn.PointCone(Vector3D.Zero, Vector3D.UnitZ, 80, 300, 1, NscSampling.Random, 4)), 1);
        using (var ms = new MemoryStream())
        {
            NscDetectorFile.Write(ms, polar, LensUnit.Meter);
            var bytes = ms.ToArray();
            Assert.Equal(624 + 4 + 120 * 64, bytes.Length);
            Assert.Equal(3, BitConverter.ToInt32(bytes, 4));
            var back = Assert.IsType<NscPolarDetectorData>(NscDetectorFile.Read(bytes, "polar"));
            Assert.Equal(polar.Flux, back.Flux);
            Assert.Equal(polar.Y, back.Y);
            Assert.Equal(0.1, back.RadialSize);

            bytes[4] = 4;   // type 4: Detector Volume
            Assert.Contains("Detector Volume", Assert.Throws<InvalidDataException>(() => NscDetectorFile.Read(bytes, "volume")).Message);
        }
    }

    // ================================================================= viewer

    [Fact]
    public void TheViewer_ShowsMapsCrossSectionsLogScaleAndSmoothing()
    {
        var data = SampleRectangle(false);
        var map = NscDetectorViewer.Render(data, new NscDetectorViewerSettings(), LensUnit.Millimeter);
        var plot = Assert.IsType<AnalysisMapPlot>(Assert.Single(map.Plots));
        Assert.Equal(data.Irradiance(data.Index(1, 0)), plot.Field.Values[0, 1], 12);
        Assert.Contains(map.Sections, s => s.Heading == "Beam info");
        Assert.Contains(map.Sections, s => s.Heading == "Noise");

        var row = NscDetectorViewer.Render(data, new NscDetectorViewerSettings { ShowAs = NscDetectorShow.CrossSectionRow, RowOrColumn = 2 }, LensUnit.Millimeter);
        var line = Assert.IsType<LinePlot>(Assert.Single(row.Plots));
        Assert.Equal(3, line.Curves[0].Y.Count);
        Assert.Equal(data.Irradiance(data.Index(2, 1)), line.Curves[0].Y[2], 12);

        var log = NscDetectorViewer.Render(data, new NscDetectorViewerSettings { Scale = NscDetectorScale.Log5, MaxPlotScale = 3e3 }, LensUnit.Millimeter);
        var logPlot = Assert.IsType<AnalysisMapPlot>(Assert.Single(log.Plots));
        Assert.Equal(4.0, logPlot.Maximum);    // next power of 10 above 3000
        Assert.Equal(-1.0, logPlot.Minimum);   // five decades below

        var intensity = NscDetectorViewer.Render(data, new NscDetectorViewerSettings { ShowData = NscDetectorShowData.RadiantIntensity, Photometric = true }, LensUnit.Millimeter);
        Assert.Equal("cd", Assert.IsType<AnalysisMapPlot>(Assert.Single(intensity.Plots)).ValueUnit);

        // Smoothing a single bright pixel in the interior of a 5 × 5 grid: once, every pixel of
        // its 3 × 3 block averages nine values of which one is 1 (derived).
        var delta = new double[25];
        delta[12] = 1;
        var once = NscDetectorViewer.SmoothGrid(delta, 5, 5, 1, wrapX: false);
        Assert.Equal(1.0 / 9, once[12], 15);
        Assert.Equal(1.0 / 9, once[6], 15);
        Assert.Equal(0.0, once[0]);
        Assert.Equal(1.0 / 6, NscDetectorViewer.SmoothGrid(new double[] { 1, 0, 0, 0, 0, 0 }, 3, 2, 1, false)[1], 15);   // an edge pixel has five neighbours

        Assert.Throws<ArgumentException>(() => NscDetectorViewer.Render(data, new NscDetectorViewerSettings { ShowData = NscDetectorShowData.CoherentPhase, Smoothing = 1 }, LensUnit.Millimeter));
        Assert.Throws<ArgumentException>(() => NscDetectorViewer.Render(data, new NscDetectorViewerSettings { ShowAs = NscDetectorShow.TrueColor }, LensUnit.Millimeter));

        var color = SampleRectangle(true);
        var rgb = NscDetectorViewer.Render(color, new NscDetectorViewerSettings { ShowAs = NscDetectorShow.TrueColor }, LensUnit.Millimeter);
        Assert.Equal(3, rgb.Plots.Count);
        var chart = NscDetectorViewer.Render(color, new NscDetectorViewerSettings { ShowAs = NscDetectorShow.ChromaticityCie1976 }, LensUnit.Millimeter);
        Assert.IsType<AnalysisScatterPlot>(Assert.Single(chart.Plots));
        Assert.Contains(chart.Sections, s => s.Heading == "Colour");
    }

    [Fact]
    public void TheViewerAnalysis_ReadsAFile_AndWithoutOne_NamesOP77()
    {
        Assert.True(OpticsAnalysisRegistry.CreateDefault().TryGet(NscDetectorViewerAnalysis.SavedId, out _));
        var analysis = new NscDetectorViewerAnalysis();
        var context = new OpticsAnalysisContext("P", new OpticsSettings());
        Assert.Contains("OP-77", Assert.Throws<ArgumentException>(() => analysis.Compute(context, new NscDetectorViewerSettings(), default)).Message);
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".DDP");
        try
        {
            var polar = Data<NscPolarDetectorData>(Run(new NscScene { Objects = new[] { new NscObject { Geometry = new NscDetectorPolar(90, 0.1, 10, 12) } } },
                NscSourceStandIn.PointCone(Vector3D.Zero, Vector3D.UnitZ, 80, 300, 1, NscSampling.Random, 4)), 1);
            NscDetectorFile.Write(path, polar, LensUnit.Millimeter);
            var result = analysis.Compute(context, new NscDetectorViewerSettings { File = path, ShowData = NscDetectorShowData.RadiantIntensity, Smoothing = 1 }, default);
            Assert.IsType<AnalysisMapPlot>(Assert.Single(result.Plots));
            var cut = analysis.Compute(context, new NscDetectorViewerSettings { File = path, ShowData = NscDetectorShowData.RadiantIntensity, ShowAs = NscDetectorShow.CrossSectionRow, Angle = 90 }, default);
            Assert.Equal(19, Assert.IsType<LinePlot>(Assert.Single(cut.Plots)).Curves[0].X.Count);
            Assert.Contains("Data read from", result.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
