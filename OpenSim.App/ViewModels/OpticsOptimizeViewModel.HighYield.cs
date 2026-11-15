using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Merit;
using OpenSim.Optics.Optimization.HighYield;

namespace OpenSim.App.ViewModels;

// OP-62: High-Yield optimization in the Optimize window.

/// <summary>One surface's High-Yield term in the Optimize window's list.</summary>
public sealed record HighYieldSurfaceRow(string Configuration, int Surface, int Rows, string MeanTau, string MaxTau, string Share);

public sealed partial class OpticsOptimizeViewModel
{
    private bool _loadingHighYield;

    /// <summary>Writes a merit function into the workspace's Merit Function Editor as one undo
    /// step named by the text; returns why it was refused, or null. Null: the window cannot
    /// write rows (Add and Remove are disabled).</summary>
    public Func<MeritFunction, string, string?>? ApplyMerit { get; init; }

    /// <summary>The weight of the HYLD rows relative to the default merit function's rows
    /// (help, "Optimization Goal", "Weight").</summary>
    [ObservableProperty] private double _highYieldWeight = HighYieldMerit.DefaultWeight;

    [ObservableProperty] private string _highYieldText = "Add High-Yield rows writes HYLD rows for every refracting surface at the end of the merit function.";

    /// <summary>The High-Yield term surface by surface, from the last evaluation.</summary>
    public ObservableCollection<HighYieldSurfaceRow> HighYieldSurfaces { get; } = new();

    /// <summary>The last report, or null.</summary>
    public HighYieldReport? LastHighYieldReport { get; private set; }

    /// <summary>The settings as saved; null when they are the defaults.</summary>
    public OpticsHighYieldSettings? HighYieldToSettings() =>
        HighYieldWeight == HighYieldMerit.DefaultWeight ? null : new OpticsHighYieldSettings { Weight = HighYieldWeight };

    /// <summary>Shows saved settings; null is the defaults.</summary>
    public void LoadHighYield(OpticsHighYieldSettings? saved)
    {
        _loadingHighYield = true;
        try { HighYieldWeight = saved?.Weight ?? HighYieldMerit.DefaultWeight; }
        finally { _loadingHighYield = false; }
    }

    private bool CanEditHighYield() => !IsRunning && ApplyMerit is not null;

    /// <summary>Writes the HYLD rows (replacing an earlier High-Yield block) at the end of the
    /// project's merit function, from the Optimization Wizard's saved options, then evaluates them.</summary>
    [RelayCommand(CanExecute = nameof(CanEditHighYield))]
    private void AddHighYieldRows()
    {
        if (ApplyMerit is null) return;
        try
        {
            var system = _system();
            var catalogs = _catalogs();
            var registry = MeritOperandRegistry.CreateDefault();
            var notes = new List<string>();
            var wizard = DefaultMeritWizardSettings.FromSettings(system.MeritWizard, notes);
            var configurations = new ProjectMeritConfigurations(system, catalogs);
            int current = Math.Clamp(system.MultiConfiguration?.CurrentConfiguration ?? 1, 1, configurations.Count);
            var rows = HighYieldMerit.Generate(configurations, current, wizard, HighYieldWeight);
            var existing = MeritFunctionSettings.FromSettings(system.MeritFunction, registry, notes);
            var function = HighYieldMerit.WithRows(existing, rows.Rows, registry);
            if (ApplyMerit(function, "High-Yield rows") is { } refusal) { HighYieldText = $"Not added: {refusal}"; Log.Add(HighYieldText); return; }
            foreach (var n in notes.Concat(rows.Notes)) Log.Add(n);
            Log.Add(rows.Summary);
            EvaluateHighYield();
            HighYieldText = $"{rows.OperandCount} HYLD rows written. {HighYieldText}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            HighYieldText = ex.Message;
            Log.Add(ex.Message);
        }
    }

    /// <summary>Removes the High-Yield block from the project's merit function.</summary>
    [RelayCommand(CanExecute = nameof(CanEditHighYield))]
    private void RemoveHighYieldRows()
    {
        if (ApplyMerit is null) return;
        try
        {
            var system = _system();
            var registry = MeritOperandRegistry.CreateDefault();
            var existing = MeritFunctionSettings.FromSettings(system.MeritFunction, registry, new List<string>());
            var without = HighYieldMerit.Without(existing, registry);
            if (without.Rows.Count == existing.Rows.Count) { HighYieldText = "The merit function has no High-Yield rows."; return; }
            if (ApplyMerit(without, "Remove High-Yield rows") is { } refusal) { HighYieldText = $"Not removed: {refusal}"; Log.Add(HighYieldText); return; }
            HighYieldSurfaces.Clear();
            LastHighYieldReport = null;
            HighYieldText = $"{existing.Rows.Count - without.Rows.Count} High-Yield rows removed.";
            Log.Add(HighYieldText);
        }
        catch (ArgumentException ex)
        {
            HighYieldText = ex.Message;
            Log.Add(ex.Message);
        }
    }

    /// <summary>Evaluates the project's merit function and lists the HYLD rows surface by surface.</summary>
    [RelayCommand]
    private void EvaluateHighYield()
    {
        HighYieldSurfaces.Clear();
        LastHighYieldReport = null;
        try
        {
            var system = _system();
            var registry = MeritOperandRegistry.CreateDefault();
            var function = MeritFunctionSettings.FromSettings(system.MeritFunction, registry, new List<string>());
            var evaluation = MeritEvaluator.Evaluate(function, new ProjectMeritConfigurations(system, _catalogs()), registry);
            var report = HighYieldMerit.Report(evaluation);
            LastHighYieldReport = report;
            bool many = report.Surfaces.Select(s => s.Configuration).Distinct().Count() > 1;
            foreach (var s in report.Surfaces)
                HighYieldSurfaces.Add(new HighYieldSurfaceRow(many ? s.Configuration.ToString(CultureInfo.InvariantCulture) : "", s.Surface, s.Rows,
                    G(s.MeanTau), G(s.MaxTau), s.Contribution.ToString("F2", CultureInfo.InvariantCulture) + " %"));
            foreach (var f in report.Failures) Log.Add(f);
            HighYieldText = report.Surfaces.Count == 0 && report.Failures.Count == 0
                ? "The merit function has no HYLD rows."
                : $"HYLD rows are {report.Contribution.ToString("F2", CultureInfo.InvariantCulture)} % of the merit function ({G(evaluation.Value)})"
                  + (report.Failures.Count > 0 ? $"; {report.Failures.Count} failed (see the log)." : ".");
        }
        catch (ArgumentException ex)
        {
            HighYieldText = ex.Message;
            Log.Add(ex.Message);
        }
    }

    private static string G(double x) => x.ToString("G6", CultureInfo.InvariantCulture);
}
