using System.Text.Json;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.MultiConfig;
using M = OpenSim.App.ViewModels.OpticsMultiConfigEditorViewModel;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-91: Make Thermal and the glass-to-material links in the Multi-Configuration
/// Editor. The lens (constant indices, so the TCE columns give the expansion) and the
/// temperatures are arbitrary inputs.</summary>
public sealed class OpticsMultiConfigThermalTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();
    private M Editor => Optics.MultiConfig;

    private static OpticsSettings Lens() => new()
    {
        SystemData = new OpticsSystemSettings { TemperatureCelsius = 20 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 4, Index = 1.5, IsStop = true, Tce = 7 },
            new() { Radius = -50, Thickness = 45, Tce = 23 },
            new(),
        },
    };

    [Fact]
    public void MakeThermal_BuildsTheTemperatureConfigurations_AndEveryOneApplies()
    {
        Assert.Empty(Optics.LoadSettings(Lens()));
        Editor.ThermalConfigurations = 2;
        Editor.ThermalMinimum = 0;
        Editor.ThermalMaximum = 40;
        Editor.MakeThermalCommand.Execute(null);

        Assert.Equal(3, Editor.ConfigurationCount);
        Assert.Empty(Editor.Problems);
        var table = Optics.ToSettings().MultiConfiguration!;
        var temp = table.Operands!.Single(o => o.Type == "TEMP");
        Assert.Equal(new[] { 20.0, 0, 40 }, temp.Values!.Select(v => v.Value).ToArray());
        var thic2 = table.Operands!.Single(o => o.Type == "THIC" && o.Number1 == 2);
        Assert.Equal(MultiConfiguration.ThermalPickup, thic2.Values![2].Solve);
        // The thermal pickup evaluates: at 40 °C the air space is not the nominal 45.
        var hot = MultiConfiguration.SettingsFor(Optics.ToSettings(), 3, Array.Empty<OpenSim.Optics.Materials.OpticalCatalog>());
        Assert.NotEqual(45, hot.Surfaces![2].Thickness);
        Assert.Contains("Made 3 configurations", Editor.Status);
    }

    [Fact]
    public void MakeThermal_RefusesABadForm_WithAMessage()
    {
        Assert.Empty(Optics.LoadSettings(Lens()));
        Editor.ThermalMinimum = 50;
        Editor.ThermalMaximum = 10;
        Editor.MakeThermalCommand.Execute(null);
        Assert.Contains("below the minimum", Editor.Status);
        Editor.ThermalMaximum = 60;
        Editor.ThermalPressure = "lots";
        Editor.MakeThermalCommand.Execute(null);
        Assert.Contains("not a number", Editor.Status);
        Assert.Equal(1, Editor.ConfigurationCount);
    }

    [Fact]
    public void AGlassLink_IsSavedWithTheLens_AndLoadsBack()
    {
        Assert.Empty(Optics.LoadSettings(Lens()));
        Assert.Null(Optics.ToSettings().Thermal);
        string material = Assert.IsAssignableFrom<IReadOnlyList<string>>(Editor.MaterialNames).First();
        Editor.LinkGlass = "N-BK7";
        Editor.LinkMaterial = material;
        Editor.LinkGlassToMaterialCommand.Execute(null);

        var saved = Optics.ToSettings();
        var link = Assert.Single(saved.Thermal!.GlassMaterials!);
        Assert.Equal("N-BK7", link.Glass);
        Assert.Equal(material, link.Material!.Name);
        Assert.Contains("N-BK7", Editor.GlassLinksText);

        Optics.Reset();
        Assert.Null(Optics.ToSettings().Thermal);
        Assert.Empty(Optics.LoadSettings(saved));
        Assert.Equal(JsonSerializer.Serialize(saved.Thermal), JsonSerializer.Serialize(Optics.ToSettings().Thermal));

        Editor.UnlinkGlassCommand.Execute(null);
        Assert.Null(Optics.ToSettings().Thermal);
    }
}
