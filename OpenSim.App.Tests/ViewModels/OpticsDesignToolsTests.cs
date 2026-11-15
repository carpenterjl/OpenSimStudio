using System.IO;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.DesignTools;
using OpenSim.Optics.Merit;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>
/// OP-61: the design conversion tools as the Optics workspace runs them. The library's gates are
/// in OpenSim.Tests (DesignToolsTests); here a result must reach the lens only through Keep, as
/// one undoable edit of the Lens Data Editor, and a bad request must be named. Radii, indices and
/// coefficients are arbitrary inputs.
/// </summary>
public sealed class OpticsDesignToolsTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    /// <summary>A singlet whose front is an Even Asphere (α2 = 1e-6 mm⁻³), semi-diameter 10 mm,
    /// and a one-row merit function.</summary>
    private static OpticsSettings AsphereSinglet() => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 10 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new()
            {
                Type = "Even Asphere", Radius = 40, Thickness = 5, Index = 1.5, IsStop = true, SemiDiameter = 10,
                Parameters = new() { new() { Number = 2, Value = 1e-6 } },
            },
            new() { Radius = -200, Thickness = 70, SemiDiameter = 10 },
            new(),
        },
        MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(new[] { MeritOperandRow.Of("EFFL", 0, 1, target: 70, weight: 1) })),
    };

    [Fact]
    public async Task Convert_ShowsTheReport_AndKeepWritesItThroughTheEditor_AndUndoPutsItBack()
    {
        Assert.Empty(Optics.LoadSettings(AsphereSinglet()));
        var tools = Optics.DesignTools;
        tools.ConvertSurface = 1;
        tools.SelectedTarget = tools.Targets.Single(t => t.Value == AsphereTarget.QTypeQcon);

        await tools.ConvertCommand.ExecuteAsync(null);

        Assert.True(tools.HasResult, tools.Status);
        Assert.Contains("Residual sag error", tools.Details);
        Assert.Equal("Even Asphere", Optics.ToSettings().Surfaces![1].Type);   // nothing written before Keep

        tools.KeepCommand.Execute(null);
        Assert.Equal("Q-Type Asphere", Optics.ToSettings().Surfaces![1].Type);
        Assert.Equal("Convert Asphere Types", tools.LastKept);
        Assert.False(tools.HasResult);

        Optics.LensEditor.UndoCommand.Execute(null);
        Assert.Equal("Even Asphere", Optics.ToSettings().Surfaces![1].Type);
    }

    [Fact]
    public async Task Convert_OfTheImageSurface_IsRefusedByName()
    {
        Assert.Empty(Optics.LoadSettings(AsphereSinglet()));
        var tools = Optics.DesignTools;
        tools.ConvertSurface = 3;
        await tools.ConvertCommand.ExecuteAsync(null);
        Assert.False(tools.HasResult);
        Assert.Contains("surface 3 is not a lens surface", tools.Status);
    }

    [Fact]
    public async Task StockLensMatching_WithNoCatalogFolder_SaysSo()
    {
        Assert.Empty(Optics.LoadSettings(AsphereSinglet()));
        var tools = Optics.DesignTools;
        tools.LibraryFolder = Path.Combine(Path.GetTempPath(), "opensim-op61-none-" + Guid.NewGuid().ToString("N"));
        await tools.MatchStockLensesCommand.ExecuteAsync(null);
        Assert.False(tools.HasResult);
        Assert.Contains("No stock lens catalog", tools.Status);
    }
}
