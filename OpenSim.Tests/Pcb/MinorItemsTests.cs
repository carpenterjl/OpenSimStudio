using System.Numerics;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Excellon;
using OpenSim.Pcb.Gerber;
using OpenSim.Pcb.Import;
using OpenSim.Rf.Layered;
using OpenSim.Rf.Si;
using OpenSim.Rf.Surface;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// Fix 19: the minor import, RF and SI items — each a small thing that was read wrong,
/// dropped without a word, or labelled as something it is not.
/// </summary>
public class MinorItemsTests
{
    private const string Header = "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.2*%\nD10*\n";

    // ---------------------------------------------------------------- PCB-14: G74

    [Theory]
    // Quarter circles of radius 1 mm about the origin, one per quadrant, counter-clockwise.
    // In G74 the I/J are unsigned distances to the centre.
    [InlineData(1, 0, 0, 1, 1, 0)]      // (1,0) → (0,1):  centre is at −I
    [InlineData(0, 1, -1, 0, 0, 1)]     // (0,1) → (−1,0): centre is at −J
    [InlineData(-1, 0, 0, -1, 1, 0)]    // (−1,0) → (0,−1): centre is at +I
    [InlineData(0, -1, 1, 0, 0, 1)]     // (0,−1) → (1,0):  centre is at +J
    public void SingleQuadrantArcs_FindTheirCentre_InEveryQuadrant(
        double x0, double y0, double x1, double y1, double i, double j)
    {
        static string C(double mm) => ((long)Math.Round(mm * 1e6)).ToString();
        string body = $"G74*\nX{C(x0)}Y{C(y0)}D02*\nG03*\nX{C(x1)}Y{C(y1)}I{C(i)}J{C(j)}D01*\nM02*";
        var doc = new GerberParser().Parse(Header + body);

        Assert.Empty(doc.Warnings);
        var draw = Assert.IsType<DrawOp>(Assert.Single(doc.Ops));
        // Every point of the arc lies on the unit circle about the origin, and the arc is a
        // quarter turn — not the three-quarter turn about a mirrored centre that reading the
        // offsets as signed gives in three of these four cases.
        Assert.All(draw.Path, p => Assert.Equal(1e-3, Math.Sqrt(p.X * p.X + p.Y * p.Y), 9));
        double length = 0;
        for (int k = 1; k < draw.Path.Count; k++)
            length += Math.Sqrt(Math.Pow(draw.Path[k].X - draw.Path[k - 1].X, 2)
                                + Math.Pow(draw.Path[k].Y - draw.Path[k - 1].Y, 2));
        Assert.InRange(length, 0.99 * Math.PI / 2 * 1e-3, Math.PI / 2 * 1e-3);
    }

    [Fact]
    public void MultiQuadrantArcs_AreUnchanged_AndG75SwitchesBack()
    {
        // G74 then G75: the second arc is a signed-offset three-quarter turn again.
        string body = "G74*\nG75*\nX1000000Y0D02*\nG03*\nX0Y-1000000I-1000000J0D01*\nM02*";
        var draw = Assert.IsType<DrawOp>(Assert.Single(new GerberParser().Parse(Header + body).Ops));
        double length = 0;
        for (int k = 1; k < draw.Path.Count; k++)
            length += Math.Sqrt(Math.Pow(draw.Path[k].X - draw.Path[k - 1].X, 2)
                                + Math.Pow(draw.Path[k].Y - draw.Path[k - 1].Y, 2));
        Assert.InRange(length, 0.99 * 1.5 * Math.PI * 1e-3, 1.5 * Math.PI * 1e-3);
    }

    // ---------------------------------------------------------------- PCB-15: slots

    [Fact]
    public void AGerberFormatDrillFile_KeepsItsDrawnSlots()
    {
        // One flashed hole and one slot drawn with the same round tool.
        string drill = "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.8*%\nD10*\nX0Y0D03*\n" +
                       "X2000000Y0D02*\nX5000000Y0D01*\nM02*";
        var features = DrillExtractor.Extract(drill);

        Assert.Single(features.Holes);
        var slot = Assert.Single(features.Slots);
        Assert.Equal(0.8e-3, slot.Diameter, 12);
        Assert.Equal(2e-3, slot.Start.X, 12);
        Assert.Equal(5e-3, slot.End.X, 12);
        Assert.Empty(features.Warnings!);
    }

    [Fact]
    public void ExcellonRoutMode_BecomesSlots_AndArcsAreRefused()
    {
        string file = "M48\nMETRIC\nT1C1.000\n%\nT1\nX1.0Y1.0\n" +
                      "G00X10.0Y5.0\nM15\nG01X14.0Y5.0\nG01X14.0Y8.0\nM16\nG05\nX20.0Y20.0\nM30\n";
        var drills = new ExcellonParser().Parse(file);

        Assert.Equal(2, drills.Hits.Count);
        Assert.Equal(2, drills.Slots.Count);
        Assert.Equal(new Point2(10e-3, 5e-3), drills.Slots[0].Start);
        Assert.Equal(14e-3, drills.Slots[0].End.X, 12);
        Assert.Equal(8e-3, drills.Slots[1].End.Y, 12);
        Assert.Equal(1e-3, drills.Slots[1].Diameter, 12);
        Assert.Empty(drills.Warnings);

        // A rapid move without a plunge cuts nothing.
        var dry = new ExcellonParser().Parse(
            "M48\nMETRIC\nT1C1.000\n%\nT1\nG00X10.0Y5.0\nG01X14.0Y5.0\nM30\n");
        Assert.Empty(dry.Slots);

        var ex = Assert.Throws<InvalidDataException>(() => new ExcellonParser().Parse(
            "M48\nMETRIC\nT1C1.000\n%\nT1\nG00X10.0Y5.0\nM15\nG02X14.0Y5.0A2.0\nM16\nM30\n"));
        Assert.Contains("arc", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- PCB-13: plating and span

    [Fact]
    public void ADrillFile_IsPlatedOrNot_ByItsAttribute_NotItsName()
    {
        // Named like a plated file, declared non-plated.
        var nonPlated = GerberLayerClassifier.DrillFunction("board-PTH.gbr",
            "%TF.FileFunction,NonPlated,1,4,NPTH*%\n%FSLAX46Y46*%");
        Assert.False(nonPlated.Plated);
        Assert.True(nonPlated.FromAttribute);

        // A blind drill keeps its span; an Excellon file carries the attribute in a comment.
        var blind = GerberLayerClassifier.DrillFunction("drill.drl",
            "M48\n; #@! TF.FileFunction,Plated,1,2,Blind\nMETRIC\n");
        Assert.True(blind.Plated);
        Assert.Equal((1, 2), (blind.FromLayer, blind.ToLayer));
        Assert.Equal(GerberLayerType.Drill,
            GerberLayerClassifier.Classify("anything.txt", "M48\n; #@! TF.FileFunction,Plated,1,2,Blind\n").Type);

        // No attribute: the name is all there is.
        var byName = GerberLayerClassifier.DrillFunction("board-NPTH.drl", "M48\nMETRIC\n");
        Assert.False(byName.Plated);
        Assert.False(byName.FromAttribute);

        var via = new Via(new Point2(0, 0), 0.3e-3, true, 1, 2);
        Assert.True(via.Reaches(1) && via.Reaches(2));
        Assert.False(via.Reaches(3));
        Assert.True(new Via(new Point2(0, 0), 0.3e-3, true).Reaches(7));
    }

    // ---------------------------------------------------------------- RF-10: probe snap

    [Fact]
    public void AProbeNearTheRim_MovesAnInteriorVertex_NeverTheOutline()
    {
        // 20 × 20 mm plate on a 5 mm grid, probe 1 mm inside the +x edge: the nearest grid
        // vertex is ON the rim. Moving it notched the outline.
        const double size = 20e-3;
        var result = SurfaceMeshBuilder.BuildRectangularPlate(size, size, 5e-3,
            snapVertex: (size / 2 - 1e-3, 0.0));
        Assert.Null(result.FailureReason);
        var structure = result.Structure!;

        Assert.Contains(structure.Vertices, v => Math.Abs(v.X - (size / 2 - 1e-3)) < 1e-12 && Math.Abs(v.Y) < 1e-12);
        // The outline is intact: every rim grid point is still a vertex.
        for (int k = 0; k <= 4; k++)
        {
            double y = -size / 2 + size * k / 4;
            Assert.Contains(structure.Vertices, v => Math.Abs(v.X - size / 2) < 1e-12 && Math.Abs(v.Y - y) < 1e-12);
        }
        // And the squeezed triangles are said.
        Assert.Contains(result.Warnings, w => w.Contains("minimum angle"));

        // A probe off the plate is refused.
        var outside = SurfaceMeshBuilder.BuildRectangularPlate(size, size, 5e-3, snapVertex: (size, 0.0));
        Assert.NotNull(outside.FailureReason);
        Assert.Contains("not inside", outside.FailureReason);
    }

    // ---------------------------------------------------------------- RF-8 / D19: the ledger

    [Fact]
    public void ThePowerLedger_NamesWhereThePowerWent()
    {
        // Lossy: 60 % radiated, 10 % surface wave → 30 % dielectric loss; D = 6.3 (8 dBi)
        // → gain 10·log10(0.6·6.3) = 5.77 dBi.
        string lossy = PowerLedger.Describe(1.0, 0.6, 0.1, 0.02, 6.3);
        Assert.Contains("radiation efficiency P_rad/P_in = 60.0", lossy);
        Assert.Contains("30.0", lossy);
        Assert.Contains("dielectric loss", lossy);
        Assert.Contains("5.77 dBi", lossy);

        // Lossless and closing.
        Assert.Contains("the ledger closes", PowerLedger.Describe(1.0, 0.9, 0.1, 0, 6.3));
        // Lossless and 4 % over: said to be the model's error, not shown as a bare 104 %.
        string over = PowerLedger.Describe(1.0, 0.94, 0.10, 0, 6.3);
        Assert.Contains("should be 100", over);
        Assert.Contains("excess", over);
        // Lossy and over-closed: no dielectric loss can be read off.
        Assert.Contains("MORE than was put in", PowerLedger.Describe(1.0, 0.95, 0.10, 0.02, 6.3));
    }

    // ---------------------------------------------------------------- SI-13: impedances

    private static CoupledLineCrossSection Lines(params (double X, double W)[] traces) =>
        new(new LayeredStackup(new[] { new LayeredStackup.Layer(4.4, 0, 0.2e-3) }), 0,
            traces.Select(t => new TraceCrossSection(t.X, t.W, 35e-6, 5.8e7)).ToArray());

    [Fact]
    public void TheCharacteristicImpedanceMatrix_IsWhatItsLimitsSay()
    {
        // One line: √(L/C).
        var single = LineReadout.CharacteristicImpedance(new[,] { { 3e-7 } }, new[,] { { 1.2e-10 } });
        Assert.Equal(50.0, single[0, 0], 9);

        // Symmetric pair: Z_c11 ± Z_c12 are the even and odd impedances,
        // √((L11 ± L12)/(C11 ± C12)) with the Maxwell C12 negative.
        double l11 = 3.2e-7, l12 = 0.6e-7, c11 = 1.3e-10, c12 = -0.2e-10;
        var pair = LineReadout.CharacteristicImpedance(
            new[,] { { l11, l12 }, { l12, l11 } }, new[,] { { c11, c12 }, { c12, c11 } });
        Assert.Equal(Math.Sqrt((l11 + l12) / (c11 + c12)), pair[0, 0] + pair[0, 1], 9);
        Assert.Equal(Math.Sqrt((l11 - l12) / (c11 - c12)), pair[0, 0] - pair[0, 1], 9);

        // The defining property, on three unequal lines: Z_c·C·Z_c = L.
        var l = new[,] { { 3.0e-7, 0.5e-7, 0.1e-7 }, { 0.5e-7, 3.4e-7, 0.6e-7 }, { 0.1e-7, 0.6e-7, 2.8e-7 } };
        var c = new[,] { { 1.2e-10, -0.2e-10, -0.02e-10 }, { -0.2e-10, 1.1e-10, -0.25e-10 }, { -0.02e-10, -0.25e-10, 1.4e-10 } };
        var z = LineReadout.CharacteristicImpedance(l, c);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double sum = 0;
                for (int a = 0; a < 3; a++)
                    for (int b = 0; b < 3; b++) sum += z[i, a] * c[a, b] * z[b, j];
                Assert.Equal(l[i, j], sum, 15);
                Assert.Equal(z[i, j], z[j, i], 9);
            }
    }

    [Fact]
    public void TheReadout_GivesEachGeometryItsOwnLabels()
    {
        string one = LineReadout.Describe(RlgcExtractor.Extract(Lines((0, 0.3e-3))));
        Assert.Contains("Z0 = ", one);
        Assert.DoesNotContain("Z_even", one);

        string pair = LineReadout.Describe(RlgcExtractor.Extract(Lines((-0.25e-3, 0.3e-3), (0.25e-3, 0.3e-3))));
        Assert.Contains("Z_even", pair);
        Assert.Contains("Z_diff = 2·Z_odd", pair);
        Assert.DoesNotContain("C_m/C11 = -", pair);          // the coupling is printed positive

        // Unequal widths, and three lines: no even/odd impedances are claimed.
        string unequal = LineReadout.Describe(RlgcExtractor.Extract(Lines((-0.25e-3, 0.3e-3), (0.35e-3, 0.5e-3))));
        Assert.DoesNotContain("Z_even =", unequal);
        Assert.Contains("characteristic impedance matrix", unequal);
        string three = LineReadout.Describe(RlgcExtractor.Extract(
            Lines((-0.5e-3, 0.3e-3), (0, 0.3e-3), (0.5e-3, 0.3e-3))));
        Assert.DoesNotContain("Z_even =", three);
        Assert.Contains("characteristic impedance matrix", three);
    }

    // ---------------------------------------------------------------- SI-18: Touchstone

    [Fact]
    public void TheExportGrid_StartsAtDc_AndResolvesTheLinesDelay()
    {
        // 300 mm at 6.7 ns/m is 2 ns; to 10 GHz that needs Δf ≤ 1/(16·2 ns) = 31.25 MHz.
        var grid = LineReadout.ExportFrequencies(10e9, 2e-9);
        Assert.Equal(0, grid[0]);
        Assert.Equal(10e9, grid[^1]);
        double step = grid[1] - grid[0];
        Assert.True(step <= 31.25e6 * (1 + 1e-12), $"step {step / 1e6:g4} MHz");
        for (int k = 2; k < grid.Length; k++) Assert.Equal(step, grid[k] - grid[k - 1], 3);

        // A short line still gets the minimum number of points; a very long one is capped.
        Assert.Equal(41, LineReadout.ExportFrequencies(10e9, 1e-12).Length);
        Assert.Equal(4001, LineReadout.ExportFrequencies(10e9, 1e-6).Length);
    }

    [Fact]
    public void TheTouchstoneFile_SaysWhichPortIsWhich_AndTheNetworkAnswersNearDc()
    {
        var names = LineReadout.PortNames(2);
        Assert.Equal(new[] { "line 1, near end", "line 2, near end", "line 1, far end", "line 2, far end" }, names);

        var rlgc = RlgcExtractor.Extract(Lines((-0.25e-3, 0.3e-3), (0.25e-3, 0.3e-3)));
        var network = new MtlNetwork(new MtlSectionBase[] { new MtlSection(rlgc, 0.1) });
        // The export's DC row is evaluated at 1 Hz: a 100 mm line there is a through
        // connection with its DC resistance in series, R/(R + 2·Z_ref) reflected.
        var s = network.Scattering(1.0);
        double r = rlgc.ResistanceDcOhmsPerMeter[0] * 0.1;
        Assert.Equal(r / (r + 100), s[0, 0].Magnitude, 4);
        Assert.Equal(100 / (r + 100), s[2, 0].Magnitude, 4);

        string text = TouchstoneWriter.Write(new[] { 0.0, 1e9 },
            new[] { s, network.Scattering(1e9) }, portNames: names);
        Assert.Contains("! Port 3: line 1, far end", text);
        Assert.Throws<ArgumentException>(() => TouchstoneWriter.Write(new[] { 1e9 },
            new[] { s }, portNames: new[] { "only one" }));
    }
}
