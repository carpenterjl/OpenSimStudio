using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Optics.Interop.Zemax;

namespace OpenSim.App.ViewModels;

// OP-94: Zemax lens files (.zmx, and the lens inside a .zar archive) in and out of the
// workspace, beside the program's own lens file (docs/Optics-Conventions.md section 52).

public partial class OpticsViewModel
{
    /// <summary>When true, content a Zemax file holds that this build does not read or write is
    /// listed in the log and left out instead of failing the import or export.</summary>
    [ObservableProperty] private bool _zemaxUnsupportedAsWarnings;

    /// <summary>Replaces the workspace's optical model with a Zemax lens file's (.zmx or .zar).
    /// Glass names are checked against every loaded catalog. Returns the import's warnings and
    /// notes, also written to the log; a file that cannot be read throws, every problem listed.</summary>
    public IReadOnlyList<string> ImportZemaxFile(string path)
    {
        var result = ZemaxLensFiles.Import(path, _glassLibrary.Catalogs, new ZemaxImportOptions { UnsupportedAsWarnings = ZemaxUnsupportedAsWarnings });
        var lines = new List<string>();
        lines.AddRange(result.Warnings.Select(w => "Not read: " + w));
        lines.AddRange(result.Notes);
        lines.AddRange(LoadSettings(result.Optics));
        foreach (var line in lines) _log.Append($"Zemax import ({Path.GetFileName(path)}): {line}");
        LensFileStatus = $"Zemax lens imported from {path} ({result.Encoding})"
            + (result.Warnings.Count > 0 ? $"; {result.Warnings.Count} items not read, listed in the log." : ".")
            + (lines.Count > result.Warnings.Count ? " Notes are in the log." : "");
        return lines;
    }

    /// <summary>Writes the workspace's optical model as a Zemax lens file. Returns the export's
    /// warnings and notes, also written to the log; a system the file subset cannot hold throws,
    /// every item listed, and nothing is written.</summary>
    public IReadOnlyList<string> ExportZemaxFile(string path)
    {
        var result = ZemaxLensFiles.Export(ToSettings(), path, new ZemaxExportOptions { UnsupportedAsWarnings = ZemaxUnsupportedAsWarnings });
        var lines = result.Warnings.Select(w => "Not written: " + w).Concat(result.Notes).ToList();
        foreach (var line in lines) _log.Append($"Zemax export ({Path.GetFileName(path)}): {line}");
        LensFileStatus = $"Zemax lens written to {path}" + (result.Warnings.Count > 0 ? $"; {result.Warnings.Count} items not written, listed in the log." : ".");
        return lines;
    }

    [RelayCommand]
    private void ImportZemax()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = ZemaxLensFiles.ImportFilter, Title = "Import a Zemax lens" };
        if (dialog.ShowDialog() != true) return;
        try { ImportZemaxFile(dialog.FileName); }
        catch (Exception ex) when (ex is ZemaxFileException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _log.Append($"Zemax import ({Path.GetFileName(dialog.FileName)}): {ex.Message}");
            LensFileStatus = $"Zemax lens not imported: {(ex is ZemaxFileException z ? $"{z.Problems.Count} problems, listed in the log." : ex.Message)}";
        }
    }

    [RelayCommand]
    private void ExportZemax()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = ZemaxLensFiles.ExportFilter,
            FileName = (string.IsNullOrWhiteSpace(SystemTitle) ? "lens" : SystemTitle) + ZemaxLensFiles.LensExtension,
            Title = "Export as a Zemax lens",
        };
        if (dialog.ShowDialog() != true) return;
        try { ExportZemaxFile(dialog.FileName); }
        catch (Exception ex) when (ex is ZemaxFileException or IOException or UnauthorizedAccessException)
        {
            _log.Append($"Zemax export ({Path.GetFileName(dialog.FileName)}): {ex.Message}");
            LensFileStatus = $"Zemax lens not written: {(ex is ZemaxFileException z ? $"{z.Problems.Count} problems, listed in the log." : ex.Message)}";
        }
    }
}
