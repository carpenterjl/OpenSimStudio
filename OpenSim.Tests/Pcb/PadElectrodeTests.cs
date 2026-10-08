using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Core.Results;
using OpenSim.Pcb.Extrude;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class PadElectrodeTests
{
    private static readonly Material Copper = new()
    {
        Name = "Copper (annealed)", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
        ElectricalConductivity = 5.96e7, ThermalConductivity = 400
    };

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    /// <summary>A straight trace with a pad flash at each end, on one layer, as a net.</summary>
    private static (CopperNet Net, System.Collections.Generic.List<CopperPad> Pads) TraceWithPads()
    {
        var trace = new CopperIsland(0, 1, "L1", Rect(0, 0, 20e-3, 1e-3));
        var net = new CopperNet(1, new[] { trace });
        var pads = new System.Collections.Generic.List<CopperPad>
        {
            new(1, new Point2(1e-3, 0.5e-3), Rect(0, 0, 2e-3, 1e-3), 2e-3),        // left end
            new(1, new Point2(19e-3, 0.5e-3), Rect(18e-3, 0, 20e-3, 1e-3), 2e-3)   // right end
        };
        return (net, pads);
    }

    [Fact]
    public void MeshedNet_TagsEachPadTopFaceDistinctly()
    {
        var (net, pads) = TraceWithPads();
        var result = new NetMesher().MeshNet(net, pads, new NetMeshOptions { TargetEdgeLength = 0.4e-3 });

        Assert.Equal(2, result.Pads.Count);
        Assert.Equal(2, result.Pads.Select(p => p.FaceId).Distinct().Count());
        Assert.All(result.Pads, p => Assert.True(p.FaceId >= PcbMeshGenerator.PadFaceBase));
        // Each tagged face actually exists on the mesh boundary.
        var faceIds = result.Body.Mesh!.BoundaryTriangles.Select(t => t.FaceId).ToHashSet();
        Assert.All(result.Pads, p => Assert.Contains(p.FaceId, faceIds));
    }

    [Fact]
    public void PadToPad_ResistanceSolveMatchesTraceFormula_AndAppearsInSummary()
    {
        const double length = 20e-3, width = 1e-3, thickness = 35e-6, volts = 0.01;
        var (net, pads) = TraceWithPads();
        var result = new NetMesher().MeshNet(net, pads,
            new NetMeshOptions { TargetEdgeLength = 0.4e-3, CopperThickness = thickness });
        var mesh = result.Body.Mesh!;

        int sourceFace = result.Pads.OrderBy(p => p.Center.X).First().FaceId;
        int sinkFace = result.Pads.OrderByDescending(p => p.Center.X).First().FaceId;

        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Source", FaceIds = new[] { sourceFace }, Volts = volts },
                new VoltagePotential { Name = "Sink", FaceIds = new[] { sinkFace }, Volts = 0 }
            }
        });

        // Resistance is now a first-class summary value.
        Assert.NotNull(output.Summary);
        Assert.True(output.Summary!.ContainsKey("Resistance (Ω)"));
        double solvedR = output.Summary["Resistance (Ω)"];

        // Pad-to-pad length is ~16 mm (between the inner pad edges); bound generously since
        // the exact current path depends on pad size. Just assert the right order of magnitude
        // and a positive finite value near ρL/A.
        double rho = 1.0 / Copper.ElectricalConductivity!.Value;
        double approxR = rho * length / (width * thickness);
        Assert.InRange(solvedR, approxR * 0.4, approxR * 1.2);
    }

    [Fact]
    public void PerLayerThickness_ScalesResistanceInversely()
    {
        var (net, pads) = TraceWithPads();   // net is on layer 1

        double SolveR(double thicknessUm)
        {
            var result = new NetMesher().MeshNet(net, pads, new NetMeshOptions
            {
                TargetEdgeLength = 0.4e-3,
                LayerThickness = new Dictionary<int, double> { [1] = thicknessUm * 1e-6 }
            });
            var mesh = result.Body.Mesh!;
            int src = result.Pads.OrderBy(p => p.Center.X).First().FaceId;
            int snk = result.Pads.OrderByDescending(p => p.Center.X).First().FaceId;
            var output = new ElectricalConductionSolver().Solve(new SolveInput
            {
                Mesh = mesh,
                Material = Copper,
                BoundaryConditions = new BoundaryCondition[]
                {
                    new VoltagePotential { Name = "S", FaceIds = new[] { src }, Volts = 0.01 },
                    new VoltagePotential { Name = "K", FaceIds = new[] { snk }, Volts = 0 }
                }
            });
            return output.Summary!["Resistance (Ω)"];
        }

        // Doubling copper thickness (35 → 70 µm) roughly halves the resistance.
        double thin = SolveR(35);
        double thick = SolveR(70);
        Assert.Equal(0.5, thick / thin, 0.05);
    }

    /// <summary>Area of the boundary faces carrying a face id [m²].</summary>
    private static double TaggedArea(FeMesh mesh, int faceId) => mesh.BoundaryTriangles
        .Where(t => t.FaceId == faceId)
        .Sum(t => 0.5 * Vector3D.Cross(mesh.Nodes[t.B] - mesh.Nodes[t.A], mesh.Nodes[t.C] - mesh.Nodes[t.A]).Length);

    /// <summary>The area a pad outline can gain or lose to the polygon engine's snap and weld:
    /// its perimeter times 10 nm, far below any copper feature (an unimprinted pad was off by
    /// a fraction of an element, ~0.1 mm).</summary>
    private static double Snap(Polygon2 pad) =>
        10e-9 * pad.Outer.Select((p, i) => (pad.Outer[(i + 1) % pad.Outer.Count] - p).Length).Sum();

    private static Polygon2 Disc(double cx, double cy, double r, int n) =>
        new(Enumerable.Range(0, n).Select(i => new Point2(
            cx + r * Math.Cos(2 * Math.PI * i / n), cy + r * Math.Sin(2 * Math.PI * i / n))).ToList());

    [Fact]
    public void APadSmallerThanAnElement_IsAnElectrodeOfExactlyItsOwnArea()
    {
        // A 0.4 × 0.3 mm pad and a 0.25 mm round pad in a 10 mm pour meshed at 1 mm: the pads
        // are constraints of the mesh, so each is an electrode and its face is the pad, no
        // more and no less (PCB-17: before, a pad got whichever triangles had their centre
        // inside it — none, or triangles larger than the pad).
        var pour = new CopperIsland(0, 1, "L1", Rect(0, 0, 10e-3, 10e-3));
        var net = new CopperNet(1, new[] { pour });
        var square = Rect(3.1e-3, 4.2e-3, 3.5e-3, 4.5e-3);
        var round = Disc(7.3e-3, 6.1e-3, 0.125e-3, 24);
        var pads = new System.Collections.Generic.List<CopperPad>
        {
            new(1, new Point2(3.3e-3, 4.35e-3), square, 0.4e-3),
            new(1, new Point2(7.3e-3, 6.1e-3), round, 0.25e-3)
        };
        var result = new NetMesher().MeshNet(net, pads, new NetMeshOptions { TargetEdgeLength = 1e-3 });

        Assert.Equal(2, result.Pads.Count);
        var mesh = result.Body.Mesh!;
        Assert.Equal(square.Area(), TaggedArea(mesh, result.Pads.Single(p => p.Center.X < 5e-3).FaceId), Snap(square));
        Assert.Equal(round.Area(), TaggedArea(mesh, result.Pads.Single(p => p.Center.X > 5e-3).FaceId), Snap(round));
    }

    [Fact]
    public void OnAMultiLayerNet_EachPadFaceIsItsPad()
    {
        // The same check through the multi-layer path: copper on L1 and L2, a pad on each.
        var top = new CopperIsland(0, 1, "L1", Rect(0, 0, 8e-3, 3e-3));
        var bottom = new CopperIsland(1, 2, "L2", Rect(2e-3, 0, 10e-3, 3e-3));
        var net = new CopperNet(1, new[] { top, bottom });
        var padTop = Rect(0.7e-3, 1.1e-3, 1.3e-3, 1.9e-3);
        var padBottom = Disc(9.1e-3, 1.4e-3, 0.3e-3, 32);
        var pads = new System.Collections.Generic.List<CopperPad>
        {
            new(1, new Point2(1e-3, 1.5e-3), padTop, 0.8e-3),
            new(2, new Point2(9.1e-3, 1.4e-3), padBottom, 0.6e-3)
        };
        var result = new NetMesher().MeshNet(net, pads, new NetMeshOptions { TargetEdgeLength = 1e-3 });

        Assert.Equal(2, result.Pads.Count);
        var mesh = result.Body.Mesh!;
        Assert.Equal(padTop.Area(), TaggedArea(mesh, result.Pads.Single(p => p.LayerOrder == 1).FaceId), Snap(padTop));
        Assert.Equal(padBottom.Area(), TaggedArea(mesh, result.Pads.Single(p => p.LayerOrder == 2).FaceId), Snap(padBottom));
    }

    [Fact]
    public void RealBoard_SmallNetMeshesWithPadElectrodes()
    {
        string zip = Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", "example_board.zip");
        var board = new PcbBoardReader().Read(zip);
        // A small single-layer net that has pads.
        var net = board.Nets
            .Where(n => n.IsSingleLayer && n.Area is > 1e-7 and < 1e-5)
            .OrderByDescending(n => n.Area)
            .First();

        var result = new NetMesher().MeshNet(net, board.Pads, new NetMeshOptions());
        Assert.True(result.Body.Mesh!.ElementCount > 0);
        // Most real signal nets have at least one pad; if this net has pads they are tagged.
        Assert.All(result.Pads, p => Assert.True(p.FaceId >= PcbMeshGenerator.PadFaceBase));
    }
}
