using System.IO;
using System.Numerics;
using System.Text.Json;
using OpenSim.App.Services;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Optics.Coatings;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>OP-15: the Optics coating catalog panel — editing, publishing, and its files.</summary>
public sealed class OpticsCoatingCatalogTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public void Edits_BuildTheCatalog_AndEachPublishesTheSystem()
    {
        int published = 0;
        var vm = new OpticsCoatingCatalogViewModel(() => published++);
        vm.NewName = "low index";
        vm.AddMaterialCommand.Execute(null);
        Assert.Equal("low_index", vm.SelectedMaterialName);
        vm.IndexRows[0].N = 1.38;
        vm.AddIndexRowCommand.Execute(null);
        Assert.Equal(2, vm.IndexRows.Count);
        vm.RemoveIndexRowCommand.Execute(vm.IndexRows[1]);
        Assert.Equal(new Complex(1.38, 0), vm.Catalog.FindMaterial("LOW_INDEX")!.Index(0.55));

        vm.NewName = "";
        vm.AddLayerCoatingCommand.Execute(null);
        Assert.True(vm.IsLayerCoatingSelected);
        vm.AddLayerCommand.Execute(null);
        Assert.Equal("low_index", vm.Layers[0].Material);       // the selected material
        vm.Layers[0].Thickness = 0.5;
        var coating = Assert.IsType<LayerCoating>(vm.SelectedCoating);
        Assert.Equal(0.5, coating.Layers[0].Thickness);
        Assert.Empty(vm.Problems);

        vm.NewName = "AR1";
        vm.RenameCoatingCommand.Execute(null);
        Assert.NotNull(vm.Catalog.FindCoating("AR1"));
        vm.NewName = "MGF2";
        vm.RenameMaterialCommand.Execute(null);
        Assert.Equal("MGF2", ((LayerCoating)vm.Catalog.FindCoating("AR1")!).Layers[0].Material);   // the layer follows the rename

        vm.AddIdealCoatingCommand.Execute(null);
        Assert.True(vm.IsIdealCoatingSelected);
        vm.IdealTransmission = 0.9;
        vm.IdealReflection = 0.05;
        Assert.Equal(0.05, ((IdealCoating)vm.SelectedCoating!).Reflection);
        Assert.True(published >= 10);

        // A layer naming a material the catalog lacks is listed as a problem.
        vm.SelectedCoatingName = "AR1";
        vm.Layers[0].Material = "NOPE";
        Assert.Contains("NOPE", vm.Problems);

        var copy = new OpticsCoatingCatalogViewModel(() => { });
        copy.LoadSettings(vm.ToSettings(), new List<string>());
        Assert.Equal(JsonSerializer.Serialize(vm.ToSettings()), JsonSerializer.Serialize(copy.ToSettings()));
        copy.Reset();
        Assert.Null(copy.ToSettings());
    }

    [Fact]
    public void Files_ImportAndExport_InBothFormats()
    {
        var vm = new OpticsCoatingCatalogViewModel(() => { });
        string dat = Path.Combine(_app.UserDirectory, "COATINGS.DAT");
        File.WriteAllText(dat, "MATE MGF2\n0.55 1.38 0\nCOAT QW\nMGF2 0.25\nIDEAL HALF 0.5 0.5\n");
        Assert.Contains("2 coatings", vm.ImportFile(dat));
        Assert.Equal(new[] { "QW", "HALF" }, vm.CoatingNames);

        string json = Path.Combine(_app.UserDirectory, "catalog" + CoatingFile.JsonExtension);
        vm.ExportFile(json);
        string back = Path.Combine(_app.UserDirectory, "back.dat");
        vm.ExportFile(back);
        var fromJson = new OpticsCoatingCatalogViewModel(() => { });
        fromJson.ImportFile(json);
        var fromDat = new OpticsCoatingCatalogViewModel(() => { });
        fromDat.ImportFile(back);
        Assert.Equal(JsonSerializer.Serialize(vm.ToSettings()), JsonSerializer.Serialize(fromJson.ToSettings()));
        Assert.Equal(JsonSerializer.Serialize(vm.ToSettings()), JsonSerializer.Serialize(fromDat.ToSettings()));

        File.WriteAllText(dat, "MATE BAD\n0.55 1.38 0.2\n");
        Assert.Contains("not read", vm.ImportFile(dat));
        Assert.Equal(2, vm.CoatingNames.Count);                    // nothing changed
    }

    [Fact]
    public void TheWorkspace_PublishesItsCoatings_ToTheAnalyses()
    {
        var optics = _app.Resolve<OpticsViewModel>();
        var session = _app.Resolve<ProjectSession>();
        optics.Coatings.AddMaterialCommand.Execute(null);
        optics.Coatings.AddLayerCoatingCommand.Execute(null);
        optics.Coatings.AddLayerCommand.Execute(null);
        Assert.Single(session.OpticsSystem!.Coatings!.Coatings!);
        Assert.Single(session.OpticsSystem.Coatings.Coatings![0].Layers!);
        optics.Reset();
        Assert.Null(session.OpticsSystem.Coatings);
    }
}
