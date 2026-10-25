using System.IO;
using System.Windows.Media.Media3D;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Pcb.Import;

namespace OpenSim.App.Tests.ViewModels;

/// <summary>
/// The stackup panel's plumbing in the view model itself — the gate the capability plan's
/// Fix 3 asked for and could not have while no test project referenced the app. The pure
/// functions behind the rows are pinned in <c>OpenSim.Tests/Pcb/StackupPanelTests.cs</c>;
/// what is proved here is that the observable rows, the two boxes and the write-through
/// between them feed those functions, and that every consumer the view model hands a
/// stackup to — the mesh options (mesher, chain builder, SI, antenna), the persisted
/// settings (project, board thermal mesher), the spacing check and the copper preview's
/// z — reads the same edited row.
/// </summary>
public sealed class PcbViewModelStackupTests : IDisposable
{
    private readonly AppComposition _app = new();

    public void Dispose() => _app.Dispose();

    private PcbViewModel Pcb => _app.Resolve<PcbViewModel>();

    private static readonly PcbStackupSettings FourLayerFile = new()
    {
        CopperThickness = 35e-6,
        BoardThickness = 1.4e-3,
        CopperLayerThicknesses = new[] { 70e-6, 35e-6, 35e-6, 70e-6 },
        DielectricGapThicknesses = new[] { 0.1e-3, 1.2e-3, 0.1e-3 },
        DielectricGapPermittivities = new[] { 3.8, 4.5, 3.8 },
        DielectricGapLossTangents = new[] { 0.01, 0.02, 0.01 },
    };

    [Fact]
    public void GerberBoard_SeedsRowsFromTheBoxes_AndTheCopperBoxWritesThroughToEveryConsumer()
    {
        var pcb = Pcb;
        pcb.InstallBoard(Boards.Layers(2), "two-layer");

        Assert.True(_app.Resolve<OpenSim.App.Services.ProjectSession>().IsPcbMode);
        Assert.Collection(pcb.LayerFilters,
            l => { Assert.Equal(1, l.LayerOrder); Assert.Equal("L1", l.Label); Assert.Equal(35.0, l.ThicknessMicrons, 9); },
            l => { Assert.Equal(2, l.LayerOrder); Assert.Equal("L2", l.Label); Assert.Equal(35.0, l.ThicknessMicrons, 9); });
        var gap = Assert.Single(pcb.DielectricGaps);
        Assert.Equal(1, gap.UpperLayerOrder);
        Assert.Equal(1600.0, gap.ThicknessMicrons, 9);        // the 1.6 mm board box, one gap
        Assert.Equal(4.4, gap.RelativePermittivity, 12);
        Assert.Equal(0.02, gap.LossTangent, 12);

        // The board went to the five analyses that read the panel's stackup through the options builder.
        Assert.Equal(2, _app.Resolve<InductanceViewModel>().Nets.Count);
        Assert.Equal(2, _app.Resolve<AntennaViewModel>().Nets.Count);
        Assert.Equal(2, _app.Resolve<SignalIntegrityViewModel>().BoardNets.Count);
        Assert.Equal(2, _app.Resolve<MagneticsViewModel>().Nets.Count);
        Assert.Equal(2, _app.Resolve<EmiViewModel>().Nets.Count);
        Assert.Equal(1.6, _app.Resolve<AntennaViewModel>().HeightAboveGroundMm, 9);   // seeded from the board box

        // 2 oz in the copper box: every row follows, and so does every consumer.
        pcb.PcbCopperThickness = 70e-6;
        Assert.All(pcb.LayerFilters, l => Assert.Equal(70.0, l.ThicknessMicrons, 9));
        var options = pcb.BuildNetMeshOptions();
        Assert.Equal(70e-6, options.Stackup.CopperThicknessOf(1), 12);
        Assert.Equal(70e-6, options.Stackup.CopperThicknessOf(2), 12);
        AssertClose(new[] { 70e-6, 70e-6 }, pcb.BuildStackupSettings().CopperLayerThicknesses);

        // A row edited afterwards overrides its own layer only.
        pcb.LayerFilters[1].ThicknessMicrons = 18;
        options = pcb.BuildNetMeshOptions();
        Assert.Equal(70e-6, options.CopperThickness, 12);                 // the box, as the fallback
        Assert.Equal(70e-6, options.LayerThickness![1], 12);
        Assert.Equal(18e-6, options.LayerThickness[2], 12);
        var settings = pcb.BuildStackupSettings();
        Assert.Equal(70e-6, settings.CopperThickness, 12);
        AssertClose(new[] { 70e-6, 18e-6 }, settings.CopperLayerThicknesses);
    }

    [Fact]
    public void FileStackup_SeedsTheRows_AndTheRowsReproduceIt()
    {
        var pcb = Pcb;
        pcb.InstallBoard(Boards.Layers(4, FourLayerFile), "four-layer.cvg");

        Assert.Equal(1.4e-3, pcb.PcbBoardThickness, 12);                  // the board box took the file's thickness
        AssertClose(new[] { 70.0, 35.0, 35.0, 70.0 }, pcb.LayerFilters.Select(l => l.ThicknessMicrons));
        Assert.Equal("L1 · layer1.gbr", pcb.LayerFilters[0].Label);        // named, because the file has a stackup
        AssertClose(new[] { 100.0, 1200.0, 100.0 }, pcb.DielectricGaps.Select(g => g.ThicknessMicrons));
        AssertClose(new[] { 3.8, 4.5, 3.8 }, pcb.DielectricGaps.Select(g => g.RelativePermittivity));
        AssertClose(new[] { 0.01, 0.02, 0.01 }, pcb.DielectricGaps.Select(g => g.LossTangent));

        // Untouched rows persist as the file's own lists, at the same indices.
        var settings = pcb.BuildStackupSettings();
        AssertClose(FourLayerFile.CopperLayerThicknesses, settings.CopperLayerThicknesses);
        AssertClose(FourLayerFile.DielectricGapThicknesses, settings.DielectricGapThicknesses);
        AssertClose(FourLayerFile.DielectricGapPermittivities, settings.DielectricGapPermittivities);
        AssertClose(FourLayerFile.DielectricGapLossTangents, settings.DielectricGapLossTangents);
        Assert.Equal(1.4e-3, settings.BoardThickness, 12);

        var options = pcb.BuildNetMeshOptions();
        var stackup = options.Stackup;
        Assert.Equal(1.2e-3, stackup.GapThicknessOf(2), 12);
        Assert.Equal(3.8, stackup.PermittivityOf(3), 12);
        Assert.Equal(0.01, stackup.LossTangentOf(1), 12);
        Assert.Equal(70e-6, stackup.CopperThicknessOf(4), 12);

        // The antenna seeded its substrate from THIS board's stackup (the rows are seeded
        // before the hand-off), and the preview sits every layer where the mesher's z model
        // puts it — the board-box write during seeding must not leave a stale z-map behind.
        Assert.Equal(1.4, _app.Resolve<AntennaViewModel>().HeightAboveGroundMm, 9);
        var (layerZ, _) = NetMesher.BuildStackupZ(1, 4, options);
        for (int layer = 1; layer <= 4; layer++)
            Assert.Equal((layerZ[layer].zLo + layerZ[layer].zHi) / 2, LayerZ(pcb, layer), 12);
        Assert.Equal(1.4e-3 + 70e-6 + 35e-6 + 35e-6 + 35e-6, LayerZ(pcb, 1), 9);   // gaps + L4 (70) + L3 + L2 + half of L1
    }

    [Fact]
    public void AGapRowEdit_ReachesTheMeshOptions_TheSettings_TheThermalMesher_AndThePreviewZ()
    {
        var pcb = Pcb;
        pcb.InstallBoard(Boards.Layers(3), "three-layer");
        double zBefore = LayerZ(pcb, 1);

        var gap = pcb.DielectricGaps[0];                                  // L1–L2
        gap.ThicknessMicrons = 200;
        gap.RelativePermittivity = 3.5;
        gap.LossTangent = 0.004;

        // The one object the mesher, chain builder, SI, capacitance and antenna paths read.
        var options = pcb.BuildNetMeshOptions();
        Assert.Equal(200e-6, options.DielectricGapThickness![1], 12);
        Assert.Equal(800e-6, options.DielectricGapThickness[2], 12);      // the other gap keeps its even share
        Assert.Equal(3.5, options.Stackup.PermittivityOf(1), 12);
        Assert.Equal(0.004, options.Stackup.LossTangentOf(1), 12);
        Assert.Equal(4.4, options.Stackup.PermittivityOf(2), 12);

        // The persisted settings, indexed by (upper layer − 1).
        var settings = pcb.BuildStackupSettings();
        AssertClose(new[] { 200e-6, 800e-6 }, settings.DielectricGapThicknesses);
        AssertClose(new[] { 3.5, 4.4 }, settings.DielectricGapPermittivities);
        AssertClose(new[] { 0.004, 0.02 }, settings.DielectricGapLossTangents);

        // The board thermal mesher gets the same settings object through the rail's context.
        var (context, problem) = pcb.RailBoardThermal(traceDirections: false, withParts: false);
        Assert.True(problem is null, problem);
        var thermal = context!.Options.Stackup;
        Assert.Equal(settings.CopperThickness, thermal.CopperThickness, 12);
        Assert.Equal(settings.BoardThickness, thermal.BoardThickness, 12);
        AssertClose(settings.CopperLayerThicknesses, thermal.CopperLayerThicknesses);
        AssertClose(settings.DielectricGapThicknesses, thermal.DielectricGapThicknesses);
        AssertClose(settings.DielectricGapPermittivities, thermal.DielectricGapPermittivities);
        AssertClose(settings.DielectricGapLossTangents, thermal.DielectricGapLossTangents);

        // The copper preview re-sat its layers: L1's z moved by the same amount the mesher's
        // own z model says the thinner gap moves it (the preview recomposes synchronously
        // when there is no dispatcher, as here).
        var (layerZ, _) = NetMesher.BuildStackupZ(1, 3, options);
        double expected = (layerZ[1].zLo + layerZ[1].zHi) / 2;
        Assert.Equal(expected, LayerZ(pcb, 1), 12);
        Assert.NotEqual(zBefore, LayerZ(pcb, 1));
        Assert.Equal(600e-6, zBefore - expected, 9);                     // the L1–L2 gap went 0.8 mm → 0.2 mm
    }

    [Fact]
    public void SpacingCheck_ReadsThePanelsStackup_NotTheBoards()
    {
        var pcb = Pcb;
        pcb.InstallBoard(Boards.Layers(2), "two-layer");
        // Boxes away from the defaults BoardStackup.FromBoard would carry (35 µm, 1.6 mm/gaps).
        pcb.PcbCopperThickness = 50e-6;
        pcb.PcbBoardThickness = 2.0e-3;
        pcb.LayerFilters[0].ThicknessMicrons = 105;
        pcb.DielectricGaps[0].ThicknessMicrons = 300;
        pcb.DielectricGaps[0].LossTangent = 0.0009;

        var stackup = pcb.BuildBoardStackup();
        Assert.Equal("from the stackup panel", stackup.Source);
        Assert.Equal(105e-6, stackup.CopperThicknessOf(1), 12);
        Assert.Equal(50e-6, stackup.CopperThicknessOf(2), 12);
        Assert.Equal(50e-6, stackup.DefaultCopperThickness, 12);
        Assert.Equal(2.0e-3, stackup.DefaultGapThickness, 12);
        Assert.Equal(300e-6, stackup.GapThicknessOf(1), 12);
        Assert.Equal(0.0009, stackup.LossTangentOf(1), 12);
        var shared = pcb.BuildNetMeshOptions().Stackup;
        Assert.Equal(shared.DefaultCopperThickness, stackup.DefaultCopperThickness, 12);
        Assert.Equal(shared.DefaultGapThickness, stackup.DefaultGapThickness, 12);
        for (int layer = 1; layer <= 2; layer++)
            Assert.Equal(shared.CopperThicknessOf(layer), stackup.CopperThicknessOf(layer), 12);
        Assert.Equal(shared.GapThicknessOf(1), stackup.GapThicknessOf(1), 12);
        Assert.Equal(shared.PermittivityOf(1), stackup.PermittivityOf(1), 12);
        Assert.Equal(shared.LossTangentOf(1), stackup.LossTangentOf(1), 12);
    }

    [Fact]
    public void ReimportingAnotherBoard_ReplacesTheRows_AndKeepsTheBoxes()
    {
        var pcb = Pcb;
        pcb.InstallBoard(Boards.Layers(4, FourLayerFile), "four-layer.cvg");
        Assert.Equal(4, pcb.LayerFilters.Count);
        Assert.Equal(1.4e-3, pcb.PcbBoardThickness, 12);

        pcb.InstallBoard(Boards.Layers(2), "two-layer");
        Assert.Equal(2, pcb.LayerFilters.Count);
        var gap = Assert.Single(pcb.DielectricGaps);
        // A Gerber set has no stackup of its own: the board box stays as the user last had
        // it (the file's 1.4 mm), and the single gap takes all of it.
        Assert.Equal(1.4e-3, pcb.PcbBoardThickness, 12);
        Assert.Equal(1400.0, gap.ThicknessMicrons, 9);
        Assert.Equal(2, pcb.NetRows.Count);
    }

    /// <summary>What a project saved from a three-layer board's panel (TC-2).</summary>
    private static readonly PcbStackupSettings SavedThreeLayer = new()
    {
        CopperThickness = 50e-6,
        BoardThickness = 2.0e-3,
        CopperLayerThicknesses = new[] { 105e-6, 18e-6, 35e-6 },
        DielectricGapThicknesses = new[] { 0.3e-3, 1.5e-3 },
        DielectricGapPermittivities = new[] { 3.5, 4.6 },
        DielectricGapLossTangents = new[] { 0.004, 0.015 },
    };

    [Fact]
    public void AReopenedProject_RestoresTheRows_AtTheNextImport_AndOnlyOnce()
    {
        var pcb = Pcb;
        pcb.LoadStackupSettings(SavedThreeLayer);
        Assert.Equal(50e-6, pcb.PcbCopperThickness, 12);
        Assert.Equal(2.0e-3, pcb.PcbBoardThickness, 12);
        Assert.Empty(pcb.LayerFilters);                                      // the board is not persisted

        // A save before any re-import keeps the rows, with the boxes as they are now.
        pcb.PcbBoardThickness = 1.8e-3;
        var kept = pcb.BuildStackupSettings();
        Assert.Equal(1.8e-3, kept.BoardThickness, 12);
        Assert.Equal(50e-6, kept.CopperThickness, 12);
        AssertClose(SavedThreeLayer.CopperLayerThicknesses, kept.CopperLayerThicknesses);
        AssertClose(SavedThreeLayer.DielectricGapThicknesses, kept.DielectricGapThicknesses);
        AssertClose(SavedThreeLayer.DielectricGapLossTangents, kept.DielectricGapLossTangents);

        // The re-import of a Gerber set (no stackup of its own) shows the saved rows; the box stays.
        pcb.InstallBoard(Boards.Layers(3), "three-layer");
        AssertClose(new[] { 105.0, 18.0, 35.0 }, pcb.LayerFilters.Select(l => l.ThicknessMicrons));
        AssertClose(new[] { 300.0, 1500.0 }, pcb.DielectricGaps.Select(g => g.ThicknessMicrons));
        AssertClose(new[] { 3.5, 4.6 }, pcb.DielectricGaps.Select(g => g.RelativePermittivity));
        AssertClose(new[] { 0.004, 0.015 }, pcb.DielectricGaps.Select(g => g.LossTangent));
        Assert.Equal(1.8e-3, pcb.PcbBoardThickness, 12);
        var options = pcb.BuildNetMeshOptions();                             // and every consumer reads them
        Assert.Equal(105e-6, options.Stackup.CopperThicknessOf(1), 12);
        Assert.Equal(1.5e-3, options.Stackup.GapThicknessOf(2), 12);
        AssertClose(new[] { 105e-6, 18e-6, 35e-6 }, pcb.BuildStackupSettings().CopperLayerThicknesses);

        // Consumed: the next board seeds from the boxes again, as before.
        pcb.InstallBoard(Boards.Layers(2), "two-layer");
        Assert.All(pcb.LayerFilters, l => Assert.Equal(50.0, l.ThicknessMicrons, 9));
        Assert.Equal(1800.0, Assert.Single(pcb.DielectricGaps).ThicknessMicrons, 9);
    }

    [Fact]
    public void AFileStackup_StillWins_OverAReopenedProjectsRows()
    {
        var pcb = Pcb;
        pcb.LoadStackupSettings(SavedThreeLayer);
        pcb.InstallBoard(Boards.Layers(4, FourLayerFile), "four-layer.cvg");

        AssertClose(new[] { 70.0, 35.0, 35.0, 70.0 }, pcb.LayerFilters.Select(l => l.ThicknessMicrons));
        AssertClose(new[] { 100.0, 1200.0, 100.0 }, pcb.DielectricGaps.Select(g => g.ThicknessMicrons));
        Assert.Equal(1.4e-3, pcb.PcbBoardThickness, 12);
    }

    [Fact]
    public void SaveThenReopen_RoundTripsTheEditedRows_ThroughTheProjectFile()
    {
        var main = _app.Resolve<MainViewModel>();
        var session = _app.Resolve<OpenSim.App.Services.ProjectSession>();
        var pcb = Pcb;
        pcb.InstallBoard(Boards.Layers(3), "three-layer");
        pcb.PcbCopperThickness = 50e-6;
        pcb.LayerFilters[0].ThicknessMicrons = 105;
        pcb.DielectricGaps[1].ThicknessMicrons = 1500;
        pcb.DielectricGaps[1].RelativePermittivity = 4.6;
        pcb.DielectricGaps[1].LossTangent = 0.015;

        string path = Path.Combine(_app.UserDirectory, "round-trip.ossproj");
        // Save: the panel's boxes and rows go into the project (what SaveProject writes before the serializer).
        main.CaptureProjectSettings();
        AssertClose(new[] { 105e-6, 50e-6, 50e-6 }, session.Project.Stackup!.CopperLayerThicknesses);
        AssertClose(new[] { 800e-6, 1500e-6 }, session.Project.Stackup.DielectricGapThicknesses);
        _app.Resolve<ProjectSerializer>().Save(session.Project, path);

        // Meanwhile the boxes move on, so the restore below is seen to come from the file.
        pcb.PcbCopperThickness = 35e-6;
        pcb.PcbBoardThickness = 2.2e-3;

        // Reopen: the board is torn down (it is not in the file), the boxes come back at once.
        main.LoadProjectFromPath(path);
        Assert.Contains(_app.Log.Entries, e => e.StartsWith("Project loaded from"));
        Assert.False(session.IsPcbMode);
        Assert.Empty(pcb.LayerFilters);
        Assert.Equal(50e-6, pcb.PcbCopperThickness, 12);
        Assert.Equal(1.6e-3, pcb.PcbBoardThickness, 12);

        // Re-import the same board: the edited rows are back, and the log says so.
        pcb.InstallBoard(Boards.Layers(3), "three-layer");
        AssertClose(new[] { 105.0, 50.0, 50.0 }, pcb.LayerFilters.Select(l => l.ThicknessMicrons));
        AssertClose(new[] { 800.0, 1500.0 }, pcb.DielectricGaps.Select(g => g.ThicknessMicrons));
        AssertClose(new[] { 4.4, 4.6 }, pcb.DielectricGaps.Select(g => g.RelativePermittivity));
        AssertClose(new[] { 0.02, 0.015 }, pcb.DielectricGaps.Select(g => g.LossTangent));
        Assert.Contains("Stackup: 3 layer rows and 2 gap rows restored from the project.", _app.Log.Entries);
    }

    [Fact]
    public void ACopperBoxEdit_WhileTheSavedRowsWait_SetsEveryOneOfThem()
    {
        // The box's rule with a board loaded — it sets EVERY layer — holds for the rows a
        // reopened project has waiting, so the re-import cannot bring back the saved copper
        // over a value typed into the box.
        var pcb = Pcb;
        pcb.LoadStackupSettings(SavedThreeLayer);
        pcb.PcbCopperThickness = 70e-6;

        var kept = pcb.BuildStackupSettings();                               // a save before the re-import
        Assert.Equal(70e-6, kept.CopperThickness, 12);
        AssertClose(new[] { 70e-6, 70e-6, 70e-6 }, kept.CopperLayerThicknesses);
        AssertClose(SavedThreeLayer.DielectricGapThicknesses, kept.DielectricGapThicknesses);

        pcb.InstallBoard(Boards.Layers(3), "three-layer");                   // the re-import
        Assert.All(pcb.LayerFilters, l => Assert.Equal(70.0, l.ThicknessMicrons, 9));
        AssertClose(new[] { 300.0, 1500.0 }, pcb.DielectricGaps.Select(g => g.ThicknessMicrons));
    }

    [Fact]
    public void TheSavedRows_AreForgotten_WhenTheBoardWorkflowIsLeft_OrAnotherProjectHasNone()
    {
        var pcb = Pcb;
        var session = _app.Resolve<OpenSim.App.Services.ProjectSession>();

        // A primitive, an STL or a STEP import leaves PCB mode: the waiting rows go with it.
        pcb.LoadStackupSettings(SavedThreeLayer);
        session.RaiseGeometryReplaced(leavingPcbMode: true);
        Assert.Empty(pcb.BuildStackupSettings().CopperLayerThicknesses);
        Assert.Equal(50e-6, pcb.PcbCopperThickness, 12);                     // the boxes stay
        pcb.InstallBoard(Boards.Layers(3), "three-layer");
        Assert.All(pcb.LayerFilters, l => Assert.Equal(50.0, l.ThicknessMicrons, 9));
        Assert.DoesNotContain(_app.Log.Entries, e => e.Contains("restored from the project"));

        // Opening a project that saved no rows forgets them too, and takes that project's boxes
        // (a project open tears the board down first, as LoadProjectFromPath does).
        session.RaiseGeometryReplaced(leavingPcbMode: true);
        pcb.LoadStackupSettings(SavedThreeLayer);
        pcb.LoadStackupSettings(new PcbStackupSettings { CopperThickness = 20e-6, BoardThickness = 1.0e-3 });
        Assert.Empty(pcb.BuildStackupSettings().DielectricGapThicknesses);
        Assert.Equal(20e-6, pcb.PcbCopperThickness, 12);
        Assert.Equal(1.0e-3, pcb.PcbBoardThickness, 12);

        // An older file with no stackup at all: the rows are forgotten, the boxes stay as they are.
        pcb.LoadStackupSettings(SavedThreeLayer);
        pcb.LoadStackupSettings(null);
        Assert.Empty(pcb.BuildStackupSettings().CopperLayerThicknesses);
        Assert.Equal(50e-6, pcb.PcbCopperThickness, 12);
        Assert.Equal(2.0e-3, pcb.PcbBoardThickness, 12);

        // New project: the default boxes, no rows.
        pcb.LoadStackupSettings(SavedThreeLayer);
        _app.Resolve<MainViewModel>().NewProjectCommand.Execute(null);
        Assert.Equal(35e-6, pcb.PcbCopperThickness, 12);
        Assert.Equal(1.6e-3, pcb.PcbBoardThickness, 12);
        Assert.Empty(pcb.BuildStackupSettings().CopperLayerThicknesses);
        pcb.InstallBoard(Boards.Layers(2), "two-layer");
        Assert.All(pcb.LayerFilters, l => Assert.Equal(35.0, l.ThicknessMicrons, 9));
        Assert.Equal(1600.0, Assert.Single(pcb.DielectricGaps).ThicknessMicrons, 9);
    }

    [Fact]
    public void AnImportWithItsOwnStackup_UsesTheSavedRowsUp_AndADifferentLayerCountIsLogged()
    {
        var pcb = Pcb;

        var session = _app.Resolve<OpenSim.App.Services.ProjectSession>();

        // An IPC-2581 board after the reopen: its file wins, the saved rows are spent, the log says so.
        pcb.LoadStackupSettings(SavedThreeLayer);
        pcb.InstallBoard(Boards.Layers(4, FourLayerFile), "four-layer.cvg");
        Assert.Contains("Stackup: the board file's own stackup replaces the rows the project saved.", _app.Log.Entries);
        pcb.InstallBoard(Boards.Layers(2), "two-layer");
        Assert.All(pcb.LayerFilters, l => Assert.Equal(50.0, l.ThicknessMicrons, 9));   // the copper box
        Assert.Equal(1400.0, Assert.Single(pcb.DielectricGaps).ThicknessMicrons, 9);  // the board box, the file's 1.4 mm
        Assert.DoesNotContain(_app.Log.Entries, e => e.Contains("restored from the project"));

        // A board with more layers than the rows were saved for: the rows follow layer
        // order and the log says what happened (a project open tears the board down first).
        session.RaiseGeometryReplaced(leavingPcbMode: true);
        pcb.LoadStackupSettings(SavedThreeLayer);
        pcb.InstallBoard(Boards.Layers(4), "four-layer");
        AssertClose(new[] { 105.0, 18.0, 35.0, 50.0 }, pcb.LayerFilters.Select(l => l.ThicknessMicrons));
        Assert.Contains(_app.Log.Entries, e => e.StartsWith("Stackup: the rows restored from the project were saved for 3 copper layers and this board has 4. "
            + "The rows follow layer order (L1 first); layers and gaps past L3 take the boxes."));

        // And fewer: the saved rows past the board are dropped, and the log says that instead.
        session.RaiseGeometryReplaced(leavingPcbMode: true);
        pcb.LoadStackupSettings(SavedThreeLayer);
        pcb.InstallBoard(Boards.Layers(2), "two-layer");
        AssertClose(new[] { 105.0, 18.0 }, pcb.LayerFilters.Select(l => l.ThicknessMicrons));
        Assert.Contains(_app.Log.Entries, e => e.Contains("this board has 2. The rows follow layer order (L1 first); the saved rows past L2 are dropped."));
    }

    /// <summary>Element-wise equality to 12 places: the rows hold microns, so a value that
    /// went metres → microns → metres lands within 1e-19 m of the literal.</summary>
    private static void AssertClose(IEnumerable<double> expected, IEnumerable<double> actual)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        Assert.Equal(e.Count, a.Count);
        for (int i = 0; i < e.Count; i++) Assert.Equal(e[i], a[i], 12);
    }

    /// <summary>The z the preview draws copper layer <paramref name="order"/> at: the
    /// translate transform of its layer group in the frozen preview tree.</summary>
    private static double LayerZ(PcbViewModel pcb, int order)
    {
        var filter = pcb.LayerFilters.First(f => f.LayerOrder == order);
        int index = pcb.LayerFilters.Where(f => f.Enabled).ToList().IndexOf(filter);
        var group = (Model3DGroup)pcb.CopperPreviewModel.Children[index];
        return ((TranslateTransform3D)group.Transform).OffsetZ;
    }
}
