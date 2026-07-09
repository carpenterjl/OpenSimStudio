using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Results;
using OpenSim.Pcb.Extrude;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class PcbMeshTests
{
    private static readonly Material Copper = new()
    {
        Name = "Copper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
        ElectricalConductivity = 5.96e7, ThermalConductivity = 400
    };

    private static Polygon2 Rect(double x0, double y0, double x1, double y1) =>
        new(new[] { new Point2(x0, y0), new Point2(x1, y0), new Point2(x1, y1), new Point2(x0, y1) });

    private static PlanarMesh RectMesh(double w, double h, double edge, int regionId = 0) =>
        new PlanarMesher().Mesh(new[] { new PlanarRegion(regionId, new[] { Rect(0, 0, w, h) }) }, edge);

    [Fact]
    public void ExtrudedTrace_IsWatertightWithCorrectVolume()
    {
        var planar = RectMesh(10e-3, 2e-3, 1e-3);
        var surface = PolygonExtruder.Extrude(planar, 0, 0, 35e-6);

        Assert.True(surface.IsWatertight(), "Extruded surface must be closed.");
        double expected = 10e-3 * 2e-3 * 35e-6;
        Assert.Equal(expected, Math.Abs(surface.ComputeSignedVolume()), expected * 1e-3);

        // Every boundary triangle carries a face id; top/bottom/side ids are all present.
        var faceIds = surface.TriangleFaceIds.Distinct().ToList();
        Assert.Contains(0, faceIds);                                  // top
        Assert.Contains(1, faceIds);                                  // bottom
        Assert.Contains(faceIds, f => f >= 2);                        // side walls
    }

    [Fact]
    public void CopperOnlyMesh_HasPositiveVolumeAndValidBoundary()
    {
        var planar = RectMesh(10e-3, 2e-3, 1e-3);
        var mesh = new PcbMeshGenerator().GenerateCopperOnly(planar, 35e-6);

        double expected = 10e-3 * 2e-3 * 35e-6;
        Assert.Equal(expected, mesh.TotalVolume(), expected * 1e-2);
        for (int e = 0; e < mesh.ElementCount; e++)
            Assert.True(mesh.ElementVolume(e) > 0, $"Element {e} has non-positive volume.");

        // Single region → all elements region 0; every boundary triangle has a valid face id.
        Assert.All(Enumerable.Range(0, mesh.ElementCount), e => Assert.Equal(0, mesh.RegionOf(e)));
        Assert.All(mesh.BoundaryTriangles, bt => Assert.True(bt.FaceId >= 0));

        // The boundary skin is closed: every triangle edge is shared by exactly two
        // boundary triangles (a watertight surface).
        var edgeCount = new Dictionary<(int, int), int>();
        foreach (var bt in mesh.BoundaryTriangles)
            foreach (var (u, v) in new[] { (bt.A, bt.B), (bt.B, bt.C), (bt.C, bt.A) })
            {
                var k = u < v ? (u, v) : (v, u);
                edgeCount[k] = edgeCount.GetValueOrDefault(k) + 1;
            }
        Assert.All(edgeCount.Values, c => Assert.Equal(2, c));
    }

    [Fact]
    public void MultiRegionMesh_IsConformalAtInterface()
    {
        // Copper (region 0) sitting on a board (region 1) of the same footprint.
        var copper = new PlanarRegion(PcbStackup.CopperRegion, new[] { Rect(0, 0, 8e-3, 2e-3) });
        var board = new PlanarRegion(PcbStackup.DielectricRegion, new[] { Rect(0, 0, 8e-3, 2e-3) });
        var planar = new PlanarMesher().Mesh(new[] { copper, board }, 1e-3);

        var mesh = new PcbMeshGenerator().Generate(planar,
            PcbStackup.CopperOnBoard(copperThickness: 35e-6, boardThickness: 200e-6));

        // Both regions present.
        Assert.Contains(Enumerable.Range(0, mesh.ElementCount), e => mesh.RegionOf(e) == PcbStackup.CopperRegion);
        Assert.Contains(Enumerable.Range(0, mesh.ElementCount), e => mesh.RegionOf(e) == PcbStackup.DielectricRegion);

        // Region volumes match the analytic slabs.
        double copperVol = 0, boardVol = 0;
        for (int e = 0; e < mesh.ElementCount; e++)
            (mesh.RegionOf(e) == PcbStackup.CopperRegion ? ref copperVol : ref boardVol) += mesh.ElementVolume(e);
        Assert.Equal(8e-3 * 2e-3 * 35e-6, copperVol, 8e-3 * 2e-3 * 35e-6 * 2e-2);
        Assert.Equal(8e-3 * 2e-3 * 200e-6, boardVol, 8e-3 * 2e-3 * 200e-6 * 2e-2);

        // Conformity: no two distinct nodes occupy the same location — the copper/board
        // interface shares nodes rather than duplicating them.
        var seen = new Dictionary<(long, long, long), int>();
        foreach (var n in mesh.Nodes)
        {
            var key = ((long)Math.Round(n.X * 1e12), (long)Math.Round(n.Y * 1e12), (long)Math.Round(n.Z * 1e12));
            Assert.False(seen.ContainsKey(key), "Duplicate node at an interface breaks conformity.");
            seen[key] = 1;
        }
    }

    [Fact]
    public void ExtrudedTrace_DcSolveMatchesResistanceFormula()
    {
        // A straight copper trace; voltage across the two end walls → R = ρL/A.
        const double length = 10e-3, width = 1e-3, thickness = 35e-6, volts = 1e-3;
        var planar = RectMesh(length, width, 0.5e-3);
        var mesh = new PcbMeshGenerator().GenerateCopperOnly(planar, thickness);

        // The end walls have outward normals ±x → distinct side-wall face ids. Find them
        // by the average x of each side face's triangles.
        var sideFaces = mesh.BoundaryTriangles.Where(t => t.FaceId >= 2).Select(t => t.FaceId).Distinct();
        int FaceAtX(double targetX) => sideFaces
            .OrderBy(f => Math.Abs(AverageX(mesh, f) - targetX)).First();
        int x0Face = FaceAtX(0), x1Face = FaceAtX(length);
        Assert.NotEqual(x0Face, x1Face);

        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = Copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "Ground", FaceIds = new[] { x0Face }, Volts = 0 },
                new VoltagePotential { Name = "Supply", FaceIds = new[] { x1Face }, Volts = volts }
            }
        });

        var power = (ElementScalarField)output.Fields.Single(
            f => f.Name == ElectricalConductionSolver.ElementPowerFieldName);
        double dissipated = Enumerable.Range(0, mesh.ElementCount).Sum(e => power.Values[e] * mesh.ElementVolume(e));
        double solvedR = volts * volts / dissipated;

        double analyticR = length / (Copper.ElectricalConductivity!.Value * width * thickness);
        Assert.Equal(analyticR, solvedR, analyticR * 2e-2);
    }

    private static double AverageX(FeMesh mesh, int faceId)
    {
        var tris = mesh.BoundaryTriangles.Where(t => t.FaceId == faceId).ToList();
        return tris.Average(t => (mesh.Nodes[t.A].X + mesh.Nodes[t.B].X + mesh.Nodes[t.C].X) / 3);
    }
}
