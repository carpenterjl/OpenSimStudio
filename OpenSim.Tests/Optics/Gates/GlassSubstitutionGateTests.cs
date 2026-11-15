using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization;
using Xunit.Abstractions;

namespace OpenSim.Tests.Optics.Gates;

/// <summary>
/// OP-59's gate (plan): "An achromat started with two wrong glasses recovers a crown-flint pair
/// with the closed-form power split."
/// <para>
/// <b>The closed form, derived.</b> Two thin lenses in contact, powers φ₁ and φ₂ at the d line,
/// Abbe numbers Vᵢ = (n_d,i − 1)/(n_F,i − n_C,i). Each power is (nᵢ(λ) − 1)Kᵢ with Kᵢ the
/// curvature difference, so φ(F) − φ(C) = Σ (n_F,i − n_C,i)Kᵢ = Σ φᵢ/Vᵢ. Colour-free (equal
/// focal lengths at F and C) with total power φ: φ₁ + φ₂ = φ and φ₁/V₁ + φ₂/V₂ = 0, whence
/// φ₁ = φV₁/(V₁ − V₂) and φ₂ = −φV₂/(V₁ − V₂). The positive element has the larger Abbe number
/// (the crown) and the negative one the smaller (the flint).
/// </para>
/// <para>
/// <b>Glasses.</b> The repository's own catalogs, the refractiveindex.info seed shipped in
/// OpenSim.Optics (OP-12: RII-MAIN, RII-GLASS, RII-ORGANIC, read from the embedded YAML pages).
/// Every index used below is evaluated from those files through the lens's own medium; no
/// value is typed. The lens starts with PMMA on both elements: with V₁ = V₂ no power split is
/// colour-free, so the start cannot be an achromat.
/// </para>
/// <para>
/// <b>Lens and merit.</b> A cemented doublet, element thicknesses 10⁻³ mm (the thin-lens limit;
/// the thickness terms are of order t·φ ≈ 10⁻⁵ relative), plane back, entrance pupil 10 mm,
/// wavelengths F, d, C. Variables: the front and junction curvatures. Two exact constraints
/// (negative weight): EFFL at d = 100 mm and EFFL(F) − EFFL(C) = 0; they fix both curvatures,
/// so re-optimization finds the achromat for any pair with V₁ ≠ V₂. Two rows CVVA = 0 (weight 1)
/// rank the pairs by how strongly curved the achromat is, so the search prefers a widely
/// separated crown and flint.
/// </para>
/// </summary>
public sealed class GlassSubstitutionGateTests
{
    private readonly ITestOutputHelper _out;

    public GlassSubstitutionGateTests(ITestOutputHelper output) => _out = output;

    private const double FocalLength = 100;

    private static readonly double[] Fdc = { SpectralLines.F.Micrometers, SpectralLines.LowerD.Micrometers, SpectralLines.C.Micrometers };

    private static IReadOnlyList<OpticalCatalog> Catalogs => RefractiveIndexInfoSeed.Catalogs;

    private static string Pmma => "RII-ORGANIC:poly_methyl_methacrylate-Sultanova";

    private static OpticsCellFlagSettings Flag(string column) => new() { Column = column, Kind = "Variable" };

    private static OpticsSettings Achromat(string glass1, string glass2, bool substitute) => new()
    {
        SystemData = new OpticsSystemSettings
        {
            ApertureValue = 10,
            Wavelengths = Fdc.Select(w => new OpticsWavelengthSettings { Micrometers = w }).ToList(),
            PrimaryWavelength = 2,
        },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new()
            {
                Radius = 50, Thickness = 1e-3, Material = glass1, IsStop = true,
                Flags = substitute ? new() { Flag("Radius"), Flag(GlassVariables.SubstituteColumn) } : new() { Flag("Radius") },
            },
            new()
            {
                Radius = -50, Thickness = 1e-3, Material = glass2,
                Flags = substitute ? new() { Flag("Radius"), Flag(GlassVariables.SubstituteColumn) } : new() { Flag("Radius") },
            },
            new() { Thickness = 100 },
            new(),
        },
        MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(new[]
        {
            MeritOperandRow.Of("EFFL", 0, 2, target: FocalLength, weight: -1),
            MeritOperandRow.Of("EFFL", 0, 1, target: 0, weight: 0),
            MeritOperandRow.Of("EFFL", 0, 3, target: 0, weight: 0),
            MeritOperandRow.Of("DIFF", 2, 3, target: 0, weight: -1),
            MeritOperandRow.Of("CVVA", 1, 0, target: 0, weight: 1),
            MeritOperandRow.Of("CVVA", 2, 0, target: 0, weight: 1),
        })),
    };

    /// <summary>The indices at F, d and C of the medium after <paramref name="surface"/>, from the lens.</summary>
    private static (double F, double D, double C) Indices(OpticsSettings optics, int surface)
    {
        var lens = ProjectLens.From(optics, Catalogs)!;
        var system = SystemDataSettings.FromSettings(optics.SystemData, new List<string>());
        var medium = lens.Surfaces[surface].Medium;
        return (medium.IndexAt(Fdc[0], system.Environment), medium.IndexAt(Fdc[1], system.Environment), medium.IndexAt(Fdc[2], system.Environment));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void AnAchromatStartedWithTwoWrongGlasses_RecoversACrownFlintPair_WithTheClosedFormPowerSplit()
    {
        var start = Achromat(Pmma, Pmma, substitute: true);
        var result = GlassSubstitution.Run(start, Catalogs, new GlassSubstitutionOptions
        {
            MaxRounds = 3,
            // The start (V₁ = V₂) cannot meet the colour constraint; fewer multiplier rounds bound
            // the time spent showing that. A pair that can meet it does so in a few rounds.
            Optimization = new LocalOptimizationOptions { MaxConstraintRounds = 15 },
        });
        foreach (var line in result.Log) _out.WriteLine(line);
        foreach (var g in result.Glasses) _out.WriteLine($"surface {g.Surface}: {g.Before} → {g.After}");

        Assert.False(result.Cancelled);
        Assert.True(result.Merit < result.ReoptimizedStartMerit, $"merit {result.Merit:R} against the start's {result.ReoptimizedStartMerit:R}");
        Assert.NotEqual(result.Glasses[0].After, result.Glasses[1].After);

        var best = result.Best;
        var (f1, d1, c1) = Indices(best, 1);
        var (f2, d2, c2) = Indices(best, 2);
        double v1 = (d1 - 1) / (f1 - c1), v2 = (d2 - 1) / (f2 - c2);
        var s = best.Surfaces!;
        double k1 = 1 / s[1].Radius!.Value - 1 / s[2].Radius!.Value;   // element 1: front minus junction curvature
        double k2 = 1 / s[2].Radius!.Value - 0;                          // element 2: junction minus the plane back
        double phi1 = (d1 - 1) * k1, phi2 = (d2 - 1) * k2;
        double phi = 1 / FocalLength;
        double expected1 = phi * v1 / (v1 - v2), expected2 = -phi * v2 / (v1 - v2);
        _out.WriteLine($"V1 {v1:R}, V2 {v2:R}; φ1 {phi1:R} (closed form {expected1:R}), φ2 {phi2:R} (closed form {expected2:R})");

        // A crown–flint pair: the converging element has the larger Abbe number.
        Assert.True(Math.Abs(v1 - v2) > 0);
        var (crownV, flintV) = phi1 > 0 ? (v1, v2) : (v2, v1);
        Assert.True(crownV > flintV, $"the converging element's V {crownV:R} is not above the diverging one's {flintV:R}");
        Assert.True(phi1 * phi2 < 0, "an achromat of positive power needs one positive and one negative element");

        // The power split: thin-lens closed form; the elements are 1e-3 mm thick, so the thick-lens
        // terms (n − 1)²t·c₁c₂/n are below 1e-5 of φ for curvatures under 0.1/mm. 1e-4 relative of φ.
        Assert.True(Math.Abs(phi1 - expected1) <= 1e-4 * phi, $"φ1 {phi1:R} against {expected1:R}");
        Assert.True(Math.Abs(phi2 - expected2) <= 1e-4 * phi, $"φ2 {phi2:R} against {expected2:R}");
    }

    /// <summary>
    /// The model-glass route: element 1 a fixed model glass, element 2 a model glass whose Vd is
    /// the one variable, curvatures fixed. The colour-free condition φ₁/V₁ + φ₂/V₂ = 0 then fixes
    /// V₂ = −φ₂V₁/φ₁ (derived above), with φᵢ = (n_d,i − 1)Kᵢ from the model glasses' own nd. A model
    /// glass reproduces its nd and Vd exactly (ModelGlassFit's constraints), so the optimized Vd
    /// must be that value to the thin-lens order (1e-4 relative). The fit is trained on the
    /// repository's seed catalogs.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public void AModelGlassAbbeNumber_OptimizesToTheColourFreeValue()
    {
        var optics = Achromat("x", "x", substitute: false);
        var surfaces = optics.Surfaces!.ToList();
        surfaces[1] = surfaces[1] with { Material = null, ModelGlass = new OpticsModelGlassSettings { Nd = 1.5, Vd = 60 }, Flags = null };
        surfaces[2] = surfaces[2] with
        {
            Material = null, ModelGlass = new OpticsModelGlassSettings { Nd = 1.6, Vd = 50 },
            Flags = new() { Flag(GlassVariables.ModelVdColumn) },
        };
        surfaces[3] = surfaces[3] with { Radius = -1000 };
        optics = optics with
        {
            Surfaces = surfaces,
            MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(new[]
            {
                MeritOperandRow.Of("EFFL", 0, 1, target: 0, weight: 0),
                MeritOperandRow.Of("EFFL", 0, 3, target: 0, weight: 0),
                MeritOperandRow.Of("DIFF", 1, 2, target: 0, weight: 1),
            })),
        };
        var result = LocalOptimizer.Run(optics, Catalogs);
        double k1 = 1 / 50.0 + 1 / 50.0, k2 = -1 / 50.0 + 1 / 1000.0;
        double phi1 = (1.5 - 1) * k1, phi2 = (1.6 - 1) * k2;
        double expected = -phi2 * 60 / phi1;
        _out.WriteLine($"Vd {result.Values[0]:R}, closed form {expected:R}, merit {result.Merit:R}");
        Assert.True(Math.Abs(result.Values[0] - expected) <= 1e-4 * expected, $"Vd {result.Values[0]:R} against {expected:R}");
    }
}
