using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Pcb.Polygons;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Pcb;

/// <summary>
/// The meshed copper must match the source trace/pad/via geometry: per-layer boundaries
/// are imprinted (no triangle straddles a copper edge), trace loops keep their open
/// interior, and via barrels are hollow annuli around an open bore — plus the preview's
/// per-island layer filtering and the winding guard at union entry points.
/// </summary>
public class GeometryFidelityTests
{
    private static readonly IPolygonOps Ops = new ClipperPolygonOps();

    private static readonly Material Copper = new()
    {
        Name = "Copper (annealed)", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
        ElectricalConductivity = 5.96e7, ThermalConductivity = 400
    };

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    private const double Cu = 35e-6;

    // ---------------- per-island layer filtering ----------------

    [Fact]
    public void VisibleIslands_FiltersPerIsland_NotPerNet()
    {
        var spansBoth = new CopperNet(1, new[]
        {
            new CopperIsland(0, 1, "L1", Rect(0, 0, 1e-3, 1e-3)),
            new CopperIsland(1, 2, "L2", Rect(0, 0, 1e-3, 1e-3)),
        });
        var topOnly = new CopperNet(2, new[] { new CopperIsland(2, 1, "L1", Rect(2e-3, 0, 3e-3, 1e-3)) });
        var nets = new[] { spansBoth, topOnly };

        // Disabling L2 removes exactly the L2 island; the multi-layer net's L1 copper stays.
        var visible = NetVisibility.VisibleIslands(nets, _ => true, layer => layer != 2).ToList();
        Assert.Equal(2, visible.Count);
        Assert.All(visible, i => Assert.Equal(1, i.LayerOrder));

        // A net's own toggle removes all of its islands regardless of layers.
        visible = NetVisibility.VisibleIslands(nets, net => net.Id != 1, _ => true).ToList();
        Assert.Single(visible);
        Assert.Equal(2, visible[0].Index);
    }

    // ---------------- winding guard ----------------

    [Fact]
    public void MiswoundHole_StillSubtractsInUnion()
    {
        // Hole deliberately wound the SAME way (CCW) as the outer — under NonZero fill an
        // unguarded union would fill it. The OrientedRings guard must keep it open.
        var outer = Rect(0, 0, 10e-3, 10e-3).Outer;
        var holeCcw = Rect(4e-3, 4e-3, 6e-3, 6e-3).Outer;
        Assert.True(Polygon2.RingArea(holeCcw) > 0);             // confirm mis-wound input

        var result = Ops.Union(Polygon2.OrientedRings(new Polygon2(outer, new[] { holeCcw })));
        Assert.Single(result);
        Assert.Single(result[0].Holes);
        Assert.Equal(100e-6 - 4e-6, result[0].Area(), 1e-9);
    }

    // ---------------- arrangement fidelity: crossing layers ----------------

    [Fact]
    public void CrossingLayers_MeshHugsEachLayersOwnCopper()
    {
        // A horizontal strip on L1 crossing a vertical strip on L2. Their outlines cross
        // inside the union, so without boundary imprinting triangles straddle them and the
        // per-layer copper grows jagged extra area — the reported bug.
        var l1 = Rect(0, 4e-3, 10e-3, 6e-3);
        var l2 = Rect(4e-3, 0, 6e-3, 10e-3);
        var net = new CopperNet(1, new[]
        {
            new CopperIsland(0, 1, "L1", l1),
            new CopperIsland(1, 2, "L2", l2),
        });
        var mesh = new NetMesher().MeshNet(net, null, new NetMeshOptions
        {
            TargetEdgeLength = 0.5e-3,
            LayerThickness = new Dictionary<int, double> { [1] = Cu, [2] = Cu },
            DielectricGapThickness = new Dictionary<int, double> { [1] = 200e-6 },
        }).Body.Mesh!;

        // Stack: L2 [0, 35 µm], gap, L1 [235, 270 µm].
        double vol1 = 0, vol2 = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var el = mesh.Elements[e];
            double z = (mesh.Nodes[el.N0].Z + mesh.Nodes[el.N1].Z + mesh.Nodes[el.N2].Z + mesh.Nodes[el.N3].Z) / 4;
            double v = Math.Abs(mesh.ElementVolume(e));
            var c = Centroid2(mesh, e);
            if (z > 200e-6)
            {
                vol1 += v;
                Assert.True(PlanarMesher.ContainsPoint(new[] { l1 }, c),
                    $"L1 element centroid ({c.X * 1e3:g4}, {c.Y * 1e3:g4}) mm lies outside the L1 strip.");
            }
            else
            {
                vol2 += v;
                Assert.True(PlanarMesher.ContainsPoint(new[] { l2 }, c),
                    $"L2 element centroid ({c.X * 1e3:g4}, {c.Y * 1e3:g4}) mm lies outside the L2 strip.");
            }
        }
        // Meshed footprint area per layer ≈ that layer's own polygon area (20 mm² each).
        Assert.Equal(l1.Area(), vol1 / Cu, l1.Area() * 0.025);
        Assert.Equal(l2.Area(), vol2 / Cu, l2.Area() * 0.025);
    }

    // ---------------- loop trace ----------------

    [Fact]
    public void LoopTrace_InteriorStaysOpen()
    {
        // A closed rectangular loop trace, stroked 0.4 mm wide. The enclosed interior is
        // NOT copper and must survive union + meshing as a hole.
        var path = new[]
        {
            new Point2(0, 0), new Point2(5e-3, 0), new Point2(5e-3, 5e-3),
            new Point2(0, 5e-3), new Point2(0, 0)
        };
        var islands = Ops.Union(Ops.StrokeOpenPath(path, 0.2e-3));
        Assert.Single(islands);
        Assert.Single(islands[0].Holes);                          // the loop interior

        var net = new CopperNet(1, new[] { new CopperIsland(0, 1, "L1", islands[0]) });
        var mesh = new NetMesher().MeshNet(net, null, new NetMeshOptions
        {
            TargetEdgeLength = 0.3e-3, CopperThickness = Cu
        }).Body.Mesh!;

        double volume = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            volume += Math.Abs(mesh.ElementVolume(e));
            // Nothing may be meshed inside the loop (center of the loop is deep interior).
            var c = Centroid2(mesh, e);
            Assert.True((c - new Point2(2.5e-3, 2.5e-3)).Length > 1.5e-3,
                "Mesh element found inside the open loop interior.");
        }
        // Footprint ≈ the ring's area, far below the filled outline's ~27 mm².
        Assert.Equal(islands[0].Area(), volume / Cu, islands[0].Area() * 0.05);
    }

    // ---------------- annular via barrel ----------------

    [Fact]
    public void ViaBarrel_IsHollowAnnulus_WithOpenBore()
    {
        // Two stacked pads joined by one plated via. The barrel through the dielectric
        // must be an annulus of the plating thickness around an open bore.
        var center = new Point2(1e-3, 1e-3);
        double bore = 0.15e-3, plating = 25e-6, gap = 0.5e-3;
        var via = new Via(center, 2 * bore, Plated: true);
        var net = new CopperNet(1, new[]
        {
            new CopperIsland(0, 1, "L1", Rect(0, 0, 2e-3, 2e-3)),
            new CopperIsland(1, 2, "L2", Rect(0, 0, 2e-3, 2e-3)),
        })
        { StitchingVias = new[] { new ViaBridge(via, new[] { 1, 2 }) } };

        var pads = new List<CopperPad>
        {
            new(1, center, Rect(0.7e-3, 0.7e-3, 1.3e-3, 1.3e-3), 0.6e-3),
            new(2, center, Rect(0.7e-3, 0.7e-3, 1.3e-3, 1.3e-3), 0.6e-3),
        };
        var result = new NetMesher().MeshNet(net, pads, new NetMeshOptions
        {
            TargetEdgeLength = 0.25e-3,
            LayerThickness = new Dictionary<int, double> { [1] = Cu, [2] = Cu },
            DielectricGapThickness = new Dictionary<int, double> { [1] = gap },
            ViaPlatingThickness = plating,
        });
        var mesh = result.Body.Mesh!;

        // Stack: L2 [0, 35 µm], gap [35, 535 µm], L1 [535, 570 µm].
        double gapVolume = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var el = mesh.Elements[e];
            double z = (mesh.Nodes[el.N0].Z + mesh.Nodes[el.N1].Z + mesh.Nodes[el.N2].Z + mesh.Nodes[el.N3].Z) / 4;
            var c = Centroid2(mesh, e);
            // The drilled bore is open copper-free space at every height.
            Assert.True((c - center).Length > 0.7 * bore,
                $"Mesh element inside the via bore at z = {z * 1e6:g3} µm.");
            if (z > Cu + 1e-6 && z < Cu + gap - 1e-6)
                gapVolume += Math.Abs(mesh.ElementVolume(e));
        }
        // Copper between the layers = the plated wall only: π((r+t)² − r²)·gap.
        double wallArea = Math.PI * (Math.Pow(bore + plating, 2) - bore * bore);
        Assert.Equal(wallArea * gap, gapVolume, wallArea * gap * 0.12);

        // Electrically the thin wall dominates: R ≈ ρ·gap/wallArea plus pad spreading.
        int source = result.Pads.First(p => p.LayerOrder == 1).FaceId;
        int sink = result.Pads.First(p => p.LayerOrder == 2).FaceId;
        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Source", FaceIds = new[] { source }, Volts = 0.01 },
                new VoltagePotential { Name = "Sink", FaceIds = new[] { sink }, Volts = 0 }
            }
        });
        double rho = 1.0 / Copper.ElectricalConductivity!.Value;
        double rBarrel = rho * gap / wallArea;
        double r = output.Summary!["Resistance (Ω)"];
        Assert.InRange(r, 0.5 * rBarrel, 3.0 * rBarrel);
    }

    private static Point2 Centroid2(FeMesh mesh, int e)
    {
        var el = mesh.Elements[e];
        double x = (mesh.Nodes[el.N0].X + mesh.Nodes[el.N1].X + mesh.Nodes[el.N2].X + mesh.Nodes[el.N3].X) / 4;
        double y = (mesh.Nodes[el.N0].Y + mesh.Nodes[el.N1].Y + mesh.Nodes[el.N2].Y + mesh.Nodes[el.N3].Y) / 4;
        return new Point2(x, y);
    }
}
