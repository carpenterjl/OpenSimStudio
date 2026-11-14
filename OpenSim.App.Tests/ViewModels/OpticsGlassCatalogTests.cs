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
        Assert.NotEmpty(panel.Catalogs);                              // the shipped seed (OP-12)
        Assert.All(panel.Catalogs, r => Assert.Equal("built in", r.Origin));

        string status = panel.ImportCatalogFile(WriteCatalog("TESTGLASS"));
        Assert.StartsWith("Imported catalog 'TESTGLASS': 2 glasses.", status);
        var row = Assert.Single(panel.Catalogs, r => r.Origin == "user folder");
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

        Assert.Equal("", panel.ProbeEnvironmentResult);                 // adjusting is off

        // OP-13: with adjusting on, the probe also shows the index at the system's environment.
        optics.AdjustIndexToEnvironment = true;
        Assert.StartsWith("n = 1.5168000 relative to the system's air at 20 °C and 1 atm", panel.ProbeEnvironmentResult);   // the reference conditions
        Assert.Contains("Ciddor", panel.ProbeEnvironmentResult);
        optics.TemperatureCelsius = 40;
        Assert.StartsWith("n = 1.516857", panel.ProbeEnvironmentResult);   // 20 K × about 2.9e-6/K (N-BK7's TD constants) above 1.5168000
        optics.AdjustIndexToEnvironment = false;
        optics.TemperatureCelsius = 20;

        panel.ProbeMicrometers = 3.0;
        Assert.Contains("outside its valid range 0.3–2.5 µm", panel.ProbeResult);

        panel.GlassFilter = "of";
        Assert.Equal("OFF", Assert.Single(panel.Glasses).Name);
        Assert.Equal("OFF", panel.SelectedGlass!.Name);
        Assert.StartsWith("Does not match", panel.GlassCheckResult);

        string broken = Path.Combine(_app.UserDirectory, "BROKEN.agf");
        File.WriteAllText(broken, "NM A 2 0 1.5 60\nCD 1 x 3\n");
        Assert.StartsWith("Catalog not imported:", panel.ImportCatalogFile(broken));
        Assert.Single(panel.Catalogs, r => r.Origin == "user folder");
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
        panel.SelectedCatalog = panel.Catalogs.Single(r => r.Name == "SECOND");
        panel.SelectedGlass = panel.Glasses[1];
        panel.GlassFilter = "x";
        Assert.Same(before, session.OpticsSystem);

        // The shipped catalogs stay active beside SECOND, in the list's order.
        string[] expected = panel.Catalogs.Where(r => r.Name != "FIRST").Select(r => r.Name).ToArray();
        Assert.Equal("SECOND", expected[^1]);
        panel.Catalogs.Single(r => r.Name == "FIRST").IsActive = false;
        Assert.Equal(expected, panel.ActiveCatalogNames);
        Assert.Equal(expected, optics.ToSettings().Materials!.ActiveCatalogs);
        Assert.Equal(expected, session.OpticsSystem!.Materials!.ActiveCatalogs);

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
        string[] expected = panel.Catalogs.Where(r => r.Name != "FIRST").Select(r => r.Name).ToArray();
        panel.Catalogs.Single(r => r.Name == "FIRST").IsActive = false;

        string path = Path.Combine(_app.UserDirectory, "glasses.ossproj");
        main.CaptureProjectSettings();
        _app.Resolve<ProjectSerializer>().Save(session.Project, path);
        main.NewProjectCommand.Execute(null);
        Assert.Null(main.Optics.ToSettings().Materials);

        main.LoadProjectFromPath(path);
        Assert.Equal(expected, main.Optics.ToSettings().Materials!.ActiveCatalogs);
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

    /// <summary>OP-12: the shipped refractiveindex.info catalogs are listed as built in and active
    /// for a new lens, and a shipped material shows its formula in the database's own numbering,
    /// its page and references. The index at d is worked from Malitson.yml's coefficients
    /// (OpenSim.Optics/Materials/RefractiveIndexInfo/data/main/SiO2/nk/Malitson.yml) in the test.</summary>
    [Fact]
    public void TheShippedCatalogs_AreListed_AndAMaterialShowsItsSource()
    {
        var optics = _app.Resolve<OpticsViewModel>();
        var panel = optics.GlassCatalogs;
        Assert.Equal(new[] { "RII-GLASS", "RII-MAIN", "RII-ORGANIC" }, panel.Catalogs.Select(r => r.Name));
        Assert.All(panel.Catalogs, r => Assert.True(r.IsActive));
        Assert.Null(optics.ToSettings().Materials);                   // every catalog, not chosen

        panel.SelectedCatalog = panel.Catalogs.Single(r => r.Name == "RII-MAIN");
        Assert.Contains("not checked", panel.CatalogSummary);
        panel.GlassFilter = "SiO2-Malitson";
        Assert.Equal("SiO2-Malitson", panel.SelectedGlass!.Name);
        Assert.Equal("refractiveindex.info formula 1 (Sellmeier (preferred))", panel.GlassFormula);
        Assert.Equal("0.21 – 6.7", panel.GlassRange);
        Assert.StartsWith("refractiveindex.info database (CC0 1.0), page main/SiO2/nk/Malitson.yml (Malitson 1965: n 0.21–6.7 µm)", panel.GlassSource);
        Assert.Contains("I. H. Malitson.", panel.GlassSource);
        Assert.Equal("temperature 293", panel.GlassConditions);
        Assert.Equal("Fused silica, 20 °C", panel.GlassComment);

        double l2 = 0.5875618 * 0.5875618;                              // the d line, SpectralLines.LowerD
        double n = Math.Sqrt(1 + 0.6961663 * l2 / (l2 - 0.0684043 * 0.0684043)
                               + 0.4079426 * l2 / (l2 - 0.1162414 * 0.1162414)
                               + 0.8974794 * l2 / (l2 - 9.896161 * 9.896161));
        Assert.StartsWith($"n = {n.ToString("F7", System.Globalization.CultureInfo.InvariantCulture)}", panel.ProbeResult);

        panel.GlassFilter = "Si-Li-293K";
        Assert.Equal("Tabulated n", panel.GlassFormula);
        panel.SelectedCatalog = panel.Catalogs.Single(r => r.Name == "RII-GLASS");
        panel.GlassFilter = "soda-lime";
        Assert.Equal("refractiveindex.info formula 5 (Cauchy); k tabulated separately", panel.GlassFormula);
    }
}
