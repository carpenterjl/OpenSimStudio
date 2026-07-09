using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Polygons;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class MacroExpressionTests
{
    private static IReadOnlyList<MacroPrimitive> Eval(string body, params double[] parameters)
    {
        var statements = body.Split('*', StringSplitOptions.RemoveEmptyEntries);
        return MacroEvaluator.Evaluate(new MacroDefinition("TEST", statements), parameters, 1e-3, new List<string>());
    }

    private static double CircleDiameterMm(string expression, params double[] parameters)
    {
        // Route the expression through a circle's diameter slot: result in file units (mm here).
        var circle = Assert.IsType<MacroCircle>(Assert.Single(Eval($"1,1,{expression},0,0*", parameters)));
        return circle.Diameter / 1e-3;
    }

    [Fact]
    public void Expressions_EvaluateWithPrecedence()
    {
        Assert.Equal(7.0, CircleDiameterMm("1+2x3"), 12);          // x binds tighter than +
        Assert.Equal(9.0, CircleDiameterMm("(1+2)x3"), 12);
        Assert.Equal(2.5, CircleDiameterMm("$1/2", 5.0), 12);
        Assert.Equal(4.0, CircleDiameterMm("$1-(-1)", 3.0), 12);   // unary minus
        Assert.Equal(0.75, CircleDiameterMm("$1x$2", 1.5, 0.5), 12);
        Assert.Equal(2.0, CircleDiameterMm("10/5X1"), 12);         // uppercase X multiply, left-assoc
    }

    [Fact]
    public void VariableAssignments_DefineAndUse()
    {
        var primitives = Eval("$3=$1x0.75*1,1,$3,0,0*", 2.0);
        var circle = Assert.IsType<MacroCircle>(Assert.Single(primitives));
        Assert.Equal(1.5e-3, circle.Diameter, 12);
    }

    [Fact]
    public void UndefinedVariable_AndDivisionByZero_ThrowWithContext()
    {
        var undefined = Assert.Throws<InvalidDataException>(() => Eval("1,1,$4,0,0*"));
        Assert.Contains("TEST", undefined.Message);
        Assert.Contains("$4", undefined.Message);
        Assert.Throws<InvalidDataException>(() => Eval("1,1,1/0,0,0*"));
    }

    [Fact]
    public void Comments_AreSkipped_UnknownPrimitives_Throw()
    {
        Assert.Single(Eval("0thisisacomment*1,1,0.8,0,0*"));
        var ex = Assert.Throws<InvalidDataException>(() => Eval("99,1,0.8,0,0*"));
        Assert.Contains("99", ex.Message);
    }
}

public class MacroGeometryTests
{
    private static readonly ClipperPolygonOps Ops = new();

    private static IReadOnlyList<Polygon2> FlattenAd(string gerber, int code = 10)
    {
        var doc = new GerberParser().Parse(gerber);
        var macro = Assert.IsType<MacroAperture>(doc.Apertures[code]);
        return MacroFlattener.Flatten(macro, Ops, 5e-6);
    }

    private static double TotalArea(IReadOnlyList<Polygon2> polygons) => polygons.Sum(p => p.Area());

    [Fact]
    public void AltiumRoundedRect_FlattensToExactObround()
    {
        // The exact ROUNDEDRECTD50 body from the real Altium fixture: two center-line
        // rects rotated 270° about the MACRO ORIGIN plus four UNROTATED corner circles
        // already placed at the post-rotation positions (x=±0.249, y=±0.499). Only
        // origin-rotation of the rects makes the union close into a rounded rectangle
        // 0.6 mm wide × 1.1 mm tall with 0.051 mm corner radius — rotating the rects
        // about their own centers leaves the corner circles disconnected.
        var polygons = FlattenAd(
            "%FSLAX46Y46*%\n%MOMM*%\n%AMROUNDEDRECTD50*\n" +
            "21,1,1.10000,0.49800,0,0,270.0*\n" +
            "21,1,0.99800,0.60000,0,0,270.0*\n" +
            "1,1,0.10200,-0.24900,-0.49900*\n" +
            "1,1,0.10200,-0.24900,0.49900*\n" +
            "1,1,0.10200,0.24900,0.49900*\n" +
            "1,1,0.10200,0.24900,-0.49900*\n%\n" +
            "%ADD10ROUNDEDRECTD50*%\nM02*");

        var polygon = Assert.Single(polygons);
        Assert.Empty(polygon.Holes);

        // Rounded-rect area = W·H − (4 − π)·r².
        double w = 0.6e-3, h = 1.1e-3, r = 0.051e-3;
        double expected = w * h - (4 - Math.PI) * r * r;
        Assert.Equal(expected, TotalArea(polygons), expected * 5e-3);

        double minX = polygon.Outer.Min(p => p.X), maxX = polygon.Outer.Max(p => p.X);
        double minY = polygon.Outer.Min(p => p.Y), maxY = polygon.Outer.Max(p => p.Y);
        Assert.Equal(w, maxX - minX, w * 1e-2);
        Assert.Equal(h, maxY - minY, h * 1e-2);
    }

    [Fact]
    public void ExposureOff_CutsAnnulus_AndOrderMatters()
    {
        // on-circle, off-circle → annulus.
        var annulus = FlattenAd(
            "%FSLAX46Y46*%\n%MOMM*%\n%AMANNULUS*\n1,1,2.0,0,0*\n1,0,1.0,0,0*\n%\n%ADD10ANNULUS*%\nM02*");
        var polygon = Assert.Single(annulus);
        Assert.Single(polygon.Holes);
        double expected = Math.PI * (Math.Pow(1e-3, 2) - Math.Pow(0.5e-3, 2));
        Assert.Equal(expected, TotalArea(annulus), expected * 2e-2);

        // A later on-primitive repaints the hole: on, off, on-small → hole partially refilled.
        var repainted = FlattenAd(
            "%FSLAX46Y46*%\n%MOMM*%\n%AMREFILL*\n1,1,2.0,0,0*\n1,0,1.0,0,0*\n1,1,0.6,0,0*\n%\n%ADD10REFILL*%\nM02*");
        double refill = Math.PI * Math.Pow(0.3e-3, 2);
        Assert.Equal(expected + refill, TotalArea(repainted), expected * 2e-2);
    }

    [Fact]
    public void Thermal_FourSpokes_AnalyticArea()
    {
        var polygons = FlattenAd(
            "%FSLAX46Y46*%\n%MOMM*%\n%AMTHERM*\n7,0,0,2.0,1.0,0.2,0*\n%\n%ADD10THERM*%\nM02*");

        Assert.Equal(4, polygons.Count);                          // four disjoint quadrant arcs

        // Annulus minus two gap strips (each gap · annular width, counted once per arm pair):
        // area ≈ π(R²−r²) − 4·gap·(R−r) + 4·gap² overlap correction is zero since the
        // crosshair only removes annulus material: strips of width g crossing radially.
        double R = 1e-3, r = 0.5e-3, g = 0.2e-3;
        double expected = Math.PI * (R * R - r * r) - 4 * g * (R - r);
        Assert.Equal(expected, TotalArea(polygons), expected * 3e-2);
    }

    [Fact]
    public void VectorLine_And_Outline_ResolveToRings()
    {
        // 20: a 2 mm × 0.5 mm flat-ended line; 4: a right triangle, rotated 90°.
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%AMSHAPES*\n" +
            "20,1,0.5,-1.0,0,1.0,0,0*\n" +
            "4,1,3,0,0,1.0,0,1.0,1.0,0,0,90.0*\n%\n" +
            "%ADD10SHAPES*%\nM02*");
        var macro = Assert.IsType<MacroAperture>(doc.Apertures[10]);

        var line = Assert.IsType<MacroRing>(macro.Primitives[0]);
        Assert.Equal(1e-6 /* 2 mm × 0.5 mm */, Math.Abs(Polygon2.RingArea(line.Vertices)), 1e-6 * 1e-9);

        var triangle = Assert.IsType<MacroRing>(macro.Primitives[1]);
        Assert.Equal(3, triangle.Vertices.Count);                 // closing duplicate dropped
        Assert.Equal(0.5e-6, Math.Abs(Polygon2.RingArea(triangle.Vertices)), 0.5e-6 * 1e-9);
        // 90° rotation about the origin maps vertex (1.0, 0) → (0, 1.0).
        Assert.Equal(0.0, triangle.Vertices[1].X, 12);
        Assert.Equal(1.0e-3, triangle.Vertices[1].Y, 12);
    }

    [Fact]
    public void Moire_WarnsAndApproximatesAsOuterCircle()
    {
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%AMMOIRE*\n6,0,0,2.0,0.1,0.1,3,0.05,2.2,0*\n%\n%ADD10MOIRE*%\nM02*");
        Assert.Contains(doc.Warnings, w => w.Contains("moiré", StringComparison.OrdinalIgnoreCase));
        var macro = Assert.IsType<MacroAperture>(doc.Apertures[10]);
        var circle = Assert.IsType<MacroCircle>(Assert.Single(macro.Primitives));
        Assert.Equal(2.0e-3, circle.Diameter, 12);
    }
}

public class RealBoardMacroTests
{
    [Fact]
    public void ExampleBoard_CopperLayers_ParseWithoutMacroWarnings()
    {
        // The Altium fixture's ROUNDEDRECT pads used to flash as approximate circles
        // with warnings; with AM support both copper layers must parse warning-free
        // and every macro aperture must size correctly for pad extraction.
        string zip = Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", "example_board.zip");
        var files = OpenSim.Pcb.Import.PcbArchive.Read(zip);
        var copper = files.Where(f => f.Name.Contains("Copper_Signal", StringComparison.OrdinalIgnoreCase));

        int macroApertures = 0;
        foreach (var file in copper)
        {
            var doc = new GerberParser().Parse(file.Text);
            Assert.DoesNotContain(doc.Warnings,
                w => w.Contains("macro", StringComparison.OrdinalIgnoreCase)
                     || w.Contains("approximated", StringComparison.OrdinalIgnoreCase));
            foreach (var macro in doc.Apertures.Values.OfType<MacroAperture>())
            {
                macroApertures++;
                Assert.True(macro.BoundingSize > 0, $"Macro '{macro.MacroName}' sized 0 — pads would vanish.");
                Assert.NotEmpty(MacroFlattener.Flatten(macro, Ops, 5e-6));
            }
        }
        Assert.True(macroApertures > 0, "Fixture is expected to define macro apertures.");
    }

    private static readonly ClipperPolygonOps Ops = new();
}

public class PolygonApertureTests
{
    [Fact]
    public void Hexagon_FlashArea_MatchesRegularPolygon()
    {
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10P,1.0X6*%\nD10*\nX0Y0D03*\nM02*");
        var aperture = Assert.IsType<PolygonAperture>(doc.Apertures[10]);
        Assert.Equal(6, aperture.VertexCount);
        Assert.Equal(1.0e-3, aperture.OuterDiameter, 12);

        var image = new LayerImageBuilder(new ClipperPolygonOps()).Build(doc);
        // Regular n-gon inscribed in diameter d: area = (n/2)·(d/2)²·sin(2π/n).
        // Tolerance covers the polygon engine's integer-grid quantization only.
        double expected = 6.0 / 2 * Math.Pow(0.5e-3, 2) * Math.Sin(2 * Math.PI / 6);
        Assert.Equal(expected, image.TotalArea(), expected * 1e-4);
    }

    [Fact]
    public void RotationAndHole_AreHonored()
    {
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10P,1.0X4X45X0.4*%\nD10*\nX0Y0D03*\nM02*");
        var aperture = Assert.IsType<PolygonAperture>(doc.Apertures[10]);
        Assert.Equal(45.0, aperture.RotationDeg, 12);             // degrees, NOT unit-scaled
        Assert.Equal(0.4e-3, aperture.HoleDiameter!.Value, 12);

        var image = new LayerImageBuilder(new ClipperPolygonOps()).Build(doc);
        var polygon = Assert.Single(image.Polygons);
        Assert.Single(polygon.Holes);                             // the round hole
        // A square (n=4, d=1 mm) at 45°: vertices land on the axes.
        double squareArea = 2 * Math.Pow(0.5e-3, 2);
        double expected = squareArea - Math.PI * Math.Pow(0.2e-3, 2);
        Assert.Equal(expected, image.TotalArea(), expected * 3e-2);
    }

    [Fact]
    public void VertexCountOutOfRange_Throws()
    {
        Assert.Throws<InvalidDataException>(() => new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10P,1.0X2*%\nM02*"));
        Assert.Throws<InvalidDataException>(() => new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10P,1.0X13*%\nM02*"));
    }

    [Fact]
    public void StandardApertureHole_SubtractsFromFlash()
    {
        // C/R/O hole parameters used to be dropped with a warning; now they cut.
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,1.0X0.4*%\nD10*\nX0Y0D03*\nM02*");
        Assert.Empty(doc.Warnings);
        var image = new LayerImageBuilder(new ClipperPolygonOps()).Build(doc);
        var polygon = Assert.Single(image.Polygons);
        Assert.Single(polygon.Holes);
        double expected = Math.PI * (Math.Pow(0.5e-3, 2) - Math.Pow(0.2e-3, 2));
        Assert.Equal(expected, image.TotalArea(), expected * 2e-2);
    }
}

public class StepRepeatTests
{
    [Fact]
    public void FlashGrid_ReplaysAtOffsets()
    {
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.5*%\nD10*\n" +
            "%SRX3Y2I5.0J4.0*%\nX0Y0D03*\n%SR*%\nM02*");

        var flashes = doc.Ops.OfType<FlashOp>().ToList();
        Assert.Equal(6, flashes.Count);
        var positions = flashes.Select(f => (X: f.Position.X, Y: f.Position.Y)).ToHashSet();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 2; j++)
                Assert.Contains(positions, p =>
                    Math.Abs(p.X - i * 5e-3) < 1e-12 && Math.Abs(p.Y - j * 4e-3) < 1e-12);
    }

    [Fact]
    public void DrawAndRegion_ReplayInOrder()
    {
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.2*%\nD10*\n" +
            "%SRX2Y1I10.0J0*%\n" +
            "X0Y0D02*\nX1000000Y0D01*\n" +
            "G36*\nX2000000Y0D02*\nX3000000Y0D01*\nX3000000Y1000000D01*\nX2000000Y1000000D01*\nX2000000Y0D01*\nG37*\n" +
            "%SR*%\nM02*");

        // Block = 1 draw + 1 region; replayed once at +10 mm, order preserved per repeat.
        Assert.Equal(4, doc.Ops.Count);
        var draw0 = Assert.IsType<DrawOp>(doc.Ops[0]);
        Assert.IsType<RegionOp>(doc.Ops[1]);
        var draw1 = Assert.IsType<DrawOp>(doc.Ops[2]);
        var region1 = Assert.IsType<RegionOp>(doc.Ops[3]);
        Assert.Equal(draw0.Path[0].X + 10e-3, draw1.Path[0].X, 12);
        Assert.Equal(12e-3, region1.Contours[0][0].X, 12);
    }

    [Fact]
    public void OpenAtEndOfFile_StillReplays()
    {
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.5*%\nD10*\n%SRX2Y1I5.0J0*%\nX0Y0D03*\nM02*");
        Assert.Equal(2, doc.Ops.OfType<FlashOp>().Count());
    }

    [Fact]
    public void AbsurdExpansion_RefusesLoudly()
    {
        Assert.Throws<InvalidDataException>(() => new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.5*%\nD10*\n%SRX2000Y2000I1.0J1.0*%\nX0Y0D03*\n%SR*%\nM02*"));
    }
}
