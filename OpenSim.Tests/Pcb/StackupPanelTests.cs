using OpenSim.Core.Geometry2D;
using OpenSim.Core.Model;
using OpenSim.Pcb.Import;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The stackup panel's plumbing as pure functions (<see cref="StackupPanel"/>): how a board
/// seeds the rows, how the rows become the mesh options every analysis reads and the
/// settings the project persists, and that the two agree with each other and with the
/// seeder's index contract. The view model's side of it is in
/// <c>OpenSim.App.Tests/ViewModels/PcbViewModelStackupTests.cs</c>.
/// </summary>
public class StackupPanelTests
{
    private static Polygon2 Rect(double half) =>
        new(new[] { new Point2(-half, -half), new Point2(half, -half), new Point2(half, half), new Point2(-half, half) });

    /// <summary>A board with one island on each of the given copper layers, and optionally
    /// named copper layers (as an IPC-2581 or attribute-carrying Gerber set has).</summary>
    private static PcbBoard Board(int[] islandLayers, PcbStackupSettings? stackup = null,
        params (int Order, string Name)[] namedLayers)
    {
        var islands = islandLayers.Select((l, i) => new CopperIsland(i, l, $"L{l}", Rect(10e-3))).ToList();
        return new PcbBoard
        {
            Outline = Array.Empty<Polygon2>(),
            Islands = islands,
            Pads = Array.Empty<CopperPad>(),
            Vias = Array.Empty<Via>(),
            Nets = islands.Select(i => new CopperNet(i.Index + 1, new[] { i })).ToList(),
            Layers = namedLayers.Select(n => new BoardLayer(n.Name, GerberLayerType.CopperSignal, n.Order, n.Order == 1)).ToList(),
            Warnings = Array.Empty<string>(),
            Stackup = stackup,
        };
    }

    private static readonly PcbStackupSettings FourLayerFile = new()
    {
        CopperThickness = 35e-6,
        BoardThickness = 1.4e-3,
        CopperLayerThicknesses = new[] { 70e-6, 35e-6, 35e-6, 70e-6 },
        DielectricGapThicknesses = new[] { 0.1e-3, 1.2e-3, 0.1e-3 },
        DielectricGapPermittivities = new[] { 3.8, 4.5, 3.8 },
        DielectricGapLossTangents = new[] { 0.01, 0.02, 0.01 },
    };

    // ------------------------------------------------------------------ Seed

    [Fact]
    public void Seed_GerberBoard_TakesTheBoxes_AndSplitsTheBoardEvenly()
    {
        // 2 oz in the copper box — not the 35 µm the fallback would give.
        var seed = StackupPanel.Seed(Board(new[] { 1, 2, 3 }), copperThickness: 70e-6, boardThickness: 1.6e-3);

        Assert.Equal(1.6e-3, seed.BoardThickness, 12);
        Assert.Equal(new[] { 1, 2, 3 }, seed.Layers.Select(l => l.LayerOrder));
        Assert.All(seed.Layers, l => { Assert.Equal(70e-6, l.ThicknessMeters, 12); Assert.Null(l.Name); });
        Assert.Collection(seed.Gaps,
            g => { Assert.Equal(1, g.UpperLayerOrder); Assert.Equal(0.8e-3, g.ThicknessMeters, 12); },
            g => { Assert.Equal(2, g.UpperLayerOrder); Assert.Equal(0.8e-3, g.ThicknessMeters, 12); });
        Assert.All(seed.Gaps, g => { Assert.Equal(4.4, g.RelativePermittivity, 12); Assert.Equal(0.02, g.LossTangent, 12); });
    }

    [Fact]
    public void Seed_GerberBoard_NamesLayersOnlyWhenTheFileHasAStackup()
    {
        var gerber = Board(new[] { 1, 2 }, null, (1, "top.gbr"), (2, "bottom.gbr"));
        Assert.All(StackupPanel.Seed(gerber, 35e-6, 1.6e-3).Layers, l => Assert.Null(l.Name));

        var ipc = Board(new[] { 1, 2 }, new PcbStackupSettings(), (1, "Top Layer"), (2, "Bottom Layer"));
        Assert.Equal(new[] { "Top Layer", "Bottom Layer" }, StackupPanel.Seed(ipc, 35e-6, 1.6e-3).Layers.Select(l => l.Name));
    }

    [Fact]
    public void Seed_AnEmptyCopperBox_FallsBackTo35Microns()
    {
        var seed = StackupPanel.Seed(Board(new[] { 1, 2 }), copperThickness: 0, boardThickness: 1.6e-3);
        Assert.All(seed.Layers, l => Assert.Equal(35e-6, l.ThicknessMeters, 12));
    }

    [Fact]
    public void Seed_ALayerWithNoCopper_StillGetsARowAndItsGaps()
    {
        // Copper on L1 and L3 only; the file declares L2. Without a row for L2 and gaps on
        // both sides of it, the z model would put one whole-board gap between L1 and L3.
        var board = Board(new[] { 1, 3 }, null, (1, "l1.gbr"), (2, "l2.gbr"), (3, "l3.gbr"));
        var seed = StackupPanel.Seed(board, 35e-6, 1.6e-3);

        Assert.Equal(new[] { 1, 2, 3 }, seed.Layers.Select(l => l.LayerOrder));
        Assert.Equal(new[] { 1, 2 }, seed.Gaps.Select(g => g.UpperLayerOrder));
        Assert.All(seed.Gaps, g => Assert.Equal(0.8e-3, g.ThicknessMeters, 12));
    }

    [Fact]
    public void Seed_FileStackup_IsReadBackAtTheSameIndex_AndSetsTheBoardBox()
    {
        var seed = StackupPanel.Seed(Board(new[] { 1, 2, 3, 4 }, FourLayerFile), copperThickness: 18e-6, boardThickness: 1.6e-3);

        Assert.Equal(1.4e-3, seed.BoardThickness, 12);                                   // the file's, not the box
        Assert.Equal(FourLayerFile.CopperLayerThicknesses, seed.Layers.Select(l => l.ThicknessMeters));
        Assert.Equal(FourLayerFile.DielectricGapThicknesses, seed.Gaps.Select(g => g.ThicknessMeters));
        Assert.Equal(FourLayerFile.DielectricGapPermittivities, seed.Gaps.Select(g => g.RelativePermittivity));
        Assert.Equal(FourLayerFile.DielectricGapLossTangents, seed.Gaps.Select(g => g.LossTangent));
    }

    [Fact]
    public void Seed_FileStackupShorterThanTheBoard_CompletesFromTheBoxesAndFr4()
    {
        // The file lists copper for two layers and one gap; the board has three copper layers.
        var file = new PcbStackupSettings
        {
            BoardThickness = 1.0e-3,
            CopperLayerThicknesses = new[] { 70e-6, 18e-6 },
            DielectricGapThicknesses = new[] { 0.3e-3 },
            DielectricGapPermittivities = new[] { 3.0 },
        };
        var seed = StackupPanel.Seed(Board(new[] { 1, 2, 3 }, file), copperThickness: 105e-6, boardThickness: 1.6e-3);

        Assert.Equal(new[] { 70e-6, 18e-6, 105e-6 }, seed.Layers.Select(l => l.ThicknessMeters));   // L3: the box
        Assert.Equal(1.0e-3, seed.BoardThickness, 12);
        Assert.Collection(seed.Gaps,
            g => { Assert.Equal(0.3e-3, g.ThicknessMeters, 12); Assert.Equal(3.0, g.RelativePermittivity, 12); Assert.Equal(0.02, g.LossTangent, 12); },
            g => { Assert.Equal(0.5e-3, g.ThicknessMeters, 12); Assert.Equal(4.4, g.RelativePermittivity, 12); Assert.Equal(0.02, g.LossTangent, 12); });
    }

    // ------------------------------------------------------------------ ToSettings

    [Fact]
    public void ToSettings_NoRows_KeepsOnlyTheBoxes()
    {
        var s = StackupPanel.ToSettings(50e-6, 2.0e-3, Array.Empty<PanelCopperRow>(), Array.Empty<PanelGapRow>());
        Assert.Equal(50e-6, s.CopperThickness, 12);
        Assert.Equal(2.0e-3, s.BoardThickness, 12);
        Assert.Empty(s.CopperLayerThicknesses);
        Assert.Empty(s.DielectricGapThicknesses);
        Assert.Empty(s.DielectricGapPermittivities);
        Assert.Empty(s.DielectricGapLossTangents);
    }

    [Fact]
    public void ToSettings_CopperRows_AreListedByOrder_WithTheBoxForAMissingOrder()
    {
        var layers = new[] { new PanelCopperRow(1, 70e-6), new PanelCopperRow(3, 18e-6) };
        var s = StackupPanel.ToSettings(35e-6, 1.6e-3, layers, Array.Empty<PanelGapRow>());
        Assert.Equal(new[] { 70e-6, 35e-6, 18e-6 }, s.CopperLayerThicknesses);
    }

    [Fact]
    public void ToSettings_GapRows_AreListedByUpperLayer_WithAnEvenShareAndFr4ForAMissingOrder()
    {
        var gaps = new[] { new PanelGapRow(1, 0.2e-3, 3.5, 0.004), new PanelGapRow(3, 0.1e-3, 3.0, 0.001) };
        var s = StackupPanel.ToSettings(35e-6, 1.5e-3, Array.Empty<PanelCopperRow>(), gaps);
        Assert.Equal(new[] { 0.2e-3, 0.5e-3, 0.1e-3 }, s.DielectricGapThicknesses.Select(t => Math.Round(t, 12)));
        Assert.Equal(new[] { 3.5, 4.4, 3.0 }, s.DielectricGapPermittivities);
        Assert.Equal(new[] { 0.004, 0.02, 0.001 }, s.DielectricGapLossTangents);
    }

    [Fact]
    public void ToSettings_RoundTripsThroughSeed()
    {
        var layers = new[] { new PanelCopperRow(1, 70e-6), new PanelCopperRow(2, 18e-6), new PanelCopperRow(3, 35e-6) };
        var gaps = new[] { new PanelGapRow(1, 0.2e-3, 3.5, 0.004), new PanelGapRow(2, 1.0e-3, 4.6, 0.018) };
        var settings = StackupPanel.ToSettings(35e-6, 1.2e-3, layers, gaps);

        // A board that carries exactly these settings (as a saved project or an IPC-2581 file
        // would) seeds exactly these rows, at the same orders.
        var seed = StackupPanel.Seed(Board(new[] { 1, 2, 3 }, settings), copperThickness: 35e-6, boardThickness: 9.9e-3);
        Assert.Equal(1.2e-3, seed.BoardThickness, 12);
        Assert.Equal(layers, seed.Layers);
        Assert.Equal(gaps, seed.Gaps);
    }

    // ------------------------------------------------------------------ ToMeshOptions

    [Fact]
    public void ToMeshOptions_CarriesEveryRowAndBothBoxes()
    {
        var layers = new[] { new PanelCopperRow(1, 70e-6), new PanelCopperRow(2, 18e-6) };
        var gaps = new[] { new PanelGapRow(1, 0.2e-3, 3.5, 0.004) };
        // Boxes away from NetMeshOptions' own defaults (35 µm, 1.6 mm), so a dropped box shows.
        var o = StackupPanel.ToMeshOptions(50e-6, 2.2e-3, layers, gaps, targetEdgeLength: 0.5e-3, viaPlatingThickness: 20e-6);

        Assert.Equal(0.5e-3, o.TargetEdgeLength, 12);
        Assert.Equal(20e-6, o.ViaPlatingThickness, 12);
        Assert.Equal(50e-6, o.CopperThickness, 12);
        Assert.Equal(2.2e-3, o.DefaultDielectricThickness, 12);
        Assert.Equal(70e-6, o.LayerThickness![1], 12);
        Assert.Equal(18e-6, o.LayerThickness[2], 12);
        Assert.Equal(0.2e-3, o.DielectricGapThickness![1], 12);
        Assert.Equal(3.5, o.DielectricGapPermittivity![1], 12);
        Assert.Equal(0.004, o.DielectricGapLossTangent![1], 12);

        // A layer or gap without a row reads the box through the shared stackup object.
        Assert.Equal(50e-6, o.Stackup.CopperThicknessOf(3), 12);
        Assert.Equal(2.2e-3, o.Stackup.GapThicknessOf(2), 12);
    }

    [Fact]
    public void ARowAtOrBelowZero_TakesTheSameFallbackInTheSettingsAsInTheSharedStackup()
    {
        // A blank or mistyped row. BoardStackup (what the mesher, SI and antenna read) falls
        // back to the box for copper and gap thickness and εr, and to FR4 for tanδ; the
        // persisted settings must say the same, since the thermal mesher takes them as they are.
        var layers = new[] { new PanelCopperRow(1, 0), new PanelCopperRow(2, -1e-6), new PanelCopperRow(3, 18e-6) };
        var gaps = new[] { new PanelGapRow(1, 0, 0, -0.5), new PanelGapRow(2, 0.3e-3, 3.0, 0) };

        var settings = StackupPanel.ToSettings(50e-6, 1.2e-3, layers, gaps);
        Assert.Equal(new[] { 50e-6, 50e-6, 18e-6 }, settings.CopperLayerThicknesses);
        Assert.Equal(new[] { 0.6e-3, 0.3e-3 }, settings.DielectricGapThicknesses.Select(t => Math.Round(t, 12)));
        Assert.Equal(new[] { 4.4, 3.0 }, settings.DielectricGapPermittivities);
        Assert.Equal(new[] { 0.02, 0.0 }, settings.DielectricGapLossTangents);

        var shared = StackupPanel.ToMeshOptions(50e-6, 1.2e-3, layers, gaps, 0, 25e-6).Stackup;
        Assert.Equal(50e-6, shared.CopperThicknessOf(1), 12);
        Assert.Equal(50e-6, shared.CopperThicknessOf(2), 12);
        Assert.Equal(4.4, shared.PermittivityOf(1), 12);
        Assert.Equal(0.02, shared.LossTangentOf(1), 12);
        Assert.Equal(0.0, shared.LossTangentOf(2), 12);
        // The one place the two differ by design: a row-less or zero gap is the whole board box in
        // the options (the mesher's long-standing default) and an even share in the settings.
        Assert.Equal(1.2e-3, shared.GapThicknessOf(1), 12);
    }

    [Fact]
    public void ToMeshOptions_AndToSettings_DescribeTheSameStackup()
    {
        // The same rows, read two ways: as the options' BoardStackup (what the mesher, SI and
        // antenna read) and as a board carrying the persisted settings (what the thermal
        // mesher and a library caller read). Every listed layer and gap agrees.
        var layers = new[] { new PanelCopperRow(1, 70e-6), new PanelCopperRow(2, 18e-6), new PanelCopperRow(3, 35e-6), new PanelCopperRow(4, 105e-6) };
        var gaps = new[] { new PanelGapRow(1, 0.2e-3, 3.5, 0.004), new PanelGapRow(2, 1.0e-3, 4.6, 0.018), new PanelGapRow(3, 0.15e-3, 3.2, 0.002) };

        var fromOptions = StackupPanel.ToMeshOptions(35e-6, 1.35e-3, layers, gaps, 0, 25e-6).Stackup;
        var fromSettings = BoardStackup.FromBoard(Board(new[] { 1, 2, 3, 4 }, StackupPanel.ToSettings(35e-6, 1.35e-3, layers, gaps)));

        for (int layer = 1; layer <= 4; layer++)
            Assert.Equal(fromOptions.CopperThicknessOf(layer), fromSettings.CopperThicknessOf(layer), 12);
        for (int gap = 1; gap <= 3; gap++)
        {
            Assert.Equal(fromOptions.GapThicknessOf(gap), fromSettings.GapThicknessOf(gap), 12);
            Assert.Equal(fromOptions.PermittivityOf(gap), fromSettings.PermittivityOf(gap), 12);
            Assert.Equal(fromOptions.LossTangentOf(gap), fromSettings.LossTangentOf(gap), 12);
        }
        Assert.Equal(fromOptions.TotalThickness(4), fromSettings.TotalThickness(4), 12);
    }

    // ------------------------------------------------------------------ Validation

    [Fact]
    public void RowsWithAnInvalidOrDuplicateOrder_AreRefused()
    {
        var none = Array.Empty<PanelGapRow>();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StackupPanel.ToSettings(35e-6, 1.6e-3, new[] { new PanelCopperRow(0, 35e-6) }, none));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StackupPanel.ToMeshOptions(35e-6, 1.6e-3, Array.Empty<PanelCopperRow>(), new[] { new PanelGapRow(0, 1e-3, 4.4, 0.02) }, 0, 25e-6));
        Assert.Throws<ArgumentException>(() =>
            StackupPanel.ToSettings(35e-6, 1.6e-3, new[] { new PanelCopperRow(2, 35e-6), new PanelCopperRow(2, 70e-6) }, none));
    }
}
