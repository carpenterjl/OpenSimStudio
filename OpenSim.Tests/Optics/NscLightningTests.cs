// OP-85
using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.NonSequential;
using OpenSim.Optics.NonSequential.Detectors;
using OpenSim.Optics.NonSequential.Lightning;
using OpenSim.Optics.NonSequential.Sources;
using OpenSim.Optics.Paraxial;
using Xunit;

namespace OpenSim.Tests.Optics;

/// <summary>
/// The noise-free fast illumination solve (plan item OP-85; docs/Optics-Conventions.md §79):
/// LightningTrace by flux tubes, the Source Illumination Map and the NSLT merit operand.
/// <para>
/// <b>Closed forms</b> (derived here). A Source Point with semi-cone α emits P uniformly in solid
/// angle (OP-77's reading, Conventions §72), so its intensity is I = P/(2π(1 − cos α)). A
/// rectangle [x1, x2] × [y1, y2] in the plane z = d, seen from the origin, subtends
/// Ω = F(x2, y2) − F(x1, y2) − F(x2, y1) + F(x1, y1) with F(x, y) = arctan(xy/(d√(x² + y² + d²)))
/// (derived: ∂²F/∂x∂y = d/(x² + y² + d²)^{3/2}, the solid-angle density of the plane, and F
/// vanishes on both axes; F(a, a)·4 is OP-88's square formula, checked there by quadrature). A
/// pixel wholly inside the cone receives IΩ.
/// </para>
/// <para>
/// <b>Expected convergence.</b> The method maps each chart cell's corner rays and spreads its power
/// uniformly over the piecewise-linear image of the cell; the misplaced power per cell is the
/// cell's power times a displacement of the order of the image's curvature times the cell size
/// squared, and only cells cut by a pixel boundary contribute, so a pixel's flux error is O(h²) in
/// the chart cell size h (second order): halving h (one Ray Sampling step) divides it by about 4.
/// The tests require at least 2.5. Cells cut by an object's or detector's edge are halved Edge
/// Sampling times; what is left is reported as <see cref="NscLightningResult.Unresolved"/>, and
/// the flux error from it is at most that power (each corner ray carries a quarter of a cell whose
/// true share lies between none and all of it).
/// </para>
/// </summary>
public class NscLightningTests
{
    private const double Mm = 1e-3;
    private static readonly IReadOnlyList<OpticalCatalog> NoCatalogs = Array.Empty<OpticalCatalog>();

    private static readonly NscSourceEmitOptions Options = new() { SystemWavelengths = new[] { new NscSpectralLine(0.55, 1) } };
    private static readonly NscTraceSettings Unpolarized = new() { UsePolarization = false };

    private static double F(double x, double y, double d) => Math.Atan(x * y / (d * Math.Sqrt(x * x + y * y + d * d)));

    private static double RectangleSolidAngle(double x1, double x2, double y1, double y2, double d) =>
        F(x2, y2, d) - F(x1, y2, d) - F(x2, y1, d) + F(x1, y1, d);

    private static double ConeIntensity(double power, double alphaDegrees) => power / (2 * Math.PI * (1 - Math.Cos(alphaDegrees * Math.PI / 180)));

    private static NscObject Point(double coneDegrees, double power = 1, int rays = 1000) =>
        new() { Geometry = new NscSourcePoint(coneDegrees, new NscSourceCommon { Power = power, AnalysisRays = rays }) };

    private static NscObject Detector(double halfWidth, int pixels, double z, double x = 0, double y = 0) => new()
    {
        Geometry = new NscDetectorRectangle(halfWidth, halfWidth, pixels, pixels), Placement = new NscPlacement(x, y, z), Material = NscMaterial.Absorb,
    };

    private static NscLightningResult Lightning(NscScene scene, int ray, int edge, int? threads = null, NscTraceSettings? trace = null) =>
        NscLightningTrace.Run(scene, trace ?? Unpolarized, Options, new NscLightningSettings { RaySampling = ray, EdgeSampling = edge, MaxDegreeOfParallelism = threads });

    private static NscFlatDetectorData Flat(NscLightningResult r, int number) => (NscFlatDetectorData)r.Detectors.Single(d => d.ObjectNumber == number);

    private static void Within(double expected, double actual, double tolerance, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"{what}: expected {expected:R}, got {actual:R} (difference {actual - expected:R}, allowed {tolerance:R}).");

    // ================================================================ the analytic cone

    /// <summary>A 1 W, 30° Source Point onto a ±40 mm, 8 × 8 Detector Rectangle 50 mm away: the cone
    /// (radius 50·tan 30° = 28.9 mm) lands wholly on the detector, so every mesh ray lands, nothing
    /// is refined and the detector total is the source power to rounding; the 16 pixels inside the
    /// cone (|x|, |y| ≤ 20 mm, corner radius 28.3 mm) converge to IΩ at second order.</summary>
    [Fact]
    public void AConeOnADetector_ConvergesToTheClosedForm_AtSecondOrder()
    {
        double d = 50 * Mm, a = 40 * Mm, pixel = 10 * Mm;
        var scene = new NscScene { Objects = new[] { Point(30), Detector(a, 8, d) } };
        double intensity = ConeIntensity(1, 30);
        double Error(NscLightningResult r)
        {
            var data = Flat(r, 2);
            double sum = 0;
            int count = 0;
            for (int iy = 2; iy < 6; iy++)
                for (int ix = 2; ix < 6; ix++)
                {
                    double x1 = -a + ix * pixel, y1 = -a + iy * pixel;
                    double exact = intensity * RectangleSolidAngle(x1, x1 + pixel, y1, y1 + pixel, d);
                    double e = (data.Flux[data.Index(ix, iy)] - exact) / exact;
                    sum += e * e;
                    count++;
                }
            return Math.Sqrt(sum / count);
        }

        var coarse = Lightning(scene, 0, 0);
        var fine = Lightning(scene, 1, 0);
        foreach (var r in new[] { coarse, fine })
        {
            // Σ cell powers = Σ (w(s_{i+1}) − w(s_i))·(1/n) = 1; each triangle's pixel shares add to 1.
            Within(1, r.Emitted, 1e-12, "emitted");
            Within(1, Flat(r, 2).Flux.Sum(), 1e-12, "detector total");
            Assert.Equal(0, r.Missed);
            Assert.Equal(0, r.Unresolved);
            Assert.Equal(0, r.RefinedCells);
        }
        // Bands of 4 rows each trace their 5 node rows of n + 1 rays (the shared row twice): (n/4)·5·(n + 1).
        Assert.Equal((8L * 5 * 33, 16L * 5 * 65), (coarse.Rays, fine.Rays));
        double e0 = Error(coarse), e1 = Error(fine);
        Assert.True(e1 < 5e-3, $"relative RMS error at 64 cells {e1}");
        Assert.True(e0 / e1 >= 2.5, $"second order: errors {e0} at 32 cells and {e1} at 64 cells, ratio {e0 / e1}");
        var data1 = Flat(fine, 2);
        Assert.All(data1.FluxSquares, v => Assert.True(double.IsNaN(v)));   // noise-free: no noise estimate
        Assert.Equal(8 * 8, data1.PixelCount);
    }

    /// <summary>A detector smaller than the beam and off the axis (centre (1.3, 0.7) mm, half width
    /// 10 mm, 50 mm from a 30° cone): the cells its edges cut are refined, the unresolved power
    /// falls about as 2^−k with Edge Sampling k (each halving keeps the cut cells, half as wide), and
    /// the detector total stays within the unresolved power (plus 10⁻³ for the resolved cells'
    /// interpolation) of IΩ. The ledger identity is exact: emitted − missed − unresolved ≤ detected
    /// ≤ emitted − missed.</summary>
    [Fact]
    public void Edges_AreRefined_AndTheUnresolvedPowerBoundsTheError()
    {
        double d = 50 * Mm, a = 10 * Mm, cx = 1.3 * Mm, cy = 0.7 * Mm;
        var scene = new NscScene { Objects = new[] { Point(30), Detector(a, 4, d, cx, cy) } };
        double exact = ConeIntensity(1, 30) * RectangleSolidAngle(cx - a, cx + a, cy - a, cy + a, d);
        var r0 = Lightning(scene, 0, 0);
        var r3 = Lightning(scene, 0, 3);
        Assert.True(r0.Unresolved > 0);
        Assert.True(r3.Unresolved < r0.Unresolved / 4, $"unresolved {r0.Unresolved} at edge 0, {r3.Unresolved} at edge 3");
        Assert.True(r3.RefinedCells > 0);
        foreach (var r in new[] { r0, r3 })
        {
            double detected = Flat(r, 2).Flux.Sum();
            Within(1, r.Emitted, 1e-12, "emitted");
            Assert.True(detected >= r.Emitted - r.Missed - r.Unresolved - 1e-12 && detected <= r.Emitted - r.Missed + 1e-12,
                $"ledger: detected {detected}, emitted {r.Emitted}, missed {r.Missed}, unresolved {r.Unresolved}");
            Within(exact, detected, r.Unresolved + 1e-3 * exact, $"detector total at edge sampling {r.EdgeDepth}");
        }
    }

    /// <summary>The bands are traced in parallel and deposited in band order: one thread and many
    /// give the same bits (with refinement at the edges).</summary>
    [Fact]
    public void TheResult_IsBitwiseTheSame_AtAnyThreadCount()
    {
        var scene = new NscScene { Objects = new[] { Point(30), Detector(10 * Mm, 6, 50 * Mm, 1.3 * Mm, 0.7 * Mm) } };
        var one = Flat(Lightning(scene, 1, 2, threads: 1), 2);
        var many = Flat(Lightning(scene, 1, 2), 2);
        Assert.Equal(one.Flux.Select(BitConverter.DoubleToInt64Bits), many.Flux.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(one.Luminous!.Select(BitConverter.DoubleToInt64Bits), many.Luminous!.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(one.SpatialHits, many.SpatialHits);
    }

    // ================================================================ refusals

    [Fact]
    public void SplittingScatteringCollimatedSourcesAndPolarDetectors_AreRefusedByName()
    {
        var scene = new NscScene { Objects = new[] { Point(30), Detector(40 * Mm, 4, 50 * Mm) } };
        Assert.Contains("splitting", Assert.Throws<NotSupportedException>(() => Lightning(scene, 0, 0, trace: Unpolarized with { Splitting = NscSplitting.Split })).Message);
        Assert.Contains("Scatter NSC Rays", Assert.Throws<NotSupportedException>(() => Lightning(scene, 0, 0, trace: Unpolarized with { ScatterRays = true })).Message);
        Assert.Throws<ArgumentException>(() => Lightning(scene, 6, 0));
        Assert.Throws<ArgumentException>(() => Lightning(scene, 0, -1));

        var ray = new NscScene { Objects = new[] { new NscObject { Geometry = new NscSourceRay(0, 0, 1) }, Detector(40 * Mm, 4, 50 * Mm) } };
        Assert.Contains("collimated", Assert.Throws<NotSupportedException>(() => Lightning(ray, 0, 0)).Message);
        var narrow = new NscScene { Objects = new[] { Point(0), Detector(40 * Mm, 4, 50 * Mm) } };
        Assert.Contains("collimated", Assert.Throws<NotSupportedException>(() => Lightning(narrow, 0, 0)).Message);
        var gaussian = new NscScene { Objects = new[] { new NscObject { Geometry = new NscSourceGaussian(1 * Mm, 5 * Mm) }, Detector(40 * Mm, 4, 50 * Mm) } };
        Assert.Contains("Source Gaussian", Assert.Throws<NotSupportedException>(() => Lightning(gaussian, 0, 0)).Message);

        var polar = new NscScene
        {
            Objects = new[]
            {
                Point(30),
                new NscObject { Geometry = new NscDetectorPolar(90, 100 * Mm, 10, 12), Placement = new NscPlacement(Z: 50 * Mm), Material = NscMaterial.Absorb },
            },
        };
        Assert.Contains("Detector Polar", Assert.Throws<NotSupportedException>(() => Lightning(polar, 0, 0)).Message);
    }

    /// <summary>An isotropic volume source and a cosine-exponent rectangle chart over their whole
    /// far field: every chart is accepted and the emitted power is the source power.</summary>
    [Fact]
    public void OtherSourceTypes_ChartTheirWholeFarField()
    {
        var volume = new NscScene { Objects = new[] { new NscObject { Geometry = new NscSourceVolume(NscVolumeShape.Rectangle, 1 * Mm, 1 * Mm, 1 * Mm, new NscSourceCommon { Power = 2 }) }, Detector(40 * Mm, 4, 50 * Mm) } };
        var rv = Lightning(volume, 0, 1);
        Within(2, rv.Emitted, 1e-12, "volume source power");
        // An isotropic source puts half its power into the +z hemisphere; the detector takes IΩ of it.
        Within(2 / (4 * Math.PI) * RectangleSolidAngle(-40 * Mm, 40 * Mm, -40 * Mm, 40 * Mm, 50 * Mm), Flat(rv, 2).Flux.Sum(), rv.Unresolved + 1e-3, "isotropic source on the detector");
        var cosine = new NscScene { Objects = new[] { new NscObject { Geometry = new NscSourceRectangle(1 * Mm, 1 * Mm, cosineExponent: 2) }, Detector(40 * Mm, 4, 50 * Mm) } };
        Within(1, Lightning(cosine, 0, 0).Emitted, 1e-12, "cosine source power");
    }

    // ================================================================ the gate

    /// <summary>
    /// The plan's gate: on a smooth lens, LightningTrace equals the converged Monte Carlo irradiance
    /// within the mesh resolution. A 1 W, 20° Source Point at the origin; a biconvex Standard Lens
    /// (radii ±40 mm, 6 mm thick, clear semi-diameter 10 mm, index 1.5) at z = 20 mm, which the cone
    /// (radius 7.3 mm there) fills without reaching its edge; a ±25 mm, 10 × 10 absorbing Detector
    /// Rectangle at z = 80 mm. The source lies inside the focal length, so the beam leaves diverging
    /// and has no caustic. Polarization off in both traces (no Fresnel losses).
    /// <list type="bullet">
    /// <item>Monte Carlo: 2,000,000 Sobol rays through OP-77's runner. Each pixel's count is at worst
    /// binomial, σ_k = √(p_k(1 − p_k)/N) with p_k the pixel's share (Sobol sampling does better);
    /// LightningTrace at 256 cells per side must agree within 5σ_k on every pixel.</item>
    /// <item>Convergence: against that 256-cell result, the 32- and 64-cell results differ by
    /// d₀ ≈ C·h₀²(1 − 1/64) and d₁ ≈ C·h₀²(1/4 − 1/64) at second order, a ratio of 4.2; at least
    /// 2.5 is required.</item>
    /// </list>
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void Gate_OnASmoothLens_LightningTraceEqualsTheConvergedMonteCarloIrradiance()
    {
        const int n = 2_000_000;
        var scene = new NscScene
        {
            Objects = new[]
            {
                Point(20, 1, n),
                new NscObject
                {
                    Geometry = new NscStandardLens(40 * Mm, 0, 10 * Mm, 11 * Mm, 6 * Mm, -40 * Mm, 0, 10 * Mm, 11 * Mm),
                    Material = NscMaterial.Of(ParaxialMedium.Constant(1.5)), Placement = new NscPlacement(Z: 20 * Mm),
                },
                Detector(25 * Mm, 10, 80 * Mm),
            },
        };
        var reference = Flat(Lightning(scene, 3, 2), 3);
        var r0 = Flat(Lightning(scene, 0, 2), 3);
        var r1 = Flat(Lightning(scene, 1, 2), 3);
        Within(1, reference.Flux.Sum(), 1e-9, "every mesh ray reaches the detector");

        var mc = NscSourceRun.Run(scene, Unpolarized, Options);
        var monteCarlo = (NscFlatDetectorData)mc.Detectors!.Single(d => d.ObjectNumber == 3);
        for (int k = 0; k < reference.PixelCount; k++)
        {
            double p = Math.Clamp(reference.Flux[k], 0, 1);
            double sigma = Math.Sqrt(p * (1 - p) / n);
            Within(reference.Flux[k], monteCarlo.Flux[k], 5 * sigma + 1e-12, $"pixel {k + 1}: Monte Carlo against LightningTrace");
        }

        double Distance(NscFlatDetectorData a) => Math.Sqrt(a.Flux.Zip(reference.Flux, (x, y) => (x - y) * (x - y)).Sum());
        double d0 = Distance(r0), d1 = Distance(r1);
        Assert.True(d0 / d1 >= 2.5, $"second order: distances {d0} (32 cells) and {d1} (64 cells) from the 256-cell result, ratio {d0 / d1}");
    }

    // ================================================================ project, operand and analyses

    /// <summary>A pure non-sequential project (millimetres): a 1 W Source Point (90° semi-cone, random
    /// analysis rays) at z = −20 mm and a ±20 mm, 10 × 10 Detector Rectangle at z = 0.</summary>
    private static OpticsSettings PointAndDetector(int rays = 5000) => new()
    {
        NonSequential = new OpticsNonSequentialSettings
        {
            Objects = new()
            {
                new OpticsNscObjectSettings { Type = "Source Point", Z = -20, Parameters = new() { 10, rays, 1, 0, 0, 90 }, Source = new OpticsNscSourceSettings { Sampling = "Random" } },
                new OpticsNscObjectSettings { Type = "Detector Rectangle", Parameters = new() { 20, 20, 10, 10 } },
            },
        },
    };

    private static MeritOperandRow Row(string code, int int1, int int2, params double[] data) => MeritOperandRow.Of(code, int1, int2, 0, 0, data);

    private static MeritEvaluation Evaluate(OpticsSettings optics, params MeritOperandRow[] rows) =>
        MeritEvaluator.Evaluate(new MeritFunction(rows), new ProjectMeritConfigurations(optics, NoCatalogs));

    /// <summary>NSLT after a clearing row fills the detectors with the LightningTrace result (the
    /// NSDD total equals the project trace's to the bit, and the closed form PΩ/2π within the
    /// unresolved power); RT? 1 is NSTR with everything off, to the bit; the help's preconditions
    /// and ranges are enforced.</summary>
    [Fact]
    public void Nslt_FillsTheDetectors_AndRtOneIsTheConventionalTrace()
    {
        var optics = PointAndDetector();
        var lightning = Evaluate(optics, Row("NSDD", 1, 0), Row("NSLT", 1, 0, 0, 2, 0), Row("NSDD", 1, 2, 0, 0));
        Assert.False(lightning.HasFailures, string.Join("; ", lightning.Errors));
        Assert.Equal(0, lightning.Rows[1].Value);
        var direct = ProjectNscLightning.Trace(new OpticsAnalysisContext("t", optics), 0, 2);
        double total = Flat(direct, 2).Flux.Sum();
        Within(total, lightning.Rows[2].Value, 1e-12 * total, "NSDD total against the project trace (summed in another order)");
        double a = 20 * Mm, d = 20 * Mm;
        Within(RectangleSolidAngle(-a, a, -a, a, d) / (2 * Math.PI), total, direct.Unresolved + 1e-3, "NSDD total after NSLT");

        var rt = Evaluate(optics, Row("NSDD", 1, 0), Row("NSLT", 1, 0, 0, 0, 1), Row("NSDD", 1, 2, 0, 0));
        var nstr = Evaluate(optics, Row("NSDD", 1, 0), Row("NSTR", 1, 0, 0, 0, 0, 0), Row("NSDD", 1, 2, 0, 0));
        Assert.False(rt.HasFailures, string.Join("; ", rt.Errors));
        Assert.Equal(BitConverter.DoubleToInt64Bits(nstr.Rows[2].Value), BitConverter.DoubleToInt64Bits(rt.Rows[2].Value));

        var noClear = Evaluate(optics, Row("NSLT", 1, 0, 0, 0, 0));
        Assert.Contains("Det# set equal to 0", noClear.Rows[0].Error);
        Assert.Contains("Edge Samp", Evaluate(optics, Row("NSDD", 1, 0), Row("NSLT", 1, 0, 0, 5, 0)).Rows[1].Error);
        Assert.Contains("Ray Samp", Evaluate(optics, Row("NSDD", 1, 0), Row("NSLT", 1, 0, 6, 0, 0)).Rows[1].Error);
        Assert.Contains("RT?", Evaluate(optics, Row("NSDD", 1, 0), Row("NSLT", 1, 0, 0, 0, 2)).Rows[1].Error);
        Assert.Contains("not a source", Evaluate(optics, Row("NSDD", 1, 0), Row("NSLT", 1, 2, 0, 0, 0)).Rows[1].Error);
        Assert.Contains("OP-86", Evaluate(optics, Row("NSLT", 2, 0, 0, 0, 0)).Rows[0].Error);
    }

    [Fact]
    public void TheAnalyses_AreRegistered_AndTheLightningTraceShowsItsMesh()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        Assert.Contains(registry.All, x => x.Id == NscLightningTraceAnalysis.SavedId);
        Assert.Contains(registry.All, x => x.Id == NscSourceIlluminationMapAnalysis.SavedId);
        var context = new OpticsAnalysisContext("t", PointAndDetector());
        var result = new NscLightningTraceAnalysis().Compute(context, new NscLightningTraceSettings(), default);
        Assert.Contains(result.Sections, s => s.Heading == "LightningTrace");
        Assert.NotEmpty(result.Plots);
        Assert.Throws<ArgumentException>(() => new NscLightningTraceAnalysis().Compute(context, new NscLightningTraceSettings { Detector = 1 }, default));
    }

    /// <summary>
    /// Source Illumination Map: a 1 W, 90° Source Point 1000 mm in front of the screen (±500 mm,
    /// 10 × 10 pixels at the global origin), with an absorbing rectangle between them that the map
    /// leaves out ("this analysis only considers the far-field distribution of the sources"). Each
    /// pixel not on the screen's border (the border pixels hold the unresolved edge cells) receives
    /// IΩ with I = P/2π within 2·10⁻³ (128 cells per side: the second-order error is far below);
    /// its lumens are its watts times one factor (the source's spectral average of 683·V(λ)), and a file name is
    /// refused naming OP-78.
    /// </summary>
    [Fact]
    public void TheSourceIlluminationMap_GivesEachPixelTheSolidAngleItSubtends()
    {
        var optics = new OpticsSettings
        {
            NonSequential = new OpticsNonSequentialSettings
            {
                Objects = new()
                {
                    new OpticsNscObjectSettings { Type = "Source Point", Z = -1000, Parameters = new() { 10, 1000, 1, 0, 0, 90 } },
                    new OpticsNscObjectSettings { Type = "Rectangle", Z = -500, Parameters = new() { 600, 600 }, Material = "ABSORB" },
                },
            },
        };
        var context = new OpticsAnalysisContext("t", optics);
        var settings = new NscSourceIlluminationMapSettings { XHalfWidth = 500, YHalfWidth = 500, XPixels = 10, YPixels = 10, RaySampling = NscLightningSampling.X16, EdgeSampling = NscLightningSampling.X16 };
        var scene = ProjectNonSequential.Scene(context, LensUnit.Millimeter)!;
        var chosen = NscSourceIlluminationMapAnalysis.Chosen("", scene);
        Assert.Equal(new[] { 1 }, chosen);
        var screenScene = NscSourceIlluminationMapAnalysis.ScreenScene(scene, chosen, settings, LensUnit.Millimeter, out int screen);
        Assert.Equal(3, screen);
        var result = ProjectNscLightning.Trace(context, screenScene, 2, 2, chosen, null, default);
        var data = Flat(result, screen);
        Assert.True(data.IsColor);
        double d = 1000 * Mm, half = 500 * Mm, pixel = 100 * Mm, intensity = 1 / (2 * Math.PI);
        // One source: every pixel's lumens per watt is that source's spectral average of 683·V(λ).
        double perWatt = data.Luminous!.Sum() / data.Flux.Sum();
        Assert.True(perWatt > 0);
        for (int iy = 1; iy < 9; iy++)
            for (int ix = 1; ix < 9; ix++)
            {
                double x1 = -half + ix * pixel, y1 = -half + iy * pixel;
                double exact = intensity * RectangleSolidAngle(x1, x1 + pixel, y1, y1 + pixel, d);
                int k = data.Index(ix, iy);
                Within(exact, data.Flux[k], 2e-3 * exact, $"pixel ({ix}, {iy}) flux");
                Within(data.Flux[k] * perWatt, data.Luminous![k], 1e-12 * data.Flux[k] * perWatt, $"pixel ({ix}, {iy}) lumens");
            }

        var shown = new NscSourceIlluminationMapAnalysis().Compute(context, settings, default);
        Assert.NotEmpty(shown.Plots);
        Assert.Contains("OP-78", Assert.Throws<NotSupportedException>(() =>
            new NscSourceIlluminationMapAnalysis().Compute(context, settings with { FileName = "lamp.ies" }, default)).Message);
        Assert.Throws<ArgumentException>(() => NscSourceIlluminationMapAnalysis.Chosen("2", scene));
    }
}
