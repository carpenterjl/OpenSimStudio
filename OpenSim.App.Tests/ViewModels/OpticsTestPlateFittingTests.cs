using System.IO;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Merit;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>
/// OP-17: Test Plate Fitting as the Optics workspace runs it. The library's gate is in
/// OpenSim.Tests (TestPlateFittingTests); here the fitted design must reach the lens through the
/// Lens Data Editor as one undoable edit, and a bad list must be named. The doublet's radii,
/// thicknesses, indices and the plates are arbitrary inputs; the list is synthetic.
/// </summary>
public sealed class OpticsTestPlateFittingTests : IDisposable
{
    private readonly AppComposition _app = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opensim-op17-" + Guid.NewGuid().ToString("N"));

    public OpticsTestPlateFittingTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _app.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    /// <summary>A cemented doublet with fixed semi-diameters 10 mm, its three radii variable at
    /// 0.5 % off 60, −45 and −200 mm; merit EFFL = <paramref name="focal"/>.</summary>
    private static OpticsSettings Doublet(double focal) => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 10 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 60.3, Thickness = 4, Index = 1.5, IsStop = true, SemiDiameter = 10, Flags = new() { Var() } },
            new() { Radius = -45.2, Thickness = 2, Index = 1.65, SemiDiameter = 10, Flags = new() { Var() } },
            new() { Radius = -201, Thickness = 95, SemiDiameter = 10, Flags = new() { Var() } },
            new(),
        },
        MeritFunction = MeritFunctionSettings.ToSettings(new MeritFunction(new[] { MeritOperandRow.Of("EFFL", 0, 1, target: focal, weight: 1) })),
    };

    private static OpticsCellFlagSettings Var() => new() { Column = "Radius", Kind = "Variable" };

    private string WriteList(string text)
    {
        string path = Path.Combine(_folder, "synthetic.tpd");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Fit_WritesThePlateRadiiThroughTheEditor_AndUndoPutsTheDesignBack()
    {
        // The focal length of the doublet at the start, so the plate design is near the optimum.
        var start = Doublet(0);
        double focal = MeritEvaluator.Evaluate(new MeritFunction(new[] { MeritOperandRow.Of("EFFL", 0, 1, target: 0, weight: 1) }),
            new ProjectMeritConfigurations(start, Array.Empty<OpenSim.Optics.Materials.OpticalCatalog>())).Rows[0].Value;
        Assert.Empty(Optics.LoadSettings(Doublet(focal)));
        var fit = Optics.TestPlates;
        fit.FileName = WriteList("! Synthetic\nP60 60 30 -1\nN57 57 30 0\nP45 45 30 0\nN47 47 30 0\nP200 200 30 -1\nN190 190 30 0\n");
        fit.SelectedMethod = fit.Methods.Single(m => m.Value == OpenSim.Optics.Optimization.TestPlates.TestPlateFitMethod.BestToWorst);

        await fit.FitCommand.ExecuteAsync(null);

        Assert.NotNull(fit.LastResult);
        var s = Optics.ToSettings().Surfaces!;
        Assert.Equal(new double?[] { 60, -45, -200 }, new[] { s[1].Radius, s[2].Radius, s[3].Radius });
        Assert.All(new[] { 1, 2, 3 }, j => Assert.DoesNotContain(s[j].Flags ?? new List<OpticsCellFlagSettings>(), f => f.Column == "Radius" && f.Kind == "Variable"));
        Assert.Contains("plate P60", fit.Details);

        Optics.LensEditor.UndoCommand.Execute(null);
        var back = Optics.ToSettings().Surfaces!;
        Assert.Equal(new double?[] { 60.3, -45.2, -201 }, new[] { back[1].Radius, back[2].Radius, back[3].Radius });
        Assert.Contains(back[1].Flags!, f => f.Column == "Radius" && f.Kind == "Variable");
    }

    [Fact]
    public async Task ABadList_IsNamed_AndNothingIsApplied()
    {
        Assert.Empty(Optics.LoadSettings(Doublet(100)));
        var fit = Optics.TestPlates;
        fit.FileName = WriteList("P60 60 30 7\n");
        await fit.FitCommand.ExecuteAsync(null);
        Assert.Null(fit.LastResult);
        Assert.Contains("line 1", fit.Status);
        Assert.Equal(60.3, Optics.ToSettings().Surfaces![1].Radius);

        fit.FileName = Path.Combine(_folder, "missing.tpd");
        fit.ShowListCommand.Execute(null);
        Assert.Contains("does not exist", fit.Status);
    }
}
