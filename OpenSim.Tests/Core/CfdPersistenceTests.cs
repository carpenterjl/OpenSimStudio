using System.Text.Json;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;

namespace OpenSim.Tests.Core;

/// <summary>
/// A CFD case that cannot be saved cannot be re-run, and a number nobody can re-run is
/// not reproducible — so the domain, the grid, the working fluid and every opening ride
/// on the project file. And, as with every model addition here, a project written before
/// any of it existed must still load.
/// </summary>
public class CfdPersistenceTests
{
    private static string RoundTrip(SimProject project)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cfd-{Guid.NewGuid():N}.ossproj");
        try
        {
            new ProjectSerializer().Save(project, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static SimProject Load(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cfd-{Guid.NewGuid():N}.ossproj");
        try
        {
            File.WriteAllText(path, json);
            return new ProjectSerializer().Load(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ACfdCase_RoundTripsWithItsDomainOpeningsAndFluid()
    {
        var project = new SimProject { Name = "heat exchanger" };
        project.Bodies.Add(new Body { Name = "copper" });
        project.Bodies.Add(new Body { Name = "water", Role = BodyRole.FluidRegion });
        project.Cfd = CfdSettings.ForInternalFlow(
            new Aabb(new Vector3D(0, 0.005, 0.010), new Vector3D(0.300, 0.195, 0.040)),
            new[]
            {
                new FlowOpening
                {
                    Face = BoxFace.XMin, UMin = 0.170, UMax = 0.190, VMin = 0.015, VMax = 0.035,
                    Kind = FlowFaceKind.InletVelocity, Velocity = new Vector3D(0.1, 0, 0),
                    Temperature = 363.15
                },
                new FlowOpening
                {
                    Face = BoxFace.XMax, UMin = 0.010, UMax = 0.030, VMin = 0.015, VMax = 0.035,
                    Kind = FlowFaceKind.OutletPressure
                }
            },
            FluidLibrary.Water.Name,
            new CfdSettings { CellSize = 0.0025, MaxSteps = 20000 });

        var loaded = Load(RoundTrip(project));

        Assert.Equal(BodyRole.Solid, loaded.Bodies[0].Role);
        Assert.Equal(BodyRole.FluidRegion, loaded.Bodies[1].Role);

        var cfd = Assert.IsType<CfdSettings>(loaded.Cfd);
        Assert.Equal(FlowRegime.Internal, cfd.Regime);
        Assert.Equal(FluidLibrary.Water.Name, cfd.FluidName);
        Assert.False(cfd.ResolvesSurroundings);
        Assert.Equal(0.0025, cfd.CellSize, 12);
        Assert.Equal(20000, cfd.MaxSteps);
        Assert.Equal(0.300, cfd.DomainBox!.Value.Max.X, 12);
        Assert.Equal(0.010, cfd.DomainBox!.Value.Min.Z, 12);

        Assert.Equal(2, cfd.Openings.Count);
        var inlet = cfd.Openings[0];
        Assert.Equal(BoxFace.XMin, inlet.Face);
        Assert.Equal(FlowFaceKind.InletVelocity, inlet.Kind);
        Assert.Equal(0.1, inlet.Velocity.X, 12);
        Assert.Equal(363.15, inlet.Temperature!.Value, 12);
        Assert.Equal(0.190, inlet.UMax, 12);
        Assert.Null(cfd.Openings[1].Temperature);
        foreach (var face in new[] { BoxFace.YMin, BoxFace.ZMax })
            Assert.Equal(FlowFaceKind.Wall, cfd.FaceKind(face));
    }

    [Fact]
    public void AProjectWrittenBeforeAnyOfThisExisted_StillLoads()
    {
        // Verbatim shape of an older file: no Cfd member, no Role on the body. Both must
        // land on their defaults — no CFD case, and a body that is ordinary material.
        // camelCase, because that is what the serializer writes and therefore what every
        // file on disk actually contains.
        const string json = """
        {
          "name": "old project",
          "analysisType": "Thermal",
          "bodies": [ { "name": "block", "geometrySource": "box" } ]
        }
        """;

        var loaded = Load(json);

        Assert.Null(loaded.Cfd);
        Assert.Single(loaded.Bodies);
        Assert.Equal(BodyRole.Solid, loaded.Bodies[0].Role);
    }

    [Fact]
    public void AnOpeningWithoutATemperature_StaysNullThroughTheFile()
    {
        // Null means "the ambient", and it must survive as null rather than being written
        // as the ambient of the day — the two mean different things on reload.
        var project = new SimProject();
        project.Cfd = new CfdSettings
        {
            Openings = new[]
            {
                new FlowOpening
                {
                    Face = BoxFace.ZMin, UMin = 0, UMax = 1, VMin = 0, VMax = 1,
                    Kind = FlowFaceKind.InletVelocity, Velocity = new Vector3D(0, 0, 1)
                }
            }
        };

        string json = RoundTrip(project);
        var loaded = Load(json);

        Assert.Null(loaded.Cfd!.Openings[0].Temperature);
        Assert.True(loaded.Cfd.ResolvesSurroundings);
        Assert.Equal(FlowRegime.External, loaded.Cfd.Regime);
    }
}
