using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Optimization;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-59: the Optimize window's Glasses section as the Optics workspace runs it. The
/// glass flags must reach the lens through the Lens Data Editor and survive it (the editor has no
/// cell for them and keeps them with the row), and the Glass Substitution Template must save and
/// reload with the lens. The search and the conversion are the library's (OpenSim.Tests'
/// GlassSubstitutionTests and Gates/GlassSubstitutionGateTests). Values are arbitrary inputs.</summary>
public sealed class OpticsGlassSubstitutionTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    private static OpticsSettings Singlet() => new()
    {
        SystemData = new OpticsSystemSettings { ApertureValue = 10 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = 50, Thickness = 2, ModelGlass = new OpticsModelGlassSettings { Nd = 1.6, Vd = 40 }, IsStop = true },
            new() { Radius = -50, Thickness = 100 },
            new(),
        },
    };

    [Fact]
    public void GlassFlags_ReachTheLens_AndAreVariables()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var optimize = Optics.Optimize;
        optimize.RefreshGlassesCommand.Execute(null);
        var row = Assert.Single(optimize.Glasses);
        Assert.Equal(1, row.Surface);
        Assert.True(row.IsModel);
        row.VaryVd = true;
        row.Substitute = true;

        optimize.ApplyGlassFlagsCommand.Execute(null);

        var saved = Optics.ToSettings();
        var columns = saved.Surfaces![1].Flags!.Select(f => f.Column).ToArray();
        Assert.Contains(GlassVariables.ModelVdColumn, columns);
        Assert.Contains(GlassVariables.SubstituteColumn, columns);
        Assert.Equal(new OpticsModelGlassSettings { Nd = 1.6, Vd = 40 }, saved.Surfaces[1].ModelGlass);
        Assert.Equal(new[] { LensVariableKind.ModelAbbe }, LensVariables.Find(saved).Select(v => v.Kind));
        Assert.Equal(new[] { 1 }, GlassVariables.SubstituteSurfaces(saved));

        optimize.RefreshGlassesCommand.Execute(null);
        Assert.True(optimize.Glasses[0].VaryVd);
        Assert.False(optimize.Glasses[0].VaryNd);
        Assert.True(optimize.Glasses[0].Substitute);
    }

    [Fact]
    public void TheTemplate_SavesAndReloadsWithTheLens()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        Assert.Null(Optics.ToSettings().GlassSubstitution);   // no template: saved as before OP-59

        var optimize = Optics.Optimize;
        optimize.UseTemplate = true;
        optimize.AllowObsolete = false;
        optimize.MaxRelativeCost = 2.5;
        optimize.TemplateCatalogs = "RII-GLASS, RII-ORGANIC";
        var saved = Optics.ToSettings().GlassSubstitution;
        Assert.NotNull(saved);
        Assert.True(saved!.UseTemplate);
        Assert.False(saved.AllowObsolete);
        Assert.Equal(2.5, saved.MaxRelativeCost);
        Assert.Equal(new[] { "RII-GLASS", "RII-ORGANIC" }, saved.Catalogs);

        Assert.Empty(Optics.LoadSettings(Singlet()));
        Assert.False(optimize.UseTemplate);
        Assert.Empty(Optics.LoadSettings(Singlet() with { GlassSubstitution = saved }));
        Assert.True(optimize.UseTemplate);
        Assert.Equal(2.5, optimize.MaxRelativeCost);
        var again = Optics.ToSettings().GlassSubstitution!;
        Assert.Equal(saved with { Catalogs = null }, again with { Catalogs = null });
        Assert.Equal(saved.Catalogs, again.Catalogs);
    }

    [Fact]
    public async Task SubstitutionWithoutFlaggedSurfaces_SaysWhy()
    {
        Assert.Empty(Optics.LoadSettings(Singlet()));
        var optimize = Optics.Optimize;
        await optimize.SubstituteGlassesCommand.ExecuteAsync(null);
        Assert.Contains("flagged for glass substitution", optimize.Status);
        Assert.False(optimize.IsRunning);
    }
}
