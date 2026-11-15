using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Optics.Tolerancing;

namespace OpenSim.App.ViewModels;

// OP-67: the tolerance script of the User Script criterion (docs/Optics-Conventions.md §60), inside
// the Tolerance Data Editor. The sensitivity and Monte Carlo runs take it from RunOptions().

public sealed partial class OpticsToleranceEditorViewModel
{
    private string? _scriptMacroFolder;

    /// <summary>The script's name in messages and the saved settings ("ALIGN.TSC").</summary>
    [ObservableProperty] private string _scriptName = new ToleranceScriptOptions().Name;

    /// <summary>The script: ZPL with the tolerance-script commands.</summary>
    [ObservableProperty] private string _scriptText = "";

    /// <summary>The folder LOADMERIT reads merit function files (.json) from.</summary>
    [ObservableProperty] private string _scriptMeritFolder = "";

    /// <summary>The folder of OPENFILE's files and SAVE's lenses.</summary>
    [ObservableProperty] private string _scriptOutputFolder = "";

    /// <summary>The file name Save merit function writes the lens's merit function to, in the merit folder.</summary>
    [ObservableProperty] private string _scriptMeritName = "CENTER.json";

    [ObservableProperty] private string _scriptStatus = "Choose the User script criterion to run this script in every trial instead of the compensation. Check finds syntax errors and commands this version does not run.";

    private static readonly HashSet<string> ScriptSettingNames = new(StringComparer.Ordinal)
    {
        nameof(ScriptName), nameof(ScriptText), nameof(ScriptMeritFolder), nameof(ScriptOutputFolder),
    };

    /// <summary>The script as the run takes it; null when nothing is set.</summary>
    public ToleranceScriptOptions? ScriptOptions()
    {
        static string? Folder(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        if (string.IsNullOrEmpty(ScriptText) && Folder(ScriptMeritFolder) is null && Folder(ScriptOutputFolder) is null
            && (ScriptName ?? "") == new ToleranceScriptOptions().Name && _scriptMacroFolder is null)
            return null;
        return new ToleranceScriptOptions
        {
            Name = string.IsNullOrWhiteSpace(ScriptName) ? new ToleranceScriptOptions().Name : ScriptName.Trim(),
            Text = ScriptText ?? "",
            MeritFolder = Folder(ScriptMeritFolder), OutputFolder = Folder(ScriptOutputFolder), MacroFolder = _scriptMacroFolder,
        };
    }

    private void LoadScript(ToleranceScriptOptions? s)
    {
        ScriptName = s?.Name ?? new ToleranceScriptOptions().Name;
        ScriptText = s?.Text ?? "";
        ScriptMeritFolder = s?.MeritFolder ?? "";
        ScriptOutputFolder = s?.OutputFolder ?? "";
        _scriptMacroFolder = s?.MacroFolder;
    }

    /// <summary>Checks the script: its syntax and the commands, keywords and functions this version does not run.</summary>
    [RelayCommand]
    private void CheckScript()
    {
        var problems = ToleranceScript.Check(ScriptOptions() ?? new ToleranceScriptOptions());
        ScriptStatus = problems.Count == 0
            ? (string.IsNullOrWhiteSpace(ScriptText) ? "The script is empty." : "The script parses, and every command it names runs in this version.")
            : string.Join(Environment.NewLine, problems);
    }

    /// <summary>Writes the lens's merit function as a merit function file LOADMERIT reads, in the merit folder.</summary>
    [RelayCommand]
    private void SaveMeritForScript()
    {
        string folder = (ScriptMeritFolder ?? "").Trim();
        string name = (ScriptMeritName ?? "").Trim();
        if (folder.Length == 0) { ScriptStatus = "Set the merit folder first."; return; }
        if (name.Length == 0) { ScriptStatus = "Give the merit function file a name (as CENTER.json)."; return; }
        if (Path.GetExtension(name).Length == 0) name += ".json";
        if (!string.Equals(Path.GetExtension(name), ".json", StringComparison.OrdinalIgnoreCase))
        {
            ScriptStatus = "The merit function file is this program's JSON; give it the extension .json.";
            return;
        }
        var merit = _lens().MeritFunction;
        if (merit?.Operands is not { Count: > 0 }) { ScriptStatus = "The lens has no merit function: build one in the Merit Function Editor first."; return; }
        string path = Path.Combine(folder, name);
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(merit, new JsonSerializerOptions { WriteIndented = true }));
            ScriptStatus = $"Saved the lens's merit function ({merit.Operands.Count} rows) as {path}; the script loads it with LOADMERIT {Path.GetFileNameWithoutExtension(name)}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            ScriptStatus = $"The merit function could not be saved as {path}: {e.Message}";
        }
    }
}
