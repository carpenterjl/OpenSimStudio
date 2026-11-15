using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Persistence;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Optimization;
using OpenSim.Optics.Optimization.Global;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.Cli;

// Wave 8 wiring: the arms OP-97 left for OP-58 (optics optimise --global / --hammer) and OP-65
// (optics tolerance --montecarlo) (docs/Optics-Conventions.md §46). The engines are called as the
// app's Optimize window and Tolerance Data Editor call them; this file only reads the options,
// writes the results folder and sets the exit code.

public static partial class CliRunner
{
    /// <summary>A whole-number option of at least <paramref name="min"/>, or the fallback.</summary>
    private static int Whole(Dictionary<string, string> o, string key, int fallback, int min)
    {
        if (!o.TryGetValue(key, out var s)) return fallback;
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < min)
            throw new UsageException($"--{key} needs a whole number of at least {min.ToString(CultureInfo.InvariantCulture)}, not '{s}'.");
        return v;
    }

    private static long Seed64(Dictionary<string, string> o, long fallback) =>
        o.TryGetValue("seed", out var s)
            ? long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v
                : throw new UsageException($"--seed needs a whole number, not '{s}'.")
            : fallback;

    // ---------------- optimise --global / --hammer (OP-58) ----------------

    /// <summary>
    /// The Global or Hammer search on the saved lens: every option the command line does not give
    /// keeps the engine's own default (<see cref="GlobalSearchOptions"/>, <see cref="HammerSearchOptions"/>).
    /// Writes report.md/.html, designs.csv (the best designs), log.txt and the best design applied
    /// as &lt;project&gt;-optimised.ossproj. Exit 2 when cancelled (the designs found so far are
    /// written) or when no trial produced a design.
    /// </summary>
    private static int OpticsSearch(string projectPath, Dictionary<string, string> o, TextWriter stdout, CancellationToken cancellation,
        SimProject project, OpticsSettings optics, IReadOnlyList<OpticalCatalog> active, LocalOptimizationOptions local,
        List<string> log, Action<string> Log)
    {
        bool hammer = o.ContainsKey("hammer");
        if (hammer && o.ContainsKey("global")) throw new UsageException("Give --global or --hammer, not both.");
        // The local optimization of each trial: the algorithm and cycles given; the search sets the rest.
        var trialLocal = new LocalOptimizationOptions { Algorithm = local.Algorithm, Cycles = local.Cycles, CycleCount = local.CycleCount };
        if (local.Cycles == LocalOptimizationCycles.Infinite) Log("Cycles: infinite runs as automatic inside a search (each trial's local optimization must end).");
        double? curvatureLimit = o.ContainsKey("curvature-limit") ? Number(o, "curvature-limit", 0) : null;
        TimeSpan? timeLimit = null;
        if (o.ContainsKey("time-limit"))
        {
            double seconds = Number(o, "time-limit", 0);
            if (!(seconds > 0) || !double.IsFinite(seconds)) throw new UsageException($"--time-limit needs a positive number of seconds, not '{o["time-limit"]}'.");
            timeLimit = TimeSpan.FromSeconds(seconds);
        }
        int? threads = o.ContainsKey("threads") ? Whole(o, "threads", 1, 1) : null;
        void Progress(GlobalSearchProgress p) => stdout.WriteLine(
            $"  {(p.Kind == GlobalSearchKind.Global ? "generation" : "round")} {p.Iteration}: "
            + (p.Best.Count > 0 ? $"best merit {StudyReport.Number(p.Best[0].Merit, "g8")}" : "no design yet")
            + $" (start {StudyReport.Number(p.StartMerit, "g8")}), {p.Trials} trials, {p.FailedTrials} failed");

        GlobalSearchResult result;
        try
        {
            if (hammer)
            {
                var d = new HammerSearchOptions();
                result = HammerSearch.Run(optics, active, new HammerSearchOptions
                {
                    Local = trialLocal,
                    MaxEvaluationsPerTrial = Whole(o, "max-evaluations", d.MaxEvaluationsPerTrial, 1),
                    CurvatureLimit = curvatureLimit,
                    StepFraction = Number(o, "step", d.StepFraction),
                    TrialsPerRound = Whole(o, "trials", d.TrialsPerRound, 1),
                    Rounds = Whole(o, "rounds", d.Rounds, 0),
                    GlassChangeProbability = Number(o, "glass-probability", d.GlassChangeProbability),
                    Seed = Seed64(o, d.Seed),
                    SaveCount = Whole(o, "save", d.SaveCount, 1),
                    TimeLimit = timeLimit, MaxDegreeOfParallelism = threads, CancellationToken = cancellation, Progress = Progress,
                });
            }
            else
            {
                var d = new GlobalSearchOptions();
                result = GlobalSearch.Run(optics, active, new GlobalSearchOptions
                {
                    Local = trialLocal,
                    MaxEvaluationsPerTrial = Whole(o, "max-evaluations", d.MaxEvaluationsPerTrial, 1),
                    CurvatureLimit = curvatureLimit,
                    PopulationSize = o.ContainsKey("population") ? Whole(o, "population", 0, 1) : d.PopulationSize,
                    Generations = Whole(o, "generations", d.Generations, 0),
                    Seed = Seed64(o, d.Seed),
                    SaveCount = Whole(o, "save", d.SaveCount, 1),
                    TimeLimit = timeLimit, MaxDegreeOfParallelism = threads, CancellationToken = cancellation, Progress = Progress,
                });
            }
        }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message, ex); }
        foreach (var line in result.Log) Log(line);

        string dir = OutDir(projectPath, o, hammer ? "-optics-hammer" : "-optics-global");
        var report = SearchReport(project.Name, result);
        File.WriteAllText(Path.Combine(dir, "report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "report.html"), report.ToHtml());
        File.WriteAllText(Path.Combine(dir, "designs.csv"), DesignsCsv(result));
        int code = 0;
        if (result.Best.Count > 0)
        {
            var saved = new ProjectSerializer().Load(projectPath);
            saved.Optics = result.ApplyTo(saved.Optics ?? optics, result.Best[0]);
            if (o.ContainsKey("high-yield")) saved.Optics = saved.Optics with { MeritFunction = optics.MeritFunction };   // OP-62: the rows the search used
            string optimisedPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(projectPath) + "-optimised" + ProjectSerializer.FileExtension);
            new ProjectSerializer().Save(saved, optimisedPath);
            Log($"The best design (merit {StudyReport.Number(result.Best[0].Merit, "g10")}, trial {result.Best[0].Trial}) written to {optimisedPath} (the input project is unchanged).");
        }
        else
        {
            Log(result.Cancelled ? "Stopped before any trial finished: no design to write." : "No trial produced a design that could be evaluated; see the log. No project written.");
            code = 2;
        }
        File.WriteAllText(Path.Combine(dir, "log.txt"), Join(log));
        stdout.WriteLine($"Results written to {dir}");
        if (result.Cancelled)
        {
            stdout.WriteLine("Cancelled: the designs found so far are written.");
            return 2;
        }
        return code;
    }

    /// <summary>The Global or Hammer search report: the search, merit before and the best, the best
    /// designs, each variable's range, start and best value, and the search's log.</summary>
    public static StudyReport SearchReport(string projectName, GlobalSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string N(double v) => StudyReport.Number(v, "g10");
        var ci = CultureInfo.InvariantCulture;
        bool global = result.Kind == GlobalSearchKind.Global;
        var start = LensVariables.Values(result.Start, result.Variables);
        var best = result.Best.Count > 0 ? result.Best[0] : null;
        var results = new List<(string, string)>
        {
            ("Merit function of the start", N(result.StartMerit)),
            ("Best merit function", best is null ? "no design" : N(best.Merit) + (best.ConstraintsMet ? "" : " (exact constraints not met)")),
            ("Stopped", result.Stop.ToString()),
            (global ? "Generations run" : "Rounds run", result.Iterations.ToString(ci)),
            ("Trials", result.Trials.ToString(ci)),
            ("Failed trials", result.FailedTrials.ToString(ci)),
        };
        return new StudyReport
        {
            Title = $"{projectName} — {(global ? "Global" : "Hammer")} search",
            Inputs = new List<(string, string)>
            {
                ("Search", global ? "Global: differential evolution of locally optimized designs" : "Hammer: monotonic basin hopping with glass substitution"),
                ("Seed", result.Seed.ToString(ci)),
                ("Variables", result.Variables.Count.ToString(ci)),
                ("Surfaces to substitute", result.SubstituteSurfaces.Count == 0 ? "none" : string.Join(", ", result.SubstituteSurfaces.Select(j => j.ToString(ci)))),
            },
            Results = results,
            Sections = new[]
            {
                new ReportSection("Best designs")
                {
                    TableHeaders = DesignHeaders(result),
                    TableRows = result.Best.Select((d, k) => (IReadOnlyList<string>)DesignCells(d, k, N).ToArray()).ToList()
                },
                new ReportSection("Variables")
                {
                    TableHeaders = new[] { "Variable", "Range from", "Range to", "Start", "Best" },
                    TableRows = result.Variables.Select((v, i) => (IReadOnlyList<string>)new[]
                    {
                        v.Label, N(result.Ranges[i].Lower), N(result.Ranges[i].Upper), N(start[i]), best is null ? "—" : N(best.Values[i])
                    }).ToList()
                },
                new ReportSection("Search log") { Lines = result.Log },
            },
            Assumptions = new[]
            {
                $"{(global ? "Global" : "Hammer")} search (OP-58, docs/Optics-Conventions.md §43) of the saved variables against the saved merit function, every trial locally optimized (OP-57, §35); this program's own algorithm, not OpticStudio's (which is unpublished).",
                "A run is a fixed function of the seed and the settings, at any thread count; a time limit is checked between generations or rounds.",
                "A design that meets the exact (negative-weight) constraints ranks above one that does not, then the lower merit, then the earlier trial.",
                "Values are in saved units; a radius is varied and listed as its curvature (1/lens unit). The ranges bound only the random starts or steps.",
            }
        };
    }

    private static IReadOnlyList<string> DesignHeaders(GlobalSearchResult r) =>
        new[] { "Rank", "Trial", r.Kind == GlobalSearchKind.Global ? "Generation" : "Round", "Merit", "Constraints met" }
            .Concat(r.Variables.Select(v => v.Label))
            .Concat(r.SubstituteSurfaces.Select(j => $"Surface {j.ToString(CultureInfo.InvariantCulture)} glass")).ToArray();

    private static IEnumerable<string> DesignCells(GlobalDesign d, int rank, Func<double, string> number) =>
        new[] { (rank + 1).ToString(CultureInfo.InvariantCulture), d.Trial.ToString(CultureInfo.InvariantCulture), d.Iteration.ToString(CultureInfo.InvariantCulture),
                number(d.Merit), d.ConstraintsMet ? "yes" : "no" }
            .Concat(d.Values.Select(number)).Concat(d.Glasses);

    private static string DesignsCsv(GlobalSearchResult r) =>
        PlotCsv.Table(DesignHeaders(r).Select(h => h.ToLowerInvariant()).ToArray(),
            r.Best.Select((d, k) => (IReadOnlyList<string>)DesignCells(d, k, PlotCsv.Number).ToArray()),
            new[]
            {
                $"{r.Kind.ToString().ToLowerInvariant()} search, seed {r.Seed.ToString(CultureInfo.InvariantCulture)}, start merit {PlotCsv.Number(r.StartMerit)}",
                "Values in saved units; a radius as its curvature (1/lens unit)."
            });

    // ---------------- tolerance --montecarlo (OP-65) ----------------

    /// <summary>
    /// Monte Carlo tolerancing of the saved rows with <paramref name="run"/> (the saved run settings
    /// and the command line's criterion, compensation and threads) and the project's saved Monte Carlo
    /// settings, overridden by --trials, --seed and --yield-limit. Writes report.md/.html,
    /// montecarlo.txt (the editor's report text), trials.csv, histogram.csv, log.txt and, with
    /// --save-trial k, trial k's lens as MC_T000k.osslens. Exit 2 when a trial failed (counted and
    /// named in the report) or the trial lens could not be saved.
    /// </summary>
    private static int OpticsMonteCarlo(string projectPath, Dictionary<string, string> o, TextWriter stdout, SimProject project,
        OpticsSettings optics, ToleranceTable table, IReadOnlyList<OpticalCatalog> active, ToleranceRunOptions run,
        List<string> log, Action<string> Log)
    {
        var notes = new List<string>();
        var saved = MonteCarloSettings.FromSettings(optics.Tolerances?.MonteCarlo, notes, out _, out _);
        foreach (var n in notes) Log(n);
        var mc = saved with
        {
            Trials = Whole(o, "trials", saved.Trials, 1),
            Seed = o.ContainsKey("seed") ? Whole(o, "seed", 0, int.MinValue) : saved.Seed,
            YieldLimit = o.ContainsKey("yield-limit") ? Number(o, "yield-limit", 0) : saved.YieldLimit,
        };
        int? saveTrial = o.ContainsKey("save-trial") ? Whole(o, "save-trial", 1, 1) : null;
        if (saveTrial > mc.Trials) throw new UsageException($"--save-trial {saveTrial} names a trial the run does not have (trials 1 to {mc.Trials}).");
        var gate = new object();
        mc = mc with
        {
            Progress = p => { lock (gate) stdout.WriteLine($"  {p.TrialsDone} of {p.TrialCount} trials, {p.Failed} failed"); },
        };
        run = run with { Progress = null };

        MonteCarloResult result;
        try { result = MonteCarloAnalysis.Run(optics, table, active, run, mc); }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message, ex); }

        string dir = OutDir(projectPath, o, "-optics-montecarlo");
        var report = MonteCarloReport(project.Name, result);
        File.WriteAllText(Path.Combine(dir, "report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(dir, "report.html"), report.ToHtml());
        // The Tolerance Data Editor's Monte Carlo report text (OpticsToleranceEditorViewModel.ShowMonteCarlo).
        File.WriteAllText(Path.Combine(dir, "montecarlo.txt"), Join(result.ReportLines()));
        File.WriteAllText(Path.Combine(dir, "trials.csv"), TrialsCsv(result));
        if (result.HistogramPlot() is { } histogram) File.WriteAllText(Path.Combine(dir, "histogram.csv"), AnalysisPlotCsv.Write(histogram));
        if (result.MtfOverlayPlot() is { } overlay) File.WriteAllText(Path.Combine(dir, "mtf-overlay.csv"), AnalysisPlotCsv.Write(overlay));
        foreach (var line in result.ReportLines().Take(2)) Log(line);
        Log($"Evaluated {result.Evaluated} of {result.Trials.Count} trials; failed and left out of the statistics: {result.Failed}.");
        foreach (var t in result.Trials.Where(t => !t.Succeeded)) Log($"Trial {t.Index} failed: {t.Failure}");
        int code = result.Failed == 0 ? 0 : 2;
        if (saveTrial is int k)
        {
            try
            {
                var lens = MonteCarloAnalysis.TrialLens(optics, table, active, run with { CancellationToken = default },
                    mc with { Progress = null, Seed = result.Seed }, k);
                string path = Path.Combine(dir, $"MC_T{k.ToString("0000", CultureInfo.InvariantCulture)}{OpticsFileSerializer.FileExtension}");
                new OpticsFileSerializer().Save(new OpticsLensFile { Name = $"Monte Carlo trial {k.ToString(CultureInfo.InvariantCulture)}", Optics = lens }, path);
                Log($"Trial {k} lens written to {path}.");
            }
            catch (ArgumentException ex)
            {
                Log($"Trial {k} lens not written: {ex.Message}");
                code = 2;
            }
        }
        File.WriteAllText(Path.Combine(dir, "log.txt"), Join(log));
        stdout.WriteLine($"Results written to {dir}");
        return code;
    }

    /// <summary>The Monte Carlo report: the run's settings, the nominal and the statistics, the yield,
    /// every trial (a failed one with its reason), the notes and the assumptions.</summary>
    public static StudyReport MonteCarloReport(string projectName, MonteCarloResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string V(double v) => double.IsNaN(v) ? "—" : StudyReport.Number(v, "g6");
        var ci = CultureInfo.InvariantCulture;
        string u = result.Unit.Length == 0 ? "" : " " + result.Unit;
        var info = ToleranceCriteria.Info(result.RunOptions.Criterion);
        var results = new List<(string, string)>
        {
            ("Nominal criterion", V(result.Nominal) + u),
            ("Trials evaluated", $"{result.Evaluated} of {result.Trials.Count}"),
            ("Trials failed (left out)", result.Failed.ToString(ci)),
        };
        if (result.Statistics is { } s)
        {
            results.Add(("Mean", V(s.Mean) + u));
            results.Add(("Standard deviation", V(s.StandardDeviation) + u));
            results.Add(("Min", V(s.Min) + u));
            results.Add(("Max", V(s.Max) + u));
            foreach (var p in s.Percentiles) results.Add(($"{StudyReport.Number(p.Percent, "g4")} % at or below", V(p.Value) + u));
            if (result.Best is int b) results.Add(("Best trial", b.ToString(ci)));
            if (result.Worst is int w) results.Add(("Worst trial", w.ToString(ci)));
        }
        if (result.Yield is { } y)
            results.Add(($"Yield ({(result.HigherIsBetter ? "≥" : "≤")} {V(y.Limit)}{u})", $"{StudyReport.Number(100 * y.Fraction, "g4")} % ({y.Passed} of {y.Evaluated})"));
        var sections = new List<ReportSection>
        {
            new("Trials")
            {
                TableHeaders = new[] { "Trial", "Criterion", "Change" }.Concat(result.Compensators).Append("Note").ToArray(),
                TableRows = result.Trials.Select(t => (IReadOnlyList<string>)new[] { t.Index.ToString(ci), V(t.Criterion), V(t.Change) }
                    .Concat(t.Succeeded ? t.Compensators.Select(V) : result.Compensators.Select(_ => "—"))
                    .Append(t.Failure is { } f ? $"failed: {f}" : t.MtfFailure is { } m ? $"MTF: {m}" : t.Index == result.Best ? "best" : t.Index == result.Worst ? "worst" : "")
                    .ToArray()).ToList()
            },
        };
        if (result.Notes.Count > 0) sections.Add(new ReportSection("Notes") { Lines = result.Notes });
        return new StudyReport
        {
            Title = $"{projectName} — Monte Carlo tolerancing",
            Inputs = new List<(string, string)>
            {
                ("Criterion", info.Name + (result.Unit.Length == 0 ? "" : $" ({result.Unit})")),
                ("Compensation", result.RunOptions.Compensation.ToString()),
                ("Compensators", result.Compensators.Count == 0 ? "none" : string.Join("; ", result.Compensators)),
                ("Trials", result.Options.Trials.ToString(ci)),
                ("Seed", result.Seed.ToString(ci)),
                ("Statistics (rows no STAT row governs)", result.Options.Statistics.ToString()
                    + (result.Options.Statistics == ToleranceStatistics.Normal ? $", {result.Options.StandardDeviations} standard deviations" : "")),
                ("Yield limit", result.Options.YieldLimit is double l ? V(l) + u : "none"),
            },
            Results = results,
            Sections = sections,
            Assumptions = result.Assumptions,
        };
    }

    private static string TrialsCsv(MonteCarloResult r)
    {
        var first = r.Trials.Count > 0 ? r.Trials[0].Draws : Array.Empty<MonteCarloDraw>();
        var header = new List<string> { "trial", "criterion", "change" };
        header.AddRange(r.Compensators.Select(c => "compensator " + c));
        header.Add("failure");
        foreach (var d in first)
        {
            string row = $"row {d.Row.ToString(CultureInfo.InvariantCulture)} {d.Code}";
            header.Add(row);
            if (d.DirectionDegrees is not null) header.Add(row + " direction (deg)");
            if (d.AngleDegrees is not null) header.Add(row + " angle (deg)");
        }
        return PlotCsv.Table(header, r.Trials.Select(t =>
        {
            var cells = new List<string> { t.Index.ToString(CultureInfo.InvariantCulture), PlotCsv.Number(t.Criterion), PlotCsv.Number(t.Change) };
            cells.AddRange(t.Succeeded ? t.Compensators.Select(PlotCsv.Number) : r.Compensators.Select(_ => PlotCsv.Number(double.NaN)));
            cells.Add(t.Failure ?? "");
            foreach (var d in t.Draws)
            {
                cells.Add(PlotCsv.Number(d.Value));
                if (d.DirectionDegrees is double dir) cells.Add(PlotCsv.Number(dir));
                if (d.AngleDegrees is double a) cells.Add(PlotCsv.Number(a));
            }
            return (IReadOnlyList<string>)cells;
        }), new[] { $"monte carlo, seed {r.Seed.ToString(CultureInfo.InvariantCulture)}, nominal {PlotCsv.Number(r.Nominal)} {r.Unit}".TrimEnd() });
    }
}
