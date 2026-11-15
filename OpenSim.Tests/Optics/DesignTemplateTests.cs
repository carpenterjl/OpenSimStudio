using OpenSim.Core.Model;
using OpenSim.Optics;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Catalogs;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Paraxial;
using OpenSim.Optics.Sequential.LensEditing;

namespace OpenSim.Tests.Optics;

/// <summary>
/// OP-16: design templates (docs/Optics-Conventions.md section 53). Each template is this
/// program's own closed-form prescription; the checks run the paraxial engine (section 11) on the
/// built design and compare with the inputs the template promises to meet. Glasses are the
/// built-in refractiveindex.info catalogs' (CC0, <see cref="GlassCatalogLibrary.BuiltInCatalogs"/>).
/// </summary>
public sealed class DesignTemplateTests
{
    private static readonly IReadOnlyList<OpticalCatalog> Glass = GlassCatalogLibrary.BuiltInCatalogs;

    private static ParaxialSystem Paraxial(OpticsSettings optics, int? wavelength = null)
    {
        var lens = ProjectLens.From(optics, Glass)!;
        return ParaxialSystem.Create(lens.ToParaxialLens(), SystemDataSettings.FromSettings(optics.SystemData, new List<string>()), wavelength);
    }

    private static void Close(double expected, double? actual, double relative, string what)
    {
        Assert.True(actual.HasValue, $"{what}: no value");
        Assert.True(Math.Abs(actual!.Value - expected) <= relative * Math.Abs(expected),
            FormattableString.Invariant($"{what}: expected {expected:R}, got {actual:R}"));
    }

    /// <summary>The singlets meet the focal length asked for, positive and negative, with a
    /// catalog glass and with a constant index; the solve puts the image at the paraxial focus
    /// (BFL equals the saved gap). 1e-12: closed forms against the engine, a few roundings.</summary>
    [Theory]
    [InlineData("plano-singlet", "100", "SiO2-Malitson")]
    [InlineData("plano-singlet", "-80", "1.6")]
    [InlineData("equi-singlet", "100", "SiO2-Malitson")]
    [InlineData("equi-singlet", "-60", "1.7")]
    [InlineData("equi-singlet", "25", "1.5")]
    public void TheSinglets_MeetTheirFocalLength(string id, string focal, string glass)
    {
        var t = DesignTemplates.Find(id)!;
        var built = t.Build(new Dictionary<string, string> { ["Focal length"] = focal, ["Glass"] = glass, ["Thickness"] = "5" }, Glass).Optics;
        var p = Paraxial(built);
        double f = double.Parse(focal, System.Globalization.CultureInfo.InvariantCulture);
        Close(f / 1000, p.EffectiveFocalLength, 1e-12, $"{id} EFL");
        Close(p.BackFocalLength!.Value * 1000, built.Surfaces![^2].Thickness, 1e-12, $"{id} image gap");
        Assert.Equal("Marginal ray height", built.Surfaces![^2].Flags!.Single().Solve);
        Assert.Equal(1, StockLensProperties.ElementCount(built.Surfaces!));
        Assert.Equal(id == "plano-singlet" ? StockLensShape.Plano : StockLensShape.Equi, StockLensProperties.Shape(built.Surfaces!));
    }

    /// <summary>
    /// The achromat meets its focal length after Make Focal (1e-10: a uniform scale is exact to
    /// rounding, OP-51's Make Focal), and in the thin limit (both thicknesses
    /// 1e-6 mm) its F- and C-line focal lengths agree — the thin-lens condition φ₁/V₁ + φ₂/V₂ = 0
    /// makes φ_F − φ_C = Σ (n_F − n_C)K = Σ φᵢ/Vᵢ = 0 exactly, so what is left is the thickness
    /// term, of order t/f = 1e-8; 1e-6 relative is asked.
    /// </summary>
    [Fact]
    public void TheAchromat_MeetsItsFocalLength_AndIsColourFreeInTheThinLimit()
    {
        var t = DesignTemplates.Find("cemented-achromat")!;
        var built = t.Build(null, Glass).Optics;
        Close(0.1, Paraxial(built).EffectiveFocalLength, 1e-10, "achromat EFL");
        Assert.Equal("(?,S,2)", $"({StockLensCodes.Shape(StockLensProperties.Shape(built.Surfaces!))},{StockLensCodes.Type(StockLensProperties.Type(built.Surfaces!))},{StockLensProperties.ElementCount(built.Surfaces!)})");

        var thin = t.Build(new Dictionary<string, string> { ["Crown thickness"] = "1e-6", ["Flint thickness"] = "1e-6" }, Glass).Optics;
        double fF = Paraxial(thin, 0).EffectiveFocalLength!.Value, fC = Paraxial(thin, 2).EffectiveFocalLength!.Value;
        Assert.True(Math.Abs(fF - fC) <= 1e-6 * 0.1, FormattableString.Invariant($"F {fF:R} m, C {fC:R} m"));

        // A singlet of the crown glass has a colour spread many times larger (sanity check of the measure).
        var singlet = DesignTemplates.Find("equi-singlet")!.Build(new Dictionary<string, string> { ["Glass"] = "poly_methyl_methacrylate-Sultanova", ["Thickness"] = "1e-6" }, Glass).Optics;
        double sF = Paraxial(singlet, 0).EffectiveFocalLength!.Value, sC = Paraxial(singlet, 2).EffectiveFocalLength!.Value;
        Assert.True(Math.Abs(sF - sC) > 1000 * Math.Abs(fF - fC));
    }

    /// <summary>The paraboloid's image is at −f after the mirror (section 3) and its focal length
    /// has magnitude f (derived: radius −2f).</summary>
    [Fact]
    public void TheParabolicMirror_FocusesAtItsFocalLength()
    {
        var built = DesignTemplates.Find("parabolic-mirror")!.Build(new Dictionary<string, string> { ["Focal length"] = "500" }, Glass).Optics;
        Close(-500, built.Surfaces![1].Thickness, 1e-12, "image gap");
        Close(0.5, Math.Abs(Paraxial(built).EffectiveFocalLength!.Value), 1e-12, "|EFL|");
        Assert.Equal(-1, built.Surfaces![1].Conic);
    }

    /// <summary>Both telescopes are afocal with angular magnification −f₁/f₂ (derived).</summary>
    [Theory]
    [InlineData("keplerian-telescope", 200, 25)]
    [InlineData("galilean-telescope", 200, -50)]
    public void TheTelescopes_AreAfocal_WithMagnificationMinusF1OverF2(string id, double f1, double f2)
    {
        var built = DesignTemplates.Find(id)!.Build(new Dictionary<string, string>
        {
            ["Objective focal length"] = f1.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Eyepiece focal length"] = f2.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, Glass).Optics;
        var p = Paraxial(built);
        Assert.True(p.IsAfocal);
        Close(-f1 / f2, p.AngularMagnification, 1e-12, $"{id} magnification");
    }

    [Fact]
    public void BadInputs_FailNamingTheTemplateAndTheInput()
    {
        var t = DesignTemplates.Find("plano-singlet")!;
        Assert.Contains("'Thickness'", Assert.Throws<ArgumentException>(() => t.Build(new Dictionary<string, string> { ["Thickness"] = "abc" }, Glass)).Message);
        Assert.Contains("NO-SUCH-GLASS", Assert.Throws<ArgumentException>(() => t.Build(new Dictionary<string, string> { ["Glass"] = "NO-SUCH-GLASS" }, Glass)).Message);
        Assert.Contains("no input 'Colour'", Assert.Throws<ArgumentException>(() => t.Build(new Dictionary<string, string> { ["Colour"] = "1" }, Glass)).Message);
        Assert.Contains("glass name", Assert.Throws<ArgumentException>(() => DesignTemplates.Find("cemented-achromat")!.Build(new Dictionary<string, string> { ["Crown glass"] = "1.5" }, Glass)).Message);
        Assert.Throws<ArgumentException>(() => DesignTemplates.Find("galilean-telescope")!.Build(new Dictionary<string, string> { ["Eyepiece focal length"] = "50" }, Glass));
        // An equi-convex lens with t/n beyond the focal length does not exist (the root's argument 1 − tφ/n ≤ 0).
        Assert.Throws<ArgumentException>(() => DesignTemplates.Find("equi-singlet")!.Build(new Dictionary<string, string> { ["Focal length"] = "2", ["Thickness"] = "10", ["Glass"] = "1.5" }, Glass));
    }

    [Fact]
    public void TheList_FiltersByTextElementsAndKind()
    {
        Assert.Equal(6, DesignTemplates.All.Count);
        Assert.Equal(new[] { "keplerian-telescope", "galilean-telescope" }, DesignTemplates.Filter("telescope").Select(t => t.Id));
        Assert.Equal(new[] { "cemented-achromat", "keplerian-telescope", "galilean-telescope" }, DesignTemplates.Filter(null, elements: 2).Select(t => t.Id));
        Assert.Equal(new[] { "parabolic-mirror" }, DesignTemplates.Filter(null, kind: "Mirror").Select(t => t.Id));
    }
}
