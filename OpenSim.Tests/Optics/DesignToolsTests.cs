using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Catalogs;
using OpenSim.Optics.DesignTools;
using OpenSim.Optics.Interop.Zemax;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Sequential;
using OpenSim.Optics.Sequential.Aspheres;
using OpenSim.Optics.Sequential.LensEditing;
using Xunit.Abstractions;
using F = OpenSim.Tests.Optics.LocalOptimizationFixtures;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-61: the design conversion tools (docs/Optics-Conventions.md §59): Convert Asphere Types,
/// Find Best Asphere and Stock Lens Matching. Every lens is in millimeters with constant indices
/// (relative to air), so no glass catalog is read; radii, thicknesses, indices and asphere
/// coefficients are arbitrary inputs. Expected values are derived in the tests (stated beside
/// each); none is a reference value from a source. OP-23's library-level conversion gate is
/// <see cref="AsphereTests"/> (<c>EvenToQconAndBack_KeepsTheSag</c>); these tests are the
/// tool-level ones, on a saved lens.
/// </summary>
public sealed class DesignToolsTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "op61-" + Guid.NewGuid().ToString("N"));
    private static readonly IReadOnlyList<OpticalCatalog> NoGlass = Array.Empty<OpticalCatalog>();

    public DesignToolsTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static OpticsSurfaceSettings S(double? radius, double thickness, double? index = null) =>
        new() { Radius = radius, Thickness = thickness, Index = index };

    private static List<OpticsSurfaceParameterSettings> Params(params double[] values) =>
        values.Select((v, i) => new OpticsSurfaceParameterSettings { Number = i + 1, Value = v }).Where(p => p.Value != 0).ToList();

    /// <summary>A lens (EPD 20 mm) whose surface 1 is an Even Asphere with the given α (mm units),
    /// semi-diameter fixed at 10 mm.</summary>
    private static OpticsSettings AsphereLens(double radius, double conic, params double[] alpha) =>
        LensCatalogTests.Lens("asphere", 20, new[]
        {
            new OpticsSurfaceSettings { Type = "Even Asphere", Radius = radius, Conic = conic, Thickness = 5, Index = 1.5, SemiDiameter = 10, Parameters = Params(alpha) },
            new OpticsSurfaceSettings { Thickness = 50, SemiDiameter = 10 },
        });

    private static SequentialSurface Surface(OpticsSettings optics, int j) =>
        new LensEditContext(optics.SystemData, NoGlass).ToLens(optics.Surfaces!).Surfaces[j];

    // ------------------------------------------------------------------ Convert Asphere Types

    /// <summary>
    /// Gate: converting an Even Asphere to the Q-type keeps the sag within the fit tolerance. The
    /// departure α2r⁴ + α3r⁶ + α4r⁸ is u⁴ times a polynomial of order 2 in u² (u = r/ρ), which three
    /// Q^con terms represent exactly (as in OP-23's gate), so the automatic count is 3 (the rule:
    /// order 8 → 8/2 − 1) and the fit is exact to rounding; the radius and conic are kept because
    /// the fixed fit already has no residual. The sag of the saved, converted surface (read back
    /// through the lens builder) equals the original's at 201 radii to 1e-12 of the 10 mm aperture.
    /// </summary>
    [Fact]
    public void EvenAsphere_ToQcon_OnTheSavedLens_KeepsTheSag()
    {
        var lens = AsphereLens(40, -1.2, 0, 1e-6, -2e-9, 3e-12);
        var result = AsphereTypeConversion.Run(lens, NoGlass, new AsphereTypeConversionOptions { Surface = 1, Target = AsphereTarget.QTypeQcon });
        foreach (var line in result.ReportLines()) _out.WriteLine(line);
        var saved = result.Settings.Surfaces![1];
        Assert.Equal("Q-Type Asphere", saved.Type);
        Assert.Equal(3, result.Terms);
        Assert.Equal(40, saved.Radius);
        Assert.Equal(-1.2, saved.Conic);
        Assert.Equal(10, result.ApertureRadius);
        Assert.True(result.RmsResidual <= 1e-12 * 10, $"RMS residual {result.RmsResidual:R} mm");

        var before = Surface(lens, 1);
        var after = Surface(result.Settings, 1);
        Assert.IsType<QTypeAsphere>(after.Shape);
        double rho = 0.01, tol = 1e-12 * rho;
        for (int i = 0; i <= 200; i++)
        {
            double r = rho * i / 200;
            double d = AsphereConversion.RadialSag(after, r) - AsphereConversion.RadialSag(before, r);
            Assert.True(Math.Abs(d) <= tol, $"sag difference {d:R} m at r = {r} m");
        }
    }

    /// <summary>
    /// The help's rule "if the radius or conic … are non-zero or variable, then these parameters
    /// are also made variable in the fitting process": an Even Asphere with an r² term (α1), which
    /// a Q^con departure (starting at u⁴) cannot hold. With the curvature fixed (OP-23's fit) the
    /// residual stays (OP-23's <c>EvenWithAnR2Term_ToQcon_ReportsTheResidual</c>); with it fitted,
    /// the new vertex curvature takes the r² term: c′ = c + 2α1 (derived: the r² coefficient of
    /// the sphere is c/2), and the remaining difference of the two spheres is a series in r⁴ …
    /// that eight terms hold to r¹⁸ (remainder of order (c′ρ)¹⁹ρ ≈ 1e-11 mm). So the residual falls
    /// by more than a million and the radius is 1/(c + 2α1) to 1e-6.
    /// </summary>
    [Fact]
    public void AnR2Term_IsTakenByTheRadius_WhenTheRadiusIsFitted()
    {
        double c = 1.0 / 40, alpha1 = 5e-4;
        var lens = AsphereLens(40, 0, alpha1, 5e-8);
        var fixedFit = AsphereTypeConversion.Run(lens, NoGlass, new AsphereTypeConversionOptions
        {
            Surface = 1, Target = AsphereTarget.QTypeQcon, Terms = 8, VaryRadiusAndConic = false,
        });
        var fitted = AsphereTypeConversion.Run(lens, NoGlass, new AsphereTypeConversionOptions { Surface = 1, Target = AsphereTarget.QTypeQcon, Terms = 8 });
        _out.WriteLine($"fixed RMS {fixedFit.RmsResidual:R}, fitted RMS {fitted.RmsResidual:R}, radius {fitted.RadiusAfter:R}");
        Assert.Equal(40, fixedFit.RadiusAfter);
        Assert.True(fitted.RmsResidual < 1e-6 * fixedFit.RmsResidual, $"fitted {fitted.RmsResidual:R} vs fixed {fixedFit.RmsResidual:R}");
        double expected = 1 / (c + 2 * alpha1);
        Assert.True(Math.Abs(fitted.RadiusAfter!.Value - expected) <= 1e-6 * expected, $"radius {fitted.RadiusAfter:R}, expected {expected:R}");
        Assert.Equal(0, fitted.ConicAfter);   // zero and not variable: not fitted
    }

    /// <summary>The Extended Asphere target (added to OP-23's fit for this tool): an Even Asphere
    /// of order 8 is an Extended Asphere of four terms on ρ = r/10 mm with A_i = α_i·10^(2i)
    /// (derived), reproduced to rounding.</summary>
    [Fact]
    public void EvenAsphere_ToExtended_ScalesTheCoefficientsByTheNormalizationRadius()
    {
        var lens = AsphereLens(40, -0.5, 0, 1e-6, -2e-9, 3e-12);
        var result = AsphereTypeConversion.Run(lens, NoGlass, new AsphereTypeConversionOptions { Surface = 1, Target = AsphereTarget.ExtendedAsphere });
        Assert.Equal(4, result.Terms);
        var saved = result.Settings.Surfaces![1];
        Assert.Equal("Extended Asphere", saved.Type);
        double[] alpha = { 0, 1e-6, -2e-9, 3e-12 };
        for (int i = 1; i <= 4; i++)
        {
            double expected = alpha[i - 1] * Math.Pow(10, 2 * i);
            double got = SavedSurfaces.Value(saved, 14 + i);
            Assert.True(Math.Abs(got - expected) <= 1e-12 * 1e-2 + 1e-9 * Math.Abs(expected), $"A{i}: {got:R}, expected {expected:R}");
        }
        Assert.Equal(10, SavedSurfaces.Value(saved, 14), 12);
    }

    [Fact]
    public void Conversion_RefusesTheObjectAndATooLargeTermCount()
    {
        var lens = AsphereLens(40, 0, 0, 1e-6);
        Assert.Throws<ArgumentException>(() => AsphereTypeConversion.Run(lens, NoGlass, new AsphereTypeConversionOptions { Surface = 0 }));
        var e = Assert.Throws<ArgumentException>(() => AsphereTypeConversion.Run(lens, NoGlass,
            new AsphereTypeConversionOptions { Surface = 1, Target = AsphereTarget.EvenAsphere, Terms = 9 }));
        Assert.Contains("1 to 8", e.Message);
    }

    // ------------------------------------------------------------------ Find Best Asphere

    /// <summary>
    /// A fast singlet (F/5: entrance pupil 20 mm, focal length held at 100 mm by an F/# solve on
    /// the back curvature), index 1.5, 4 mm thick, the front radius variable, the back distance a
    /// marginal-ray-height solve (paraxial focus). The merit function is the on-axis transverse
    /// aberration REAY at five pupil zones, target 0. Bending alone cannot remove the spherical
    /// aberration of a singlet, so the re-optimized sphere keeps a merit; a conic or an even
    /// asphere on either surface removes the third-order part.
    /// </summary>
    private static OpticsSettings FastSinglet() => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 20 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 60, Thickness = 4, Index = 1.5, IsStop = true, Flags = new() { F.Variable("Radius") } },
            new() { Radius = -300, Thickness = 95, Flags = new() { F.Solve("Radius", "F/#", 5), F.Solve("Thickness", "Marginal ray height", 0, 0) } },
            new(),
        },
        MeritFunction = MeritFunctionSettings.ToSettings(F.MarginalRayMerit(imageSurface: 3, 1.0, 0.85, 0.7, 0.5, 0.3)),
    };

    /// <summary>
    /// Gate: on a singlet with spherical aberration, Find Best Asphere tries both surfaces (the
    /// variable front and the F/#-solved back are both candidates by the help's rules), each
    /// improves on the re-optimized sphere, and the best lowers the merit at least fivefold (the
    /// conic removes the third-order aberration; what is left is fifth order, small at F/5).
    /// </summary>
    [Theory]
    [InlineData(BestAsphereKind.Conic)]
    [InlineData(BestAsphereKind.EvenAsphere)]
    public void FindBestAsphere_OnASphericalSinglet_RanksASurfaceAndImprovesTheMerit(BestAsphereKind kind)
    {
        var result = FindBestAsphere.Run(FastSinglet(), F.NoCatalogs, new FindBestAsphereOptions { Kind = kind, MaxOrder = 6 });
        foreach (var line in result.ReportLines()) _out.WriteLine(line);
        Assert.Equal(new[] { 1, 2 }, result.Trials.Select(t => t.Surface));
        Assert.All(result.Trials, t => Assert.True(t.Merit < result.ReoptimizedStartMerit, $"surface {t.Surface}: {t.Merit} vs {result.ReoptimizedStartMerit}"));
        Assert.NotNull(result.BestSurface);
        Assert.True(result.BestMerit < 0.2 * result.ReoptimizedStartMerit, $"best {result.BestMerit:R}, sphere {result.ReoptimizedStartMerit:R}");
        Assert.Equal(result.Trials.Min(t => t.Merit!.Value), result.BestMerit);
        var best = result.Best.Surfaces![result.BestSurface!.Value];
        if (kind == BestAsphereKind.Conic) Assert.NotEqual(0, best.Conic);
        else
        {
            Assert.Equal("Even Asphere", best.Type);
            Assert.NotEqual(0, SavedSurfaces.Value(best, 2));
            Assert.Contains(best.Flags!, f => f.Column == "Par2" && f.Kind == SavedSurfaces.VariableFlag);
            Assert.Contains(best.Flags!, f => f.Column == "Par3" && f.Kind == SavedSurfaces.VariableFlag);
        }
    }

    /// <summary>The help's candidate rules on OP-57's cemented doublet: surface 1's radius is fixed,
    /// surface 2 is cemented (glass on both sides), surface 3 is variable with air after it; a conic
    /// or a CRVT row takes surface 3 out.</summary>
    [Fact]
    public void Candidates_FollowTheHelpsRules()
    {
        var doublet = F.Doublet();
        Assert.Contains("neither variable", FindBestAsphere.WhyNotCandidate(doublet, 1));
        Assert.Contains("cemented", FindBestAsphere.WhyNotCandidate(doublet, 2));
        Assert.Null(FindBestAsphere.WhyNotCandidate(doublet, 3));
        var conic = doublet with { Surfaces = doublet.Surfaces!.Select((s, j) => j == 3 ? s with { Conic = -1 } : s).ToList() };
        Assert.Contains("conic", FindBestAsphere.WhyNotCandidate(conic, 3));
        var mce = doublet with
        {
            MultiConfiguration = new OpticsMultiConfigSettings
            {
                ConfigurationCount = 1,
                Operands = new() { new OpticsConfigOperandSettings { Type = "CRVT", Number1 = 3, Values = new() { new() { Value = -0.005 } } } },
            },
        };
        Assert.Contains("Multi-Configuration", FindBestAsphere.WhyNotCandidate(mce, 3));
    }

    // ------------------------------------------------------------------ Stock Lens Matching

    /// <summary>
    /// The vendor folder: two plano-convex and two equi-convex singlets, index 1.5, entrance pupil
    /// 12 mm, written as .zmx with OP-94's writer as OP-16's tests do. Their focal lengths (the
    /// paraxial engine's) are 100 and 120 mm (R1/(n − 1)) and about 80.8 and 90.9 mm (the thick-lens
    /// formula); the names are labels.
    /// </summary>
    private LensCatalog Vendor()
    {
        string dir = Path.Combine(_root, "ACME");
        Directory.CreateDirectory(dir);
        void Write(string name, double r1, double? r2, double t) =>
            ZemaxLensFiles.Export(LensCatalogTests.Lens(name, 12, new[] { S(r1, t, 1.5), S(r2, 90) }), Path.Combine(dir, name + ".zmx"));
        Write("PCX-100", 50, null, 4);
        Write("PCX-120", 60, null, 4);
        Write("BCX-80", 80, -80, 5);
        Write("BCX-90", 90, -90, 5);
        var catalog = LensCatalog.FromFolder(dir, NoGlass);
        Assert.Empty(catalog.Problems);
        return catalog;
    }

    /// <summary>Two air-spaced singlets, entrance pupil 12 mm, semi-diameters fixed at 5.9 mm (so
    /// each component is 11.8 mm across and the 12 mm stock lenses are within the EPD tolerance),
    /// the air space 20 mm, the back distance a marginal-ray-height solve.</summary>
    private static OpticsSettings TwoSinglets(double r1, double? r2, double t1, double r3, double r4, double t3, OpticsMeritFunctionSettings? merit = null)
    {
        var lens = LensCatalogTests.Lens("design", 12, new[]
        {
            new OpticsSurfaceSettings { Radius = r1, Thickness = t1, Index = 1.5, SemiDiameter = 5.9 },
            new OpticsSurfaceSettings { Radius = r2, Thickness = 20, SemiDiameter = 5.9, Flags = new() { F.Variable("Thickness") } },
            new OpticsSurfaceSettings { Radius = r3, Thickness = t3, Index = 1.5, SemiDiameter = 5.9 },
            new OpticsSurfaceSettings { Radius = r4, Thickness = 80, SemiDiameter = 5.9, Flags = new() { F.Solve("Thickness", "Marginal ray height", 0, 0) } },
        });
        return lens with { MeritFunction = merit };
    }

    /// <summary>
    /// Gate: matching a design built from catalog lenses finds those lenses. The design is built
    /// from PCX-100 and BCX-80, then its elements are perturbed to nearby non-catalog values
    /// (radii 52/−3000 and 77/−84, thicknesses 4.3 and 5.4). The merit function holds the focal
    /// length and the transverse aberration at two pupil zones at the values of the catalog-built
    /// design (evaluated here, so derived), so that design's merit is zero and no other is. Each
    /// component lists its catalog lens among its matches, and the best combination is exactly
    /// PCX-100 and BCX-80, unreversed, with a merit that is zero to rounding (the lens file's
    /// radii come back through CURV = 1/R).
    /// </summary>
    [Fact]
    public void Matching_ADesignBuiltFromCatalogLenses_FindsThoseLenses()
    {
        var vendor = Vendor();
        var catalogBuilt = TwoSinglets(50, null, 4, 80, -80, 5);
        var registry = MeritOperandRegistry.CreateDefault();
        var probe = new MeritFunction(new[]
        {
            MeritOperandRow.Of("EFFL", 0, 1, target: 0, weight: 1),
            MeritOperandRow.Of("REAY", 5, 1, 0, 1, 0, 0, 0, 1.0),
            MeritOperandRow.Of("REAY", 5, 1, 0, 1, 0, 0, 0, 0.7),
        });
        var values = MeritEvaluator.Evaluate(probe, new ProjectMeritConfigurations(catalogBuilt, NoGlass), registry, new MeritEvaluationOptions()).Rows
            .Select(r => r.Value).ToArray();
        var merit = MeritFunctionSettings.ToSettings(new MeritFunction(new[]
        {
            MeritOperandRow.Of("EFFL", 0, 1, target: values[0], weight: 1),
            MeritOperandRow.Of("REAY", 5, 1, values[1], 1, 0, 0, 0, 1.0),
            MeritOperandRow.Of("REAY", 5, 1, values[2], 1, 0, 0, 0, 0.7),
        }));
        var perturbed = TwoSinglets(52, -3000, 4.3, 77, -84, 5.4, merit);

        var result = StockLensMatching.Run(perturbed, new[] { vendor }, NoGlass);
        foreach (var line in result.ReportLines()) _out.WriteLine(line);
        Assert.True(result.NominalMerit > 0);
        Assert.Equal(2, result.Components.Count);
        Assert.Equal((1, 2), (result.Components[0].First, result.Components[0].Last));
        Assert.Equal((3, 4), (result.Components[1].First, result.Components[1].Last));
        Assert.Contains(result.Matches[1], m => m.Lens.PartName == "PCX-100" && !m.Reversed);
        Assert.Contains(result.Matches[2], m => m.Lens.PartName == "BCX-80" && !m.Reversed);
        Assert.Empty(result.Failed);

        var bestChoice = Assert.IsType<StockLensCombination>(result.BestChoice);
        Assert.Equal(new[] { "PCX-100", "BCX-80" }, bestChoice.Lenses.Select(m => m.Lens.PartName));
        Assert.All(bestChoice.Lenses, m => Assert.False(m.Reversed));
        Assert.True(bestChoice.Merit <= 1e-9 * result.NominalMerit, $"best combination merit {bestChoice.Merit:R}, nominal {result.NominalMerit:R}");

        var best = result.Best!.Surfaces!;
        Assert.Equal(50, best[1].Radius!.Value, 9);
        Assert.True(SavedSurfaces.IsPlane(best[2]));
        Assert.Equal(4, best[1].Thickness, 12);
        Assert.Equal(20, best[2].Thickness);   // the design's air space is kept, with its flag
        Assert.Contains(best[2].Flags!, f => f.Column == "Thickness" && f.Kind == SavedSurfaces.VariableFlag);
        Assert.True(best[1].IsStop);
        Assert.Equal(80, best[3].Radius!.Value, 9);
        Assert.Equal(-80, best[4].Radius!.Value, 9);
        Assert.Equal("PCX-100 (ACME)", best[1].Comment);
    }

    /// <summary>The help's shape factor S = (C_front + C_back)/(C_front − C_back): 1 for a
    /// plano-convex lens with its curved side first, 0 for an equi-convex one, −1 turned round.</summary>
    [Fact]
    public void ShapeFactor_IsTheHelpsFormula()
    {
        var s = new List<OpticsSurfaceSettings> { new(), S(50, 4, 1.5), S(null, 10), S(80, 5, 1.5), S(-80, 10), S(null, 4, 1.5), S(-50, 1), new() };
        Assert.Equal(1, StockLensMatching.ShapeFactor(s, 1, 2));
        Assert.Equal(0, StockLensMatching.ShapeFactor(s, 3, 4));
        Assert.Equal(-1, StockLensMatching.ShapeFactor(s, 5, 6));
    }
}
