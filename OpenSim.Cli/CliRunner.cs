using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenSim.Core.Interfaces;
using OpenSim.Core.Model;
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
            Runs the project's analysis (or --analysis NAME) on its first solid body.
            --analysis NAME     Static, Electrical, Thermal, JouleCoupled, TransientThermal,
                                Modal, AcElectrical, Electrostatic, EnvironmentThermal
            --body NAME         the body to solve (single-body analyses)
            --out DIR           results folder (default: <project>-results next to the file)
            --remesh            re-mesh every body with its stored settings
            --fields            also write every result field as CSV
            --duration S --step S --initial K        transient settings
            --fmin HZ --fmax HZ --points N           AC sweep settings
            --modes N                                modal
            --steady                                 environment heat flow: equilibrium only

        opensim sweep <job.json> [--out DIR]
            Runs the project at every point of a parameter plan and tabulates the outputs.
            Job: { "project": "...", "analysis": "...", "mode": "OneAtATime|FullFactorial|Corners",
                   "parameters": [ { "name": "...", "target": "condition:hot.value", "values": [...] },
                                   { "name": "...", "target": "material:FR4.relativePermittivity",
                                     "nominal": 4.4, "tolerancePercent": 10, "points": 3 } ],
                   "outputs": [ "Max Temperature (K)" ] }
            Targets: condition:<name>.value|.ambient, body:<name>.heatSourcePower,
                     material:<name>.<property>, mesh:<body>.targetEdgeLength,
                     geometry:<body>.sizeX|sizeY|sizeZ, environment.ambientTemperature,
                     settings.duration|timeStep|initialTemperature|minFrequency|maxFrequency

        opensim impedance <line.json> [--out DIR]
            The stackup and impedance calculator, optionally swept over the line's own
            properties (stackup tolerances).
            Job: { "line": { "structure": "Microstrip", "widthMeters": 3e-4, "heightMeters": 2e-4,
                             "relativePermittivity": 4.4, "thicknessMeters": 35e-6 },
                   "mode": "OneAtATime",
                   "parameters": [ { "name": "w", "target": "WidthMeters", "nominal": 3e-4,
                                     "tolerancePercent": 10, "points": 3 } ] }

        opensim spacing <board.zip|folder|ipc2581.xml> --volts nets.csv [options]
            Clearance and creepage of the board's nets against a spacing table.
            nets.csv: "net,volts" lines. --table FILE loads a table (CSV), --coated uses the
            coated outer-layer column, --within F lists pairs within F × their requirement.

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
            var options = ParseOptions(args.Skip(2));
            return args[0].ToLowerInvariant() switch
            {
                "solve" => Solve(Require(args, "a project file"), options, stdout),
                "sweep" => Sweep(Require(args, "a sweep job file"), options, stdout),
                "impedance" => Impedance(Require(args, "an impedance job file"), options, stdout),
                "spacing" => Spacing(Require(args, "a board"), options, stdout),
                "emi" => Emi(Require(args, "an EMI job file"), options, stdout),
                "magnetics" => Magnetics(Require(args, "a board"), options, stdout),
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

    private static StudySettings Settings(Dictionary<string, string> o) => new()
    {
        InitialTemperature = Number(o, "initial", 293.15),
        Duration = Number(o, "duration", 10),
        TimeStep = Number(o, "step", 0.1),
        MinFrequency = Number(o, "fmin", 1e3),
        MaxFrequency = Number(o, "fmax", 1e8),
        PointCount = (int)Number(o, "points", 15),
        ModeCount = (int)Number(o, "modes", 6),
        EnvironmentSteady = o.ContainsKey("steady"),
        BodyName = o.GetValueOrDefault("body"),
        Remesh = o.ContainsKey("remesh")
    };

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
        var settings = Settings(o);
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
        var baseSettings = spec.Settings ?? new StudySettings();
        var serializer = new ProjectSerializer();
        var materials = new MaterialLibrary();
        var mesher = StudyRunner.DefaultMesher();
        var targets = spec.Parameters.ToDictionary(p => p.Name, p => p.Target, StringComparer.OrdinalIgnoreCase);
        StudyRun? nominal = null;

        stdout.WriteLine($"Sweep '{Path.GetFileName(jobPath)}': {plan.Count} run(s), {plan.Mode}.");
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
            nominal ??= run;
            var outputs = StudyRunner.Outputs(run);
            if (spec.Outputs is { Count: > 0 } wanted)
            {
                var missing = wanted.Where(w => !outputs.ContainsKey(w)).ToList();
                if (missing.Count > 0)
                    throw new InvalidOperationException($"Output(s) not produced: {string.Join(", ", missing)}. Available: {string.Join(", ", outputs.Keys)}.");
                outputs = wanted.ToDictionary(w => w, w => outputs[w]);
            }
            return outputs;
        }, new Progress<(int Done, int Total)>(p => stdout.WriteLine($"  run {p.Done} of {p.Total}")));

        string dir = OutDir(jobPath, o, "-sweep");
        File.WriteAllText(Path.Combine(dir, "sweep.csv"), result.ToCsv());
        var report = SweepReport($"{Path.GetFileNameWithoutExtension(spec.Project)} — parameter sweep", result,
            spec.Parameters, nominal?.Report);
        File.WriteAllText(Path.Combine(dir, "sweep-report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "sweep-report.html"), report.ToHtml());
        foreach (var line in result.Describe()) stdout.WriteLine(line);
        foreach (var row in result.Rows.Where(r => !r.Succeeded))
            stdout.WriteLine($"  run {row.Point.Index} failed: {row.Error}");
        stdout.WriteLine($"Results written to {dir}");
        return result.Failures == 0 ? 0 : 2;
    }

    /// <summary>A report of a sweep: the plan, the ranges, the sensitivities, every run, and
    /// the nominal run's assumptions.</summary>
    public static StudyReport SweepReport(string title, SweepResult result, IReadOnlyList<SweepParameterSpec> specs,
        StudyReport? nominal)
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
        sections.Add(new("Runs")
        {
            TableHeaders = new[] { "Run" }.Concat(result.Plan.Parameters.Select(p => p.Name)).Concat(result.OutputNames).Concat(new[] { "Error" }).ToList(),
            TableRows = runs
        });
        var assumptions = new List<string>
        {
            "Each run is an independent solve of the project with the listed values set; nothing carries over between runs.",
            "Sensitivities are secant slopes between a parameter's smallest and largest value with the others at nominal."
        };
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
            var plan = new SweepPlan
            {
                Parameters = spec.Parameters.Select(p => p.ToParameter()).ToList(),
                Mode = Enum.TryParse<SweepMode>(spec.Mode, true, out var mode) ? mode
                    : throw new InvalidOperationException($"Unknown sweep mode '{spec.Mode}'.")
            };
            var targets = spec.Parameters.ToDictionary(p => p.Name, p => p.Target, StringComparer.OrdinalIgnoreCase);
            var result = ParameterSweep.Run(plan, values =>
            {
                var swept = line;
                foreach (var (name, value) in values) swept = ImpedanceSpec.With(swept, targets[name], value);
                return ImpedanceSpec.Outputs(ImpedanceCalculator.Solve(swept, spec.PanelsPerTrace));
            });
            File.WriteAllText(Path.Combine(dir, "impedance-sweep.csv"), result.ToCsv());
            var sweepReport = SweepReport($"{Path.GetFileNameWithoutExtension(jobPath)} — stackup tolerance sweep", result,
                spec.Parameters, report);
            File.WriteAllText(Path.Combine(dir, "impedance-sweep-report.md"), sweepReport.ToMarkdown());
            File.WriteAllText(Path.Combine(dir, "impedance-sweep-report.html"), sweepReport.ToHtml());
            foreach (var l in result.Describe()) stdout.WriteLine(l);
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
