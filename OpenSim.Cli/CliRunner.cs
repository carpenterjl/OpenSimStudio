using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
using OpenSim.Core.Numerics;
using OpenSim.Core.Persistence;
using OpenSim.Core.PostProcessing;
using OpenSim.Core.Studies;
using OpenSim.Pcb.Import;
using OpenSim.Pcb.Ipc2581;
using OpenSim.Pcb.Rules;
using OpenSim.Rf.Si;

namespace OpenSim.Cli;

/// <summary>
/// The command line: <c>opensim solve</c>, <c>sweep</c>, <c>impedance</c> and <c>spacing</c>.
/// Every command reads files and writes a results folder — a report (Markdown and HTML)
/// with the assumptions, the tables as CSV, the log — so a study can be run again from a
/// script and compared. Exit codes: 0 done, 1 usage, 2 the run failed.
/// </summary>
public static partial class CliRunner
{
    public const string Usage = """
        opensim — OpenSim Studio from the command line

        opensim solve <project.ossproj> [options]
            Runs the project's analysis (or --analysis NAME) on its first solid body. The
            settings the project was saved with are the defaults; the options below override.
            --analysis NAME     Static, Electrical, Thermal, JouleCoupled, TransientThermal,
                                Modal, AcElectrical, Electrostatic, EnvironmentThermal,
                                ConjugateThermal (the project's saved CFD case and environment)
            --body NAME         the body to solve (single-body analyses)
            --out DIR           results folder (default: <project>-results next to the file)
            --remesh            re-mesh every body with its stored settings
            --fields            also write every result field as CSV
            --duration S --step S --initial K        transient settings
            --integrator be|trbdf2|adaptive          transient time integrator (TR-BDF2 and the
                                                     adaptive step: linear models only)
            --tolerance K                            adaptive: the error one step may add
            --capacity auto|consistent|lumped        transient heat-capacity matrix
            --fmin HZ --fmax HZ --points N           AC sweep settings
            --modes N                                modal
            --open                                   electrostatic: open outer boundary (1/r
                                                     condition) instead of a zero-flux wall
            --steady                                 environment and conjugate heat flow:
                                                     equilibrium only
            --max-steps N                            conjugate: cap on the steady flow march
            --momentum                               conjugate transient: march the flow with
                                                     the heating
            --recover                                Electrical, Thermal: the recovered flux, its
                                                     error field and the estimated error
            --probe "NAME:X,Y,Z:NX,NY,NZ[;...]"      Electrical, Thermal: the current or heat flow
                                                     through the plane through X,Y,Z (m) normal to
                                                     NX,NY,NZ; "NAME:faces:ID[,ID...]" through
                                                     boundary faces (implies --recover)

        opensim sweep <job.json> [--out DIR]
            Runs the project at every point of a parameter plan and tabulates the outputs.
            Job: { "project": "...", "analysis": "...",
                   "mode": "OneAtATime|FullFactorial|Corners|MonteCarlo", "samples": 200, "seed": 1,
                   "parameters": [ { "name": "...", "target": "condition:hot.value", "values": [...] },
                                   { "name": "...", "target": "material:FR4.relativePermittivity",
                                     "nominal": 4.4, "tolerancePercent": 10, "points": 3,
                                     "distribution": "Normal", "sigma": 0.1 } ],
                   "outputs": [ "Max Temperature (K)" ],
                   "limits": { "Max Temperature (K)": [0, 350] } }
            Targets: condition:<name>.value|.ambient, body:<name>.heatSourcePower,
                     material:<name>.<property>, mesh:<body>.targetEdgeLength,
                     geometry:<body>.sizeX|sizeY|sizeZ (box) | radius|height (cylinder) |
                       scale|scaleX|scaleY|scaleZ (any geometry), environment.ambientTemperature,
                     settings.duration|timeStep|initialTemperature|tolerance|minFrequency|
                       maxFrequency|pointCount|modeCount
            Monte Carlo draws each parameter uniformly over its range (or normally about its
            nominal, sigma = range/6 unless given), reports mean, sigma, percentiles and the
            yield inside each limit. The project's saved solver settings are the defaults.
            A board instead of a project re-reads a net with the swept stackup at every
            point: its pad-to-pad DC resistance, and its trace as a line (Z0, RLGC) over the
            board's reference planes.
            Job: { "board": { "path": "board.zip", "net": "CLK", "source": "U1.3", "sink": "R2.1",
                              "rlgc": true, "frequencyHz": 1e9, "targetEdgeLength": 0 },
                   "parameters": [ { "name": "t", "target": "stackup.copperThickness",
                                     "nominal": 35e-6, "tolerancePercent": 20 } ] }
            Board targets: stackup.copperThickness|boardThickness|viaPlating,
                     stackup.layer<n>.thickness, stackup.gap<n>.thickness|permittivity|lossTangent,
                     mesh.targetEdgeLength, settings.frequency, plus material:<name>.<property>

        opensim impedance <line.json> [--out DIR]
            The stackup and impedance calculator, optionally swept over the line's own
            properties (stackup tolerances); "mode", "samples", "seed" and "limits" as in sweep.
            Job: { "line": { "structure": "Microstrip", "widthMeters": 3e-4, "heightMeters": 2e-4,
                             "relativePermittivity": 4.4, "thicknessMeters": 35e-6 },
                   "foil": "Circuit Foil BF-TZA",        (optional: a catalog foil's data-sheet Rz
                   "foils": "my-foils.json",             as Huray roughness; "foils" adds your own)
                   "mode": "OneAtATime",
                   "parameters": [ { "name": "w", "target": "WidthMeters", "nominal": 3e-4,
                                     "tolerancePercent": 10, "points": 3 } ] }

        opensim foils [--foils FILE] [--name NAME]
            The copper-foil catalog: each foil's published treated-side roughness per weight, the
            Huray parameters the Cannonball stack makes of it, and the data sheet it was read from.
            --foils FILE merges a JSON array of your own foils; --name shows one foil.

        opensim spacing <board.zip|folder|ipc2581.xml> --volts nets.csv [options]
            Clearance and creepage of the board's nets against a spacing table.
            nets.csv: "net,volts" lines. --table FILE loads a table (CSV), --coated uses the
            coated outer-layer column everywhere, --mask reads the board's solder-mask layers and
            uses it only where the mask covers both copper edges, --within F lists pairs within
            F × their requirement.

        opensim emi <job.json> [--out DIR]
            Conducted-emission estimate of a switching converter's input on two LISNs against a
            limit line, and first-order radiated estimates of its hot loop and input cable.
            Job: { "vin": 12, "iout": 3, "frequencyHz": 5e5, "duty": 0.4, "riseSeconds": 1e-8,
                   "fallSeconds": 1e-8, "switchNodeCapacitance": 2e-11, "inputCapacitance": 1e-5,
                   "inputEsr": 0.005, "inputEsl": 1e-9, "filterInductance": 0, "filterCapacitance": 0,
                   "limit": "ConductedClassBQuasiPeak", "loopArea": 1e-4, "cableLength": 1,
                   "distance": 3, "radiatedLimit": "RadiatedClassB3m" }
            Exit code 3 when a conducted line is over the limit.

        opensim magnetics <board> --nets P[,S...] --center X,Y [options]
            Planar magnetics from the layout: the windings' copper cut along a ray from the
            winding axis (mm), solved as an axisymmetric section.
            --angle DEG         direction of the cut (default 0)
            --cut-angles N      average the cut over N angles around the axis (folds the
                                spiral's pitch in; default 1 = the single --angle cut)
            --radius MM         how far out to cut (default 30)
            --frequency HZ      AC resistance and inductance with skin and proximity effect
            --drive NET         the winding that carries current (default the first)
            --core FILE         core JSON: { "postRadius": 3e-3, "windowRadius": 10e-3,
                                "outerRadius": 12e-3, "plateThickness": 2e-3, "windowHeight": 3e-3,
                                "centreGap": 2e-4, "relativePermeability": 2000 }

        opensim help
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            stdout.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }
        try
        {
            string command = args[0].ToLowerInvariant();
            var options = ParseOptions(args.Skip(command == "foils" ? 1 : 2));     // foils takes no positional argument
            return command switch
            {
                "solve" => Solve(Require(args, "a project file"), options, stdout),
                "sweep" => Sweep(Require(args, "a sweep job file"), options, stdout),
                "impedance" => Impedance(Require(args, "an impedance job file"), options, stdout),
                "spacing" => Spacing(Require(args, "a board"), options, stdout),
                "emi" => Emi(Require(args, "an EMI job file"), options, stdout),
                "magnetics" => Magnetics(Require(args, "a board"), options, stdout),
                "foils" => Foils(options, stdout),
                _ => Fail(stderr, $"Unknown command '{args[0]}'.{Environment.NewLine}{Usage}", 1)
            };
        }
        catch (UsageException ex) { return Fail(stderr, ex.Message, 1); }
        catch (Exception ex) { return Fail(stderr, ex.Message, 2); }
    }

    private sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message) { }
    }

    private static string Require(string[] args, string what) =>
        args.Length > 1 && !args[1].StartsWith("--") ? args[1] : throw new UsageException($"'{args[0]}' needs {what}.");

    private static int Fail(TextWriter stderr, string message, int code)
    {
        stderr.WriteLine(message);
        return code;
    }

    private static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--")) throw new UsageException($"Unexpected argument '{list[i]}'.");
            string key = list[i][2..];
            if (i + 1 < list.Count && !list[i + 1].StartsWith("--")) options[key] = list[++i];
            else options[key] = "true";
        }
        return options;
    }

    private static double Number(Dictionary<string, string> o, string key, double fallback) =>
        o.TryGetValue(key, out var s)
            ? double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v
                : throw new UsageException($"--{key} needs a number, not '{s}'.")
            : fallback;

    private static T Choice<T>(Dictionary<string, string> o, string key, T fallback, Dictionary<string, T> choices) =>
        o.TryGetValue(key, out var s)
            ? choices.TryGetValue(s.ToLowerInvariant(), out var v) ? v
                : throw new UsageException($"--{key} takes {string.Join(", ", choices.Keys)}, not '{s}'.")
            : fallback;

    /// <summary>The run's settings: the project's saved ones where the command line says
    /// nothing, the command line where it does.</summary>
    private static StudySettings Settings(Dictionary<string, string> o, SolverSettings? saved)
    {
        var d = StudySettings.FromProject(saved);
        return d with
        {
            InitialTemperature = Number(o, "initial", d.InitialTemperature),
            Duration = Number(o, "duration", d.Duration),
            TimeStep = Number(o, "step", d.TimeStep),
            MaxFlowSteps = o.ContainsKey("max-steps") ? (int)Number(o, "max-steps", 4000) : null,
            TransientMomentum = o.ContainsKey("momentum"),
            Integrator = Choice(o, "integrator", d.Integrator, new()
            {
                ["be"] = TimeIntegrator.BackwardEuler, ["trbdf2"] = TimeIntegrator.TrBdf2, ["adaptive"] = TimeIntegrator.Adaptive
            }),
            Tolerance = Number(o, "tolerance", d.Tolerance),
            Capacity = Choice(o, "capacity", d.Capacity, new()
            {
                ["auto"] = ThermalCapacity.Automatic, ["consistent"] = ThermalCapacity.Consistent, ["lumped"] = ThermalCapacity.Lumped
            }),
            MinFrequency = Number(o, "fmin", d.MinFrequency),
            MaxFrequency = Number(o, "fmax", d.MaxFrequency),
            PointCount = (int)Number(o, "points", d.PointCount),
            ModeCount = (int)Number(o, "modes", d.ModeCount),
            OpenBoundary = o.ContainsKey("open") || d.OpenBoundary,
            FluxRecovery = Recovery(o),
            EnvironmentSteady = o.ContainsKey("steady") || d.EnvironmentSteady,
            BodyName = o.GetValueOrDefault("body"),
            Remesh = o.ContainsKey("remesh")
        };
    }

    /// <summary>--recover and --probe: null when neither is given.</summary>
    private static FluxRecoverySettings? Recovery(Dictionary<string, string> o)
    {
        if (!o.TryGetValue("probe", out var spec)) return o.ContainsKey("recover") ? new FluxRecoverySettings() : null;
        var probes = new List<FluxProbe>();
        foreach (string item in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':');
            if (parts.Length == 3 && parts[1].Equals("faces", StringComparison.OrdinalIgnoreCase))
            {
                var ids = parts[2].Split(',').Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                    ? id : throw new UsageException($"--probe '{item}': '{s}' is not a face id.")).ToList();
                probes.Add(new FluxProbe { Name = parts[0], FaceIds = ids });
                continue;
            }
            if (parts.Length != 3)
                throw new UsageException($"--probe '{item}' is not NAME:X,Y,Z:NX,NY,NZ or NAME:faces:ID[,ID...].");
            probes.Add(new FluxProbe { Name = parts[0], Origin = Point(item, parts[1]), Normal = Point(item, parts[2]) });
        }
        return new FluxRecoverySettings { Probes = probes };

        static Vector3D Point(string item, string text)
        {
            var v = text.Split(',').Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                ? x : throw new UsageException($"--probe '{item}': '{s}' is not a number.")).ToArray();
            return v.Length == 3 ? new Vector3D(v[0], v[1], v[2])
                : throw new UsageException($"--probe '{item}': '{text}' needs three numbers.");
        }
    }

    private static string OutDir(string inputPath, Dictionary<string, string> o, string suffix)
    {
        string dir = o.TryGetValue("out", out var given)
            ? given
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".",
                Path.GetFileNameWithoutExtension(inputPath) + suffix);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------- solve ----------------

    private static int Solve(string projectPath, Dictionary<string, string> o, TextWriter stdout)
    {
        var project = new ProjectSerializer().Load(projectPath);
        var settings = Settings(o, project.Solver);
        var log = new List<string>();
        void Log(string line) { log.Add(line); stdout.WriteLine(line); }
        StudyRunner.MeshBodies(project, StudyRunner.DefaultMesher(), settings.Remesh, Log);
        var run = StudyRunner.Solve(project, o.GetValueOrDefault("analysis"), new MaterialLibrary(), settings, Log);

        string dir = OutDir(projectPath, o, "-results");
        WriteRun(run, dir, project.Name, o.ContainsKey("fields"));
        stdout.WriteLine($"Results written to {dir}");
        return 0;
    }

    private static void WriteRun(StudyRun run, string dir, string projectName, bool fields)
    {
        File.WriteAllText(Path.Combine(dir, "report.md"), run.Report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "report.html"), run.Report.ToHtml());
        File.WriteAllText(Path.Combine(dir, "log.txt"), string.Join(Environment.NewLine, run.Output.Log));
        var context = new ResultReportContext
        {
            ProjectName = projectName, BodyName = run.BodyName, Analysis = run.Analysis,
            Material = run.Input.Material, BoundaryConditions = run.Input.BoundaryConditions
        };
        File.WriteAllText(Path.Combine(dir, "summary.csv"), ResultReportCsv.WriteSummary(run.Output.Fields, run.Input.Mesh, context));
        var outputs = StudyRunner.Outputs(run);
        File.WriteAllText(Path.Combine(dir, "summary.json"),
            JsonSerializer.Serialize(outputs, new JsonSerializerOptions { WriteIndented = true }));
        if (!fields) return;
        string fieldDir = Path.Combine(dir, "fields");
        Directory.CreateDirectory(fieldDir);
        foreach (var field in run.Output.Fields)
        {
            string name = string.Concat(field.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            File.WriteAllText(Path.Combine(fieldDir, name + ".csv"), ResultReportCsv.WriteField(field, run.Input.Mesh, context));
        }
    }

    // ---------------- sweep ----------------

    private static int Sweep(string jobPath, Dictionary<string, string> o, TextWriter stdout)
    {
        var spec = SweepSpec.Load(jobPath);
        var plan = spec.Plan();
        stdout.WriteLine($"Sweep '{Path.GetFileName(jobPath)}': {plan.Count} run(s), {plan.Mode}.");
        var progress = new Progress<(int Done, int Total)>(p => stdout.WriteLine($"  run {p.Done} of {p.Total}"));
        StudyReport? nominal;
        string title;
        SweepResult result;
        if (spec.Board is { } board)
        {
            result = SweepBoard(spec, board, plan, stdout, progress, out nominal);
            title = $"{Path.GetFileNameWithoutExtension(board.Path)} — board stackup sweep";
        }
        else
        {
            result = SweepProject(spec, plan, progress, out nominal);
            title = $"{Path.GetFileNameWithoutExtension(spec.Project)} — parameter sweep";
        }

        string dir = OutDir(jobPath, o, "-sweep");
        File.WriteAllText(Path.Combine(dir, "sweep.csv"), result.ToCsv());
        var report = SweepReport(title, result, spec.Parameters, nominal, spec.Limits);
        File.WriteAllText(Path.Combine(dir, "sweep-report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "sweep-report.html"), report.ToHtml());
        if (plan.Mode == SweepMode.MonteCarlo)
            File.WriteAllText(Path.Combine(dir, "sweep-statistics.json"), StatisticsJson(result, spec.Limits));
        foreach (var line in result.Describe()) stdout.WriteLine(line);
        foreach (var line in YieldLines(result, spec.Limits)) stdout.WriteLine(line);
        foreach (var row in result.Rows.Where(r => !r.Succeeded))
            stdout.WriteLine($"  run {row.Point.Index} failed: {row.Error}");
        stdout.WriteLine($"Results written to {dir}");
        return result.Failures == 0 ? 0 : 2;
    }

    /// <summary>The project sweep: a fresh load per run with the point's values applied, the
    /// project's saved solver settings as the defaults the job may override.</summary>
    private static SweepResult SweepProject(SweepSpec spec, SweepPlan plan, IProgress<(int, int)> progress, out StudyReport? nominal)
    {
        var serializer = new ProjectSerializer();
        var baseSettings = spec.Settings ?? StudySettings.FromProject(serializer.Load(spec.Project).Solver);
        var materials = new MaterialLibrary();
        var mesher = StudyRunner.DefaultMesher();
        var targets = spec.Parameters.ToDictionary(p => p.Name, p => p.Target, StringComparer.OrdinalIgnoreCase);
        StudyReport? first = null;
        var result = ParameterSweep.Run(plan, values =>
        {
            // A fresh project per run: nothing carries over from the last point.
            var project = serializer.Load(spec.Project);
            var settings = baseSettings;
            var overrides = new Dictionary<string, Material>();
            foreach (var (name, value) in values)
                settings = ProjectTargets.Apply(project, targets[name], value, settings, overrides);
            var lines = new List<string>();
            StudyRunner.MeshBodies(project, mesher, settings.Remesh, lines.Add);
            var run = StudyRunner.Solve(project, spec.Analysis, materials, settings, lines.Add, materialOverrides: overrides);
            first ??= run.Report;
            return Select(StudyRunner.Outputs(run), spec.Outputs);
        }, progress);
        nominal = first;
        return result;
    }

    /// <summary>
    /// The board sweep: the board read once, and at every point the net meshed again with
    /// that point's stackup (copper, gaps, plating) and solved for the DC resistance between
    /// the two pads; with <see cref="BoardSweepSpec.Rlgc"/>, the net's trace as a line over
    /// the board's own reference planes at the same stackup. Material and condition targets
    /// apply to the run's one-body project as in a project sweep.
    /// </summary>
    private static SweepResult SweepBoard(SweepSpec spec, BoardSweepSpec board, SweepPlan plan, TextWriter stdout,
        IProgress<(int, int)> progress, out StudyReport? nominal)
    {
        var pcb = Ipc2581Reader.Matches(board.Path) ? new Ipc2581Reader().Read(board.Path) : new PcbBoardReader().Read(board.Path);
        stdout.WriteLine($"Board: {pcb.Nets.Count} nets, {pcb.Islands.Count} copper islands, {pcb.Pads.Count} pads" +
                         (pcb.Warnings.Count > 0 ? $", {pcb.Warnings.Count} import note(s)." : "."));
        var net = pcb.Nets.FirstOrDefault(n => string.Equals(n.Name, board.Net, StringComparison.OrdinalIgnoreCase))
                  ?? pcb.Nets.FirstOrDefault(n => string.Equals($"Net {n.Id}", board.Net, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException(
                      $"No net named '{board.Net}'. Nets: {string.Join(", ", pcb.Nets.Select(n => n.Name ?? $"Net {n.Id}"))}.");
        int copperLayers = pcb.Islands.Count == 0 ? 1 : pcb.Islands.Max(i => i.LayerOrder);
        NetMeshOptions baseOptions;
        if (board.Stackup is { } given)
            baseOptions = BoardTargets.ToMeshOptions(given, copperLayers, board.TargetEdgeLength, board.CopperMaterial);
        else
        {
            var file = BoardStackup.FromBoard(pcb);
            baseOptions = new NetMeshOptions
            {
                TargetEdgeLength = board.TargetEdgeLength, CopperThickness = file.DefaultCopperThickness,
                CopperMaterialName = board.CopperMaterial, LayerThickness = file.LayerThickness,
                DefaultDielectricThickness = file.DefaultGapThickness, DielectricGapThickness = file.GapThickness,
                DielectricGapPermittivity = file.GapPermittivity, DielectricGapLossTangent = file.GapLossTangent
            };
            stdout.WriteLine($"Stackup: {file.Source}.");
        }
        var materials = new MaterialLibrary();
        var baseSettings = spec.Settings ?? new StudySettings();
        var targets = spec.Parameters.ToDictionary(p => p.Name, p => p.Target, StringComparer.OrdinalIgnoreCase);
        StudyReport? first = null;
        var result = ParameterSweep.Run(plan, values =>
        {
            var options = baseOptions;
            double frequency = board.FrequencyHz;
            var deferred = new List<(string Target, double Value)>();
            foreach (var (name, value) in values)
            {
                string target = targets[name];
                if (BoardTargets.Handles(target)) (options, frequency) = BoardTargets.Apply(options, frequency, target, value);
                else deferred.Add((target, value));
            }

            var meshed = new NetMesher().MeshNet(net, pcb.Pads, options);
            var body = meshed.Body;
            body.Material = materials.Materials.FirstOrDefault(m => m.Name == options.CopperMaterialName)
                            ?? throw new InvalidOperationException($"The material library has no '{options.CopperMaterialName}'.");
            var source = FindPad(meshed.Pads, board.Source, "source");
            var sink = FindPad(meshed.Pads, board.Sink, "sink");
            if (source.FaceId == sink.FaceId) throw new InvalidOperationException("The source and sink name the same pad.");
            body.BoundaryConditions.Add(new VoltagePotential { Name = "Source", FaceIds = new[] { source.FaceId }, Volts = 1 });
            body.BoundaryConditions.Add(new VoltagePotential { Name = "Sink", FaceIds = new[] { sink.FaceId }, Volts = 0 });
            var project = new SimProject { Name = Path.GetFileNameWithoutExtension(board.Path), AnalysisType = "Electrical" };
            project.Bodies.Add(body);
            var settings = baseSettings;
            var overrides = new Dictionary<string, Material>();
            foreach (var (target, value) in deferred) settings = ProjectTargets.Apply(project, target, value, settings, overrides);

            var lines = new List<string>();
            var run = StudyRunner.Solve(project, "Electrical", materials, settings, lines.Add, materialOverrides: overrides);
            var outputs = new Dictionary<string, double>(StudyRunner.Outputs(run));
            var assumptions = new List<string>(meshed.Warnings)
            {
                $"Electrodes: source {source.Label}, sink {sink.Label}; 1 V between them, the pad faces as equipotentials."
            };
            if (board.Rlgc)
            {
                var material = overrides.GetValueOrDefault(body.Material.Name) ?? body.Material;
                var line = BoardTraceLine.Extract(pcb, net, new BoardCoupledOptions
                {
                    Stackup = options.Stackup, CopperThicknessMeters = options.CopperThickness, Model = RlgcModel.Board,
                    ConductivitySiemensPerMeter = material.ElectricalConductivity ?? 5.8e7
                }, frequency);
                if (line.FailureReason is not null) throw new InvalidOperationException("Trace line: " + line.FailureReason);
                var mode = line.Mode!;
                outputs["Z0 (Ω)"] = mode.ImpedanceOhms;
                outputs["eps_eff"] = mode.EffectivePermittivity;
                outputs["Delay (ps/in)"] = mode.DelaySecondsPerMeter * 1e12 * 0.0254;
                outputs["Loss (dB/in)"] = mode.LossDbPerMeter * 0.0254;
                outputs["R (Ω/m)"] = mode.ResistanceOhmsPerMeter;
                outputs["L (nH/m)"] = mode.InductanceHenriesPerMeter * 1e9;
                outputs["C (pF/m)"] = mode.CapacitanceFaradsPerMeter * 1e12;
                outputs["Line length (mm)"] = line.LengthMeters * 1e3;
                assumptions.Add($"Trace line at {frequency / 1e9:g4} GHz, board model (thickness, return-path loss, causal dielectric).");
                assumptions.AddRange(line.Assumptions);
            }
            first ??= run.Report with { Assumptions = run.Report.Assumptions.Concat(assumptions).ToList() };
            return Select(outputs, spec.Outputs);
        }, progress);
        nominal = first;
        return result;
    }

    /// <summary>A pad by "component.pin" (case-insensitive), or the nearest pad centre to "x,y" in mm.</summary>
    private static NetMesher.PadElectrode FindPad(IReadOnlyList<NetMesher.PadElectrode> pads, string spec, string role)
    {
        if (pads.Count == 0) throw new InvalidOperationException("The net has no pad electrodes to connect to.");
        var byPin = pads.Where(p => p.ComponentRef is not null &&
                                    string.Equals($"{p.ComponentRef}.{p.Pin}", spec, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byPin.Count == 1) return byPin[0];
        if (byPin.Count > 1) throw new InvalidOperationException($"The {role} '{spec}' names {byPin.Count} pads; give x,y in mm instead.");
        var xy = spec.Split(',');
        if (xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                           && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            return pads.MinBy(p => Math.Pow(p.Center.X - x * 1e-3, 2) + Math.Pow(p.Center.Y - y * 1e-3, 2))!;
        throw new InvalidOperationException(
            $"The {role} '{spec}' is not a pad of the net. Pads: {string.Join("; ", pads.Select(p => p.Label))}.");
    }

    private static IReadOnlyDictionary<string, double> Select(IReadOnlyDictionary<string, double> outputs, List<string>? wanted)
    {
        if (wanted is not { Count: > 0 }) return outputs;
        var missing = wanted.Where(w => !outputs.ContainsKey(w)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Output(s) not produced: {string.Join(", ", missing)}. Available: {string.Join(", ", outputs.Keys)}.");
        return wanted.ToDictionary(w => w, w => outputs[w]);
    }

    private static IEnumerable<string> YieldLines(SweepResult result, Dictionary<string, double[]>? limits)
    {
        if (limits is null || result.Rows.Count == 0) yield break;
        var ci = CultureInfo.InvariantCulture;
        foreach (var (output, band) in limits)
            yield return $"  Yield of {output} within [{band[0].ToString("g5", ci)}, {band[1].ToString("g5", ci)}]: " +
                         $"{(result.Yield(output, band[0], band[1]) * 100).ToString("g4", ci)} %";
    }

    private static string StatisticsJson(SweepResult result, Dictionary<string, double[]>? limits)
    {
        var stats = new Dictionary<string, object>();
        foreach (string output in result.OutputNames)
        {
            var s = result.Statistics(output);
            var entry = new Dictionary<string, double>
            {
                ["samples"] = s.Count, ["mean"] = s.Mean, ["sigma"] = s.StandardDeviation, ["min"] = s.Min,
                ["p5"] = s.Percentile5, ["p50"] = s.Percentile50, ["p95"] = s.Percentile95, ["max"] = s.Max
            };
            if (limits is not null && limits.TryGetValue(output, out var band))
            {
                entry["low"] = band[0]; entry["high"] = band[1]; entry["yield"] = result.Yield(output, band[0], band[1]);
            }
            stats[output] = entry;
        }
        return JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>A report of a sweep: the plan, the ranges, the sensitivities (one at a time),
    /// the statistics and yield (Monte Carlo), every run, and the nominal run's assumptions.</summary>
    public static StudyReport SweepReport(string title, SweepResult result, IReadOnlyList<SweepParameterSpec> specs,
        StudyReport? nominal, Dictionary<string, double[]>? limits = null)
    {
        var ci = CultureInfo.InvariantCulture;
        var inputs = new List<(string, string)> { ("Plan", $"{result.Plan.Mode}, {result.Rows.Count} runs") };
        foreach (var p in result.Plan.Parameters)
        {
            var spec = specs.FirstOrDefault(s => s.Name == p.Name);
            inputs.Add((p.Name, $"{(spec is null ? "" : spec.Target + ": ")}{string.Join(", ", p.Values.Select(v => v.ToString("g5", ci)))}" +
                                $"{(p.Unit is null ? "" : " " + p.Unit)} (nominal {p.Nominal.ToString("g5", ci)})"));
        }
        var ranges = new List<IReadOnlyList<string>>();
        foreach (string output in result.OutputNames)
        {
            var (min, atMin, max, atMax) = result.Range(output);
            ranges.Add(new[] { output, min.ToString("g6", ci), Point(atMin), max.ToString("g6", ci), Point(atMax) });
        }
        var sensitivities = result.Sensitivities().Select(s => (IReadOnlyList<string>)new[]
        {
            s.Output, s.Parameter, s.Slope.ToString("g5", ci),
            double.IsNaN(s.Normalized) ? "—" : (s.Normalized * 100).ToString("g4", ci), s.Range.ToString("g5", ci)
        }).ToList();
        var runs = result.Rows.Select(r => (IReadOnlyList<string>)new[] { r.Point.Index.ToString(ci) }
            .Concat(result.Plan.Parameters.Select(p => r.Point[p.Name].ToString("g6", ci)))
            .Concat(result.OutputNames.Select(n => r.Outputs.TryGetValue(n, out double v) ? v.ToString("g6", ci) : "—"))
            .Concat(new[] { r.Error ?? "" }).ToList()).ToList();

        var sections = new List<ReportSection>
        {
            new("Ranges") { TableHeaders = new[] { "Output", "Min", "at", "Max", "at" }, TableRows = ranges }
        };
        if (sensitivities.Count > 0)
            sections.Add(new("Sensitivities (one at a time)")
            {
                TableHeaders = new[] { "Output", "Parameter", "Slope", "% per % of nominal", "Range over the values" },
                TableRows = sensitivities
            });
        var assumptions = new List<string>
        {
            "Each run is an independent solve of the project with the listed values set; nothing carries over between runs."
        };
        if (result.Plan.Mode == SweepMode.MonteCarlo)
        {
            inputs.Add(("Samples", $"{result.Plan.Samples} random draws after the nominal, seed {result.Plan.Seed}; " +
                                   string.Join(", ", result.Plan.Parameters.Select(p => p.Distribution == SweepDistribution.Normal
                                       ? $"{p.Name} normal σ {p.Sigma.ToString("g4", ci)}"
                                       : $"{p.Name} uniform [{p.Minimum.ToString("g5", ci)}, {p.Maximum.ToString("g5", ci)}]"))));
            var statistics = result.OutputNames.Where(n => result.Rows.Any(r => r.Succeeded && r.Point.Sampled && r.Outputs.ContainsKey(n)))
                .Select(result.Statistics).Select(s => (IReadOnlyList<string>)new[]
                {
                    s.Output, s.Count.ToString(ci), s.Mean.ToString("g6", ci), s.StandardDeviation.ToString("g5", ci),
                    s.Min.ToString("g6", ci), s.Percentile5.ToString("g6", ci), s.Percentile50.ToString("g6", ci),
                    s.Percentile95.ToString("g6", ci), s.Max.ToString("g6", ci)
                }).ToList();
            sections.Add(new("Statistics (Monte Carlo)")
            {
                TableHeaders = new[] { "Output", "Samples", "Mean", "σ", "Min", "P5", "P50", "P95", "Max" },
                TableRows = statistics
            });
            if (limits is { Count: > 0 })
                sections.Add(new("Yield")
                {
                    TableHeaders = new[] { "Output", "Low", "High", "Inside (%)" },
                    TableRows = limits.Select(kv => (IReadOnlyList<string>)new[]
                    {
                        kv.Key, kv.Value[0].ToString("g6", ci), kv.Value[1].ToString("g6", ci),
                        (result.Yield(kv.Key, kv.Value[0], kv.Value[1]) * 100).ToString("g4", ci)
                    }).ToList()
                });
            assumptions.Add("Monte Carlo: every parameter is drawn independently of the others at each sample — uniform over its range, " +
                            "or normal about its nominal with the stated σ (a sixth of the range unless given, so the tolerance band is ±3σ). " +
                            "Statistics and yield are over the successful samples; the nominal row is not a draw, and a failed run counts as outside every limit. " +
                            "The draws are a fixed function of the seed, so the same job gives the same points on any machine.");
        }
        else
            assumptions.Add("Sensitivities are secant slopes between a parameter's smallest and largest value with the others at nominal.");
        sections.Add(new("Runs")
        {
            TableHeaders = new[] { "Run" }.Concat(result.Plan.Parameters.Select(p => p.Name)).Concat(result.OutputNames).Concat(new[] { "Error" }).ToList(),
            TableRows = runs
        });
        if (nominal is not null) assumptions.AddRange(nominal.Assumptions);
        return new StudyReport
        {
            Title = title, Inputs = inputs, Sections = sections, Assumptions = assumptions,
            Results = result.Failures > 0 ? new[] { ("Failed runs", result.Failures.ToString(ci)) } : Array.Empty<(string, string)>()
        };

        string Point(SweepPoint p) => string.Join(", ", result.Plan.Parameters.Select(q => $"{q.Name} = {p[q.Name].ToString("g4", ci)}"));
    }

    // ---------------- impedance ----------------

    private static int Impedance(string jobPath, Dictionary<string, string> o, TextWriter stdout)
    {
        var spec = ImpedanceSpec.Load(jobPath);
        var line = spec.Line!;
        if (spec.Foil is { Length: > 0 } foilName)
        {
            var catalog = new FoilCatalog();
            if (spec.Foils is { Length: > 0 } foilsPath)
                catalog = catalog.WithFile(Path.IsPathRooted(foilsPath)
                    ? foilsPath
                    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jobPath)) ?? ".", foilsPath));
            CopperFoil foil;
            try { foil = catalog.Require(foilName); }
            catch (KeyNotFoundException ex) { throw new UsageException(ex.Message); }
            line = line with { Model = line.Model with { Roughness = foil.Roughness(line.ThicknessMeters) } };
        }
        string dir = OutDir(jobPath, o, "-impedance");

        var nominalReport = ImpedanceCalculator.Solve(line, spec.PanelsPerTrace);
        foreach (var l in nominalReport.Describe()) stdout.WriteLine(l);
        var report = new StudyReport
        {
            Title = $"{Path.GetFileNameWithoutExtension(jobPath)} — impedance",
            Inputs = LineInputs(line),
            Results = ImpedanceSpec.Outputs(nominalReport).Select(kv => (kv.Key, kv.Value.ToString("g5", CultureInfo.InvariantCulture))).ToList(),
            Sections = new[] { new ReportSection("Line") { Lines = nominalReport.Describe() } },
            Assumptions = nominalReport.Assumptions
        };
        File.WriteAllText(Path.Combine(dir, "impedance-report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "impedance-report.html"), report.ToHtml());

        if (spec.Parameters is { Count: > 0 })
        {
            var plan = SweepSpec.ParsePlan(spec.Parameters, spec.Mode, spec.Samples, spec.Seed);
            var targets = spec.Parameters.ToDictionary(p => p.Name, p => p.Target, StringComparer.OrdinalIgnoreCase);
            var result = ParameterSweep.Run(plan, values =>
            {
                var swept = line;
                foreach (var (name, value) in values) swept = ImpedanceSpec.With(swept, targets[name], value);
                return ImpedanceSpec.Outputs(ImpedanceCalculator.Solve(swept, spec.PanelsPerTrace));
            });
            File.WriteAllText(Path.Combine(dir, "impedance-sweep.csv"), result.ToCsv());
            var sweepReport = SweepReport($"{Path.GetFileNameWithoutExtension(jobPath)} — stackup tolerance sweep", result,
                spec.Parameters, report, spec.Limits);
            File.WriteAllText(Path.Combine(dir, "impedance-sweep-report.md"), sweepReport.ToMarkdown());
            File.WriteAllText(Path.Combine(dir, "impedance-sweep-report.html"), sweepReport.ToHtml());
            if (plan.Mode == SweepMode.MonteCarlo)
                File.WriteAllText(Path.Combine(dir, "impedance-statistics.json"), StatisticsJson(result, spec.Limits));
            foreach (var l in result.Describe()) stdout.WriteLine(l);
            foreach (var l in YieldLines(result, spec.Limits)) stdout.WriteLine(l);
            if (result.Failures > 0)
            {
                foreach (var row in result.Rows.Where(r => !r.Succeeded)) stdout.WriteLine($"  run {row.Point.Index} failed: {row.Error}");
                stdout.WriteLine($"Results written to {dir}");
                return 2;
            }
        }
        stdout.WriteLine($"Results written to {dir}");
        return 0;
    }

    // ---------------- foils ----------------

    private static int Foils(Dictionary<string, string> o, TextWriter stdout)
    {
        var catalog = new FoilCatalog();
        if (o.TryGetValue("foils", out var path)) catalog = catalog.WithFile(path);
        IEnumerable<CopperFoil> foils = catalog.Foils;
        if (o.TryGetValue("name", out var name))
        {
            try { foils = new[] { catalog.Require(name) }; }
            catch (KeyNotFoundException ex) { throw new UsageException(ex.Message); }
        }
        stdout.WriteLine("Huray parameters by the Cannonball stack (14 spheres, A_flat = 36r², r = Rz/(4√3(1+√2)) or " +
                         "RMS/(2(1+√2))) from each data sheet's treated-side roughness; a '≤' value is the sheet's limit.");
        foreach (var foil in foils)
            foreach (var line in foil.Describe()) stdout.WriteLine(line);
        return 0;
    }

    private static List<(string, string)> LineInputs(LineSpec line)
    {
        var ci = CultureInfo.InvariantCulture;
        var inputs = new List<(string, string)>
        {
            ("Structure", line.Structure.ToString()),
            ("Width", $"{line.WidthMeters * 1e6:g5} µm" + (line.TopWidthMeters is { } tw ? $" (top {tw * 1e6:g5} µm)" : "")),
            ("Thickness", $"{line.ThicknessMeters * 1e6:g5} µm"),
            ("Height to plane", $"{line.HeightMeters * 1e6:g5} µm, εr {line.RelativePermittivity.ToString("g4", ci)}, tanδ {line.LossTangent.ToString("g3", ci)}"),
            ("Frequency", $"{line.FrequencyHz / 1e9:g4} GHz"),
            ("Model", line.Model.ToString())
        };
        if (line.Model.Roughness is { } rough) inputs.Add(("Roughness", rough.Describe()));
        if (line.PairGapMeters is { } gap) inputs.Add(("Pair gap", $"{gap * 1e6:g5} µm"));
        if (line.CoplanarGapMeters is { } cg) inputs.Add(("Coplanar gap", $"{cg * 1e6:g5} µm, ground width {line.CoplanarGroundWidthMeters * 1e6:g5} µm"));
        if (line.Structure != LineStructure.Microstrip)
            inputs.Add(("Upper dielectric", $"{line.UpperHeightMeters * 1e6:g5} µm, εr {line.UpperRelativePermittivity.ToString("g4", ci)}"));
        return inputs;
    }

    // ---------------- spacing ----------------

    private static int Spacing(string boardPath, Dictionary<string, string> o, TextWriter stdout)
    {
        if (!o.TryGetValue("volts", out var voltsPath)) throw new UsageException("'spacing' needs --volts nets.csv.");
        var volts = new Dictionary<string, double>();
        foreach (var raw in File.ReadAllLines(voltsPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var cells = line.Split(',');
            if (cells.Length < 2) throw new InvalidDataException($"'{line}' is not 'net,volts'.");
            if (!double.TryParse(cells[^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                if (volts.Count == 0) continue;                      // a header line
                throw new InvalidDataException($"'{cells[^1]}' is not a voltage.");
            }
            volts[string.Join(",", cells[..^1]).Trim()] = v;
        }
        if (volts.Count < 2) throw new InvalidDataException("The voltage file names fewer than two nets.");

        var board = Ipc2581Reader.Matches(boardPath) ? new Ipc2581Reader().Read(boardPath) : new PcbBoardReader().Read(boardPath);
        stdout.WriteLine($"Board: {board.Nets.Count} nets, {board.Islands.Count} copper islands" +
                         (board.Warnings.Count > 0 ? $", {board.Warnings.Count} import warning(s)." : "."));

        var table = Ipc2221Spacing.Table6_1;
        if (o.TryGetValue("table", out var tablePath))
            table = SpacingTable.ParseCsv(File.ReadAllText(tablePath), Path.GetFileNameWithoutExtension(tablePath), $"loaded from {tablePath}");
        bool ipc = ReferenceEquals(table, Ipc2221Spacing.Table6_1);
        var options = new SpacingCheckOptions
        {
            Table = table,
            OuterColumn = ipc ? (o.ContainsKey("coated") ? "B4" : "B2") : table.Columns[0],
            CoatedOuterColumn = ipc && o.ContainsKey("mask") && !o.ContainsKey("coated") ? "B4" : null,
            InnerColumn = ipc ? "B1" : table.Columns[Math.Min(1, table.Columns.Count - 1)],
            ReportWithinFactor = Math.Max(1, Number(o, "within", 2))
        };
        var report = SpacingChecker.Check(board, volts, options);
        foreach (var line in report.Describe()) stdout.WriteLine(line);

        string dir = OutDir(boardPath, o, "-spacing");
        var sb = new StringBuilder("kind,net A,net B,layer,volts,measured (mm),required (mm),column,passes,x (mm),y (mm)\r\n");
        foreach (var f in report.Findings)
            sb.Append(CultureInfo.InvariantCulture,
                $"{f.Kind},{Csv(f.NetA)},{Csv(f.NetB)},{f.Layer},{f.Volts:R},{f.MeasuredMeters * 1e3:R},{f.RequiredMeters * 1e3:R},{f.Column},{f.Passes},{f.At.X * 1e3:R},{f.At.Y * 1e3:R}\r\n");
        File.WriteAllText(Path.Combine(dir, "spacing.csv"), sb.ToString());
        var study = new StudyReport
        {
            Title = $"{Path.GetFileNameWithoutExtension(boardPath)} — spacing check",
            Inputs = new List<(string, string)> { ("Table", $"{table.Name} ({table.Source})"), ("Outer column", options.OuterColumn), ("Inner column", options.InnerColumn) }
                .Concat(volts.Select(kv => ($"Net {kv.Key}", $"{kv.Value.ToString("g5", CultureInfo.InvariantCulture)} V"))).ToList(),
            Results = new[] { ("Pairs listed", report.Findings.Count.ToString()), ("Below requirement", report.Violations.Count.ToString()) },
            Sections = new[]
            {
                new ReportSection("Findings")
                {
                    TableHeaders = new[] { "Kind", "Net A", "Net B", "Layer", "V", "Measured (mm)", "Required (mm)", "Column", "Passes" },
                    TableRows = report.Findings.OrderBy(f => f.Passes).ThenBy(f => f.MarginMeters).Select(f => (IReadOnlyList<string>)new[]
                    {
                        f.Kind.ToString(), f.NetA, f.NetB, f.Layer.ToString(), f.Volts.ToString("g4", CultureInfo.InvariantCulture),
                        (f.MeasuredMeters * 1e3).ToString("0.###", CultureInfo.InvariantCulture),
                        (f.RequiredMeters * 1e3).ToString("0.###", CultureInfo.InvariantCulture), f.Column, f.Passes ? "yes" : "NO"
                    }).ToList()
                }
            },
            Assumptions = report.Notes.Concat(board.Warnings.Select(w => "Import: " + w)).ToList()
        };
        File.WriteAllText(Path.Combine(dir, "spacing-report.md"), study.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "spacing-report.html"), study.ToHtml());
        stdout.WriteLine($"Results written to {dir}");
        return report.Violations.Count == 0 ? 0 : 3;
    }

    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
}
