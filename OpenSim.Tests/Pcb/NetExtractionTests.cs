using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Core.Results;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class NetExtractionTests
{
    private static string Zip => Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", "example_board.zip");

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    [Fact]
    public void ViaBridgesLayers_OnlyWhenAPadCoversItOnEachLayer()
    {
        // Two islands on different layers, not touching in-plane.
        var top = new CopperIsland(0, 1, "top", Rect(0, 0, 2e-3, 1e-3));
        var bot = new CopperIsland(1, 2, "bot", Rect(0, 0, 2e-3, 1e-3));
        var viaPos = new Point2(1e-3, 0.5e-3);
        var noPads = Array.Empty<CopperPad>();
        CopperPad Pad(int layer) => new(layer, viaPos, Rect(viaPos.X - 0.3e-3, viaPos.Y - 0.3e-3,
            viaPos.X + 0.3e-3, viaPos.Y + 0.3e-3), 0.6e-3);

        // No via → two nets.
        Assert.Equal(2, NetExtractor.Extract(new[] { top, bot }, Array.Empty<Via>(), noPads).Count);

        // Plated via WITH a pad on each layer → one net spanning both (a real via connection).
        var withPads = NetExtractor.Extract(new[] { top, bot },
            new[] { new Via(viaPos, 0.3e-3, Plated: true) }, new[] { Pad(1), Pad(2) });
        var net = Assert.Single(withPads);
        Assert.Equal(new[] { 1, 2 }, net.Layers);

        // THE FIX: a plated via with NO pad (a signal via passing through copper) must NOT
        // bridge — this is what stops planes swallowing every net.
        Assert.Equal(2, NetExtractor.Extract(new[] { top, bot },
            new[] { new Via(viaPos, 0.3e-3, Plated: true) }, noPads).Count);

        // A pad on only one layer → no bridge (needs an annular ring on both).
        Assert.Equal(2, NetExtractor.Extract(new[] { top, bot },
            new[] { new Via(viaPos, 0.3e-3, Plated: true) }, new[] { Pad(1) }).Count);

        // An OFF-CENTRE pad (a pour-fill flash that merely overlaps the drill, not a real
        // annular ring) must NOT bridge — this is the concentric requirement.
        CopperPad OffsetPad(int layer) => new(layer, new Point2(viaPos.X + 2e-3, viaPos.Y),
            Rect(viaPos.X + 1e-3, viaPos.Y - 1e-3, viaPos.X + 3e-3, viaPos.Y + 1e-3), 2e-3);
        Assert.Equal(2, NetExtractor.Extract(new[] { top, bot },
            new[] { new Via(viaPos, 0.3e-3, Plated: true) }, new[] { OffsetPad(1), OffsetPad(2) }).Count);

        // Non-plated via never bridges, pads or not.
        Assert.Equal(2, NetExtractor.Extract(new[] { top, bot },
            new[] { new Via(viaPos, 0.3e-3, Plated: false) }, new[] { Pad(1), Pad(2) }).Count);
    }

    [Fact]
    public void RealBoard_ExtractsManyNetsWithViaStitchedPlanes()
    {
        var board = new PcbBoardReader().Read(Zip);

        Assert.True(board.Islands.Count > 100, "A real board layer has many copper islands.");
        Assert.True(board.Nets.Count > 10, "The board should resolve into many distinct nets.");
        Assert.NotEmpty(board.Pads);
        Assert.Contains(board.Vias, v => v.Plated);
        // Plated vias stitch copper across the layers present in the fixture into one net.
        Assert.Contains(board.Nets, n => n.Layers.Count >= 2);
        // Nets are ordered largest-first.
        Assert.True(board.Nets[0].Area >= board.Nets[^1].Area);
    }

    [Fact]
    public void RealBoard_NoSingleNetSwallowsTheBoard()
    {
        // The over-stitching symptom was one giant net dominating the copper. With the
        // annular-pad rule the fixture board resolves into many nets, none dominant.
        var board = new PcbBoardReader().Read(Zip);
        double totalCopper = board.Islands.Sum(i => i.Area);
        double largestFraction = board.Nets.Max(n => n.Area) / totalCopper;
        Assert.True(largestFraction < 0.35,
            $"Largest net is {largestFraction:p0} of copper — a net is swallowing the board.");
        Assert.True(board.Nets.Count > 100, "A dense board should resolve into hundreds of nets.");
    }

    [Fact]
    public void PadExtractor_KeepsPadSizedFlashesAndExcludesPours()
    {
        // A small round pad (0.6 mm), a small rectangle pad (0.8 mm), and an oversized
        // flash (10 mm, a pour) — only the two pad-sized flashes are pads.
        var doc = new OpenSim.Pcb.Gerber.GerberParser().Parse(
            "%FSLAX46Y46*%\n%MOMM*%\n%ADD10C,0.6*%\n%ADD11R,0.8X0.5*%\n%ADD12C,10.0*%\n" +
            "D10*\nX1000000Y1000000D03*\nD11*\nX2000000Y1000000D03*\nD12*\nX5000000Y5000000D03*\nM02*");
        var pads = PadExtractor.Extract(doc, layerOrder: 1);

        Assert.Equal(2, pads.Count);
        Assert.All(pads, p => Assert.True(p.Size <= PadExtractor.PadSizeMax));
        Assert.All(pads, p => Assert.Equal(1, p.LayerOrder));
        Assert.DoesNotContain(pads, p => p.Size > 5e-3);
    }

    [Fact]
    public void RealBoard_SmallNetMeshesFastAndSolvesForResistance()
    {
        var board = new PcbBoardReader().Read(Zip);
        // Pick a small single-layer signal net — a trace, not a plane.
        var net = board.Nets
            .Where(n => n.IsSingleLayer && n.Area is > 1e-7 and < 1e-5)
            .OrderByDescending(n => n.Area)
            .First();

        var result = new NetMesher().MeshNet(net);
        var mesh = result.Body.Mesh!;
        Assert.True(mesh.ElementCount > 0);
        for (int e = 0; e < mesh.ElementCount; e++)
            Assert.True(mesh.ElementVolume(e) > 0);

        // Two opposite side-wall faces can carry a voltage difference for a DC solve.
        var copper = new MaterialLibrary().Materials.Single(m => m.Name == "Copper (annealed)");
        var sideFaces = mesh.BoundaryTriangles.Where(t => t.FaceId >= 2).Select(t => t.FaceId).Distinct().ToList();
        Assert.True(sideFaces.Count >= 2);

        double AvgX(int face) => mesh.BoundaryTriangles.Where(t => t.FaceId == face)
            .Average(t => (mesh.Nodes[t.A].X + mesh.Nodes[t.B].X + mesh.Nodes[t.C].X) / 3);
        int lo = sideFaces.OrderBy(AvgX).First();
        int hi = sideFaces.OrderByDescending(AvgX).First();

        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "A", FaceIds = new[] { lo }, Volts = 0 },
                new VoltagePotential { Name = "B", FaceIds = new[] { hi }, Volts = 0.01 }
            }
        });
        var power = (ElementScalarField)output.Fields.Single(
            f => f.Name == ElectricalConductionSolver.ElementPowerFieldName);
        double dissipated = Enumerable.Range(0, mesh.ElementCount).Sum(e => power.Values[e] * mesh.ElementVolume(e));
        Assert.True(dissipated > 0, "A driven copper net must dissipate power.");
    }

    [Fact]
    public void OverComplexNet_MeshesWithAdvisoryWarning()
    {
        // The old ~3000-vertex hard cap is gone: a finely tessellated pour outline
        // meshes (the pipeline handles plane/pour nets now); above the advisory
        // threshold the user gets a heads-up in the log instead of a rejection.
        // 25 000 points around a circle exceed the ~20 000-vertex advisory.
        var ring = new List<Point2>();
        for (int i = 0; i < 25_000; i++)
        {
            double a = 2 * Math.PI * i / 25_000;
            ring.Add(new Point2(0.02 * Math.Cos(a), 0.02 * Math.Sin(a)));
        }
        var plane = new CopperNet(1, new[] { new CopperIsland(0, 1, "plane", new Polygon2(ring)) });

        var result = new NetMesher().MeshNet(plane);
        Assert.True(result.Body.Mesh!.ElementCount > 0);
        Assert.Contains(result.Warnings, w => w.Contains("take a while"));
    }
}
