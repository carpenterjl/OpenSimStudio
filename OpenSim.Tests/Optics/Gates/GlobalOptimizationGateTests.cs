using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Optimization.Global;
using OpenSim.Optics.Paraxial;
using Xunit.Abstractions;
using static OpenSim.Tests.Optics.LocalOptimizationFixtures;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-58's gate (plan): "From a deliberately poor start the search reaches the known triplet and
/// double-Gauss forms; a fixed seed reproduces a run exactly at any thread count." The double-Gauss
/// half is not gated (see <see cref="FromParallelPlates_TheGlobalSearchReachesTheCookeTripletForm"/>).
/// <para>
/// <b>The known triplet.</b> The Cooke triplet of Optiland 0.6.3 (MIT licence),
/// <c>optiland/samples/objectives.py</c>, class <c>CookeTriplet</c>, read from the installed
/// package under <c>plans/sources/optics/pyref-venv</c>: radii 22.01359, −435.76044, −22.21328,
/// 20.29192 (stop), 79.68360, −18.39533 mm; thicknesses 3.25896, 6.00755, 0.99997, 4.75041,
/// 2.95208, 42.20778 mm; glasses SK16 (Hikari), F2 (Schott), SK16 (Hikari); EPD 10 mm; angle fields
/// 0, 14, 20°; wavelengths 0.48, 0.55 (primary), 0.65 µm. The two glasses are not in the
/// repository's shipped catalogs, so each is a model glass with the catalog's printed nd and Vd:
/// SK16 nd 1.620409, Vd 60.280874 (hikari.agf, "NM SK16" line) and F2 nd 1.62004, Vd 36.37
/// (schott.agf, "NM F2" line), both from the AGF files under
/// <c>plans/sources/optics/zemaxglass/AGF_files</c>. The published design and every searched
/// design use the same model glasses, so the comparison does not depend on the stand-in.
/// </para>
/// <para>
/// <b>Set-up.</b> The help's recipe (The Global Optimization Algorithm: "the very rough design can
/// be parallel plates of glass with a curvature solve on the last surface to control the focal
/// length"; Suggestions for Using Global Optimizers, items 2 and 3): the last curvature is an F/#
/// solve and the image distance a marginal-ray-height solve (paraxial focus). The F/# is the
/// published design's own, EFL/EPD with its EFL computed by this program's paraxial engine, so the
/// published design with the solve is the published design. Thicknesses and glasses are fixed; the
/// five other curvatures are the variables. The merit function is the Optimization Wizard's (spot
/// radius about the centroid, Gaussian quadrature, 3 rings) generated on the published lens.
/// </para>
/// <para>
/// <b>"Reaches the form"</b> (robust, not radius by radius): the best design's merit is at or below
/// the published design's merit evaluated in this program, and its element powers have the Cooke
/// pattern + − + (the flint element negative). Element power by the thick-lens formula
/// φ = (n − 1)[c₁ − c₂ + (n − 1)t c₁c₂/n] (derived from the two-surface paraxial matrix; only its
/// sign is used), with n the model glass's nd.
/// </para>
/// </summary>
public sealed class GlobalOptimizationGateTests
{
    private readonly ITestOutputHelper _out;

    public GlobalOptimizationGateTests(ITestOutputHelper output) => _out = output;

    private static IReadOnlyList<OpticalCatalog> Catalogs => RefractiveIndexInfoSeed.Catalogs;

    // Optiland 0.6.3, samples/objectives.py, CookeTriplet (cited above).
    private static readonly double[] PublishedRadii = { 22.01359, -435.76044, -22.21328, 20.29192, 79.68360, -18.39533 };
    private static readonly double[] Thicknesses = { 3.25896, 6.00755, 0.99997, 4.75041, 2.95208, 42.20778 };
    private const double Epd = 10;
    private static readonly double[] FieldAngles = { 0, 14, 20 };
    private static readonly double[] Micrometers = { 0.48, 0.55, 0.65 };

    // hikari.agf "NM SK16 1 620603 1.620409 60.280874"; schott.agf "NM F2 2 620364.360 1.62004 36.37".
    private static readonly OpticsModelGlassSettings Sk16 = new() { Nd = 1.620409, Vd = 60.280874 };
    private static readonly OpticsModelGlassSettings F2 = new() { Nd = 1.62004, Vd = 36.37 };

    /// <summary>The triplet with the given front five radii (null: plane) and the last radius as
    /// given, or held by an F/# solve when <paramref name="fNumber"/> is set; the five radii
    /// flagged Variable when <paramref name="variable"/>.</summary>
    private static OpticsSettings Triplet(double?[] radii, double lastRadius, double? fNumber, bool variable, MeritFunction? merit = null)
    {
        OpticsCellFlagSettings V() => Variable("Radius");
        List<OpticsCellFlagSettings>? Flags(bool v) => v ? new() { V() } : null;
        var last = new List<OpticsCellFlagSettings> { Solve("Thickness", "Marginal ray height", 0, 0) };
        if (fNumber is double f) last.Insert(0, Solve("Radius", "F/#", f));
        var system = SystemDataSettings.ToSettings(OpticalSystemData.Default) with
        {
            ApertureType = "EntrancePupilDiameter", ApertureValue = Epd, FieldType = "Angle",
            Fields = FieldAngles.Select(a => new OpticsFieldSettings { Y = a }).ToList(),
            Wavelengths = Micrometers.Select(w => new OpticsWavelengthSettings { Micrometers = w }).ToList(),
            PrimaryWavelength = 2,
        };
        return new OpticsSettings
        {
            SystemData = system,
            Surfaces = new()
            {
                new() { ThicknessIsInfinite = true },
                new() { Radius = radii[0], Thickness = Thicknesses[0], ModelGlass = Sk16, Flags = Flags(variable) },
                new() { Radius = radii[1], Thickness = Thicknesses[1], Flags = Flags(variable) },
                new() { Radius = radii[2], Thickness = Thicknesses[2], ModelGlass = F2, Flags = Flags(variable) },
                new() { Radius = radii[3], Thickness = Thicknesses[3], IsStop = true, Flags = Flags(variable) },
                new() { Radius = radii[4], Thickness = Thicknesses[4], ModelGlass = Sk16, Flags = Flags(variable) },
                new() { Radius = lastRadius, Thickness = Thicknesses[5], Flags = last },
                new(),
            },
            MeritFunction = merit is null ? null : MeritFunctionSettings.ToSettings(merit),
        };
    }

    private static double EflMillimeters(OpticsSettings optics)
    {
        var configurations = new ProjectMeritConfigurations(optics, Catalogs);
        double efl = ParaxialSystem.Create(configurations.LensFor(1).ToParaxialLens(), configurations.SystemFor(1)).EffectiveFocalLength!.Value;
        return OpticsConventions.FromMeters(efl, ProjectLens.LensUnitOf(optics.SystemData));
    }

    /// <summary>Thick-lens power (derived above) of the element between surfaces j and j+1, from
    /// the built lens (so the solved last curvature is the solve's), in 1/m; only its sign is used.</summary>
    private static double ElementPower(OpticsSettings optics, int j)
    {
        var lens = ProjectLens.From(optics, Catalogs)!;
        double c1 = lens.Surfaces[j].Curvature, c2 = lens.Surfaces[j + 1].Curvature, t = lens.Surfaces[j].Thickness;
        double n = optics.Surfaces![j].ModelGlass!.Nd;
        return (n - 1) * (c1 - c2 + (n - 1) * t * c1 * c2 / n);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void FromParallelPlates_TheGlobalSearchReachesTheCookeTripletForm()
    {
        // The published design as given, its F/#, then with the solve (the same design).
        var given = Triplet(PublishedRadii.Take(5).Select(r => (double?)r).ToArray(), PublishedRadii[5], fNumber: null, variable: false);
        double fNumber = EflMillimeters(given) / Epd;
        var published = Triplet(PublishedRadii.Take(5).Select(r => (double?)r).ToArray(), PublishedRadii[5], fNumber, variable: false);
        var wizard = DefaultMeritWizard.Generate(new ProjectMeritConfigurations(published, Catalogs), 1, new DefaultMeritOptions { Rings = 3 });
        var merit = new MeritFunction(wizard.Rows);
        var reference = MeritEvaluator.Evaluate(merit, new ProjectMeritConfigurations(published, Catalogs));
        Assert.False(reference.HasFailures, string.Join(Environment.NewLine, reference.Errors));
        double publishedMerit = reference.Value;
        Assert.True(ElementPower(published, 1) > 0 && ElementPower(published, 3) < 0 && ElementPower(published, 5) > 0,
            "the published design itself has the + − + pattern");

        // The deliberately poor start: parallel plates, all power in the solved last surface.
        var start = Triplet(new double?[] { null, null, null, null, null }, -100, fNumber, variable: true, merit);
        try
        {
            var local = LocalOptimizer.Run(start, Catalogs, new LocalOptimizationOptions { MaxEvaluations = 2000 });
            _out.WriteLine($"Published merit {publishedMerit:R}; local optimization alone from the plates: {local.Merit:R}.");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _out.WriteLine($"Published merit {publishedMerit:R}; local optimization alone from the plates failed: {e.Message}");
        }

        var result = GlobalSearch.Run(start, Catalogs, new GlobalSearchOptions { Seed = 1, Generations = 10, PopulationSize = 12 });
        foreach (string line in result.Log) _out.WriteLine(line);
        Assert.Equal(GlobalSearchStop.IterationLimit, result.Stop);
        Assert.NotEmpty(result.Best);
        var best = result.Best[0].Design;
        _out.WriteLine("Best front five radii: " + string.Join(", ", best.Surfaces!.Skip(1).Take(5).Select(s => s.Radius?.ToString("R") ?? "plane")));
        double p1 = ElementPower(best, 1), p2 = ElementPower(best, 3), p3 = ElementPower(best, 5);
        _out.WriteLine($"Element powers {p1:R}, {p2:R}, {p3:R}; best merit {result.Best[0].Merit:R}.");

        Assert.True(result.Best[0].Merit <= publishedMerit, $"best merit {result.Best[0].Merit:R} is above the published design's {publishedMerit:R}");
        Assert.True(p1 > 0, "the first (crown) element should converge");
        Assert.True(p2 < 0, "the middle (flint) element should diverge");
        Assert.True(p3 > 0, "the last (crown) element should converge");
    }

    private static void SameDesigns(GlobalSearchResult a, GlobalSearchResult b, string what)
    {
        Assert.Equal(a.Trials, b.Trials);
        Assert.Equal(a.FailedTrials, b.FailedTrials);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(a.Best.Count, b.Best.Count);
        for (int i = 0; i < a.Best.Count; i++)
        {
            var x = a.Best[i];
            var y = b.Best[i];
            Assert.True(x.Trial == y.Trial, $"{what}: design {i}: trial {x.Trial} against {y.Trial}");
            Assert.True(BitConverter.DoubleToInt64Bits(x.Merit) == BitConverter.DoubleToInt64Bits(y.Merit), $"{what}: design {i}: merit {x.Merit:R} against {y.Merit:R}");
            Assert.Equal(x.ConstraintsMet, y.ConstraintsMet);
            Assert.Equal(x.Glasses, y.Glasses);
            Assert.Equal(x.Values.Select(BitConverter.DoubleToInt64Bits), y.Values.Select(BitConverter.DoubleToInt64Bits));
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void AFixedSeed_ReproducesTheGlobalSearchBitwise_AtOneAndManyThreads()
    {
        GlobalSearchResult Run(int? threads) => GlobalSearch.Run(Doublet(), NoCatalogs, new GlobalSearchOptions
        {
            Seed = 7, PopulationSize = 6, Generations = 2, CurvatureLimit = 0.05, MaxDegreeOfParallelism = threads,
        });
        var one = Run(1);
        Assert.Equal(18, one.Trials);   // 6 members × (the first population + 2 generations), failed trials included
        Assert.NotEmpty(one.Best);
        SameDesigns(one, Run(4), "4 threads");
        SameDesigns(one, Run(null), "no thread limit");
        SameDesigns(one, Run(1), "1 thread again");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void AFixedSeed_ReproducesTheHammerSearchBitwise_AtOneAndManyThreads_AndItNeverGetsWorse()
    {
        // Start away from the doublet's optimum.
        GlobalSearchResult Run(int? threads) => HammerSearch.Run(Doublet(r2: -30, r3: -400), NoCatalogs, new HammerSearchOptions
        {
            Seed = 11, Rounds = 3, TrialsPerRound = 4, CurvatureLimit = 0.05, MaxDegreeOfParallelism = threads,
        });
        var one = Run(1);
        Assert.Equal(1 + 3 * 4, one.Trials);
        SameDesigns(one, Run(3), "3 threads");
        SameDesigns(one, Run(null), "no thread limit");
        // Trial 0 is the start re-optimized; the best design is never worse than it.
        var reoptimized = LocalOptimizer.Run(Doublet(r2: -30, r3: -400), NoCatalogs, new LocalOptimizationOptions { MaxEvaluations = 2000, MaxDegreeOfParallelism = 1 });
        Assert.True(one.Best[0].Merit <= reoptimized.Merit, $"Hammer best {one.Best[0].Merit:R} against the re-optimized start {reoptimized.Merit:R}");
    }

    /// <summary>
    /// The Hammer search shakes glasses through OP-59's machinery: OP-59's gate achromat (thin
    /// cemented doublet, EFFL = 100 mm and EFFL(F) − EFFL(C) = 0 as exact constraints, CVVA rows),
    /// started with PMMA on both elements (V₁ = V₂, so no power split is colour-free) and both
    /// surfaces flagged Substitute, with every trial a glass change. A design that meets the
    /// constraints must be found and ranked first, with the converging element of the larger
    /// Abbe number (Vᵢ evaluated from the lens's own media at F, d, C); Apply writes its glasses.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void TheHammerSearch_ReplacesGlasses_AndFindsAColourCorrectedPair()
    {
        double[] fdc = { SpectralLines.F.Micrometers, SpectralLines.LowerD.Micrometers, SpectralLines.C.Micrometers };
        const string pmma = "RII-ORGANIC:poly_methyl_methacrylate-Sultanova";
        OpticsSurfaceSettings Glass(double radius) => new()
        {
            Radius = radius, Thickness = 1e-3, Material = pmma,
            Flags = new() { Variable("Radius"), Variable(GlassVariables.SubstituteColumn) },
        };
        var start = new OpticsSettings
        {
            SystemData = new OpticsSystemSettings
            {
                ApertureValue = 10,
                Wavelengths = fdc.Select(w => new OpticsWavelengthSettings { Micrometers = w }).ToList(),
                PrimaryWavelength = 2,
            },
            Surfaces = new() { new() { ThicknessIsInfinite = true }, Glass(50) with { IsStop = true }, Glass(-50), new() { Thickness = 100 }, new() },
            MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(new[]
            {
                MeritOperandRow.Of("EFFL", 0, 2, target: 100, weight: -1),
                MeritOperandRow.Of("EFFL", 0, 1, target: 0, weight: 0),
                MeritOperandRow.Of("EFFL", 0, 3, target: 0, weight: 0),
                MeritOperandRow.Of("DIFF", 2, 3, target: 0, weight: -1),
                MeritOperandRow.Of("CVVA", 1, 0, target: 0, weight: 1),
                MeritOperandRow.Of("CVVA", 2, 0, target: 0, weight: 1),
            })),
        };
        var result = HammerSearch.Run(start, Catalogs, new HammerSearchOptions
        {
            Seed = 3, Rounds = 2, TrialsPerRound = 6, GlassChangeProbability = 1, CurvatureLimit = 0.05,
            Local = new LocalOptimizationOptions { MaxConstraintRounds = 15 },
        });
        foreach (string line in result.Log) _out.WriteLine(line);
        Assert.Equal(new[] { 1, 2 }, result.SubstituteSurfaces);
        Assert.NotEmpty(result.Best);
        var top = result.Best[0];
        Assert.True(top.ConstraintsMet, "no design meeting the exact constraints was found");
        Assert.Contains(top.Glasses, g => !string.Equals(g, pmma, StringComparison.OrdinalIgnoreCase));

        var applied = result.ApplyTo(start, top);
        Assert.Equal(top.Glasses, GlobalTrials.GlassesOf(applied, result.SubstituteSurfaces));
        // The cells hold radii 1/c, so reading the curvature back is exact to rounding only.
        var back = LensVariables.Values(applied, result.Variables);
        for (int i = 0; i < back.Length; i++)
            Assert.True(Math.Abs(back[i] - top.Values[i]) <= 1e-12 * Math.Abs(top.Values[i]), $"variable {i}: {back[i]:R} against {top.Values[i]:R}");

        var lens = ProjectLens.From(applied, Catalogs)!;
        var system = SystemDataSettings.FromSettings(applied.SystemData, new List<string>());
        double V(int surface)
        {
            var m = lens.Surfaces[surface].Medium;
            double f = m.IndexAt(fdc[0], system.Environment), d = m.IndexAt(fdc[1], system.Environment), c = m.IndexAt(fdc[2], system.Environment);
            return (d - 1) / (f - c);
        }
        var s = applied.Surfaces!;
        double k1 = 1 / s[1].Radius!.Value - 1 / s[2].Radius!.Value, k2 = 1 / s[2].Radius!.Value;
        double v1 = V(1), v2 = V(2);
        _out.WriteLine($"V1 {v1:R}, V2 {v2:R}; K1 {k1:R}, K2 {k2:R}");
        var (converging, diverging) = k1 > 0 ? (v1, v2) : (v2, v1);
        Assert.True(k1 * k2 < 0, "an achromat of positive power needs one converging and one diverging element");
        Assert.True(converging > diverging, $"the converging element's V {converging:R} is not above the diverging one's {diverging:R}");
    }
}
