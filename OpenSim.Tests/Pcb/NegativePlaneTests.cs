using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Import;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// A plane file declared <c>%TF.FilePolarity,Negative*%</c> draws where copper is
/// REMOVED (antipads, thermal reliefs); the copper is the board outline minus the
/// drawing. Imaged as drawn, the plane does not exist and every antipad is a copper
/// disc concentric with the via it was meant to isolate. The fixture is a 20 × 10 mm
/// two-layer board: three via pads on L1, and a negative L2 plane with nothing at via
/// A (direct connect), a thermal relief at via C and an antipad at via B.
/// </summary>
public class NegativePlaneTests
{
    private const string Profile =
        "%TF.FileFunction,Profile,NP*%\n%FSLAX46Y46*%\n%MOMM*%\n" +
        "G36*\nX0Y0D02*\nX20000000Y0D01*\nX20000000Y10000000D01*\nX0Y10000000D01*\nX0Y0D01*\nG37*\nM02*";

    // Three separate 0.8 mm via pads at A (5,5), C (10,5), B (15,5).
    private const string Top =
        "%TF.FileFunction,Copper,L1,Top,Signal*%\n%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.8*%\nD10*\n" +
        "X5000000Y5000000D03*\nX10000000Y5000000D03*\nX15000000Y5000000D03*\nM02*";

    private const string NegativeHeader =
        "%TF.FileFunction,Copper,L2,Bot,Plane*%\n%TF.FilePolarity,Negative*%\n%FSLAX46Y46*%\n%MOMM*%\n";

    // 1.2 mm antipad at B only.
    private const string PlaneAntipadOnly =
        NegativeHeader + "%ADD10C,1.2*%\nD10*\nX15000000Y5000000D03*\nM02*";

    // Antipad at B, four-spoke thermal relief (outer 1.2, inner 0.8, 0.3 mm spokes) at C.
    private const string PlaneAntipadAndThermal =
        NegativeHeader + "%AMTHERMAL*7,0,0,1.2,0.8,0.3,0*%\n%ADD10C,1.2*%\n%ADD11THERMAL*%\n" +
        "D10*\nX15000000Y5000000D03*\nD11*\nX10000000Y5000000D03*\nM02*";

    private const string Drills =
        "M48\nMETRIC\nT1C0.3\n%\nT1\nX5.0Y5.0\nX10.0Y5.0\nX15.0Y5.0\nM30";

    private static PcbBoard Read(string plane, bool withProfile = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "opensim-negplane-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (withProfile) File.WriteAllText(Path.Combine(dir, "profile.gbr"), Profile);
            File.WriteAllText(Path.Combine(dir, "top.gbr"), Top);
            File.WriteAllText(Path.Combine(dir, "plane.gbr"), plane);
            File.WriteAllText(Path.Combine(dir, "drill_PTH.drl"), Drills);
            return new PcbBoardReader().Read(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Parser_ReadsFilePolarity()
    {
        Assert.True(new GerberParser().Parse(PlaneAntipadOnly).IsNegative);
        Assert.False(new GerberParser().Parse(Top).IsNegative);
        Assert.False(new GerberParser().Parse(
            "%TF.FilePolarity,Positive*%\n%FSLAX46Y46*%\n%MOMM*%\nM02*").IsNegative);
    }

    [Fact]
    public void NegativePlane_IsTheOutlineMinusTheAntipad()
    {
        var board = Read(PlaneAntipadOnly);

        var plane = Assert.Single(board.Islands, i => i.LayerOrder == 2);
        Assert.Single(plane.Shape.Holes);
        // 20 × 10 mm less the 1.2 mm antipad (an inscribed polygon at 5 µm chord
        // tolerance sits within 2 % of the true disc).
        double antipad = Math.PI * 0.6e-3 * 0.6e-3;
        Assert.InRange(plane.Area, 200e-6 - antipad, 200e-6 - 0.98 * antipad);
    }

    [Fact]
    public void NegativePlane_AntipadIsNotAPad_AndDrawsAreNotTraces()
    {
        var board = Read(PlaneAntipadOnly);

        Assert.DoesNotContain(board.Pads, p => p.LayerOrder == 2);
        Assert.Equal(3, board.Pads.Count(p => p.LayerOrder == 1));
        Assert.DoesNotContain(board.TraceCenterlines, c => c.LayerOrder == 2);
    }

    [Fact]
    public void DirectConnectVia_JoinsThePlane_AntipadViaDoesNot()
    {
        var board = Read(PlaneAntipadOnly);

        // Nets: {plane, pad A, pad C} and {pad B}.
        Assert.Equal(2, board.Nets.Count);
        var planeNet = board.Nets[0];                             // largest first
        Assert.Equal(new[] { 1, 2 }, planeNet.Layers);
        Assert.Equal(3, planeNet.Islands.Count);
        Assert.Equal(2, planeNet.StitchingVias.Count);
        Assert.All(planeNet.StitchingVias, b => Assert.Equal(new[] { 1, 2 }, b.Layers));
        Assert.DoesNotContain(planeNet.StitchingVias, b => Math.Abs(b.Via.Position.X - 15e-3) < 1e-6);

        var isolated = board.Nets[1];
        var pad = Assert.Single(isolated.Islands);
        Assert.Equal(1, pad.LayerOrder);
        Assert.InRange(pad.Bounds().MinX, 14.5e-3, 14.7e-3);      // pad B, at x = 15 mm
    }

    [Fact]
    public void ThermalReliefVia_JoinsThePlaneThroughItsSpokes()
    {
        var board = Read(PlaneAntipadAndThermal);

        // The thermal's centre and spokes are copper continuous with the plane: one
        // island on L2 (with the antipad hole and the four relief arcs as holes).
        var plane = Assert.Single(board.Islands, i => i.LayerOrder == 2);
        Assert.Equal(5, plane.Shape.Holes.Count);

        Assert.Equal(2, board.Nets.Count);
        Assert.Equal(3, board.Nets[0].Islands.Count);
        Assert.Contains(board.Nets[0].StitchingVias, b => Math.Abs(b.Via.Position.X - 10e-3) < 1e-6);
    }

    [Fact]
    public void NegativePlane_WithoutABoardOutline_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Read(PlaneAntipadOnly, withProfile: false));
        Assert.Contains("plane.gbr", ex.Message);
        Assert.Contains("outline", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
