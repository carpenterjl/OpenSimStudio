using System.IO;
using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Optics.Materials;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-11: the Optics materials panel — import, the self-check it shows, a glass's
/// data, and the active catalogs through the project file.</summary>
public sealed class OpticsGlassCatalogTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    /// <summary>A two-glass catalog. Its Sellmeier 1 coefficients are AGFFileReader.jl's
    /// Examples_N_BK7 (MIT, src/BaseGlasses.jl line 8); nd 1.516800 and Vd 64.1673 are what they
    /// give at d, F and C, rounded (the library's fixture, OP11_FIXTURE.agf, says how). The
    /// second glass prints an nd 1e−4 off, so the self-check fails it.</summary>
    private string WriteCatalog(string name)
    {
        string path = Path.Combine(_app.UserDirectory, name + ".agf");
        File.WriteAllText(path, """
            CC A test catalog
            NM GOOD 2 517642 1.516800 64.1673 0 1 -
            GC the good one
            ED 7.1 8.2 2.51 -0.0009 0
            CD 1.03961212 0.00600069867 0.231792344 0.0200179144 1.01046945 103.560653 0 0 0 0
            TD 1.86E-06 1.31E-08 -1.37E-11 4.34E-07 6.27E-10 0.17 20.0
            LD 0.3 2.5
            IT 0.4 0.99 25
            NM OFF 2 517642 1.516900 64.1673 0 0 -
            CD 1.03961212 0.00600069867 0.231792344 0.0200179144 1.01046945 103.560653 0 0 0 0
            LD 0.3 2.5
            """);
        return path;
    }

    [Fact]
    public void ImportingACatalog_ListsIt_SelectsIt_AndShowsItsSelfCheck()
    {
        var optics = _app.Resolve<OpticsViewModel>();
        var panel = optics.GlassCatalogs;
        Assert.Empty(panel.Catalogs);                                 // no built-ins until OP-12

        string status = panel.ImportCatalogFile(WriteCatalog("TESTGLASS"));
        Assert.StartsWith("Imported catalog 'TESTGLASS': 2 glasses.", status);
        var row = Assert.Single(panel.Catalogs);
        Assert.Same(row, panel.SelectedCatalog);
        Assert.True(row.IsActive);
        Assert.Equal("user folder", row.Origin);
        Assert.Equal("1 of 2 checked glasses match the printed nd and Vd to the printed digits; 1 do not; 0 not checked.", panel.CatalogSummary);

        Assert.Equal("GOOD", panel.SelectedGlass!.Name);
        Assert.Equal("2 (Sellmeier 1)", panel.GlassFormula);
        Assert.Equal("1.516800 / 64.1673", panel.GlassNdVd);
        Assert.StartsWith("1.516800 / 64.167", panel.GlassComputedNdVd);
        Assert.StartsWith("Matches", panel.GlassCheckResult);
        Assert.Equal("20", panel.GlassReferenceTemperature);
        Assert.Equal("0.3 – 2.5", panel.GlassRange);
        Assert.Equal("the good one", panel.GlassComment);
        Assert.StartsWith("n = 1.5168000", panel.ProbeResult);       // the d line by default

        panel.ProbeMicrometers = 3.0;
        Assert.Contains("outside its valid range 0.3–2.5 µm", panel.ProbeResult);

        panel.GlassFilter = "of";
        Assert.Equal("OFF", Assert.Single(panel.Glasses).Name);
        Assert.Equal("OFF", panel.SelectedGlass!.Name);
        Assert.StartsWith("Does not match", panel.GlassCheckResult);

        string broken = Path.Combine(_app.UserDirectory, "BROKEN.agf");
        File.WriteAllText(broken, "NM A 2 0 1.5 60\nCD 1 x 3\n");
        Assert.StartsWith("Catalog not imported:", panel.ImportCatalogFile(broken));
        Assert.Single(panel.Catalogs);
    }

    [Fact]
    public void ChangingTheActiveCatalogs_PublishesTheSystem_ButBrowsingDoesNot()
    {
        var optics = _app.Resolve<OpticsViewModel>();
        var session = _app.Resolve<ProjectSession>();
        var panel = optics.GlassCatalogs;
        panel.ImportCatalogFile(WriteCatalog("FIRST"));
        panel.ImportCatalogFile(WriteCatalog("SECOND"));
        Assert.Null(optics.ToSettings().Materials);                   // every catalog, not chosen

        var before = session.OpticsSystem;
        panel.SelectedCatalog = panel.Catalogs[1];
        panel.SelectedGlass = panel.Glasses[1];
        panel.GlassFilter = "x";
        Assert.Same(before, session.OpticsSystem);

        panel.Catalogs.Single(r => r.Name == "FIRST").IsActive = false;
        Assert.Equal(new[] { "SECOND" }, panel.ActiveCatalogNames);
        Assert.Equal(new[] { "SECOND" }, optics.ToSettings().Materials!.ActiveCatalogs);
        Assert.Equal(new[] { "SECOND" }, session.OpticsSystem!.Materials!.ActiveCatalogs);

        optics.Reset();
        Assert.Null(optics.ToSettings().Materials);
        Assert.All(panel.Catalogs, r => Assert.True(r.IsActive));
    }

    [Fact]
    public void TheActiveCatalogs_SaveWithTheProject_AndAMissingOneIsKeptAndNamed()
    {
        var main = _app.Resolve<MainViewModel>();
        var session = _app.Resolve<ProjectSession>();
        var panel = main.Optics.GlassCatalogs;
        panel.ImportCatalogFile(WriteCatalog("FIRST"));
        panel.ImportCatalogFile(WriteCatalog("SECOND"));
        panel.Catalogs.Single(r => r.Name == "FIRST").IsActive = false;

        string path = Path.Combine(_app.UserDirectory, "glasses.ossproj");
        main.CaptureProjectSettings();
        _app.Resolve<ProjectSerializer>().Save(session.Project, path);
        main.NewProjectCommand.Execute(null);
        Assert.Null(main.Optics.ToSettings().Materials);

        main.LoadProjectFromPath(path);
        Assert.Equal(new[] { "SECOND" }, main.Optics.ToSettings().Materials!.ActiveCatalogs);
        Assert.False(panel.Catalogs.Single(r => r.Name == "FIRST").IsActive);

        // A file naming a catalog this machine lacks: kept, shown as not loaded, logged.
        File.WriteAllText(path, """{ "formatVersion": 5, "name": "Elsewhere", "analysisType": "Optics", "optics": { "materials": { "activeCatalogs": [ "VENDOR", "FIRST" ] } }, "bodies": [] }""");
        main.LoadProjectFromPath(path);
        Assert.Equal(new[] { "VENDOR", "FIRST" }, main.Optics.ToSettings().Materials!.ActiveCatalogs);
        var missing = panel.Catalogs.First();
        Assert.Equal(("VENDOR", "not loaded", 0), (missing.Name, missing.Origin, missing.GlassCount));
        Assert.False(panel.Catalogs.Single(r => r.Name == "SECOND").IsActive);
        Assert.Contains(_app.Log.Entries, e => e.Contains("'VENDOR'") && e.Contains("not loaded on this machine"));
        Assert.Equal("FIRST", _app.Resolve<GlassCatalogLibrary>().FindGlass("GOOD", panel.ActiveCatalogNames)!.Catalog);
    }
}
