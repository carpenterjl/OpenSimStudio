using System.Globalization;
using OpenSim.Core.Model;
using OpenSim.Core.Plotting;
using OpenSim.Core.PostProcessing;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization.HighYield;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.Cli;

// Wave 9 wiring: the command-line arms of OP-62 (optics optimise --high-yield <weight>) and OP-66
// (optics tolerance --wizard, --quick-yield) (docs/Optics-Conventions.md §46). The engines are
// called as the app's Optimize window and Tolerance Data Editor call them; this file only reads
// the options, writes the results folder and sets the exit code.

public static partial class CliRunner
{
    /// <summary>
    /// <c>--high-yield &lt;weight&gt;</c>: the project's merit function with its High-Yield block
    /// (if any) replaced by OP-62's rows at the end, generated from the saved Optimization Wizard
    /// options and the current configuration, as the Optimize window's Add High-Yield Rows does.
    /// </summary>
    private static OpticsSettings WithHighYield(OpticsSettings optics, IReadOnlyList<OpticalCatalog> active, Dictionary<string, string> o, Action<string> log)
    {
        if (o["high-yield"] == "true") throw new UsageException("--high-yield needs the High-Yield weight (a positive number), for example --high-yield 1.");
        double weight = Number(o, "high-yield", HighYieldMerit.DefaultWeight);
        var notes = new List<string>();
        var registry = MeritOperandRegistry.CreateDefault();
        var wizard = DefaultMeritWizardSettings.FromSettings(optics.MeritWizard, notes);
        var configurations = new ProjectMeritConfigurations(optics, active);
        int current = Math.Clamp(optics.MultiConfiguration?.CurrentConfiguration ?? 1, 1, configurations.Count);
        HighYieldRows rows;
        try { rows = HighYieldMerit.Generate(configurations, current, wizard, weight); }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message, ex); }
        var existing = MeritFunctionSettings.FromSettings(optics.MeritFunction, registry, notes);
        var function = HighYieldMerit.WithRows(existing, rows.Rows, registry);
        foreach (var n in notes.Concat(rows.Notes)) log(n);
        log($"High-Yield (weight {StudyReport.Number(weight, "g6")}): {rows.Summary}");
        return optics with { MeritFunction = MeritFunctionSettings.ToSettings(function) };
    }

    /// <summary><c>--wizard</c>: the saved tolerance rows with OP-66's Tolerance Wizard rows written
    /// from its saved settings (from its Start At Row on), as the editor's Wizard button does.</summary>
    private static ToleranceTable WizardTable(OpticsSettings optics, ToleranceTable table, IReadOnlyList<OpticalCatalog> active, Action<string> log)
    {
        var notes = new List<string>();
        var options = ToleranceWizardSettings.FromSettings(optics.Tolerances?.Wizard, notes);
        ToleranceWizardResult result;
        try { result = ToleranceWizard.Generate(optics, table, active, options); }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message, ex); }
        foreach (var n in notes.Concat(result.Notes)) log("Tolerance Wizard: " + n);
        log($"Tolerance Wizard: {result.RowCount} rows written from row {result.FirstRow}; the run uses them (the project is unchanged).");
        return result.Table;
    }

    /// <summary><c>--quick-yield</c>: OP-66's Quick Yield with the run options the sensitivity run
    /// would use, the saved Monte Carlo settings (statistics, seed, yield limit; <c>--seed</c> and
    /// <c>--yield-limit</c> override them) and the saved Quick Yield settings (<c>--samples</c>
    /// overrides the sample count).</summary>
    private static int OpticsQuickYield(string projectPath, Dictionary<string, string> o, TextWriter stdout, SimProject project,
        OpticsSettings optics, ToleranceTable table, IReadOnlyList<OpticalCatalog> active, ToleranceRunOptions run,
        List<string> log, Action<string> Log)
    {
        var notes = new List<string>();
        var savedMc = MonteCarloSettings.FromSettings(optics.Tolerances?.MonteCarlo, notes, out _, out _);
        foreach (var n in notes) Log(n);
        var mc = savedMc with
        {
            Seed = o.ContainsKey("seed") ? Whole(o, "seed", 0, int.MinValue) : savedMc.Seed,
            YieldLimit = o.ContainsKey("yield-limit") ? Number(o, "yield-limit", 0) : savedMc.YieldLimit,
            Progress = null,
        };
        var saved = QuickYieldSettings.FromSettings(optics.Tolerances?.QuickYield);
        var gate = new object();
        var options = saved with
        {
            Samples = Whole(o, "samples", saved.Samples, 1),
            Progress = p => { lock (gate) stdout.WriteLine($"  {p.EvaluationsDone} of {p.EvaluationCount} lens evaluations"); },
        };
        run = run with { Progress = null };

        QuickYieldResult result;
        try { result = QuickYield.Run(optics, table, active, run, mc, options); }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message, ex); }

        string dir = OutDir(projectPath, o, "-optics-quickyield");
        // The Tolerance Data Editor's Quick Yield report text.
        File.WriteAllText(Path.Combine(dir, "quickyield.txt"), Join(result.ReportLines()));
        if (result.HistogramPlot() is { } histogram) File.WriteAllText(Path.Combine(dir, "histogram.csv"), AnalysisPlotCsv.Write(histogram));
        if (result.YieldPlot() is { } yield) File.WriteAllText(Path.Combine(dir, "yield.csv"), AnalysisPlotCsv.Write(yield));
        foreach (var line in result.ReportLines().Take(2)) Log(line);
        foreach (var f in result.Failures) Log("Failed: " + f);
        File.WriteAllText(Path.Combine(dir, "log.txt"), Join(log));
        stdout.WriteLine($"Results written to {dir}");
        return result.Succeeded ? 0 : 2;
    }
}
