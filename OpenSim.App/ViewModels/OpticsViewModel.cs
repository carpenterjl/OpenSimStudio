using CommunityToolkit.Mvvm.ComponentModel;
using OpenSim.Core.Model;
using OpenSim.Optics;

namespace OpenSim.App.ViewModels;

/// <summary>An optics mode as the picker shows it.</summary>
public sealed record OpticsModeOption(string Label, OpticsMode Mode)
{
    public static readonly IReadOnlyList<OpticsModeOption> All =
        OpticsModes.All.Select(m => new OpticsModeOption(OpticsModes.Label(m), m)).ToArray();
}

/// <summary>
/// The Optics workspace (plan item OP-1). So far it holds the one choice the workspace has —
/// sequential or non-sequential — and carries it to and from the project file. The system
/// editors, the ray trace and the analyses are later plan items; like the antenna study,
/// optics will keep its own result records rather than go through the FE solver path.
/// </summary>
public partial class OpticsViewModel : ObservableObject
{
    public IReadOnlyList<OpticsModeOption> Modes => OpticsModeOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mode))]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    private OpticsModeOption _selectedMode = OpticsModeOption.All[0];

    partial void OnSelectedModeChanged(OpticsModeOption value)
    {
        // A picker pushes a transient null while its list rebuilds; the mode is never unset.
        if (value is null) SelectedMode = OpticsModeOption.All[0];
    }

    public OpticsMode Mode => SelectedMode.Mode;

    /// <summary>What the selected mode means, for the panel.</summary>
    public string ModeDescription => Mode switch
    {
        OpticsMode.Sequential =>
            "Surfaces in a fixed order: every ray meets surface 1, then 2, and so on. For lens design.",
        OpticsMode.NonSequential =>
            "Objects in space with no set order: a ray meets whatever lies in its path. For illumination and stray light.",
        _ => ""
    };

    /// <summary>The workspace's inputs as the project file stores them.</summary>
    public OpticsSettings ToSettings() => new() { Mode = OpticsModes.SavedName(Mode) };

    /// <summary>Restores the workspace from a project file; null (every older file) leaves it
    /// as it is. Returns notes for the log: a mode this build does not know is kept out and
    /// named, never opened as another mode without a word.</summary>
    public IReadOnlyList<string> LoadSettings(OpticsSettings? saved)
    {
        var notes = new List<string>();
        if (saved?.Mode is not { } name) return notes;
        if (OpticsModes.TryParseSavedName(name, out var mode))
            SelectedMode = OpticsModeOption.All.First(o => o.Mode == mode);
        else
            notes.Add($"Optics mode '{name}' is not one this version knows; kept '{SelectedMode.Label}'.");
        return notes;
    }

    /// <summary>A new project starts in the default mode.</summary>
    public void Reset() => SelectedMode = OpticsModeOption.All[0];
}
