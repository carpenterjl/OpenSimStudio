using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Core.Results;
using OpenSim.Core.Geometry2D;
using OpenSim.Pcb.Geometry2D;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Meshing2D;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class PcbArchiveImportTests
{
    private static string Zip => Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", "example_board.zip");

    [Fact]
    public void RealAltiumArchive_ClassifiesLayersByFileFunction()
    {
        var files = PcbArchive.Read(Zip);
        var layers = files.Select(f => GerberLayerClassifier.Classify(f.Name, f.Text)).ToList();

        Assert.Contains(layers, l => l.Type == GerberLayerType.Profile);
        Assert.Contains(layers, l => l.Type == GerberLayerType.Drill);
        Assert.Contains(layers, l => l.Type == GerberLayerType.CopperPlane || l.Type == GerberLayerType.CopperSignal);
        // The top signal copper is tagged L1/Top.
        Assert.Contains(layers, l => l.Type == GerberLayerType.CopperSignal && l.IsTopSide && l.CopperOrder == 1);
    }

    [Fact]
    public void RealAltiumArchive_ImportsSolvableBoard()
    {
        // The regression that reproduces the user's crash: a real Altium board with a
        // stroked, arc-tessellated outline and hundreds of drilled holes must import and
        // mesh without throwing, and produce a thermally solvable domain.
        var result = new PcbBoardImporter().ImportArchive(Zip, new BoardImportOptions());

        var mesh = result.Body.Mesh!;
        Assert.True(mesh.ElementCount > 100, "The board should mesh into a non-trivial number of elements.");
        for (int e = 0; e < mesh.ElementCount; e++)
            Assert.True(mesh.ElementVolume(e) > 0, $"Element {e} has non-positive volume.");
        Assert.All(mesh.BoundaryTriangles, bt => Assert.True(bt.FaceId >= 0));

        // Board-only import is a single FR4 region, ready for a heat-conduction solve:
        // fix temperature on one face, inject heat on another.
        var fr4 = new MaterialLibrary().Materials.Single(m => m.Name == result.Body.RegionMaterialNames![0]);
        var faces = mesh.BoundaryTriangles.Select(t => t.FaceId).Distinct().OrderBy(f => f).ToList();
        var output = new HeatConductionSolver().Solve(new SolveInput
        {
            Mesh = mesh,
            Material = fr4,
            BoundaryConditions = new BoundaryCondition[]
            {
                new FixedTemperature { Name = "Sink", FaceIds = new[] { faces[0] }, Kelvin = 300 },
                new HeatFlux { Name = "Heat", FaceIds = new[] { faces[^1] }, TotalPower = 1.0 }
            }
        });
        var temperature = (NodalScalarField)output.Fields.Single(f => f.Name == "Temperature");
        Assert.All(Enumerable.Range(0, mesh.NodeCount), n => Assert.True(temperature.Values[n] >= 299.9));
    }

    [Fact]
    public void RealAltiumArchive_RoundTripsAsProject()
    {
        var result = new PcbBoardImporter().ImportArchive(Zip, new BoardImportOptions());
        var project = new SimProject { Name = "USBC board", AnalysisType = "Thermal" };
        project.Bodies.Add(result.Body);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = serializer.Load(path);
            var body = Assert.Single(loaded.Bodies);
            Assert.Equal(result.Body.Mesh!.ElementCount, body.Mesh!.ElementCount);
        }
        finally { File.Delete(path); }
    }
}

public class Cdt2DRobustnessTests
{
    // A concave, arc-like outline peppered with sub-micron near-duplicate points — the
    // shape of a real Gerber board outline — must mesh without a recovery failure.
    [Fact]
    public void NearDegenerateOutline_MeshesWithoutError()
    {
        var pts = new List<Point2>();
        // An L-shaped outline sampled with deliberate near-duplicate spurs.
        void Edge(Point2 a, Point2 b, int n)
        {
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / n;
                pts.Add(a + (b - a) * t);
                if (i % 3 == 0) pts.Add(a + (b - a) * t + new Point2(1e-8, -1e-8)); // spur
            }
        }
        Edge(new(0, 0), new(20e-3, 0), 20);
        Edge(new(20e-3, 0), new(20e-3, 8e-3), 10);
        Edge(new(20e-3, 8e-3), new(8e-3, 8e-3), 12);
        Edge(new(8e-3, 8e-3), new(8e-3, 20e-3), 12);
        Edge(new(8e-3, 20e-3), new(0, 20e-3), 8);
        Edge(new(0, 20e-3), new(0, 0), 20);

        var cleaned = PolygonCleaner.Clean(new[] { new Polygon2(pts) });
        var mesh = new PlanarMesher().Mesh(new[] { new PlanarRegion(0, cleaned) }, 1.5e-3);

        Assert.True(mesh.Triangles.Count > 20);
        double area = mesh.Triangles.Sum(t =>
            Math.Abs(Point2.Cross(mesh.Points[t.B] - mesh.Points[t.A], mesh.Points[t.C] - mesh.Points[t.A])) / 2);
        // L-shape area = 20×8 + 8×12 = 160 + 96 = 256 mm².
        Assert.Equal(256e-6, area, 256e-6 * 1e-2);
    }

    [Fact]
    public void PolygonCleaner_RemovesSpursButKeepsShape()
    {
        var ring = new List<Point2>
        {
            new(0, 0), new(1e-9, 0), new(10e-3, 0), new(10e-3, 1e-9),
            new(10e-3, 5e-3), new(5e-3, 5e-3 + 2e-9), new(0, 5e-3)
        };
        var cleaned = PolygonCleaner.CleanRing(ring, 1e-6);
        Assert.True(cleaned.Count <= 5, "Near-duplicate and collinear points should be dropped.");
        Assert.True(Math.Abs(Polygon2.RingArea(cleaned)) > 40e-6, "The rectangle's area must be preserved.");
    }
}
