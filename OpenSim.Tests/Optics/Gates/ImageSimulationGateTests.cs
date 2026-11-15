using System.Numerics;
using OpenSim.Core.Numerics;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Analysis.ImageSimulation;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Special;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-42 gate (plan: "A perfect lens returns the scene scaled by magnification; a uniform PSF
/// reduces to a single convolution; coherent and incoherent edge images match their closed
/// forms").
/// <list type="number">
/// <item>An ideal lens (OP-27's paraxial surface at its own conjugates, m = −1/3) with no
/// aberration model images the picture onto the detector exactly: pixel [i, j] of the detector
/// (default pixel size |m|·δ, the help's √2/2 × the chief rays' corner distance) is the picture's
/// pixel [H − 1 − i, W − 1 − j] (the image is inverted). Only rounding separates them.</item>
/// <item>A PSF grid of one kernel everywhere gives one ordinary convolution, summed directly here
/// (the tents are a partition of unity); rounding only.</item>
/// <item>Edges, on the periodic grid of <see cref="DiffractionImaging"/>. The object is the
/// sampled step o(c) = 1 for c ≥ N/2, constant down the columns, whose DFT along x is, by the
/// geometric series, Ô(0) = N/2, Ô(k) = ((−1)^k − 1)/(1 − e^{−2πik/N}): −2/(1 − e^{−2πik/N}) for
/// odd k and 0 for even k ≠ 0 (derived). It lives on the ky = 0 row, so the image is
/// (1/N)·Σ_k Ô(k)·T(k)·e^{2πikc/N} with T the transfer function on that row: for the coherent
/// image the CTF, 1 for |k| ≤ R (a disk of radius R on the frequency lattice) and the intensity
/// |a|²; for the incoherent image the OTF of that lattice disk, the number of lattice points in
/// the overlap of the disk and its copy shifted by k over the number in the disk (counted here).
/// These discrete forms are exact for the discretisation and are held to rounding.</item>
/// <item>The continuous closed forms: the coherent edge a(x) = ½ + Si(2π·f_c·x)/π, the
/// incoherent edge I(x) = ½ + (1/π)·∫₀^{2f_c} OTF(ν)·sin(2πνx)/ν dν with the circular pupil's
/// OTF (2/π)(acos s − s√(1 − s²)), s = ν/(2f_c) (the area of overlap of two unit disks over π,
/// derived), x from the edge at (N/2 − ½)·d. The discrete images depart from them by the sampled
/// step's spectrum (factor (πk/N)/sin(πk/N) on coefficient k), the periodic copy of the other
/// edge N/2 samples away, the lattice cut-off (|k| ≤ 40 for R = 40.5) and, incoherently, the
/// lattice disk's area. A NumPy prototype of the same sums (written for this test, not a
/// reference) gave, within three cutoff periods of the edge at N = 512, R = 40.5, departures of
/// 8.1·10⁻³ (coherent intensity) and 1.7·10⁻³ (incoherent); 2·10⁻² and 5·10⁻³ are allowed.</item>
/// <item>The same edges through a lens: the Partially Coherent Image Analysis of an ideal lens at
/// infinite conjugates (zero OPD at every field), whose CTF is 1 inside the unit pupil circle and
/// 0 outside, against the discrete forms with R = f_c·(file size) from the analysis's own cutoff;
/// 10⁻⁷ allows the OPD the trace leaves (~10⁻¹⁰ waves) and rounding.</item>
/// </list>
/// </summary>
public sealed class ImageSimulationGateTests
{
    private static void Close(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    private static double[,] Random(int rows, int columns, int seed)
    {
        var rng = new System.Random(seed);
        var m = new double[rows, columns];
        for (int r = 0; r < rows; r++) for (int c = 0; c < columns; c++) m[r, c] = 0.1 + rng.NextDouble();
        return m;
    }

    // ---- 1. The perfect lens ----

    [Fact]
    public void APerfectLens_ReturnsTheSceneScaledByTheMagnification()
    {
        var (lens, m) = ImageSimulationTests.FiniteIdealLens(0.05, 0.2);
        var system = ImageSimulationTests.HeightSystem(0.01, new[] { 0.0, 0.002 }, 0.55);
        const int w = 8, h = 6;
        const double delta = 1e-4;
        var source = Random(h, w, 11);
        foreach (int field in new[] { 0, 1 })
        {
            var result = ImageSimulator.Simulate(lens, system, new ImageSimulationRequest
            {
                Scene = SceneImage.Grey(source), Field = field, FieldHeight = h * delta,
                Channels = new[] { new SimulationChannel(0, 0, 1, "g") }, Aberrations = ImageSimulationAberrations.None,
                UseRelativeIllumination = false, PsfPointsX = 3, PsfPointsY = 3,
            });
            Close(Math.Abs(m) * delta, result.DefaultPixelSize, 1e-12 * Math.Abs(m) * delta, "default pixel size |m|·δ");
            Close(m * system.Fields.Points[field].Y, result.CentreY, 1e-12 * Math.Abs(m) * 0.002, "centred on the field's chief ray");
            Assert.Equal(w, result.Columns);
            Assert.Equal(h, result.Rows);
            var image = result.Images[0];
            for (int i = 0; i < h; i++)
                for (int j = 0; j < w; j++)
                    Close(source[h - 1 - i, w - 1 - j], image[i, j], 1e-9, $"field {field + 1}, detector ({i}, {j})");
        }
    }

    /// <summary>The same through the analysis, from a PNG on disk at a single wavelength (the
    /// blue channel, help) and with the plot's rows bottom to top.</summary>
    [Fact]
    public void APerfectLens_ThroughTheAnalysis_ShowsThePictureTurned()
    {
        var (lens, _) = ImageSimulationTests.FiniteIdealLens(0.05, 0.2);
        var system = ImageSimulationTests.HeightSystem(0.01, new[] { 0.0, 0.002 }, 0.55);
        var context = ImageSimulationTests.Context(system);
        string dir = Path.Combine(Path.GetTempPath(), "opensim-op42-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const int w = 5, h = 4;
            var rng = new System.Random(3);
            var blue = new byte[h, w];
            for (int r = 0; r < h; r++) for (int c = 0; c < w; c++) blue[r, c] = (byte)(10 + rng.Next(240));
            string file = Path.Combine(dir, "s.png");
            File.WriteAllBytes(file, PngFile.Format(new[] { new byte[h, w], new byte[h, w], blue }));
            var result = new ImageSimulationAnalysis(_ => lens).Compute(context, new ImageSimulationSettings
            {
                File = file, FieldHeight = OpticsConventions.FromMeters(h * 1e-4, system.Units.LensUnit), Wavelengths = SceneWavelengths.Single,
                WavelengthNumber = 1, Aberrations = ImageSimulationAberrations.None, UseRelativeIllumination = false, PsfPointsX = 2, PsfPointsY = 2,
            }, default);
            var map = Assert.IsType<AnalysisMapPlot>(Assert.Single(result.Plots));
            // Plot [r, c] = detector [h − 1 − r, c] = picture [r, w − 1 − c].
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++) Close(blue[r, w - 1 - c], map.Field.Values[r, c], 1e-7, $"plot ({r}, {c})");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- 2. A uniform PSF ----

    [Fact]
    public void AUniformPsf_IsOneConvolution()
    {
        const int h = 21, w = 30;
        var source = Random(h, w, 5);
        var k = Random(5, 4, 6);
        var kernel = new PixelKernel(k, 2, 1);
        foreach (var (gx, gy) in new[] { (1, 1), (3, 2), (5, 5) })
        {
            var b = SpatiallyVariantConvolution.Apply(source, gx, gy, (_, _) => kernel);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double direct = 0;
                    for (int i = 0; i < 5; i++)
                        for (int j = 0; j < 4; j++)
                        {
                            int r = y - (i - 2), c = x - (j - 1);
                            if (r >= 0 && r < h && c >= 0 && c < w) direct += source[r, c] * k[i, j];
                        }
                    Close(direct, b[y, x], 1e-12, $"grid {gx} × {gy}, pixel ({y}, {x})");
                }
        }
    }

    // ---- 3, 4. Edges on the grid ----

    private static double[,] Edge(int n)
    {
        var o = new double[n, n];
        for (int r = 0; r < n; r++) for (int c = n / 2; c < n; c++) o[r, c] = 1;
        return o;
    }

    private static Complex Step(int k, int n) => k == 0 ? new Complex(n / 2.0, 0)
        : k % 2 == 0 ? Complex.Zero : -2 / (1 - Complex.Exp(new Complex(0, -2 * Math.PI * k / n)));

    /// <summary>(1/N)·Σ_k Ô(k)·T(k)·e^{2πikc/N} over |k| ≤ kmax.</summary>
    private static double[] Series(int n, int kmax, Func<int, Complex> transfer, bool intensity)
    {
        var o = new double[n];
        var t = Enumerable.Range(-kmax, 2 * kmax + 1).Select(k => Step(k, n) * transfer(k)).ToArray();
        for (int c = 0; c < n; c++)
        {
            Complex s = 0;
            for (int k = -kmax; k <= kmax; k++) s += t[k + kmax] * Complex.Exp(new Complex(0, 2 * Math.PI * k * c / n));
            s /= n;
            o[c] = intensity ? s.Real * s.Real + s.Imaginary * s.Imaginary : s.Real;
        }
        return o;
    }

    /// <summary>The OTF of the lattice disk i² + j² ≤ R² at shift (k, 0).</summary>
    private static Func<int, Complex> LatticeOtf(double radius)
    {
        int m = (int)Math.Ceiling(radius) + 1;
        var inside = new List<(int, int)>();
        for (int i = -m; i <= m; i++) for (int j = -m; j <= m; j++) if (i * i + j * j <= radius * radius) inside.Add((i, j));
        var set = inside.ToHashSet();
        return k => new Complex(inside.Count(p => set.Contains((p.Item1 - k, p.Item2))) / (double)inside.Count, 0);
    }

    private static double Si(double t)
    {
        if (t == 0) return 0;
        int panels = (int)Math.Ceiling(Math.Abs(t) / 2) + 1;
        double sum = 0;
        for (int p = 0; p < panels; p++)
        {
            var (x, wgt) = GaussLegendre.Rule(20, t * p / panels, t * (p + 1) / panels);
            for (int i = 0; i < x.Length; i++) sum += wgt[i] * Math.Sin(x[i]) / x[i];
        }
        return sum;
    }

    private static double ContinuousIncoherentEdge(double x, double fc)
    {
        // ½ + (1/π)∫₀^{2fc} OTF(ν)·sin(2πνx)/ν dν; the integrand is smooth with an
        // (1 − s)^{3/2} end, so 400 panels of 20 Gauss–Legendre points are ample.
        double sum = 0;
        const int panels = 400;
        for (int p = 0; p < panels; p++)
        {
            var (nu, wgt) = GaussLegendre.Rule(20, 2 * fc * p / panels, 2 * fc * (p + 1) / panels);
            for (int i = 0; i < nu.Length; i++)
            {
                double s = nu[i] / (2 * fc);
                double otf = 2 / Math.PI * (Math.Acos(s) - s * Math.Sqrt(1 - s * s));
                sum += wgt[i] * otf * Math.Sin(2 * Math.PI * nu[i] * x) / nu[i];
            }
        }
        return 0.5 + sum / Math.PI;
    }

    [Fact]
    public void TheCoherentEdge_MatchesItsDiscreteAndSineIntegralForms()
    {
        const int n = 512;
        const double radius = 40.5;
        var h = DiffractionImaging.Lattice(n, 1, (fx, fy) => fx * fx + fy * fy <= radius * radius / ((double)n * n) ? Complex.One : Complex.Zero);
        var obj = Edge(n);
        var image = DiffractionImaging.Intensity(DiffractionImaging.CoherentAmplitude(obj, h, null));
        var discrete = Series(n, 40, _ => Complex.One, intensity: true);
        foreach (int r in new[] { 0, 200 })
            for (int c = 0; c < n; c++) Close(discrete[c], image[r, c], 1e-12, $"discrete coherent edge, ({r}, {c})");
        double fc = radius / n;
        for (int c = 0; c < n; c++)
        {
            double x = c - (n / 2 - 0.5);
            if (Math.Abs(x) > 3 / fc) continue;
            double a = 0.5 + Si(2 * Math.PI * fc * x) / Math.PI;
            Close(a * a, image[0, c], 2e-2, $"sine-integral edge at x = {x}");
        }
    }

    [Fact]
    public void TheIncoherentEdge_MatchesItsDiscreteAndLineSpreadIntegralForms()
    {
        const int n = 512;
        const double radius = 40.5;
        var h = DiffractionImaging.Lattice(n, 1, (fx, fy) => fx * fx + fy * fy <= radius * radius / ((double)n * n) ? Complex.One : Complex.Zero);
        var image = DiffractionImaging.Incoherent(Edge(n), h, null);
        var discrete = Series(n, 81, LatticeOtf(radius), intensity: false);
        foreach (int r in new[] { 0, 311 })
            for (int c = 0; c < n; c++) Close(discrete[c], image[r, c], 1e-12, $"discrete incoherent edge, ({r}, {c})");
        double fc = radius / n;
        for (int c = 0; c < n; c++)
        {
            double x = c - (n / 2 - 0.5);
            if (Math.Abs(x) > 3 / fc) continue;
            Close(ContinuousIncoherentEdge(x, fc), image[0, c], 5e-3, $"incoherent edge at x = {x}");
        }
        // The two samples either side of the edge sum to 1 (the step's odd symmetry about it).
        Close(1, image[0, n / 2 - 1] + image[0, n / 2], 1e-12, "symmetry about the edge");
    }

    // ---- 5. Edges through a lens ----

    [Theory]
    [InlineData(PartiallyCoherentDataType.CoherentImage)]
    [InlineData(PartiallyCoherentDataType.IncoherentImage)]
    public void TheEdgeThroughAPerfectLens_MatchesTheDiscreteForm(PartiallyCoherentDataType type)
    {
        // f = 0.1 m, EPD 10 mm: n·a ≈ 0.05, f_c = a/λ ≈ 10⁵ /m at 0.5 µm; a 0.205 mm file puts
        // the lattice cutoff R = f_c·(file size) near 20.5, away from a lattice point.
        const double f = 0.1;
        var lens = ImageSimulationTests.Lens(double.PositiveInfinity, ImageSimulationTests.S(double.PositiveInfinity, f, stop: true, shape: new ParaxialShape(f)));
        var system = ImageSimulationTests.AngleSystem(0.01, new[] { 0.0 }, 0.5);
        var obj = new double[16, 16];
        for (int r = 0; r < 16; r++) for (int c = 8; c < 16; c++) obj[r, c] = 1;
        const double size = 0.205e-3;
        var result = PartiallyCoherentImaging.Compute(lens, system, new PartiallyCoherentRequest
        {
            Object = obj, FileSize = size, Oversampling = 16, OtfSampling = 64, Field = 0, Wavelength = 0, DataType = type,
        });
        const int n = 256;
        Assert.Equal(n, result.TransformSize);
        double radius = result.CutoffFrequency * size;
        // Precondition: no lattice point within 10⁻⁶ of the circle, so rounding cannot move one across.
        for (int i = -25; i <= 25; i++)
            for (int j = -25; j <= 25; j++)
                Assert.True(Math.Abs(Math.Sqrt(i * i + j * j) - radius) > 1e-6, $"lattice point ({i}, {j}) sits on the cutoff R = {radius}");
        bool coherent = type == PartiallyCoherentDataType.CoherentImage;
        int kmax = (int)Math.Floor(coherent ? radius : 2 * radius);
        var expected = Series(n, kmax, coherent ? _ => Complex.One : LatticeOtf(radius), coherent);
        // The analysis divides by the peak; so does the expectation.
        double peak = expected.Max();
        double shownPeak = 0;
        foreach (var v in result.Values) shownPeak = Math.Max(shownPeak, v);
        foreach (int r in new[] { 0, 128 })
            for (int c = 0; c < n; c++) Close(expected[c] / peak, result.Values[r, c] / shownPeak, 1e-7, $"{type} edge through the lens, ({r}, {c})");
    }
}
