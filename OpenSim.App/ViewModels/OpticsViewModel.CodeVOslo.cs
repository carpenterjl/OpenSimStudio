using System.IO;
using CommunityToolkit.Mvvm.Input;
using OpenSim.Optics.Interop;
using OpenSim.Optics.Interop.Zemax;

namespace OpenSim.App.ViewModels;

// OP-95: CODE V (.seq) and OSLO (.len) lens files into the workspace, beside OP-94's Zemax
// import (docs/Optics-Conventions.md section 54). The same check box lists unsupported content
// instead of stopping (ZemaxUnsupportedAsWarnings).

public partial class OpticsViewModel
{
    /// <summary>Replaces the workspace's optical model with a CODE V sequence file's (.seq) or an
    /// OSLO lens file's (.len), chosen by the extension. Glass names are checked against every
    /// loaded catalog. Returns the import's warnings and notes, also written to the log; a file
    /// that cannot be read throws, every problem listed.</summary>
    public IReadOnlyList<string> ImportCodeVOsloFile(string path)
    {
        string format = LensFileImport.FormatOf(path);
        var result = LensFileImport.Import(path, _glassLibrary.Catalogs, new ZemaxImportOptions { UnsupportedAsWarnings = ZemaxUnsupportedAsWarnings });
        var lines = new List<string>();
        lines.AddRange(result.Warnings.Select(w => "Not read: " + w));
        lines.AddRange(result.Notes);
        lines.AddRange(LoadSettings(result.Optics));
        foreach (var line in lines) _log.Append($"{format} import ({Path.GetFileName(path)}): {line}");
        LensFileStatus = $"{format} lens imported from {path} ({result.Encoding})"
            + (result.Warnings.Count > 0 ? $"; {result.Warnings.Count} items not read, listed in the log." : ".")
            + (lines.Count > result.Warnings.Count ? " Notes are in the log." : "");
        return lines;
    }

    [RelayCommand]
    private void ImportCodeVOslo()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = LensFileImport.CodeVOsloFilter, Title = "Import a CODE V or OSLO lens" };
        if (dialog.ShowDialog() != true) return;
        string format = LensFileImport.FormatOf(dialog.FileName);
        try { ImportCodeVOsloFile(dialog.FileName); }
        catch (Exception ex) when (ex is ZemaxFileException or IOException or UnauthorizedAccessException)
        {
            _log.Append($"{format} import ({Path.GetFileName(dialog.FileName)}): {ex.Message}");
            LensFileStatus = $"{format} lens not imported: {(ex is ZemaxFileException z ? $"{z.Problems.Count} problems, listed in the log." : ex.Message)}";
        }
    }
}
