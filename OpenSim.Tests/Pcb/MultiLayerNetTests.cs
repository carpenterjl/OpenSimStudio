using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// A via-stitched two-layer net must mesh into ONE connected 3D body with each trace at its
/// true z and a solid copper barrel through the dielectric — never collapsed to one plane.
/// </summary>
public class MultiLayerNetTests
{
    private static readonly Material Copper = new()
    {
        Name = "Copper (annealed)", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
        ElectricalConductivity = 5.96e7, ThermalConductivity = 400
    };

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    private const double Cu = 35e-6, Diel = 200e-6, Edge = 0.4e-3;

    /// <summary>
    /// Two overlapping traces (L1 left, L2 right) joined by one plated via with a concentric
    /// annular pad on each layer, plus an end pad on each layer. Extracted through the real
    /// pipeline so the net carries its stitching via.
    /// </summary>
    private static CopperNet TwoLayerNet()
    {
        var islands = new[]
        {
            new CopperIsland(0, 1, "L1", Rect(0, 0, 20e-3, 1e-3)),
            new CopperIsland(1, 2, "L2", Rect(18e-3, 0, 38e-3, 1e-3)),
        };
        var via = new Via(new Point2(19e-3, 0.5e-3), 0.3e-3, Plated: true);
        var pads = new List<CopperPad>
        {
            // Concentric annular rings at the via on both layers → a real cross-layer connection.
            new(1, new Point2(19e-3, 0.5e-3), Rect(18.7e-3, 0.2e-3, 19.3e-3, 0.8e-3), 0.6e-3),
            new(2, new Point2(19e-3, 0.5e-3), Rect(18.7e-3, 0.2e-3, 19.3e-3, 0.8e-3), 0.6e-3),
            // End electrodes, one per layer.
            new(1, new Point2(1e-3, 0.5e-3), Rect(0, 0, 2e-3, 1e-3), 2e-3),
            new(2, new Point2(37e-3, 0.5e-3), Rect(36e-3, 0, 38e-3, 1e-3), 2e-3),
        };
        var nets = NetExtractor.Extract(islands, new[] { via }, pads);
        return nets.Single();
    }

    private static NetMeshOptions Options() => new()
    {
        TargetEdgeLength = Edge,
        LayerThickness = new Dictionary<int, double> { [1] = Cu, [2] = Cu },
        DielectricGapThickness = new Dictionary<int, double> { [1] = Diel },
    };

    private static List<CopperPad> BoardPads() => new()
    {
        new(1, new Point2(19e-3, 0.5e-3), Rect(18.7e-3, 0.2e-3, 19.3e-3, 0.8e-3), 0.6e-3),
        new(2, new Point2(19e-3, 0.5e-3), Rect(18.7e-3, 0.2e-3, 19.3e-3, 0.8e-3), 0.6e-3),
        new(1, new Point2(1e-3, 0.5e-3), Rect(0, 0, 2e-3, 1e-3), 2e-3),
        new(2, new Point2(37e-3, 0.5e-3), Rect(36e-3, 0, 38e-3, 1e-3), 2e-3),
    };

    [Fact]
    public void NetExtractor_RecordsStitchingViaAcrossLayers()
    {
        var net = TwoLayerNet();
        Assert.Equal(new[] { 1, 2 }, net.Layers);
        Assert.Single(net.StitchingVias);
        Assert.Equal(new[] { 1, 2 }, net.StitchingVias[0].Layers);
    }

    [Fact]
    public void MultiLayerNet_MeshSpansBothLayerHeights_AndStaysOneConnectedBody()
    {
        var net = TwoLayerNet();
        var mesh = new NetMesher().MeshNet(net, BoardPads(), Options()).Body.Mesh!;

        // z spans the full copper/dielectric/copper stack — layers are NOT squished together.
        double minZ = mesh.Nodes.Min(n => n.Z), maxZ = mesh.Nodes.Max(n => n.Z);
        Assert.Equal(2 * Cu + Diel, maxZ - minZ, 5e-6);
        // Nodes exist at the top of L1 and the bottom of L2 (distinct heights).
        Assert.Contains(mesh.Nodes, n => n.Z > maxZ - 1e-6);
        Assert.Contains(mesh.Nodes, n => n.Z < minZ + 1e-6);

        // The whole net is a single connected component: the only way L1 (top) and L2 (bottom)
        // connect is through the meshed via barrel. If the barrel were missing this would be 2.
        Assert.Equal(1, ConnectedComponents(mesh));
    }

    [Fact]
    public void MultiLayerNet_TagsElectrodesOnBothOuterLayers()
    {
        var result = new NetMesher().MeshNet(TwoLayerNet(), BoardPads(), Options());
        Assert.Contains(result.Pads, p => p.LayerOrder == 1);
        Assert.Contains(result.Pads, p => p.LayerOrder == 2);
    }

    [Fact]
    public void MultiLayerNet_ResistanceThroughBarrel_IsFiniteAndPlausible()
    {
        var result = new NetMesher().MeshNet(TwoLayerNet(), BoardPads(), Options());
        int source = result.Pads.Where(p => p.LayerOrder == 1).OrderBy(p => p.Center.X).First().FaceId;
        int sink = result.Pads.Where(p => p.LayerOrder == 2).OrderByDescending(p => p.Center.X).First().FaceId;

        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = result.Body.Mesh!,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Source", FaceIds = new[] { source }, Volts = 0.01 },
                new VoltagePotential { Name = "Sink", FaceIds = new[] { sink }, Volts = 0 }
            }
        });

        Assert.NotNull(output.Summary);
        double r = output.Summary!["Resistance (Ω)"];

        // Two ~18 mm trace runs in series (pad-to-via on each layer) plus the short barrel.
        double rho = 1.0 / Copper.ElectricalConductivity!.Value;
        double approx = 2 * rho * 18e-3 / (1e-3 * Cu);
        Assert.True(r > 0 && double.IsFinite(r));
        Assert.InRange(r, approx * 0.3, approx * 2.5);
    }

    [Fact]
    public void SingleLayerNet_ZExtentIsJustCopperThickness()
    {
        var net = new CopperNet(1, new[] { new CopperIsland(0, 1, "L1", Rect(0, 0, 20e-3, 1e-3)) });
        var mesh = new NetMesher().MeshNet(net, null,
            new NetMeshOptions { TargetEdgeLength = Edge, CopperThickness = Cu }).Body.Mesh!;
        double extent = mesh.Nodes.Max(n => n.Z) - mesh.Nodes.Min(n => n.Z);
        Assert.Equal(Cu, extent, 1e-9);
    }

    /// <summary>Number of connected components of the mesh under element (shared-node) adjacency.</summary>
    private static int ConnectedComponents(FeMesh mesh)
    {
        var parent = Enumerable.Range(0, mesh.NodeCount).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) => parent[Find(a)] = Find(b);
        foreach (var e in mesh.Elements)
        {
            Union(e.N0, e.N1); Union(e.N0, e.N2); Union(e.N0, e.N3);
        }
        var used = new HashSet<int>();
        foreach (var e in mesh.Elements)
            foreach (int n in new[] { e.N0, e.N1, e.N2, e.N3 })
                used.Add(Find(n));
        return used.Count;
    }
}
