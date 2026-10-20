using System.Windows.Media.Media3D;
using OpenSim.App.Tests.Support;
using OpenSim.App.ViewModels;
using OpenSim.Core.Model;
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
