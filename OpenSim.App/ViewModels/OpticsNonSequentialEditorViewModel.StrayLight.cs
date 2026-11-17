using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Analysis;
using OpenSim.Optics.NonSequential.StrayLight;

namespace OpenSim.App.ViewModels;

// OP-84: the project's path filters (_1, _2, ...) and the Sequence Selector's sequences
// (docs/Optics-Conventions.md §78). The boxes show the saved lists; Apply writes them back and
// publishes; Generate runs Path Analysis or the Sequence Generator on the project (a ZRD file, or
// the project's sources traced now) and saves the result. None of these is a step on the sheet's
// undo stack.
public sealed partial class OpticsNonSequentialEditorViewModel
{
    /// <summary>The project as analyses see it, for the Generate commands; the workspace sets it.
    /// Null: the Generate commands refuse, saying so.</summary>
    public Func<OpticsAnalysisContext?>? AnalysisContext { get; set; }

    /// <summary>One path per line; line k is the filter _k ("1.0, 2.1, 2.2, 4.0").</summary>
    [ObservableProperty] private string _pathFiltersText = "";

    /// <summary>One sequence per line, "name; sequence"; a leading "-" turns its check box off.</summary>
    [ObservableProperty] private string _sequencesText = "";

    /// <summary>Generate's ray database: the full path of a ZRD file, blank to trace the sources now.</summary>
    [ObservableProperty] private string _strayLightRayDatabase = "";

    /// <summary>Generate path filters: 0 groups every branch, n the hits on detector n.</summary>
    [ObservableProperty] private int _pathAnalysisDetector;

    /// <summary>Generate sequences: the Sequence Generator's method.</summary>
    [ObservableProperty] private NscSequenceMethod _sequenceMethod = NscSequenceMethod.StraightThroughPaths;

    /// <summary>The methods for the picker.</summary>
    public IReadOnlyList<NscSequenceMethod> SequenceMethods { get; } = Enum.GetValues<NscSequenceMethod>();

    private void LoadStrayLightFields()
    {
        try
        {
            PathFiltersText = NscSequenceNames.FormatPathFilters(NscSequenceNames.PathFilters(_settings));
            SequencesText = NscSequenceNames.Format(NscSequenceNames.Sequences(_settings));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            // Keep the saved text as it is, so nothing is lost; the problem is reported.
            PathFiltersText = string.Join(Environment.NewLine, _settings?.PathFilters ?? new List<string>());
            SequencesText = string.Join(Environment.NewLine, (_settings?.Sequences ?? new List<OpticsNscSequenceSettings>())
                .Select(s => $"{(s.Enabled == false ? "-" : "")}{s.Name}; {s.Sequence}"));
            Status = $"The saved path filters or sequences have a problem: {e.Message}";
        }
    }

    /// <summary>Parses <see cref="PathFiltersText"/> into the project's path filters and publishes.</summary>
    [RelayCommand]
    private void ApplyPathFilters() => Status = ApplyPathFiltersText() ?? "Path filters applied.";

    /// <summary>As <see cref="ApplyPathFiltersCommand"/>; returns why not, or null.</summary>
    public string? ApplyPathFiltersText()
    {
        List<NscPathSequence> paths;
        try { paths = NscSequenceNames.ParsePathFilters(PathFiltersText); }
        catch (FormatException e) { return e.Message; }
        SetStrayLight(paths, null);
        return null;
    }

    /// <summary>Parses <see cref="SequencesText"/> into the project's sequences and publishes.</summary>
    [RelayCommand]
    private void ApplySequences() => Status = ApplySequencesText() ?? "Sequences applied.";

    /// <summary>As <see cref="ApplySequencesCommand"/>; returns why not, or null.</summary>
    public string? ApplySequencesText()
    {
        List<NscSequence> sequences;
        try { sequences = NscSequenceNames.ParseText(SequencesText); }
        catch (Exception e) when (e is FormatException or ArgumentException) { return e.Message; }
        var named = (_settings?.NamedFilters ?? new List<string>()).Select(l => l.Split(';')[0].Trim());
        try { NscSequenceNames.Validate(sequences, named); }
        catch (ArgumentException e) { return e.Message; }
        SetStrayLight(null, sequences);
        return null;
    }

    /// <summary>Runs Path Analysis (Generate Path Filters) and saves its paths as _1, _2, ....</summary>
    [RelayCommand]
    private void GeneratePathFilters() => Status = GeneratePathFiltersNow();

    /// <summary>As <see cref="GeneratePathFiltersCommand"/>; returns what happened or why not.</summary>
    public string GeneratePathFiltersNow()
    {
        if (AnalysisContext?.Invoke() is not { } context) return "Generate path filters needs the project; it is not available here.";
        try
        {
            var (result, _, _, _) = NscPathAnalysisAnalysis.Run(context, new NscPathAnalysisSettings
            {
                File = StrayLightRayDatabase.Trim(), Detector = PathAnalysisDetector, GeneratePathFilters = true,
            });
            var paths = result.Paths.Select(p => p.Sequence).ToList();
            SetStrayLight(paths, null);
            PathFiltersText = NscSequenceNames.FormatPathFilters(paths);
            return $"{paths.Count} path filter(s) saved (_1 to _{paths.Count}).";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or IOException or InvalidDataException or FormatException)
        {
            return e.Message;
        }
    }

    /// <summary>Runs the Sequence Generator with <see cref="SequenceMethod"/> and saves the list.</summary>
    [RelayCommand]
    private void GenerateSequences() => Status = GenerateSequencesNow();

    /// <summary>As <see cref="GenerateSequencesCommand"/>; returns what happened or why not.</summary>
    public string GenerateSequencesNow()
    {
        if (AnalysisContext?.Invoke() is not { } context) return "Generate sequences needs the project; it is not available here.";
        try
        {
            var (list, added, skipped, _) = NscSequenceSelectorAnalysis.Generate(context, new NscSequenceSelectorSettings
            {
                Method = SequenceMethod, File = StrayLightRayDatabase.Trim(),
            });
            SetStrayLight(null, list);
            SequencesText = NscSequenceNames.Format(list);
            return $"{added} sequence(s) generated ({skipped} duplicate(s) skipped); {list.Count} in the list.";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or IOException or InvalidDataException or FormatException)
        {
            return e.Message;
        }
    }

    private void SetStrayLight(IReadOnlyList<NscPathSequence>? paths, IReadOnlyList<NscSequence>? sequences)
    {
        var s = _settings ?? new OpticsNonSequentialSettings();
        if (paths is not null) s = s with { PathFilters = NscSequenceNames.ToSettings(paths) };
        if (sequences is not null) s = s with { Sequences = NscSequenceNames.ToSettings(sequences) };
        _settings = s;
        AfterChange(publish: true);
    }
}
