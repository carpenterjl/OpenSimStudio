using OpenSim.Pcb.Excellon;
using OpenSim.Pcb.Gerber;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class GerberParserTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", name);

    [Fact]
    public void TwoPadsTrace_ParsesAperturesFlashesAndDraw()
    {
        var doc = new GerberParser().ParseFile(Fixture("two_pads_trace.gbr"));

        Assert.Empty(doc.Warnings);
        Assert.Equal(2, doc.Apertures.Count);
        var pad = Assert.IsType<CircleAperture>(doc.Apertures[10]);
        Assert.Equal(1.0e-3, pad.Diameter, 12);                   // 1 mm in meters
        var trace = Assert.IsType<CircleAperture>(doc.Apertures[11]);
        Assert.Equal(0.4e-3, trace.Diameter, 12);

        Assert.Equal(3, doc.Ops.Count);
        var flash1 = Assert.IsType<FlashOp>(doc.Ops[0]);
        Assert.Equal(1.0e-3, flash1.Position.X, 12);              // X1000000 with 4.6 format, mm
        Assert.Equal(1.0e-3, flash1.Position.Y, 12);
        var flash2 = Assert.IsType<FlashOp>(doc.Ops[1]);
        Assert.Equal(9.0e-3, flash2.Position.X, 12);

        var draw = Assert.IsType<DrawOp>(doc.Ops[2]);
        Assert.Equal(2, draw.Path.Count);
        Assert.Equal(1.0e-3, draw.Path[0].X, 12);
        Assert.Equal(9.0e-3, draw.Path[1].X, 12);
        Assert.Same(doc.Apertures[11], draw.Aperture);
    }

    [Fact]
    public void RegionCutout_ParsesRegionAndPolarity()
    {
        var doc = new GerberParser().ParseFile(Fixture("region_cutout.gbr"));

        Assert.Equal(2, doc.Ops.Count);
        var region = Assert.IsType<RegionOp>(doc.Ops[0]);
        Assert.Equal(GerberPolarity.Dark, region.Polarity);
        var contour = Assert.Single(region.Contours);
        Assert.True(contour.Count >= 4);

        var cutout = Assert.IsType<FlashOp>(doc.Ops[1]);
        Assert.Equal(GerberPolarity.Clear, cutout.Polarity);
        Assert.Equal(5.0e-3, cutout.Position.X, 12);
    }

    [Fact]
    public void ArcTrace_TessellatesQuarterCircleWithinChordTolerance()
    {
        const double tol = 5e-6;
        var doc = new GerberParser(new GerberParseOptions { ChordTolerance = tol }).ParseFile(
            Fixture("arc_trace.gbr"));

        var draw = Assert.IsType<DrawOp>(Assert.Single(doc.Ops));
        Assert.True(draw.Path.Count > 10, "A 5 mm quarter arc at 5 µm tolerance needs many segments.");

        // Every tessellated point lies on the r = 5 mm circle about the origin,
        // and the endpoints land exactly on start/end.
        const double r = 5e-3;
        foreach (var p in draw.Path)
            Assert.Equal(r, p.Length, r * 1e-6);
        Assert.Equal(0.0, draw.Path[^1].X, 12);
        Assert.Equal(r, draw.Path[^1].Y, 12);

        // Chord sagitta bound: midpoint of each segment within tol of the circle.
        for (int i = 1; i < draw.Path.Count; i++)
        {
            var mid = (draw.Path[i - 1] + draw.Path[i]) * 0.5;
            Assert.True(r - mid.Length <= tol * 1.01,
                $"Chord deviation {(r - mid.Length) * 1e6:f2} µm exceeds tolerance.");
        }
    }

    [Fact]
    public void Rejects_TrailingZeroAndIncrementalModes()
    {
        var parser = new GerberParser();
        Assert.Throws<InvalidDataException>(() => parser.Parse("%FSTAX46Y46*%\n%MOMM*%\nM02*"));
        Assert.Throws<InvalidDataException>(() => parser.Parse("%FSLAX46Y46*%\n%MOMM*%\nG91*\nM02*"));
        // Coordinates before FS/MO must fail, not misscale.
        Assert.Throws<InvalidDataException>(() => parser.Parse("%ADD10C,1*%\nD10*\nX100Y100D03*\nM02*"));
    }

    [Fact]
    public void MacroAperture_ParsesCirclePrimitive()
    {
        // The same input that used to warn-and-approximate now evaluates exactly.
        var doc = new GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%AMTHERMAL*1,1,0.8,0,0*%\n%ADD10THERMAL*%\nD10*\nX0Y0D03*\nM02*");

        Assert.Empty(doc.Warnings);
        var macro = Assert.IsType<MacroAperture>(doc.Apertures[10]);
        var circle = Assert.IsType<MacroCircle>(Assert.Single(macro.Primitives));
        Assert.True(circle.Exposure);
        Assert.Equal(0.8e-3, circle.Diameter, 12);
        Assert.Equal(0.8e-3, macro.BoundingSize, 12);
        Assert.IsType<FlashOp>(Assert.Single(doc.Ops));
    }
}

public class ExcellonParserTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", name);

    [Fact]
    public void DrillFile_ParsesToolsAndHitsInMeters()
    {
        var drills = new ExcellonParser().ParseFile(Fixture("holes.drl"));

        Assert.Equal(3, drills.Hits.Count);
        Assert.Equal(0.6e-3, drills.Hits[0].Diameter, 12);
        Assert.Equal(2.0e-3, drills.Hits[0].Position.X, 12);
        Assert.Equal(0.6e-3, drills.Hits[1].Diameter, 12);
        Assert.Equal(1.0e-3, drills.Hits[2].Diameter, 12);
        Assert.Equal(8.0e-3, drills.Hits[2].Position.Y, 12);
    }

    [Fact]
    public void ParsesG85Slot()
    {
        var drills = new ExcellonParser().Parse(
            "M48\nMETRIC\nT1C0.6\n%\nT1\nX1.0Y1.0G85X2.0Y1.0\nM30");

        Assert.Empty(drills.Hits);
        var slot = Assert.Single(drills.Slots);
        Assert.Equal(1.0e-3, slot.Start.X, 12);
        Assert.Equal(1.0e-3, slot.Start.Y, 12);
        Assert.Equal(2.0e-3, slot.End.X, 12);
        Assert.Equal(1.0e-3, slot.End.Y, 12);
        Assert.Equal(0.6e-3, slot.Diameter, 12);
    }

    [Fact]
    public void G85Slot_MissingEndAxis_InheritsFromStart()
    {
        var drills = new ExcellonParser().Parse(
            "M48\nMETRIC\nT1C0.6\n%\nT1\nX1.0Y3.0G85X2.5\nM30");

        var slot = Assert.Single(drills.Slots);
        Assert.Equal(2.5e-3, slot.End.X, 12);
        Assert.Equal(3.0e-3, slot.End.Y, 12); // Y is modal: inherited from the start point
    }

    [Fact]
    public void Rejects_IntegerCoordinates()
    {
        Assert.Throws<InvalidDataException>(() => new ExcellonParser().Parse(
            "M48\nMETRIC\nT1C0.6\n%\nT1\nX001000Y001000\nM30"));
    }

    [Fact]
    public void Rejects_MalformedSlots()
    {
        var parser = new ExcellonParser();
        // Integer coordinates inside a slot line.
        Assert.Throws<InvalidDataException>(() => parser.Parse(
            "M48\nMETRIC\nT1C0.6\n%\nT1\nX001000Y001000G85X002000Y001000\nM30"));
        // Bare multi-line G85 form.
        Assert.Throws<InvalidDataException>(() => parser.Parse(
            "M48\nMETRIC\nT1C0.6\n%\nT1\nX1.0Y1.0\nG85\nX2.0Y1.0\nM30"));
        // Slot before any tool selection.
        Assert.Throws<InvalidDataException>(() => parser.Parse(
            "M48\nMETRIC\nT1C0.6\n%\nX1.0Y1.0G85X2.0Y1.0\nM30"));
    }
}
