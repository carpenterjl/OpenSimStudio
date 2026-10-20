using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Polygons;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class MacroExpressionTests
{
    private static IReadOnlyList<MacroPrimitive> Eval(string body, params double[] parameters) =>
        Eval(new List<string>(), body, parameters);

    private static IReadOnlyList<MacroPrimitive> Eval(List<string> warnings, string body, params double[] parameters)
    {
        var statements = body.Split('*', StringSplitOptions.RemoveEmptyEntries);
        return MacroEvaluator.Evaluate(new MacroDefinition("TEST", statements), parameters, 1e-3, warnings);
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

    // Gerber Layer Format Specification, Revision 2026.05, 4.5.4.2 (p. 74), the variable
    // rules verbatim in OpenSim.Tests/References/FU-20/README.md: $1…$n are the AD
    // command's n parameters, undefined variables are 0, a variable cannot be redefined.
    // PCB-19 / FU-20: the evaluator used to throw on an undefined $n.

    [Fact]
    public void UndefinedVariable_ReadsZero_AndWarnsOnce()
    {
        var warnings = new List<string>();
        // $4 appears twice (diameter and X); $1 is supplied. One warning, not two.
        var circle = Assert.IsType<MacroCircle>(Assert.Single(Eval(warnings, "1,1,$1+$4,$4,0*", 2.0)));
        Assert.Equal(2.0e-3, circle.Diameter, 12);
        Assert.Equal(0.0, circle.Center.X, 12);

        var warning = Assert.Single(warnings);
        Assert.Contains("TEST", warning);
        Assert.Contains("$4", warning);
        Assert.Contains("4.5.4.2", warning);
        Assert.Contains("$4 is undefined", warning);
        Assert.DoesNotContain("$1 is undefined", warning);
    }

    [Fact]
    public void UndefinedVariable_InAnAssignment_ReadsZero()
    {
        // $5 = $4 x 0.25 with $4 unsupplied: $4 reads 0, so $5 = 0 and the circle has no size.
        var warnings = new List<string>();
        var circle = Assert.IsType<MacroCircle>(Assert.Single(Eval(warnings, "$5=$4x0.25*1,1,$5,0,0*", 0.02)));
        Assert.Equal(0.0, circle.Diameter, 12);
        Assert.Contains("$4", Assert.Single(warnings));
    }

    [Fact]
    public void SpecExample_Donutcal_ParametersAndNewVariable()
    {
        // Spec 4.5.4.2 worked example: %AMDONUTCAL* 1,1,$1,$2,$3* $5=$4x0.25* 1,0,$5,$2,$3*%
        // with %ADD35DONUTCAL,0.020X0X0X0.06*% → $1 = 0.02, $2 = 0, $3 = 0, $4 = 0.06,
        // $5 = 0.06 × 0.25 = 0.015. Nothing is undefined, so no warning.
        var warnings = new List<string>();
        var primitives = Eval(warnings, "1,1,$1,$2,$3*$5=$4x0.25*1,0,$5,$2,$3*", 0.020, 0, 0, 0.06);
        Assert.Empty(warnings);
        Assert.Equal(2, primitives.Count);

        var outer = Assert.IsType<MacroCircle>(primitives[0]);
        Assert.True(outer.Exposure);
        Assert.Equal(0.020e-3, outer.Diameter, 15);
        Assert.Equal(0.0, outer.Center.X, 15);
        Assert.Equal(0.0, outer.Center.Y, 15);

        var inner = Assert.IsType<MacroCircle>(primitives[1]);
        Assert.False(inner.Exposure);
        Assert.Equal(0.015e-3, inner.Diameter, 15);
    }

    [Fact]
    public void RedefinedVariable_Throws()
    {
        // Spec 4.5.4.2: a redefinition makes the file invalid — a new variable, and an AD parameter.
        var twice = Assert.Throws<InvalidDataException>(() => Eval("$3=$1x0.75*$3=1*1,1,$3,0,0*", 2.0));
        Assert.Contains("$3", twice.Message);
        Assert.Contains("TEST", twice.Message);
        var parameter = Assert.Throws<InvalidDataException>(() => Eval("$1=2*1,1,$1,0,0*", 1.0));
        Assert.Contains("$1", parameter.Message);
    }

    [Fact]
    public void DivisionByZero_ThrowsWithContext()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Eval("1,1,1/0,0,0*"));
        Assert.Contains("TEST", ex.Message);
    }

    [Fact]
    public void DollarZero_IsNotAVariable()
    {
        // Spec 4.5 (p. 59) and the grammar (p. 39): a name is '$' + an integer > 0, so $0
        // is malformed, not an undefined variable that reads 0.
        var warnings = new List<string>();
        Assert.Throws<InvalidDataException>(() => Eval(warnings, "1,1,$0,0,0*"));
        Assert.Throws<InvalidDataException>(() => Eval(warnings, "$0=1*1,1,1,0,0*"));
        Assert.Empty(warnings);
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
    public void ShortAdCommand_ParsesWithZeroedVariables_AndWarnsPerVariable()
    {
        // Spec 4.5.5.2's DONUTVAR body (1,1,$1,$2,$3* 1,0,$4,$2,$3*) called with one AD
        // parameter instead of its four (the spec's call is ,0.100X0X0X0.080): $2, $3, $4
        // read 0 — an on-circle of 0.100 at the origin and an off-circle of no size — and
        // the import succeeds with one warning per missing variable (PCB-19 used to fail it).
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%AMDONUTVAR*\n1,1,$1,$2,$3*\n1,0,$4,$2,$3*%\n" +
            "%ADD34DONUTVAR,0.100*%\nD34*\nX0Y0D03*\nM02*");

        var macro = Assert.IsType<MacroAperture>(doc.Apertures[34]);
        Assert.Equal(2, macro.Primitives.Count);
        var on = Assert.IsType<MacroCircle>(macro.Primitives[0]);
        Assert.Equal(0.100e-3, on.Diameter, 15);
        Assert.Equal(0.0, on.Center.X, 15);
        Assert.Equal(0.0, on.Center.Y, 15);
        var off = Assert.IsType<MacroCircle>(macro.Primitives[1]);
        Assert.Equal(0.0, off.Diameter, 15);
        Assert.Equal(0.100e-3, macro.BoundingSize, 15);

        var undefined = doc.Warnings.Where(w => w.Contains("DONUTVAR") && w.Contains("undefined")).ToList();
        Assert.Equal(3, undefined.Count);
        foreach (var name in new[] { "$2", "$3", "$4" })
            Assert.Single(undefined, w => w.Contains(name + " "));

        // The spec's own four-parameter call defines everything: no warning at all.
        var full = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%AMDONUTVAR*\n1,1,$1,$2,$3*\n1,0,$4,$2,$3*%\n" +
            "%ADD34DONUTVAR,0.100X0X0X0.080*%\nM02*");
        Assert.Empty(full.Warnings);
        Assert.Equal(0.080e-3, Assert.IsType<MacroCircle>(
            Assert.IsType<MacroAperture>(full.Apertures[34]).Primitives[1]).Diameter, 15);
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
