using System.IO;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Optics.Catalogs;
using OpenSim.Optics.Interop.Zemax;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>
/// OP-16: Lens Catalogs as the Optics workspace runs it. The library's gate is in OpenSim.Tests
/// (LensCatalogTests); here Search must list the library folder's lenses, Insert must reach the
/// lens through the Lens Data Editor as one undoable edit, and Make Private Lens Catalog and the
/// design templates must work from the dialog. The lenses (index 1.5, radii, pupils) are
/// arbitrary inputs written with OP-94's writer; no vendor file is used.
/// </summary>
public sealed class OpticsLensCatalogTests : IDisposable
{
    private readonly AppComposition _app = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opensim-op16-" + Guid.NewGuid().ToString("N"));

    public OpticsLensCatalogTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _app.Dispose();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private OpticsViewModel Optics => _app.Resolve<OpticsViewModel>();

    private static OpticsSettings Singlet(string title, double r1, double? r2, double back) => new()
    {
        SystemData = new OpticsSystemSettings { Title = title, ApertureValue = 10 },
        Surfaces = new()
        {
            new() { ThicknessIsInfinite = true },
            new() { Radius = r1, Thickness = 4, Index = 1.5, IsStop = true },
            new() { Radius = r2, Thickness = back },
            new(),
        },
    };

    /// <summary>A library with one vendor folder: a 100 mm plano-convex (R1 = (n − 1)·100 = 50,
    /// derived) and a meniscus.</summary>
    private string Library()
    {
        string library = Path.Combine(_folder, "Library");
        string vendor = Path.Combine(library, "ACME");
        Directory.CreateDirectory(vendor);
        ZemaxLensFiles.Export(Singlet("PCX-100", 50, null, 96), Path.Combine(vendor, "PCX-100.zmx"));
        ZemaxLensFiles.Export(Singlet("MEN-200", 50, 100, 190), Path.Combine(vendor, "MEN-200.zmx"));
        return library;
    }

    [Fact]
    public void SearchAndInsert_PutTheLensIntoTheEditor_AndUndoTakesItOut()
    {
        Assert.Empty(Optics.LoadSettings(new OpticsSettings
        {
            SystemData = new OpticsSystemSettings { ApertureValue = 10 },
            Surfaces = new() { new() { ThicknessIsInfinite = true }, new() { Thickness = 10, IsStop = true }, new() },
        }));
        var catalog = Optics.LensCatalog;
        catalog.SetLibraryFolder(Library(), save: false);
        Assert.Equal(new[] { OpticsLensCatalogViewModel.AllCatalogs, "ACME" }, catalog.Vendors);

        catalog.UseFocalLength = true;
        catalog.FocalLengthMin = 95;
        catalog.FocalLengthMax = 105;
        catalog.SearchCommand.Execute(null);
        Assert.Equal("PCX-100", Assert.Single(catalog.Results).Lens.PartName);

        catalog.InsertAt = 2;
        catalog.InsertLensCommand.Execute(null);
        var s = Optics.ToSettings().Surfaces!;
        Assert.Equal(5, s.Count);
        Assert.Equal(50, s[2].Radius!.Value, 12);
        Assert.Contains("inserted as surfaces 2 to 3", catalog.Status);

        Optics.LensEditor.UndoCommand.Execute(null);
        Assert.Equal(3, Optics.ToSettings().Surfaces!.Count);
    }

    [Fact]
    public void MakePrivateCatalog_WritesTheFileIntoTheLibrary_AndTheReportListsIt()
    {
        string library = Path.Combine(_folder, "Empty");
        Directory.CreateDirectory(library);
        string source = Library();
        var catalog = Optics.LensCatalog;
        catalog.SetLibraryFolder(library, save: false);
        catalog.PrivateSource = Path.Combine(source, "ACME");
        catalog.PrivateName = "Mine";
        catalog.MakePrivateCatalogCommand.Execute(null);
        Assert.True(File.Exists(Path.Combine(library, "Mine" + LensCatalog.FileExtension)), catalog.Status);
        Assert.Equal("Mine", catalog.SelectedVendor);

        catalog.ReportCommand.Execute(null);
        Assert.Contains("Sorted by effective focal length", catalog.Details);
        Assert.Contains("MEN-200", catalog.Details);
    }

    [Fact]
    public void ATemplate_LoadsAsTheWorkspaceLens()
    {
        var catalog = Optics.LensCatalog;
        catalog.TemplateFilter = "plano";
        Assert.Equal("plano-singlet", Assert.Single(catalog.Templates).Id);
        catalog.TemplateInputs.Single(r => r.Parameter.Name == "Glass").Value = "1.5";
        catalog.LoadTemplateCommand.Execute(null);
        var s = Optics.ToSettings().Surfaces!;
        Assert.Equal(4, s.Count);
        Assert.Equal(50, s[1].Radius!.Value, 12);   // (n − 1)·f with the default 100 mm
        Assert.Contains("loaded", catalog.Status);

        catalog.TemplateInputs.Single(r => r.Parameter.Name == "Thickness").Value = "x";
        catalog.LoadTemplateCommand.Execute(null);
        Assert.Contains("'Thickness'", catalog.Status);
    }
}
