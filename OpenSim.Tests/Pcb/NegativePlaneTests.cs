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

    // The same plane layer with NO polarity statement (FU-22): once as X2 output whose
    // FileFunction still calls it a plane, once as a Protel-era file with no attributes at
    // all, named by its extension.
    private const string UndeclaredPlaneHeader =
        "%TF.FileFunction,Copper,L2,Bot,Plane*%\n%FSLAX46Y46*%\n%MOMM*%\n";
    private const string BareHeader = "%FSLAX46Y46*%\n%MOMM*%\n";
    private const string AntipadAndThermalBody =
        "%AMTHERMAL*7,0,0,1.2,0.8,0.3,0*%\n%ADD10C,1.2*%\n%ADD11THERMAL*%\n" +
        "D10*\nX15000000Y5000000D03*\nD11*\nX10000000Y5000000D03*\nM02*";

    private static PcbBoard Read(string plane, bool withProfile = true, string planeFile = "plane.gbr",
        bool withDrills = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "opensim-negplane-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            if (withProfile) File.WriteAllText(Path.Combine(dir, "profile.gbr"), Profile);
            File.WriteAllText(Path.Combine(dir, "top.gbr"), Top);
            File.WriteAllText(Path.Combine(dir, planeFile), plane);
            if (withDrills) File.WriteAllText(Path.Combine(dir, "drill_PTH.drl"), Drills);
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
    public void Parser_ReadsTheDeprecatedImagePolarity()
    {
        // %IPNEG*% is RS-274X's statement of what X2 says with TF.FilePolarity,Negative.
        Assert.True(new GerberParser().Parse("%IPNEG*%\n%FSLAX46Y46*%\n%MOMM*%\nM02*").IsNegative);
        Assert.False(new GerberParser().Parse("%IPPOS*%\n%FSLAX46Y46*%\n%MOMM*%\nM02*").IsNegative);
    }

    /// <summary>The negative plane's own copper: one L2 island whose antipad and relief arcs
    /// are holes, the direct-connect and relieved vias in the plane net, the antipad via out.</summary>
    private static void AssertIsTheNegativePlane(PcbBoard board)
    {
        var plane = Assert.Single(board.Islands, i => i.LayerOrder == 2);
        Assert.Equal(5, plane.Shape.Holes.Count);
        Assert.Equal(2, board.Nets.Count);
        Assert.Equal(3, board.Nets[0].Islands.Count);
        Assert.Equal(2, board.Nets[0].StitchingVias.Count);
        Assert.DoesNotContain(board.Nets[0].StitchingVias, b => Math.Abs(b.Via.Position.X - 15e-3) < 1e-6);
    }

    [Fact]
    public void AnUndeclaredPlane_OfAntipadsAndThermals_IsReadAsNegative()
    {
        var board = Read(UndeclaredPlaneHeader + AntipadAndThermalBody);

        AssertIsTheNegativePlane(board);
        var note = Assert.Single(board.Warnings, w => w.Contains("read as a NEGATIVE plane"));
        Assert.Contains("plane.gbr", note);
        Assert.Contains("2 of its 2 flashes sit on drilled holes", note);
    }

    [Fact]
    public void AProtelPlaneFile_WithNoAttributesAtAll_IsReadAsNegative()
    {
        // Named only by its .gp2 extension; the stack order then comes from the file names.
        var board = Read(BareHeader + AntipadAndThermalBody, planeFile: "plane.gp2");

        AssertIsTheNegativePlane(board);
        Assert.Contains(board.Warnings, w => w.Contains("plane.gp2") && w.Contains("read as a NEGATIVE plane"));
    }

    [Fact]
    public void AnUndeclaredPlane_DrawnAsAPour_StaysPositive()
    {
        // A positive plane layer: an 18 × 8 mm pour with the three via pads flashed on it. Its
        // region covers most of the board, so it is what it draws, and all three vias join it.
        const string Pour = "G36*\nX1000000Y1000000D02*\nX19000000Y1000000D01*\nX19000000Y9000000D01*\n" +
                            "X1000000Y9000000D01*\nX1000000Y1000000D01*\nG37*\n";
        var board = Read(UndeclaredPlaneHeader + "%ADD10C,0.8*%\n" + Pour +
                         "D10*\nX5000000Y5000000D03*\nX10000000Y5000000D03*\nX15000000Y5000000D03*\nM02*");

        var plane = Assert.Single(board.Islands, i => i.LayerOrder == 2);
        Assert.Equal(144e-6, plane.Area, 1e-9);
        Assert.Single(board.Nets);
        Assert.Equal(3, board.Nets[0].StitchingVias.Count);
        Assert.DoesNotContain(board.Warnings, w => w.Contains("NEGATIVE") || w.Contains("no polarity statement"));
    }

    [Fact]
    public void AnUndeclaredPlane_WithoutADrillFile_IsImagedAsDrawn_AndSaysSo()
    {
        // The flashes cannot be checked against holes, so the file is read as it is drawn and
        // the log says what it looks like.
        var board = Read(UndeclaredPlaneHeader + AntipadAndThermalBody, withDrills: false);

        Assert.Equal(5, board.Islands.Count(i => i.LayerOrder == 2));          // the antipad disc and the relief's four arcs
        var note = Assert.Single(board.Warnings, w => w.Contains("plane.gbr") && w.Contains("no polarity statement"));
        Assert.Contains("no drill file", note);
        Assert.Contains("imaged as drawn", note);
    }

    [Fact]
    public void ThermalReliefs_NameTheNegativePlane_AntipadsDoNot()
    {
        // X2 object attributes on a negative plane name the hole each clearance belongs to.
        // The relieved via's net is tied to the plane through the spokes, so it is the
        // plane's name; the antipad's net is isolated from it.
        var board = Read(NegativeHeader + "%AMTHERMAL*7,0,0,1.2,0.8,0.3,0*%\n%ADD10C,1.2*%\n%ADD11THERMAL*%\n" +
                         "%TO.N,SIG_B*%\nD10*\nX15000000Y5000000D03*\n" +
                         "%TO.N,GND*%\nD11*\nX10000000Y5000000D03*\n%TD*%\nM02*");

        Assert.Equal(2, board.Nets.Count);
        Assert.Equal("GND", board.Nets[0].Name);
        Assert.Equal(new[] { 1, 2 }, board.Nets[0].Layers);
        Assert.Null(board.Nets[1].Name);                                       // pad B: only the clearance named it
        Assert.DoesNotContain(board.Nets, n => n.Name == "SIG_B");
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
