using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Import;
using Xunit;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// FU-6 (audit PCB-16): pads that are not small flashes. A via pad the CAD wrote as a G36
/// region, or a through-hole pad flashed larger than <see cref="PadExtractor.PadSizeMax"/>,
/// used to produce no pad, so its plated drill stitched nothing and the net split in two.
/// A pour stays a pour.
/// </summary>
public class DrilledPadTests
{
    private const string Profile =
        "%TF.FileFunction,Profile,NP*%\n%FSLAX46Y46*%\n%MOMM*%\n" +
        "G36*\nX0Y0D02*\nX40000000Y0D01*\nX40000000Y10000000D01*\nX0Y10000000D01*\nX0Y0D01*\nG37*\nM02*";

    private static string Square(double cx, double cy, double side)
    {
        long X(double v) => (long)Math.Round(v * 1e6);
        double h = side / 2;
        return $"G36*\nX{X(cx - h)}Y{X(cy - h)}D02*\nX{X(cx + h)}Y{X(cy - h)}D01*\nX{X(cx + h)}Y{X(cy + h)}D01*\n" +
               $"X{X(cx - h)}Y{X(cy + h)}D01*\nX{X(cx - h)}Y{X(cy - h)}D01*\nG37*\n";
    }

    // A (5,5): a 1 mm region pad on top, a 0.8 mm flash below, a 0.3 mm via.
    // B (14,5): a 6 mm flashed pad on both layers round a 3.2 mm plated hole.
    // C (30,5): a 7 mm unnamed region (a pour) on top with a 0.3 mm via at its centre.
    // D (22,5): an SMD pad written as a region, named pin 3 of U1.
    private static readonly string Top =
        "%TF.FileFunction,Copper,L1,Top,Signal*%\n%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,6.0*%\n" +
        Square(5, 5, 1.0) + "D10*\nX14000000Y5000000D03*\n" + Square(30, 5, 7.0) +
        "%TO.P,U1,3*%\n" + Square(22, 5, 1.2) + "%TD*%\nM02*";

    private const string Bottom =
        "%TF.FileFunction,Copper,L2,Bot,Signal*%\n%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.8*%\n%ADD11C,6.0*%\n" +
        "D10*\nX5000000Y5000000D03*\nX30000000Y5000000D03*\nD11*\nX14000000Y5000000D03*\nM02*";

    private const string Drills =
        "M48\nMETRIC\nT1C0.3\nT2C3.2\n%\nT1\nX5.0Y5.0\nX30.0Y5.0\nT2\nX14.0Y5.0\nM30";

    private static PcbBoard Read()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opensim-drilledpad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "profile.gbr"), Profile);
            File.WriteAllText(Path.Combine(dir, "top.gbr"), Top);
            File.WriteAllText(Path.Combine(dir, "bottom.gbr"), Bottom);
            File.WriteAllText(Path.Combine(dir, "drill_PTH.drl"), Drills);
            return new PcbBoardReader().Read(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static CopperNet NetAt(PcbBoard board, int layer, double x, double y) =>
        board.Nets.Single(n => n.Islands.Any(i => i.LayerOrder == layer &&
            OpenSim.Pcb.Meshing2D.PlanarMesher.ContainsPoint(new[] { i.Shape }, new Point2(x * 1e-3, y * 1e-3))));

    [Fact]
    public void ARegionViaPad_StitchesItsVia()
    {
        var board = Read();
        Assert.Same(NetAt(board, 1, 5, 5), NetAt(board, 2, 5, 5));
        Assert.Contains(board.Pads, p => p.LayerOrder == 1 && (p.Center - new Point2(5e-3, 5e-3)).Length < 1e-6);
    }

    [Fact]
    public void ALargeThroughHolePad_StitchesItsHole()
    {
        var board = Read();
        Assert.Same(NetAt(board, 1, 14, 5), NetAt(board, 2, 14, 5));
        Assert.Contains(board.Warnings, w => w.Contains("taken as pads because a plated drill sits at their centre"));
    }

    [Fact]
    public void APour_IsNotAPad_EvenWithAViaAtItsCentre()
    {
        var board = Read();
        Assert.NotSame(NetAt(board, 1, 30, 5), NetAt(board, 2, 30, 5));
        Assert.DoesNotContain(board.Pads, p => p.LayerOrder == 1 && (p.Center - new Point2(30e-3, 5e-3)).Length < 1e-6);
    }

    [Fact]
    public void ARegionNamedAPin_IsThatPinsPad()
    {
        var pad = Assert.Single(Read().Pads, p => p.ComponentRef == "U1");
        Assert.Equal("3", pad.Pin);
        Assert.Equal(1, pad.LayerOrder);
        Assert.Equal(1.2e-3, pad.Size, 9);
        Assert.Equal(1.44e-6, pad.Shape.Area(), 12);
        Assert.True((pad.Center - new Point2(22e-3, 5e-3)).Length < 1e-9);
    }

    [Fact]
    public void ALargeFlashNamedAPin_IsAPad()
    {
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,8.0*%\n%TO.P,J1,1*%\nD10*\nX0Y0D03*\n%TD*%\nX20000000Y0D03*\nM02*");
        var pad = Assert.Single(PadExtractor.Extract(doc, 1));
        Assert.Equal("J1", pad.ComponentRef);
    }
}
