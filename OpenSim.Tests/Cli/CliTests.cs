using System.Globalization;
using System.Text.Json;
using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Geometry;
using Xunit.Abstractions;

namespace OpenSim.Tests.Cli;

/// <summary>
/// Feature 15: the command line. A box between two fixed temperatures is linear in z, so
/// every number the commands write is known; the impedance sweep is checked for the
/// direction a wider trace must move Z0; the spacing command runs on a two-net Gerber
/// written here.
/// </summary>
public class CliTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public CliTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "opensim-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliRunner.Run(args, stdout, stderr);
        _out.WriteLine($"$ opensim {string.Join(' ', args)}  → {code}");
        _out.WriteLine(stdout.ToString());
        if (stderr.ToString().Length > 0) _out.WriteLine("stderr: " + stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A 10 × 10 × 20 mm bar, 300 K at z = 0 and 400 K at z = 20 mm.</summary>
    private string WriteBarProject(string name = "bar")
    {
        var body = new Body
        {
            Name = "Bar",
            Geometry = PrimitiveFactory.CreateBox(0.01, 0.01, 0.02),
            GeometrySource = "Box 10 × 10 × 20 mm",
            MeshSettings = new MeshSettings { TargetEdgeLength = 0.005, Method = MeshMethod.StructuredLattice },
            Material = new Material
            {
                Name = "TestCopper", YoungsModulus = 110e9, PoissonRatio = 0.34, Density = 8960,
                ThermalConductivity = 400, SpecificHeat = 385, ElectricalConductivity = 5.96e7
            }
        };
        body.BoundaryConditions.Add(new FixedTemperature { Name = "cold", FaceIds = new[] { 4 }, Kelvin = 300 });
        body.BoundaryConditions.Add(new FixedTemperature { Name = "hot", FaceIds = new[] { 5 }, Kelvin = 400 });
        var project = new SimProject { Name = "Bar", AnalysisType = "Thermal" };
        project.Bodies.Add(body);
        string path = Path.Combine(_dir, name + ".ossproj");
        new ProjectSerializer().Save(project, path);
        return path;
    }

    private static List<string[]> Csv(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => l.Split(',')).ToList();

    [Fact]
    public void Usage_AndBadArguments()
    {
        Assert.Equal(1, Run().Code);
        Assert.Equal(0, Run("help").Code);
        Assert.Contains("opensim solve", Run("help").Out);
        var (code, _, err) = Run("bogus");
        Assert.Equal(1, code);
        Assert.Contains("Unknown command", err);
        (code, _, err) = Run("solve");
        Assert.Equal(1, code);
        Assert.Contains("needs a project file", err);
        (code, _, err) = Run("solve", Path.Combine(_dir, "missing.ossproj"));
        Assert.Equal(2, code);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void Solve_MeshesTheProjectAndWritesTheReport()
    {
        string project = WriteBarProject();
        string outDir = Path.Combine(_dir, "results");
        var (code, stdout, err) = Run("solve", project, "--out", outDir, "--fields");
        Assert.Equal(0, code);
        Assert.Equal("", err);
        Assert.Contains("Meshed 'Bar'", stdout);

        string report = File.ReadAllText(Path.Combine(outDir, "report.md"));
        _out.WriteLine(report);
        Assert.StartsWith("# Bar — Thermal", report);
        Assert.Contains("| Temperature | K | 300 | 400 |", report);
        Assert.Contains("| Condition 'hot' | FixedTemperature on 1 face(s): 400 K |", report);
        Assert.Contains("## Solver log", report);
        Assert.True(File.Exists(Path.Combine(outDir, "report.html")));
        Assert.True(File.Exists(Path.Combine(outDir, "log.txt")));
        Assert.True(File.Exists(Path.Combine(outDir, "fields", "Temperature.csv")));

        var summary = File.ReadAllLines(Path.Combine(outDir, "summary.csv"));
        Assert.Contains(summary, l => l.StartsWith("# analysis: Thermal"));
        Assert.Contains(summary, l => l.StartsWith("Temperature,K,node,"));

        var outputs = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(outDir, "summary.json")))!;
        Assert.Equal(400, outputs["Max Temperature (K)"], 1e-6);
        Assert.Equal(300, outputs["Min Temperature (K)"], 1e-6);

        // The analysis can be overridden; one the project cannot do is refused by name.
        (code, _, err) = Run("solve", project, "--out", outDir, "--analysis", "Modal");
        Assert.Equal(2, code);
        Assert.False(string.IsNullOrWhiteSpace(err));
        (code, _, err) = Run("solve", project, "--out", outDir, "--analysis", "Nonsense");
        Assert.Equal(2, code);
        Assert.Contains("Unknown analysis", err);
    }

    [Fact]
    public void Sweep_VariesAConditionAndTheMesh_AndTabulatesTheOutputs()
    {
        string project = WriteBarProject();
        string job = Path.Combine(_dir, "sweep.json");
        File.WriteAllText(job, """
            {
              "project": "bar.ossproj",
              "mode": "OneAtATime",
              "parameters": [
                { "name": "hot", "target": "condition:hot.value", "values": [350, 400, 450], "unit": "K" },
                { "name": "edge", "target": "mesh:Bar.targetEdgeLength", "values": [0.005, 0.0025] }
              ],
              "outputs": [ "Max Temperature (K)", "Min Temperature (K)" ]
            }
            """);
        string outDir = Path.Combine(_dir, "sweep");
        var (code, stdout, err) = Run("sweep", job, "--out", outDir);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        Assert.Contains("6 run(s)", stdout);
        Assert.Contains("d(Max Temperature (K))/d(hot) = 1", stdout);

        var rows = Csv(Path.Combine(outDir, "sweep.csv"));
        Assert.Equal("run,hot [K],edge,Max Temperature (K),Min Temperature (K),error", string.Join(",", rows[0]));
        Assert.Equal(7, rows.Count);
        foreach (var row in rows.Skip(1))
        {
            double hot = double.Parse(row[1], CultureInfo.InvariantCulture);
            Assert.Equal(hot, double.Parse(row[3], CultureInfo.InvariantCulture), 1e-6);
            Assert.Equal(300, double.Parse(row[4], CultureInfo.InvariantCulture), 1e-6);
            Assert.Equal("", row[5]);
        }
        Assert.Equal("0.0025", rows[6][2]);                                   // the finer mesh ran

        string report = File.ReadAllText(Path.Combine(outDir, "sweep-report.md"));
        Assert.Contains("## Sensitivities (one at a time)", report);
        Assert.Contains("## Runs", report);
        Assert.Contains("| hot | condition:hot.value: 350, 400, 450 K (nominal 400) |", report);

        // An unknown target is a failed run, reported, exit code 2.
        File.WriteAllText(job, """
            { "project": "bar.ossproj", "parameters": [ { "name": "x", "target": "condition:nothere.value", "values": [1] } ] }
            """);
        (code, stdout, _) = Run("sweep", job, "--out", outDir);
        Assert.Equal(2, code);
        Assert.Contains("No boundary condition named 'nothere'", stdout);
    }

    [Fact]
    public void Sweep_MaterialAndGeometryTargets_ReachTheSolve()
    {
        string project = WriteBarProject();
        string job = Path.Combine(_dir, "sweep2.json");
        // Electrical: the bar's resistance R = L/(σA) is exact on a lattice; sweep σ and the length.
        var p = new ProjectSerializer().Load(project);
        p.AnalysisType = "Electrical";
        p.Bodies[0].BoundaryConditions.Clear();
        p.Bodies[0].BoundaryConditions.Add(new VoltagePotential { Name = "gnd", FaceIds = new[] { 4 }, Volts = 0 });
        p.Bodies[0].BoundaryConditions.Add(new VoltagePotential { Name = "drive", FaceIds = new[] { 5 }, Volts = 1 });
        new ProjectSerializer().Save(p, project);
        File.WriteAllText(job, """
            {
              "project": "bar.ossproj",
              "mode": "Corners",
              "parameters": [
                { "name": "sigma", "target": "material:TestCopper.electricalConductivity", "nominal": 5.96e7, "tolerancePercent": 10 },
                { "name": "length", "target": "geometry:Bar.sizeZ", "values": [0.01, 0.02, 0.04] }
              ],
              "outputs": [ "Resistance (Ω)" ]
            }
            """);
        string outDir = Path.Combine(_dir, "sweep2");
        var (code, stdout, err) = Run("sweep", job, "--out", outDir);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        var rows = Csv(Path.Combine(outDir, "sweep.csv"));
        Assert.Equal(6, rows.Count);                                           // nominal + 4 corners
        foreach (var row in rows.Skip(1))
        {
            double sigma = double.Parse(row[1], CultureInfo.InvariantCulture);
            double length = double.Parse(row[2], CultureInfo.InvariantCulture);
            double r = double.Parse(row[3], CultureInfo.InvariantCulture);
            Assert.Equal(length / (sigma * 1e-4), r, length / (sigma * 1e-4) * 1e-6);
        }
    }

    [Fact]
    public void Impedance_SweepsTheStackupTolerance()
    {
        string job = Path.Combine(_dir, "line.json");
        File.WriteAllText(job, """
            {
              "line": { "structure": "Microstrip", "widthMeters": 3e-4, "heightMeters": 2e-4,
                        "relativePermittivity": 4.4, "thicknessMeters": 35e-6 },
              "mode": "OneAtATime",
              "parameters": [ { "name": "w", "target": "WidthMeters", "nominal": 3e-4, "tolerancePercent": 10, "points": 3 } ],
              "panelsPerTrace": 24
            }
            """);
        string outDir = Path.Combine(_dir, "impedance");
        var (code, stdout, err) = Run("impedance", job, "--out", outDir);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        Assert.Contains("Z0 = ", stdout);

        var rows = Csv(Path.Combine(outDir, "impedance-sweep.csv"));
        int z0 = Array.IndexOf(rows[0], "Z0 (Ω)");
        Assert.True(z0 > 0, string.Join(",", rows[0]));
        Assert.Equal(5, rows.Count);                                           // nominal + 3 widths
        var swept = rows.Skip(2).Select(r => double.Parse(r[z0], CultureInfo.InvariantCulture)).ToList();
        Assert.True(swept[0] > swept[1] && swept[1] > swept[2], $"Z0 against width: {string.Join(", ", swept)}");
        Assert.InRange(swept[1], 40, 70);                                      // a 50-ish Ω microstrip
        Assert.Equal(double.Parse(rows[1][z0], CultureInfo.InvariantCulture), swept[1], 1e-9);
        Assert.True(File.Exists(Path.Combine(outDir, "impedance-report.md")));
        Assert.Contains("## Sensitivities", File.ReadAllText(Path.Combine(outDir, "impedance-sweep-report.md")));

        // A property that is not on the line is refused by name.
        File.WriteAllText(job, """
            { "line": { "widthMeters": 3e-4, "heightMeters": 2e-4 }, "parameters": [ { "name": "q", "target": "Nope", "values": [1] } ] }
            """);
        (code, stdout, _) = Run("impedance", job, "--out", outDir);
        Assert.Equal(2, code);
        Assert.Contains("no property 'Nope'", stdout);
    }

    [Fact]
    public void Spacing_ChecksATwoNetGerberBoard()
    {
        string board = Path.Combine(_dir, "board");
        Directory.CreateDirectory(board);
        // Two 10 × 5 mm rectangles 0.5 mm apart on the top copper, named A and B by X2 attributes.
        File.WriteAllText(Path.Combine(board, "top.gbr"), """
            %TF.FileFunction,Copper,L1,Top,Signal*%
            %TF.FilePolarity,Positive*%
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10R,10.000X5.000*%
            D10*
            %TO.N,A*%
            X5000000Y2500000D03*
            %TO.N,B*%
            X15500000Y2500000D03*
            %TD*%
            M02*
            """);
        File.WriteAllText(Path.Combine(board, "profile.gbr"), """
            %TF.FileFunction,Profile,NP*%
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10C,0.100*%
            D10*
            X-1000000Y-1000000D02*
            X21500000Y-1000000D01*
            X21500000Y6000000D01*
            X-1000000Y6000000D01*
            X-1000000Y-1000000D01*
            M02*
            """);
        string volts = Path.Combine(_dir, "nets.csv");
        File.WriteAllText(volts, "net,volts\nA,0\nB,300\n");
        string outDir = Path.Combine(_dir, "spacing");

        var (code, stdout, err) = Run("spacing", board, "--volts", volts, "--out", outDir);
        Assert.Equal("", err);
        Assert.Equal(3, code);                                                 // a violation
        Assert.Contains("FAIL A ↔ B at 300 V: clearance on L1 0.5 mm, required 1.25 mm (B2)", stdout);
        var rows = Csv(Path.Combine(outDir, "spacing.csv"));
        Assert.Equal(2, rows.Count);
        Assert.Equal("Clearance", rows[1][0]);
        Assert.Equal("False", rows[1][8]);

        // Coated outer layers: 0.4 mm is enough.
        (code, stdout, _) = Run("spacing", board, "--volts", volts, "--out", outDir, "--coated");
        Assert.Equal(0, code);
        Assert.Contains("ok   A ↔ B at 300 V", stdout);

        (code, _, err) = Run("spacing", board);
        Assert.Equal(1, code);
        Assert.Contains("--volts", err);
    }
}
