using OpenSim.Pcb.Import;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// Copper layer numbering for sets that do not declare it. The filename fallback knows
/// only "top", "bottom" and "inner"; it used to hand the bottom layer order 99, and the
/// stackup z model then walked every integer from 1 to 99 — 97 layers and gaps that do
/// not exist, a 160 mm tall stack and a via barrel a hundred times too long.
/// </summary>
public class LayerOrderTests
{
    private const string Head = "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.8*%\n%ADD11C,0.3*%\n";

    // A pad at (5,5) with a trace to (15,5) on each layer.
    private const string Copper =
        Head + "D10*\nX5000000Y5000000D03*\nD11*\nX5000000Y5000000D02*\nX15000000Y5000000D01*\nM02*";

    private const string Drill = "M48\nMETRIC\nT1C0.3\n%\nT1\nX5.0Y5.0\nM30";

    private static PcbBoard Read(params (string Name, string Text)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), "opensim-layerorder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (name, text) in files) File.WriteAllText(Path.Combine(dir, name), text);
            return new PcbBoardReader().Read(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TopAndBottomByName_AreLayersOneAndTwo()
    {
        var board = Read(("top_copper.gbr", Copper), ("bottom_copper.gbr", Copper), ("drill_PTH.drl", Drill));

        Assert.Equal(new[] { 1, 2 }, board.Islands.Select(i => i.LayerOrder).Distinct().OrderBy(l => l));
        Assert.Equal(new[] { 1, 2 }, board.Layers.Where(l => l.CopperOrder > 0).Select(l => l.CopperOrder).OrderBy(l => l));
        Assert.Equal(1, board.Layers.Single(l => l.FileName == "top_copper.gbr").CopperOrder);
        Assert.Equal(2, board.Layers.Single(l => l.FileName == "bottom_copper.gbr").CopperOrder);
        Assert.Contains(board.Warnings, w => w.Contains("L1 = top_copper.gbr") && w.Contains("L2 = bottom_copper.gbr"));

        // The via joins L1 and L2 and its barrel is one gap long.
        var net = Assert.Single(board.Nets);
        var bridge = Assert.Single(net.StitchingVias);
        Assert.Equal(new[] { 1, 2 }, bridge.Layers);
        var options = new NetMeshOptions { CopperThickness = 35e-6, DefaultDielectricThickness = 1.6e-3 };
        var (layerZ, gapZ) = NetMesher.BuildStackupZ(bridge.Layers.Min(), bridge.Layers.Max(), options);
        Assert.Equal(2, layerZ.Count);
        Assert.Single(gapZ);
        Assert.Equal(1.6e-3 + 2 * 35e-6, layerZ[1].zHi - layerZ[2].zLo, 12);
    }

    [Fact]
    public void ProtelExtensions_ClassifyCopper_AndInnerLayersLieBetweenTopAndBottom()
    {
        Assert.Equal(GerberLayerType.CopperSignal, GerberLayerClassifier.ClassifyByName("board.gtl").Type);
        Assert.True(GerberLayerClassifier.ClassifyByName("board.GTL").IsTopSide);
        Assert.Equal(GerberLayerType.CopperSignal, GerberLayerClassifier.ClassifyByName("board.gbl").Type);
        Assert.Equal(GerberLayerType.CopperSignal, GerberLayerClassifier.ClassifyByName("board.g1").Type);
        Assert.Equal(GerberLayerType.CopperPlane, GerberLayerClassifier.ClassifyByName("board.gp1").Type);
        Assert.Equal(GerberLayerType.Unknown, GerberLayerClassifier.ClassifyByName("board.gm1").Type);

        var board = Read(("board.gbl", Copper), ("board.g1", Copper), ("board.gtl", Copper), ("board.drl", Drill));

        Assert.Equal(1, board.Layers.Single(l => l.FileName == "board.gtl").CopperOrder);
        Assert.Equal(2, board.Layers.Single(l => l.FileName == "board.g1").CopperOrder);
        Assert.Equal(3, board.Layers.Single(l => l.FileName == "board.gbl").CopperOrder);
        Assert.Equal(new[] { 1, 2, 3 }, board.Islands.Select(i => i.LayerOrder).Distinct().OrderBy(l => l));
        Assert.Contains(board.Warnings, w => w.Contains("board.g1") && w.Contains("not stated"));
    }

    [Fact]
    public void DeclaredOrders_AreKept()
    {
        static string X2(int layer, string side) =>
            $"%TF.FileFunction,Copper,L{layer},{side},Signal*%\n" + Copper;

        // L1, L2 and L4 present, L3's file missing: the declared numbers are physical and stay.
        var board = Read(("a.gbr", X2(4, "Bot")), ("b.gbr", X2(1, "Top")), ("c.gbr", X2(2, "Inr")));

        Assert.Equal(new[] { 1, 2, 4 }, board.Islands.Select(i => i.LayerOrder).Distinct().OrderBy(l => l));
        Assert.DoesNotContain(board.Warnings, w => w.Contains("taken from file names"));
    }
}
