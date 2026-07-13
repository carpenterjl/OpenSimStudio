using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Geometry;
using OpenSim.Meshing;
using Xunit;

namespace OpenSim.Tests.Core;

public class ProjectSerializerTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsFullProject()
    {
        var geometry = PrimitiveFactory.CreateBox(0.1, 0.02, 0.01);
        var mesh = new DelaunayMeshGenerator().Generate(geometry, new MeshSettings { TargetEdgeLength = 0.008 });

        var body = new Body
        {
            Name = "Beam",
            GeometrySource = "Box 0.1×0.02×0.01 m",
            Geometry = geometry,
            MeshSettings = new MeshSettings { TargetEdgeLength = 0.008 },
            Mesh = mesh,
            Material = new Material
            {
                Name = "Test steel", YoungsModulus = 200e9, PoissonRatio = 0.3, Density = 7850
            }
        };
        body.BoundaryConditions.Add(new FixedSupport { Name = "Wall", FaceIds = new[] { 0 } });
        body.BoundaryConditions.Add(new ForceLoad
        {
            Name = "Tip", FaceIds = new[] { 1 }, TotalForce = new Vector3D(0, 0, -100)
        });
        body.BoundaryConditions.Add(new PressureLoad
        {
            Name = "Top", FaceIds = new[] { 5 }, Magnitude = 2e5
        });

        var project = new SimProject { Name = "Cantilever" };
        project.Bodies.Add(body);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = serializer.Load(path);

            Assert.Equal("Cantilever", loaded.Name);
            var loadedBody = Assert.Single(loaded.Bodies);
            Assert.Equal("Beam", loadedBody.Name);
            Assert.Equal(geometry.Vertices.Count, loadedBody.Geometry!.Vertices.Count);
            Assert.Equal(geometry.ComputeSignedVolume(), loadedBody.Geometry.ComputeSignedVolume(), 12);
            Assert.Equal(mesh.NodeCount, loadedBody.Mesh!.NodeCount);
            Assert.Equal(mesh.ElementCount, loadedBody.Mesh.ElementCount);
            Assert.Equal(mesh.TotalVolume(), loadedBody.Mesh.TotalVolume(), 12);
            Assert.Equal(0.008, loadedBody.MeshSettings.TargetEdgeLength);
            Assert.Equal("Test steel", loadedBody.Material!.Name);
            Assert.Equal(200e9, loadedBody.Material.YoungsModulus);

            Assert.Equal(3, loadedBody.BoundaryConditions.Count);
            Assert.IsType<FixedSupport>(loadedBody.BoundaryConditions[0]);
            var force = Assert.IsType<ForceLoad>(loadedBody.BoundaryConditions[1]);
            Assert.Equal(new Vector3D(0, 0, -100), force.TotalForce);
            var pressure = Assert.IsType<PressureLoad>(loadedBody.BoundaryConditions[2]);
            Assert.Equal(2e5, pressure.Magnitude);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PcbStackup_PerGapMaterials_RoundTrip_AndOldProjectsDefaultToEmpty()
    {
        // Stage F-data: per-gap εr/tanδ persist alongside the thicknesses (parallel init
        // lists, the same pattern the thickness list already uses). An older .ossproj with
        // no material lists must load them as empty (⇒ the VM re-seeds FR4), never crash.
        var project = new SimProject
        {
            Name = "Board",
            Stackup = new PcbStackupSettings
            {
                CopperThickness = 18e-6,
                BoardThickness = 1.6e-3,
                DielectricGapThicknesses = new[] { 0.8e-3, 0.4e-3 },
                DielectricGapPermittivities = new[] { 4.4, 3.66 },
                DielectricGapLossTangents = new[] { 0.02, 0.004 }
            }
        };

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = serializer.Load(path);

            Assert.NotNull(loaded.Stackup);
            Assert.Equal(18e-6, loaded.Stackup!.CopperThickness);
            Assert.Equal(new[] { 4.4, 3.66 }, loaded.Stackup.DielectricGapPermittivities);
            Assert.Equal(new[] { 0.02, 0.004 }, loaded.Stackup.DielectricGapLossTangents);
            Assert.Equal(new[] { 0.8e-3, 0.4e-3 }, loaded.Stackup.DielectricGapThicknesses);

            // A default-constructed stackup (an older project) carries empty material lists.
            var bare = new PcbStackupSettings();
            Assert.Empty(bare.DielectricGapPermittivities);
            Assert.Empty(bare.DielectricGapLossTangents);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class Phase2PersistenceTests
{
    [Fact]
    public void ElectricalAndThermalBoundaryConditions_RoundTrip()
    {
        var body = new Body { Name = "Trace" };
        body.BoundaryConditions.Add(new VoltagePotential { Name = "Vcc", FaceIds = new[] { 2 }, Volts = 3.3 });
        body.BoundaryConditions.Add(new CurrentFlow { Name = "Load", FaceIds = new[] { 3 }, TotalCurrent = 1.5 });
        body.BoundaryConditions.Add(new FixedTemperature { Name = "Sink", FaceIds = new[] { 4 }, Kelvin = 295 });
        body.BoundaryConditions.Add(new HeatFlux { Name = "Chip", FaceIds = new[] { 5 }, TotalPower = 2.5 });
        body.BoundaryConditions.Add(new Convection
        {
            Name = "Air", FaceIds = new[] { 6 }, Coefficient = 12, AmbientTemperature = 298
        });
        var project = new SimProject { Name = "PCB" };
        project.Bodies.Add(body);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loaded = Assert.Single(serializer.Load(path).Bodies);

            Assert.Equal(5, loaded.BoundaryConditions.Count);
            Assert.Equal(3.3, Assert.IsType<VoltagePotential>(loaded.BoundaryConditions[0]).Volts);
            Assert.Equal(1.5, Assert.IsType<CurrentFlow>(loaded.BoundaryConditions[1]).TotalCurrent);
            Assert.Equal(295, Assert.IsType<FixedTemperature>(loaded.BoundaryConditions[2]).Kelvin);
            Assert.Equal(2.5, Assert.IsType<HeatFlux>(loaded.BoundaryConditions[3]).TotalPower);
            var convection = Assert.IsType<Convection>(loaded.BoundaryConditions[4]);
            Assert.Equal(12, convection.Coefficient);
            Assert.Equal(298, convection.AmbientTemperature);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FeMeshRegionIds_RoundTrip_AndOldFormatLoadsWithoutThem()
    {
        var geometry = PrimitiveFactory.CreateBox(0.05, 0.02, 0.01);
        var meshed = new DelaunayMeshGenerator().Generate(geometry, new MeshSettings { TargetEdgeLength = 0.008 });
        var regions = Enumerable.Range(0, meshed.ElementCount).Select(e => e % 2).ToArray();
        var mesh = new FeMesh(meshed.Nodes, meshed.Elements, meshed.BoundaryTriangles, regions);

        var body = new Body { Name = "Two-region", Mesh = mesh };
        var project = new SimProject { Name = "Regions" };
        project.Bodies.Add(body);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ossproj");
        try
        {
            var serializer = new ProjectSerializer();
            serializer.Save(project, path);
            var loadedMesh = Assert.Single(serializer.Load(path).Bodies).Mesh!;

            Assert.NotNull(loadedMesh.ElementRegionIds);
            Assert.Equal(regions, loadedMesh.ElementRegionIds);
            Assert.Equal(1, loadedMesh.RegionOf(1));
        }
        finally
        {
            File.Delete(path);
        }

        // A milestone-1-era mesh payload has no elementRegionIds property; it must
        // still deserialize, defaulting to the single-region behaviour.
        const string oldJson = """
        {
            "nodes": [ {"x":0,"y":0,"z":0}, {"x":1,"y":0,"z":0}, {"x":0,"y":1,"z":0}, {"x":0,"y":0,"z":1} ],
            "elements": [ {"n0":0,"n1":1,"n2":2,"n3":3} ],
            "boundaryTriangles": [ {"a":0,"b":2,"c":1,"faceId":0} ]
        }
        """;
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };
        var oldMesh = System.Text.Json.JsonSerializer.Deserialize<FeMesh>(oldJson, options)!;
        Assert.Null(oldMesh.ElementRegionIds);
        Assert.Equal(0, oldMesh.RegionOf(0));
        Assert.Equal(1, oldMesh.ElementCount);
    }
}

// Material library semantics are covered in MaterialLibraryTests.cs (temp-dir seam —
// the public constructor reads the machine-wide AppData file and must not run in tests).
