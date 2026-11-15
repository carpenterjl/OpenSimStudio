using System.Globalization;
using System.Text;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Core.Studies;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.MultiConfig;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.Cli;

// OP-97: the optics command family (docs/Optics-Conventions.md §46). Each subcommand reads a saved
// project's lens and writes a results folder like the other commands: report.md and report.html
// (StudyReport), CSV tables, and log.txt.

public static partial class CliRunner
{
    /// <summary>The optics subcommands, in the order Usage lists them.</summary>
    public static readonly IReadOnlyList<string> OpticsSubcommands = new[] { "list", "analyse", "trace", "optimise", "tolerance" };

    /// <summary>Subcommands planned but not built; each fails with a message naming its item.</summary>
    public static readonly IReadOnlyList<string> OpticsNotBuilt = new[]
    {
        "optics optimise --global (global search and Hammer, OP-58)",
        "optics tolerance --montecarlo (Monte Carlo tolerancing, OP-65)",
    };

    private static int Optics(string[] args, Dictionary<string, string> o, TextWriter stdout, CancellationToken cancellation)
    {
        if (args.Length < 2 || args[1].StartsWith("--"))
            throw new UsageException($"'optics' needs a subcommand: {string.Join(", ", OpticsSubcommands)}.");
        string sub = args[1].ToLowerInvariant();
        if (sub == "list") return OpticsList(o, stdout);
        string project = args.Length > 2 && !args[2].StartsWith("--") ? args[2]
            : throw new UsageException($"'optics {args[1]}' needs a project file.");
        return sub switch
        {
            "analyse" or "analyze" => OpticsAnalyse(project, o, stdout, cancellation),
            "trace" => OpticsTrace(project, o, stdout, cancellation),
            "optimise" or "optimize" => OpticsOptimise(project, o, stdout, cancellation),
            "tolerance" => OpticsTolerance(project, o, stdout, cancellation),
            _ => throw new UsageException($"Unknown optics subcommand '{args[1]}'; use {string.Join(", ", OpticsSubcommands)}.")
        };
    }

    /// <summary>The loaded project, its optics record (refused when it has no surfaces and
    /// <paramref name="needSurfaces"/>), and the glass catalogs the app would load.</summary>
    private static (SimProject Project, OpticsSettings Optics, IReadOnlyList<OpticalCatalog> Catalogs) LoadOptics(
        string projectPath, Dictionary<string, string> o, Action<string> log, bool needSurfaces)
    {
        var project = new ProjectSerializer().Load(projectPath);
        var optics = project.Optics ?? new OpticsSettings();
        if (needSurfaces && optics.Surfaces is not { Count: > 0 })
            throw new InvalidOperationException($"The project '{project.Name}' has no lens: its optics record has no sequential surfaces.");
        return (project, optics, LoadedCatalogs(o, log));
    }

    /// <summary>The loaded glass catalogs: the app's library (the built-in set plus the user's
    /// catalog folder), or the built-in set alone with --builtin-catalogs.</summary>
    private static IReadOnlyList<OpticalCatalog> LoadedCatalogs(Dictionary<string, string> o, Action<string> log)
    {
        if (o.ContainsKey("builtin-catalogs"))
        {
            log("Glass catalogs: the built-in set only (--builtin-catalogs).");
            // In the library's order (built-ins by name), so the active list is the app's with an empty catalog folder.
            return GlassCatalogLibrary.BuiltInCatalogs.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        var library = new GlassCatalogLibrary();
        foreach (var w in library.LoadWarnings) log("Glass catalogs: " + w);
        log($"Glass catalogs: {string.Join(", ", library.Catalogs.Select(c => c.Name))} (built-in and {library.CatalogFolder}).");
        return library.Catalogs;
    }

    private static string Join(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    // ---------------- list ----------------

    private static int OpticsList(Dictionary<string, string> o, TextWriter stdout)
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        IEnumerable<IOpticsAnalysis> shown = registry.All;
        if (o.TryGetValue("analysis", out var id))
        {
            if (!registry.TryGet(id, out var one)) throw new UsageException(UnknownAnalysis(registry, id));
            shown = new[] { one };
        }
        var ci = CultureInfo.InvariantCulture;
        foreach (var a in shown)
        {
            stdout.WriteLine($"{a.Id}  —  {a.Name} ({a.Category})");
            if (!o.ContainsKey("analysis")) continue;
            var defaults = a.CreateDefaultSettings();
            foreach (var (name, type) in OpticsBatch.SettingNames(a))
            {
                object? value = a.SettingsType.GetProperty(name)?.GetValue(defaults);
                string shownValue = value is IFormattable f ? f.ToString(null, ci) : value?.ToString() ?? "null";
                var t = Nullable.GetUnderlyingType(type) ?? type;
                string kind = t.IsEnum ? string.Join("|", Enum.GetNames(t)) : t.Name;
                stdout.WriteLine($"    {name} = {shownValue}   ({kind})");
            }
        }
        return 0;
    }

    private static string UnknownAnalysis(OpticsAnalysisRegistry registry, string id) =>
        $"No analysis has the id '{id}'. 'opensim optics list' lists them; ids: {string.Join(", ", registry.All.Select(a => a.Id))}.";

    // ---------------- analyse and trace ----------------

    private static int OpticsAnalyse(string projectPath, Dictionary<string, string> o, TextWriter stdout, CancellationToken cancellation)
    {
        if (!o.TryGetValue("analysis", out var list) || list == "true")
            throw new UsageException("'optics analyse' needs --analysis ID[,ID...] ('opensim optics list' lists the ids).");
        var registry = OpticsAnalysisRegistry.CreateDefault();
        var ids = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) throw new UsageException("--analysis names no analysis.");
        var analyses = ids.Select(i => registry.TryGet(i, out var a) ? a : throw new UsageException(UnknownAnalysis(registry, i))).ToList();
        if (analyses.Count > 1 && o.ContainsKey("settings"))
            throw new UsageException("--settings FILE applies to a single analysis; with several, set values by --set \"ID.Name=value\".");
        return RunAnalyses(projectPath, o, stdout, cancellation, analyses, "-optics-analyse", o.GetValueOrDefault("set"), extra: null);
    }

    private static int OpticsTrace(string projectPath, Dictionary<string, string> o, TextWriter stdout, CancellationToken cancellation)
    {
        var registry = OpticsAnalysisRegistry.CreateDefault();
        registry.TryGet(SingleRayTraceAnalysis.SavedId, out var analysis);
        var extra = new List<KeyValuePair<string, string>>();
        foreach (var (option, setting) in new[] { ("hx", "Hx"), ("hy", "Hy"), ("px", "Px"), ("py", "Py"), ("wavelength", "Wavelength") })
            if (o.TryGetValue(option, out var v)) extra.Add(new(setting, v));
        if (o.ContainsKey("global")) extra.Add(new("GlobalCoordinates", "true"));
        return RunAnalyses(projectPath, o, stdout, cancellation, new[] { analysis }, "-optics-trace", null, extra, echoText: true);
    }

    /// <summary>Computes the analyses on the project's lens with the settings the project saved
    /// for each (the app's rule), then --settings and --set; writes the composite report (the
    /// app's Save report, byte for byte), each analysis's text and plot data, and the log.</summary>
    private static int RunAnalyses(string projectPath, Dictionary<string, string> o, TextWriter stdout, CancellationToken cancellation,
        IReadOnlyList<IOpticsAnalysis> analyses, string suffix, string? set, IReadOnlyList<KeyValuePair<string, string>>? extra,
        bool echoText = false)
    {
        var log = new List<string>();
        void Log(string line) { log.Add(line); stdout.WriteLine(line); }
        var (project, optics, catalogs) = LoadOptics(projectPath, o, Log, needSurfaces: false);
        if (optics.Surfaces is not { Count: > 0 }) Log($"Note: the project '{project.Name}' has no sequential surfaces.");
        var overrides = ParseSet(set, analyses);
        var runs = new List<(IOpticsAnalysis, object)>();
        foreach (var analysis in analyses)
        {
            var settings = OpticsBatch.SavedSettings(analysis, optics, out var note);
            if (note is not null) Log(note);
            try
            {
                if (o.TryGetValue("settings", out var file)) settings = OpticsBatch.SettingsFromJson(analysis, File.ReadAllText(file));
                var mine = overrides.Where(x => x.Analysis is null || x.Analysis == analysis.Id).Select(x => new KeyValuePair<string, string>(x.Name, x.Value)).ToList();
                if (extra is not null) mine.AddRange(extra);
                settings = OpticsBatch.WithOverrides(analysis, settings, mine);
            }
            catch (ArgumentException ex) { throw new UsageException(ex.Message); }
            runs.Add((analysis, settings));
            Log($"{analysis.Name} ({analysis.Id}): {OpticsAnalysisSettingsCodec.Save(settings).GetRawText()}");
        }

        var context = OpticsBatch.Context(project.Name, optics, catalogs);
        var entries = OpticsBatch.Run(context, runs, cancellation);
        var report = OpticsBatch.Report(context, entries);

        string dir = OutDir(projectPath, o, suffix);
        File.WriteAllText(Path.Combine(dir, "report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "report.html"), report.ToHtml());
        var stems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int failed = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            string id = runs[i].Item1.Id;
            int n = stems[id] = stems.GetValueOrDefault(id) + 1;
            string stem = n == 1 ? id : $"{id}-{n}";
            if (entry.Result is not { } result)
            {
                failed++;
                Log($"{entry.Title}: {entry.Status}");
                continue;
            }
            File.WriteAllText(Path.Combine(dir, stem + ".txt"), result.Text);
            for (int k = 0; k < result.Plots.Count; k++)
            {
                try { File.WriteAllText(Path.Combine(dir, $"{stem}-plot{k + 1}.csv"), AnalysisPlotCsv.Write(result.Plots[k])); }
                catch (NotSupportedException ex) { Log($"{entry.Title}: {ex.Message}"); }
            }
            Log($"{entry.Title}: done, {result.Plots.Count} plot(s).");
            if (echoText) stdout.Write(result.Text);
        }
        File.WriteAllText(Path.Combine(dir, "log.txt"), Join(log));
        stdout.WriteLine($"Results written to {dir}");
        return failed == 0 ? 0 : 2;
    }

    /// <summary>--set "Name=value;Name=value": with one analysis plain names; with several,
    /// "ID.Name=value" (a plain name then applies to every listed analysis).</summary>
    private static List<(string? Analysis, string Name, string Value)> ParseSet(string? set, IReadOnlyList<IOpticsAnalysis> analyses)
    {
        var list = new List<(string?, string, string)>();
        if (set is null) return list;
        if (set == "true") throw new UsageException("--set needs \"Name=value[;Name=value...]\".");
        foreach (string item in set.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = item.IndexOf('=');
            if (eq <= 0) throw new UsageException($"--set '{item}' is not Name=value.");
            string name = item[..eq].Trim(), value = item[(eq + 1)..].Trim();
            string? id = null;
            int dot = name.IndexOf('.');
            if (dot > 0)
            {
                id = name[..dot];
                name = name[(dot + 1)..];
                var match = analyses.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))
                    ?? throw new UsageException($"--set '{item}': '{id}' is not one of the analyses run ({string.Join(", ", analyses.Select(a => a.Id))}).");
                id = match.Id;
            }
            list.Add((id, name, value));
        }
        return list;
    }

    // ---------------- optimise ----------------

    private static int OpticsOptimise(string projectPath, Dictionary<string, string> o, TextWriter stdout, CancellationToken cancellation)
    {
        if (o.ContainsKey("global") || o.ContainsKey("hammer"))
            // OP-58 hook: global search and Hammer optimization go here, one arm calling the OP-58 engine.
            throw new UsageException("'optics optimise --global' and '--hammer' are not built yet (global optimization, OP-58); run the local optimizer without them.");

        var log = new List<string>();
        void Log(string line) { log.Add(line); stdout.WriteLine(line); }
        var (project, optics, catalogs) = LoadOptics(projectPath, o, Log, needSurfaces: true);
        var active = GlassLookup.ActiveCatalogs(catalogs, optics.Materials?.ActiveCatalogs);
        var algorithm = Choice(o, "algorithm", LocalOptimizationAlgorithm.DampedLeastSquares, new()
        {
            ["dls"] = LocalOptimizationAlgorithm.DampedLeastSquares, ["od"] = LocalOptimizationAlgorithm.OrthogonalDescent
        });
        var cycles = LocalOptimizationCycles.Automatic;
        int cycleCount = 5;
        if (o.TryGetValue("cycles", out var c))
        {
            if (c.Equals("auto", StringComparison.OrdinalIgnoreCase)) cycles = LocalOptimizationCycles.Automatic;
            else if (c.Equals("infinite", StringComparison.OrdinalIgnoreCase)) cycles = LocalOptimizationCycles.Infinite;
            else if (int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out cycleCount) && cycleCount >= 1) cycles = LocalOptimizationCycles.Fixed;
            else throw new UsageException($"--cycles takes auto, infinite or a whole number of at least 1, not '{c}'.");
        }
        var defaults = new LocalOptimizationOptions();
        var options = new LocalOptimizationOptions
        {
            Algorithm = algorithm, Cycles = cycles, CycleCount = cycleCount,
            MaxEvaluations = (int)Number(o, "max-evaluations", defaults.MaxEvaluations),
            MaxDegreeOfParallelism = o.ContainsKey("threads") ? (int)Number(o, "threads", 1) : null,
            CancellationToken = cancellation,
            Progress = p => stdout.WriteLine($"  cycle {p.Cycle}: merit {StudyReport.Number(p.Merit, "g8")} (start {StudyReport.Number(p.StartMerit, "g8")}), {p.Evaluations} evaluations"),
        };
        if (cycles == LocalOptimizationCycles.Infinite) Log("Cycles: infinite; press Ctrl+C to stop and keep the best design so far.");

        LocalOptimizationResult result;
        try { result = LocalOptimizer.Run(optics, active, options); }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message, ex); }
        foreach (var line in result.Log) Log(line);

        string dir = OutDir(projectPath, o, "-optics-optimise");
        var report = OptimiseReport(project.Name, result, options);
        File.WriteAllText(Path.Combine(dir, "report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "report.html"), report.ToHtml());
        File.WriteAllText(Path.Combine(dir, "variables.csv"), PlotCsv.Table(new[] { "variable", "start", "end" },
            result.Variables.Select((v, i) => (IReadOnlyList<string>)new[] { v.Label, PlotCsv.Number(result.StartValues[i]), PlotCsv.Number(result.Values[i]) }),
            new[] { "Values in saved units; a radius as its curvature (1/lens unit)." }));
        File.WriteAllText(Path.Combine(dir, "merit.csv"), MeritCsv(result.Evaluation));
        var saved = new ProjectSerializer().Load(projectPath);
        saved.Optics = result.ApplyTo(saved.Optics ?? optics);
        string optimisedPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(projectPath) + "-optimised" + ProjectSerializer.FileExtension);
        new ProjectSerializer().Save(saved, optimisedPath);
        Log($"Optimised project written to {optimisedPath} (the input project is unchanged).");
        File.WriteAllText(Path.Combine(dir, "log.txt"), Join(log));
        stdout.WriteLine($"Results written to {dir}");
        if (result.Cancelled)
        {
            stdout.WriteLine("Cancelled: the best design so far is written.");
            return 2;
        }
        return 0;
    }

    /// <summary>The optimization report: the run's settings, merit before and after, every
    /// variable, every merit row, and the optimizer's log.</summary>
    public static StudyReport OptimiseReport(string projectName, LocalOptimizationResult result, LocalOptimizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        string N(double v) => StudyReport.Number(v, "g10");
        var ci = CultureInfo.InvariantCulture;
        return new StudyReport
        {
            Title = $"{projectName} — local optimization",
            Inputs = new List<(string, string)>
            {
                ("Algorithm", options.Algorithm == LocalOptimizationAlgorithm.DampedLeastSquares ? "damped least squares" : "orthogonal descent"),
                ("Cycles", options.Cycles == LocalOptimizationCycles.Fixed ? options.CycleCount.ToString(ci) : options.Cycles.ToString().ToLowerInvariant()),
                ("Evaluation limit", options.MaxEvaluations.ToString(ci)),
                ("Variables", result.Variables.Count.ToString(ci)),
            },
            Results = new List<(string, string)>
            {
                ("Merit function before", N(result.StartMerit)),
                ("Merit function after", N(result.Merit)),
                ("Stopped", result.Stop.ToString()),
                ("Cycles run", result.Cycles.ToString(ci)),
                ("Evaluations", result.Evaluations.ToString(ci)),
                ("Failed evaluations", result.FailedEvaluations.ToString(ci)),
            },
            Sections = new[]
            {
                new ReportSection("Variables")
                {
                    TableHeaders = new[] { "Variable", "Start", "End" },
                    TableRows = result.Variables.Select((v, i) => (IReadOnlyList<string>)new[] { v.Label, N(result.StartValues[i]), N(result.Values[i]) }).ToList()
                },
                new ReportSection("Merit function after") { TableHeaders = MeritHeaders, TableRows = MeritRows(result.Evaluation) },
                new ReportSection("Optimizer log") { Lines = result.Log },
            },
            Assumptions = new[]
            {
                "Local optimization (OP-57, docs/Optics-Conventions.md §35) of the saved variables against the saved merit function, over every configuration; solves and pickups follow the variables.",
                "Values are in saved units; a radius is varied and listed as its curvature (1/lens unit).",
                "A local optimizer finds the nearest minimum from the starting design, not the best design overall.",
            }
        };
    }

    private static readonly string[] MeritHeaders = { "Row", "Type", "Config", "Target", "Weight", "Value", "% contrib", "Status" };

    private static List<IReadOnlyList<string>> MeritRows(MeritEvaluation evaluation) =>
        evaluation.Rows.Select(r => (IReadOnlyList<string>)new[]
        {
            r.Number.ToString(CultureInfo.InvariantCulture), r.Row.Code, r.Configuration.ToString(CultureInfo.InvariantCulture),
            StudyReport.Number(r.Row.Target, "g8"), StudyReport.Number(r.Row.Weight, "g6"),
            double.IsNaN(r.Value) ? "—" : StudyReport.Number(r.Value, "g10"), StudyReport.Number(r.Contribution, "g4"),
            r.Error ?? r.Status.ToString()
        }).ToList();

    private static string MeritCsv(MeritEvaluation evaluation) =>
        PlotCsv.Table(new[] { "row", "type", "config", "target", "weight", "value", "contribution %", "status" },
            evaluation.Rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Number.ToString(CultureInfo.InvariantCulture), r.Row.Code, r.Configuration.ToString(CultureInfo.InvariantCulture),
                PlotCsv.Number(r.Row.Target), PlotCsv.Number(r.Row.Weight), PlotCsv.Number(r.Value), PlotCsv.Number(r.Contribution),
                r.Error ?? r.Status.ToString()
            }),
            new[] { $"merit function {PlotCsv.Number(evaluation.Value)}" });

    // ---------------- tolerance ----------------

    private static int OpticsTolerance(string projectPath, Dictionary<string, string> o, TextWriter stdout, CancellationToken cancellation)
    {
        if (o.ContainsKey("montecarlo"))
            // OP-65 hook: Monte Carlo tolerancing goes here, one arm calling the OP-65 engine after (or instead of) the sensitivity run.
            throw new UsageException("'optics tolerance --montecarlo' is not built yet (Monte Carlo tolerancing, OP-65); run sensitivity or an inverse mode.");

        var log = new List<string>();
        void Log(string line) { log.Add(line); stdout.WriteLine(line); }
        var (project, optics, catalogs) = LoadOptics(projectPath, o, Log, needSurfaces: true);
        var active = GlassLookup.ActiveCatalogs(catalogs, optics.Materials?.ActiveCatalogs);
        var notes = new List<string>();
        var table = ToleranceDataSettings.FromSettings(optics.Tolerances, notes);
        if (table.Rows.Count == 0)
            throw new InvalidOperationException($"The project '{project.Name}' has no tolerance rows: build them in the Tolerance Data Editor first.");
        var options = ToleranceRunSettings.FromSettings(optics.Tolerances?.Run, notes);
        foreach (var n in notes) Log(n);
        bool quick = false;
        if (o.TryGetValue("mode", out var mode))
        {
            switch (mode.ToLowerInvariant())
            {
                case "sensitivity": options = options with { Mode = ToleranceMode.Sensitivity }; break;
                case "inverse-limit": options = options with { Mode = ToleranceMode.InverseLimit }; break;
                case "inverse-increment": options = options with { Mode = ToleranceMode.InverseIncrement }; break;
                case "quick": quick = true; break;
                case "montecarlo":
                    // OP-65 hook (see --montecarlo above).
                    throw new UsageException("'optics tolerance --mode montecarlo' is not built yet (Monte Carlo tolerancing, OP-65).");
                default: throw new UsageException($"--mode takes sensitivity, inverse-limit, inverse-increment or quick, not '{mode}'.");
            }
        }
        if (o.TryGetValue("criterion", out var criterion))
            options = options with
            {
                Criterion = Enum.TryParse<ToleranceCriterion>(criterion, true, out var cr) && Enum.IsDefined(cr) && !int.TryParse(criterion, out _) ? cr
                    : throw new UsageException($"--criterion takes {string.Join(", ", Enum.GetNames<ToleranceCriterion>())}, not '{criterion}'.")
            };
        options = options with
        {
            Compensation = Choice(o, "compensation", options.Compensation, new()
            {
                ["none"] = ToleranceCompensation.None, ["paraxial"] = ToleranceCompensation.ParaxialFocus,
                ["dls"] = ToleranceCompensation.OptimizeDls, ["od"] = ToleranceCompensation.OptimizeOd
            }),
            Change = Choice(o, "change", options.Change, new() { ["linear"] = ToleranceChangeMode.LinearDifference, ["rss"] = ToleranceChangeMode.RssDifference }),
            Fields = Choice(o, "fields", options.Fields, new() { ["y"] = ToleranceFieldSet.YSymmetric, ["xy"] = ToleranceFieldSet.XYSymmetric, ["user"] = ToleranceFieldSet.UserDefined }),
            Sampling = (int)Number(o, "sampling", options.Sampling),
            Limit = Number(o, "limit", options.Limit),
            Increment = Number(o, "increment", options.Increment),
            MtfFrequency = Number(o, "mtf-frequency", options.MtfFrequency),
            EncircledRadius = Number(o, "radius", options.EncircledRadius),
            OptimizerCycles = o.ContainsKey("cycles") ? (int)Number(o, "cycles", 0) : options.OptimizerCycles,
            ShowWorst = (int)Number(o, "worst", options.ShowWorst),
            MaxDegreeOfParallelism = o.ContainsKey("threads") ? (int)Number(o, "threads", 1) : null,
            CancellationToken = cancellation,
            Progress = p => stdout.WriteLine($"  {p.RowsDone} of {p.RowCount} rows, {p.Trials} trials{(p.Last is null ? "" : $" (last {p.Last})")}"),
        };

        ToleranceRunResult result;
        try
        {
            result = quick ? ToleranceAnalysis.QuickSensitivity(optics, table, active, options) : ToleranceAnalysis.Run(optics, table, active, options);
        }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message, ex); }

        string dir = OutDir(projectPath, o, "-optics-tolerance");
        var report = ToleranceReport(project.Name, result);
        File.WriteAllText(Path.Combine(dir, "report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "report.html"), report.ToHtml());
        // The Tolerance Data Editor's report text, as the app shows it (OpticsToleranceEditorViewModel.Show).
        File.WriteAllText(Path.Combine(dir, "tolerance.txt"), Join(result.ReportLines()));
        File.WriteAllText(Path.Combine(dir, "rows.csv"), ToleranceCsv(result));
        foreach (var line in result.ReportLines().Take(2)) Log(line);
        int failed = result.Rows.Count(r => !r.AtMin.Succeeded || !r.AtMax.Succeeded);
        if (failed > 0) Log($"{failed} row(s) could not be evaluated; see the report's notes.");
        File.WriteAllText(Path.Combine(dir, "log.txt"), Join(log));
        stdout.WriteLine($"Results written to {dir}");
        return failed == 0 ? 0 : 2;
    }

    /// <summary>The tolerance report: the run's settings, the nominal criterion and estimate, one
    /// row per tolerance, the worst offenders, the notes and the assumptions.</summary>
    public static StudyReport ToleranceReport(string projectName, ToleranceRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string V(double v) => double.IsNaN(v) ? "—" : StudyReport.Number(v, "g6");
        var info = ToleranceCriteria.Info(result.Options.Criterion);
        var ci = CultureInfo.InvariantCulture;
        var sections = new List<ReportSection>
        {
            new("Tolerances")
            {
                TableHeaders = new[] { "Row", "Type", "Min", "Max", "Criterion (min)", "Change (min)", "Criterion (max)", "Change (max)", "Note" },
                TableRows = result.Rows.Select(r => (IReadOnlyList<string>)new[]
                {
                    r.Row.ToString(ci), r.Code, V(r.Min), V(r.Max), V(r.AtMin.Criterion), V(r.AtMin.Change), V(r.AtMax.Criterion), V(r.AtMax.Change),
                    !r.AtMin.Succeeded ? $"min: {r.AtMin.Failure}" : !r.AtMax.Succeeded ? $"max: {r.AtMax.Failure}"
                        : r.Adjusted ? $"was {V(r.StartMin)}, {V(r.StartMax)}" : ""
                }).ToList()
            },
            new("Worst offenders")
            {
                TableHeaders = new[] { "Row", "Type", "Side", "Value", "Criterion", "Change" },
                TableRows = result.WorstOffenders.Select(t => (IReadOnlyList<string>)new[]
                {
                    t.Row.ToString(ci), t.Code, t.Side.ToString().ToLowerInvariant(), V(t.Value), V(t.Criterion), V(t.Change)
                }).ToList()
            },
        };
        if (result.Notes.Count > 0) sections.Add(new ReportSection("Notes") { Lines = result.Notes });
        string unit = result.Unit.Length == 0 ? "" : " " + result.Unit;
        return new StudyReport
        {
            Title = $"{projectName} — tolerance {result.Options.Mode switch { ToleranceMode.Sensitivity => "sensitivity", ToleranceMode.InverseLimit => "inverse limit", _ => "inverse increment" }}",
            Inputs = new List<(string, string)>
            {
                ("Criterion", info.Name + (result.Unit.Length == 0 ? "" : $" ({result.Unit})")),
                ("Change", result.Options.Change == ToleranceChangeMode.LinearDifference ? "linear difference" : "RSS difference"),
                ("Compensation", result.Options.Compensation.ToString()),
                ("Compensators", result.Compensators.Count == 0 ? "none" : string.Join("; ", result.Compensators)),
                ("Fields", result.Options.Fields.ToString()),
            },
            Results = new List<(string, string)>
            {
                ("Nominal criterion", V(result.Nominal) + unit),
                ("Estimated change (RSS)", result.EstimatedChange is double c ? V(c) + unit : "not available (see the notes)"),
                ("Estimated performance", result.EstimatedPerformance is double f ? V(f) + unit : "not available (see the notes)"),
                ("Trials", result.Trials.ToString(ci)),
            },
            Sections = sections,
            Assumptions = result.Assumptions,
        };
    }

    private static string ToleranceCsv(ToleranceRunResult result) =>
        PlotCsv.Table(new[] { "row", "type", "min", "max", "criterion min", "change min", "criterion max", "change max", "start min", "start max", "failure" },
            result.Rows.Select(r => (IReadOnlyList<string>)new[]
            {
                r.Row.ToString(CultureInfo.InvariantCulture), r.Code, PlotCsv.Number(r.Min), PlotCsv.Number(r.Max),
                PlotCsv.Number(r.AtMin.Criterion), PlotCsv.Number(r.AtMin.Change), PlotCsv.Number(r.AtMax.Criterion), PlotCsv.Number(r.AtMax.Change),
                PlotCsv.Number(r.StartMin), PlotCsv.Number(r.StartMax), r.AtMin.Failure ?? r.AtMax.Failure ?? ""
            }),
            new[] { $"nominal {PlotCsv.Number(result.Nominal)} {result.Unit}".TrimEnd() });

    // ---------------- sweep over optics targets ----------------

    /// <summary>True when the job's parameters set the lens (every target optics:...); a job
    /// mixing optics and other targets is refused, naming the others.</summary>
    internal static bool IsOpticsSweep(SweepSpec spec)
    {
        var optics = spec.Parameters.Where(p => OpticsTargets.Handles(p.Target)).ToList();
        if (optics.Count == 0) return false;
        if (optics.Count < spec.Parameters.Count)
            throw new InvalidOperationException("An optics sweep sets only the lens; these targets are not optics targets: "
                + string.Join(", ", spec.Parameters.Where(p => !OpticsTargets.Handles(p.Target)).Select(p => $"'{p.Name}' ({p.Target})")) + ".");
        if (spec.Board is not null) throw new InvalidOperationException("Optics targets apply to a project's lens, not to a board sweep.");
        return true;
    }

    /// <summary>
    /// The optics sweep: the project's saved lens with each point's values set
    /// (<see cref="OpticsTargets"/>), and its saved merit function evaluated over every
    /// configuration: the outputs are the merit function and every evaluated row's value
    /// ("Row n CODE"). A row that fails at a point fails that run, naming the row.
    /// </summary>
    private static SweepResult SweepOptics(SweepSpec spec, SweepPlan plan, IProgress<(int, int)> progress, TextWriter stdout,
        CancellationToken cancellation, out StudyReport nominal)
    {
        var project = new ProjectSerializer().Load(spec.Project);
        var optics = project.Optics ?? throw new InvalidOperationException($"The project '{project.Name}' has no optics record to sweep.");
        if (optics.Surfaces is not { Count: > 0 }) throw new InvalidOperationException($"The project '{project.Name}' has no lens surfaces to sweep.");
        OpticsTargets.Validate(optics, spec.Parameters);
        var registry = MeritOperandRegistry.CreateDefault();
        var notes = new List<string>();
        var function = MeritFunctionSettings.FromSettings(optics.MeritFunction, registry, notes);
        if (function.Rows.Count == 0)
            throw new InvalidOperationException("An optics sweep reports the saved merit function's rows, and the lens has none: build a merit function in the Merit Function Editor first.");
        var problems = MeritEvaluator.Validate(function, registry, MultiConfiguration.ConfigurationCount(optics));
        if (problems.Count > 0) throw new InvalidOperationException("Merit function:" + Environment.NewLine + Join(problems));
        var catalogs = GlassLookup.ActiveCatalogs(new GlassCatalogLibrary().Catalogs, optics.Materials?.ActiveCatalogs);
        var targets = spec.Parameters.ToDictionary(p => p.Name, p => p.Target, StringComparer.OrdinalIgnoreCase);
        foreach (var n in notes) stdout.WriteLine(n);

        var result = ParameterSweep.Run(plan, values =>
        {
            var o = optics;
            foreach (var (name, value) in values) o = OpticsTargets.Apply(o, targets[name], value);
            var evaluation = MeritEvaluator.Evaluate(function, new ProjectMeritConfigurations(o, catalogs), registry, cancellation: cancellation);
            var failed = evaluation.Rows.Where(r => r.Status == MeritRowStatus.Failed).ToList();
            if (failed.Count > 0) throw new InvalidOperationException(string.Join("; ", failed.Select(r => r.Error ?? $"row {r.Number} {r.Row.Code} failed")));
            var outputs = new Dictionary<string, double> { ["Merit function"] = evaluation.Value };
            foreach (var r in evaluation.Rows.Where(r => r.Status == MeritRowStatus.Evaluated))
                outputs[$"Row {r.Number} {r.Row.Code}"] = r.Value;
            return Select(outputs, spec.Outputs);
        }, progress, cancellation);
        nominal = new StudyReport
        {
            Title = project.Name,
            Assumptions = notes.Concat(new[]
            {
                "Optics sweep (OP-97): each run sets the listed lens cells on the saved lens and evaluates the saved merit function over every configuration; solves and pickups are evaluated again, and a multi-configuration operand on a swept cell still overrides it in its configurations.",
                "The outputs are the merit function and each evaluated row's value (\"Row n CODE\"), in the row's own unit.",
            }).ToList()
        };
        return result;
    }
}
