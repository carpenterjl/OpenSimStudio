using System.Text.Json;
using OpenSim.Cfd;
using OpenSim.Cli;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Core.Results;
using OpenSim.Tests.Solvers;
using Xunit;
using Xunit.Abstractions;

namespace OpenSim.Tests.Cli;

/// <summary>
/// FU-43: conjugate heat flow from the command line — the project's saved CFD case and
/// environment, the flow march cap and the transient-momentum switch as options — and the
/// CFD case (fans included) surviving a save.
/// </summary>
public class ConjugateCliTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public ConjugateCliTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "opensim-cli-conjugate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const double Ambient = 293.15, Power = 5.0;

    private static EnvironmentSettings Stream() => new()
    {
        Medium = MediumKind.MovingFluid,
        AmbientTemperature = Ambient,
        FlowVelocity = new Vector3D(0.01, 0, 0),
        CustomFluid = FluidProperties.Constant("test air",
            density: 1.2, dynamicViscosity: 1.8e-5, thermalConductivity: 0.026,
            specificHeat: 1005, thermalExpansion: 3.4e-3),
        IncludeRadiation = false,
        Gravity = new Vector3D(0, 0, 0)
    };

    private static CfdSettings Grid(int maxSteps) =>
        CfdSettings.ForExternalFlow(new Vector3D(0.01, 0, 0)) with
        {
            DomainBox = new Aabb(new Vector3D(0, 0, 0), new Vector3D(1.5, 0.5, 0.5)),
            CellSize = 0.5 / 12,
            SteadyTolerance = 1e-6,
            MaxSteps = maxSteps
        };

    private string WriteProject(int maxSteps)
    {
        var material = StructuredBoxMesh.Conductor("aluminium-ish", 200);
        var body = StructuredBoxMesh.Box("block", 0.4, 0.6, 0.15, 0.35, 0.15, 0.35, 2, 2, 2, material);
        body.HeatSourcePower = Power;
        var project = new SimProject { Name = "Block in a stream", AnalysisType = "ConjugateThermal" };
        project.Bodies.Add(body);
        project.Environment = Stream();
        project.Cfd = Grid(maxSteps);
        string path = Path.Combine(_dir, "block.ossproj");
        new ProjectSerializer().Save(project, path);
        return path;
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliRunner.Run(args, stdout, stderr);
        _out.WriteLine($"$ opensim {string.Join(' ', args)}  → {code}");
        if (stderr.ToString().Length > 0) _out.WriteLine("stderr: " + stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void ConjugateThermal_FromTheCommandLine_IsTheStudyItself_AndTheMarchCapIsAnOption()
    {
        string project = WriteProject(maxSteps: 40);
        string outDir = Path.Combine(_dir, "results");

        // The saved cap is too low for this case: a typed failure naming it.
        var (code, _, err) = Run("solve", project, "--steady", "--out", outDir);
        Assert.Equal(2, code);
        Assert.Contains("did not converge in 40 steps", err);

        (code, var stdout, err) = Run("solve", project, "--steady", "--max-steps", "60000", "--out", outDir);
        Assert.Equal(0, code);
        Assert.Contains("flow march cap 60000 steps", stdout);
        Assert.Contains("Conjugate exchange settled", stdout);
        Assert.Contains("Outlets: the stream carries", stdout);
        var outputs = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(outDir, "summary.json")))!;

        // The same study run directly: the command line adds nothing and loses nothing.
        var material = StructuredBoxMesh.Conductor("aluminium-ish", 200);
        var body = StructuredBoxMesh.Box("block", 0.4, 0.6, 0.15, 0.35, 0.15, 0.35, 2, 2, 2, material);
        var assembled = FeMeshAssembler.Assemble(new[] { body });
        double volume = assembled.Mesh.TotalVolume();
        var direct = ConjugateHeatStudy.Run(new SolveInput
        {
            Mesh = assembled.Mesh,
            Material = material,
            BoundaryConditions = assembled.BoundaryConditions,
            RegionMaterials = assembled.RegionMaterials,
            ElementHeatSource = Enumerable.Repeat(Power / volume, assembled.Mesh.ElementCount).ToArray()
        }, assembled.NodeBases, Stream(), Grid(60000));
        double directMax = ((NodalScalarField)direct.Thermal.Fields.First(f => f.Name == "Temperature")).Values.Max();
        _out.WriteLine($"max temperature: command line {outputs["Max Temperature (K)"]:F6} K, direct {directMax:F6} K");
        Assert.Equal(directMax, outputs["Max Temperature (K)"], 1e-6);
        Assert.StartsWith("# Block in a stream — ConjugateThermal", File.ReadAllText(Path.Combine(outDir, "report.md")));
    }

    [Fact]
    public void TheCfdCase_FanAndSwitchesIncluded_SurvivesASave()
    {
        var fan = new FanCurve { Points = new[] { new FanPoint(0, 40), new FanPoint(0.01, 22), new FanPoint(0.02, 0) } };
        var project = new SimProject { Name = "Fan box" };
        project.Cfd = new CfdSettings
        {
            MaxSteps = 12345,
            TransientMomentum = true,
            Openings = new[]
            {
                new FlowOpening
                {
                    Face = BoxFace.XMin, UMin = 0, UMax = 0.1, VMin = 0, VMax = 0.1,
                    Velocity = new Vector3D(1, 0, 0), Fan = fan
                }
            }
        };
        string path = Path.Combine(_dir, "fan.ossproj");
        new ProjectSerializer().Save(project, path);
        var back = new ProjectSerializer().Load(path).Cfd!;
        Assert.Equal(12345, back.MaxSteps);
        Assert.True(back.TransientMomentum);
        Assert.Equal(fan.Points, back.Openings[0].Fan!.Points);
        Assert.Equal(22, back.Openings[0].Fan!.PressureAt(0.01), 12);
    }
}
