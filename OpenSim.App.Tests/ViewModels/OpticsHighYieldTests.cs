using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization.HighYield;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-62: the Optimize window's High Yield section as the Optics workspace runs it. The
/// rows and their values are the library's (OpenSim.Tests' HighYieldTests); here they must reach
/// the Merit Function Editor, replace rather than pile up, come out again, and the weight must be
/// saved with the lens. The lens (a glass shell before an ideal lens; radii, indices and the focal
/// length arbitrary inputs) has two refracting surfaces.</summary>
public sealed class OpticsHighYieldTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    private static OpticsSettings Shell() => new()
    {
        SystemData = new OpticsSystemSettings
        {
            ApertureValue = 10,
            Fields = new() { new OpticsFieldSettings() },
            Wavelengths = new() { new OpticsWavelengthSettings { Micrometers = 0.55 } },
        },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 15, Thickness = 0, Index = 1.5, IsStop = true },
            new() { Radius = 15, Thickness = 5 },
            new()
            {
                Type = "Paraxial", Thickness = 100,
                Parameters = new() { new() { Number = 1, Value = 100 }, new() { Number = 2, Value = 2 } },
            },
            new(),
        },
        MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(new[] { MeritOperandRow.Of("EFFL", 0, 1, 100, 1) })),
    };

    private int HyldRows() => Optics.MeritEditor.ToFunction().Rows.Count(r => r.Code == "HYLD");

    [Fact]
    public void AddingRows_ReachesTheMeritEditor_ReplacesThem_AndRemovesThem()
    {
        Assert.Empty(Optics.LoadSettings(Shell()));
        var optimize = Optics.Optimize;
        Assert.True(optimize.AddHighYieldRowsCommand.CanExecute(null));

        optimize.HighYieldWeight = 2;
        optimize.AddHighYieldRowsCommand.Execute(null);
        int count = HyldRows();
        Assert.True(count > 0, optimize.HighYieldText);
        Assert.Equal("EFFL", Optics.MeritEditor.ToFunction().Rows[0].Code);
        Assert.StartsWith(HighYieldMerit.HeaderPrefix, Optics.MeritEditor.ToFunction().Rows[1].Comment);
        Assert.Equal(new[] { 1, 2 }, optimize.HighYieldSurfaces.Select(s => s.Surface));
        Assert.NotNull(optimize.LastHighYieldReport);
        Assert.Equal(2.0, Optics.ToSettings().HighYield!.Weight);

        optimize.AddHighYieldRowsCommand.Execute(null);
        Assert.Equal(count, HyldRows());

        optimize.RemoveHighYieldRowsCommand.Execute(null);
        Assert.Equal(0, HyldRows());
        Assert.Single(Optics.MeritEditor.ToFunction().Rows);
        Assert.Empty(optimize.HighYieldSurfaces);
    }

    [Fact]
    public void TheWeight_IsSavedWithTheLens_AndTheDefaultSavesNothing()
    {
        Assert.Empty(Optics.LoadSettings(Shell()));
        Assert.Null(Optics.ToSettings().HighYield);
        Assert.Empty(Optics.LoadSettings(Shell() with { HighYield = new OpticsHighYieldSettings { Weight = 0.25 } }));
        Assert.Equal(0.25, Optics.Optimize.HighYieldWeight);
        Assert.Equal(0.25, Optics.ToSettings().HighYield!.Weight);
    }

    [Fact]
    public void ABadWeight_IsRefusedByName_AndNothingIsWritten()
    {
        Assert.Empty(Optics.LoadSettings(Shell()));
        Optics.Optimize.HighYieldWeight = -1;
        Optics.Optimize.AddHighYieldRowsCommand.Execute(null);
        Assert.Contains("weight must be positive", Optics.Optimize.HighYieldText);
        Assert.Equal(0, HyldRows());
    }
}
