using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;

namespace OpenSim.Tests.Optics;

/// <summary>OP-15: the twelve coating plots as analyses of the OP-9 framework.</summary>
public sealed class CoatingAnalysisTests
{
    private static OpticsSettings SystemWithQuarterWave() => new()
    {
        Coatings = new OpticsCoatingsSettings
        {
            Materials = new() { new OpticsCoatingMaterialSettings { Name = "LOW", Rows = new() { new OpticsIndexRowSettings { Micrometers = 0.55, N = 1.38 } } } },
            Coatings = new() { new OpticsCoatingSettings { Name = "QW", Layers = new() { new OpticsCoatingLayerSettings { Material = "LOW", Thickness = 0.25 } } } }
        },
        SystemData = new OpticsSystemSettings
        {
            Wavelengths = new() { new OpticsWavelengthSettings { Micrometers = 0.45, Weight = 1 }, new OpticsWavelengthSettings { Micrometers = 0.55, Weight = 1 }, new OpticsWavelengthSettings { Micrometers = 0.65, Weight = 1 } },
            PrimaryWavelength = 2
        }
    };

    [Fact]
    public void TheRegistry_OffersTwelveCoatingPlots_WithUniqueIds()
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        var coatings = registry.All.Where(a => a.Category == "Coatings").ToArray();
        Assert.Equal(12, coatings.Length);
        Assert.Equal(12, coatings.Select(a => a.Id).Distinct().Count());
        Assert.True(registry.TryGet("CoatingRetardanceVsWavelength", out var retardance));
        Assert.Equal("Retardance vs wavelength", retardance.Name);
        foreach (var analysis in coatings)
        {
            var result = analysis.Compute(new OpticsAnalysisContext("P", SystemWithQuarterWave()), analysis.CreateDefaultSettings(), default);
            var plot = Assert.IsType<LinePlot>(Assert.Single(result.Plots));
            Assert.NotEmpty(plot.Curves);
            Assert.All(plot.Curves, c => Assert.All(c.Y, y => Assert.True(double.IsFinite(y))));
            Assert.Contains(result.Assumptions, a => a.Contains("not yet applied to traced rays"));
        }
    }

    /// <summary>Reflection against angle of an uncoated surface starts at the normal-incidence
    /// Fresnel value and the p curve dips to zero at Brewster's angle (both computed here).</summary>
    [Fact]
    public void ReflectionVsAngle_OfABareSurface_ShowsFresnelAndBrewster()
    {
        var analysis = new CoatingVsAngleAnalysis(CoatingQuantity.Reflection);
        double brewster = Math.Atan(1.52) * 180 / Math.PI;
        var settings = new CoatingVsAngleSettings { Substrate = "1.52", MinimumAngleDegrees = 0, MaximumAngleDegrees = 2 * brewster, Points = 3 };
        var result = analysis.Compute(new OpticsAnalysisContext("P", new OpticsSettings()), settings, default);
        var plot = (LinePlot)result.Plots[0];
        var s = plot.Curves.Single(c => c.Name == "s");
        var p = plot.Curves.Single(c => c.Name == "p");
        Assert.Equal(Math.Pow(0.52 / 2.52, 2), s.Y[0], 14);
        Assert.Equal(brewster, p.X[1], 12);
        Assert.True(p.Y[1] < 1e-28);
        Assert.Equal((s.Y[2] + p.Y[2]) / 2, plot.Curves.Single(c => c.Name == "Average").Y[2], 15);
    }

    /// <summary>Against wavelength the quarter-wave coating's minimum lies at the primary
    /// wavelength (0.55 µm here, the system's second wavelength) and equals the closed form.</summary>
    [Fact]
    public void ReflectionVsWavelength_OfAQuarterWave_HasItsMinimumAtThePrimaryWavelength()
    {
        var analysis = new CoatingVsWavelengthAnalysis(CoatingQuantity.Reflection);
        var settings = new CoatingVsWavelengthSettings { Coating = "qw", Substrate = "1.52", Points = 21 };   // 0.45–0.65 µm from the system
        var plot = (LinePlot)analysis.Compute(new OpticsAnalysisContext("P", SystemWithQuarterWave()), settings, default).Plots[0];
        var s = plot.Curves.Single(c => c.Name == "s");
        Assert.Equal(0.45, s.X[0], 15);
        Assert.Equal(0.65, s.X[^1], 15);
        int min = Array.IndexOf(s.Y.ToArray(), s.Y.Min());
        Assert.Equal(0.55, s.X[min], 12);
        Assert.Equal(Math.Pow((1.52 - 1.38 * 1.38) / (1.52 + 1.38 * 1.38), 2), s.Y[min], 14);
    }

    [Fact]
    public void AMissingCoating_IsRefusedByName_ListingTheCatalog()
    {
        var analysis = new CoatingVsAngleAnalysis(CoatingQuantity.Transmission);
        var ex = Assert.Throws<InvalidOperationException>(() => analysis.Compute(new OpticsAnalysisContext("P", SystemWithQuarterWave()),
            new CoatingVsAngleSettings { Coating = "NOPE" }, default));
        Assert.Contains("NOPE", ex.Message);
        Assert.Contains("QW", ex.Message);
        Assert.Throws<InvalidOperationException>(() => analysis.Compute(new OpticsAnalysisContext("P", SystemWithQuarterWave()),
            new CoatingVsAngleSettings { Substrate = "UNOBTAINIUM" }, default));
    }

    /// <summary>A substrate named as a glass is found in the snapshot's glass catalogs.</summary>
    [Fact]
    public void ASubstrateGlass_IsFoundInTheActiveCatalogs()
    {
        var glass = new OpticalMaterial
        {
            Name = "TESTGLASS", Catalog = "test",
            Tabulated = new TabulatedIndex { Points = new[] { new TabulatedIndexPoint(0.4, 1.6, 0), new TabulatedIndexPoint(0.8, 1.6, 0) } }
        };
        var context = new OpticsAnalysisContext("P", new OpticsSettings())
        {
            Catalogs = new[] { new OpticalCatalog { Name = "test", Materials = new[] { glass } } }
        };
        var result = new CoatingVsAngleAnalysis(CoatingQuantity.Reflection).Compute(context,
            new CoatingVsAngleSettings { Substrate = "testglass", WavelengthMicrometers = 0.55, MaximumAngleDegrees = 10, Points = 2 }, default);
        Assert.Equal(Math.Pow(0.6 / 2.6, 2), ((LinePlot)result.Plots[0]).Curves[0].Y[0], 14);
    }
}
