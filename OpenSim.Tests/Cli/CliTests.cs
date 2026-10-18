using System.Globalization;
using System.Text.Json;
using OpenSim.Cli;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Geometry;
using OpenSim.Rf.Si;
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
    private string WriteBarProject(string name = "bar", Material? material = null)
    {
        var body = new Body
        {
            Name = "Bar",
            Geometry = PrimitiveFactory.CreateBox(0.01, 0.01, 0.02),
            GeometrySource = "Box 10 × 10 × 20 mm",
            MeshSettings = new MeshSettings { TargetEdgeLength = 0.005, Method = MeshMethod.StructuredLattice },
            Material = material ?? new Material
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
    public void Solve_ProbesReportTheHeatFlowThroughAPlaneAndAFace()
    {
        // k·ΔT·A/L through the bar, from the hot end at z = 20 mm towards z = 0: negative along
        // +z, and negative out of the hot face, where it comes in.
        string project = WriteBarProject("probed");
        string outDir = Path.Combine(_dir, "probed-results");
        var (code, _, err) = Run("solve", project, "--out", outDir, "--probe", "cut:0,0,0.0073:0,0,1;hot:faces:5");
        Assert.Equal(0, code);
        Assert.Equal("", err);
        var outputs = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(outDir, "summary.json")))!;
        double flow = 400 * 100 * 0.01 * 0.01 / 0.02;
        Assert.Equal(-flow, outputs["Probe 'cut' heat flow (W)"], flow * 1e-8);
        Assert.Equal(-flow, outputs["Probe 'hot' heat flow (W)"], flow * 1e-8);
        Assert.True(outputs.ContainsKey("Heat flux error estimate (%)"));
        Assert.True(outputs.ContainsKey("Max Heat flux (recovered) (W/m²)"));

        Assert.Equal(1, Run("solve", project, "--out", outDir, "--probe", "cut:0,0:0,0,1").Code);
    }

    [Fact]
    public void Solve_KeepsTheProjectsOwnMaterial_AndLogsWhereTheLibraryDiffers()
    {
        // D24: a project saved with a library material's name but other values solves with the
        // project's values — the library's used to replace them without a word.
        var library = new MaterialLibrary().Materials.First(m => m.ThermalConductivity is > 0);
        double k = library.ThermalConductivity!.Value / 2;
        string project = WriteBarProject("diverged", library with { ThermalConductivity = k });
        string outDir = Path.Combine(_dir, "diverged-results");
        var (code, stdout, err) = Run("solve", project, "--out", outDir, "--probe", "cut:0,0,0.0073:0,0,1");
        Assert.Equal(0, code);
        Assert.Equal("", err);
        var outputs = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(Path.Combine(outDir, "summary.json")))!;
        double flow = k * 100 * 0.01 * 0.01 / 0.02;
        Assert.Equal(-flow, outputs["Probe 'cut' heat flow (W)"], flow * 1e-8);
        Assert.Contains($"Material '{library.Name}': the project's values differ from the library's (thermal conductivity ", stdout);

        // The library's own values: the same solve, no line.
        string same = WriteBarProject("same", library);
        (code, stdout, err) = Run("solve", same, "--out", Path.Combine(_dir, "same-results"), "--probe", "cut:0,0,0.0073:0,0,1");
        Assert.Equal(0, code);
        Assert.Equal("", err);
        Assert.DoesNotContain("differ from the library", stdout);
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

    /// <summary>FU-46: the project's saved solver settings drive the solve; an option given
    /// on the command line overrides only itself.</summary>
    [Fact]
    public void Solve_UsesTheProjectsSavedSettings_UnlessOverridden()
    {
        string project = WriteBarProject("clocked");
        var p = new ProjectSerializer().Load(project);
        p.AnalysisType = "TransientThermal";
        p.Solver = new SolverSettings { Duration = 2, TimeStep = 0.5, InitialTemperature = 320 };
        new ProjectSerializer().Save(p, project);
        string outDir = Path.Combine(_dir, "clocked-results");

        var (code, _, err) = Run("solve", project, "--out", outDir);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        Assert.Contains("2 s in steps of 0.5 s from 320 K", File.ReadAllText(Path.Combine(outDir, "report.md")));

        (code, _, _) = Run("solve", project, "--out", outDir, "--duration", "4");
        Assert.Equal(0, code);
        Assert.Contains("4 s in steps of 0.5 s from 320 K", File.ReadAllText(Path.Combine(outDir, "report.md")));
    }

    /// <summary>FU-46: geometry targets beyond the box — a cylinder's radius and height, and a
    /// scale of any geometry — checked against the primitive's closed-form volume (a regular
    /// n-gon prism: n·sin(2π/n)/2·r²·h) and extents; then a scaled bar solved: R = L/(σA).</summary>
    [Fact]
    public void Sweep_GeometryTargets_CylinderAndScale()
    {
        const int n = 48;
        var project = new SimProject { Name = "Shapes" };
        project.Bodies.Add(new Body { Name = "Cyl", Geometry = PrimitiveFactory.CreateCylinder(5e-3, 20e-3, n) });
        project.Bodies.Add(new Body { Name = "Bar", Geometry = PrimitiveFactory.CreateBox(0.01, 0.01, 0.02) });
        var settings = new StudySettings();
        var overrides = new Dictionary<string, Material>();
        double PrismVolume(double r, double h) => n * Math.Sin(2 * Math.PI / n) / 2 * r * r * h;

        ProjectTargets.Apply(project, "geometry:Cyl.radius", 10e-3, settings, overrides);
        var cyl = project.Bodies[0].Geometry!;
        Assert.Equal(PrismVolume(10e-3, 20e-3), cyl.ComputeSignedVolume(), PrismVolume(10e-3, 20e-3) * 1e-12);
        Assert.Equal(20e-3, cyl.Vertices.Max(v => v.X) - cyl.Vertices.Min(v => v.X), 1e-12);   // n even: a vertex at 0 and at π
        Assert.Null(project.Bodies[0].Mesh);

        ProjectTargets.Apply(project, "geometry:Cyl.height", 5e-3, settings, overrides);
        cyl = project.Bodies[0].Geometry!;
        Assert.Equal(PrismVolume(10e-3, 5e-3), cyl.ComputeSignedVolume(), PrismVolume(10e-3, 5e-3) * 1e-12);
        Assert.Equal(5e-3, cyl.Vertices.Max(v => v.Z) - cyl.Vertices.Min(v => v.Z), 1e-12);

        ProjectTargets.Apply(project, "geometry:Cyl.scaleZ", 3, settings, overrides);
        cyl = project.Bodies[0].Geometry!;
        Assert.Equal(15e-3, cyl.Vertices.Max(v => v.Z) - cyl.Vertices.Min(v => v.Z), 1e-12);
        Assert.Equal(20e-3, cyl.Vertices.Max(v => v.X) - cyl.Vertices.Min(v => v.X), 1e-12);
        Assert.Equal(0, cyl.Vertices.Min(v => v.Z), 1e-15);                                     // scaled about its own base

        ProjectTargets.Apply(project, "geometry:Bar.scale", 0.5, settings, overrides);
        Assert.Equal(0.01 * 0.01 * 0.02 / 8, project.Bodies[1].Geometry!.ComputeSignedVolume(), 1e-18);

        Assert.Contains("not a cylinder", Assert.Throws<InvalidOperationException>(
            () => ProjectTargets.Apply(project, "geometry:Bar.radius", 1, settings, overrides)).Message);
        Assert.Contains("not a box", Assert.Throws<InvalidOperationException>(
            () => ProjectTargets.Apply(project, "geometry:Cyl.sizeX", 1, settings, overrides)).Message);
        Assert.Throws<InvalidOperationException>(() => ProjectTargets.Apply(project, "geometry:Bar.scale", -1, settings, overrides));

        // Solved: the bar's length scaled, R = L/(σA) exact on the lattice.
        string path = WriteBarProject("scaled");
        var p = new ProjectSerializer().Load(path);
        p.AnalysisType = "Electrical";
        p.Bodies[0].BoundaryConditions.Clear();
        p.Bodies[0].BoundaryConditions.Add(new VoltagePotential { Name = "gnd", FaceIds = new[] { 4 }, Volts = 0 });
        p.Bodies[0].BoundaryConditions.Add(new VoltagePotential { Name = "drive", FaceIds = new[] { 5 }, Volts = 1 });
        new ProjectSerializer().Save(p, path);
        string job = Path.Combine(_dir, "scaled.json");
        File.WriteAllText(job, """
            { "project": "scaled.ossproj", "mode": "OneAtATime",
              "parameters": [ { "name": "s", "target": "geometry:Bar.scaleZ", "values": [1, 2] } ],
              "outputs": [ "Resistance (Ω)" ] }
            """);
        string outDir = Path.Combine(_dir, "scaled-sweep");
        var (code, _, err) = Run("sweep", job, "--out", outDir);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        var rows = Csv(Path.Combine(outDir, "sweep.csv"));
        Assert.Equal(4, rows.Count);
        foreach (var row in rows.Skip(1))
        {
            double s = double.Parse(row[1], CultureInfo.InvariantCulture);
            double expected = 0.02 * s / (5.96e7 * 1e-4);
            Assert.Equal(expected, double.Parse(row[2], CultureInfo.InvariantCulture), expected * 1e-6);
        }
    }

    /// <summary>FU-46: a Monte Carlo sweep. The bar's hottest node IS the hot face, so the
    /// output's statistics are the parameter's own: the report's mean and yield equal what the
    /// CSV's rows give by hand, and the draws stay inside the stated range.</summary>
    [Fact]
    public void Sweep_MonteCarlo_ReportsStatisticsAndYield()
    {
        string project = WriteBarProject();
        string job = Path.Combine(_dir, "mc.json");
        File.WriteAllText(job, """
            {
              "project": "bar.ossproj", "mode": "MonteCarlo", "samples": 24, "seed": 5,
              "parameters": [ { "name": "hot", "target": "condition:hot.value", "nominal": 400, "plusMinus": 50, "unit": "K" } ],
              "outputs": [ "Max Temperature (K)" ],
              "limits": { "Max Temperature (K)": [375, 425] }
            }
            """);
        string outDir = Path.Combine(_dir, "mc");
        var (code, stdout, err) = Run("sweep", job, "--out", outDir);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        Assert.Contains("25 run(s), MonteCarlo", stdout);
        Assert.Contains("Yield of Max Temperature (K) within [375, 425]", stdout);

        var rows = Csv(Path.Combine(outDir, "sweep.csv"));
        Assert.Equal(26, rows.Count);
        Assert.Equal("400", rows[1][1]);                                                        // the nominal heads the plan
        var samples = rows.Skip(2).Select(r => double.Parse(r[1], CultureInfo.InvariantCulture)).ToList();
        Assert.All(samples, h => Assert.InRange(h, 350, 450));
        foreach (var row in rows.Skip(1))
            Assert.Equal(double.Parse(row[1], CultureInfo.InvariantCulture), double.Parse(row[2], CultureInfo.InvariantCulture), 1e-6);

        var stats = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(
            File.ReadAllText(Path.Combine(outDir, "sweep-statistics.json")))!["Max Temperature (K)"];
        Assert.Equal(24, stats["samples"]);
        Assert.Equal(samples.Average(), stats["mean"], 1e-6);
        Assert.Equal(samples.Count(h => h >= 375 && h <= 425) / 24.0, stats["yield"], 1e-12);
        Assert.Equal(samples.Min(), stats["min"], 1e-6);
        Assert.Equal(samples.Max(), stats["max"], 1e-6);

        string report = File.ReadAllText(Path.Combine(outDir, "sweep-report.md"));
        Assert.Contains("## Statistics (Monte Carlo)", report);
        Assert.Contains("## Yield", report);
        Assert.Contains("seed 5", report);
        Assert.DoesNotContain("## Sensitivities", report);
    }

    /// <summary>
    /// FU-46: the board re-import sweep. A two-layer Gerber set written here — a 1 mm trace
    /// 20 mm between two X2-named pads on L1 over a solid plane on L2 — swept over the copper
    /// thickness and the dielectric gap. Each run re-meshes the net with that stackup: the
    /// pad-to-pad resistance halves (to first order) with doubled copper, and the trace line
    /// over the board's own plane is the stackup calculator's microstrip of the same numbers
    /// exactly, with Z0 rising with the gap.
    /// </summary>
    [Fact]
    public void Sweep_Board_ReimportsTheNetWithTheSweptStackup()
    {
        string board = Path.Combine(_dir, "two-layer");
        Directory.CreateDirectory(board);
        File.WriteAllText(Path.Combine(board, "top.gbr"), """
            %TF.FileFunction,Copper,L1,Top,Signal*%
            %TF.FilePolarity,Positive*%
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10C,1.500*%
            %ADD11C,1.000*%
            G01*
            D10*
            %TO.N,SIG*%
            %TO.P,U1,1*%
            X2000000Y5000000D03*
            %TO.P,U1,2*%
            X22000000Y5000000D03*
            %TD*%
            %TO.N,SIG*%
            D11*
            X2000000Y5000000D02*
            X22000000Y5000000D01*
            %TD*%
            M02*
            """);
        File.WriteAllText(Path.Combine(board, "bottom.gbr"), """
            %TF.FileFunction,Copper,L2,Bot,Plane*%
            %TF.FilePolarity,Positive*%
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10R,40.000X20.000*%
            D10*
            %TO.N,GND*%
            X12000000Y5000000D03*
            %TD*%
            M02*
            """);
        File.WriteAllText(Path.Combine(board, "profile.gbr"), """
            %TF.FileFunction,Profile,NP*%
            %FSLAX46Y46*%
            %MOMM*%
            %ADD10C,0.100*%
            D10*
            X-8000000Y-5000000D02*
            X32000000Y-5000000D01*
            X32000000Y15000000D01*
            X-8000000Y15000000D01*
            X-8000000Y-5000000D01*
            M02*
            """);
        string job = Path.Combine(_dir, "board.json");
        File.WriteAllText(job, """
            {
              "board": { "path": "two-layer", "net": "SIG", "source": "U1.1", "sink": "U1.2",
                         "frequencyHz": 1e9, "targetEdgeLength": 0.4e-3,
                         "stackup": { "copperThickness": 35e-6, "boardThickness": 0.2e-3,
                                      "dielectricGapThicknesses": [0.2e-3], "dielectricGapPermittivities": [4.4],
                                      "dielectricGapLossTangents": [0.02] } },
              "mode": "OneAtATime",
              "parameters": [ { "name": "t", "target": "stackup.copperThickness", "values": [35e-6, 70e-6] },
                              { "name": "h", "target": "stackup.gap1.thickness", "values": [0.2e-3, 0.4e-3] } ],
              "outputs": [ "Resistance (Ω)", "Z0 (Ω)", "eps_eff", "C (pF/m)", "Line length (mm)" ]
            }
            """);
        string outDir = Path.Combine(_dir, "board-sweep");
        var (code, stdout, err) = Run("sweep", job, "--out", outDir);
        Assert.Equal("", err);
        Assert.Equal(0, code);
        Assert.Contains("5 run(s)", stdout);

        var rows = Csv(Path.Combine(outDir, "sweep.csv"));
        Assert.Equal("run,t,h,Resistance (Ω),Z0 (Ω),eps_eff,C (pF/m),Line length (mm),error", string.Join(",", rows[0]));
        Assert.Equal(6, rows.Count);
        double Cell(int row, int col) => double.Parse(rows[row][col], CultureInfo.InvariantCulture);
        // CSV rows (after the header): 1 nominal (t 70 µm, h 0.4 mm), 2–3 t varied at h 0.4, 4–5 h varied at t 70.
        Assert.Equal(20.0, Cell(1, 7), 1e-6);                                                   // pad centre to pad centre
        double ratio = Cell(2, 3) / Cell(3, 3);                                                 // R(35 µm) / R(70 µm)
        _out.WriteLine($"R(35)/R(70) = {ratio}");                                                // measured 1.9991
        Assert.InRange(ratio, 1.8, 2.2);
        Assert.True(Cell(5, 4) > Cell(4, 4), $"Z0 against the gap: {Cell(4, 4)}, {Cell(5, 4)}");
        Assert.True(Cell(5, 6) < Cell(4, 6), "C must fall as the gap grows");
        Assert.All(rows.Skip(1), r => Assert.InRange(double.Parse(r[5], CultureInfo.InvariantCulture), 1, 4.4));

        // The board's line at t 70 µm, h 0.2 mm is the calculator's microstrip of the same numbers.
        double sigma = new MaterialLibrary().Materials.First(m => m.Name == "Copper (annealed)").ElectricalConductivity!.Value;
        var line = ImpedanceCalculator.Solve(new LineSpec
        {
            Structure = LineStructure.Microstrip, WidthMeters = 1e-3, HeightMeters = 0.2e-3, RelativePermittivity = 4.4,
            LossTangent = 0.02, ThicknessMeters = 70e-6, ConductivitySiemensPerMeter = sigma, FrequencyHz = 1e9
        });
        Assert.Equal(line.ImpedanceOhms, Cell(4, 4), line.ImpedanceOhms * 1e-9);
        Assert.Equal(line.Modes[0].EffectivePermittivity, Cell(4, 5), 1e-9);
        Assert.Equal(line.Modes[0].CapacitanceFaradsPerMeter * 1e12, Cell(4, 6), line.Modes[0].CapacitanceFaradsPerMeter * 1e12 * 1e-9);

        string report = File.ReadAllText(Path.Combine(outDir, "sweep-report.md"));
        Assert.Contains("board stackup sweep", report);
        Assert.Contains("reference plane is L2", report);

        // A pad the net does not have is a failed run, named.
        File.WriteAllText(job, File.ReadAllText(job).Replace("\"U1.2\"", "\"U9.9\""));
        (code, stdout, _) = Run("sweep", job, "--out", outDir);
        Assert.Equal(2, code);
        Assert.Contains("'U9.9' is not a pad of the net", stdout);
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
    public void Impedance_TakesANamedFoil_AndTheCatalogLists()
    {
        // The catalog itself.
        var (code, stdout, err) = Run("foils");
        Assert.Equal(0, code);
        Assert.Equal("", err);
        Assert.Contains("Circuit Foil BF-TZA", stdout);
        Assert.Contains("Source:", stdout);
        (code, stdout, _) = Run("foils", "--name", "Rogers Rolled foil");
        Assert.Equal(0, code);
        Assert.Contains("RMS 0.4 µm typical", stdout);
        Assert.DoesNotContain("BF-TZA", stdout);

        // A job naming a foil: its roughness is on the line and in the report.
        string job = Path.Combine(_dir, "foil.json");
        File.WriteAllText(job, """
            { "line": { "structure": "Microstrip", "widthMeters": 3e-4, "heightMeters": 2e-4, "thicknessMeters": 35e-6 },
              "foil": "Circuit Foil BF-TZA", "panelsPerTrace": 24 }
            """);
        string outDir = Path.Combine(_dir, "foil-out");
        (code, _, err) = Run("impedance", job, "--out", outDir);
        Assert.Equal(0, code);
        Assert.Equal("", err);
        string report = File.ReadAllText(Path.Combine(outDir, "impedance-report.md"));
        Assert.Contains("Cannonball from Circuit Foil BF-TZA 35 µm", report);

        // A foils file beside the job adds a foil; an unknown name is a usage error.
        File.WriteAllText(Path.Combine(_dir, "mine.json"), """
            [ { "vendor": "Acme", "name": "X1", "gauges": [ { "thicknessMicrons": 35, "roughnessMicrons": 1.0 } ], "source": "Acme sheet" } ]
            """);
        File.WriteAllText(job, """
            { "line": { "widthMeters": 3e-4, "heightMeters": 2e-4, "thicknessMeters": 35e-6 },
              "foil": "Acme X1", "foils": "mine.json", "panelsPerTrace": 24 }
            """);
        (code, _, err) = Run("impedance", job, "--out", outDir);
        Assert.Equal(0, code);
        Assert.Contains("Cannonball from Acme X1", File.ReadAllText(Path.Combine(outDir, "impedance-report.md")));

        File.WriteAllText(job, """
            { "line": { "widthMeters": 3e-4, "heightMeters": 2e-4 }, "foil": "Acme X9", "panelsPerTrace": 24 }
            """);
        (code, _, err) = Run("impedance", job, "--out", outDir);
        Assert.Equal(1, code);
        Assert.Contains("No foil is named 'Acme X9'", err);
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
