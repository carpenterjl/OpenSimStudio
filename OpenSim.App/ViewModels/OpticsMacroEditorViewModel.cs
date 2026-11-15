using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Core.Model;
using OpenSim.Optics.Materials;
using OpenSim.Optics.Scripting.Zpl;

namespace OpenSim.App.ViewModels;

// OP-99: the macro editor (OpticStudio's Edit/Run ZPL Macros; docs/Optics-User-Guide.md, "ZPL macros").

/// <summary>
/// The ZPL macro window: the macro text, the macro folder and its macro list, Run (off the UI
/// thread) and Stop, the output window and a check that lists the keywords and functions the
/// macro names that this version does not bind. A run works on a copy of the project's optics
/// record; when it changed the lens, the result is written back through the workspace (<c>apply</c>).
/// A failed or stopped run changes nothing.
/// </summary>
public sealed partial class OpticsMacroEditorViewModel : ObservableObject
{
    private readonly Func<OpticsSettings> _system;
    private readonly Func<IReadOnlyList<OpticalCatalog>> _catalogs;
    private readonly Func<OpticsSettings, OpticsSettings, string?> _apply;
    private CancellationTokenSource? _cancel;

    /// <param name="system">The project's optics record now.</param>
    /// <param name="catalogs">The active glass catalogs.</param>
    /// <param name="apply">Writes the macro's result (before, after) into the workspace; returns
    /// why it was refused, or null.</param>
    public OpticsMacroEditorViewModel(Func<OpticsSettings> system, Func<IReadOnlyList<OpticalCatalog>> catalogs,
        Func<OpticsSettings, OpticsSettings, string?> apply)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _macroFolder = ZplMacroFolder.Current;
        RefreshMacros();
    }

    /// <summary>The macro being edited.</summary>
    [ObservableProperty] private string _text = "";

    /// <summary>The file the macro was opened from or saved to, or empty.</summary>
    [ObservableProperty] private string _fileName = "";

    /// <summary>The macro folder: CALLMACRO, OUTPUT and OPEN names without a folder, the macro
    /// list, and the folder ZPLM operands and macro solves read (<see cref="ZplMacroFolder"/>).</summary>
    [ObservableProperty] private string _macroFolder;

    partial void OnMacroFolderChanged(string value) => RefreshMacros();

    /// <summary>The .zpl files of the macro folder.</summary>
    [ObservableProperty] private IReadOnlyList<string> _macros = Array.Empty<string>();

    [ObservableProperty] private string? _selectedMacro;

    /// <summary>The macro's text output.</summary>
    [ObservableProperty] private string _output = "";

    [ObservableProperty] private string _status = "Runs OpticStudio ZPL macros on the lens. Keywords this version does not bind stop the run, naming the keyword and line.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    /// <summary>INPUT and PAUSE (the window); null makes them fail.</summary>
    public IZplHost? Host { get; set; }

    /// <summary>The last run's result, or null.</summary>
    public ZplRunResult? LastResult { get; private set; }

    public void RefreshMacros()
    {
        try
        {
            Macros = Directory.Exists(MacroFolder)
                ? Directory.GetFiles(MacroFolder, "*" + ZplMacro.Extension).Select(Path.GetFileName).OfType<string>().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Macros = Array.Empty<string>(); Status = e.Message; }
    }

    /// <summary>Opens a macro file into the editor.</summary>
    public void Open(string path)
    {
        try
        {
            Text = File.ReadAllText(path);
            FileName = path;
            Status = $"Opened {Path.GetFileName(path)}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = e.Message; }
    }

    /// <summary>Saves the editor's text.</summary>
    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, Text);
            FileName = path;
            Status = $"Saved {Path.GetFileName(path)}.";
            RefreshMacros();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = e.Message; }
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void LoadSelected()
    {
        if (SelectedMacro is { } name) Open(Path.Combine(MacroFolder, name));
    }

    /// <summary>Parses the macro and lists the names it uses that are not bound.</summary>
    [RelayCommand]
    private void Check()
    {
        try
        {
            var program = ZplMacro.Parse(Text, MacroName());
            var missing = program.NotBound();
            Status = missing.Count == 0
                ? "The macro parses, and every keyword and function it names is bound."
                : "Not bound in this version: " + string.Join(", ", missing.Select(m => $"{m.Name} (line {m.Line})")) + ".";
        }
        catch (ZplException e) { Status = e.Message; }
    }

    private string MacroName() => FileName.Length > 0 ? Path.GetFileName(FileName) : "macro";

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RunAsync()
    {
        ZplProgram program;
        try { program = ZplMacro.Parse(Text, MacroName()); }
        catch (ZplException e) { Status = e.Message; return; }
        var before = _system();
        var catalogs = _catalogs();
        var cts = new CancellationTokenSource();
        _cancel = cts;
        var ui = SynchronizationContext.Current;
        Output = "";
        ZplMacroFolder.Current = MacroFolder;
        var options = new ZplRunOptions
        {
            System = before,
            Catalogs = catalogs,
            MacroFolder = MacroFolder,
            LensFolder = MacroFolder,
            Host = Host,
            CancellationToken = cts.Token,
            Output = text => { if (ui is not null) ui.Post(_ => { if (IsRunning) Output += text; }, null); },
            ScreenReplaced = text => { if (ui is not null) ui.Post(_ => { if (IsRunning) Output = text; }, null); },
        };
        IsRunning = true;
        Status = "Running…";
        try
        {
            var result = await Task.Run(() => ZplMacro.Run(program, options));
            LastResult = result;
            Output = result.Output;
            if (ReferenceEquals(result.System, before)) { Status = $"Done ({result.Steps} statements); the lens is unchanged."; return; }
            string? refusal;
            try { refusal = _apply(before, result.System); }
            catch (ArgumentException ex) { refusal = ex.Message; }
            Status = refusal is null ? $"Done ({result.Steps} statements); the macro's changes were written to the lens." : $"Done, but the changes were not applied: {refusal}";
        }
        catch (ZplException e) { Status = e.Message; }
        catch (OperationCanceledException) { Status = "Stopped: nothing was applied."; }
        finally
        {
            IsRunning = false;
            _cancel = null;
            cts.Dispose();
        }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cancel?.Cancel();
}
