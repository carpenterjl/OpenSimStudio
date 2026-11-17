using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.RayDatabase;
using OpenSim.Optics.NonSequential.Sources;
using OpenSim.Optics.NonSequential.Volume;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-81: volume scattering, fluorescence and phosphors (docs/Optics-Conventions.md §77). Gate:
/// Beer–Lambert attenuation of the unscattered beam; Henyey–Greenstein samples' mean cosine equals
/// g; Mie efficiency tends to the Rayleigh limit for small spheres and to 2 for large, with spot
/// values against miepython; converted + absorbed + transmitted energy closes.
/// <para>
/// <b>Sources.</b> OpticStudio's help pages "Bulk scattering" (p(x) = 1 − e^(−x/M); the
/// Henyey–Greenstein and Rayleigh phase functions), "Phosphors &amp; Fluorescence" (the spectrum
/// files; l(λ) = [ε(λ)·c·ln 10]⁻¹; I_emitted = I_absorbed·Q·λ_in/λ_emitted) and "Wavelength Shift",
/// in plans/sources/optics/op-81-help/. The Mie spot values are miepython 3.3.1 (MIT licence;
/// <c>efficiencies_mx</c> and <c>i_unpolarized(..., norm='one')</c>), stored in
/// plans/sources/optics/op-81-help/miepython-oracle.txt with the script that produced them. The
/// Rayleigh cross-section σ = (8π/3)(2π/λ)⁴((n² − 1)/(n² + 2))² r⁶ is Wikipedia, "Rayleigh
/// scattering" (so Q = σ/(πr²) = (8/3)x⁴((m² − 1)/(m² + 2))², derived). "In the small wavelength
/// limit, the total scattering cross-section of an impenetrable sphere is twice its geometrical
/// cross-sectional area" is Wikipedia, "Extinction paradox". Every other expected value is
/// derived where it is used.
/// </para>
/// <para>
/// <b>Monte Carlo tolerances.</b> Fixed seeds. A count of N trials with probability p has standard
/// deviation √(N p (1 − p)); a mean of N values has standard error s/√N with s the sample standard
/// deviation. Every Monte Carlo comparison allows 5 standard errors (two-sided normal tail 5.7e-7).
/// </para>
/// </summary>
public sealed class NscVolumeTests
{
    private static void Within(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    private static void Relative(double expected, double actual, double tolerance, string what) =>
        Within(expected, actual, tolerance * Math.Abs(expected), what);

    private const double R = 1e-3;   // sphere radius, meters

    private static NscScene Sphere(NscVolumePhysics? physics, params NscObject[] more) => new()
    {
        Objects = new List<NscObject> { new() { Geometry = new NscSphere(R), VolumePhysics = physics } }.Concat(more).ToList(),
    };

    private static NscLaunchRay Centre(NscSourceBulkScatter bulk = NscSourceBulkScatter.Many) => new()
    {
        Position = new Vector3D(0, 0, 0), Direction = new Vector3D(0, 0, 1), InsideOf = 1, BulkScatter = bulk,
    };

    private static readonly NscTraceSettings Plain = new() { UsePolarization = false };

    // ============================================================ gate: Beer–Lambert

    /// <summary>Rays start at the centre of a blank sphere (index 1 in air, so its wall is no
    /// interface) and may scatter once: a ray leaves unscattered when its free path exceeds R, so
    /// the unscattered fraction is e^(−R/M) (the help's p(x) = 1 − e^(−x/M), derived). M = R and
    /// M = R/2; 20000 rays; 5 binomial standard deviations.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void UnscatteredBeam_FollowsBeerLambert(double meanPathInRadii)
    {
        var physics = new NscVolumePhysics { Model = NscVolumeModel.AngleScattering, MeanPath = meanPathInRadii * R, AngleDegrees = 180 };
        var tracer = new NscTracer(Sphere(physics), 0.55, Plain);
        const int n = 20000;
        var result = tracer.Run(Enumerable.Repeat(Centre(NscSourceBulkScatter.Once), n).ToList());
        double p = Math.Exp(-1 / meanPathInRadii);
        double unscattered = 1 - (double)result.Ledger.VolumeEvents / n;
        Within(p, unscattered, 5 * Math.Sqrt(p * (1 - p) / n), "unscattered fraction e^(−R/M)");
        Within(0, result.Ledger.Residual, 1e-9, "ledger closes");
        Within(n, result.Ledger.Escaped, 1e-9, "every ray leaves the sphere (no loss in angle scattering)");
    }

    /// <summary>With Bulk Scatter "Never" the scene with volume physics traces exactly as the scene
    /// without it (no random number is drawn): every segment equal to the bit.</summary>
    [Fact]
    public void NeverSource_TracesAsTheSceneWithoutPhysics_ToTheBit()
    {
        var physics = new NscVolumePhysics { Model = NscVolumeModel.AngleScattering, MeanPath = R / 10, AngleDegrees = 30 };
        var with = new NscTracer(Sphere(physics), 0.55).TraceSingle(Centre(NscSourceBulkScatter.Never), 7);
        var without = new NscTracer(Sphere(null), 0.55).TraceSingle(Centre(), 7);
        Assert.Equal(without.Segments.Count, with.Segments.Count);
        for (int i = 0; i < with.Segments.Count; i++)
        {
            var a = with.Segments[i];
            var b = without.Segments[i];
            Assert.Equal((b.Position, b.Direction, b.Intensity, b.Events, b.Hit), (a.Position, a.Direction, a.Intensity, a.Events, a.Hit));
        }
    }

    // ============================================================ gate: Henyey–Greenstein mean cosine

    /// <summary>The sampler's mean cosine equals g: 200000 draws of a SplitMix64 stream, 5 standard
    /// errors of the sample. g = 0.8, 0.3, −0.5.</summary>
    [Theory]
    [InlineData(0.8)]
    [InlineData(0.3)]
    [InlineData(-0.5)]
    public void HenyeyGreensteinSamples_HaveMeanCosineG(double g)
    {
        var phase = new NscHenyeyGreensteinPhase(g);
        var rng = new SplitMix64(42);
        const int n = 200000;
        double sum = 0, squares = 0;
        for (int i = 0; i < n; i++)
        {
            double mu = phase.SampleCosine(rng.NextDouble());
            sum += mu;
            squares += mu * mu;
        }
        double mean = sum / n, s = Math.Sqrt(squares / n - mean * mean);
        Within(g, mean, 5 * s / Math.Sqrt(n), "HG mean cosine");
    }

    /// <summary>The same through the tracer and the help's Henyey-Greenstein-bulk DLL (first-party):
    /// rays from the centre scatter once ("Once"); the cosine between each B segment's direction and
    /// its child's has mean g = 0.6. Mean path R/50, so nearly every ray scatters.</summary>
    [Fact]
    public void HenyeyGreensteinDll_InTheTracer_GivesMeanCosineG()
    {
        var physics = new NscVolumePhysics { Model = NscVolumeModel.DllDefined, Dll = "Henyey-Greenstein-bulk.dll", MeanPath = R / 50, DllParameters = new[] { 0.6 } };
        var tracer = new NscTracer(Sphere(physics), 0.55, Plain);
        var recorder = new NscRayRecorder();
        tracer.Run(Enumerable.Repeat(Centre(NscSourceBulkScatter.Once), 20000).ToList(), recorder);
        var cosines = new List<double>();
        foreach (var ray in recorder.Database.Rays)
            for (int k = 0; k < ray.Segments.Count; k++)
                if (ray.Segments[k].Events.HasFlag(NscEvents.BulkScattered))
                {
                    var child = ray.Segments.First(c => c.Parent == k);
                    cosines.Add(Vector3D.Dot(ray.Segments[k].Direction, child.Direction));
                }
        Assert.True(cosines.Count > 19000, $"{cosines.Count} rays scattered; e^(−50) of 20000 should not.");
        double mean = cosines.Average(), s = Math.Sqrt(cosines.Average(c => c * c) - mean * mean);
        Within(0.6, mean, 5 * s / Math.Sqrt(cosines.Count), "mean cosine of the scattered rays");
    }

    /// <summary>Rayleigh (1 + μ²): mean 0 and E[μ²] = 2/5 (derived: ∫μ²(1 + μ²)dμ / ∫(1 + μ²)dμ on
    /// [−1, 1]). Cone of 40°: μ uniform on [cos 40°, 1], mean (1 + cos 40°)/2 (derived).</summary>
    [Fact]
    public void RayleighAndCone_SampleTheirDistributions()
    {
        var rng = new SplitMix64(3);
        const int n = 200000;
        double sum = 0, sq = 0, sq4 = 0;
        for (int i = 0; i < n; i++)
        {
            double mu = NscRayleighPhase.Instance.SampleCosine(rng.NextDouble());
            Assert.InRange(mu, -1, 1);
            sum += mu; sq += mu * mu; sq4 += mu * mu * mu * mu;
        }
        // var(μ) ≤ E[μ²]; var(μ²) = E[μ⁴] − E[μ²]².
        Within(0, sum / n, 5 * Math.Sqrt(sq / n / n), "Rayleigh mean cosine");
        Within(0.4, sq / n, 5 * Math.Sqrt((sq4 / n - (sq / n) * (sq / n)) / n), "Rayleigh E[μ²]");

        var cone = new NscConePhase(40);
        double c = Math.Cos(40 * Math.PI / 180), csum = 0;
        for (int i = 0; i < n; i++)
        {
            double mu = cone.SampleCosine(rng.NextDouble());
            Assert.InRange(mu, c - 1e-15, 1);
            csum += mu;
        }
        // Uniform on [c, 1]: standard deviation (1 − c)/√12.
        Within((1 + c) / 2, csum / n, 5 * (1 - c) / Math.Sqrt(12) / Math.Sqrt(n), "cone mean cosine");
        Assert.Equal((1 + c) / 2, cone.MeanCosine, 15);
    }

    // ============================================================ gate: Mie

    /// <summary>Q_ext, Q_sca and g against miepython 3.3.1 <c>efficiencies_mx(m, x)</c>
    /// (op-81-help/miepython-oracle.txt). Both sum the same series to Wiscombe's N (miepython: error
    /// "around 1e-6" from the truncation, the same in both), so they agree far closer than that;
    /// the allowance is 1e-6 relative.</summary>
    [Theory]
    [InlineData(1.5, 0.5, 0.014566628235594573, 0.04886604418056481)]
    [InlineData(1.5, 1.0, 0.21509759604288553, 0.19894249463608737)]
    [InlineData(1.5, 5.0, 3.9278267315833557, 0.7072947840169671)]
    [InlineData(1.5, 10.0, 2.881998952075896, 0.742912898568678)]
    [InlineData(1.5, 100.0, 2.094387814676545, 0.8182464399386712)]
    [InlineData(1.33, 3.0, 1.7533969840974128, 0.7832007711510385)]
    [InlineData(0.75, 2.0, 0.2772746957305371, 0.5364898708679892)]
    [InlineData(1.2, 1000.0, 2.0291092409139626, 0.9311225045850574)]
    public void MieEfficiencies_MatchMiepython(double m, double x, double q, double g)
    {
        var mie = new NscMie(m, x);
        Relative(q, mie.Qext, 1e-6, $"Q_ext (m = {m}, x = {x})");
        Relative(q, mie.Qsca, 1e-6, $"Q_sca (m = {m}, x = {x}; real index: equal to Q_ext)");
        Relative(g, mie.Asymmetry, 1e-6, $"g (m = {m}, x = {x})");
    }

    /// <summary>The unpolarized phase function (∫ p dΩ = 1) against miepython's
    /// <c>i_unpolarized(m, x, mu, norm='one')</c> at μ = 1, 0.5, 0, −0.5, −1.</summary>
    [Theory]
    [InlineData(1.5, 1.0, new[] { 0.18158871848388775, 0.0936573548160561, 0.057402219233903116, 0.05380710755467071, 0.06902944092333932 })]
    [InlineData(1.5, 5.0, new[] { 1.977661569047834, 0.048869371963749506, 0.012471367656281542, 0.008354583332168805, 0.044650463723928405 })]
    public void MiePhaseFunction_MatchesMiepython(double m, double x, double[] expected)
    {
        var mie = new NscMie(m, x);
        double[] mus = { 1, 0.5, 0, -0.5, -1 };
        for (int i = 0; i < mus.Length; i++)
            Relative(expected[i], mie.Phase(mus[i]), 1e-6, $"p(μ = {mus[i]}) for m = {m}, x = {x}");
    }

    /// <summary>The Rayleigh limit: Q_sca/((8/3)x⁴((m² − 1)/(m² + 2))²) → 1. Its first correction is
    /// of order x² (the series in x has only even powers beyond the leading term), so the deviation
    /// falls about 100-fold from x = 0.1 to x = 0.01; it is checked to fall between 50- and 200-fold
    /// and to be below 1e-3 at x = 0.01.</summary>
    [Fact]
    public void MieEfficiency_TendsToTheRayleighLimitForSmallSpheres()
    {
        const double m = 1.5;
        double L = (m * m - 1) / (m * m + 2);
        double Deviation(double x) => new NscMie(m, x).Qsca / (8.0 / 3 * Math.Pow(x, 4) * L * L) - 1;
        double d1 = Deviation(0.1), d2 = Deviation(0.01);
        Assert.True(Math.Abs(d2) < 1e-3, $"deviation at x = 0.01 is {d2:R}");
        double ratio = d1 / d2;
        Assert.InRange(ratio, 50, 200);
    }

    /// <summary>Large spheres: Q_ext tends to 2 ("twice its geometrical cross-sectional area",
    /// extinction paradox). At x = 1000 (m = 1.2) it is within 0.03 of 2, and closer than at
    /// x = 10 and 100 (m = 1.5, oracle values 2.88 and 2.094).</summary>
    [Fact]
    public void MieEfficiency_TendsToTwoForLargeSpheres()
    {
        double q10 = new NscMie(1.5, 10).Qext, q100 = new NscMie(1.5, 100).Qext, q1000 = new NscMie(1.2, 1000).Qext;
        Assert.True(Math.Abs(q1000 - 2) < 0.03, $"Q_ext(1000) = {q1000:R}");
        Assert.True(Math.Abs(q1000 - 2) < Math.Abs(q100 - 2) && Math.Abs(q100 - 2) < Math.Abs(q10 - 2), $"{q10:R}, {q100:R}, {q1000:R}");
    }

    /// <summary>The sampling table integrates to 1 and keeps the series' g within its stated
    /// tolerance; its samples' mean cosine equals g within 5 standard errors plus that tolerance.</summary>
    [Fact]
    public void MiePhaseTable_KeepsTheAsymmetry()
    {
        var phase = new NscMiePhase(new NscMie(1.5, 5));
        Within(phase.Mie.Asymmetry, phase.TableMeanCosine, NscMiePhase.Tolerance, "table mean cosine");
        var rng = new SplitMix64(11);
        const int n = 200000;
        double sum = 0, sq = 0;
        for (int i = 0; i < n; i++)
        {
            double mu = phase.SampleCosine(rng.NextDouble());
            sum += mu; sq += mu * mu;
        }
        double mean = sum / n, s = Math.Sqrt(sq / n - mean * mean);
        Within(phase.Mie.Asymmetry, mean, 5 * s / Math.Sqrt(n) + NscMiePhase.Tolerance, "sampled mean cosine");
    }

    /// <summary>The Mie DLL's mean free path from density: 1/(N·Q_sca·πr²) with N = density·10⁶ m⁻³
    /// (derived). Radius 1 µm, 10⁶ cm⁻³, host index 1 at 0.5 µm (x = 4π): L = 1/(Q_sca·π) m.</summary>
    [Fact]
    public void MieDll_MeanFreePath_FromDensity()
    {
        var physics = new NscVolumePhysics { Model = NscVolumeModel.DllDefined, Dll = "Mie", DllParameters = new[] { 1.5, 1.0, 1e6 } };
        var at = new NscVolumeAt(physics, 0.5, 1.0, "test");
        double q = new NscMie(1.5, 4 * Math.PI).Qsca;
        Relative(1 / (q * Math.PI), at.MeanFreePath, 1e-12, "Mie mean free path");
    }

    /// <summary>Rayleigh DLL: the mean path belongs to the reference wavelength and scales as λ⁴
    /// (the help's λ⁻⁴ in P(θ)): at twice the reference wavelength it is 16 M.</summary>
    [Fact]
    public void RayleighDll_MeanPathScalesAsLambdaToTheFourth()
    {
        var physics = new NscVolumePhysics { Model = NscVolumeModel.DllDefined, Dll = "Rayleigh.dll", MeanPath = 0.01, DllParameters = new[] { 0.5, 0.9 } };
        Relative(0.16, new NscVolumeAt(physics, 1.0, 1, "test").MeanFreePath, 1e-14, "L(2λ_ref) = 16 M");
    }

    // ============================================================ gate: phosphor energy closes

    private sealed class PhosphorFiles : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "op81-" + Path.GetRandomFileName());
        public string Zas => Path.Combine(Dir, "a.ZAS");
        public string Zes => Path.Combine(Dir, "e.ZES");
        public string Zqe => Path.Combine(Dir, "q.ZQE");

        public PhosphorFiles()
        {
            Directory.CreateDirectory(Dir);
            // Arbitrary test spectra in the help's layouts: absorption flat on 0.40–0.50 µm, emission
            // flat on 0.50–0.70 µm for every incident wavelength, quantum yield 0.8 everywhere.
            File.WriteAllText(Zas, "!absorption\n5 0\n0.40 1\n0.425 1\n0.45 1\n0.475 1\n0.50 1\n");
            File.WriteAllText(Zes, "!emission\n5 1 1\n450\n500 1\n550 1\n600 1\n650 1\n700 1\n");
            File.WriteAllText(Zqe, "!quantum yield\n1 0\n0.45 0.8\n");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    /// <summary>The density for which the photoluminescence mean free path at λ_x is R:
    /// l = [ε·c·ln 10]⁻¹ cm → c = 1/(ε·ln 10·l) mol/L, density = c·N_A/1000 cm⁻³ (derived from the
    /// help's formula).</summary>
    private static double DensityFor(double epsilon, double lengthMeters) =>
        1 / (epsilon * Math.Log(10) * lengthMeters * 100) * NscPhosphor.Avogadro / 1000;

    private static NscVolumePhysics Phosphor(PhosphorFiles f) => new()
    {
        Model = NscVolumeModel.PhosphorsAndFluorescence,
        Phosphor = new NscPhosphor
        {
            Absorption = NscSpectrumTable.Parse(File.ReadAllText(f.Zas), "a.ZAS", 5),
            Emission = NscEmissionTable.Parse(File.ReadAllText(f.Zes), "e.ZES"),
            QuantumYield = NscSpectrumTable.Parse(File.ReadAllText(f.Zqe), "q.ZQE", 1),
            ExtinctionCoefficient = 1e5, ExtinctionMicrometers = 0.45, DensityPerCm3 = DensityFor(1e5, R),
        },
    };

    /// <summary>
    /// A phosphor sphere, rays from the centre at 0.45 µm, one event per branch. Checks: the
    /// photoluminescence mean free path at λ_x is R; the converted fraction of rays is 1 − e^(−1);
    /// each event loses exactly what it does not re-emit (Converted + ConversionLoss = events·I);
    /// the mean re-emitted power per event is Q·λ_in·E[1/λ] = 0.8·0.45·ln(0.7/0.5)/0.2 for λ uniform
    /// on 0.5–0.7 µm (derived); and transmitted (escaped) + absorbed + conversion loss = emitted.
    /// A Detector Color with spectral bins outside sees both wavelengths.
    /// </summary>
    [Fact]
    public void PhosphorEnergy_ConvertedPlusAbsorbedPlusTransmitted_Closes()
    {
        using var files = new PhosphorFiles();
        var physics = Phosphor(files);
        Relative(R, physics.Phosphor!.PhotoluminescenceMeanFreePath(0.45), 1e-12, "l(λ_x)");
        var detector = new NscObject
        {
            Geometry = new NscDetectorColor(1, 1, 1, 1, spectral: (4, 0.4, 0.8)),
            Placement = new NscPlacement(Z: 2 * R),
        };
        var tracer = new NscTracer(Sphere(physics, detector), 0.45, Plain with { SystemWavelengths = new[] { 0.45 } });
        const int n = 20000;
        var result = tracer.Run(Enumerable.Repeat(Centre(NscSourceBulkScatter.Once), n).ToList());
        var l = result.Ledger;
        double p = 1 - Math.Exp(-1);
        Within(p, (double)l.ConversionEvents / n, 5 * Math.Sqrt(p * (1 - p) / n), "converted fraction");
        Assert.Equal(l.ConversionEvents, l.VolumeEvents);
        Within(l.ConversionEvents, l.Converted + l.ConversionLoss, 1e-9, "each event: re-emitted + lost = incident");
        Within(n, l.Escaped + l.Absorbed + l.ConversionLoss, 1e-9, "transmitted + absorbed + converted-loss = emitted");
        Within(0, l.Residual, 1e-9, "ledger residual");
        // Per event the factor 0.36/λ lies in [0.514, 0.72]: its standard deviation is below 0.06.
        double expected = 0.8 * 0.45 * Math.Log(0.7 / 0.5) / 0.2;
        Within(expected, l.Converted / l.ConversionEvents, 5 * 0.06 / Math.Sqrt(l.ConversionEvents), "mean re-emitted power per event");

        var data = Assert.IsType<NscFlatDetectorData>(Assert.Single(result.Detectors!));
        var spectrum = data.Spectrum!;
        Assert.True(spectrum[0] > 0, "unconverted light (0.45 µm) on the detector");
        Assert.True(spectrum[1] > 0 && spectrum[2] > 0, "converted light (0.5–0.7 µm) on the detector");
        Relative(data.Flux.Sum(), spectrum.Sum(), 1e-12, "every hit is binned at its own wavelength");
    }

    /// <summary>A converted ray carries its new wavelength: the child of a photoluminescence
    /// segment has a wavelength in 0.5–0.7 µm, wavelength number 0 (not a system wavelength).</summary>
    [Fact]
    public void ConvertedRay_CarriesItsWavelength()
    {
        using var files = new PhosphorFiles();
        var tracer = new NscTracer(Sphere(Phosphor(files)), 0.45, Plain with { SystemWavelengths = new[] { 0.45 } });
        for (long k = 0; k < 100; k++)
        {
            var tree = tracer.TraceSingle(Centre(NscSourceBulkScatter.Once), k);
            int b = tree.Segments.ToList().FindIndex(s => s.Events.HasFlag(NscEvents.BulkScattered));
            if (b < 0) continue;
            Assert.Null(tree.Segments[b].AirMicrometers);
            var child = tree.Segments.First(s => s.Parent == b);
            Assert.InRange(child.AirMicrometers!.Value, 0.5, 0.7);
            Assert.Equal(0, child.WavelengthNumber);
            return;
        }
        Assert.Fail("No ray converted in 100 (probability e^(−100)).");
    }

    /// <summary>Volume events draw from each launched ray's own stream and shifted optics are pure,
    /// so a run is the same to the bit on one thread and on many: every ledger line and the
    /// detector's arrays.</summary>
    [Fact]
    public void VolumeRuns_AreBitwiseTheSame_AtAnyThreadCount()
    {
        using var files = new PhosphorFiles();
        var physics = Phosphor(files);
        var detector = new NscObject { Geometry = new NscDetectorColor(1, 1, 4, 4, spectral: (4, 0.4, 0.8)), Placement = new NscPlacement(Z: 2 * R) };
        var rays = Enumerable.Repeat(Centre(), 3000).ToList();
        NscRunResult Run(int? threads) =>
            new NscTracer(Sphere(physics, detector), 0.45, Plain with { SystemWavelengths = new[] { 0.45 }, MaxDegreeOfParallelism = threads }).Run(rays);
        var one = Run(1);
        var many = Run(null);
        Assert.Equal(JsonSerializer.Serialize(one.Ledger), JsonSerializer.Serialize(many.Ledger));
        var a = (NscFlatDetectorData)one.Detectors![0];
        var b = (NscFlatDetectorData)many.Detectors![0];
        Assert.Equal(a.Flux, b.Flux);
        Assert.Equal(a.Spectrum, b.Spectrum);
        Assert.Equal(a.Y, b.Y);
    }

    // ============================================================ wavelength shift

    /// <summary>"1, 2, 1.0": every bulk scatter shifts wavelength 1 to 2. The child segments are at
    /// 0.6 µm (number 2), X_WAVESHIFT(1,2) passes exactly the scattered rays and X_WAVESHIFT(2,1)
    /// none, and the database keeps the shifted wavelengths through this program's ZRD format
    /// (version 2).</summary>
    [Fact]
    public void WavelengthShift_ShiftsAndIsRecorded()
    {
        var physics = new NscVolumePhysics
        {
            Model = NscVolumeModel.AngleScattering, MeanPath = R / 2, AngleDegrees = 180,
            WavelengthShift = NscWavelengthShift.Parse("1, 2, 1.0"),
        };
        var tracer = new NscTracer(Sphere(physics), 0.5, Plain with { SystemWavelengths = new[] { 0.5, 0.6 } });
        var recorder = new NscRayRecorder();
        recorder.PrepareRun(null, 1);
        var result = tracer.Run(Enumerable.Repeat(Centre(NscSourceBulkScatter.Once), 200).ToList(), recorder);
        var db = recorder.Database;
        var forward = NscRayFilter.Parse("X_WAVESHIFT(1,2)")!;
        var backward = NscRayFilter.Parse("X_WAVESHIFT(2,1)")!;
        int scattered = 0;
        foreach (var ray in db.Rays)
        {
            int b = ray.Segments.ToList().FindIndex(s => s.Events.HasFlag(NscEvents.BulkScattered));
            Assert.Equal(b >= 0, forward.TreePasses(ray, db.Runs[ray.Run]));
            Assert.False(backward.TreePasses(ray, db.Runs[ray.Run]));
            if (b < 0) continue;
            scattered++;
            var child = ray.Segments.First(s => s.Parent == b);
            Assert.Equal((0.6, 2), (child.AirMicrometers!.Value, child.WavelengthNumber!.Value));
            Assert.Equal(0.6, db.Wavelength(ray, child));
        }
        Assert.Equal(result.Ledger.VolumeEvents, scattered);
        Assert.True(scattered > 100, $"{scattered} of 200 scattered (expected 1 − e^(−2) of them)");

        using var stream = new MemoryStream();
        NscZrdFile.Write(stream, db, NscZrdFormat.OpenSimFull, LensUnit.Millimeter);
        stream.Position = 0;
        var back = NscZrdFile.Read(stream, LensUnit.Millimeter);
        for (int i = 0; i < db.Rays.Count; i++)
            for (int k = 0; k < db.Rays[i].Segments.Count; k++)
                Assert.Equal((db.Rays[i].Segments[k].AirMicrometers, db.Rays[i].Segments[k].WavelengthNumber),
                    (back.Rays[i].Segments[k].AirMicrometers, back.Rays[i].Segments[k].WavelengthNumber));
    }

    [Fact]
    public void WavelengthShiftString_ParsesAndChoosesByRelativeProbability()
    {
        var shift = NscWavelengthShift.Parse("1, 2, 50.0; 1, 3, 40.0; 1, 1, 10.0");
        Assert.Equal(3, shift.Transitions.Count);
        Assert.Equal(2, shift.Choose(1, 0.0));
        Assert.Equal(2, shift.Choose(1, 0.49));
        Assert.Equal(3, shift.Choose(1, 0.5));
        Assert.Equal(1, shift.Choose(1, 0.95));
        Assert.Equal(4, shift.Choose(4, 0.3));   // no transition from 4: unchanged
        Assert.Throws<FormatException>(() => NscWavelengthShift.Parse("1, 2"));
        Assert.Throws<FormatException>(() => NscWavelengthShift.Parse("0, 2, 1"));
    }

    // ============================================================ spectrum files

    /// <summary>ZES with two incident columns (linear between them, help: five or fewer), and the
    /// emission sample restricted above the incident wavelength.</summary>
    [Fact]
    public void EmissionTable_InterpolatesAndSamplesAboveTheIncidentWavelength()
    {
        var table = NscEmissionTable.Parse("!e\n5 2 0\n0.4 0.5\n0.50 1 3\n0.55 1 3\n0.60 1 3\n0.65 1 3\n0.70 1 3\n", "t.ZES");
        foreach (double v in table.At(0.45)) Within(2, v, 1e-14, "halfway between the columns 1 and 3");
        var rng = new SplitMix64(5);
        for (int i = 0; i < 1000; i++)
            Assert.InRange(table.Sample(0.62, rng.NextDouble())!.Value, 0.62, 0.70);
        Assert.Null(table.Sample(0.75, 0.5));
        Assert.Contains("at least five", Assert.Throws<FormatException>(() => NscEmissionTable.Parse("4 1 0\n0.4\n0.5 1\n0.6 1\n0.7 1\n0.8 1\n", "s.ZES")).Message);
        Assert.Contains("at least 5", Assert.Throws<FormatException>(() => NscSpectrumTable.Parse("3 0\n0.4 1\n0.5 1\n0.6 1\n", "s.ZAS", 5)).Message);
        Assert.Equal(0.001, NscSpectrumTable.Parse("1 1\n450 0.7\n", "q.ZQE", 1).Micrometers[0] / 450, 15);
    }

    // ============================================================ refusals

    [Fact]
    public void UnbuiltAndInvalidVolumePhysics_AreRefusedByName()
    {
        var dll = new NscVolumePhysics { Model = NscVolumeModel.DllDefined, Dll = "poly_bulk_scat.dll", MeanPath = R };
        Assert.Contains(Sphere(dll).Validate(), p => p.Contains("poly_bulk_scat.dll") && p.Contains("not built"));

        var onSurface = new NscScene
        {
            Objects = new[] { new NscObject { Geometry = new NscRectangle(R, R), VolumePhysics = new NscVolumePhysics { Model = NscVolumeModel.AngleScattering, MeanPath = R, AngleDegrees = 10 } } },
        };
        Assert.Contains(onSurface.Validate(), p => p.Contains("needs a refractive volume"));

        var shift = new NscVolumePhysics { Model = NscVolumeModel.AngleScattering, MeanPath = R, AngleDegrees = 10, WavelengthShift = NscWavelengthShift.Parse("1,2,1") };
        Assert.Contains("system wavelengths", Assert.Throws<ArgumentException>(() => new NscTracer(Sphere(shift), 0.5)).Message);
        Assert.Contains("the system has 1", Assert.Throws<ArgumentException>(() => new NscTracer(Sphere(shift), 0.5, new NscTraceSettings { SystemWavelengths = new[] { 0.5 } })).Message);

        var badAngle = new NscVolumePhysics { Model = NscVolumeModel.AngleScattering, MeanPath = R, AngleDegrees = 200 };
        Assert.Contains(Sphere(badAngle).Validate(), p => p.Contains("0–180"));
    }

    // ============================================================ project

    private static OpticsNonSequentialSettings Saved(OpticsNscVolumePhysicsSettings volume) => new()
    {
        Objects = new() { new OpticsNscObjectSettings { Type = "Sphere", Parameters = new() { 1, 1 }, VolumePhysics = volume } },
    };

    [Fact]
    public void SavedVolumePhysics_BecomesTheScene_AndRoundTripsThroughTheProjectFile()
    {
        var volume = new OpticsNscVolumePhysicsSettings { Model = "Angle Scattering", MeanPath = 2, Angle = 15, WavelengthShift = "1,2,1" };
        var scene = ProjectNonSequential.Scene(new OpticsAnalysisContext("P", new OpticsSettings { NonSequential = Saved(volume) }), LensUnit.Millimeter)!;
        var physics = scene.Objects[0].VolumePhysics!;
        Assert.Equal(NscVolumeModel.AngleScattering, physics.Model);
        Within(0.002, physics.MeanPath, 1e-18, "mean path 2 mm in meters");
        Assert.Equal(15, physics.AngleDegrees);
        Assert.Single(physics.WavelengthShift!.Transitions);

        var project = new SimProject { Name = "V", AnalysisType = "Optics", Optics = new OpticsSettings { NonSequential = Saved(volume) } };
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = serializer.Load(path).Optics!.NonSequential!.Objects![0].VolumePhysics;
            Assert.Equal(volume, loaded);
        }
        finally
        {
            File.Delete(path);
        }

        // Every field: a phosphor with all its fields set survives JSON as the project writes it.
        var full = new OpticsNscVolumePhysicsSettings
        {
            Model = "Phosphors and Fluorescence", MeanPath = 1, Angle = 2, Dll = "Mie.dll", DllParameters = new() { 1.5, 2, 3 },
            WavelengthShift = "", PhotoluminescenceModel = "Basic", AbsorptionFile = "C:\\a.ZAS", EmissionFile = "C:\\e.ZES", QuantumYieldFile = "C:\\q.ZQE",
            ExtinctionCoefficient = 1e5, ExtinctionWavelength = 0.47, PhosphorDensity = 3.1e17, ConsiderMie = true, ParticleIndex = 1.5,
            ParticleRadius = 8, ParticleDensity = 1e9, MieMeanFreePath = 0.1, UseMieMeanFreePath = true,
        };
        var text = JsonSerializer.Serialize(full);
        var again = JsonSerializer.Deserialize<OpticsNscVolumePhysicsSettings>(text)!;
        Assert.Equal(full with { DllParameters = null }, again with { DllParameters = null });
        Assert.Equal(full.DllParameters, again.DllParameters);
    }

    [Fact]
    public void ExcitationFilesAndUnknownModels_AreRefusedByName()
    {
        var zex = new OpticsNscVolumePhysicsSettings { Model = "Phosphors and Fluorescence", AbsorptionFile = "C:\\a.ZAS", EmissionFile = "C:\\e.ZES", QuantumYieldFile = "C:\\x.ZEX" };
        var problems = new List<string>();
        Assert.Null(ProjectNscVolume.Physics(zex, LensUnit.Millimeter, "Object 1", problems));
        Assert.Contains(problems, p => p.Contains("ZEX") && p.Contains("not built"));

        problems.Clear();
        Assert.Null(ProjectNscVolume.Physics(new OpticsNscVolumePhysicsSettings { Model = "Fog" }, LensUnit.Millimeter, "Object 1", problems));
        Assert.Contains(problems, p => p.Contains("is not one of"));
    }
}
