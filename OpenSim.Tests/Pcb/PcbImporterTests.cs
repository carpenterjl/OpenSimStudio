using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Core.Results;
using OpenSim.Pcb;
using OpenSim.Pcb.Extrude;
using OpenSim.Solvers;
using Xunit;

namespace OpenSim.Tests.Pcb;

public class PcbImporterTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Pcb", "Fixtures", name);

    private static Material FromLibrary(string name) =>
        new MaterialLibrary().Materials.Single(m => m.Name == name);

    [Fact]
    public void CopperOnlyImport_ProducesSolvableSingleRegionBody()
    {
        var result = new PcbImporter().Import(new PcbImportRequest
        {
            CopperGerberPath = Fixture("two_pads_trace.gbr"),
            TargetEdgeLength = 0.4e-3
        });

        var body = result.Body;
        Assert.NotNull(body.Mesh);
        Assert.NotNull(body.Geometry);
        Assert.True(body.Mesh!.ElementCount > 0);
        Assert.All(Enumerable.Range(0, body.Mesh.ElementCount), e => Assert.Equal(0, body.Mesh.RegionOf(e)));

        var copper = FromLibrary("Copper (annealed)");
        var faceIds = body.Mesh.BoundaryTriangles.Select(t => t.FaceId).Distinct().ToList();
        // Solving needs a reference potential; ground one side face, drive another.
        var sideFaces = faceIds.Where(f => f >= 2).ToList();
        Assert.True(sideFaces.Count >= 2, "The board outline should yield multiple selectable side faces.");

        var output = new ElectricalConductionSolver().Solve(new SolveInput
        {
            Mesh = body.Mesh,
            Material = copper,
            BoundaryConditions = new BoundaryCondition[]
            {
                new VoltagePotential { Name = "A", FaceIds = new[] { sideFaces[0] }, Volts = 0 },
                new VoltagePotential { Name = "B", FaceIds = new[] { sideFaces[^1] }, Volts = 0.01 }
            }
        });
        Assert.Contains(output.Fields, f => f.Name == "Electric potential");
    }

    [Fact]
    public void OutlineImport_ProducesMultiRegionBoardBody()
    {
        var result = new PcbImporter().Import(new PcbImportRequest
        {
            CopperGerberPath = Fixture("two_pads_trace.gbr"),
            OutlineGerberPath = Fixture("board_outline.gbr"),
            DrillPath = Fixture("holes.drl"),
            TargetEdgeLength = 0.8e-3
        });

        var mesh = result.Body.Mesh!;
        Assert.Contains(Enumerable.Range(0, mesh.ElementCount), e => mesh.RegionOf(e) == PcbStackup.CopperRegion);
        Assert.Contains(Enumerable.Range(0, mesh.ElementCount), e => mesh.RegionOf(e) == PcbStackup.DielectricRegion);

        Assert.NotNull(result.Body.RegionMaterialNames);
        Assert.Equal("Copper (annealed)", result.Body.RegionMaterialNames![PcbStackup.CopperRegion]);
        Assert.Equal("FR4 (PCB laminate)", result.Body.RegionMaterialNames[PcbStackup.DielectricRegion]);
    }

    [Fact]
    public void ImportedPcbProject_RoundTripsThroughOssproj()
    {
        var result = new PcbImporter().Import(new PcbImportRequest
        {
            CopperGerberPath = Fixture("two_pads_trace.gbr"),
            OutlineGerberPath = Fixture("board_outline.gbr"),
            TargetEdgeLength = 1e-3
        });

        var project = new SimProject
        {
            Name = "Board",
            AnalysisType = "JouleCoupled",
            Stackup = new PcbStackupSettings { CopperThickness = 35e-6, BoardThickness = 1.6e-3 }
        };
        project.Bodies.Add(result.Body);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = serializer.Load(path);

            Assert.Equal("JouleCoupled", loaded.AnalysisType);
            Assert.Equal(35e-6, loaded.Stackup!.CopperThickness);
            var body = Assert.Single(loaded.Bodies);
            Assert.Equal(result.Body.Mesh!.ElementCount, body.Mesh!.ElementCount);
            Assert.Equal(result.Body.Mesh.TotalVolume(), body.Mesh.TotalVolume(), 12);
            // Region ids and per-region materials survive the round trip.
            Assert.Equal(result.Body.Mesh.RegionOf(0), body.Mesh.RegionOf(0));
            Assert.Equal("FR4 (PCB laminate)", body.RegionMaterialNames![PcbStackup.DielectricRegion]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
